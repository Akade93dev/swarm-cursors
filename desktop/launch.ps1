<#
一键启动 Swarm 桌面端。

桌面端是 1x1 透明窗口 + 命名管道宿主，本身没有可见界面，
所以这个脚本负责「启动 + 确认通道就绪 + 给一个能看见的状态面板」。

用法：
  powershell -ExecutionPolicy Bypass -File launch.ps1            # 启动并显示状态面板
  powershell -ExecutionPolicy Bypass -File launch.ps1 -Headless  # 只启动并打印状态（供脚本/CI 用）
  powershell -ExecutionPolicy Bypass -File launch.ps1 -Rebuild   # 先重新 publish 再启动
#>
param(
    [switch]$Headless,
    [switch]$Rebuild,
    [int]$TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$PipeName = 'SwarmDesktop.V1'
$DesktopRoot = $PSScriptRoot

function Find-DesktopExe {
    $candidates = @(
        (Join-Path $DesktopRoot 'publish\Swarm.Desktop\Swarm.Desktop.exe'),
        (Join-Path $DesktopRoot 'publish-verify\Swarm.Desktop\Swarm.Desktop.exe'),
        (Join-Path $DesktopRoot 'Swarm.Desktop\bin\Release\net10.0-windows\Swarm.Desktop.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ }

    if (-not $candidates) { return $null }

    # 取最新的一份：用同目录 Swarm.Desktop.dll 的时间戳判断，apphost 时间戳不可靠
    $best = $null
    $bestTime = [datetime]::MinValue
    foreach ($exe in $candidates) {
        $dll = Join-Path (Split-Path -Parent $exe) 'Swarm.Desktop.dll'
        $stamp = if (Test-Path -LiteralPath $dll) { (Get-Item -LiteralPath $dll).LastWriteTime } else { (Get-Item -LiteralPath $exe).LastWriteTime }
        if ($stamp -gt $bestTime) { $bestTime = $stamp; $best = $exe }
    }
    return $best
}

function Test-DesktopPipe {
    $client = $null
    try {
        $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $client.Connect(400)
        return $true
    } catch {
        return $false
    } finally {
        if ($client) { $client.Dispose() }
    }
}

function Get-DesktopProcess {
    Get-Process -Name 'Swarm.Desktop' -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Get-LogTail([int]$Lines) {
    $dir = Join-Path $env:LOCALAPPDATA 'SwarmCursors\logs'
    $file = Join-Path $dir ("desktop-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))
    if (-not (Test-Path -LiteralPath $file)) { return '' }
    return ((Get-Content -LiteralPath $file -Tail $Lines) -join [Environment]::NewLine)
}

if ($Rebuild) {
    if (-not $Headless) { Write-Host '正在重新发布…' }
    & (Join-Path $DesktopRoot 'publish.ps1') | Out-Null
}

$exe = Find-DesktopExe
if (-not $exe) {
    $message = "找不到 Swarm.Desktop.exe。请先在 desktop 目录运行 publish.ps1，或用 -Rebuild 参数。"
    if ($Headless) { Write-Output "ERROR $message"; exit 2 }
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($message, 'Swarm 启动器', 'OK', 'Error')
    exit 2
}

$status = ''
$detail = ''
$started = $false

$running = Get-DesktopProcess
if ($running) {
    $status = '已在运行'
    $detail = "PID $($running.Id) · 通道 $(if (Test-DesktopPipe) { '就绪' } else { '启动中' })"
} else {
    $proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { break }
        if (Test-DesktopPipe) { $ready = $true; break }
        Start-Sleep -Milliseconds 250
    }

    if ($proc.HasExited) {
        $status = '启动失败'
        $detail = "进程已退出（exit $($proc.ExitCode)）。日志：`n" + (Get-LogTail 8)
        if ($Headless) { Write-Output "ERROR $status $detail"; exit 1 }
    } elseif ($ready) {
        $started = $true
        $status = '运行中'
        $detail = "PID $($proc.Id) · 命名管道 $PipeName 就绪"
    } else {
        $status = '通道未就绪'
        $detail = "进程 PID $($proc.Id) 在 $TimeoutSeconds 秒内未打开管道。日志：`n" + (Get-LogTail 8)
        if ($Headless) { Write-Output "ERROR $status $detail"; exit 1 }
    }
}

if ($Headless) {
    Write-Output "OK $status · $detail"
    exit 0
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Swarm 桌面助手'
$form.Size = New-Object System.Drawing.Size(460, 260)
$form.StartPosition = 'CenterScreen'
$form.TopMost = $true
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false
$form.BackColor = [System.Drawing.Color]::FromArgb(28, 30, 34)
$form.ForeColor = [System.Drawing.Color]::White

$title = New-Object System.Windows.Forms.Label
$title.Text = $status
$title.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 16, [System.Drawing.FontStyle]::Bold)
$title.ForeColor = if ($status -eq '运行中' -or $status -eq '已在运行') { [System.Drawing.Color]::FromArgb(94, 214, 141) } else { [System.Drawing.Color]::FromArgb(240, 120, 120) }
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point(20, 18)
$form.Controls.Add($title)

$hint = New-Object System.Windows.Forms.Label
$hint.Text = '桌面端没有可见窗口是正常的：它常驻后台，等待 Trae 通过 MCP 调用。'
$hint.AutoSize = $false
$hint.Size = New-Object System.Drawing.Size(410, 36)
$hint.Location = New-Object System.Drawing.Point(20, 56)
$hint.ForeColor = [System.Drawing.Color]::FromArgb(170, 175, 184)
$form.Controls.Add($hint)

$info = New-Object System.Windows.Forms.TextBox
$info.Multiline = $true
$info.ReadOnly = $true
$info.ScrollBars = 'Vertical'
$info.BorderStyle = 'FixedSingle'
$info.BackColor = [System.Drawing.Color]::FromArgb(20, 22, 26)
$info.ForeColor = [System.Drawing.Color]::FromArgb(210, 214, 220)
$info.Font = New-Object System.Drawing.Font('Consolas', 9)
$info.Size = New-Object System.Drawing.Size(410, 90)
$info.Location = New-Object System.Drawing.Point(20, 96)
$info.Text = $detail
$form.Controls.Add($info)

$closeButton = New-Object System.Windows.Forms.Button
$closeButton.Text = '关闭面板'
$closeButton.Size = New-Object System.Drawing.Size(110, 30)
$closeButton.Location = New-Object System.Drawing.Point(210, 196)
$closeButton.Add_Click({ $form.Close() })
$form.Controls.Add($closeButton)

$stopButton = New-Object System.Windows.Forms.Button
$stopButton.Text = '停止桌面端'
$stopButton.Size = New-Object System.Drawing.Size(110, 30)
$stopButton.Location = New-Object System.Drawing.Point(330, 196)
$stopButton.Add_Click({
    Get-Process -Name 'Swarm.Desktop' -ErrorAction SilentlyContinue | Stop-Process -Force
    $form.Close()
})
$form.Controls.Add($stopButton)

$form.Add_Shown({ $form.Activate() })
[void]$form.ShowDialog()
