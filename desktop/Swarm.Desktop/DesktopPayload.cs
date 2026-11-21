using System.Runtime.InteropServices;
using System.Windows.Automation;
using Swarm.Core.Capture;
using Swarm.Core.Semantics;
using Swarm.Core.Visual;

namespace Swarm.Desktop;

public sealed record DesktopRebuild(object Message, int ElementCount);

public static class DesktopPayload
{
    /// <summary>Tier B 反解器：GDI 度量 + 候选字体打分，进程内复用度量缓存。</summary>
    private static readonly FontMetricFitter FontFitter = new(new GdiFontMeasurer());

    public static DesktopRebuild Screenshot(CapturedScreen capture)
    {
        var model = ScreenshotTiler.Tile(capture.Frame);
        var scale = capture.DpiScale;
        var elements = model.Tiles.Select(tile => (object)new
        {
            id = $"tile-{tile.X}-{tile.Y}",
            kind = "image",
            x = tile.X / scale,
            y = tile.Y / scale,
            w = tile.Width / scale,
            h = tile.Height / scale,
            src = NativeImage.ToDataUrl(new RgbaFrame(tile.Width, tile.Height, tile.Pixels)),
            semantic = "screenshot",
        }).ToArray();
        return Rebuild(capture, model.Background, elements);
    }

