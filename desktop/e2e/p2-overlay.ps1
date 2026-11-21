$ErrorActionPreference = 'Stop'
$root = (Join-Path $PSScriptRoot '..\publish-verify')
function Say([string]$text) { Write-Output $text }
function Assert([string]$name, [bool]$ok, $detail) {
    if (-not $ok) { Say "FAIL $name :: $detail"; throw "assertion failed: $name" }
    Say "PASS $name :: $detail"
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class OverlayNative {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }
    public static void Key(byte vk, bool up) { keybd_event(vk, 0, up ? 2u : 0u, UIntPtr.Zero); }
    public static void Click() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static RECT WorkArea(IntPtr hwnd) {
        var info = new MONITORINFO();
        info.Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
        GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info);
        return info.Work;
    }
}
'@

# 覆盖层热键：Alt+Shift+S（重建）；急停：Ctrl+Alt+Shift+Esc。
function Send-RebuildHotkey {
    [OverlayNative]::Key(0x12, $false); [OverlayNative]::Key(0x10, $false)
    [OverlayNative]::Key(0x53, $false); [OverlayNative]::Key(0x53, $true)
    [OverlayNative]::Key(0x10, $true); [OverlayNative]::Key(0x12, $true)
}
function Send-EmergencyStop {
    [OverlayNative]::Key(0x11, $false); [OverlayNative]::Key(0x12, $false); [OverlayNative]::Key(0x10, $false)
    [OverlayNative]::Key(0x1B, $false); [OverlayNative]::Key(0x1B, $true)
    [OverlayNative]::Key(0x10, $true); [OverlayNative]::Key(0x12, $true); [OverlayNative]::Key(0x11, $true)
}

function Get-TopWindows([int]$TargetPid) {
    $script:found = New-Object System.Collections.ArrayList
    $callback = [OverlayNative+EnumProc]{
        param($h, $l)
        $owner = 0
        [void][OverlayNative]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $script:scanPid -and [OverlayNative]::IsWindowVisible($h)) { [void]$script:found.Add($h) }
        return $true
    }
    $script:scanPid = $TargetPid
    [void][OverlayNative]::EnumWindows($callback, [IntPtr]::Zero)
    return $script:found
}

function Get-OverlayFor([int]$TargetPid, [int]$MinWidth, [int]$MinHeight) {
    foreach ($h in (Get-TopWindows $TargetPid)) {
        $r = New-Object OverlayNative+RECT
        [void][OverlayNative]::GetWindowRect($h, [ref]$r)
        if (($r.Right - $r.Left) -ge $MinWidth -and ($r.Bottom - $r.Top) -ge $MinHeight) {
            return [pscustomobject]@{ Hwnd = $h; Rect = $r }
        }
    }
    return $null
}

function Wait-OverlayHidden([int]$TargetPid) {
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $visible = @(Get-TopWindows $TargetPid | Where-Object {
            $r = New-Object OverlayNative+RECT
            [void][OverlayNative]::GetWindowRect($_, [ref]$r)
            ($r.Right - $r.Left) -gt 200
        })
        if ($visible.Count -eq 0) { return $true }
    }
    return $false
}

