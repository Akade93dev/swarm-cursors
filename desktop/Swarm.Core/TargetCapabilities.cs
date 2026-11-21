namespace Swarm.Core.Semantics;

/// <summary>
/// 目标应用的能力画像：决定输入通道选择与 verified 语义。
/// MessageReliable=false 表示该应用通常忽略合成窗口消息（Chromium/Electron 系）。
/// </summary>
public sealed record TargetCapabilities(
    bool IsChromium,
    bool IsElectron,
    bool UiaAvailable,
    bool SupportsInvoke,
    bool SupportsValue,
    bool SupportsText,
    bool MessageReliable)
{
    public static readonly TargetCapabilities Unknown = new(false, false, false, false, false, false, true);
}

public static class TargetCapabilitiesDetector
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "chromium", "brave", "vivaldi", "opera", "yandex",
    };

    private const string ChromeClass = "Chrome_WidgetWin";
    private const string ChromeRenderClass = "Chrome_RenderWidgetHostHWND";

    public static TargetCapabilities Detect(
        string? processName,
        string? className,
        IEnumerable<string>? patterns,
        bool uiaAvailable)
    {
        var process = NormalizeProcess(processName);
        var chromeClass = Contains(className, ChromeClass) || Contains(className, ChromeRenderClass);
        var browser = Browsers.Contains(process);
        var isChromium = browser;
        // Electron/WebView2 的窗口类与 Chromium 一致，但进程名不是浏览器。
        var isElectron = !browser && (process == "electron" || process == "msedgewebview2" || chromeClass);
        var list = patterns?.ToArray() ?? Array.Empty<string>();
        return new TargetCapabilities(
            IsChromium: isChromium,
            IsElectron: isElectron,
            UiaAvailable: uiaAvailable,
            SupportsInvoke: Has(list, "InvokePattern"),
            SupportsValue: Has(list, "ValuePattern"),
            SupportsText: Has(list, "TextPattern"),
            MessageReliable: !(browser || chromeClass || process == "electron" || process == "msedgewebview2"));
    }

    private static string NormalizeProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return string.Empty;
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    private static bool Contains(string? value, string token) =>
        value != null && value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool Has(IEnumerable<string> patterns, string token) =>
        patterns.Any(pattern => pattern.Contains(token, StringComparison.OrdinalIgnoreCase));
}

/// <summary>B 级输入的独立回读判定：只认“读回内容包含预期文本”。</summary>
public static class MessageVerification
{
    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static bool Matches(string? readback, string expected) =>
        !string.IsNullOrEmpty(readback) &&
        !string.IsNullOrEmpty(expected) &&
        Normalize(readback).Contains(Normalize(expected), StringComparison.Ordinal);
}

public static class MessageEvidence
{
    public const string None = "none";
    public const string WmGetText = "wm_gettext";
    public const string UiaText = "uia_text";
    public const string UiaValue = "uia_value";
    public const string Pixels = "pixels";
}
