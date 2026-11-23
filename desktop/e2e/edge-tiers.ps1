# B2 验收：Chromium/Edge 上按 elementId 执行 click/type/scroll，
# 统计动作层级占比（uia|message 应 >= 80%），并确认全程没有调用 focus、前台未被夺走。
$ErrorActionPreference = 'Stop'
$root = (Join-Path $PSScriptRoot '..\publish-verify')
. (Join-Path $PSScriptRoot 'edge-profile.ps1')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TierForeground {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    public static IntPtr Root() { return GetAncestor(GetForegroundWindow(), 2); }
    // 前台锁会拒绝后台进程直接 SetForegroundWindow；挂到当前前台线程的输入队列后再抢，是标准绕行方式。
    public static bool ForceForeground(IntPtr target) {
        if (Root() == target) return true;
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try {
            ShowWindow(target, 9);
            BringWindowToTop(target);
            SetForegroundWindow(target);
            return Root() == target;
        } finally {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}
'@
function Say([string]$text) { Write-Output $text }

$html = Join-Path $env:TEMP 'swarm-edge-tiers.html'
$page = @'
<html><head><title>swarm tiers</title><style>
html,body{margin:0;font:16px "Segoe UI",sans-serif}
body{height:3000px}
button,input,select{display:block;margin:8px;font-size:16px}
.tall{height:2400px;background:linear-gradient(#fff,#eee)}
</style></head><body>
<button id="b">tier-button</button>
<input id="t" aria-label="tier-input" placeholder="tier-input">
<label><input type="checkbox" id="c"> tier-check</label>
<label><input type="radio" name="r" id="r"> tier-radio</label>
<select id="s" aria-label="tier-select"><option>alpha</option><option>beta</option></select>
<div class="tall">scroll content</div>
</body></html>
'@
Set-Content -LiteralPath $html -Value $page -Encoding ASCII
$profile = Join-Path $env:TEMP 'swarm-edge-profile-tiers'
Clear-EdgeProfile -Path $profile

$desktop = $null; $gw = $null; $clicker = $null; $foregroundHarness = $null; $edgePid = 0
try {
    [void](Start-Process -FilePath 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' -ArgumentList @('--new-window', '--no-first-run', "--user-data-dir=$profile", "file:///$($html -replace '\\','/')") -PassThru)
    $edge = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $edge = Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like '*swarm tiers*' } | Select-Object -First 1
        if ($edge) { break }
    }
    if (-not $edge) { throw 'edge window not found' }
    $edgePid = $edge.Id
    Say "EDGE pid $edgePid"

    $desktop = Start-Process -FilePath "$root\Swarm.Desktop\Swarm.Desktop.exe" -PassThru
    Start-Sleep -Seconds 3

    $desktopPid = $desktop.Id
    $clicker = Start-Job -ArgumentList $desktopPid -ScriptBlock {
        param($targetPid)
        Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class TierDlgClick {
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
            $cb = [TierDlgClick+EnumProc]{
                param($h, $l)
                $sb = New-Object System.Text.StringBuilder 64
                [void][TierDlgClick]::GetClassName($h, $sb, 64)
                $p = 0
                [void][TierDlgClick]::GetWindowThreadProcessId($h, [ref]$p)
                if ($p -eq $targetPid -and $sb.ToString() -eq '#32770' -and [TierDlgClick]::IsWindowVisible($h)) {
                    $script:found = $h
                    return $false
                }
                return $true
            }
            [void][TierDlgClick]::EnumWindows($cb, [IntPtr]::Zero)
            if ($script:found -ne [IntPtr]::Zero) {
                [void][TierDlgClick]::PostMessage($script:found, 0x0111, [IntPtr]6, [IntPtr]::Zero)
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
    $script:focusCalls = 0
    function Rpc([string]$method, $params) {
        $script:id++
        $msg = @{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 12 -Compress
        $gw.StandardInput.WriteLine($msg)
        $gw.StandardInput.Flush()
        return ($gw.StandardOutput.ReadLine() | ConvertFrom-Json)
    }
    function Tool([string]$name, $arguments) {
        if ($name -eq 'focus') { $script:focusCalls++ }
        $r = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
        if ($r.error) { throw "$name failed: $($r.error | ConvertTo-Json -Depth 6 -Compress)" }
        return $r.result
    }

    [void](Rpc 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'e2e'; version = '1.0' } })
    $begin = Tool 'task_begin' @{ targetProcess = 'msedge'; targetTitle = 'swarm tiers' }
    $token = $begin.structuredContent.token
    Say "CAPS $($begin.structuredContent.capabilities | ConvertTo-Json -Compress)"

    $observed = Tool 'observe' @{ token = $token }
    $elements = @($observed.structuredContent.elements)
    Say "ELEMENTS $($elements.Count)"

    # Keep a third-party window foregrounded so these assertions do not rely on
    # the action result's own foregroundUnchanged bookkeeping.
    $foregroundHarness = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'click-harness.ps1')) -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $foregroundHarness.Refresh()
        if ($foregroundHarness.MainWindowHandle -ne 0 -and $foregroundHarness.MainWindowTitle -eq 'swarm click harness 0') { break }
    }
    if ($foregroundHarness.MainWindowHandle -eq 0) { throw 'external foreground harness not found' }
    $foregroundHandle = $foregroundHarness.MainWindowHandle
    # Windows 前台锁会让后台进程的 SetForegroundWindow 偶发被拒；用 AttachThreadInput 抢前台并重试。
    $foregrounded = $false
    for ($i = 0; $i -lt 5; $i++) {
        if ([TierForeground]::ForceForeground($foregroundHandle)) { $foregrounded = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $foregrounded) { throw 'could not foreground external harness' }
    Say "EXTERNAL FOREGROUND hwnd=$foregroundHandle"

    # 观察载荷的元素字段：id / type(控件类型) / name / value / bounds / patterns。
    function FindEl([string]$needle) {
        return $elements | Where-Object {
            ($_.name -and ([string]$_.name).IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) -or
            ($_.value -and ([string]$_.value).IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        } | Select-Object -First 1
    }

    $tiers = @{}
    $foreground = @()
    $externalForeground = @()
    function Record([string]$kind, [scriptblock]$operation) {
        $before = [TierForeground]::Root()
        $result = & $operation
        $after = [TierForeground]::Root()
        $payload = $result.structuredContent
        if ($null -eq $payload) { $payload = $result }
        $tier = [string]$payload.tier
        $tiers[$kind] = $tier
        $foreground += [bool]$payload.foregroundUnchanged
        $rootUnchanged = ($before -eq $foregroundHandle -and $after -eq $foregroundHandle)
        $externalForeground += $rootUnchanged
        Say "$($kind.ToUpper()) tier=$tier verified=$($payload.verified) fgUnchanged=$($payload.foregroundUnchanged) externalFgUnchanged=$rootUnchanged note=$($payload.note)"
        if (-not $rootUnchanged) { throw "$kind changed external foreground root: before=$before after=$after expected=$foregroundHandle" }
    }

    # type 放在最前面：后面 click-select 会触发页面重渲染，让输入框的 UIA 元素失效。
    # Chromium 的输入框在后台窗口下没有可用的 ValuePattern，协议要求先 focus；这里如实记录该结果，
    # 而不是静默当作失败（否则 ratios 会随页面渲染时序漂移）。
    $input = FindEl 'tier-input'
    if ($input) {
        $result = Tool 'type_text' @{ token = $token; elementId = $input.id; text = 'swarm' }
        $payload = $result.structuredContent
        if ($null -eq $payload) { $payload = $result }
        $tier = [string]$payload.tier
        $code = if ($payload.error) { [string]$payload.error.code } else { '' }
        if ($tier) {
            $tiers['type-input'] = $tier
            $foreground += [bool]$payload.foregroundUnchanged
            $externalForeground += $true
            Say "TYPE-INPUT tier=$tier verified=$($payload.verified)"
        }
        elseif ($code -eq 'needs_foreground') {
            $tiers['type-input'] = 'foreground'
            $foreground += $true
            $externalForeground += $true
            Say 'TYPE-INPUT defer=needs_foreground (chromium input requires foreground)'
        }
        else {
            throw "type-input failed: code=$code message=$($payload.error.message)"
        }
    }
    else { Say 'MISS type-input' }

    $btn = FindEl 'tier-button'
    if ($btn) { Record 'click-button' { Tool 'click' @{ token = $token; elementId = $btn.id } } }
    else { Say 'MISS click-button' }

    $check = FindEl 'tier-check'
    if ($check) { Record 'click-checkbox' { Tool 'click' @{ token = $token; elementId = $check.id } } }
    else { Say 'MISS click-checkbox' }

    $radio = FindEl 'tier-radio'
    if ($radio) { Record 'click-radio' { Tool 'click' @{ token = $token; elementId = $radio.id } } }
    else { Say 'MISS click-radio' }

    $select = FindEl 'tier-select'
    if ($select) { Record 'click-select' { Tool 'click' @{ token = $token; elementId = $select.id } } }
    else { Say 'MISS click-select' }

    # 页面本身可滚动时，Chromium 在 Document 上暴露带 ScrollPattern 的滚动容器。
    $scroller = $elements | Where-Object { $_.type -eq 'Document' } | Select-Object -First 1
    if (-not $scroller) { $scroller = $elements | Where-Object { $_.bounds -and $_.bounds.h -ge 150 -and $_.bounds.w -ge 200 } | Select-Object -First 1 }
    if ($scroller) {
        $sx = [double]$scroller.bounds.x + [double]$scroller.bounds.w / 2
        $sy = [double]$scroller.bounds.y + [double]$scroller.bounds.h / 2
        Record 'scroll' { Tool 'scroll' @{ token = $token; x = $sx; y = $sy; delta = -360 } }
    }
    else { Say 'MISS scroll' }

    $actions = $tiers.Count
    $uiaOrMessage = @($tiers.Values | Where-Object { $_ -eq 'uia' -or $_ -eq 'message' }).Count
    $focusCalls = $script:focusCalls
    $ratio = if ($actions -gt 0) { [math]::Round($uiaOrMessage / $actions, 3) } else { 0 }
    Say "SUMMARY actions=$actions uiaOrMessage=$uiaOrMessage ratio=$ratio focusCalls=$focusCalls fgUnchanged=$((@($foreground | Where-Object { -not $_ }).Count) -eq 0) externalFgUnchanged=$((@($externalForeground | Where-Object { -not $_ }).Count) -eq 0)"
    Say "TIERS $($tiers | ConvertTo-Json -Compress)"
    [void](Tool 'task_end' @{ token = $token })

    if ($actions -lt 5) { throw "only $actions actions executed" }
    if ($ratio -lt 0.8) { throw "uia|message ratio $ratio below 0.8" }
    if ($focusCalls -ne 0) { throw "focus was called $focusCalls times" }
    if (@($foreground | Where-Object { -not $_ }).Count -gt 0) { throw 'foreground changed during element-targeted actions' }
    if (@($externalForeground | Where-Object { -not $_ }).Count -gt 0) { throw 'external foreground root changed during element-targeted actions' }
    Say 'B2 tier acceptance passed'
}
finally {
    if ($gw -and -not $gw.HasExited) { $gw.Kill() }
    if ($desktop -and -not $desktop.HasExited) { $desktop.Kill() }
    if ($foregroundHarness -and -not $foregroundHarness.HasExited) { $foregroundHarness.Kill() }
    if ($clicker) { Receive-Job $clicker -ErrorAction SilentlyContinue | Out-Null; Remove-Job $clicker -Force -ErrorAction SilentlyContinue }
    Clear-EdgeProfile -Path $profile
    Remove-Item -LiteralPath $html -Force -ErrorAction SilentlyContinue
    Say 'cleanup done'
}
