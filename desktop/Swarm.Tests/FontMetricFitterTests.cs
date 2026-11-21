using Swarm.Core.Visual;

namespace Swarm.Tests;

/// <summary>
/// Tier B 字体度量反解：用已知文本 + 截图墨迹宽高 + 笔画宽度反推字体族/字重/字号。
/// 这里注入合成度量，不依赖本机 GDI 与已装字体，保证 CI 可重复。
/// </summary>
public class FontMetricFitterTests
{
    private sealed class FakeProbes : IFontProbeSource
    {
        private readonly Dictionary<(string Family, int Weight, bool Italic), FontProbe> _probes;

        public FakeProbes(Dictionary<(string, int, bool), FontProbe> probes) => _probes = probes;

        public FontProbe Measure(string text, string family, int weight, bool italic) =>
            _probes.TryGetValue((family, weight, italic), out var probe) ? probe : default;
    }

    private static FontMetricFitter Fitter(params ((string Family, int Weight, bool Italic) Key, FontProbe Probe)[] probes)
    {
        var map = new Dictionary<(string, int, bool), FontProbe>();
        foreach (var (key, probe) in probes) map[key] = probe;
        return new FontMetricFitter(new FakeProbes(map));
    }

    [Fact]
    public void Fit_PicksBoldSegoeForBoldTitleInk()
    {
        // 20pt 粗体标题的实测值：Segoe UI Bold 40px，墨迹 368x39，核心笔画 6px。
        var fitter = Fitter(
            (("Segoe UI", 400, false), new FontProbe(856, 70, 50, 74, 23)),
            (("Segoe UI", 700, false), new FontProbe(914, 70, 50, 74, 23)),
            (("Arial", 700, false), new FontProbe(944, 72, 52, 73, 22)),
            (("Microsoft YaHei UI", 700, false), new FontProbe(985, 76, 54, 80, 25)));

        var result = fitter.Fit(new FontFitRequest("Workspace settings", 368, 39, 6, 1.5));

        Assert.NotNull(result);
        Assert.Equal("Segoe UI", result!.Family);
        Assert.Equal(700, result.Weight);
        Assert.InRange(result.SizeCss, 26.25, 27.25);
        Assert.True(result.Confidence > 0.15, $"confidence={result.Confidence}");
    }

    [Fact]
    public void Fit_HandlesClippedDescender()
    {
        // 控件高度不足时降部被裁掉，墨迹高度只到基线；仍应判定为粗体标题而不是更小字号。
        var fitter = Fitter(
            (("Segoe UI", 400, false), new FontProbe(856, 70, 50, 74, 23)),
            (("Segoe UI", 700, false), new FontProbe(914, 70, 50, 74, 23)),
            (("Tahoma", 700, false), new FontProbe(985, 73, 55, 76, 20)));

        var result = fitter.Fit(new FontFitRequest("Workspace settings", 368, 31, 6, 1.5));

        Assert.NotNull(result);
        Assert.Equal("Segoe UI", result!.Family);
        Assert.Equal(700, result.Weight);
        Assert.InRange(result.SizeCss, 26.25, 27.25);
    }

    [Fact]
    public void Fit_DoesNotMistakeRegularYaHeiForBoldSegoe()
    {
        // 纯 ASCII 下 YaHei Regular 与 Segoe Regular 的宽度/高度/笔画几乎同构，可靠分族不成立；
        // 但笔画比明显低于粗体先验，绝不能判成 Segoe Bold（返回 null 交给像素估计也算正确）。
        var fitter = Fitter(
            (("Microsoft YaHei UI", 400, false), new FontProbe(932, 76, 54, 80, 25)),
            (("Segoe UI", 400, false), new FontProbe(856, 70, 50, 74, 23)),
            (("Segoe UI", 700, false), new FontProbe(914, 70, 50, 74, 23)));

        var result = fitter.Fit(new FontFitRequest("Display name", 374, 42, 4, 1));

        Assert.True(result is null || result.Weight == 400, $"weight={result?.Weight}");
    }

    [Fact]
    public void Fit_ReturnsNullWhenCandidatesAreIndistinguishable()
    {
        // 两个候选的宽度/高度完全相同，置信度为 0：宁可回退像素估计，也不要自信地猜错。
        var fitter = Fitter(
            (("Alpha", 400, false), new FontProbe(900, 70, 50, 72, 21)),
            (("Beta", 400, false), new FontProbe(900, 70, 50, 72, 21)));

        var result = fitter.Fit(
            new FontFitRequest("Settings", 360, 36.4, 4.2, 1),
            new[]
            {
                new FontCandidate("Alpha", 400, false),
                new FontCandidate("Beta", 400, false),
            });

        Assert.Null(result);
    }

    [Fact]
    public void Fit_ReturnsNullWithoutStrokeWhenWeightsCannotBeToldApart()
    {
        // 没有笔画宽度时，同族 400/700 只能得到几乎相同的分数，应判定为不可区分。
        var fitter = Fitter(
            (("Segoe UI", 400, false), new FontProbe(870, 70, 50, 74, 21)),
            (("Segoe UI", 700, false), new FontProbe(917.5, 70, 50, 74, 21)));

        var result = fitter.Fit(new FontFitRequest("Settings", 360, 36.4, null, 1));

        Assert.Null(result);
    }

    [Fact]
    public void Fit_ConvertsPhysicalSizeToCssPixels()
    {
        var fitter = Fitter((("Segoe UI", 700, false), new FontProbe(917.5, 70, 50, 74, 21)));

        var result = fitter.Fit(new FontFitRequest("Workspace settings", 917.5, 91, 10.5, 2));

        Assert.NotNull(result);
        Assert.InRange(result!.SizePhysical, 99.5, 100.5);
        Assert.InRange(result.SizeCss, 49.75, 50.25);
    }

    [Fact]
    public void Fit_ReturnsNullForEmptyInk()
    {
        var fitter = Fitter((("Segoe UI", 400, false), new FontProbe(870, 70, 50, 74, 21)));

        Assert.Null(fitter.Fit(new FontFitRequest("", 0, 0, null, 1)));
        Assert.Null(fitter.Fit(new FontFitRequest("Text", 0, 20, null, 1)));
    }

    [Theory]
    [InlineData(0.10, 400)]
    [InlineData(0.15, 400)]
    [InlineData(0.21, 400)]
    [InlineData(0.22, 700)]
    [InlineData(0.30, 700)]
    public void ClassifyWeight_UsesStrokeToXHeightRatio(double ratio, int expected)
    {
        Assert.Equal(expected, FontMetricFitter.ClassifyWeight(ratio));
    }
}
