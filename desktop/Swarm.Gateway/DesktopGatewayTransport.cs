using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Swarm.Gateway;

public interface IDesktopGatewayTransport
{
    /// <summary>发送一次桌面请求。progress 在每个进度行到达时被 await，保证通知先于最终响应写出。</summary>
    Task<DesktopIpcResponse> SendAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        Func<DesktopProgress, Task>? progress = null);

    /// <summary>尽力取消桌面端当前在途动作；桌面不可达时静默失败，不影响向客户端返回 cancelled。</summary>
    Task CancelAsync(CancellationToken cancellationToken = default);
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
    // 进度行数量上限：正常一次 observe/动作只有几条；超过上限说明对端异常，必须中止而不是无限读。
    private const int MaxResponseLines = 512;
    private readonly TimeSpan _timeout;

    public NamedPipeDesktopGatewayTransport(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? GatewayIpcProtocol.DefaultTimeout;
    }

    public async Task<DesktopIpcResponse> SendAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        Func<DesktopProgress, Task>? progress = null)
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
        // 桌面端可以在最终响应前插入若干进度行；含 progress 且无 ok 的行转成通知后继续读。
        for (var lines = 0; lines < MaxResponseLines; lines++)
        {
            var line = await reader.ReadLineAsync(token) ?? throw new IOException("Desktop IPC closed the pipe without a response.");
            if (GatewayIpcProtocol.TryParseProgress(line, id, out var update))
            {
                if (progress is not null) await progress(update);
                continue;
            }
            return GatewayIpcProtocol.ParseResponse(line, id);
        }
        throw new InvalidDataException($"Desktop IPC exceeded {MaxResponseLines} progress lines without a final response.");
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            await using var pipe = new NamedPipeClientStream(".", GatewayIpcProtocol.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 256, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteLineAsync("{\"action\":\"cancel\"}".AsMemory(), timeout.Token);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            // 桌面未启动或被人工接管时取消无对象；客户端仍会收到 -32800。
        }
    }
}
