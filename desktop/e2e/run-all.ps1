$ErrorActionPreference = 'Stop'
$desktop = Split-Path $PSScriptRoot -Parent
$verifyRoot = Join-Path $desktop 'publish-verify'

# Each suite launches its own Desktop, which cannot coexist with another instance:
# the single-instance mutex and the IPC pipe are both machine-wide.
function Get-SwarmInstance {
    param([string[]]$Names)
    foreach ($name in $Names) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            $path = $null
            try { $path = $_.Path } catch { }
            [pscustomobject]@{
                Process  = $_
                Path     = $path
                IsVerify = ($path -and $path.StartsWith($verifyRoot, [StringComparison]::OrdinalIgnoreCase))
            }
        }
    }
}

$instances = @(Get-SwarmInstance -Names @('Swarm.Desktop', 'Swarm.Gateway'))
$leftovers = @($instances | Where-Object IsVerify)
foreach ($item in $leftovers) {
    $item.Process | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Output "stopped leftover: $($item.Process.ProcessName) pid $($item.Process.Id)"
}
if ($leftovers.Count -gt 0) { Start-Sleep -Seconds 1 }

$blocking = @(Get-SwarmInstance -Names @('Swarm.Desktop') | Where-Object { -not $_.IsVerify })
if ($blocking.Count -gt 0) {
    $detail = ($blocking | ForEach-Object { "pid $($_.Process.Id) at $($_.Path)" }) -join '; '
    throw "Swarm.Desktop is already running ($detail). Its single-instance mutex blocks e2e; exit that instance first."
}

Write-Output '== publish-verify =='
Push-Location $desktop
try {
    dotnet publish Swarm.Gateway\Swarm.Gateway.csproj -c Release -o publish-verify\Swarm.Gateway -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'gateway publish failed' }
    dotnet publish Swarm.Desktop\Swarm.Desktop.csproj -c Release -o publish-verify\Swarm.Desktop -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'desktop publish failed' }
}
finally {
    Pop-Location
}

$suites = @('p0-routing.ps1', 'p1-pixels.ps1', 'p2-overlay.ps1', 'edge-pixels.ps1', 'type-focus.ps1')
$failed = @()
foreach ($suite in $suites) {
    Write-Output "== $suite =="
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $suite)
    if ($LASTEXITCODE -ne 0) { $failed += $suite }
}
# Cleanup is part of the contract: report anything the suites failed to remove.
$profiles = @(Get-ChildItem $env:TEMP -Filter 'swarm-edge-profile-*' -Directory -ErrorAction SilentlyContinue)
$pages = @('swarm-e2e-edge.html', 'swarm-edge-pixels.html' |
    ForEach-Object { Join-Path $env:TEMP $_ } |
    Where-Object { Test-Path -LiteralPath $_ })
$strayEdge = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf('swarm-edge-profile', [StringComparison]::OrdinalIgnoreCase) -ge 0 })
if ($profiles.Count -gt 0 -or $pages.Count -gt 0 -or $strayEdge.Count -gt 0) {
    $names = @($profiles.Name) + @($pages | ForEach-Object { Split-Path $_ -Leaf })
    Write-Warning "e2e left behind: dirs/pages=[$($names -join ', ')] edge=$($strayEdge.Count)"
}

if ($failed.Count -gt 0) {
    throw "e2e failed: $($failed -join ', ')"
}
Write-Output '== all e2e passed =='
