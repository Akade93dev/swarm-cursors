using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Swarm.Gateway;

public interface IDesktopGatewayTransport
{
    Task<DesktopIpcResponse> SendAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default);
}

/// <summary>桌面应用未启动或管道不可达；与桌面返回的业务错误区分，便于客户端给出明确指引。</summary>
public sealed class DesktopUnavailableException : Exception
{
    public DesktopUnavailableException(string message) : base(message)
    {
    }

    public DesktopUnavailableException(string message, Exception inner) : base(message, inner)
    {
    }
}

public sealed class NamedPipeDesktopGatewayTransport : IDesktopGatewayTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _timeout;

    public NamedPipeDesktopGatewayTransport(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? GatewayIpcProtocol.DefaultTimeout;
    }

    public async Task<DesktopIpcResponse> SendAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString();
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        });

        await using var pipe = new NamedPipeClientStream(".", GatewayIpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connect.CancelAfter(ConnectTimeout);
            try
            {
                await pipe.ConnectAsync(connect.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DesktopUnavailableException("未连接到 Swarm Desktop，请先启动 Swarm Desktop 后重试。");
            }
            catch (IOException exception)
            {
                throw new DesktopUnavailableException($"与 Swarm Desktop 的连接失败：{exception.Message}", exception);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var token = timeout.Token;

        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteLineAsync(request.AsMemory(), token);
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);
        var response = await reader.ReadLineAsync(token) ?? throw new IOException("Desktop IPC closed the pipe without a response.");
        return GatewayIpcProtocol.ParseResponse(response, id);
    }
}
