using System.Text;
using Swarm.Gateway;

var utf8 = new UTF8Encoding(false);
Console.InputEncoding = utf8;
Console.OutputEncoding = utf8;

// 请求并发执行，读循环才能持续接收 notifications/cancelled；写 stdout 必须串行，避免响应行与进度通知交错。
var writeLock = new SemaphoreSlim(1, 1);
var gateway = new McpGateway(new NamedPipeDesktopGatewayTransport(), WriteLineAsync);
var pending = new List<Task>();
while (await Console.In.ReadLineAsync() is { } line)
{
    // 只清理已完成的任务，绝不在读循环里等待在途请求——否则取消通知会被堵在 stdin 里。
    pending.RemoveAll(task => task.IsCompleted);
    // 部分 MCP 客户端以 utf-8-sig 写入首行，BOM 会让 JSON 解析失败；这里统一剥离。
    pending.Add(DispatchAsync(line.TrimStart('\uFEFF')));
}
await Task.WhenAll(pending);

async Task DispatchAsync(string line)
{
    var response = await gateway.HandleLineAsync(line);
    if (response is null) return;
    await WriteLineAsync(response);
}

// 响应与进度通知共用同一把写锁：任何一行都不能被另一次输出截断。
async Task WriteLineAsync(string line)
{
    await writeLock.WaitAsync();
    try
    {
        await Console.Out.WriteLineAsync(line);
        await Console.Out.FlushAsync();
    }
    finally
    {
        writeLock.Release();
    }
}
