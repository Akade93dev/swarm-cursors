using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Swarm.Desktop;

public sealed class DesktopIpcServer
{
    private readonly Func<string, int, Task<string>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public DesktopIpcServer(Func<string, int, Task<string>> handler) => _handler = handler;

    public void Start() => _loop = RunAsync();

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_loop != null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
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
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line == null) continue;
                    var response = IsTrustedGateway(processId)
                        ? await _handler(line, (int)processId)
                        : UnauthorizedResponse();
                    await writer.WriteLineAsync(response.AsMemory(), _stop.Token);
                    await writer.FlushAsync(_stop.Token);
                }
                pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // The Gateway may time out while local confirmation is still open.
                // Discard that connection and keep accepting later desktop requests.
            }
            catch (ObjectDisposedException)
            {
                // A disconnected client can also surface during writer disposal.
            }
        }
    }

    /// <summary>
    /// 管道仅允许当前用户连接，这里再确认对端确实是 Swarm.Gateway，避免任意本机进程驱动输入。
    /// 发布版主模块为 Swarm.Gateway.exe；开发期用 dotnet 托管启动时主模块是 dotnet，
    /// 需要在已加载模块中找到 Swarm.Gateway.dll 才放行。
    /// </summary>
    private static bool IsTrustedGateway(uint processId)
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

    private static string UnauthorizedResponse() =>
        "{\"id\":\"\",\"ok\":false,\"error\":{\"code\":\"unauthorized\",\"message\":\"未授权的本地客户端\"}}";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint processId);
}
