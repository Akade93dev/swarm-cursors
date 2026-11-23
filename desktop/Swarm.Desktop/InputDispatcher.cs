using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Swarm.Core.Operations;
using Swarm.Core.Semantics;

namespace Swarm.Desktop;

public sealed record ActionOutcome(
    InputTier Tier,
    bool Verified,
    string? Note = null,
    string? Evidence = null,
    bool ForegroundUnchanged = true,
    bool ForegroundRestored = false);

/// <summary>
/// 分级输入：
/// A 级优先走 UIA 模式，可验证且不移动鼠标；
/// B 级向目标子窗口定向 PostMessage，不激活窗口、不动光标，适合被遮挡时操作，但部分应用会忽略消息；
/// C 级仅在目标已经是前台时使用真实输入，避免抢焦点；否则返回 needs_foreground。
/// </summary>
public static class InputDispatcher
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmClick = 0x00F5;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int GwlStyle = -16;
    private const int EsPassword = 0x0020;
    private const uint MkLButton = 0x0001;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const uint MouseEventAbsolute = 0x8000;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint CwpSkipInvisible = 0x0001;
    private const uint CwpSkipDisabled = 0x0002;
    private const uint CwpSkipTransparent = 0x0004;
    private const uint GaRoot = 2;

    public static async Task<ActionOutcome> ClickAsync(
        nint window,
        UiaSnapshot? snapshot,
        string? elementId,
        int screenX,
        int screenY,
        bool allowForegroundInput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (elementId != null && snapshot != null && !UiaWatchdog.IsWindowQuarantined(window) &&
            snapshot.NativeElements.TryGetValue(elementId, out var element))
        {
            var foregroundBefore = NativeMethods.GetForegroundWindow();
            try
            {
                if (await Task.Run(() => TryNativeClick(element), cancellationToken))
                    return UiaOutcome(window, foregroundBefore, "已用原生 UIA（AutoSetFocus=false）");
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                {
                    await Task.Run(() => ((InvokePattern)invoke).Invoke(), cancellationToken);
                    return UiaOutcome(window, foregroundBefore, null);
                }
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
                {
                    await Task.Run(() => ((TogglePattern)toggle).Toggle(), cancellationToken);
                    return UiaOutcome(window, foregroundBefore, null);
                }
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
                {
                    await Task.Run(() => ((SelectionItemPattern)selection).Select(), cancellationToken);
                    return UiaOutcome(window, foregroundBefore, null);
                }
                if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
                {
                    // 组合框/树节点/折叠面板：按当前状态做一次与真实点击等价的展开或收起。
                    var pattern = (ExpandCollapsePattern)expand;
                    await Task.Run(() =>
                    {
                        if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded) pattern.Collapse();
                        else pattern.Expand();
                    }, cancellationToken);
                    return UiaOutcome(window, foregroundBefore, null);
                }
            }
            catch (Exception error) when (IsUnavailable(error))
            {
                // 元素在观察后失效或提供程序拒绝该模式，退回坐标路径并如实标记未验证。
                RestoreForeground(window, foregroundBefore);
            }
        }

        if (!IsForeground(window))
        {
            if (!MessagesReliable(snapshot))
                throw new DesktopOperationException("needs_foreground", "目标应用通常忽略窗口消息输入；请先调用 focus，再用真实输入重试", "app_ignores_message");
            var target = ResolveTarget(window, screenX, screenY);
            if (target == null)
                throw new DesktopOperationException("needs_foreground", "目标窗口被遮挡且无法定位其子窗口；请调用 focus 后再试");
            cancellationToken.ThrowIfCancellationRequested();
            var point = ScreenToClient(target.Value.Handle, screenX, screenY);
            PostClick(target.Value.Handle, point.X, point.Y);
            return new ActionOutcome(InputTier.Message, false, "已向目标子窗口投递点击；部分应用会忽略消息输入");
        }

        if (!allowForegroundInput)
            throw new DesktopOperationException("needs_foreground", "目标不在前台，已拒绝真实输入");
        EnsureUserIdleForTierC();

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsForeground(window))
            throw new DesktopOperationException("needs_foreground", "前台窗口在动作前发生变化，已取消");
        // 整组手势必须一次 SendInput：绝对移动与按下/抬起若分两次调用，其它会话可插在中间。
        Send(new[]
        {
            AbsoluteMouseMove(screenX, screenY),
            MouseInput(MouseEventLeftDown),
            MouseInput(MouseEventLeftUp),
        });
        return new ActionOutcome(InputTier.Input, false, "使用真实鼠标输入");
    }

    public static async Task<ActionOutcome> TypeTextAsync(
        nint window,
        UiaSnapshot? snapshot,
        string? elementId,
        string text,
        int screenX,
        int screenY,
        bool allowForegroundInput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutomationElement? targetElement = null;
        if (elementId != null && snapshot != null && !UiaWatchdog.IsWindowQuarantined(window) &&
            snapshot.NativeElements.TryGetValue(elementId, out var element))
        {
            targetElement = element;
            var foregroundBefore = NativeMethods.GetForegroundWindow();
            try
            {
                if (await Task.Run(() => TryNativeSetValue(element, text), cancellationToken))
                    return UiaOutcome(window, foregroundBefore, "已用原生 UIA（AutoSetFocus=false）");
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                {
                    var pattern = (ValuePattern)value;
                    if (pattern.Current.IsReadOnly) throw new DesktopOperationException("read_only", "目标输入框为只读");
                    await Task.Run(() => pattern.SetValue(text), cancellationToken);
                    return UiaOutcome(window, foregroundBefore, null);
                }
            }
            catch (Exception error) when (IsUnavailable(error))
            {
                // 提供程序拒绝 SetValue（异常时未发生写入），退回消息/真实键盘路径。
                RestoreForeground(window, foregroundBefore);
            }
        }

        if (!IsForeground(window))
        {
            if (!MessagesReliable(snapshot))
                throw new DesktopOperationException("needs_foreground", "目标应用通常忽略窗口消息输入；请先调用 focus，再用真实输入重试", "app_ignores_message");
            var target = ResolveTarget(window, screenX, screenY);
            if (target == null)
                throw new DesktopOperationException("needs_foreground", "目标窗口被遮挡且无法定位其子窗口；请调用 focus 后再试");
            cancellationToken.ThrowIfCancellationRequested();
            return await PostTextAsync(target.Value.Handle, text, targetElement, cancellationToken);
        }

        if (!allowForegroundInput)
            throw new DesktopOperationException("needs_foreground", "目标不在前台，已拒绝真实输入");
        EnsureUserIdleForTierC();

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsForeground(window))
            throw new DesktopOperationException("needs_foreground", "前台窗口在动作前发生变化，已取消");
        if (targetElement != null)
        {
            // 无 ValuePattern 的控件只能走真实键盘；先把焦点给到目标元素，否则字符会打到当前焦点控件上。
            try
            {
                await Task.Run(() => targetElement.SetFocus(), cancellationToken);
            }
            catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                // 元素不支持编程聚焦时维持原有行为：输入到当前焦点控件。
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        SendText(text, screenX, screenY);
        return new ActionOutcome(InputTier.Input, false, "使用真实键盘输入");
    }

    public static Task<ActionOutcome> PressKeyAsync(
        nint window,
        UiaSnapshot? snapshot,
        string chord,
        int screenX,
        int screenY,
        bool allowForegroundInput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = ParseChord(chord);
        if (!IsForeground(window))
        {
            if (!MessagesReliable(snapshot))
                throw new DesktopOperationException("needs_foreground", "目标应用通常忽略窗口消息输入；请先调用 focus，再用真实输入重试", "app_ignores_message");
            var target = ResolveTarget(window, screenX, screenY);
            if (target == null)
                throw new DesktopOperationException("needs_foreground", "目标窗口被遮挡且无法定位其子窗口；请调用 focus 后再试");
            foreach (var input in inputs.Keyboard)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NativeMethods.PostMessage(target.Value.Handle, input.Down ? WmKeyDown : WmKeyUp, input.VirtualKey, 1);
            }
            return Task.FromResult(new ActionOutcome(InputTier.Message, false, "已向目标子窗口投递按键；部分应用会忽略消息输入"));
        }

        if (!allowForegroundInput)
            throw new DesktopOperationException("needs_foreground", "目标不在前台，已拒绝真实输入");
        EnsureUserIdleForTierC();
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsForeground(window))
            throw new DesktopOperationException("needs_foreground", "前台窗口在动作前发生变化，已取消");
        Send(inputs.Inputs);
        return Task.FromResult(new ActionOutcome(InputTier.Input, false, "使用真实键盘输入"));
    }

    public static async Task<ActionOutcome> ScrollAsync(
        nint window,
        UiaSnapshot? snapshot,
        int screenX,
        int screenY,
        int delta,
        bool allowForegroundInput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(delta) || Math.Abs(delta) > 1200) throw new ArgumentOutOfRangeException(nameof(delta));
        var uia = await TryUiaScrollAsync(window, screenX, screenY, (int)delta, cancellationToken);
        if (uia != null) return uia;
        if (!IsForeground(window))
        {
            if (!MessagesReliable(snapshot))
                throw new DesktopOperationException("needs_foreground", "目标应用通常忽略窗口消息输入；请先调用 focus，再用真实输入重试", "app_ignores_message");
            var target = ResolveTarget(window, screenX, screenY);
            if (target == null)
                throw new DesktopOperationException("needs_foreground", "目标窗口被遮挡且无法定位其子窗口；请调用 focus 后再试");
            cancellationToken.ThrowIfCancellationRequested();
            var wParam = (nint)((long)(delta & 0xFFFF) << 16);
            NativeMethods.PostMessage(target.Value.Handle, WmMouseWheel, wParam, MakeLParam(screenX, screenY));
            return new ActionOutcome(InputTier.Message, false, "已向目标子窗口投递滚轮；部分应用会忽略消息输入");
        }

        if (!allowForegroundInput)
            throw new DesktopOperationException("needs_foreground", "目标不在前台，已拒绝真实输入");
        EnsureUserIdleForTierC();
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsForeground(window))
            throw new DesktopOperationException("needs_foreground", "前台窗口在动作前发生变化，已取消");
        Send(new[]
        {
            AbsoluteMouseMove(screenX, screenY),
            MouseInput(MouseEventWheel, delta),
        });
        return new ActionOutcome(InputTier.Input, false, "使用真实滚轮输入");
    }

    /// <summary>
    /// A 级滚动：指针下的元素或最近的可滚动祖先支持 ScrollPattern 时直接滚动，
    /// 不动光标、不激活窗口，因此被遮挡时也能用。找不到可滚动容器则返回 null 交给 B/C 级。
    /// </summary>
    private static async Task<ActionOutcome?> TryUiaScrollAsync(
        nint window,
        int screenX,
        int screenY,
        int delta,
        CancellationToken cancellationToken)
    {
        if (delta == 0) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
            return await Task.Run(() => UiaScroll(window, screenX, screenY, delta), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return null;
        }
    }

    private static ActionOutcome? UiaScroll(nint window, int screenX, int screenY, int delta)
    {
        // 指针下不是目标窗口（被遮挡）时，FromPoint 会命中别的应用，不能拿它的滚动容器代劳。
        var under = NativeMethods.WindowFromPoint(new NativeMethods.Win32Point { X = screenX, Y = screenY });
        if (RootOf(under) != RootOf(window)) return null;

        var element = AutomationElement.FromPoint(new System.Windows.Point(screenX, screenY));
        for (var depth = 0; depth < 5 && element != null; depth++)
        {
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var found))
            {
                var scroll = (ScrollPattern)found;
                if (scroll.Current.VerticallyScrollable)
                {
                    var foregroundBefore = NativeMethods.GetForegroundWindow();
                    var amount = delta < 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
                    // 滚轮一格按控件小步长折算；UIA 没有像素级滚动。
                    var steps = Math.Max(1, Math.Abs(delta) / 120);
                    for (var i = 0; i < steps; i++) scroll.Scroll(amount, ScrollAmount.NoAmount);
                    return UiaOutcome(window, foregroundBefore, "已用 UIA ScrollPattern 滚动；幅度按控件小步长折算");
                }
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return null;
    }

    public static ActionOutcome Focus(nint window)
    {
        WindowLocator.Activate(window);
        return IsForeground(window)
            ? new ActionOutcome(InputTier.None, true, "目标已在前台")
            : new ActionOutcome(InputTier.None, false, "已请求前台，但系统未切换");
    }

    public static bool IsForeground(nint window)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0) return false;
        return NativeMethods.GetAncestor(foreground, GaRoot) == NativeMethods.GetAncestor(window, GaRoot);
    }

    /// <summary>
    /// Tier C 只在用户空闲时执行：真实输入会占用唯一的光标与键盘焦点，
    /// 用户正在操作时必须主动让行，而不是和用户抢。
    /// </summary>
    private static void EnsureUserIdleForTierC()
    {
        if (UserPresenceGuard.IsIdle()) return;
        throw new DesktopOperationException("user_active", "用户正在操作，真实输入已让行，请稍后重试", "defer");
    }

    private static bool TryNativeClick(AutomationElement element)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty) return false;
            var runtimeId = element.GetRuntimeId();
            using var session = NativeUiaSession.TryCreate();
            if (session == null) return false;
            var native = session.ResolveFromPoint(
                (int)(bounds.Left + bounds.Width / 2),
                (int)(bounds.Top + bounds.Height / 2),
                runtimeId);
            if (native == 0) return false;
            try
            {
                return session.TryClick(native);
            }
            finally
            {
                session.Release(native);
            }
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return false;
        }
    }

    private static bool TryNativeSetValue(AutomationElement element, string text)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty) return false;
            var runtimeId = element.GetRuntimeId();
            using var session = NativeUiaSession.TryCreate();
            if (session == null) return false;
            var native = session.ResolveFromPoint(
                (int)(bounds.Left + bounds.Width / 2),
                (int)(bounds.Top + bounds.Height / 2),
                runtimeId);
            if (native == 0) return false;
            try
            {
                return session.TrySetValue(native, text);
            }
            finally
            {
                session.Release(native);
            }
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return false;
        }
    }

    private static ActionOutcome UiaOutcome(nint window, nint foregroundBefore, string? note)
    {
        // 部分 UIA 提供程序（如 WinForms 文本框）在执行模式时会顺带 SetFocus，把目标窗口推到前台。
        // 托管 UIA 客户端不暴露 AutoSetFocus，这里先度量是否真的发生切换，再恢复动作前的前台，
        // 并把"是否未变 / 是否恢复过"如实带回调用方，不把发生过抢焦点的事实藏起来。
        var windowRoot = RootOf(window);
        var beforeRoot = RootOf(foregroundBefore);
        var afterRoot = RootOf(NativeMethods.GetForegroundWindow());
        RestoreForeground(window, foregroundBefore);
        var restoredRoot = RootOf(NativeMethods.GetForegroundWindow());
        var guard = ForegroundGuard.Evaluate(beforeRoot, afterRoot, windowRoot, restoredRoot);
        var effectiveNote = guard.Unchanged
            ? guard.Restored ? "UIA 动作曾把目标推到前台，已恢复原前台" : note
            : "UIA 动作改变了前台窗口，且未能恢复";
        return new ActionOutcome(InputTier.Uia, true, effectiveNote, null, guard.Unchanged, guard.Restored);
    }

    private static nint RootOf(nint window) => window == 0 ? 0 : NativeMethods.GetAncestor(window, GaRoot);

    private static bool RestoreForeground(nint window, nint foregroundBefore)
    {
        if (foregroundBefore == 0) return true;
        var beforeRoot = NativeMethods.GetAncestor(foregroundBefore, GaRoot);
        var windowRoot = NativeMethods.GetAncestor(window, GaRoot);
        if (beforeRoot == 0 || windowRoot == 0) return true;
        // 动作前前台就在目标内：无需恢复，也不能把用户切走。
        if (beforeRoot == windowRoot) return true;
        var current = NativeMethods.GetForegroundWindow();
        if (current == 0 || NativeMethods.GetAncestor(current, GaRoot) != windowRoot) return true;
        try
        {
            WindowLocator.Activate(beforeRoot);
        }
        catch
        {
            return false;
        }
        var now = NativeMethods.GetForegroundWindow();
        return now != 0 && NativeMethods.GetAncestor(now, GaRoot) == beforeRoot;
    }

    private static (nint Handle, int X, int Y)? ResolveTarget(nint window, int screenX, int screenY)
    {
        var client = ScreenToClient(window, screenX, screenY);
        var point = new NativeMethods.Win32Point { X = client.X, Y = client.Y };
        var child = NativeMethods.ChildWindowFromPointEx(window, point, CwpSkipInvisible | CwpSkipDisabled | CwpSkipTransparent);
        if (child == 0) child = window;
        if (NativeMethods.GetAncestor(child, GaRoot) != NativeMethods.GetAncestor(window, GaRoot)) return null;
        var childClient = ScreenToClient(child, screenX, screenY);
        return (child, childClient.X, childClient.Y);
    }

    private static (int X, int Y) ScreenToClient(nint window, int screenX, int screenY)
    {
        var point = new NativeMethods.Win32Point { X = screenX, Y = screenY };
        if (!NativeMethods.ScreenToClient(window, ref point)) throw new DesktopOperationException("window_closed", "无法换算窗口坐标");
        return (point.X, point.Y);
    }

    private static nint MakeLParam(int low, int high) => (nint)((high << 16) | (low & 0xFFFF));

    private static void PostClick(nint target, int x, int y)
    {
        // 原生按钮类控件在合成点击时依赖真实光标位置，直接投递 DOWN/UP 往往不触发；
        // 这类控件响应 BM_CLICK，优先使用。
        var className = NativeMethods.WindowClass(target);
        if (className != null && className.IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            NativeMethods.PostMessage(target, WmClick, 0, 0);
            return;
        }
        var lParam = MakeLParam(x, y);
        NativeMethods.PostMessage(target, WmLButtonDown, (nint)MkLButton, lParam);
        NativeMethods.PostMessage(target, WmLButtonUp, 0, lParam);
    }

    private static void PostText(nint target, string text)
    {
        foreach (var character in text)
        {
            if (character is '\r' or '\n')
            {
                // 编辑控件依赖 WM_CHAR 插入换行，只发 WM_KEYDOWN 不会产生换行。
                NativeMethods.PostMessage(target, WmChar, 0x0D, 1);
                continue;
            }
            NativeMethods.PostMessage(target, WmChar, character, 1);
        }
    }

    private static async Task<ActionOutcome> PostTextAsync(nint target, string text, AutomationElement? element, CancellationToken cancellationToken)
    {
        var className = NativeMethods.WindowClass(target) ?? string.Empty;
        var editable = className.IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0;
        var password = editable && (NativeMethods.GetWindowLong32(target, GwlStyle) & EsPassword) != 0;
        var before = editable && !password ? ReadText(target) : null;

        PostText(target, text);
        if (password) return new ActionOutcome(InputTier.Message, false, "已向密码框投递文本；出于隐私不回读验证", MessageEvidence.None);
        if (editable && await ConfirmWmTextAsync(target, before, text, cancellationToken))
            return new ActionOutcome(InputTier.Message, true, "已向目标子窗口投递文本，并经 WM_GETTEXT 回读确认", MessageEvidence.WmGetText);

        // 自绘/Chromium 控件没有 WM_GETTEXT 语义，改由 UIA 独立回读判定。
        if (element != null)
        {
            await Task.Delay(120, cancellationToken);
            var (readback, evidence) = await TryReadUiaTextAsync(element, cancellationToken);
            if (MessageVerification.Matches(readback, text))
                return new ActionOutcome(InputTier.Message, true, "已向目标子窗口投递文本，并经 UIA 回读确认", evidence);
        }

        return new ActionOutcome(
            InputTier.Message,
            false,
            editable ? "已向目标子窗口投递文本，但回读未确认；部分应用会忽略消息输入" : "已向目标子窗口投递文本；该控件无法通过消息回读确认",
            MessageEvidence.None);
    }

    private static async Task<bool> ConfirmWmTextAsync(nint target, string? before, string text, CancellationToken cancellationToken)
    {
        // 标准编辑控件可通过 WM_GETTEXT 回读，投递后确认文本确实发生变化。
        var deadline = Environment.TickCount64 + 400;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadText(target);
            if (current != null && !string.Equals(current, before, StringComparison.Ordinal)
                && MessageVerification.Matches(current, text))
                return true;
            await Task.Delay(25, cancellationToken);
        }
        return false;
    }

    private static async Task<(string? Text, string Evidence)> TryReadUiaTextAsync(AutomationElement element, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(300));
            return await Task.Run(() => ReadUiaText(element), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, MessageEvidence.None);
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return (null, MessageEvidence.None);
        }
    }

    private static (string? Text, string Evidence) ReadUiaText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
                return (((TextPattern)text).DocumentRange.GetText(-1), MessageEvidence.UiaText);
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                return (((ValuePattern)value).Current.Value, MessageEvidence.UiaValue);
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            // 提供程序不可用时视为无法回读。
        }
        return (null, MessageEvidence.None);
    }

    private static bool MessagesReliable(UiaSnapshot? snapshot) => snapshot?.Capabilities.MessageReliable ?? true;

    private static bool IsUnavailable(Exception error) =>
        error is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException;

    private static string? ReadText(nint target)
    {
        var length = NativeMethods.SendMessageTimeoutValue(target, WmGetTextLength, 0, 0, SmtoAbortIfHung, 100, out var result) == 0
            ? 0
            : (int)result;
        var capacity = Math.Clamp(Math.Max(length + 2, 64), 64, 200_000);
        var buffer = new StringBuilder(capacity);
        return NativeMethods.SendMessageTimeoutText(target, WmGetText, capacity, buffer, SmtoAbortIfHung, 100, out _) == 0
            ? null
            : buffer.ToString();
    }

    private static void SendText(string text, int screenX, int screenY)
    {
        var inputs = new List<Input>(text.Length * 2 + 1) { AbsoluteMouseMove(screenX, screenY) };
        foreach (var character in text)
        {
            if (character is '\r' or '\n')
            {
                inputs.Add(KeyboardInput(0x0D, 0));
                inputs.Add(KeyboardInput(0x0D, 0x0002));
                continue;
            }
            inputs.Add(UnicodeInput(character, 0x0004));
            inputs.Add(UnicodeInput(character, 0x0004 | 0x0002));
        }
        if (inputs.Count > 0) Send(inputs);
    }

    private sealed record ChordInputs(IReadOnlyList<(ushort VirtualKey, bool Down)> Keyboard, Input[] Inputs);

    private static ChordInputs ParseChord(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) throw new ArgumentException("快捷键不能为空", nameof(chord));
        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 5) throw new ArgumentException("快捷键格式无效", nameof(chord));
        var modifiers = new List<ushort>();
        var main = parts[^1];
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => (ushort)0x11,
                "ALT" => (ushort)0x12,
                "SHIFT" => (ushort)0x10,
                _ => throw new ArgumentException($"不支持的修饰键：{parts[i]}", nameof(chord)),
            };
            if (!modifiers.Contains(modifier)) modifiers.Add(modifier);
        }

        var key = KeyCode(main);
        var events = new List<(ushort VirtualKey, bool Down)>();
        var inputs = new List<Input>();
        foreach (var modifier in modifiers)
        {
            events.Add((modifier, true));
            inputs.Add(KeyboardInput(modifier, 0));
        }
        events.Add((key, true));
        inputs.Add(KeyboardInput(key, 0));
        events.Add((key, false));
        inputs.Add(KeyboardInput(key, 0x0002));
        foreach (var modifier in Enumerable.Reverse(modifiers))
        {
            events.Add((modifier, false));
            inputs.Add(KeyboardInput(modifier, 0x0002));
        }
        return new ChordInputs(events, inputs.ToArray());
    }

    private static ushort KeyCode(string key)
    {
        if (key.Length == 1)
        {
            var scan = VkKeyScan(key[0]);
            if (scan != -1) return (ushort)(scan & 0xFF);
        }
        return key.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "BACKSPACE" => 0x08,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            _ => throw new ArgumentException($"不支持的按键：{key}"),
        };
    }

    private static Input KeyboardInput(ushort key, uint flags) => new()
    {
        Type = 1,
        Union = new InputUnion { Keyboard = new KeyboardInputData { VirtualKey = key, ScanCode = 0, Flags = flags, Time = 0, ExtraInfo = NativeMethods.SwarmInputMarker } },
    };

    private static Input UnicodeInput(char character, uint flags) => new()
    {
        Type = 1,
        Union = new InputUnion { Keyboard = new KeyboardInputData { VirtualKey = 0, ScanCode = character, Flags = flags, Time = 0, ExtraInfo = NativeMethods.SwarmInputMarker } },
    };

    private static Input MouseInput(uint flags, int data = 0) => new()
    {
        Type = 0,
        Union = new InputUnion { Mouse = new MouseInputData { X = 0, Y = 0, MouseData = data, Flags = flags, Time = 0, ExtraInfo = NativeMethods.SwarmInputMarker } },
    };

    /// <summary>
    /// 绝对定位的鼠标移动事件。与按下/抬起放进同一次 SendInput，才能保证手势不被其它会话插入；
    /// 坐标必须换算到整个虚拟桌面的 0..65535 归一化空间（多显示器 + DPI 正确）。
    /// </summary>
    private static Input AbsoluteMouseMove(int screenX, int screenY)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        var nx = (int)Math.Round((screenX - left) * 65535.0 / Math.Max(1, width - 1));
        var ny = (int)Math.Round((screenY - top) * 65535.0 / Math.Max(1, height - 1));
        return new Input
        {
            Type = 0,
            Union = new InputUnion
            {
                Mouse = new MouseInputData
                {
                    X = Math.Clamp(nx, 0, 65535),
                    Y = Math.Clamp(ny, 0, 65535),
                    Flags = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk,
                    ExtraInfo = NativeMethods.SwarmInputMarker,
                },
            },
        };
    }

    private static readonly object SendSync = new();

    private static void Send(IEnumerable<Input> inputs)
    {
        var data = inputs.ToArray();
        if (data.Length == 0) return;
        // SendInput 的“事件不交错”只保证单次调用内；跨会话必须由本进程再串行一次，
        // 否则 A 会话的 down 与 B 会话的 up 可能被交错成“按下没抬起”。
        lock (SendSync)
        {
            if (SendInput((uint)data.Length, data, Marshal.SizeOf<Input>()) != data.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 输入执行失败");
        }
        UserPresenceGuard.NoteSynthetic();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char character);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] internal MouseInputData Mouse;
        [FieldOffset(0)] internal KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        internal int X;
        internal int Y;
        internal int MouseData;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }
}
