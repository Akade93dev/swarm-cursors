using System.Text.Json;
using System.Text.Json.Nodes;
using Swarm.Gateway;

namespace Swarm.Tests;

/// <summary>
/// 回放 transcripts 目录下的 MCP 会话金样本：每步发送一行客户端消息，
/// 断言服务器通知与响应（按键排序后的 JSON 逐字比较，避免属性顺序造成假失败）。
/// </summary>
public class McpTranscriptTests
{
    private static readonly string TranscriptDirectory = Path.Combine(AppContext.BaseDirectory, "transcripts");

    public static IEnumerable<object[]> Goldens() =>
        Directory.Exists(TranscriptDirectory)
            ? Directory.EnumerateFiles(TranscriptDirectory, "*.json")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Select(path => new object[] { Path.GetFileName(path), path })
            : Array.Empty<object[]>();

    [Fact]
    public void GoldenTranscriptsArePresent() => Assert.NotEmpty(Goldens());

    [Theory]
    [MemberData(nameof(Goldens))]
    public async Task GoldenTranscriptMatches(string name, string path)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;
        var notifications = new List<string>();
        var transport = TranscriptTransport.Create(root);
        var gateway = new McpGateway(transport, line =>
        {
            notifications.Add(line);
            return Task.CompletedTask;
        });

        Task<string?>? held = null;
        var index = 0;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            notifications.Clear();
            string? response;
            if (step.TryGetProperty("send", out var send))
            {
                var call = gateway.HandleLineAsync(send.GetRawText());
                if (step.TryGetProperty("hold", out var hold) && hold.GetBoolean())
                {
                    // 在途请求不等待响应，先交给下一步的取消通知去打断。
                    held = call;
                    AssertStep(name, index, step, null, notifications);
                    if (transport.Started is { } started) await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    index++;
                    continue;
                }
                response = await call.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                Assert.NotNull(held);
                response = await held!.WaitAsync(TimeSpan.FromSeconds(10));
                held = null;
            }

            AssertStep(name, index, step, response, notifications);
            index++;
        }

        Assert.Null(held);
    }

    private static void AssertStep(string name, int index, JsonElement step, string? response, List<string> notifications)
    {
        var hasExpectation = step.TryGetProperty("expect", out var expected) && expected.ValueKind == JsonValueKind.Object;
        if (hasExpectation && expected.TryGetProperty("notifications", out var expectedNotifications))
        {
            var want = expectedNotifications.EnumerateArray().Select(item => Normalize(item.GetRawText())).ToArray();
            var actual = notifications.Select(Normalize).ToArray();
            Assert.True(want.SequenceEqual(actual),
                $"{name} 第 {index + 1} 步通知不一致。\n期望：{string.Join("\n", want)}\n实际：{string.Join("\n", actual)}");
        }
        else
        {
            Assert.True(notifications.Count == 0,
                $"{name} 第 {index + 1} 步不应有通知，实际：{string.Join("\n", notifications)}");
        }

        if (hasExpectation && expected.TryGetProperty("response", out var expectedResponse) && expectedResponse.ValueKind != JsonValueKind.Null)
        {
            Assert.NotNull(response);
            var want = Normalize(expectedResponse.GetRawText());
            var actual = Normalize(response!);
            Assert.True(want == actual, $"{name} 第 {index + 1} 步响应不一致。\n期望：{want}\n实际：{actual}");
        }
        else
        {
            Assert.True(response is null, $"{name} 第 {index + 1} 步不应有响应，实际：{response}");
        }
    }

    private static string Normalize(string json) => Sort(JsonNode.Parse(json)!).ToJsonString();

    private static JsonNode Sort(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value is null ? null : Sort(pair.Value.DeepClone())))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Sort(item.DeepClone())).ToArray()),
        _ => node.DeepClone(),
    };

    /// <summary>金样本里的 transport 描述一次桌面端行为：正常结果、错误、进度、挂起等待取消或传输异常。</summary>
    private sealed class TranscriptTransport : IDesktopGatewayTransport
    {
        public DesktopIpcResponse Result { get; init; } = DesktopIpcResponse.Success(JsonDocument.Parse("{}").RootElement.Clone());
        public IReadOnlyList<DesktopProgress> Progress { get; init; } = Array.Empty<DesktopProgress>();
        public string? ThrowMessage { get; init; }
        public bool HoldUntilCancelled { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TranscriptTransport Create(JsonElement scenario)
        {
            if (!scenario.TryGetProperty("transport", out var config) || config.ValueKind != JsonValueKind.Object)
                return new TranscriptTransport();

            var trace = new TranscriptTransport
            {
                ThrowMessage = config.TryGetProperty("throw", out var message) ? message.GetString() : null,
                HoldUntilCancelled = config.TryGetProperty("holdUntilCancelled", out var hold) && hold.GetBoolean(),
                Progress = config.TryGetProperty("progress", out var progress)
                    ? progress.EnumerateArray()
                        .Select(item => new DesktopProgress(
                            item.GetProperty("value").GetDouble(),
                            item.TryGetProperty("message", out var text) ? text.GetString() : null))
                        .ToArray()
                    : Array.Empty<DesktopProgress>(),
                Result = config.TryGetProperty("error", out var error)
                    ? DesktopIpcResponse.Failure(new DesktopIpcError(
                        error.GetProperty("code").GetString()!,
                        error.GetProperty("message").GetString()!,
                        error.TryGetProperty("reason", out var reason) ? reason.GetString() : null))
                    : config.TryGetProperty("result", out var result)
                        ? DesktopIpcResponse.Success(result.Clone())
                        : new TranscriptTransport().Result,
            };
            return trace;
        }

        public async Task<DesktopIpcResponse> SendAsync(
            string method,
            JsonElement parameters,
            CancellationToken cancellationToken = default,
            Func<DesktopProgress, Task>? progress = null)
        {
            if (ThrowMessage is not null) throw new IOException(ThrowMessage);
            if (HoldUntilCancelled)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            foreach (var update in Progress)
            {
                if (progress is not null) await progress(update);
            }
            return Result;
        }

        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
