param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $PSScriptRoot "publish")
)

$ErrorActionPreference = "Stop"
$desktopProject = Join-Path $PSScriptRoot "Swarm.Desktop/Swarm.Desktop.csproj"
$gatewayProject = Join-Path $PSScriptRoot "Swarm.Gateway/Swarm.Gateway.csproj"
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path ${env:ProgramFiles} "dotnet/dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw ".NET SDK was not found. Install it or add dotnet to PATH." }

& $dotnet publish $desktopProject --configuration $Configuration --output (Join-Path $OutputPath "Swarm.Desktop") --nologo
if ($LASTEXITCODE -ne 0) { throw "Swarm.Desktop publish failed with exit code $LASTEXITCODE." }

& $dotnet publish $gatewayProject --configuration $Configuration --output (Join-Path $OutputPath "Swarm.Gateway") --nologo
if ($LASTEXITCODE -ne 0) { throw "Swarm.Gateway publish failed with exit code $LASTEXITCODE." }

Write-Host "Desktop: $(Join-Path $OutputPath 'Swarm.Desktop/Swarm.Desktop.exe')"
Write-Host "Gateway: $(Join-Path $OutputPath 'Swarm.Gateway/Swarm.Gateway.exe')"
