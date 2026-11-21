using Swarm.Core.Semantics;

namespace Swarm.Tests;

public class TextSpanMergerTests
{
    private static SpanStyle Italic(double size = 10) => new("Segoe UI", size, 400, true, "rgb(0,0,0)");

    private static SpanStyle Regular(double size = 10) => new("Segoe UI", size, 400, false, "rgb(0,0,0)");

    [Fact]
    public void Merge_SplitsAtItalicBoundary()
    {
        const string text = "ItalicHead normal black text";
        var styles = new SpanStyle?[text.Length];
        for (var i = 0; i < text.Length; i++) styles[i] = i < 10 ? Italic() : Regular();

        var spans = TextSpanMerger.Merge(text, styles);

        Assert.Equal(2, spans.Count);
        Assert.Equal("ItalicHead", spans[0].Text);
        Assert.True(spans[0].Style.Italic);
        Assert.Equal(" normal black text", spans[1].Text);
        Assert.False(spans[1].Style.Italic);
    }

    [Fact]
    public void Merge_InheritsPreviousStyleWhenCharacterStyleIsUnknown()
    {
        const string text = "abcdef";
        var styles = new SpanStyle?[] { Regular(), null, null, Italic(), null, Italic() };

        var spans = TextSpanMerger.Merge(text, styles);

        Assert.Equal(2, spans.Count);
        Assert.Equal("abc", spans[0].Text);
        Assert.Equal("def", spans[1].Text);
    }

    [Fact]
    public void Merge_ReturnsSingleSpanForUniformText()
    {
        const string text = "Hello";
        var styles = Enumerable.Repeat<SpanStyle?>(Regular(), text.Length).ToArray();

        var spans = TextSpanMerger.Merge(text, styles);

        Assert.Single(spans);
        Assert.Equal(text, spans[0].Text);
    }

    [Fact]
    public void Merge_MergesOverflowIntoLastSpanToKeepFullText()
    {
        var text = new string('a', 40);
        var styles = new SpanStyle?[text.Length];
        for (var i = 0; i < text.Length; i++) styles[i] = Regular(i);

        var spans = TextSpanMerger.Merge(text, styles, maxSpans: 3);

        Assert.Equal(3, spans.Count);
        Assert.Equal(text, string.Concat(spans.Select(span => span.Text)));
    }

    [Fact]
    public void Merge_ReturnsEmptyForEmptyInput()
    {
        Assert.Empty(TextSpanMerger.Merge("", Array.Empty<SpanStyle?>()));
        Assert.Empty(TextSpanMerger.Merge("abc", new SpanStyle?[] { null, null, null }));
    }
}
