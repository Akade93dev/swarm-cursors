$ErrorActionPreference = 'Stop'
$root = (Join-Path $PSScriptRoot '..\publish-verify')
. (Join-Path $PSScriptRoot 'edge-profile.ps1')
$log = [System.Collections.Generic.List[string]]::new()
function Say([string]$text) { $log.Add($text); Write-Output $text }
function Assert([string]$name, [bool]$ok, $detail) {
    if (-not $ok) { Say "FAIL $name :: $detail"; throw "assertion failed: $name" }
    Say "PASS $name :: $detail"
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class E2ENative {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    public static void Key(byte vk, bool up) { keybd_event(vk, 0, up ? 2u : 0u, UIntPtr.Zero); }
    public static void ClickMouse() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
}
'@
Add-Type -AssemblyName System.Windows.Forms

# 第一阶段：真实人工输入必须立即暂停 AI；恢复仅限本地热键，且旧观察失效。
function Send-HumanKey {
    [E2ENative]::Key(0x41, $false); [E2ENative]::Key(0x41, $true)
}
function Send-RebuildHotkey {
    [E2ENative]::Key(0x12, $false); [E2ENative]::Key(0x10, $false)
    [E2ENative]::Key(0x53, $false); [E2ENative]::Key(0x53, $true)
    [E2ENative]::Key(0x10, $true); [E2ENative]::Key(0x12, $true)
}
function Send-ResumeHotkey {
    [E2ENative]::Key(0x11, $false); [E2ENative]::Key(0x12, $false); [E2ENative]::Key(0x10, $false)
    [E2ENative]::Key(0x52, $false); [E2ENative]::Key(0x52, $true)
    [E2ENative]::Key(0x10, $true); [E2ENative]::Key(0x12, $true); [E2ENative]::Key(0x11, $true)
}
function Get-OverlayRect([int]$targetPid) {
    $script:overlay = $null
    $script:overlayArea = 0
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $script:minArea = [int]($work.Width * $work.Height * 0.5)
    $callback = [E2ENative+EnumProc]{
        param($h, $l)
        $owner = 0
        [void][E2ENative]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -ne $script:scanPid -or -not [E2ENative]::IsWindowVisible($h)) { return $true }
        $r = New-Object E2ENative+RECT
        [void][E2ENative]::GetWindowRect($h, [ref]$r)
        $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
        if ($area -ge $script:minArea -and $area -gt $script:overlayArea) { $script:overlayArea = $area; $script:overlay = $r }
        return $true
    }
    $script:scanPid = $targetPid
    [void][E2ENative]::EnumWindows($callback, [IntPtr]::Zero)
    return $script:overlay
}

$html = Join-Path $env:TEMP 'swarm-e2e-edge.html'
$page = '<html><head><title>swarm e2e</title><style>html,body{margin:0;height:100%}button{width:100%;height:100%;font-size:48px}</style></head><body><button id="b" onclick="document.title=''swarm-clicked'';this.textContent=''swarm-clicked''">swarm-button</button></body></html>'
Set-Content -LiteralPath $html -Value $page -Encoding ASCII
$profile = Join-Path $env:TEMP 'swarm-edge-profile-p0'
Clear-EdgeProfile -Path $profile