    public static (DesktopRebuild Rebuild, RebuildDecision Decision) Semantic(CapturedScreen capture, UiaSnapshot snapshot)
    {
        var scale = capture.DpiScale;
        var width = capture.Frame.Width / scale;
        var height = capture.Frame.Height / scale;
        var physicalById = snapshot.Elements.ToDictionary(element => element.Id);
        var logical = snapshot.Elements.Select(element =>
        {
            var bounds = CoordinateConverter.ToLogical(element.Bounds, capture.Monitor, scale);
            var colorBounds = RelativeToCapture(element.Bounds, capture.Monitor);
            return new UiaElementInfo
            {
                Id = element.Id,
                ParentId = element.ParentId,
                ControlType = element.ControlType,
                Name = element.Name,
                Value = element.Value,
                Bounds = new RectInt(
                    (int)Math.Round(bounds.X),
                    (int)Math.Round(bounds.Y),
                    Math.Max(1, (int)Math.Round(bounds.Width)),
                    Math.Max(1, (int)Math.Round(bounds.Height))),
                ProcessId = element.ProcessId,
                IsPassword = element.IsPassword,
                HasVisual = element.HasVisual,
                Color = element.ControlType is "Pane" or "Window" or "Group" ? NativeImage.AverageCss(capture.Frame, colorBounds) : element.Color,
                Patterns = element.Patterns,
            };
        }).ToList();
        var mapped = UiaMapper.Map(logical, width, height);
        var decision = RebuildStrategy.Decide(logical, mapped, snapshot.Truncation);
        // 字体信息来自 UIA TextPattern（有预算上限），拿不到的元素再用像素估计兜底。
        var textStyles = UiaTextStyleReader.Read(snapshot);
        var parentById = snapshot.Elements.ToDictionary(item => item.Id, item => item.ParentId, StringComparer.Ordinal);
        var infoById = snapshot.Elements.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var imageIds = mapped.Where(item => item.Kind == "image").Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        // WM_GETFONT 是跨进程调用：同一句柄只查一次，并限制总次数，避免慢 provider 拖住重建。
        var win32Fonts = new Dictionary<nint, ResolvedFont?>();
        var win32Budget = 64;
        // Tier B 每元素要做 20 个候选的 GDI 度量，同样给固定预算，保证整次重建仍是数百毫秒级。
        var fitBudget = 64;
        var elements = new List<object>();
        foreach (var element in mapped)
        {
            // 截图元素本身已经带着文字，再叠一层文本只会重影。
            if (element.Kind == "text" && InsideImage(element.Id, parentById, imageIds)) continue;
            textStyles.TryGetValue(element.Id, out var uia);
            var content = element.Text;
            var edit = element.Semantic is "edit" or "document";
            if (edit)
            {
                // Edit 的 Name 往往只是关联标签（如 “Display name”），不能当正文用；
                // 优先 ValuePattern，其次 TextPattern，都读不到就留空。
                infoById.TryGetValue(element.Id, out var info);
                content = element.Sensitive ? null : info?.Value;
                if (string.IsNullOrEmpty(content) && uia?.Text != null && uia.Text != info?.Name) content = uia.Text;
                if (string.IsNullOrEmpty(content)) continue;
            }

            string? src = null;
            if (element.CropId != null && physicalById.TryGetValue(element.CropId, out var physical))
            {
                var crop = FrameCropper.Crop(capture.Frame, RelativeToCapture(physical.Bounds, capture.Monitor));
                if (crop != null) src = NativeImage.ToDataUrl(crop);
            }

            var region = physicalById.TryGetValue(element.Id, out var own)
                ? RelativeToCapture(own.Bounds, capture.Monitor)
                : default;
            string? css = null;
            string? radius = null;
            string? color = null;
            string? fontFamily = null;
            string? fontWeight = null;
            string? fontStyle = null;
            List<object>? spans = null;
            double? fontSize = null;
            double? lineHeight = null;
            var x = element.X;
            var y = element.Y;
            var w = element.W;
            var h = element.H;
            if (element.Kind == "rect")
            {
                // 只画真正有绘制的表面：底色取主色（不是会被文字拉灰的均值），四边按像素证据加边框。
                var style = RegionStyleAnalyzer.Analyze(capture.Frame, region);
                css = style.ToCss(scale);
                radius = "0px";
                color = style.Background.Css;
            }
            else if (element.Kind == "text")
            {
                var selected = uia?.HasSelection == true;
                // 输入框本身是一块真实绘制过的表面：补一层底色 + 边框，文字再叠在上面。
                RegionStyle? box = null;
                if (edit)
                {
                    // 填充改用内圈薄带，避开正文笔画和选中高亮。
                    var outline = RegionStyleAnalyzer.Analyze(capture.Frame, region);
                    var fill = RegionStyleAnalyzer.SurfaceColor(capture.Frame, region, outline.Background);
                    // 选中态会重绘整个客户区（高亮一直压到控件边缘），边缘像素不再代表控件边框。
                    box = selected ? RegionStyle.Flat(fill) : outline with { Background = fill };
                    elements.Add(new
                    {
                        id = element.Id + ":surface",
                        parent = element.Parent,
                        kind = "rect",
                        x = element.X,
                        y = element.Y,
                        w = element.W,
                        h = element.H,
                        semantic = "surface",
                        css = box.ToCss(scale),
                        radius = "0px",
                        color = box.Background.Css,
                    });
                }
                // 输入框取内圈薄带做底色，避开正文笔画和选中高亮；Label 仍然整块取主色。
                var background = box?.Background ?? RegionStyleAnalyzer.DominantColor(capture.Frame, region);
                var textPadding = box == null
                    ? 0
                    : Math.Max(Math.Max(box.Top.Width, box.Right.Width), Math.Max(box.Bottom.Width, box.Left.Width)) + 2;
                // 选中态的文字是反色画出来的，像素里没有原色；按底色明度选一个可读的正文色。
                color = selected
                    ? RegionStyleAnalyzer.PreferredTextColor(background).Css
                    : uia?.ColorCss ?? RegionStyleAnalyzer.TextColor(capture.Frame, region, background, textPadding).Css;
                // UIA 的 TextPattern 经常拿不全（WinForms 标签只有 Name 没有字体），
                // 再用经典窗口消息读一次控件自己的字体；两者都缺才回落到像素估计。
                ResolvedFont? win32 = null;
                if (uia == null || uia.FontFamily == null || uia.FontSizePoints == null || uia.FontWeight == null || uia.Italic == null)
                {
                    var handle = HandleOf(snapshot, element.Id);
                    if (handle != 0 && !win32Fonts.TryGetValue(handle, out win32) && win32Budget > 0)
                    {
                        win32Budget--;
                        var processId = infoById.TryGetValue(element.Id, out var owner) ? owner.ProcessId : snapshot.ProcessId;
                        win32 = Win32FontReader.TryRead(handle, processId, scale);
                        win32Fonts[handle] = win32;
                    }
                }
                // TextPattern 的 FontSize 单位是磅；只有像素估计需要折回 CSS 像素，默认 14 本身就是 CSS 像素。
                // 两者都拿不到时走 Tier B：用已知文本反解字号/字重/字体族，比纯像素估计更可靠。
                FontFitResult? fit = null;
                if (uia?.FontFamily == null && uia?.FontSizePoints == null && win32 == null && fitBudget > 0)
                {
                    fit = FitFromPixels(capture.Frame, region, background, textPadding, content, scale);
                    if (fit != null) fitBudget--;
                }
                var pixels = uia?.FontSizePoints is double points
                    ? points * 96 / 72
                    : win32?.SizeCss is double cssSize
                        ? cssSize
                        : fit?.SizeCss is double fitted
                            ? fitted
                            : RegionStyleAnalyzer.EstimateFontSize(capture.Frame, region, background, textPadding, content) is int estimate
                                ? estimate / scale
                                : 14;
                fontSize = Math.Clamp(Math.Round(pixels * 2, MidpointRounding.AwayFromZero) / 2, 8, 96);
                lineHeight = Math.Round(fontSize.Value * 1.3, 1, MidpointRounding.AwayFromZero);
                fontFamily = uia?.FontFamily ?? win32?.Family ?? fit?.Family ?? "\"Segoe UI\",\"Microsoft YaHei\",system-ui,sans-serif";
                fontWeight = (uia?.FontWeight ?? win32?.Weight ?? fit?.Weight ?? 400).ToString();
                fontStyle = (uia?.Italic ?? win32?.Italic) == true ? "italic" : null;
                spans = BuildSpans(uia?.Spans, content, fontFamily, fontSize, fontWeight, fontStyle, color);
                // 单行输入框的正文是垂直居中的行盒，不是铺满整个控件矩形。
                if (edit && element.H <= lineHeight.Value * 2.2)
                {
                    const double padding = 3;
                    var line = lineHeight.Value;
                    x += padding;
                    w = Math.Max(1, w - padding * 2);
                    y += Math.Max(0, (element.H - line) / 2);
                    h = Math.Min(element.H, line);
                }
            }

            elements.Add(new
            {
                id = element.Id,
                parent = element.Parent,
                kind = element.Kind,
                x,
                y,
                w,
                h,
                semantic = element.Semantic,
                src,
                text = content,
                css,
                radius,
                color,
                fontSize,
                lineHeight,
                fontFamily,
                fontWeight,
                fontStyle,
                spans,
                sensitive = element.Sensitive,
            });
        }
        return (Rebuild(capture, "rgb(250,250,250)", elements), decision);
    }

