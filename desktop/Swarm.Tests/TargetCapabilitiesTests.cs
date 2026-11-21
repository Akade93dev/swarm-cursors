using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class TargetCapabilitiesTests
{
    [Fact]
    public void Chrome窗口判定为Chromium且消息不可靠()
    {
        var result = TargetCapabilitiesDetector.Detect("chrome", "Chrome_WidgetWin_1", null, true);
        Assert.True(result.IsChromium);
        Assert.False(result.IsElectron);
        Assert.False(result.MessageReliable);
    }

    [Fact]
    public void Edge进程名带exe后缀与大小写仍可识别()
    {
        var result = TargetCapabilitiesDetector.Detect("MSEDGE.EXE", "Chrome_WidgetWin_1", null, true);
        Assert.True(result.IsChromium);
        Assert.False(result.MessageReliable);
    }

    [Fact]
    public void 非浏览器进程的Chrome窗口类判定为Electron嵌入()
    {
        var result = TargetCapabilitiesDetector.Detect("Code", "Chrome_WidgetWin_1", null, true);
        Assert.False(result.IsChromium);
        Assert.True(result.IsElectron);
        Assert.False(result.MessageReliable);
    }

    [Fact]
    public void WebView2子进程判定为Electron嵌入且消息不可靠()
    {
        var result = TargetCapabilitiesDetector.Detect("msedgewebview2", null, null, true);
        Assert.True(result.IsElectron);
        Assert.False(result.MessageReliable);
    }

    [Fact]
    public void 原生Win32窗口消息通道可靠()
    {
        var result = TargetCapabilitiesDetector.Detect("notepad", "Notepad", null, true);
        Assert.False(result.IsChromium);
        Assert.False(result.IsElectron);
        Assert.True(result.MessageReliable);
    }

    [Fact]
    public void UIA模式能力按程序化名称标记()
    {
        var patterns = new[]
        {
            "InvokePatternIdentifiers.Pattern",
            "ValuePatternIdentifiers.Pattern",
            "TextPatternIdentifiers.Pattern",
        };
        var result = TargetCapabilitiesDetector.Detect("notepad", "Notepad", patterns, true);
        Assert.True(result.SupportsInvoke);
        Assert.True(result.SupportsValue);
        Assert.True(result.SupportsText);
    }

    [Fact]
    public void 无UIA模式时能力全部为假()
    {
        var result = TargetCapabilitiesDetector.Detect("notepad", "Notepad", Array.Empty<string>(), true);
        Assert.False(result.SupportsInvoke);
        Assert.False(result.SupportsValue);
        Assert.False(result.SupportsText);
    }

    [Fact]
    public void UIA不可用时如实标记()
    {
        var result = TargetCapabilitiesDetector.Detect("notepad", "Notepad", null, false);
        Assert.False(result.UiaAvailable);
    }

    [Fact]
    public void 回读必须包含预期文本()
    {
        Assert.True(MessageVerification.Matches("hello world", "world"));
        Assert.False(MessageVerification.Matches("hello world", "mars"));
    }

    [Fact]
    public void 回读按CRLF与CR规范化后比较()
    {
        Assert.True(MessageVerification.Matches("line1\r\nline2", "line1\nline2"));
        Assert.True(MessageVerification.Matches("line1\rline2", "line1\nline2"));
    }

    [Fact]
    public void 空回读或空预期永不通过()
    {
        Assert.False(MessageVerification.Matches(null, "text"));
        Assert.False(MessageVerification.Matches("", "text"));
        Assert.False(MessageVerification.Matches("text", ""));
    }
}
