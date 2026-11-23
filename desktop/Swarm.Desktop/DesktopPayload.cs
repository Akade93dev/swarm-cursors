using System.Runtime.InteropServices;
using System.Windows.Automation;
using Swarm.Core.Capture;
using Swarm.Core.Semantics;
using Swarm.Core.Visual;

namespace Swarm.Desktop;

public sealed record DesktopRebuild(object Message, int ElementCount);

public static class DesktopPayload
{
    /// <summary>密码框固定掩码：不读取也不传输真实字符，避免明文进入载荷或落盘。</summary>
    private const string PasswordMask = "••••••••";

    /// <summary>Tier B 反解器：GDI 度量 + 候选字体打分，进程内复用度量缓存。</summary>
    private static readonly FontMetricFitter FontFitter = new(new GdiFontMeasurer());

    /// <summary>
    /// 无 UIA 可用的场景（壁纸、画布网页、游戏窗口）：整屏按原像素一张连续还原，不再切成等大瓦片拼接。
    /// </summary>
    public static DesktopRebuild Screenshot(CapturedScreen capture)
    {
        var viewport = capture.Mapper.ToCssSize(capture.Frame.Width, capture.Frame.Height);
        var background = NativeImage.AverageCss(
            capture.Frame,
            new RectInt(0, 0, capture.Frame.Width, capture.Frame.Height));
        return Rebuild(capture, background, new[] { BaseLayer(viewport.Width, viewport.Height, capture.Frame) });
    }