    /// <summary>
    /// 混排 span 只有在拼接后与元素正文完全一致时才采用，否则重建会画出与原文不同的文字。
    /// 每段都写成完整的字体样式（包括显式 normal），避免从容器样式错误继承。
    /// </summary>
    private static List<object>? BuildSpans(
        IReadOnlyList<(string Text, SpanStyle Style)>? runs,
        string? content,
        string family,
        double? size,
        string weight,
        string? style,
        string? color)
    {
        if (runs is not { Count: > 1 } || string.IsNullOrEmpty(content)) return null;
        var joined = string.Concat(runs.Select(run => run.Text));
        if (!string.Equals(NormalizeText(joined), NormalizeText(content), StringComparison.Ordinal)) return null;
        return runs.Select(run => (object)new
        {
            text = run.Text,
            fontFamily = run.Style.FontFamily ?? family,
            fontSize = run.Style.FontSizePoints is double points
                ? Math.Clamp(Math.Round(points * 96 / 72 * 2, MidpointRounding.AwayFromZero) / 2, 8, 96)
                : size,
            fontWeight = (run.Style.FontWeight ?? int.Parse(weight)).ToString(),
            fontStyle = (run.Style.Italic ?? style == "italic") ? "italic" : "normal",
            color = run.Style.ColorCss ?? color,
        }).ToList();
    }

    private static string NormalizeText(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>
    /// Tier B 的入口：从截图取该元素真实墨迹的宽、高与笔画宽度，交给度量反解。
    /// 墨迹不足（纯图标、空白占位）时返回 null，让调用方退回像素估计。
    /// </summary>
    private static FontFitResult? FitFromPixels(
        RgbaFrame frame,
        RectInt region,
        Rgb background,
        int padding,
        string? text,
        double scale)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var ink = RegionStyleAnalyzer.InkBounds(frame, region, background, padding);
        if (ink is not RectInt box || box.Width < 4 || box.Height < 4) return null;
        var stroke = RegionStyleAnalyzer.StrokeWidth(frame, box, background);
        return stroke is double width
            ? FontFitter.Fit(new FontFitRequest(text, box.Width, box.Height, width, scale))
            : null;
    }

    private static bool InsideImage(
        string id,
        IReadOnlyDictionary<string, string?> parentById,
        IReadOnlySet<string> imageIds)
    {
        var current = id;
        while (parentById.TryGetValue(current, out var parent) && parent != null)
        {
            if (imageIds.Contains(parent)) return true;
            current = parent;
        }
        return false;
    }

    /// <summary>取元素自己的原生窗口句柄；纯 UIA 元素（无 HWND）返回 0。</summary>
    private static nint HandleOf(UiaSnapshot snapshot, string id)
    {
        if (!snapshot.NativeElements.TryGetValue(id, out var native)) return 0;
        try
        {
            return (nint)native.Current.NativeWindowHandle;
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // 控件已销毁或 provider 失效：读不到句柄就静默降级到像素估计。
            return 0;
        }
    }

    private static DesktopRebuild Rebuild(CapturedScreen capture, string background, IReadOnlyCollection<object> elements) =>
        new(new
        {
            type = "rebuild",
            payload = new
            {
                width = capture.Frame.Width / capture.DpiScale,
                height = capture.Frame.Height / capture.DpiScale,
                background,
                elements,
            },
        }, elements.Count);

    private static RectInt RelativeToCapture(RectInt physical, RectInt monitor) =>
        new(physical.X - monitor.X, physical.Y - monitor.Y, physical.Width, physical.Height);
}
