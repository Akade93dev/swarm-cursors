using System.Text.Json;
using Swarm.Core.Capture;
using Swarm.Core.Governance;
using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class GovernanceTests
{
    private static UiaElementInfo Element(string id, string? name = null, string? value = null, bool password = false, int processId = 10) => new()
    {
        Id = id,
        ControlType = "Edit",
        Name = name,
        Value = value,
        Bounds = new RectInt(100, 100, 120, 24),
        ProcessId = processId,
        IsPassword = password,
        HasVisual = true,
    };

    private static TaskPolicy ParsePolicy(object policy) =>
        TaskPolicy.Parse(JsonSerializer.SerializeToElement(new { policy }));

    [Fact]
    public void 缺省策略保守且开启遮罩()
    {
        var policy = TaskPolicy.Default;
        Assert.False(policy.ReadOnly);
        Assert.False(policy.AllowCrossApp);
        Assert.Equal(500, policy.MaxActions);
        Assert.True(policy.RequiresConfirmation("send"));
        Assert.True(policy.RequiresConfirmation("cross_app"));
        Assert.Equal(RedactionOptions.MaskMode, policy.EffectiveRedaction.Mode);
    }

    [Fact]
    public void 策略解析覆盖全部字段()
    {
        var policy = ParsePolicy(new
        {
            allowedProcesses = new[] { "notepad.exe" },
            allowedTitles = new[] { "Untitled" },
            readOnly = true,
            maxActions = 3,
            maxDurationMs = 5_000,
            allowCrossApp = true,
            requireConfirmFor = Array.Empty<string>(),
            redaction = new { mode = "metadata", categories = new[] { "email" } },
        });

        Assert.Equal(new[] { "notepad.exe" }, policy.AllowedProcesses);
        Assert.Equal(new[] { "Untitled" }, policy.AllowedTitles);
        Assert.True(policy.ReadOnly);
        Assert.Equal(3, policy.MaxActions);
        Assert.Equal(5_000, policy.MaxDurationMs);
        Assert.True(policy.AllowCrossApp);
        Assert.False(policy.RequiresConfirmation("send"));
        Assert.Equal(RedactionOptions.MetadataMode, policy.EffectiveRedaction.Mode);
        Assert.True(policy.EffectiveRedaction.Allows("email"));
        Assert.False(policy.EffectiveRedaction.Allows("phone"));
    }

    [Fact]
    public void 非法策略直接拒绝()
    {
        Assert.Throws<ArgumentException>(() => ParsePolicy(new { readOnly = "yes" }));
        Assert.Throws<ArgumentException>(() => ParsePolicy(new { maxActions = "many" }));
        Assert.Throws<ArgumentException>(() => ParsePolicy(new { redaction = new { mode = "blur" } }));
        Assert.Throws<ArgumentException>(() => ParsePolicy(new { allowedProcesses = "notepad" }));
    }

    [Fact]
    public void 进程与标题白名单在开始阶段生效()
    {
        var policy = ParsePolicy(new { allowedProcesses = new[] { "notepad.exe" }, allowedTitles = new[] { "Untitled" } });
        Assert.True(PolicyEvaluator.Begin(policy, "notepad", "Untitled - Notepad").Allowed);

        var processDenied = PolicyEvaluator.Begin(policy, "mspaint", "Untitled - Paint");
        Assert.False(processDenied.Allowed);
        Assert.Equal("process_not_allowed", processDenied.Reason);

        var titleDenied = PolicyEvaluator.Begin(policy, "notepad", "README.md - Notepad");
        Assert.False(titleDenied.Allowed);
        Assert.Equal("title_not_allowed", titleDenied.Reason);
    }

    [Fact]
    public void 只读动作计数时长与跨应用分别拒绝()
    {
        var readOnly = new TaskPolicy(ReadOnly: true);
        Assert.False(PolicyEvaluator.Action(readOnly, "click", TimeSpan.Zero, 0, false).Allowed);
        Assert.True(PolicyEvaluator.Action(readOnly, "observe", TimeSpan.Zero, 0, false).Allowed);

        var capped = new TaskPolicy(MaxActions: 2);
        Assert.False(PolicyEvaluator.Action(capped, "click", TimeSpan.Zero, 2, false).Allowed);

        var timed = new TaskPolicy(MaxDurationMs: 1_000);
        Assert.Equal("time_exceeded", PolicyEvaluator.Action(timed, "click", TimeSpan.FromSeconds(2), 0, false).Reason);

        var cross = new TaskPolicy(AllowCrossApp: false);
        Assert.Equal("cross_app", PolicyEvaluator.Action(cross, "click", TimeSpan.Zero, 0, true).Reason);
    }

    [Fact]
    public void 敏感区域按元数据与正则识别()
    {
        var elements = new[]
        {
            Element("password", value: "hunter2", password: true),
            Element("credential", name: "Password"),
            Element("email", value: "alice@example.com"),
            Element("id", value: "11010519491231002X"),
            Element("bank", value: "6222021234567890123"),
            Element("phone", value: "13800138000"),
            Element("plain", value: "hello"),
        };
        var hits = RedactionDetector.Detect(RedactionOptions.Default, elements);
        Assert.Equal(new[] { "password", "credential", "email", "id", "bank", "phone" }, hits.Select(hit => hit.Category));
        Assert.DoesNotContain(hits, hit => hit.ElementId == "plain");
        Assert.All(hits, hit => Assert.False(hit.Necessary));
    }

    [Fact]
    public void 关闭遮罩与类别白名单被尊重()
    {
        var elements = new[] { Element("email", value: "alice@example.com"), Element("phone", value: "13800138000") };
        Assert.Empty(RedactionDetector.Detect(new RedactionOptions(RedactionOptions.OffMode), elements));

        var hits = RedactionDetector.Detect(new RedactionOptions(RedactionOptions.MaskMode, new[] { "email" }), elements);
        Assert.Single(hits);
        Assert.Equal("email", hits[0].Category);
    }

    [Fact]
    public void 像素遮罩只改副本并裁剪到帧内()
    {
        var frame = new RgbaFrame(10, 10, new byte[10 * 10 * 4]);
        var masked = RedactionRenderer.Mask(frame, new RectInt(100, 100, 0, 0), new[] { new RectInt(102, 103, 2, 2), new RectInt(95, 95, 2, 2) });

        var inside = (3 * 10 + 2) * 4;
        Assert.Equal(18, masked.Pixels[inside]);
        Assert.Equal(255, masked.Pixels[inside + 3]);
        Assert.Equal(0, masked.Pixels[(9 * 10 + 9) * 4]);
        Assert.All(frame.Pixels, pixel => Assert.Equal(0, pixel));
    }

    [Fact]
    public void 审计写入序号字段且不含原文()
    {
        var directory = Path.Combine(Path.GetTempPath(), "swarm-audit-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new AuditWriter(directory, "task1"))
            {
                writer.Write(new AuditEntry("task1", DateTimeOffset.UnixEpoch, "type_text", "uia",
                    new AuditTarget("notepad", AuditHash.Sha256("密码输入框"), "Edit"),
                    "allow",
                    new[] { new AuditRedaction("password", "high", false) },
                    AuditHash.Sha256("top-secret"), "top-secret".Length, "ok", 12));
            }

            var path = Path.Combine(directory, "audit-task1.jsonl");
            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            Assert.DoesNotContain("top-secret", lines[0]);
            using var document = JsonDocument.Parse(lines[0]);
            var root = document.RootElement;
            Assert.Equal("task1", root.GetProperty("taskId").GetString());
            Assert.Equal(0, root.GetProperty("seq").GetInt64());
            Assert.Equal("type_text", root.GetProperty("tool").GetString());
            Assert.Equal("allow", root.GetProperty("policyDecision").GetString());
            Assert.Equal("ok", root.GetProperty("result").GetString());
            Assert.Equal(AuditHash.Sha256("top-secret"), root.GetProperty("textHash").GetString());
            Assert.Equal(10, root.GetProperty("textLength").GetInt32());
            Assert.Equal("high", root.GetProperty("redactions")[0].GetProperty("risk").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void 审计超过阈值后轮转()
    {
        var directory = Path.Combine(Path.GetTempPath(), "swarm-audit-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new AuditWriter(directory, "task2", rotationBytes: 1024))
            {
                for (var i = 0; i < 6; i++)
                {
                    writer.Write(new AuditEntry("task2", DateTimeOffset.UnixEpoch, "observe", null, null, "allow",
                        Array.Empty<AuditRedaction>(), null, 0, "ok", i));
                }
            }
            var files = Directory.GetFiles(directory, "audit-task2*.jsonl");
            Assert.True(files.Length >= 2);
            Assert.Equal(6, files.Sum(file => File.ReadAllLines(file).Length));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void 确认令牌一次性且绑定任务与动作()
    {
        var broker = new ConfirmationBroker();
        var issued = broker.Issue("task", "fp1");

        Assert.True(broker.TryConsume("task", issued.Token, "fp1", out _));
        Assert.False(broker.TryConsume("task", issued.Token, "fp1", out var reused));
        Assert.Equal("unknown_or_used", reused);

        var second = broker.Issue("task", "fp1");
        Assert.False(broker.TryConsume("task", second.Token, "fp2", out var mismatch));
        Assert.Equal("fingerprint_mismatch", mismatch);
        Assert.True(broker.TryConsume("task", second.Token, "fp1", out _));

        var third = broker.Issue("task", "fp1");
        Assert.False(broker.TryConsume("other", third.Token, "fp1", out var wrongTask));
        Assert.Equal("task_mismatch", wrongTask);
    }

    [Fact]
    public void 过期与缺失令牌被拒绝()
    {
        var broker = new ConfirmationBroker(TimeSpan.Zero);
        var issued = broker.Issue("task", "fp");
        Assert.False(broker.TryConsume("task", issued.Token, "fp", out var expired));
        Assert.Equal("expired", expired);
        Assert.False(broker.TryConsume("task", null, "fp", out var missing));
        Assert.Equal("missing", missing);
    }

    [Fact]
    public void 动作类别与动作指纹稳定()
    {
        Assert.Equal(ActionClassifier.Delete, ActionClassifier.Classify("Delete file"));
        Assert.Equal(ActionClassifier.Delete, ActionClassifier.Classify(null, "DELETE"));
        Assert.Equal(ActionClassifier.Purchase, ActionClassifier.Classify("立即购买"));
        Assert.Equal(ActionClassifier.Send, ActionClassifier.Classify("发送邮件"));
        Assert.Equal(ActionClassifier.Submit, ActionClassifier.Classify("Submit form"));
        Assert.Null(ActionClassifier.Classify("保存"));

        var a = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["token"] = "t", ["label"] = "删除" });
        var b = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["token"] = "t", ["label"] = "删除", ["confirmToken"] = "abc" });
        var c = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["token"] = "t", ["label"] = "保留" });
        Assert.Equal(ActionFingerprint.Compute("click", a), ActionFingerprint.Compute("click", b));
        Assert.NotEqual(ActionFingerprint.Compute("click", a), ActionFingerprint.Compute("click", c));
    }
}
