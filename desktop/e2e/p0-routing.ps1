$ErrorActionPreference = 'Stop'
$root = (Join-Path $PSScriptRoot '..\publish-verify')
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
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
'@

$html = Join-Path $env:TEMP 'swarm-e2e-edge.html'
$page = '<html><head><title>swarm e2e</title><style>html,body{margin:0;height:100%}button{width:100%;height:100%;font-size:48px}</style></head><body><button id="b" onclick="document.title=''swarm-clicked'';this.textContent=''swarm-clicked''">swarm-button</button></body></html>'
Set-Content -LiteralPath $html -Value $page -Encoding ASCII
$profile = Join-Path $env:TEMP 'swarm-edge-profile-p0'

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

    $ended = Tool 'task_end' @{ token = $token }
    Assert 'task_end' ($ended.isError -ne $true) 'ok'
}
finally {
    if ($gw -and -not $gw.HasExited) { $gw.Kill() }
    if ($desktop -and -not $desktop.HasExited) { $desktop.Kill() }
    if ($notepad -and -not $notepad.HasExited) { $notepad.Kill() }
    if ($front -and -not $front.HasExited) { $front.Kill() }
    if ($clicker) { Receive-Job $clicker -ErrorAction SilentlyContinue | Out-Null; Remove-Job $clicker -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*$profile*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Say 'cleanup done'
}
