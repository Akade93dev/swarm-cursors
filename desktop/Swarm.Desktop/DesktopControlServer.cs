using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Swarm.Core.Diagnostics;

namespace Swarm.Desktop;

/// <summary>
/// 独立取消通道。主 IPC 是"一请求一管道 + 串行 accept"，动作执行期间无法再接受连接，
/// 所以取消必须走单独的管道。这里只接受 cancel 一种动作，并校验调用方确实是 Gateway。
/// </summary>
public sealed class DesktopControlServer
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly Action _cancel;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public DesktopControlServer(Action cancel) => _cancel = cancel;

    public void Start() => _loop = Task.Run(RunAsync);

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_loop != null)
        {
            try { await _loop; }
            catch (Exception) { /* 关闭阶段任何残留异常都不应阻塞退出 */ }
        }
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    "SwarmDesktop.Control.V1",
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                DesktopIpcServer.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var processId);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                // 不 Dispose writer：它包装的是 leaveOpen 流，释放时会 flush，对端已走会抛 IOException。
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 256, true) { AutoFlush = true, NewLine = "\n" };
                string? line;
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    read.CancelAfter(RequestTimeout);
                    line = await DesktopIpcServer.ReadLineAsync(reader, read.Token);
                }
                if (line == null) continue;

                string reply;
                if (!DesktopIpcServer.IsTrustedGateway(processId))
                {
                    reply = "{\"ok\":false,\"error\":\"unauthorized\"}";
                }
                else if (!IsCancel(line))
                {
                    reply = "{\"ok\":false,\"error\":\"invalid_request\"}";
                }
                else
                {
                    _cancel();
                    FileLog.Write("desktop", "control cancel accepted");
                    reply = "{\"ok\":true}";
                }
                try
                {
                    await writer.WriteLineAsync(reply.AsMemory(), _stop.Token);
                    await pipe.FlushAsync(_stop.Token);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // Gateway 发完即走、不读确认，断开是正常路径，不计为连接异常。
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
            {
                // 对端超时/断连/超长请求：丢弃本次连接，继续接受后续控制命令。
                FileLog.Write("desktop", $"control connection dropped: {error.Message}");
            }
            catch (Exception error)
            {
                FileLog.Write("desktop", $"control loop error: {error.Message}");
                try { await Task.Delay(200, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static bool IsCancel(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String &&
                string.Equals(action.GetString(), "cancel", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
