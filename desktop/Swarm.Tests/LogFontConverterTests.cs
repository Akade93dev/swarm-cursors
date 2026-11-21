using Swarm.Core.Visual;

namespace Swarm.Tests;

public class LogFontConverterTests
{
    [Fact]
    public void Convert_TreatsNegativeHeightAsEmSize()
    {
        var font = LogFontConverter.Convert(new LogFontMetrics(-40, 0, 0, 700, false, "Segoe UI"), 1.5);

        Assert.NotNull(font);
        Assert.Equal("Segoe UI", font!.Value.Family);
        Assert.Equal(700, font.Value.Weight);
        Assert.False(font.Value.Italic);
        Assert.InRange(font.Value.SizeCss!.Value, 26.6, 26.7);
    }

    [Fact]
    public void Convert_SubtractsInternalLeadingForPositiveHeight()
    {
        var font = LogFontConverter.Convert(new LogFontMetrics(48, 48, 8, 400, false, "Arial"), 1.5);

        Assert.NotNull(font);
        Assert.InRange(font.Value.SizeCss!.Value, 26.6, 26.7);
    }

    [Fact]
    public void Convert_DividesPhysicalPixelsByScale()
    {
        var font = LogFontConverter.Convert(new LogFontMetrics(-40, 0, 0, 400, false, "Segoe UI"), 2);

        Assert.NotNull(font);
        Assert.Equal(20, font!.Value.SizeCss);
    }

    [Fact]
    public void Convert_KeepsItalicFlag()
    {
        var font = LogFontConverter.Convert(new LogFontMetrics(-20, 0, 0, 400, true, "Segoe UI"), 1);

        Assert.NotNull(font);
        Assert.True(font!.Value.Italic);
        Assert.Equal(20, font.Value.SizeCss);
    }

    [Fact]
    public void Convert_RejectsUnknownWeightAndEmptyFace()
    {
        var font = LogFontConverter.Convert(new LogFontMetrics(-20, 0, 0, 0, false, " "), 1);

        Assert.NotNull(font);
        Assert.Null(font!.Value.Weight);
        Assert.Null(font.Value.Family);
    }

    [Fact]
    public void Convert_ReturnsNullWhenHeightIsZero()
    {
        var font = LogFontConverter.Convert(new LogFontMetrics(0, 0, 0, 400, false, "Segoe UI"), 1);

        Assert.Null(font);
    }
}
