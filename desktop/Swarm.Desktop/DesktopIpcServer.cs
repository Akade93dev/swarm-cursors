using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Swarm.Core.Diagnostics;

namespace Swarm.Desktop;

public sealed class DesktopIpcServer
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(1);
    // 正常请求是几百字节的 JSON；设置上限可避免本机异常/恶意客户端用超长单行耗尽内存。
    private const int MaxRequestChars = 1 << 20;
    private readonly Func<string, int, Func<double, string?, Task>, Task<string>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public DesktopIpcServer(Func<string, int, Func<double, string?, Task>, Task<string>> handler) => _handler = handler;

    // 管道 I/O 必须完全离开 UI 线程：同步 drain 或卡死的客户端否则会冻结覆盖层与全局钩子。
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
                    "SwarmDesktop.V1",
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var processId);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 16 * 1024, true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, true) { AutoFlush = true, NewLine = "\n" };
                await using (writer.ConfigureAwait(false))
                {
                    string? line;
                    // 只给“读请求行”限时：真正耗时的本地确认发生在 handler 里，不应被这里截断。
                    using (var read = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                    {
                        read.CancelAfter(RequestTimeout);
                        line = await ReadLineAsync(reader, read.Token);
                    }
                    if (line == null) continue;
                    string response;
                    try
                    {
                        response = IsTrustedGateway(processId)
                            ? await _handler(line, (int)processId, (value, message) => WriteProgressAsync(writer, line, value, message, _stop.Token))
                            : UnauthorizedResponse();
                    }
                    catch (Exception error)
                    {
                        FileLog.Write("desktop", $"ipc handler failed: {error.Message}");
                        response = "{\"id\":\"\",\"ok\":false,\"error\":{\"code\":\"desktop_error\",\"message\":\"本地请求处理失败\"}}";
                    }
                    await writer.WriteLineAsync(response.AsMemory(), _stop.Token);
                    await writer.FlushAsync(_stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
            {
                // 对端超时/断连/超长请求/其他用户进程：丢弃本次连接，继续接受后续请求，避免整个 IPC 静默死亡。
                FileLog.Write("desktop", $"ipc connection dropped: {error.Message}");
            }
            catch (Exception error)
            {
                // 未预期异常同样不能终止接受循环；短暂退避避免错误持续时忙转。
                FileLog.Write("desktop", $"ipc loop error: {error.Message}");
                try { await Task.Delay(200, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// 管道仅允许当前用户连接，这里再确认对端确实是 Swarm.Gateway，避免任意本机进程驱动输入。
    /// 发布版主模块为 Swarm.Gateway.exe；开发期用 dotnet 托管启动时主模块是 dotnet，
    /// 需要在已加载模块中找到 Swarm.Gateway.dll 才放行。
    /// </summary>
    internal static bool IsTrustedGateway(uint processId)
    {
        if (processId == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = Path.GetFileNameWithoutExtension(process.MainModule?.FileName ?? string.Empty);
            if (string.Equals(name, "Swarm.Gateway", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(Path.GetFileName(module.FileName), "Swarm.Gateway.dll", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取一行请求并限制长度。StreamReader.ReadLineAsync 会把整行缓冲完才返回，
    /// 本机客户端一旦发送超长行就会占满进程内存，因此改为分块读取并在超限时断开连接。
    /// </summary>
    internal static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        var builder = new StringBuilder(1024);
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch == '\n') return builder.ToString().TrimEnd('\r');
                if (builder.Length >= MaxRequestChars) throw new InvalidDataException("请求超过长度上限");
                builder.Append(ch);
            }
        }
    }

    /// <summary>
    /// 在最终响应前写一行进度：Gateway 读到不含 ok 的 progress 行就转成 MCP 通知并继续读。
    /// 数值统一由 System.Text.Json 按不变文化序列化，避免区域设置把小数点写成逗号。
    /// </summary>
    private static async Task WriteProgressAsync(
        StreamWriter writer,
        string requestLine,
        double value,
        string? message,
        CancellationToken token)
    {
        var clamped = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        var progress = new Dictionary<string, object?> { ["value"] = clamped };
        if (!string.IsNullOrEmpty(message)) progress["message"] = message;
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = RequestId(requestLine),
            ["progress"] = progress,
        });
        await writer.WriteLineAsync(json.AsMemory(), token);
        await writer.FlushAsync(token);
    }

    /// <summary>进度行必须带回请求 id，Gateway 才能确认它属于当前请求；畸形请求由处理器统一报错。</summary>
    private static string RequestId(string requestLine)
    {
        try
        {
            using var document = JsonDocument.Parse(requestLine);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string UnauthorizedResponse() =>
        "{\"id\":\"\",\"ok\":false,\"error\":{\"code\":\"unauthorized\",\"message\":\"未授权的本地客户端\"}}";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(nint pipe, out uint processId);
}