$desktop = $null; $harness = $null
$origin = New-Object OverlayNative+POINT
[void][OverlayNative]::GetCursorPos([ref]$origin)
try {
    $harness = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'click-harness.ps1')) -PassThru
    $form = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $form = Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like 'swarm click harness*' } | Select-Object -First 1
        if ($form) { break }
    }
    Assert 'harness window' ($null -ne $form) ($(if ($form) { $form.MainWindowTitle } else { 'not found' }))
    [void][OverlayNative]::SetForegroundWindow($form.MainWindowHandle)
    Start-Sleep -Milliseconds 500

    $desktop = Start-Process -FilePath "$root\Swarm.Desktop\Swarm.Desktop.exe" -PassThru
    Start-Sleep -Seconds 3
    Assert 'desktop alive' (-not $desktop.HasExited) "pid $($desktop.Id)"

    Send-RebuildHotkey
    $formRect = New-Object OverlayNative+RECT
    [void][OverlayNative]::GetWindowRect($form.MainWindowHandle, [ref]$formRect)
    $minWidth = $formRect.Right - $formRect.Left
    $minHeight = $formRect.Bottom - $formRect.Top
    $found = $null
    for ($i = 0; $i -lt 60 -and -not $found; $i++) {
        Start-Sleep -Milliseconds 250
        $found = Get-OverlayFor $desktop.Id $minWidth $minHeight
    }
    Assert 'overlay shown' ($null -ne $found) "hwnd=$(if ($found) { $found.Hwnd } else { 0 })"
    $overlay = $found.Hwnd
    $rect = $found.Rect
    Assert 'overlay covers target' ($rect.Left -le $formRect.Left -and $rect.Top -le $formRect.Top -and $rect.Right -ge $formRect.Right -and $rect.Bottom -ge $formRect.Bottom) "overlay=$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom) target=$($formRect.Left),$($formRect.Top),$($formRect.Right),$($formRect.Bottom)"

    # 覆盖层只铺工作区：任务栏不能被盖住，否则重建期间既看不见也点不到。
    $work = [OverlayNative]::WorkArea($form.MainWindowHandle)
    Assert 'overlay leaves taskbar usable' ($rect.Bottom -le ($work.Bottom + 1) -and $rect.Bottom -ge ($work.Bottom - 8)) "overlay.bottom=$($rect.Bottom) work.bottom=$($work.Bottom)"

    $cx = [int](($formRect.Left + $formRect.Right) / 2)
    $cy = [int](($formRect.Top + $formRect.Bottom) / 2)
    [void][OverlayNative]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 300
    $style = [OverlayNative]::GetWindowLong($overlay, -20)
    Assert 'overlay is click-through' (($style -band 0x20) -ne 0) ('exstyle=0x{0:X8}' -f $style)
    Assert 'overlay never activates' (($style -band 0x08000000) -ne 0) ('exstyle=0x{0:X8}' -f $style)
    Assert 'target keeps foreground' ([OverlayNative]::GetForegroundWindow() -eq $form.MainWindowHandle) "foreground=$([OverlayNative]::GetForegroundWindow()) target=$($form.MainWindowHandle)"

    # 真实鼠标穿透：点在覆盖层正中间，应当落到下面的 harness 按钮上。
    $before = $form.MainWindowTitle
    [OverlayNative]::Click()
    $after = $before
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 150
        $form.Refresh()
        if ($form.MainWindowTitle -ne $before) { $after = $form.MainWindowTitle; break }
    }
    Assert 'click passes through overlay' ($after -ne $before) "title=$after"

    # 穿透点击之后前台仍然属于真实窗口，覆盖层从未激活。
    Assert 'foreground unchanged after click' ([OverlayNative]::GetForegroundWindow() -eq $form.MainWindowHandle) "foreground=$([OverlayNative]::GetForegroundWindow())"

    # 底部控制条上报自身矩形；指针压上去时临时取消穿透，移开后恢复。
    $logPath = Join-Path $env:LOCALAPPDATA ('SwarmCursors\logs\desktop-' + (Get-Date -Format 'yyyyMMdd') + '.log')
    $pattern = "\[$($desktop.Id)\] hud rect=(\d+),(\d+) (\d+)x(\d+)"
    $hud = $null
    for ($i = 0; $i -lt 30 -and -not $hud; $i++) {
        Start-Sleep -Milliseconds 200
        $line = Get-Content -LiteralPath $logPath -Tail 300 -ErrorAction SilentlyContinue | Where-Object { $_ -match $pattern } | Select-Object -Last 1
        if ($line) {
            $m = [regex]::Match($line, $pattern)
            $hud = @{ X = [int]$m.Groups[1].Value; Y = [int]$m.Groups[2].Value; W = [int]$m.Groups[3].Value; H = [int]$m.Groups[4].Value }
        }
    }
    Assert 'hud rect reported' ($null -ne $hud) ($(if ($hud) { "$($hud.X),$($hud.Y) $($hud.W)x$($hud.H)" } else { 'not found' }))
    $hudCenterX = $rect.Left + $hud.X + [int]($hud.W / 2)
    $hudCenterY = $rect.Top + $hud.Y + [int]($hud.H / 2)
    # 悬停控制条不改变穿透状态：覆盖层始终点击穿透，控制条上的点击由鼠标钩子单独转发。
    [void][OverlayNative]::SetCursorPos($hudCenterX, $hudCenterY)
    Start-Sleep -Milliseconds 300
    $style = [OverlayNative]::GetWindowLong($overlay, -20)
    Assert 'overlay stays click-through over hud' (($style -band 0x20) -ne 0) ('exstyle=0x{0:X8}' -f $style)

    # 点控制条最右侧的 ✕：钩子吞掉这次点击并让页面收到 DOM click，覆盖层随之收起。
    $closeX = $rect.Left + $hud.X + $hud.W - 17
    [void][OverlayNative]::SetCursorPos($closeX, $hudCenterY)
    Start-Sleep -Milliseconds 300
    [OverlayNative]::Click()
    Assert 'hud click closes overlay' (Wait-OverlayHidden $desktop.Id) 'no oversized visible overlay window'
    Assert 'hud click keeps target foreground' ([OverlayNative]::GetForegroundWindow() -eq $form.MainWindowHandle) "foreground=$([OverlayNative]::GetForegroundWindow())"

    # 收起后再次重建，确认急停热键仍然有效。
    Send-RebuildHotkey
    $again = $null
    for ($i = 0; $i -lt 40 -and -not $again; $i++) {
        Start-Sleep -Milliseconds 250
        $again = Get-OverlayFor $desktop.Id $minWidth $minHeight
    }
    Assert 'overlay rebuilt' ($null -ne $again) "hwnd=$(if ($again) { $again.Hwnd } else { 0 })"
    Send-EmergencyStop
    Assert 'overlay hidden after stop' (Wait-OverlayHidden $desktop.Id) 'no oversized visible overlay window'
    Say 'cleanup done'
}
finally {
    [void][OverlayNative]::SetCursorPos($origin.X, $origin.Y)
    if ($desktop -and -not $desktop.HasExited) { $desktop.Kill() }
    if ($harness -and -not $harness.HasExited) { $harness.Kill() }
    Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like 'swarm click harness*' } | ForEach-Object { $_.Kill() }
}
