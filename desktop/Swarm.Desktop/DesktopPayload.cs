using Swarm.Core.Capture;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

public sealed record DesktopRebuild(object Message, int ElementCount);

public static class DesktopPayload
{
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
        var elements = mapped.Select(element =>
        {
            string? src = null;
            if (element.CropId != null && physicalById.TryGetValue(element.CropId, out var physical))
            {
                var crop = FrameCropper.Crop(capture.Frame, RelativeToCapture(physical.Bounds, capture.Monitor));
                if (crop != null) src = NativeImage.ToDataUrl(crop);
            }
            return (object)new
            {
                id = element.Id,
                parent = element.Parent,
                kind = element.Kind,
                x = element.X,
                y = element.Y,
                w = element.W,
                h = element.H,
                semantic = element.Semantic,
                src,
                text = element.Text,
                color = element.Color,
                fontSize = element.FontSize,
                sensitive = element.Sensitive,
            };
        }).ToArray();
        return (Rebuild(capture, "rgb(250,250,250)", elements), decision);
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
