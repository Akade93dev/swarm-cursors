using System.Text;
using Swarm.Gateway;

var utf8 = new UTF8Encoding(false);
Console.InputEncoding = utf8;
Console.OutputEncoding = utf8;

var gateway = new McpGateway(new NamedPipeDesktopGatewayTransport());
while (await Console.In.ReadLineAsync() is { } line)
{
    // 部分 MCP 客户端以 utf-8-sig 写入首行，BOM 会让 JSON 解析失败；这里统一剥离。
    var response = await gateway.HandleLineAsync(line.TrimStart('\uFEFF'));
    if (response is null) continue;
    await Console.Out.WriteLineAsync(response);
    await Console.Out.FlushAsync();
}
