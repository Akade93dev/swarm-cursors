namespace Swarm.Core.Semantics;

public readonly record struct RectInt(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Intersects(RectInt other) => Width > 0 && Height > 0 && X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}

public sealed class UiaElementInfo
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string ControlType { get; init; }
    public string? Name { get; init; }
    public string? Value { get; init; }
    public RectInt Bounds { get; init; }
    public int ProcessId { get; init; }
    public bool IsPassword { get; init; }
    public bool HasVisual { get; init; }
    public string? Color { get; init; }
    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();
}

public sealed class DesktopElement
{
    public required string Id { get; init; }
    public string? Parent { get; init; }
    public required string Kind { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public string Semantic { get; init; } = "";
    public string? CropId { get; init; }
    public string? Src { get; set; }
    public string? Text { get; init; }
    public string? Color { get; init; }
    public double FontSize { get; init; } = 14;
    public int ProcessId { get; init; }
    public bool Sensitive { get; init; }
}

public static class UiaMapper
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "CheckBox", "RadioButton", "Hyperlink", "MenuItem", "TabItem", "ListItem", "ComboBox", "Slider", "Image", "TitleBar", "ScrollBar",
    };

    public static IReadOnlyList<DesktopElement> Map(IEnumerable<UiaElementInfo> elements, double viewportWidth, double viewportHeight)
    {
        var viewport = new RectInt(0, 0, (int)Math.Ceiling(viewportWidth), (int)Math.Ceiling(viewportHeight));
        var included = new HashSet<string>();
        var result = new List<DesktopElement>();
        foreach (var element in elements)
        {
            if (!element.Bounds.Intersects(viewport)) continue;
            var text = element.Value ?? element.Name;
            var isText = element.ControlType.Equals("Text", StringComparison.OrdinalIgnoreCase)
                || element.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                || element.ControlType.Equals("Document", StringComparison.OrdinalIgnoreCase);
            DesktopElement? mapped = null;
            if (isText && !string.IsNullOrWhiteSpace(text))
            {
                mapped = new DesktopElement
                {
                    Id = element.Id,
                    Kind = "text",
                    Text = element.IsPassword ? "••••••••" : text,
                    Sensitive = element.IsPassword,
                };
            }
            else if (ImageTypes.Contains(element.ControlType) && element.HasVisual)
            {
                mapped = new DesktopElement { Id = element.Id, Kind = "image", CropId = element.Id };
            }
            else if ((element.ControlType.Equals("Pane", StringComparison.OrdinalIgnoreCase) || element.ControlType.Equals("Window", StringComparison.OrdinalIgnoreCase) || element.ControlType.Equals("Group", StringComparison.OrdinalIgnoreCase)) && element.HasVisual)
            {
                mapped = new DesktopElement { Id = element.Id, Kind = "rect", Color = element.Color ?? "rgb(240,240,240)" };
            }
            if (mapped == null) continue;
            var item = new DesktopElement
            {
                Id = mapped.Id,
                Parent = element.ParentId != null && included.Contains(element.ParentId) ? element.ParentId : null,
                Kind = mapped.Kind,
                X = element.Bounds.X,
                Y = element.Bounds.Y,
                W = element.Bounds.Width,
                H = element.Bounds.Height,
                Semantic = element.ControlType.ToLowerInvariant(),
                CropId = mapped.CropId,
                Text = mapped.Text,
                Color = mapped.Color,
                ProcessId = element.ProcessId,
                Sensitive = mapped.Sensitive,
            };
            included.Add(item.Id);
            result.Add(item);
        }
        return result;
    }
}
