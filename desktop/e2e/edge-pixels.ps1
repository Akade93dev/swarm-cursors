$ErrorActionPreference = 'Stop'
$root = (Join-Path $PSScriptRoot '..\publish-verify')
. (Join-Path $PSScriptRoot 'edge-profile.ps1')
function Say([string]$text) { Write-Output $text }

$html = Join-Path $env:TEMP 'swarm-edge-pixels.html'
$page = '<html><head><title>swarm pixels</title><style>html,body{margin:0;height:100%}button{width:100%;height:100%;font-size:48px}</style></head><body><button onclick="this.style.background=''#c33'';this.textContent=''changed''">swarm-pixels</button></body></html>'
Set-Content -LiteralPath $html -Value $page -Encoding ASCII
$profile = Join-Path $env:TEMP 'swarm-edge-profile-px'
Clear-EdgeProfile -Path $profile

$desktop = $null; $gw = $null; $clicker = $null
try {
    [void](Start-Process -FilePath 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' -ArgumentList @('--new-window', '--no-first-run', "--user-data-dir=$profile", "file:///$($html -replace '\\','/')") -PassThru)
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $edge = Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like '*swarm pixels*' } | Select-Object -First 1
        if ($edge) { break }
    }
    if (-not $edge) { throw 'edge window not found' }

    $desktop = Start-Process -FilePath "$root\Swarm.Desktop\Swarm.Desktop.exe" -PassThru
    Start-Sleep -Seconds 3

    $desktopPid = $desktop.Id
    $clicker = Start-Job -ArgumentList $desktopPid -ScriptBlock {
        param($targetPid)
        Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class DlgClick3 {
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
            $cb = [DlgClick3+EnumProc]{
                param($h, $l)
                $sb = New-Object System.Text.StringBuilder 64
                [void][DlgClick3]::GetClassName($h, $sb, 64)
                $p = 0
                [void][DlgClick3]::GetWindowThreadProcessId($h, [ref]$p)
                if ($p -eq $targetPid -and $sb.ToString() -eq '#32770' -and [DlgClick3]::IsWindowVisible($h)) {
                    $script:found = $h
                    return $false
                }
                return $true
            }
            [void][DlgClick3]::EnumWindows($cb, [IntPtr]::Zero)
            if ($script:found -ne [IntPtr]::Zero) {
                [void][DlgClick3]::PostMessage($script:found, 0x0111, [IntPtr]6, [IntPtr]::Zero)
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
        return ($gw.StandardOutput.ReadLine() | ConvertFrom-Json)
    }
    function Tool([string]$name, $arguments) {
        $r = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
        return $r.result
    }

    [void](Rpc 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'e2e'; version = '1.0' } })
    $begin = Tool 'task_begin' @{ targetProcess = 'msedge'; targetTitle = 'swarm pixels' }
    Say "BEGIN $($begin.structuredContent.capabilities | ConvertTo-Json -Compress)"
    $token = $begin.structuredContent.token
    $vw = [double]$begin.structuredContent.viewport.width
    $vh = [double]$begin.structuredContent.viewport.height
    $x = [math]::Round($vw / 2, 1)
    $y = [math]::Round($vh * 0.8, 1)
    $before = Tool 'observe' @{ token = $token }
    Say "BEFORE occluded=$($before.structuredContent.occluded) captureBackend=$($before.structuredContent.captureBackend)"
    $focus = Tool 'focus' @{ token = $token }
    Start-Sleep -Milliseconds 600
    $click = Tool 'click' @{ token = $token; x = $x; y = $y; verify = 'pixels' }
    Say "CLICK $($click.structuredContent | ConvertTo-Json -Depth 5 -Compress)"
    $observe = Tool 'observe' @{ token = $token }
    Say "OBSERVE occluded=$($observe.structuredContent.occluded) captureBackend=$($observe.structuredContent.captureBackend)"
    [void](Tool 'task_end' @{ token = $token })
}
finally {
    if ($gw -and -not $gw.HasExited) { $gw.Kill() }
    if ($desktop -and -not $desktop.HasExited) { $desktop.Kill() }
    if ($clicker) { Receive-Job $clicker -ErrorAction SilentlyContinue | Out-Null; Remove-Job $clicker -Force -ErrorAction SilentlyContinue }
    Clear-EdgeProfile -Path $profile
    Remove-Item -LiteralPath $html -Force -ErrorAction SilentlyContinue
    Say 'cleanup done'
}
