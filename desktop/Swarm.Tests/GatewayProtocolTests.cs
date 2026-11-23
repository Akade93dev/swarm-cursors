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
        Assert.Equal(new[] { "task_begin", "observe", "click", "type_text", "press_key", "scroll", "focus", "task_end", "task_stop", "desktop_status" },
            tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray());
        Assert.Contains("local user authorization", tools[0].GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("window-client CSS", tools[1].GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("elementId takes precedence", tools[2].GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Esc", tools[8].GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolsListDeclaresAnnotationsAndOutputSchemas()
    {
        var gateway = new McpGateway(new FakeTransport());

        var response = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/list\",\"params\":{}}");

        using var document = JsonDocument.Parse(response!);
        var tools = document.RootElement.GetProperty("result").GetProperty("tools");
        var expected = new Dictionary<string, (bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld)>
        {
            ["task_begin"] = (false, false, false, true),
            ["observe"] = (true, false, true, true),
            ["click"] = (false, true, false, true),
            ["type_text"] = (false, true, false, true),
            ["press_key"] = (false, true, false, true),
            ["scroll"] = (false, false, false, true),
            ["focus"] = (false, false, true, true),
            ["task_end"] = (false, false, true, false),
            ["task_stop"] = (false, false, true, false),
            ["desktop_status"] = (true, false, true, false),
        };

        Assert.Equal(expected.Count, tools.GetArrayLength());
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString()!;
            var want = expected[name];
            var annotations = tool.GetProperty("annotations");
            Assert.Equal(want.ReadOnly, annotations.GetProperty("readOnlyHint").GetBoolean());
            Assert.Equal(want.Destructive, annotations.GetProperty("destructiveHint").GetBoolean());
            Assert.Equal(want.Idempotent, annotations.GetProperty("idempotentHint").GetBoolean());
            Assert.Equal(want.OpenWorld, annotations.GetProperty("openWorldHint").GetBoolean());

            var schema = tool.GetProperty("outputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.True(schema.GetProperty("additionalProperties").GetBoolean());
            var properties = schema.GetProperty("properties");
            var isEnd = name is "task_end" or "task_stop";
            var isAction = name is "click" or "type_text" or "press_key" or "scroll" or "focus";
            var isStatus = name == "desktop_status";
            if (isEnd)
            {
                Assert.True(properties.TryGetProperty("ok", out _));
            }
            else if (isAction)
            {
                Assert.True(properties.TryGetProperty("executed", out _));
                Assert.True(properties.TryGetProperty("tier", out _));
                Assert.True(properties.TryGetProperty("verified", out _));
            }
            else if (isStatus)
            {
                Assert.True(properties.TryGetProperty("sessions", out _));
                Assert.True(properties.TryGetProperty("active", out _));
                Assert.True(properties.TryGetProperty("capacity", out _));
            }
            else
            {
                Assert.True(properties.TryGetProperty("token", out _));
                Assert.True(properties.TryGetProperty("generation", out _));
                Assert.True(properties.TryGetProperty("elements", out _));
            }
        }
    }

    [Fact]
    public async Task InitializeEchoesSupportedVersionAndFallsBackOtherwise()
    {
        var gateway = new McpGateway(new FakeTransport());

        var echoed = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}");
        using (var document = JsonDocument.Parse(echoed!))
            Assert.Equal("2024-11-05", document.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());

        var fallback = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2099-01-01\"}}");
        using (var document = JsonDocument.Parse(fallback!))
            Assert.Equal("2024-11-05", document.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task CancelledNotificationStopsInFlightToolCall()
    {
        var transport = new FakeTransport();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Handler = async (token, _) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("不可达");
        };
        var gateway = new McpGateway(transport);

        var call = gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"slow\",\"method\":\"tools/call\",\"params\":{\"name\":\"observe\",\"arguments\":{\"token\":\"t1\"}}}");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var notification = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"slow\"}}");

        Assert.Null(notification);
        var response = await call.WaitAsync(TimeSpan.FromSeconds(5));
        using var document = JsonDocument.Parse(response!);
        Assert.Equal(-32800, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(1, transport.CancelCount);
    }

    [Fact]
    public async Task CancelledNotificationForUnknownRequestIsIgnored()
    {
        var transport = new FakeTransport();
        var gateway = new McpGateway(transport);

        var response = await gateway.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"missing\"}}");

        Assert.Null(response);
        Assert.Equal(0, transport.CancelCount);
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

    [Fact]
    public void IpcProgressLinesAreNotFinalResponses()
    {
        const string progressLine = "{\"id\":\"req\",\"progress\":{\"value\":0.25,\"message\":\"uia\"}}";

        Assert.True(GatewayIpcProtocol.TryParseProgress(progressLine, "req", out var progress));
        Assert.Equal(0.25, progress.Value);
        Assert.Equal("uia", progress.Message);
        Assert.False(GatewayIpcProtocol.TryParseProgress("{\"id\":\"req\",\"ok\":true,\"result\":{}}", "req", out _));
        Assert.False(GatewayIpcProtocol.TryParseProgress(progressLine, "other", out _));
        Assert.False(GatewayIpcProtocol.TryParseProgress("{not-json}", "req", out _));
    }

    [Fact]
    public async Task ProgressIsForwardedAsNotificationsWhenRequestHasAStringToken()
    {
        var transport = new FakeTransport
        {
            Handler = async (_, progress) =>
            {
                Assert.NotNull(progress);
                await progress!(new DesktopProgress(0.2, "capture"));
                await progress(new DesktopProgress(0.55, "uia"));
                return DesktopIpcResponse.Success(JsonDocument.Parse("{\"token\":\"t1\"}").RootElement.Clone());
            },
        };
        var notifications = new List<string>();
        var gateway = new McpGateway(transport, json =>
        {
            notifications.Add(json);
            return Task.CompletedTask;
        });

        var response = await gateway.HandleLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"prog\",\"method\":\"tools/call\",\"params\":{\"name\":\"observe\",\"arguments\":{\"token\":\"t1\"},\"_meta\":{\"progressToken\":\"tok-1\"}}}");

        Assert.NotNull(response);
        Assert.Equal(2, notifications.Count);
        using var document = JsonDocument.Parse(notifications[0]);
        Assert.Equal("notifications/progress", document.RootElement.GetProperty("method").GetString());
        Assert.False(document.RootElement.TryGetProperty("id", out _));
        var parameters = document.RootElement.GetProperty("params");
        Assert.Equal("tok-1", parameters.GetProperty("progressToken").GetString());
        Assert.Equal(0.2, parameters.GetProperty("progress").GetDouble());
        Assert.Equal(1, parameters.GetProperty("total").GetDouble());
        Assert.Equal("capture", parameters.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProgressNumericTokenIsEchoedAsNumber()
    {
        var transport = new FakeTransport
        {
            Handler = async (_, progress) =>
            {
                await progress!(new DesktopProgress(0.4, "execute"));
                return DesktopIpcResponse.Success(JsonDocument.Parse("{}").RootElement.Clone());
            },
        };
        var notifications = new List<string>();
        var gateway = new McpGateway(transport, json =>
        {
            notifications.Add(json);
            return Task.CompletedTask;
        });

        await gateway.HandleLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"prog\",\"method\":\"tools/call\",\"params\":{\"name\":\"click\",\"arguments\":{\"token\":\"t1\"},\"_meta\":{\"progressToken\":42}}}");

        using var document = JsonDocument.Parse(Assert.Single(notifications));
        Assert.Equal(JsonValueKind.Number, document.RootElement.GetProperty("params").GetProperty("progressToken").ValueKind);
        Assert.Equal(42, document.RootElement.GetProperty("params").GetProperty("progressToken").GetInt32());
    }

    [Fact]
    public async Task ProgressIsNotSentWithoutAProgressToken()
    {
        var transport = new FakeTransport();
        var notifications = new List<string>();
        var gateway = new McpGateway(transport, json =>
        {
            notifications.Add(json);
            return Task.CompletedTask;
        });

        await gateway.HandleLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"prog\",\"method\":\"tools/call\",\"params\":{\"name\":\"observe\",\"arguments\":{\"token\":\"t1\"}}}");

        Assert.Null(transport.LastProgress);
        Assert.Empty(notifications);
    }

    private sealed class FakeTransport : IDesktopGatewayTransport
    {
        public List<(string Method, JsonElement Parameters)> Calls { get; } = new();
        public DesktopIpcResponse Response { get; set; } = DesktopIpcResponse.Success(JsonDocument.Parse("{}").RootElement.Clone());
        public Exception? Exception { get; set; }
        public Func<CancellationToken, Func<DesktopProgress, Task>?, Task<DesktopIpcResponse>>? Handler { get; set; }
        public Func<DesktopProgress, Task>? LastProgress { get; private set; }
        public int CancelCount { get; private set; }

        public async Task<DesktopIpcResponse> SendAsync(
            string method,
            JsonElement parameters,
            CancellationToken cancellationToken = default,
            Func<DesktopProgress, Task>? progress = null)
        {
            Calls.Add((method, parameters.Clone()));
            LastProgress = progress;
            if (Exception is not null) throw Exception;
            if (Handler is not null) return await Handler(cancellationToken, progress);
            return Response;
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }
    }
}
