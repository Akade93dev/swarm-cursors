$ErrorActionPreference = 'Stop'
$root = (Join-Path $PSScriptRoot '..\publish-verify')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PixelE2eNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
function Say([string]$text) { Write-Output $text }
function Assert([string]$name, [bool]$ok, $detail) {
    if (-not $ok) { Say "FAIL $name :: $detail"; throw "assertion failed: $name" }
    Say "PASS $name :: $detail"
}

$desktop = $null; $gw = $null; $harness = $null; $clicker = $null
try {
    $harness = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'pixel-harness.ps1')) -PassThru
    $target = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $target = Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -eq 'swarm pixel harness' } | Select-Object -First 1
        if ($target) { break }
    }
    Assert 'harness window' ($null -ne $target) 'swarm pixel harness'

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
public static class DlgClick2 {
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
            $cb = [DlgClick2+EnumProc]{
                param($h, $l)
                $sb = New-Object System.Text.StringBuilder 64
                [void][DlgClick2]::GetClassName($h, $sb, 64)
                $p = 0
                [void][DlgClick2]::GetWindowThreadProcessId($h, [ref]$p)
                if ($p -eq $targetPid -and $sb.ToString() -eq '#32770' -and [DlgClick2]::IsWindowVisible($h)) {
                    $script:found = $h
                    return $false
                }
                return $true
            }
            [void][DlgClick2]::EnumWindows($cb, [IntPtr]::Zero)
            if ($script:found -ne [IntPtr]::Zero) {
                [void][DlgClick2]::PostMessage($script:found, 0x0111, [IntPtr]6, [IntPtr]::Zero)
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

    $begin = Tool 'task_begin' @{ targetTitle = 'swarm pixel harness' }
    $token = $begin.structuredContent.token
    Assert 'task_begin token' (-not [string]::IsNullOrWhiteSpace($token)) "token length $($token.Length)"
    Assert 'capabilities messageReliable' ($begin.structuredContent.capabilities.messageReliable -eq $true) ($begin.structuredContent.capabilities | ConvertTo-Json -Compress)
    [void][PixelE2eNative]::SetForegroundWindow($target.MainWindowHandle)
    Start-Sleep -Milliseconds 400
    $vw = [double]$begin.structuredContent.viewport.width
    $vh = [double]$begin.structuredContent.viewport.height
    $x = [math]::Round($vw / 2, 1)
    $y = [math]::Round($vh / 2, 1)

    $first = Tool 'click' @{ token = $token; x = $x; y = $y; verify = 'pixels' }
    Assert 'first click executed' ($first.isError -ne $true -and $first.structuredContent.executed -eq $true) ($first.structuredContent | ConvertTo-Json -Depth 5 -Compress)
    Assert 'first click pixel verified' ($first.structuredContent.verified -eq $true -and $first.structuredContent.evidence -eq 'pixels') "verified=$($first.structuredContent.verified) evidence=$($first.structuredContent.evidence) note=$($first.structuredContent.note)"

    $second = Tool 'click' @{ token = $token; x = $x; y = $y; verify = 'pixels' }
    Assert 'second click executed' ($second.isError -ne $true -and $second.structuredContent.executed -eq $true) 'ok'
    Assert 'second click not verified' ($second.structuredContent.verified -eq $false -and $second.structuredContent.evidence -eq 'none') "verified=$($second.structuredContent.verified) evidence=$($second.structuredContent.evidence) note=$($second.structuredContent.note)"

    $plain = Tool 'click' @{ token = $token; x = $x; y = $y }
    Assert 'plain click has no pixel cost' ($plain.structuredContent.evidence -eq $null) "evidence=$($plain.structuredContent.evidence) tier=$($plain.structuredContent.tier)"

    $bad = Tool 'click' @{ token = $token; x = $x; y = $y; verify = 'magic' }
    Assert 'invalid verify rejected' ($bad.isError -eq $true -and $bad.structuredContent.error.code -eq 'invalid_params') "code=$($bad.structuredContent.error.code)"

    $ended = Tool 'task_end' @{ token = $token }
    Assert 'task_end' ($ended.isError -ne $true) 'ok'
}
finally {
    if ($gw -and -not $gw.HasExited) { $gw.Kill() }
    if ($desktop -and -not $desktop.HasExited) { $desktop.Kill() }
    if ($harness -and -not $harness.HasExited) { $harness.Kill() }
    Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'swarm pixel harness' } | ForEach-Object { $_.Kill() }
    if ($clicker) { Receive-Job $clicker -ErrorAction SilentlyContinue | Out-Null; Remove-Job $clicker -Force -ErrorAction SilentlyContinue }
    Say 'cleanup done'
}
