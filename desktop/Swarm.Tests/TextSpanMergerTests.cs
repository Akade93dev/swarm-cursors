using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class TextSpanMergerTests
{
    private static SpanStyle Italic(double size = 10) => new("Segoe UI", size, 400, true, "rgb(0,0,0)");

    private static SpanStyle Regular(double size = 10) => new("Segoe UI", size, 400, false, "rgb(0,0,0)");

    private static SpanStyle Fallback() => new("Tahoma", 9, 400, false, "rgb(255,255,255)");

    [Fact]
    public void Merge_SplitsAtItalicBoundary()
    {
        const string text = "ItalicHead normal black text";
        var styles = new SpanStyle?[text.Length];
        for (var i = 0; i < text.Length; i++) styles[i] = i < 10 ? Italic() : Regular();

        var spans = TextSpanMerger.Merge(text, styles, Fallback());

        Assert.Equal(2, spans.Count);
        Assert.Equal("ItalicHead", spans[0].Text);
        Assert.True(spans[0].Style.Italic);
        Assert.Equal(" normal black text", spans[1].Text);
        Assert.False(spans[1].Style.Italic);
        Assert.Equal(text, Join(spans));
    }

    [Fact]
    public void Merge_InheritsPreviousStyleWhenCharacterStyleIsUnknown()
    {
        const string text = "abcdef";
        var styles = new SpanStyle?[] { Regular(), null, null, Italic(), null, Italic() };

        var spans = TextSpanMerger.Merge(text, styles, Fallback());

        Assert.Equal(2, spans.Count);
        Assert.Equal("abc", spans[0].Text);
        Assert.Equal("def", spans[1].Text);
        Assert.Equal(text, Join(spans));
    }

    [Fact]
    public void Merge_UsesFallbackForLeadingUnknownStyles()
    {
        // 前导 null 不能被丢掉：拼接必须还原原文，样式回落到元素级 fallback。
        const string text = "abcdef";
        var styles = new SpanStyle?[] { null, null, Italic(), null, null, null };

        var spans = TextSpanMerger.Merge(text, styles, Fallback());

        Assert.Equal(2, spans.Count);
        Assert.Equal("ab", spans[0].Text);
        Assert.Equal(Fallback(), spans[0].Style);
        Assert.Equal("cdef", spans[1].Text);
        Assert.Equal(text, Join(spans));
    }

    [Fact]
    public void Merge_KeepsWholeTextWhenNoStyleIsKnown()
    {
        const string text = "abcdef";
        var spans = TextSpanMerger.Merge(text, new SpanStyle?[text.Length], Fallback());

        var span = Assert.Single(spans);
        Assert.Equal(text, span.Text);
        Assert.Equal(Fallback(), span.Style);
    }

    [Fact]
    public void Merge_ReturnsSingleSpanForUniformText()
    {
        const string text = "Hello";
        var styles = Enumerable.Repeat<SpanStyle?>(Regular(), text.Length).ToArray();

        var spans = TextSpanMerger.Merge(text, styles, Fallback());

        var span = Assert.Single(spans);
        Assert.Equal(text, span.Text);
        Assert.Equal(Regular(), span.Style);
    }

    [Fact]
    public void Merge_MergesOverflowIntoLastSpanToKeepFullText()
    {
        var text = new string('a', 40);
        var styles = new SpanStyle?[text.Length];
        for (var i = 0; i < text.Length; i++) styles[i] = Regular(i);

        var spans = TextSpanMerger.Merge(text, styles, Fallback(), maxSpans: 3);

        Assert.Equal(3, spans.Count);
        Assert.Equal(text, Join(spans));
    }

    [Fact]
    public void Merge_KeepsFullTextWhenMaxSpansIsOne()
    {
        const string text = "abcdef";
        var styles = new SpanStyle?[] { Regular(), Regular(), Regular(), Italic(), Italic(), Italic() };

        var span = Assert.Single(TextSpanMerger.Merge(text, styles, Fallback(), maxSpans: 1));

        Assert.Equal(text, span.Text);
    }

    [Fact]
    public void Merge_DoesNotSplitSurrogatePair()
    {
        // 样式在代理对中间变化时不能切：切成两个孤立代理项会渲染成替换字符。
        const string text = "a😀b";
        var styles = new SpanStyle?[] { Regular(), Regular(), Italic(), Italic() };

        var spans = TextSpanMerger.Merge(text, styles, Fallback());

        Assert.Equal(2, spans.Count);
        Assert.Equal("a😀", spans[0].Text);
        Assert.Equal("b", spans[1].Text);
        Assert.Equal(text, Join(spans));
    }

    [Fact]
    public void Merge_ReturnsEmptyForEmptyInput()
    {
        Assert.Empty(TextSpanMerger.Merge("", Array.Empty<SpanStyle?>(), Fallback()));
    }

    private static string Join(IReadOnlyList<(string Text, SpanStyle Style)> spans) =>
        string.Concat(spans.Select(span => span.Text));
}
