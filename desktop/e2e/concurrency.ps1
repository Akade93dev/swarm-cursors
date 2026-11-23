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
using System.Text;
using System.Runtime.InteropServices;
public static class ConcNative {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
'@

function Get-ForegroundProcessName {
    $h = [ConcNative]::GetForegroundWindow()
    if ($h -eq [IntPtr]::Zero) { return '' }
    $procId = 0
    [void][ConcNative]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($procId -eq 0) { return '' }
    try { return (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { return '' }
}

# 三个不同标题的页面各开一个窗口：会话以 Gateway 为键、写动作以 hwnd 为键，
# 只有目标窗口互不相同时，跨会话并行才是真并行而不是被窗口租约串行化。
function Find-WindowByTitle([string]$title) {
    $script:wantTitle = $title
    $script:hit = [IntPtr]::Zero
    $callback = [ConcNative+EnumProc]{
        param($h, $l)
        if (-not [ConcNative]::IsWindowVisible($h)) { return $true }
        $sb = New-Object System.Text.StringBuilder 512
        [void][ConcNative]::GetWindowText($h, $sb, 512)
        if ($sb.ToString().IndexOf($script:wantTitle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $script:hit = $h
            return $false
        }
        return $true
    }
    [void][ConcNative]::EnumWindows($callback, [IntPtr]::Zero)
    return $script:hit
}

$titles = @('swarm-concurrency-a', 'swarm-concurrency-b', 'swarm-concurrency-c')
$pages = @($titles | ForEach-Object { Join-Path $env:TEMP "$_.html" })
$profile = Join-Path $env:TEMP 'swarm-edge-profile-concurrency'
Clear-EdgeProfile -Path $profile
for ($i = 0; $i -lt $titles.Count; $i++) {
    $page = "<html><head><title>$($titles[$i])</title><style>html,body{margin:0;height:100%;font:28px sans-serif}</style></head><body><h1>$($titles[$i])</h1><p>concurrency harness</p></body></html>"
    Set-Content -LiteralPath $pages[$i] -Value $page -Encoding ASCII
}

$desktop = $null
$clicker = $null
$gateways = @()
$previousCapacity = $env:SWARM_MAX_SESSIONS
try {
    foreach ($page in $pages) {
        Start-Process -FilePath 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' -ArgumentList @('--new-window', '--no-first-run', "--user-data-dir=$profile", "file:///$($page -replace '\\','/')") | Out-Null
    }
    $handles = @()
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $handles = @($titles | ForEach-Object { Find-WindowByTitle $_ } | Where-Object { $_.ToInt64() -ne 0 })
        if ($handles.Count -eq 3) { break }
    }
    Assert 'three edge targets' ($handles.Count -eq 3) "windows=$($handles.Count) titles=$($titles -join ',')"

    $env:SWARM_MAX_SESSIONS = '3'
    $desktop = Start-Process -FilePath "$root\Swarm.Desktop\Swarm.Desktop.exe" -PassThru
    Start-Sleep -Seconds 3
    Assert 'desktop alive' (-not $desktop.HasExited) "pid $($desktop.Id)"

    # 每个会话的 task_begin 都要人工授权；确认队列串行，所以对话框逐个出现、逐个点“是”。
    $clicker = Start-Job -ArgumentList $desktop.Id -ScriptBlock {
        param($targetPid)
        Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ConcDlg {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
'@
        $clicked = New-Object 'System.Collections.Generic.HashSet[Int64]'
        $deadline = (Get-Date).AddSeconds(90)
        while ($clicked.Count -lt 3 -and (Get-Date) -lt $deadline) {
            $script:found = @()
            $cb = [ConcDlg+EnumProc]{
                param($h, $l)
                $sb = New-Object System.Text.StringBuilder 64
                [void][ConcDlg]::GetClassName($h, $sb, 64)
                $p = 0
                [void][ConcDlg]::GetWindowThreadProcessId($h, [ref]$p)
                if ($p -eq $targetPid -and $sb.ToString() -eq '#32770' -and [ConcDlg]::IsWindowVisible($h)) {
                    $script:found += $h
                }
                return $true
            }
            [void][ConcDlg]::EnumWindows($cb, [IntPtr]::Zero)
            foreach ($h in $script:found) {
                if ($clicked.Add($h.ToInt64())) {
                    [void][ConcDlg]::PostMessage($h, 0x0111, [IntPtr]6, [IntPtr]::Zero)
                }
            }
            Start-Sleep -Milliseconds 150
        }
        return $clicked.Count
    }

    function New-Gateway {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = "$root\Swarm.Gateway\Swarm.Gateway.exe"
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        $process = [System.Diagnostics.Process]::Start($psi)
        $gateway = [pscustomobject]@{ Process = $process; Seq = 0 }
        [void](Invoke-Rpc $gateway 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'e2e-concurrency'; version = '1.0' } })
        return $gateway
    }

    function Send-Line($Gateway, $message) {
        $Gateway.Process.StandardInput.WriteLine(($message | ConvertTo-Json -Depth 12 -Compress))
        $Gateway.Process.StandardInput.Flush()
    }

    function Read-Response($Gateway, [int]$Id, [int]$TimeoutSec = 90) {
        $task = $Gateway.Process.StandardOutput.ReadLineAsync()
        if (-not $task.Wait([TimeSpan]::FromSeconds($TimeoutSec))) {
            throw "gateway $($Gateway.Process.Id) response timeout after ${TimeoutSec}s"
        }
        $line = $task.Result
        if ($null -eq $line) {
            $stderr = if ($Gateway.Process.HasExited) { $Gateway.Process.StandardError.ReadToEnd() } else { '' }
            throw "gateway closed before response: $stderr"
        }
        $object = $line | ConvertFrom-Json
        if ([string]$object.id -ne [string]$Id) { throw "response id mismatch: expected $Id got $($object.id)" }
        return $object
    }

    function Invoke-Rpc($Gateway, [string]$Method, $Params) {
        $Gateway.Seq++
        Send-Line $Gateway @{ jsonrpc = '2.0'; id = $Gateway.Seq; method = $Method; params = $Params }
        return Read-Response $Gateway $Gateway.Seq
    }

    # 发出请求但不等待响应：3 条请求同时在途，才能覆盖并发 accept + 并发会话路径。
    function Send-Tool($Gateway, [string]$Name, $Arguments) {
        $Gateway.Seq++
        Send-Line $Gateway @{ jsonrpc = '2.0'; id = $Gateway.Seq; method = 'tools/call'; params = @{ name = $Name; arguments = $Arguments } }
        return $Gateway.Seq
    }

    function Read-Tool($Gateway, [int]$Id, [int]$TimeoutSec = 90) {
        $response = Read-Response $Gateway $Id $TimeoutSec
        if ($response.error) { throw "rpc error: $($response.error | ConvertTo-Json -Compress)" }
        return $response.result
    }

    for ($i = 0; $i -lt 3; $i++) { $gateways += New-Gateway }

    $pending = @()
    for ($i = 0; $i -lt 3; $i++) {
        $pending += [pscustomobject]@{
            Gateway = $gateways[$i]
            Id      = (Send-Tool $gateways[$i] 'task_begin' @{ targetProcess = 'msedge'; targetTitle = $titles[$i] })
        }
    }
    $begins = @($pending | ForEach-Object { Read-Tool $_.Gateway $_.Id })
    for ($i = 0; $i -lt 3; $i++) {
        Assert "task_begin $($titles[$i])" ($begins[$i].isError -ne $true -and -not [string]::IsNullOrWhiteSpace($begins[$i].structuredContent.token)) ($begins[$i] | ConvertTo-Json -Depth 4 -Compress)
    }

    $tokens = @($begins | ForEach-Object { $_.structuredContent.token })
    $sessions = @($begins | ForEach-Object { $_.structuredContent.sessionId })
    $targets = @($begins | ForEach-Object { $_.structuredContent.target.handle })
    Assert 'tokens isolated' (@($tokens | Select-Object -Unique).Count -eq 3) "distinct=$(@($tokens | Select-Object -Unique).Count)"
    Assert 'sessions isolated' (@($sessions | Select-Object -Unique).Count -eq 3 -and ($sessions -notcontains $null)) "distinct=$(@($sessions | Select-Object -Unique).Count)"
    Assert 'targets isolated' (@($targets | Select-Object -Unique).Count -eq 3) "handles=$($targets -join ',')"

    $status = (Invoke-Rpc $gateways[0] 'tools/call' @{ name = 'desktop_status'; arguments = @{} }).result.structuredContent
    Assert 'status active=3' ($status.active -eq 3) "active=$($status.active) capacity=$($status.capacity)"
    Assert 'status capacity=3' ($status.capacity -eq 3) "capacity=$($status.capacity)"
    Assert 'status lists 3 sessions' (@($status.sessions).Count -eq 3) "sessions=$(@($status.sessions).Count)"
    Assert 'status reports isolation' (-not [string]::IsNullOrWhiteSpace($status.isolation.mode)) "mode=$($status.isolation.mode)"
    Assert 'status reports tiers' ($null -ne $status.tiers) ($status.tiers | ConvertTo-Json -Compress)

    # 只读 observe 并发：3 个会话同时抓屏/UIA，single-flight 只合并同一窗口，不互相排队。
    $pending = @()
    for ($i = 0; $i -lt 3; $i++) {
        $pending += [pscustomobject]@{
            Gateway = $gateways[$i]
            Id      = (Send-Tool $gateways[$i] 'observe' @{ token = $tokens[$i]; includeScreenshot = $false })
        }
    }
    $observes = @($pending | ForEach-Object { Read-Tool $_.Gateway $_.Id })
    for ($i = 0; $i -lt 3; $i++) {
        Assert "parallel observe $($titles[$i])" ($observes[$i].isError -ne $true -and $observes[$i].structuredContent.sessionId -eq $sessions[$i]) "backend=$($observes[$i].structuredContent.captureBackend)"
    }

    # 跨会话越权：把 A 的 token 交给 B 的 Gateway 必须拒绝，说明会话身份来自管道对端而不是 token。
    $foreign = Read-Response $gateways[1] (Send-Tool $gateways[1] 'observe' @{ token = $tokens[0] })
    Assert 'cross-session token rejected' ($foreign.result.isError -eq $true -and $foreign.result.structuredContent.error.code -eq 'unauthorized') ($foreign.result | ConvertTo-Json -Depth 4 -Compress)

    # 容量准入：第 4 个 Gateway 在确认弹窗之前就被拒绝，不会多出一个对话框。
    $gateways += New-Gateway
    $overflow = Read-Response $gateways[3] (Send-Tool $gateways[3] 'task_begin' @{ targetProcess = 'msedge'; targetTitle = $titles[0] })
    Assert 'fourth session rejected' ($overflow.result.isError -eq $true -and $overflow.result.structuredContent.error.code -eq 'capacity_exceeded') ($overflow.result | ConvertTo-Json -Depth 4 -Compress)

    $foreground = Get-ForegroundProcessName
    Assert 'confirmation did not steal focus' ($foreground -ne 'Swarm.Desktop') "foreground=$foreground"

    $pending = @()
    for ($i = 0; $i -lt 3; $i++) {
        $pending += [pscustomobject]@{
            Gateway = $gateways[$i]
            Id      = (Send-Tool $gateways[$i] 'task_end' @{ token = $tokens[$i] })
        }
    }
    $ends = @($pending | ForEach-Object { Read-Tool $_.Gateway $_.Id })
    for ($i = 0; $i -lt 3; $i++) {
        Assert "task_end $($titles[$i])" ($ends[$i].isError -ne $true -and $ends[$i].structuredContent.ok -eq $true) ($ends[$i].structuredContent | ConvertTo-Json -Compress)
    }

    $status = (Invoke-Rpc $gateways[0] 'tools/call' @{ name = 'desktop_status'; arguments = @{} }).result.structuredContent
    Assert 'status active=0 after end' ($status.active -eq 0) "active=$($status.active)"
}
finally {
    foreach ($gateway in $gateways) {
        if ($gateway -and $gateway.Process -and -not $gateway.Process.HasExited) { $gateway.Process.Kill() }
    }
    if ($desktop -and -not $desktop.HasExited) { $desktop.Kill() }
    if ($clicker) { Receive-Job $clicker -ErrorAction SilentlyContinue | Out-Null; Remove-Job $clicker -Force -ErrorAction SilentlyContinue }
    Clear-EdgeProfile -Path $profile
    foreach ($page in $pages) { Remove-Item -LiteralPath $page -Force -ErrorAction SilentlyContinue }
    if ($null -eq $previousCapacity) { Remove-Item Env:\SWARM_MAX_SESSIONS -ErrorAction SilentlyContinue } else { $env:SWARM_MAX_SESSIONS = $previousCapacity }
    Say 'cleanup done'
}
