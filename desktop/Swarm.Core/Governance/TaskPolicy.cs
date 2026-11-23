using System.Text.Json;

namespace Swarm.Core.Governance;

/// <summary>脱敏模式：mask 同时遮文本与像素；metadata 只把文本替换成类别标记；off 显式关闭。</summary>
public sealed record RedactionOptions(
    string Mode = RedactionOptions.MaskMode,
    IReadOnlyList<string>? Categories = null)
{
    public const string MaskMode = "mask";
    public const string MetadataMode = "metadata";
    public const string OffMode = "off";

    public static RedactionOptions Default { get; } = new();

    public bool IsOff => string.Equals(Mode, OffMode, StringComparison.OrdinalIgnoreCase);

    public bool IsMetadataOnly => string.Equals(Mode, MetadataMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>categories 为空表示启用全部类别；否则只对白名单里的类别生效。</summary>
    public bool Allows(string category) =>
        Categories is not { Count: > 0 } || Categories.Contains(category, StringComparer.OrdinalIgnoreCase);

    public static RedactionOptions Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("policy.redaction 必须是对象");
        var mode = RedactionOptions.Default.Mode;
        if (element.TryGetProperty("mode", out var modeElement))
        {
            if (modeElement.ValueKind != JsonValueKind.String)
                throw new ArgumentException("policy.redaction.mode 必须是字符串");
            mode = (modeElement.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            if (mode is not (MaskMode or MetadataMode or OffMode))
                throw new ArgumentException("policy.redaction.mode 仅支持 mask、metadata、off");
        }
        IReadOnlyList<string>? categories = null;
        if (element.TryGetProperty("categories", out var categoriesElement))
            categories = ReadStringArray(categoriesElement, "policy.redaction.categories");
        return new RedactionOptions(mode, categories);
    }

    internal static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{name} 必须是字符串数组");
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"{name} 只能包含字符串");
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
        }
        return values;
    }
}

/// <summary>
/// task_begin 的治理策略。缺省即保守：只允许当前已授权的目标进程、禁跨应用、
/// 破坏性/外发动作需要 confirmToken、敏感区域按 mask 脱敏。
/// </summary>
public sealed record TaskPolicy(
    IReadOnlyList<string>? AllowedProcesses = null,
    IReadOnlyList<string>? AllowedTitles = null,
    bool ReadOnly = false,
    int MaxActions = 500,
    long MaxDurationMs = 600_000,
    bool AllowCrossApp = false,
    IReadOnlyList<string>? RequireConfirmFor = null,
    RedactionOptions? Redaction = null)
{
    public static readonly IReadOnlyList<string> DefaultConfirmCategories =
        new[] { "submit", "delete", "send", "purchase", "cross_app" };

    public static TaskPolicy Default { get; } = new();

    public RedactionOptions EffectiveRedaction => Redaction ?? RedactionOptions.Default;

    /// <summary>未配置时按缺省类别要求确认；显式空数组表示全部免确认。</summary>
    public bool RequiresConfirmation(string category) =>
        (RequireConfirmFor ?? DefaultConfirmCategories)
        .Any(item => string.Equals(item, category, StringComparison.OrdinalIgnoreCase));

    public static TaskPolicy Parse(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("policy", out var policy) ||
            policy.ValueKind == JsonValueKind.Null)
        {
            return Default;
        }
        if (policy.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("policy 必须是对象");

        IReadOnlyList<string>? processes = null;
        if (policy.TryGetProperty("allowedProcesses", out var processElement))
            processes = RedactionOptions.ReadStringArray(processElement, "policy.allowedProcesses");
        IReadOnlyList<string>? titles = null;
        if (policy.TryGetProperty("allowedTitles", out var titleElement))
            titles = RedactionOptions.ReadStringArray(titleElement, "policy.allowedTitles");
        IReadOnlyList<string>? confirm = null;
        if (policy.TryGetProperty("requireConfirmFor", out var confirmElement))
            confirm = RedactionOptions.ReadStringArray(confirmElement, "policy.requireConfirmFor");

        var readOnly = false;
        if (policy.TryGetProperty("readOnly", out var readOnlyElement))
        {
            if (readOnlyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("policy.readOnly 必须是布尔值");
            readOnly = readOnlyElement.GetBoolean();
        }

        var maxActions = Default.MaxActions;
        if (policy.TryGetProperty("maxActions", out var actionsElement))
        {
            if (actionsElement.ValueKind != JsonValueKind.Number || !actionsElement.TryGetInt32(out var parsed))
                throw new ArgumentException("policy.maxActions 必须是整数");
            maxActions = Math.Clamp(parsed, 1, 10_000);
        }

        var maxDuration = Default.MaxDurationMs;
        if (policy.TryGetProperty("maxDurationMs", out var durationElement))
        {
            if (durationElement.ValueKind != JsonValueKind.Number || !durationElement.TryGetInt64(out var parsed))
                throw new ArgumentException("policy.maxDurationMs 必须是整数");
            maxDuration = Math.Clamp(parsed, 1_000, 86_400_000);
        }

        var allowCrossApp = false;
        if (policy.TryGetProperty("allowCrossApp", out var crossAppElement))
        {
            if (crossAppElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("policy.allowCrossApp 必须是布尔值");
            allowCrossApp = crossAppElement.GetBoolean();
        }

        RedactionOptions? redaction = null;
        if (policy.TryGetProperty("redaction", out var redactionElement))
            redaction = RedactionOptions.Parse(redactionElement);

        return new TaskPolicy(processes, titles, readOnly, maxActions, maxDuration, allowCrossApp, confirm, redaction);
    }
}
