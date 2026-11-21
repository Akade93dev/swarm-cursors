function Clear-EdgeProfile {
    param([Parameter(Mandatory = $true)][string]$Path)

    # Kill Edge processes bound to this profile first, then delete the directory.
    # Retries because file handles may lag behind process termination.
    # Match the path literally: -like would treat brackets in the path as wildcards.
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($Path, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

        if (-not (Test-Path -LiteralPath $Path)) { return }
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $Path)) { return }
        Start-Sleep -Milliseconds 500
    }

    if (Test-Path -LiteralPath $Path) {
        Write-Warning "Edge profile not removed after 3 attempts: $Path"
    }
}