$desktop = $null; $gw = $null; $notepad = $null; $clicker = $null
try {
    $edge = Start-Process -FilePath 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' -ArgumentList @('--new-window', '--no-first-run', "--user-data-dir=$profile", "file:///$($html -replace '\\','/')") -PassThru
    $edgeProc = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $edgeProc = Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like '*swarm e2e*' } | Select-Object -First 1
        if ($edgeProc) { break }
    }
    Assert 'edge window' ($null -ne $edgeProc) ($(if ($edgeProc) { $edgeProc.MainWindowTitle } else { 'not found' }))

    $desktop = Start-Process -FilePath "$root\Swarm.Desktop\Swarm.Desktop.exe" -PassThru
    Start-Sleep -Seconds 3
    Assert 'desktop alive' (-not $desktop.HasExited) "pid $($desktop.Id)"

    $desktopPid = $desktop.Id
    $clicker = Start-Job -ArgumentList $desktopPid -ScriptBlock {
        param($targetPid)
        Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class DlgClick {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
'@
        $deadline = (Get-Date).AddSeconds(25)
        while ((Get-Date) -lt $deadline) {
            $script:found = [IntPtr]::Zero
            $cb = [DlgClick+EnumProc]{
                param($h, $l)
                $sb = New-Object System.Text.StringBuilder 64
                [void][DlgClick]::GetClassName($h, $sb, 64)
                $p = 0
                [void][DlgClick]::GetWindowThreadProcessId($h, [ref]$p)
                if ($p -eq $targetPid -and $sb.ToString() -eq '#32770' -and [DlgClick]::IsWindowVisible($h)) {
                    $script:found = $h
                    return $false
                }
                return $true
            }
            [void][DlgClick]::EnumWindows($cb, [IntPtr]::Zero)
            if ($script:found -ne [IntPtr]::Zero) {
                [void][DlgClick]::PostMessage($script:found, 0x0111, [IntPtr]6, [IntPtr]::Zero)
                return 'clicked'
            }
            Start-Sleep -Milliseconds 200
        }
        return 'notfound'
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "$root\Swarm.Gateway\Swarm.Gateway.exe"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $gw = [System.Diagnostics.Process]::Start($psi)

    $script:id = 0
    function Rpc([string]$method, $params) {
        $script:id++
        $msg = @{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 12 -Compress
        $gw.StandardInput.WriteLine($msg)
        $gw.StandardInput.Flush()
        $line = $gw.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "gateway closed: $($gw.StandardError.ReadToEnd())" }
        return $line | ConvertFrom-Json
    }
    function Tool([string]$name, $arguments) {
        $response = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
        if ($response.error) { throw "rpc error: $($response.error | ConvertTo-Json -Compress)" }
        return $response.result
    }

    [void](Rpc 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'e2e'; version = '1.0' } })

    $begin = Tool 'task_begin' @{ targetProcess = 'msedge'; targetTitle = 'swarm e2e' }
    $token = $begin.structuredContent.token
    Assert 'task_begin token' (-not [string]::IsNullOrWhiteSpace($token)) "token length $($token.Length)"
    $caps = $begin.structuredContent.capabilities
    Assert 'capabilities isChromium' ($caps.isChromium -eq $true) ($caps | ConvertTo-Json -Compress)
    Assert 'capabilities messageReliable=false' ($caps.messageReliable -eq $false) "reliable=$($caps.messageReliable) electron=$($caps.isElectron)"
    $vw = [double]$begin.structuredContent.viewport.width
    $vh = [double]$begin.structuredContent.viewport.height
    $x = [math]::Round($vw / 2, 1)
    $y = [math]::Round($vh * 0.8, 1)

    $notepad = Start-Process -FilePath 'mspaint.exe' -PassThru
    $front = $null
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 250
        $front = Get-Process mspaint -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($front) { break }
    }
    Assert 'foreground displacer' ($null -ne $front) 'mspaint'
    if ($front) { [void][E2ENative]::SetForegroundWindow($front.MainWindowHandle) }
    Start-Sleep -Milliseconds 600

    $blocked = Tool 'click' @{ token = $token; x = $x; y = $y }
    Assert 'blocked click isError' ($blocked.isError -eq $true) ($blocked | ConvertTo-Json -Depth 6 -Compress)
    Assert 'blocked reason' ($blocked.structuredContent.error.reason -eq 'app_ignores_message') "code=$($blocked.structuredContent.error.code) reason=$($blocked.structuredContent.error.reason)"
    Assert 'blocked did not click' ($edgeProc.MainWindowTitle -like '*swarm e2e*') "title=$($edgeProc.MainWindowTitle)"

    $focused = Tool 'focus' @{ token = $token }
    Assert 'focus foreground' ($focused.isError -ne $true) ($focused.structuredContent.target.title)
    Start-Sleep -Milliseconds 600

    $clicked = Tool 'click' @{ token = $token; x = $x; y = $y }
    Assert 'focused click executed' ($clicked.isError -ne $true -and $clicked.structuredContent.executed -eq $true) ($clicked | ConvertTo-Json -Depth 6 -Compress)
    Assert 'focused click tier=input' ($clicked.structuredContent.tier -eq 'input') "tier=$($clicked.structuredContent.tier)"

    $changed = $false
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 250
        $edgeProc.Refresh()
        if ($edgeProc.MainWindowTitle -like '*swarm-clicked*') { $changed = $true; break }
    }
    Assert 'page clicked' $changed "title=$($edgeProc.MainWindowTitle)"

    # 真实人工输入必须立即暂停 AI；暂停期间的动作不得触达目标窗口。
    Send-HumanKey
    Start-Sleep -Milliseconds 400
    $pausedClick = Tool 'click' @{ token = $token; x = $x; y = $y }
    Assert 'human input pauses ai' ($pausedClick.isError -eq $true) ($pausedClick | ConvertTo-Json -Depth 6 -Compress)
    Assert 'pause code manual_takeover' ($pausedClick.structuredContent.error.code -eq 'manual_takeover') "code=$($pausedClick.structuredContent.error.code)"
    Assert 'paused click did not reach page' ($edgeProc.MainWindowTitle -like '*swarm-clicked*') "title=$($edgeProc.MainWindowTitle)"

    # 只有本地热键能恢复；恢复后旧观察失效，必须重新 observe。
    Send-ResumeHotkey
    Start-Sleep -Milliseconds 600
    $afterResume = Tool 'click' @{ token = $token; x = $x; y = $y }
    Assert 'resume invalidates observation' ($afterResume.isError -eq $true) ($afterResume | ConvertTo-Json -Depth 6 -Compress)
    $observed = Tool 'observe' @{ token = $token }
    Assert 'observe after resume' ($observed.isError -ne $true) 'ok'
    $resumedClick = Tool 'click' @{ token = $token; x = $x; y = $y }
    Assert 'click after resume' ($resumedClick.isError -ne $true -and $resumedClick.structuredContent.executed -eq $true) ($resumedClick | ConvertTo-Json -Depth 6 -Compress)

    $logPath = Join-Path $env:LOCALAPPDATA ('SwarmCursors\logs\desktop-' + (Get-Date -Format 'yyyyMMdd') + '.log')
    $pattern = "\[$($desktop.Id)\] hud rect=(\d+),(\d+) (\d+)x(\d+)"
    function Get-HudRect {
        $line = Get-Content -LiteralPath $logPath -Tail 400 -ErrorAction SilentlyContinue | Where-Object { $_ -match $pattern } | Select-Object -Last 1
        if (-not $line) { return $null }
        $m = [regex]::Match($line, $pattern)
        return @{ X = [int]$m.Groups[1].Value; Y = [int]$m.Groups[2].Value; W = [int]$m.Groups[3].Value; H = [int]$m.Groups[4].Value }
    }
    $ended = Tool 'task_end' @{ token = $token }
    Assert 'task_end' ($ended.isError -ne $true) 'ok'

    # 任务结束后用重建热键显示控制条，此时“操作回放”入口可见。
    Start-Sleep -Milliseconds 800
    [void][E2ENative]::SetForegroundWindow($edgeProc.MainWindowHandle)
    Start-Sleep -Milliseconds 200
    Send-RebuildHotkey
    $overlayRect = $null
    for ($i = 0; $i -lt 60 -and -not $overlayRect; $i++) {
        Start-Sleep -Milliseconds 250
        $overlayRect = Get-OverlayRect $desktop.Id
    }
    Assert 'overlay available for replay' ($null -ne $overlayRect) 'full-screen overlay window'
    $hud = Get-HudRect
    Assert 'hud rect reported' ($null -ne $hud) ($(if ($hud) { "$($hud.X),$($hud.Y) $($hud.W)x$($hud.H)" } else { 'not found' }))
    $closeX = $overlayRect.Left + $hud.X + $hud.W - 17
    $hudCenterY = $overlayRect.Top + $hud.Y + [int]($hud.H / 2)
    $replayX = $closeX - 62
    $beforeReplay = $edgeProc.MainWindowTitle
    [void][E2ENative]::SetCursorPos($replayX, $hudCenterY)
    Start-Sleep -Milliseconds 250
    [E2ENative]::ClickMouse()
    Start-Sleep -Seconds 2
    $edgeProc.Refresh()
    Assert 'replay does not touch target' ($edgeProc.MainWindowTitle -eq $beforeReplay) "title=$($edgeProc.MainWindowTitle)"
    Assert 'replay keeps overlay visible' ($null -ne (Get-OverlayRect $desktop.Id)) 'overlay still shown'
}
finally {
    if ($gw -and -not $gw.HasExited) { $gw.Kill() }
    if ($desktop -and -not $desktop.HasExited) { $desktop.Kill() }
    if ($notepad -and -not $notepad.HasExited) { $notepad.Kill() }
    if ($front -and -not $front.HasExited) { $front.Kill() }
    if ($clicker) { Receive-Job $clicker -ErrorAction SilentlyContinue | Out-Null; Remove-Job $clicker -Force -ErrorAction SilentlyContinue }
    Clear-EdgeProfile -Path $profile
    Remove-Item -LiteralPath $html -Force -ErrorAction SilentlyContinue
    Say 'cleanup done'
}
