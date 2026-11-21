namespace Swarm.Core.Semantics;

/// <summary>UIA 遍历被预算截断的原因，用于决定是否回退以及如何提示用户。</summary>
public enum UiaTruncationReason
{
    None,
    NodeLimit,
    DepthLimit,
    TimeLimit,
}

public enum RebuildMode
{
    Semantic,
    Screenshot,
}

public enum RebuildFallbackReason
{
    None,
    NoElements,
    EmptyMapped,
    SparseSemantics,
    ShellDesktop,
}

public sealed record RebuildDecision(
    RebuildMode Mode,
    RebuildFallbackReason Reason,
    int NamedControls,
    int Elements,
    bool Truncated)
{
    public bool IsFallback => Mode == RebuildMode.Screenshot;
}

/// <summary>
/// 依据采集到的辅助功能信息量选择语义重建或截屏重建。
/// 阈值来自实测：记事本 34 个命名控件、资源管理器 103 个可用语义重建；
/// Chromium/Electron 与桌面图标层只有 2–3 个命名控件，语义模型会近乎空白，应回退截屏。
/// </summary>
public static class RebuildStrategy
{
    public const int MinNamedControls = 5;

    private static readonly HashSet<string> NamedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Edit", "Document", "Button", "CheckBox", "RadioButton", "Hyperlink",
        "MenuItem", "TabItem", "ListItem", "ComboBox", "Slider", "DataItem", "TreeItem", "SplitButton",
    };

    /// <summary>桌面外壳窗口类名：其 UIA 树来自壁纸渲染，语义重建只会切出壁纸文字。</summary>
    private static readonly HashSet<string> ShellClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW",
    };

    public static bool IsShellDesktop(string? className) =>
        !string.IsNullOrEmpty(className) && ShellClassNames.Contains(className);

    /// <summary>桌面强制截屏的固定决策，避免为壁纸白花一次 3 秒的 UIA 遍历。</summary>
    public static RebuildDecision ShellDesktop() =>
        new(RebuildMode.Screenshot, RebuildFallbackReason.ShellDesktop, 0, 0, false);

    public static RebuildDecision Decide(
        IReadOnlyList<UiaElementInfo> elements,
        IReadOnlyList<DesktopElement> mapped,
        UiaTruncationReason truncation,
        bool shellDesktop = false)
    {
        if (shellDesktop) return ShellDesktop();
        var named = CountNamed(elements);
        var truncated = truncation != UiaTruncationReason.None;
        if (elements.Count == 0) return new(RebuildMode.Screenshot, RebuildFallbackReason.NoElements, 0, 0, truncated);
        if (mapped.Count == 0) return new(RebuildMode.Screenshot, RebuildFallbackReason.EmptyMapped, named, 0, truncated);
        if (named < MinNamedControls) return new(RebuildMode.Screenshot, RebuildFallbackReason.SparseSemantics, named, mapped.Count, truncated);
        return new(RebuildMode.Semantic, RebuildFallbackReason.None, named, mapped.Count, truncated);
    }

    public static int CountNamed(IEnumerable<UiaElementInfo> elements) => elements.Count(IsNamed);

    private static bool IsNamed(UiaElementInfo element) =>
        NamedTypes.Contains(element.ControlType) && !string.IsNullOrWhiteSpace(element.Value ?? element.Name);
}
