param(
    [string]$Configuration = 'Release',
    [string]$PublishRoot = (Join-Path $PSScriptRoot 'publish-smoke'),
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
function Say([string]$text) { Write-Output $text }
function Assert([string]$name, [bool]$ok, $detail) {
    if (-not $ok) { Say "FAIL $name :: $detail"; throw "assertion failed: $name" }
    Say "PASS $name :: $detail"
}

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Configuration $Configuration -OutputPath $PublishRoot
    if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }
}

$gatewayDir = Join-Path $PublishRoot 'Swarm.Gateway'
$desktopDir = Join-Path $PublishRoot 'Swarm.Desktop'
$gatewayExe = Join-Path $gatewayDir 'Swarm.Gateway.exe'
$desktopExe = Join-Path $desktopDir 'Swarm.Desktop.exe'
$gatewayFiles = @(Get-ChildItem -LiteralPath $gatewayDir -File -ErrorAction SilentlyContinue)
Assert 'gateway self-contained single file' ($gatewayFiles.Count -eq 1 -and $gatewayFiles[0].Name -eq 'Swarm.Gateway.exe') "files=$(($gatewayFiles.Name) -join ',')"
Assert 'desktop carries coreclr' (Test-Path -LiteralPath (Join-Path $desktopDir 'coreclr.dll')) $desktopDir
Assert 'desktop carries hostfxr' (Test-Path -LiteralPath (Join-Path $desktopDir 'hostfxr.dll')) $desktopDir
Assert 'desktop is not single file' (Test-Path -LiteralPath (Join-Path $desktopDir 'Swarm.Desktop.dll')) $desktopDir

$blocking = @(Get-Process -Name 'Swarm.Desktop' -ErrorAction SilentlyContinue)
if ($blocking.Count -gt 0) {
    $detail = ($blocking | ForEach-Object { "pid $($_.Id)" }) -join '; '
    throw "Swarm.Desktop is already running ($detail); exit it before smoke."
}

# A self-contained app must not resolve the machine's shared .NET installation.
$originalPath = $env:PATH
$originalDotnetRoot = $env:DOTNET_ROOT
$originalDotnetRootX64 = $env:DOTNET_ROOT_X64
$originalMultiLevel = $env:DOTNET_MULTILEVEL_LOOKUP
$missingRuntime = Join-Path $env:TEMP "swarm-no-dotnet-$PID"
$env:DOTNET_ROOT = $missingRuntime
$env:DOTNET_ROOT_X64 = $missingRuntime
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:PATH = (($originalPath -split ';' | Where-Object { $_ -and $_ -notmatch '[\\/]dotnet[\\/]?$' }) -join ';')

$desktop = $null; $gw = $null; $harness = $null; $authorizer = $null
try {
    $harness = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'e2e\pixel-harness.ps1')) -PassThru
    $target = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $target = Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -eq 'swarm pixel harness' } | Select-Object -First 1
        if ($target) { break }
    }
    Assert 'harness window' ($null -ne $target) 'swarm pixel harness'

    $desktop = Start-Process -FilePath $desktopExe -PassThru
    Start-Sleep -Seconds 5
    Assert 'desktop cold start without shared runtime' (-not $desktop.HasExited) "pid $($desktop.Id) exit=$($desktop.ExitCode)"

    $authorizer = Start-Job -ArgumentList $desktop.Id -ScriptBlock {
        param($targetPid)
        Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SmokeAuthorization {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
'@
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            $script:found = [IntPtr]::Zero
            $cb = [SmokeAuthorization+EnumProc]{
                param($h, $l)
                $sb = New-Object System.Text.StringBuilder 64
                [void][SmokeAuthorization]::GetClassName($h, $sb, 64)
                $p = 0
                [void][SmokeAuthorization]::GetWindowThreadProcessId($h, [ref]$p)
                if ($p -eq $targetPid -and $sb.ToString() -eq '#32770' -and [SmokeAuthorization]::IsWindowVisible($h)) {
                    $script:found = $h
                    return $false
                }
                return $true
            }
            [void][SmokeAuthorization]::EnumWindows($cb, [IntPtr]::Zero)
            if ($script:found -ne [IntPtr]::Zero) {
                [void][SmokeAuthorization]::PostMessage($script:found, 0x0111, [IntPtr]6, [IntPtr]::Zero)
                return 'clicked'
            }
            Start-Sleep -Milliseconds 200
        }
        return 'notfound'
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $gatewayExe
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $gw = [System.Diagnostics.Process]::Start($psi)

    $script:id = 0
    function Read-GatewayLine([int]$timeoutMs = 20000) {
        $pending = $gw.StandardOutput.ReadLineAsync()
        if (-not $pending.Wait($timeoutMs)) { throw "gateway response timed out after ${timeoutMs}ms" }
        if ($null -eq $pending.Result) { throw "gateway closed; exit=$($gw.ExitCode)" }
        return $pending.Result
    }
    function Rpc([string]$method, $params) {
        $script:id++
        $msg = @{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 12 -Compress
        $gw.StandardInput.WriteLine($msg)
        $gw.StandardInput.Flush()
        return ((Read-GatewayLine) | ConvertFrom-Json)
    }
    function Tool([string]$name, $arguments) {
        $response = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
        if ($response.error) { throw "rpc error: $($response.error | ConvertTo-Json -Compress)" }
        return $response.result
    }

    $initialized = Rpc 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'cold-smoke'; version = '1.0' } }
    Assert 'initialize' ($null -ne $initialized.result.serverInfo) ($initialized.result.serverInfo | ConvertTo-Json -Compress)
    $listed = Rpc 'tools/list' @{}
    Assert 'tools/list' (@($listed.result.tools).Count -eq 10) "tools=$(@($listed.result.tools).Count)"

    $begin = Tool 'task_begin' @{ targetTitle = 'swarm pixel harness' }
    $token = $begin.structuredContent.token
    Assert 'task_begin' (-not $begin.isError -and -not [string]::IsNullOrWhiteSpace($token)) ($begin.structuredContent.target | ConvertTo-Json -Compress)
    $observed = Tool 'observe' @{ token = $token; includeScreenshot = $false }
    Assert 'observe' (-not $observed.isError -and $observed.structuredContent.viewport.width -gt 0) "backend=$($observed.structuredContent.captureBackend) viewport=$($observed.structuredContent.viewport.width)x$($observed.structuredContent.viewport.height)"
    $ended = Tool 'task_end' @{ token = $token }
    Assert 'task_end' (-not $ended.isError -and $ended.structuredContent.ok -eq $true) ($ended.structuredContent | ConvertTo-Json -Compress)
    Say 'SMOKE PASS: initialize -> tools/list -> task_begin -> observe -> task_end'
}
finally {
    if ($gw -and -not $gw.HasExited) { $gw.Kill() }
    if ($desktop -and -not $desktop.HasExited) { $desktop.Kill() }
    if ($harness -and -not $harness.HasExited) { $harness.Kill() }
    Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'swarm pixel harness' } | ForEach-Object { $_.Kill() }
    if ($authorizer) { Receive-Job $authorizer -ErrorAction SilentlyContinue | Out-Null; Remove-Job $authorizer -Force -ErrorAction SilentlyContinue }
    $env:PATH = $originalPath
    $env:DOTNET_ROOT = $originalDotnetRoot
    $env:DOTNET_ROOT_X64 = $originalDotnetRootX64
    $env:DOTNET_MULTILEVEL_LOOKUP = $originalMultiLevel
    Say 'cleanup done'
}
