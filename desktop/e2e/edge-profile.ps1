function Clear-EdgeProfile {
    param([Parameter(Mandatory = $true)][string]$Path)

    # Kill Edge processes bound to this profile first, then delete the directory.
    # Retries because file handles may lag behind process termination.
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -like "*$Path*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

        if (-not (Test-Path -LiteralPath $Path)) { return }
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $Path)) { return }
        Start-Sleep -Milliseconds 500
    }
}
