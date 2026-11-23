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

$desktopOutput = Join-Path $OutputPath "Swarm.Desktop"
$gatewayOutput = Join-Path $OutputPath "Swarm.Gateway"
if (Test-Path $desktopOutput) { Remove-Item -LiteralPath $desktopOutput -Recurse -Force }
if (Test-Path $gatewayOutput) { Remove-Item -LiteralPath $gatewayOutput -Recurse -Force }

# WPF stays a regular self-contained folder; the stdio Gateway is the single-file entry point.
& $dotnet publish $desktopProject --configuration $Configuration --runtime win-x64 --self-contained true --output $desktopOutput --nologo -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "Swarm.Desktop publish failed with exit code $LASTEXITCODE." }

& $dotnet publish $gatewayProject --configuration $Configuration --runtime win-x64 --self-contained true --output $gatewayOutput --nologo -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Swarm.Gateway publish failed with exit code $LASTEXITCODE." }

Write-Host "Desktop: $(Join-Path $OutputPath 'Swarm.Desktop/Swarm.Desktop.exe')"
Write-Host "Gateway: $(Join-Path $OutputPath 'Swarm.Gateway/Swarm.Gateway.exe')"
