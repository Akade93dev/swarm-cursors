using System.Runtime.InteropServices;

namespace Swarm.Desktop;

/// <summary>
/// 自建的最小 IUIAutomation COM 客户端，只做两件事：把 <c>AutoSetFocus</c> 真正关掉，
/// 以及通过它执行 UIA 模式动作。托管 UIA 客户端不暴露该属性，所以动作阶梯里
/// "不抢焦点"以前只能靠度量 + 恢复；这里用同一份实例执行模式，提供程序就不会顺手 SetFocus。
/// 槽位来自 Windows SDK 的 uiautomationclient.h：IUnknown 占 0-2，IUIAutomation 方法从 3 开始，
/// IUIAutomation2 的 AutoSetFocus 位于 58/59。
/// </summary>
internal sealed class NativeUiaSession : IDisposable
{
    private static readonly Guid ClsidCUIAutomation8 = new("E22AD333-B25F-460C-83D0-0581107395C9");
    private static readonly Guid IidIUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");

    private const int ClsCtxInProcServer = 1;
    private const int PatternInvoke = 10000;
    private const int PatternValue = 10002;
    private const int PatternExpandCollapse = 10005;
    private const int PatternSelectionItem = 10010;
    private const int PatternToggle = 10015;

    private const int ExpandCollapseStateExpanded = 1;

    private nint _automation;

    private NativeUiaSession(nint automation) => _automation = automation;

    /// <summary>创建实例并关闭 AutoSetFocus；读回失败时不返回实例，调用方继续走托管路径。</summary>
    public static NativeUiaSession? TryCreate()
    {
        var clsid = ClsidCUIAutomation8;
        var iid = IidIUIAutomation;
        var hr = CoCreateInstance(ref clsid, 0, ClsCtxInProcServer, ref iid, out var automation);
        if (hr < 0 || automation == 0) return null;

        var session = new NativeUiaSession(automation);
        try
        {
            var set = session.CallIn(59, 0);
            var get = session.CallOut(58, out var value);
            if (set < 0 || get < 0 || value != 0)
            {
                session.Dispose();
                return null;
            }
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>从屏幕点命中的元素向上找与托管元素 runtimeId 相同的原生元素；找不到返回 0。</summary>
    public nint ResolveFromPoint(int x, int y, IReadOnlyList<int> runtimeId)
    {
        var hr = Fn<FnPoint>(_automation, 7)(_automation, new Point { X = x, Y = y }, out var current);
        if (hr < 0 || current == 0) return 0;

        var walkerHr = Fn<FnOutPtr>(_automation, 14)(_automation, out var walker);
        if (walkerHr < 0 || walker == 0)
        {
            Release(current);
            return 0;
        }

        try
        {
            for (var depth = 0; depth < 12 && current != 0; depth++)
            {
                if (Matches(current, runtimeId)) return current;
                var parentHr = Fn<FnOutPtrWithInput>(walker, 3)(walker, current, out var parent);
                Release(current);
                current = parentHr >= 0 ? parent : 0;
            }
            if (current != 0) Release(current);
            return 0;
        }
        finally
        {
            Release(walker);
        }
    }

    /// <summary>按与托管阶梯相同的优先级尝试 Invoke → Toggle → SelectionItem → ExpandCollapse。</summary>
    public bool TryClick(nint element)
    {
        if (TryGetPattern(element, PatternInvoke, out var invoke))
        {
            try { return Fn<Fn0>(invoke, 3)(invoke) >= 0; }
            finally { Release(invoke); }
        }
        if (TryGetPattern(element, PatternToggle, out var toggle))
        {
            try { return Fn<Fn0>(toggle, 3)(toggle) >= 0; }
            finally { Release(toggle); }
        }
        if (TryGetPattern(element, PatternSelectionItem, out var selection))
        {
            try { return Fn<Fn0>(selection, 3)(selection) >= 0; }
            finally { Release(selection); }
        }
        if (TryGetPattern(element, PatternExpandCollapse, out var expand))
        {
            try
            {
                var stateHr = Fn<FnOutInt>(expand, 5)(expand, out var state);
                if (stateHr < 0) return false;
                return Fn<Fn0>(expand, state == ExpandCollapseStateExpanded ? 4 : 3)(expand) >= 0;
            }
            finally { Release(expand); }
        }
        return false;
    }

    /// <summary>原生 ValuePattern.SetValue；只读或提供程序拒绝时返回 false，交给托管路径报 read_only。</summary>
    public bool TrySetValue(nint element, string text)
    {
        if (!TryGetPattern(element, PatternValue, out var value)) return false;
        try
        {
            var readOnlyHr = Fn<FnOutInt>(value, 5)(value, out var readOnly);
            if (readOnlyHr < 0 || readOnly != 0) return false;
            var bstr = Marshal.StringToBSTR(text);
            try
            {
                return Fn<FnPtrIn>(value, 3)(value, bstr) >= 0;
            }
            finally
            {
                Marshal.FreeBSTR(bstr);
            }
        }
        finally
        {
            Release(value);
        }
    }

    public void Release(nint instance)
    {
        if (instance != 0) Marshal.Release(instance);
    }

    public void Dispose()
    {
        var automation = _automation;
        _automation = 0;
        if (automation != 0) Marshal.Release(automation);
    }

    private bool TryGetPattern(nint element, int patternId, out nint pattern)
    {
        var hr = Fn<FnOutIntPtrPair>(element, 16)(element, patternId, out pattern);
        if (hr < 0)
        {
            pattern = 0;
            return false;
        }
        return pattern != 0;
    }

    private static bool Matches(nint element, IReadOnlyList<int> runtimeId)
    {
        var hr = Fn<FnOutPtr>(element, 4)(element, out var safeArray);
        if (hr < 0 || safeArray == 0) return false;
        try
        {
            var lowerHr = SafeArrayGetLBound(safeArray, 1, out var lower);
            var upperHr = SafeArrayGetUBound(safeArray, 1, out var upper);
            if (lowerHr < 0 || upperHr < 0 || upper - lower + 1 != runtimeId.Count) return false;
            for (var i = lower; i <= upper; i++)
            {
                var index = i;
                if (SafeArrayGetElement(safeArray, ref index, out var value) < 0) return false;
                if (value != runtimeId[i - lower]) return false;
            }
            return true;
        }
        finally
        {
            SafeArrayDestroy(safeArray);
        }
    }

    private int CallIn(int slot, int value) => Fn<FnInInt>(_automation, slot)(_automation, value);

    private int CallOut(int slot, out int value) => Fn<FnOutInt>(_automation, slot)(_automation, out value);

    private static nint Slot(nint instance, int index) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance) + index * IntPtr.Size);

    private static T Fn<T>(nint instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Slot(instance, index));

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, int context, ref Guid iid, out nint instance);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(nint array, uint dimension, out int lower);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(nint array, uint dimension, out int upper);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetElement(nint array, ref int index, out int value);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint array);

    private delegate int Fn0(nint self);
    private delegate int FnInInt(nint self, int value);
    private delegate int FnOutInt(nint self, out int value);
    private delegate int FnPtrIn(nint self, nint value);
    private delegate int FnOutPtr(nint self, out nint value);
    private delegate int FnOutPtrWithInput(nint self, nint input, out nint value);
    private delegate int FnOutIntPtrPair(nint self, int value, out nint output);
    private delegate int FnPoint(nint self, Point point, out nint element);
}
