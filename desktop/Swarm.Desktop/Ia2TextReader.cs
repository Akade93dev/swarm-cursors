using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Accessibility;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

public sealed record Ia2ReadOptions(
    int TimeoutMilliseconds = 1200,
    int MaxNodes = 900,
    int MaxGlyphs = 5000,
    int MaxTextLength = 4000,
    int MaxDepth = 40);

public sealed record Ia2Snapshot(
    IReadOnlyList<Ia2Line> Lines,
    int Visited,
    int TextNodes,
    int Glyphs,
    long ElapsedMilliseconds,
    bool TimedOut)
{
    public static readonly Ia2Snapshot Empty = new(Array.Empty<Ia2Line>(), 0, 0, 0, 0, false);
}

/// <summary>
/// 通过 IAccessible2 读取 Chromium/Electron 的逐字符屏幕矩形。
/// 只在独立的 STA 线程上工作，超时或 provider 不支持时返回空结果，由调用方回退到 UIA。
/// </summary>
public static class Ia2TextReader
{
    private const int RoleStaticText = 41;
    private const int RoleText = 42;
    private const int RoleLink = 30;
    private const int ChildIdSelf = 0;
    private const int StateSystemInvisible = 0x8000;
    private const int StateSystemOffscreen = 0x10000;
    private const int CoordinateScreenRelative = 0;
    private const uint ObjidClient = 0xFFFFFFFC;
    private static readonly Guid IidAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private static readonly Guid IidAccessibleText = new("24FD2FFB-3AAD-4a08-8335-A3AD89C0FB4B");

