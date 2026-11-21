$ErrorActionPreference = 'Stop'
$desktop = Split-Path $PSScriptRoot -Parent

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

$suites = @('p0-routing.ps1', 'p1-pixels.ps1', 'edge-pixels.ps1')
$failed = @()
foreach ($suite in $suites) {
    Write-Output "== $suite =="
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $suite)
    if ($LASTEXITCODE -ne 0) { $failed += $suite }
}
if ($failed.Count -gt 0) {
    throw "e2e failed: $($failed -join ', ')"
}
Write-Output '== all e2e passed =='