    public static (DesktopRebuild Rebuild, RebuildDecision Decision) Semantic(
        CapturedScreen capture,
        UiaSnapshot snapshot,
        Ia2Snapshot? ia2 = null)
    {
        var mapper = capture.Mapper;
        var scale = mapper.CssScale;
        var viewport = mapper.ToCssSize(capture.Frame.Width, capture.Frame.Height);
        var width = viewport.Width;
        var height = viewport.Height;
        var physicalById = snapshot.Elements.ToDictionary(element => element.Id);
        var logical = snapshot.Elements.Select(element =>
        {
            var bounds = mapper.ToCss(element.Bounds);
            var colorBounds = RelativeToCapture(element.Bounds, capture.WorkArea);
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
        // 语义不足时立即走整屏实拍：继续编码全部控件与整屏 base64 只会白烧预算，
        // 最后同样被调用方丢弃，还会把原本数百毫秒的重建拖成超时。
        if (decision.IsFallback) return (Screenshot(capture), decision);
        // 框架重建：内容区保留底座原像素，只让窗口边框与外壳控件参与重建，避免整窗填色盖住真实内容。
        var frameOnly = decision.Reason == RebuildFallbackReason.FrameOnly;
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
        // 去拼块：窗口只铺一块连续客户区底色，容器（pane/group）与它同色时不再各自填色。
        var semanticById = mapped.ToDictionary(item => item.Id, item => item.Semantic, StringComparer.Ordinal);
        var windowOf = WindowOwners(mapped, parentById, semanticById);
        var titlebarBottom = TitlebarBottoms(mapped);
        var clientAreas = ClientAreas(mapped, physicalById, titlebarBottom, capture, scale);
        var elements = new List<object>();
        // 同一视觉行的文本片段必须共用行 y：各自按墨迹反推行盒会让单字符/内联片段上下抖动。
        // 只统计普通 Text；输入框走单独的垂直居中分支，不参与行聚类。
        var sharedLines = TextPlacement.SharedLineIds(mapped
            .Where(item => item.Kind == "text" && item.Semantic == "text")
            .Select(item => (item.Id, item.X, item.Y, item.W, item.H))
            .ToList());
        // IA2 逐字符几何已经有最终落位：命中的文本元素整块换成行元素，不再走 UIA 的框宽/墨迹校正。
        var exactLines = ia2 is { Lines.Count: > 0 }
            ? Ia2TextMatcher.Match(mapped, physicalById, ia2.Lines)
                .GroupBy(item => item.Element.Id, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Line).ToList(),
                    StringComparer.Ordinal)
            : new Dictionary<string, List<Ia2Line>>(StringComparer.Ordinal);
        // 实拍底座：壁纸、桌面上的其它客户端与网页按原像素整张还原，避免只铺一层平色被看成“一块块拼”。
        // 被操作的窗口会在它上面整块覆盖并由语义元素重建，因此底座不会和重建文字叠出重影。
        elements.Add(BaseLayer(width, height, capture.Frame));
        foreach (var element in mapped)
        {
            // 截图元素本身已经带着文字，再叠一层文本只会重影。
            if (element.Kind == "text" && InsideImage(element.Id, parentById, imageIds)) continue;
            textStyles.TryGetValue(element.Id, out var uia);
            if (exactLines.TryGetValue(element.Id, out var lines))
            {
                foreach (var line in lines) elements.Add(Ia2Element(capture, element, line, uia, windowOf.ContainsKey(element.Id)));
                continue;
            }
            // 密码框只画固定掩码：UIA 的 ValuePattern/TextPattern 在部分实现里会回读明文，
            // 任何情况下都不能让明文进入载荷、WebView 或落盘 dump。
            var content = element.Sensitive ? PasswordMask : element.Text;
            var edit = element.Semantic is "edit" or "document";
            if (edit)
            {
                // Edit 的 Name 往往只是关联标签（如 “Display name”），不能当正文用；
                // 优先 ValuePattern，其次 TextPattern，都读不到就留空。
                infoById.TryGetValue(element.Id, out var info);
                if (!element.Sensitive)
                {
                    content = info?.Value;
                    if (string.IsNullOrEmpty(content) && uia?.Text != null && uia.Text != info?.Name) content = uia.Text;
                }
                if (string.IsNullOrEmpty(content)) continue;
            }

            string? src = null;
            RectInt? cropBox = null;
            RectInt cropDesired = default;
            if (element.CropId != null && physicalById.TryGetValue(element.CropId, out var physical))
            {
                var desired = RelativeToCapture(physical.Bounds, capture.WorkArea);
                // 跨屏或贴边元素只保留真实可见的裁剪区域，避免把被截断的图拉伸回原尺寸。
                if (FrameCropper.Clamp(capture.Frame, desired) is { } box)
                {
                    var crop = FrameCropper.Crop(capture.Frame, box);
                    // 裁剪图的底色抠成透明，控件叠在连续底面上就不会拖着一条不同色的矩形。
                    if (crop != null)
                    {
                        src = NativeImage.ToDataUrl(crop, ClientColor(element.Id, windowOf, clientAreas));
                        cropBox = box;
                        cropDesired = desired;
                    }
                }
            }
            // 裁剪失败时没有可画的像素，输出 image 只会让渲染层请求一个空 src。
            if (element.Kind == "image" && src == null) continue;

            var region = physicalById.TryGetValue(element.Id, out var own)
                ? RelativeToCapture(own.Bounds, capture.WorkArea)
                : default;
            string? css = null;
            string? radius = null;
            string? color = null;
            string? fontFamily = null;
            string? fontWeight = null;
            string? fontStyle = null;
            List<object>? spans = null;
            string? surfaceColor = null;
            double? fontSize = null;
            double? lineHeight = null;
            var x = element.X;
            var y = element.Y;
            var w = element.W;
            var h = element.H;
            if (cropBox is { } visible)
            {
                // 按真实裁剪区域落位：贴边/跨屏时保持 1:1，只裁掉不可见部分。
                x += (visible.X - cropDesired.X) / scale;
                y += (visible.Y - cropDesired.Y) / scale;
                w = visible.Width / scale;
                h = visible.Height / scale;
            }
            (double Y, double H, Rgb Color)? client = null;
            if (element.Kind == "rect")
            {
                // 只画真正有绘制的表面：底色取主色（不是会被文字拉灰的均值），四边按像素证据加边框。
                var style = RegionStyleAnalyzer.Analyze(capture.Frame, region);
                radius = "0px";
                color = style.Background.Css;
                if (frameOnly)
                {
                    // Chromium/Electron 的内容无法语义化：底色留给底座，只在有真实边框时补一条边。
                    if (!style.HasBorder) continue;
                    css = style.ToCss(scale, includeBackground: false);
                }
                else if (element.Semantic == "window")
                {
                    // 窗口先按原样填底，随后补一块连续客户区，子容器叠在它上面。
                    css = style.ToCss(scale);
                    if (clientAreas.TryGetValue(element.Id, out var area)) client = area;
                }
                else if (element.Semantic is "pane" or "group" && IsClientSurface(element.Id, style.Background, windowOf, clientAreas))
                {
                    // 与客户区同色的容器只是“被切开的一块”：底色交给客户区，只保留真实边框。
                    if (!style.HasBorder) continue;
                    css = style.ToCss(scale, includeBackground: false);
                }
                else
                {
                    css = style.ToCss(scale);
                }
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
                // 透明覆盖层把文字降级为灰度抗锯齿（字形偏重），截图底座的原文也会从下方透出双绘加粗；
                // 给文字一层与所在表面同色的不透明底色，让 Chromium 重新启用 ClearType 并盖住原文。
                // 只在表面色可信时铺（输入框或落在窗口内），壁纸/渐变上不铺以免出现可见色块。
                surfaceColor = TextSurfacePolicy.ShouldPaint(edit, windowOf.ContainsKey(element.Id)) ? background.Css : null;
                var textPadding = box == null
                    ? 0
                    : Math.Max(Math.Max(box.Top.Width, box.Right.Width), Math.Max(box.Bottom.Width, box.Left.Width)) + 2;
                // 单行文本先量一次真实墨迹高度：用来判断 provider 报的“磅值”其实是像素。
                // 多行区域（文档正文）不参与，避免把整段高度当成一行字高。
                RectInt? inkHint = null;
                if (!edit && element.H > 0 && element.H <= 40)
                {
                    var hintReach = (int)Math.Ceiling(element.H * scale * 0.5);
                    var hintTop = Math.Max(0, region.Y - hintReach);
                    var hintBottom = region.Y + region.Height + hintReach;
                    inkHint = RegionStyleAnalyzer.InkBounds(
                        capture.Frame,
                        new RectInt(region.X, hintTop, region.Width, Math.Max(1, hintBottom - hintTop)),
                        background,
                        textPadding);
                }
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
                if (uia?.FontSizePoints is double reported)
                {
                    // 有的 provider 把像素当磅返回：换算后字高超过真实墨迹时按像素处理。
                    // 用墨迹高度而不是控件高度，状态栏里“纯文本”这类带上下留白的项才判断得准。
                    var limit = inkHint is RectInt hint && hint.Height >= TextPlacement.MinInk
                        ? hint.Height / scale
                        : element.H;
                    pixels = TextPlacement.NormalizeFontSize(pixels, reported, limit);
                }
                fontSize = Math.Clamp(Math.Round(pixels * 2, MidpointRounding.AwayFromZero) / 2, 8, 96);
                lineHeight = Math.Round(fontSize.Value * 1.3, 1, MidpointRounding.AwayFromZero);
                fontFamily = uia?.FontFamily ?? win32?.Family ?? fit?.Family ?? "\"Segoe UI\",\"Microsoft YaHei\",system-ui,sans-serif";
                var weight = uia?.FontWeight ?? win32?.Weight ?? fit?.Weight;
                if (weight == null)
                {
                    // 主路径也可能只有字体族和字号而缺字重；沿用 IA2 的像素分类，避免一律回退 400。
                    // 已有 provider 或 Fit 字重时不覆盖，只有全部缺失才增加这次像素与 GDI 度量。
                    weight = FontFitter.ClassifyWeightFromInk(
                        capture.Frame,
                        region,
                        background,
                        textPadding,
                        content,
                        uia?.FontFamily ?? win32?.Family ?? fit?.Family ?? "Segoe UI",
                        fontSize.Value,
                        scale);
                }
                fontWeight = (weight ?? 400).ToString();
                fontStyle = (uia?.Italic ?? win32?.Italic) == true ? "italic" : null;
                // 单行控件（状态栏、标签）里的换行只代表分段：折叠成空格，否则 pre-wrap 会排成两行并被裁掉。
                if (!edit && element.H <= lineHeight.Value * 1.8) content = TextPlacement.CollapseLine(content);
                spans = BuildSpans(element.Sensitive ? null : uia?.Spans, content, fontFamily, fontSize, fontWeight, fontStyle, color);
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
                else
                {
                    // UIA 的元素矩形通常含内边距，直接左对齐会让文字整体偏左偏上；按像素墨迹校正落位。
                    // 多行区域只探测顶部两行：整块扫描会把滚动条、边框等暗像素也当成文字。
                    // 向上多探半行：控件矩形上缘常切掉笔画顶部，探测区不扩会量到偏低的墨迹。
                    var reach = (int)Math.Ceiling(fontSize.Value * scale * 0.4);
                    var probeTop = Math.Max(0, region.Y - reach);
                    var probeBottom = region.Y + Math.Min(
                        region.Height,
                        (int)Math.Ceiling(lineHeight.Value * scale * 2.5));
                    var probe = new RectInt(
                        region.X,
                        probeTop,
                        region.Width,
                        Math.Max(1, probeBottom - probeTop));
                    var ink = RegionStyleAnalyzer.InkBounds(capture.Frame, probe, background, textPadding);
                    var row = LineOf(uia, physicalById.TryGetValue(element.Id, out var ownInfo) ? ownInfo : null);
                    var placed = TextPlacement.Align(
                        ink,
                        row is RectInt absolute ? RelativeToCapture(absolute, capture.WorkArea) : null,
                        new PlacedText(x, y, w, h),
                        scale,
                        lineHeight.Value,
                        fontSize.Value,
                        useInkY: !sharedLines.Contains(element.Id));
                    x = placed.X;
                    y = placed.Y;
                    w = placed.W;
                    h = placed.H;
                    // 单行文本的最终宽度由渲染层按真实字体实测后收窄/补足，这里不再用字符数粗估，
                    // 避免浅灰无墨迹文本被撑宽后侵入同一行的下一个片段。
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
                surfaceColor,
                sensitive = element.Sensitive,
            });
            if (client is { } clientArea)
            {
                // 客户区是整窗唯一的一块连续底色；子容器不再重复填色，画面才不会被切成碎块。
                elements.Add(new
                {
                    id = element.Id + ":client",
                    parent = element.Id,
                    kind = "rect",
                    x = element.X,
                    y = clientArea.Y,
                    w = element.W,
                    h = clientArea.H,
                    semantic = "surface",
                    css = $"background-color:{clientArea.Color.Css};",
                    radius = "0px",
                    color = clientArea.Color.Css,
                });
            }
        }
        return (Rebuild(capture, "rgb(250,250,250)", elements), decision);
    }

    /// <summary>
    /// 容器与窗口客户区的色差在 8 个色阶/通道以内时视为同一块表面：各自填色会把整窗切成近似等大的碎块。
    /// 距离按 L1（三通道差之和）计，24 即每通道 8 级；明显更浅/更深的卡片（如白底描边卡片）仍保留自己的底色。
    /// </summary>
    private const double ClientSurfaceTolerance = 24;

    private static bool IsClientSurface(
        string id,
        Rgb background,
        IReadOnlyDictionary<string, string> windowOf,
        IReadOnlyDictionary<string, (double Y, double H, Rgb Color)> clientAreas) =>
        windowOf.TryGetValue(id, out var windowId)
        && clientAreas.TryGetValue(windowId, out var area)
        && background.Distance(area.Color) <= ClientSurfaceTolerance;

    /// <summary>元素所属窗口的客户区底色；没有窗口归属时返回 null，裁剪图保持原样。</summary>
    private static Rgb? ClientColor(
        string id,
        IReadOnlyDictionary<string, string> windowOf,
        IReadOnlyDictionary<string, (double Y, double H, Rgb Color)> clientAreas) =>
        windowOf.TryGetValue(id, out var windowId) && clientAreas.TryGetValue(windowId, out var area)
            ? area.Color
            : null;

    /// <summary>每个元素所属的窗口：沿父链找最近的 window 语义元素。</summary>
    private static Dictionary<string, string> WindowOwners(
        IReadOnlyList<DesktopElement> mapped,
        IReadOnlyDictionary<string, string?> parentById,
        IReadOnlyDictionary<string, string> semanticById)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in mapped)
        {
            var current = item.Id;
            for (var depth = 0; depth < 32; depth++)
            {
                if (semanticById.TryGetValue(current, out var semantic) && semantic == "window")
                {
                    owners[item.Id] = current;
                    break;
                }
                if (!parentById.TryGetValue(current, out var parent) || string.IsNullOrEmpty(parent)) break;
                current = parent;
            }
        }
        return owners;
    }

    /// <summary>标题栏底边（CSS 像素）：客户区从它下面开始。</summary>
    private static Dictionary<string, double> TitlebarBottoms(IReadOnlyList<DesktopElement> mapped)
    {
        var bottoms = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in mapped)
        {
            if (item.Semantic != "titlebar" || string.IsNullOrEmpty(item.Parent)) continue;
            var bottom = item.Y + item.H;
            bottoms[item.Parent] = bottoms.TryGetValue(item.Parent, out var known) ? Math.Max(known, bottom) : bottom;
        }
        return bottoms;
    }

    /// <summary>窗口内、标题栏以下的连续表面：取主色作为该窗口唯一的客户区底色。</summary>
    private static Dictionary<string, (double Y, double H, Rgb Color)> ClientAreas(
        IReadOnlyList<DesktopElement> mapped,
        IReadOnlyDictionary<string, UiaElementInfo> physicalById,
        IReadOnlyDictionary<string, double> titlebarBottoms,
        CapturedScreen capture,
        double scale)
    {
        var areas = new Dictionary<string, (double Y, double H, Rgb Color)>(StringComparer.Ordinal);
        foreach (var item in mapped)
        {
            if (item.Semantic != "window" || !physicalById.TryGetValue(item.Id, out var physical)) continue;
            var region = RelativeToCapture(physical.Bounds, capture.WorkArea);
            if (region.Width <= 0 || region.Height <= 0) continue;
            var top = titlebarBottoms.TryGetValue(item.Id, out var bottom) ? Math.Max(bottom, item.Y) : item.Y;
            var height = item.Y + item.H - top;
            if (height <= 4) continue;
            var outline = RegionStyleAnalyzer.Analyze(capture.Frame, region);
            var inset = Math.Clamp((int)Math.Round((top - item.Y) * scale), 0, region.Height);
            var client = new RectInt(region.X, region.Y + inset, region.Width, region.Height - inset);
            areas[item.Id] = (top, height, RegionStyleAnalyzer.DominantColor(capture.Frame, client, outline.Background));
        }
        return areas;
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
            // span 内部的控制字符要一并规范化，否则渲染层会真的换行，把单行状态栏裁成两行。
            text = NormalizeText(run.Text),
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
    /// IA2 行元素：落位与宽度来自逐字符矩形，样式取自 attributes()。
    /// 标成 exact 后渲染层不再按右邻夹紧、也不再并入合并行，避免二次拟合破坏字符间距。
    /// </summary>
    private static object Ia2Element(CapturedScreen capture, DesktopElement element, Ia2Line line, UiaTextStyle? uia, bool hasWindowOwner)
    {
        var scale = capture.Mapper.CssScale;
        var box = RelativeToCapture(line.Bounds, capture.WorkArea);
        var style = line.Style;
        var pixels = style?.FontSizePx is double size && size > 0 ? size : 14;
        var fontSize = Math.Clamp(Math.Round(pixels * 2, MidpointRounding.AwayFromZero) / 2, 8, 96);
        // IA2 直接给出该行文字的真实背景色：与底座截图同色，铺上既能让 Chromium 重新启用 ClearType，
        // 又能盖住底座里的原文消除双绘加粗，且不会留下可见色块。IA2 未给背景色时回退像素主色，
        // 并复用与主路径一致的铺色策略，避免在壁纸/渐变上铺出错色块。
        var background = ParseRgbCss(style?.BackgroundCss);
        string? surfaceColor = style?.BackgroundCss;
        if (surfaceColor == null || background == null)
        {
            var dominant = RegionStyleAnalyzer.DominantColor(capture.Frame, box);
            if (surfaceColor == null)
                surfaceColor = TextSurfacePolicy.ShouldPaint(isEdit: false, hasWindowOwner) ? dominant.Css : null;
            background ??= dominant;
        }
        // Chromium 的 IA2 attributes() 不返回 font-weight（style.Bold 恒为 null），导致所有 IA2 文字被画成 400；
        // 用截图像素笔画宽度 + GDI 真实 x-height 归一化成笔画比，选最接近的 Light/Regular/Bold 先验。
        // 不依赖 fit 的字号反解（中文长文本宽度拟合常失败），只判字重；量不到或误差过大时回退 400。
        int? classified = null;
        if (style?.Bold == null && background is Rgb bg && box.Width >= 4 && box.Height >= 4)
        {
            var ink = RegionStyleAnalyzer.InkBounds(capture.Frame, box, bg, 0);
            if (ink is RectInt ib && ib.Width >= 4 && ib.Height >= 4)
            {
                var stroke = RegionStyleAnalyzer.StrokeWidth(capture.Frame, ib, bg);
                if (stroke is double s && s > 0)
                {
                    classified = FontFitter.ClassifyWeightFromInk(line.Text, style?.FontFamily ?? "Segoe UI", s, pixels, scale);
                }
            }
        }
        var weight = style?.Bold == true ? 700 : classified ?? uia?.FontWeight ?? 400;
        return new
        {
            id = $"{element.Id}:ia2:{box.X}:{box.Y}",
            parent = element.Parent,
            kind = "text",
            x = box.X / scale,
            y = box.Y / scale,
            w = box.Width / scale,
            h = box.Height / scale,
            semantic = element.Semantic,
            text = line.Text,
            color = style?.ColorCss ?? uia?.ColorCss ?? "rgb(32,32,32)",
            fontSize,
            lineHeight = Math.Round(fontSize * 1.3, 1, MidpointRounding.AwayFromZero),
            fontFamily = style?.FontFamily ?? uia?.FontFamily ?? "\"Segoe UI\",\"Microsoft YaHei\",system-ui,sans-serif",
            fontWeight = weight.ToString(),
            fontStyle = (style?.Italic ?? uia?.Italic) == true ? "italic" : null,
            surfaceColor,
            exact = true,
        };
    }

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

    /// <summary>解析 IA2 attributes() 给的 "rgb(r,g,b)" 背景色，失败返回 null。</summary>
    private static Rgb? ParseRgbCss(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return null;
        var open = css.IndexOf('(');
        var close = css.IndexOf(')');
        if (open < 0 || close < 0 || close < open) return null;
        var inner = css.Substring(open + 1, close - open - 1);
        var parts = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        if (!byte.TryParse(parts[0], out var r) || !byte.TryParse(parts[1], out var g) || !byte.TryParse(parts[2], out var b)) return null;
        return new Rgb(r, g, b);
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

    /// <summary>
    /// 选取与该元素框相交的行矩形。多行 provider 会给每行一个矩形，取垂直重叠最多的一行；
    /// 重叠相同时保留阅读顺序里的第一行（元素顶部那一行）。
    /// </summary>
    private static RectInt? LineOf(UiaTextStyle? uia, UiaElementInfo? physical)
    {
        if (uia?.Lines is not { Count: > 0 } lines || physical == null) return null;
        var box = physical.Bounds;
        RectInt? best = null;
        var bestOverlap = 0;
        foreach (var line in lines)
        {
            if (line.Width <= 0 || line.Height <= 0) continue;
            var vertical = Math.Min(box.Bottom, line.Bottom) - Math.Max(box.Y, line.Y);
            if (vertical <= bestOverlap) continue;
            var horizontal = Math.Min(box.Right, line.Right) - Math.Max(box.X, line.X);
            if (horizontal <= 0) continue;
            bestOverlap = vertical;
            best = line;
        }
        return best;
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

    /// <summary>整屏实拍底座：壁纸、网页、应用界面都按原像素一张连续还原，排在所有重建元素之下。</summary>
    private static object BaseLayer(double width, double height, RgbaFrame frame) => new
    {
        id = "desktop",
        kind = "image",
        x = 0d,
        y = 0d,
        w = width,
        h = height,
        src = NativeImage.ToDataUrl(frame),
        semantic = "desktop-base",
    };

    private static DesktopRebuild Rebuild(CapturedScreen capture, string background, IReadOnlyCollection<object> elements)
    {
        var viewport = capture.Mapper.ToCssSize(capture.Frame.Width, capture.Frame.Height);
        return new(new
        {
            type = "rebuild",
            payload = new
            {
                width = viewport.Width,
                height = viewport.Height,
                background,
                elements,
            },
        }, elements.Count);
    }

    private static RectInt RelativeToCapture(RectInt physical, RectInt origin) =>
        new(physical.X - origin.X, physical.Y - origin.Y, physical.Width, physical.Height);
}
