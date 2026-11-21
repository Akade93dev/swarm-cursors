using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Swarm.Gateway;

namespace Swarm.Tests;

public class GatewayProtocolTests
{
    [Fact]
    public async Task InitializeAndInitializedFollowMcpLifecycle()
    {
        var gateway = new McpGateway(new FakeTransport());

        var response = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");

        using var document = JsonDocument.Parse(response!);
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("swarm-desktop", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));

        var notification = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        Assert.Null(notification);
    }

    [Fact]
    public async Task PingReturnsAnEmptyResult()
    {
        var gateway = new McpGateway(new FakeTransport());

        var response = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"p1\",\"method\":\"ping\"}");

        using var document = JsonDocument.Parse(response!);
        Assert.Equal("p1", document.RootElement.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("result").ValueKind);
        Assert.Empty(document.RootElement.GetProperty("result").EnumerateObject());
    }

    [Fact]
    public async Task GatewayProcessReadsUtf8StandardInput()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnet))
            dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(typeof(McpGateway).Assembly.Location);

        using var process = Process.Start(startInfo)!;
        try
        {
            const string text = "桌面验证";
            var request = "{\"jsonrpc\":\"2.0\",\"id\":\"utf8\",\"method\":\"tools/call\",\"params\":{\"name\":\"" + text + "\",\"arguments\":{}}}";
            await process.StandardInput.WriteLineAsync(request);
            await process.StandardInput.FlushAsync();

            var response = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(response);
            using var document = JsonDocument.Parse(response);
            Assert.Equal($"Unknown tool: {text}", document.RootElement.GetProperty("error").GetProperty("data").GetString());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task ToolsListDescribesAuthorizationCoordinatesAndEmergencyStop()
    {
        var gateway = new McpGateway(new FakeTransport());

        var response = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");

        using var document = JsonDocument.Parse(response!);
        var tools = document.RootElement.GetProperty("result").GetProperty("tools");
        Assert.Equal(new[] { "task_begin", "observe", "click", "type_text", "press_key", "scroll", "focus", "task_end", "task_stop" },
            tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray());
        Assert.Contains("local user authorization", tools[0].GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("window-client CSS", tools[1].GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("elementId takes precedence", tools[2].GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Esc", tools[8].GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> ToolCalls()
    {
        yield return new object[] { "task_begin", "{\"targetProcess\":\"notepad\",\"targetTitle\":\"Untitled\"}" };
        yield return new object[] { "observe", "{\"token\":\"t1\"}" };
        yield return new object[] { "click", "{\"token\":\"t1\",\"elementId\":\"e2\",\"x\":10,\"y\":20,\"label\":\"Save\"}" };
        yield return new object[] { "type_text", "{\"token\":\"t1\",\"text\":\"hello\",\"sensitive\":false,\"elementId\":\"e2\"}" };
        yield return new object[] { "press_key", "{\"token\":\"t1\",\"key\":\"ENTER\"}" };
        yield return new object[] { "scroll", "{\"token\":\"t1\",\"x\":10,\"y\":20,\"delta\":-120}" };
        yield return new object[] { "focus", "{\"token\":\"t1\"}" };
        yield return new object[] { "task_end", "{\"token\":\"t1\"}" };
        yield return new object[] { "task_stop", "{\"token\":\"t1\"}" };
    }

    [Theory]
    [MemberData(nameof(ToolCalls))]
    public async Task ToolCallsForwardArgumentsAndReturnStructuredJson(string tool, string arguments)
    {
        var transport = new FakeTransport();
        transport.Response = DesktopIpcResponse.Success(JsonDocument.Parse("{\"accepted\":true}").RootElement.Clone());
        var gateway = new McpGateway(transport);
        var request = $"{{\"jsonrpc\":\"2.0\",\"id\":\"call-1\",\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{arguments}}}}}";

        var response = await gateway.HandleLineAsync(request);

        var call = Assert.Single(transport.Calls);
        Assert.Equal(tool, call.Method);
        Assert.Equal(arguments, call.Parameters.GetRawText());
        using var document = JsonDocument.Parse(response!);
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("structuredContent").GetProperty("accepted").GetBoolean());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.True(JsonDocument.Parse(content.GetProperty("text").GetString()!).RootElement.GetProperty("accepted").GetBoolean());
        Assert.False(result.TryGetProperty("isError", out _));
    }

    [Theory]
    [InlineData("observe", "{\"token\":\"t1\",\"screenshot\":{\"mimeType\":\"image/png\",\"data\":\"aGVsbG8=\"}}", false)]
    [InlineData("click", "{\"executed\":true,\"observation\":{\"screenshot\":{\"mimeType\":\"image/png\",\"data\":\"aGVsbG8=\"}}}", true)]
    public async Task ScreenshotsAreReturnedAsMcpImageContent(string tool, string payload, bool nested)
    {
        var transport = new FakeTransport
        {
            Response = DesktopIpcResponse.Success(JsonDocument.Parse(payload).RootElement.Clone()),
        };
        var gateway = new McpGateway(transport);
        var arguments = tool == "observe" ? "{\"token\":\"t1\"}" : "{\"token\":\"t1\",\"x\":1,\"y\":1}";
        var request = $"{{\"jsonrpc\":\"2.0\",\"id\":\"image-1\",\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{arguments}}}}}";

        var response = await gateway.HandleLineAsync(request);

        using var document = JsonDocument.Parse(response!);
        var result = document.RootElement.GetProperty("result");
        var content = result.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(2, content.Length);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("image/png", content[1].GetProperty("mimeType").GetString());
        Assert.Equal("aGVsbG8=", content[1].GetProperty("data").GetString());
        Assert.DoesNotContain("aGVsbG8=", content[0].GetProperty("text").GetString());
        var structured = result.GetProperty("structuredContent");
        var observation = nested ? structured.GetProperty("observation") : structured;
        Assert.False(observation.TryGetProperty("screenshot", out _));
    }

    [Fact]
    public async Task DesktopErrorsAreToolErrorsRatherThanRpcErrors()
    {
        var transport = new FakeTransport
        {
            Response = DesktopIpcResponse.Failure(new DesktopIpcError("authorization_denied", "The local user denied authorization.")),
        };
        var gateway = new McpGateway(transport);

        var response = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"task_begin\",\"arguments\":{}}}");

        using var document = JsonDocument.Parse(response!);
        Assert.False(document.RootElement.TryGetProperty("error", out _));
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("authorization_denied", result.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Contains("authorization_denied", content.GetProperty("text").GetString());
    }

    [Fact]
    public async Task TransportExceptionsAreContainedInToolResults()
    {
        var transport = new FakeTransport { Exception = new IOException("Desktop pipe is unavailable.") };
        var gateway = new McpGateway(transport);

        var response = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"observe\",\"arguments\":{\"token\":\"t1\"}}}");

        using var document = JsonDocument.Parse(response!);
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("Desktop pipe is unavailable.", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task UnknownMethodsReturnMethodNotFoundWithTheSameId()
    {
        var gateway = new McpGateway(new FakeTransport());

        var response = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"resources/list\"}");

        using var document = JsonDocument.Parse(response!);
        Assert.Equal(99, document.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32601, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task UnknownToolsReturnInvalidParamsWithoutCallingDesktop()
    {
        var transport = new FakeTransport();
        var gateway = new McpGateway(transport);

        var response = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"delete_everything\",\"arguments\":{}}}");

        using var document = JsonDocument.Parse(response!);
        Assert.Equal(-32602, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task MalformedJsonReturnsParseError()
    {
        var gateway = new McpGateway(new FakeTransport());

        var response = await gateway.HandleLineAsync("{not-json}");

        using var document = JsonDocument.Parse(response!);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(-32700, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void IpcResponseIdMustMatchRequestId()
    {
        var response = "{\"id\":\"other\",\"ok\":true,\"result\":{}}";

        Assert.Throws<InvalidDataException>(() => GatewayIpcProtocol.ParseResponse(response, "expected"));
    }

    private sealed class FakeTransport : IDesktopGatewayTransport
    {
        public List<(string Method, JsonElement Parameters)> Calls { get; } = new();
        public DesktopIpcResponse Response { get; set; } = DesktopIpcResponse.Success(JsonDocument.Parse("{}").RootElement.Clone());
        public Exception? Exception { get; set; }

        public Task<DesktopIpcResponse> SendAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
        {
            Calls.Add((method, parameters.Clone()));
            if (Exception is not null) throw Exception;
            return Task.FromResult(Response);
        }
    }
}