    public static Ia2Snapshot Read(nint window, RectInt clip, Ia2ReadOptions? options = null)
    {
        options ??= new Ia2ReadOptions();
        if (window == 0 || clip.Width <= 0 || clip.Height <= 0) return Ia2Snapshot.Empty;
        var snapshot = Ia2Snapshot.Empty;
        var thread = new Thread(() => snapshot = ReadCore(window, clip, options))
        {
            IsBackground = true,
            Name = "swarm-ia2-reader",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // 读取器内部已按预算提前返回；这里再等一小段，避免异常 provider 把整个重建拖住。
        if (!thread.Join(options.TimeoutMilliseconds + 120)) return Ia2Snapshot.Empty with { TimedOut = true };
        return snapshot;
    }

    private static Ia2Snapshot ReadCore(nint window, RectInt clip, Ia2ReadOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        IAccessible? root;
        try
        {
            var iid = IidAccessible;
            if (AccessibleObjectFromWindow(window, ObjidClient, ref iid, out var accessible) != 0) return Ia2Snapshot.Empty;
            root = accessible as IAccessible;
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return Ia2Snapshot.Empty;
        }
        if (root == null) return Ia2Snapshot.Empty;

        var lines = new List<Ia2Line>();
        var visited = 0;
        var textNodes = 0;
        var glyphCount = 0;
        var timedOut = false;
        var clipExpanded = new RectInt(clip.X - 2, clip.Y - 2, clip.Width + 4, clip.Height + 4);
        var pending = new Queue<PendingNode>();
        pending.Enqueue(new PendingNode(root, ChildIdSelf, 0));
        while (pending.Count > 0)
        {
            if (visited >= options.MaxNodes)
            {
                timedOut = true;
                break;
            }
            if (stopwatch.ElapsedMilliseconds > options.TimeoutMilliseconds)
            {
                timedOut = true;
                break;
            }
            var node = pending.Dequeue();
            visited++;
            try
            {
                var role = ToInt(node.Accessible.get_accRole(node.ChildId));
                var state = ToInt(node.Accessible.get_accState(node.ChildId));
                node.Accessible.accLocation(out var x, out var y, out var width, out var height, node.ChildId);
                var visible = (state & (StateSystemInvisible | StateSystemOffscreen)) == 0;
                var box = new RectInt(x, y, width, height);
                var hasBox = width > 0 && height > 0;
                // 有矩形但完全在捕获区外：整棵子树都不用再走（IA2 树里常混有离屏节点）。
                var outside = hasBox && !box.Intersects(clipExpanded);
                if (visible && !outside && IsTextRole(role))
                {
                    var name = node.Accessible.get_accName(node.ChildId) as string;
                    if (!string.IsNullOrEmpty(name) && name.Length <= options.MaxTextLength && TextOf(node.Accessible) is { } text)
                    {
                        textNodes++;
                        var glyphs = ReadGlyphs(text, name, options, stopwatch, ref glyphCount, ref timedOut);
                        if (glyphs.Count > 0)
                        {
                            var style = ReadStyle(text);
                            lines.AddRange(Ia2LineBuilder.Build(new Ia2TextNode(role, name, glyphs, style), clip));
                        }
                    }
                }
                if (outside || node.Depth >= options.MaxDepth) continue;
                int children;
                try
                {
                    children = node.Accessible.accChildCount;
                }
                catch (Exception error) when (error is COMException or InvalidOperationException)
                {
                    children = 0;
                }
                for (var index = 1; index <= children; index++)
                {
                    if (visited + pending.Count >= options.MaxNodes) break;
                    try
                    {
                        var child = node.Accessible.get_accChild(index);
                        pending.Enqueue(child is IAccessible nested
                            ? new PendingNode(nested, ChildIdSelf, node.Depth + 1)
                            : new PendingNode(node.Accessible, index, node.Depth + 1));
                    }
                    catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
                    {
                        // 个别子节点失效不影响同级的其它节点。
                    }
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                // 单个节点失败就跳过，整棵树的读取继续。
            }
        }
        return new Ia2Snapshot(
            Ia2LineBuilder.Deduplicate(lines),
            visited,
            textNodes,
            glyphCount,
            stopwatch.ElapsedMilliseconds,
            timedOut);
    }

    private static bool IsTextRole(int role) => role is RoleStaticText or RoleText or RoleLink;

    private static List<Ia2Glyph> ReadGlyphs(
        IAccessibleText text,
        string name,
        Ia2ReadOptions options,
        Stopwatch stopwatch,
        ref int glyphCount,
        ref bool timedOut)
    {
        var glyphs = new List<Ia2Glyph>(Math.Min(name.Length, 256));
        for (var offset = 0; offset < name.Length; offset++)
        {
            // 空白字符的推进已经体现在相邻字符的 x 上，行矩形也会被 trim，取它只会多一次跨进程调用。
            if (char.IsWhiteSpace(name[offset]) || char.IsControl(name[offset])) continue;
            // 每 64 个字符检查一次预算：跨进程 COM 调用是主要耗时，不能只在节点之间检查。
            if ((offset & 63) == 0 && stopwatch.ElapsedMilliseconds > options.TimeoutMilliseconds)
            {
                timedOut = true;
                break;
            }
            if (glyphCount >= options.MaxGlyphs) break;
            try
            {
                if (text.get_characterExtents(offset, CoordinateScreenRelative, out var x, out var y, out var width, out var height) != 0) continue;
                if (width <= 0 || height <= 0) continue;
                glyphs.Add(new Ia2Glyph(offset, new RectInt(x, y, width, height)));
                glyphCount++;
            }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                // 个别字符没有几何（软换行、零宽字符）是正常情况。
            }
        }
        return glyphs;
    }

    private static Ia2TextStyle? ReadStyle(IAccessibleText text)
    {
        try
        {
            if (text.get_attributes(0, out _, out _, out var attributes) != 0) return null;
            return Ia2TextAttributes.Parse(attributes);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IAccessibleText? TextOf(IAccessible accessible)
    {
        // Chromium 不响应直接 QI；必须经 IServiceProvider 用服务 IID_IAccessible 请求文本接口。
        if (accessible is not IServiceProvider provider) return null;
        var service = IidAccessible;
        var iid = IidAccessibleText;
        try
        {
            return provider.QueryService(ref service, ref iid, out var text) == 0 ? text : null;
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static int ToInt(object? value)
    {
        try
        {
            return value == null ? -1 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException)
        {
            return -1;
        }
    }

    private sealed record PendingNode(IAccessible Accessible, object ChildId, int Depth);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        nint hwnd,
        uint dwId,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IAccessibleText ppvObject);
    }

    /// <summary>
    /// IA2 的 IAccessibleText。vtable 顺序固定：IUnknown 之后依次是 addSelection、attributes、
    /// caretOffset、characterExtents，不能按方法名猜顺序。
    /// </summary>
    [ComImport]
    [Guid("24FD2FFB-3AAD-4a08-8335-A3AD89C0FB4B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAccessibleText
    {
        [PreserveSig]
        int addSelection(int startOffset, int endOffset);

        [PreserveSig]
        int get_attributes(int offset, out int startOffset, out int endOffset, [MarshalAs(UnmanagedType.BStr)] out string textAttributes);

        [PreserveSig]
        int get_caretOffset(out int offset);

        [PreserveSig]
        int get_characterExtents(int offset, int coordType, out int x, out int y, out int width, out int height);
    }
}
