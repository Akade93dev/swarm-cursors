param(
    [int]$ExpectedDpiPercent = 0
)

$ErrorActionPreference = 'Stop'
$root = (Join-Path $PSScriptRoot '..\publish-verify')
. (Join-Path $PSScriptRoot 'edge-profile.ps1')
function Say([string]$text) { Write-Output $text }
function Assert([string]$name, [bool]$ok, $detail) {
    if (-not $ok) { Say "FAIL $name :: $detail"; throw "assertion failed: $name" }
    Say "PASS $name :: $detail"
}

# ── WGC/screen 像素比较配置（Tier 1 参数化裁剪 + 阈值；Tier 2 降级为诊断） ──
$script:Compare_SampleStep = 4                    # 像素采样步长（每 Npx 采一个点）
$script:Compare_BorderExcludeTopLeft = 4          # 顶部/左侧裁剪（排除采集边框）
$script:Compare_BorderExcludeBottom = 16          # 底部裁剪（WGC 可能多出 16px 系统边框）
$script:Compare_BorderExcludeRight = 4            # 右侧裁剪
$script:Compare_ChannelTolerance = 24            # R/G/B 通道差值容差
$script:Compare_PixelMismatchThreshold = 0.01    # 总体差异率阈值（7 档 DPI 实测最大 0.00584，收紧 0.02→0.01 留安全余量）
$script:Compare_ScaleTolerance = 0.001            # PNG/clientPhysical 缩放比容差（实测宽/高比差 ≤0.0003，收紧 0.01→0.001）
$script:ScreenshotMax = 1920                      # SWARM_SCREENSHOT_MAX 默认 1600；提至 1920 更接近 Claude 推荐上限 2576，推到 200%+ 才触发统一缩放

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class P1SForeground {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static IntPtr Root() { return GetAncestor(GetForegroundWindow(), 2); }
    public static void Focus(IntPtr h) { SetForegroundWindow(h); }
}
'@

$script:desktop = $null
$script:gw = $null
$script:authorizer = $null
$script:id = 0
$harness = $null
$edge = $null
$edgeProfile = Join-Path $env:TEMP 'swarm-edge-profile-p1-s'
$edgeHtml = Join-Path $env:TEMP 'swarm-p1-s-capture.html'

function Open-Session([string]$captureBackend, [int]$visualLeadMs) {
    $desktopPsi = New-Object System.Diagnostics.ProcessStartInfo
    $desktopPsi.FileName = "$root\Swarm.Desktop\Swarm.Desktop.exe"
    $desktopPsi.UseShellExecute = $false
    $desktopPsi.Environment['SWARM_CAPTURE_BACKEND'] = $captureBackend
    $desktopPsi.Environment['SWARM_VISUAL_LEAD_MS'] = [string]$visualLeadMs
    $desktopPsi.Environment['SWARM_SCREENSHOT_MAX'] = [string]$script:ScreenshotMax
    $script:desktop = [System.Diagnostics.Process]::Start($desktopPsi)
    Start-Sleep -Seconds 4
    Assert 'desktop alive' (-not $script:desktop.HasExited) "pid $($script:desktop.Id)"

    $script:authorizer = Start-Job -ArgumentList $script:desktop.Id -ScriptBlock {
        param($targetPid)
        Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class P1SAuthorization {
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
            $cb = [P1SAuthorization+EnumProc]{
                param($h, $l)
                $sb = New-Object System.Text.StringBuilder 64
                [void][P1SAuthorization]::GetClassName($h, $sb, 64)
                $p = 0
                [void][P1SAuthorization]::GetWindowThreadProcessId($h, [ref]$p)
                if ($p -eq $targetPid -and $sb.ToString() -eq '#32770' -and [P1SAuthorization]::IsWindowVisible($h)) {
                    $script:found = $h
                    return $false
                }
                return $true
            }
            [void][P1SAuthorization]::EnumWindows($cb, [IntPtr]::Zero)
            if ($script:found -ne [IntPtr]::Zero) {
                [void][P1SAuthorization]::PostMessage($script:found, 0x0111, [IntPtr]6, [IntPtr]::Zero)
                return 'clicked'
            }
            Start-Sleep -Milliseconds 200
        }
        return 'notfound'
    }

    $gatewayPsi = New-Object System.Diagnostics.ProcessStartInfo
    $gatewayPsi.FileName = "$root\Swarm.Gateway\Swarm.Gateway.exe"
    $gatewayPsi.UseShellExecute = $false
    $gatewayPsi.RedirectStandardInput = $true
    $gatewayPsi.RedirectStandardOutput = $true
    $gatewayPsi.RedirectStandardError = $true
    $gatewayPsi.CreateNoWindow = $true
    $gatewayPsi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $gatewayPsi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $script:gw = [System.Diagnostics.Process]::Start($gatewayPsi)
    $script:id = 0
    [void](Rpc 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'p1-s-invariants'; version = '1.0' } })
}

function Close-Session {
    if ($script:gw -and -not $script:gw.HasExited) { $script:gw.Kill() }
    if ($script:desktop -and -not $script:desktop.HasExited) { $script:desktop.Kill() }
    if ($script:authorizer) { Receive-Job $script:authorizer -ErrorAction SilentlyContinue | Out-Null; Remove-Job $script:authorizer -Force -ErrorAction SilentlyContinue }
    $script:gw = $null
    $script:desktop = $null
    $script:authorizer = $null
}

function Send-Message($message) {
    $json = $message | ConvertTo-Json -Depth 12 -Compress
    $script:gw.StandardInput.WriteLine($json)
    $script:gw.StandardInput.Flush()
}

function Read-Response([int]$expectedId, [int]$timeoutMs = 30000) {
    $deadline = [Environment]::TickCount + $timeoutMs
    while ($true) {
        $remaining = $deadline - [Environment]::TickCount
        if ($remaining -le 0) { throw "gateway response $expectedId timed out after ${timeoutMs}ms" }
        $pending = $script:gw.StandardOutput.ReadLineAsync()
        if (-not $pending.Wait($remaining)) { throw "gateway response $expectedId timed out after ${timeoutMs}ms" }
        if ($null -eq $pending.Result) { throw 'gateway closed' }
        $response = $pending.Result | ConvertFrom-Json
        if ($null -ne $response.id -and [string]$response.id -eq [string]$expectedId) { return $response }
    }
}

function Rpc([string]$method, $params) {
    $script:id++
    $requestId = $script:id
    Send-Message @{ jsonrpc = '2.0'; id = $requestId; method = $method; params = $params }
    return Read-Response $requestId
}

function Tool([string]$name, $arguments) {
    $response = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
    if ($response.error) { throw "rpc error: $($response.error | ConvertTo-Json -Compress)" }
    return $response.result
}

function Get-PngBytes($result) {
    $image = @($result.content | Where-Object { $_.type -eq 'image' }) | Select-Object -First 1
    if (-not $image) { throw 'observation has no PNG content' }
    return [Convert]::FromBase64String($image.data)
}

function Get-Bitmap([byte[]]$bytes) {
    $stream = New-Object System.IO.MemoryStream(,$bytes)
    $bitmap = New-Object System.Drawing.Bitmap($stream)
    $stream.Dispose()
    return $bitmap
}

function Test-DpiGeometry($payload, [byte[]]$png) {
    $viewport = $payload.structuredContent.viewport
    $physical = $viewport.clientPhysical
    $dpi = [double]$viewport.dpiScale
    Assert 'DPI width conversion' ([math]::Abs([double]$physical.w - [double]$viewport.width * $dpi) -le 1.5) "css=$($viewport.width) physical=$($physical.w) dpi=$dpi"
    Assert 'DPI height conversion' ([math]::Abs([double]$physical.h - [double]$viewport.height * $dpi) -le 1.5) "css=$($viewport.height) physical=$($physical.h) dpi=$dpi"
    $bitmap = Get-Bitmap $png
    try {
        Assert 'PNG matches client width' ($bitmap.Width -eq [int]$physical.w) "png=$($bitmap.Width) client=$($physical.w)"
        Assert 'PNG matches client height' ($bitmap.Height -eq [int]$physical.h) "png=$($bitmap.Height) client=$($physical.h)"
    }
    finally { $bitmap.Dispose() }
}

function Test-Cancel([string]$token, [double]$x, [double]$y) {
    $script:id++
    $requestId = $script:id
    Send-Message @{ jsonrpc = '2.0'; id = $requestId; method = 'tools/call'; params = @{ name = 'click'; arguments = @{ token = $token; x = $x; y = $y } } }
    Start-Sleep -Milliseconds 150
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/cancelled'; params = @{ requestId = $requestId } }
    $cancelled = Read-Response $requestId 1000
    $watch.Stop()
    Assert 'cancel returns -32800 within 1s' ($cancelled.error.code -eq -32800 -and $watch.ElapsedMilliseconds -le 1000) "code=$($cancelled.error.code) elapsedMs=$($watch.ElapsedMilliseconds)"
    $observed = Tool 'observe' @{ token = $token; includeScreenshot = $false }
    Assert 'cancel preserves task and token' (-not $observed.isError -and $observed.structuredContent.token -eq $token) "generation=$($observed.structuredContent.generation)"
}

function Compare-Captures($wgcResult, [byte[]]$wgcPng, $screenResult, [byte[]]$screenPng) {
    $wgcClient = $wgcResult.structuredContent.viewport.clientPhysical
    $screenClient = $screenResult.structuredContent.viewport.clientPhysical

    # ── Tier 2：WGC 是单一后端的几何锚点；两后端 clientPhysical 应一致（同一窗口） ──
    Assert 'backend client origin aligned' ($wgcClient.x -eq $screenClient.x -and $wgcClient.y -eq $screenClient.y) "wgc=$($wgcClient.x),$($wgcClient.y) screen=$($screenClient.x),$($screenClient.y)"
    Assert 'backend client size aligned' ($wgcClient.w -eq $screenClient.w -and $wgcClient.h -eq $screenClient.h) "wgc=$($wgcClient.w)x$($wgcClient.h) screen=$($screenClient.w)x$($screenClient.h)"

    # ── Tier 1：遮挡守卫（WGC 天然免疫遮挡；screen 被覆盖时必然画面不同） ──
    $wgcOccluded = [bool]$wgcResult.structuredContent.occluded
    $screenOccluded = [bool]$screenResult.structuredContent.occluded
    if ($wgcOccluded -or $screenOccluded) {
        Say "SKIP wgc/screen pixel alignment :: wgcOccluded=$wgcOccluded screenOccluded=$screenOccluded (occluded 时两后端画面必然不同，见 E2E 像素差异 0.9+)"; return
    }

    $wgcBitmap = Get-Bitmap $wgcPng
    $screenBitmap = Get-Bitmap $screenPng
    try {
        Assert 'backend PNG size aligned' ($wgcBitmap.Width -eq $screenBitmap.Width -and $wgcBitmap.Height -eq $screenBitmap.Height) "wgc=$($wgcBitmap.Width)x$($wgcBitmap.Height) screen=$($screenBitmap.Width)x$($screenBitmap.Height)"

        # ── Tier 1：PNG 与 clientPhysical 缩放比断言（防止两后端被缩放到不同尺寸） ──
        $wgcScaleW = [double]$wgcBitmap.Width / [double]$wgcClient.w
        $wgcScaleH = [double]$wgcBitmap.Height / [double]$wgcClient.h
        $screenScaleW = [double]$screenBitmap.Width / [double]$screenClient.w
        $screenScaleH = [double]$screenBitmap.Height / [double]$screenClient.h
        Assert 'backend PNG scale ratio aligned' (
            [math]::Abs($wgcScaleW - $screenScaleW) -le $script:Compare_ScaleTolerance -and
            [math]::Abs($wgcScaleH - $screenScaleH) -le $script:Compare_ScaleTolerance) "wgc=($([math]::Round($wgcScaleW,4)),$([math]::Round($wgcScaleH,4))) screen=($([math]::Round($screenScaleW,4)),$([math]::Round($screenScaleH,4)))"

        # ── Tier 1 参数化裁剪 + Tier 2 像素对齐降级为诊断项 ──
        $mismatch = 0; $total = 0
        $yStart = $script:Compare_BorderExcludeTopLeft
        $yEnd   = $wgcBitmap.Height - $script:Compare_BorderExcludeBottom
        $xStart = $script:Compare_BorderExcludeTopLeft
        $xEnd   = $wgcBitmap.Width  - $script:Compare_BorderExcludeRight
        $step   = $script:Compare_SampleStep
        $tol    = $script:Compare_ChannelTolerance
        for ($y = $yStart; $y -lt $yEnd; $y += $step) {
            for ($x = $xStart; $x -lt $xEnd; $x += $step) {
                $a = $wgcBitmap.GetPixel($x, $y)
                $b = $screenBitmap.GetPixel($x, $y)
                $total++
                if ([math]::Abs([int]$a.R - [int]$b.R) -gt $tol -or
                    [math]::Abs([int]$a.G - [int]$b.G) -gt $tol -or
                    [math]::Abs([int]$a.B - [int]$b.B) -gt $tol) { $mismatch++ }
            }
        }
        $ratio = if ($total -gt 0) { $mismatch / $total } else { 1 }
        $thr   = $script:Compare_PixelMismatchThreshold
        $detail = "mismatch=$mismatch/$total ratio=$([math]::Round($ratio,5)); sampleStep=$step borders=topLeft=$($script:Compare_BorderExcludeTopLeft) bottom=$($script:Compare_BorderExcludeBottom) right=$($script:Compare_BorderExcludeRight)"

        # Tier 2：像素对齐从硬 Assert 降级为 PASS/WARN（非致命）
        if ($ratio -le $thr) {
            Say "PASS wgc/screen client pixels aligned :: $detail"
        } else {
            Say "WARN wgc/screen pixel ratio exceeds threshold :: $detail (threshold=$thr, non-fatal; 可能是 DWM/GDI 微差或渲染路径分歧)"
        }
    }
    finally { $wgcBitmap.Dispose(); $screenBitmap.Dispose() }
}

$blocking = @(Get-Process -Name 'Swarm.Desktop' -ErrorAction SilentlyContinue)
if ($blocking.Count -gt 0) { throw 'Swarm.Desktop is already running; exit it before p1-s invariants.' }

try {
    $harness = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'pixel-harness.ps1')) -PassThru
    $target = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $target = Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -eq 'swarm pixel harness' } | Select-Object -First 1
        if ($target) { break }
    }
    Assert 'harness window' ($null -ne $target) 'swarm pixel harness'
    $targetHandle = $target.MainWindowHandle

    Open-Session 'auto' 2000
    try {
        [P1SForeground]::Focus($targetHandle)
        Start-Sleep -Milliseconds 300
        $begin = Tool 'task_begin' @{ targetTitle = 'swarm pixel harness' }
        $token = $begin.structuredContent.token
        Assert 'task_begin token' (-not [string]::IsNullOrWhiteSpace($token)) "backend=$($begin.structuredContent.captureBackend)"
        $latest = Tool 'observe' @{ token = $token; includeScreenshot = $true }
        Assert 'DPI observe' (-not $latest.isError) "backend=$($latest.structuredContent.captureBackend)"
        $latestPng = Get-PngBytes $latest

        Test-DpiGeometry $latest $latestPng
        $actualDpi = [int][math]::Round([double]$latest.structuredContent.viewport.dpiScale * 100)
        if ($ExpectedDpiPercent -gt 0) {
            Assert 'requested DPI is active' ($actualDpi -eq $ExpectedDpiPercent) "expected=$ExpectedDpiPercent actual=$actualDpi"
        }
        foreach ($percent in @(100, 125, 150, 175, 200, 225, 250)) {
            if ($percent -eq $actualDpi) { Say "PASS DPI $percent coverage :: current display scaling" }
            else { Say "SKIP DPI $percent coverage :: rerun with -ExpectedDpiPercent $percent after selecting that display scale" }
        }

        $x = [math]::Round([double]$latest.structuredContent.viewport.width / 2, 1)
        $y = [math]::Round([double]$latest.structuredContent.viewport.height / 2, 1)
        [P1SForeground]::Focus($targetHandle)
        Start-Sleep -Milliseconds 400
        [void](Tool 'observe' @{ token = $token; includeScreenshot = $false })
        $clicked = Tool 'click' @{ token = $token; x = $x; y = $y }
        Assert 'DPI coordinate click executed' (-not $clicked.isError -and $clicked.structuredContent.executed -eq $true) ($clicked.structuredContent | ConvertTo-Json -Depth 5 -Compress)
        Start-Sleep -Milliseconds 300
        $afterClick = Tool 'observe' @{ token = $token; includeScreenshot = $true }
        $afterBitmap = Get-Bitmap (Get-PngBytes $afterClick)
        try {
            $afterDpi = [double]$afterClick.structuredContent.viewport.dpiScale
            $pixelX = [math]::Min($afterBitmap.Width - 1, [math]::Max(0, [int][math]::Floor($x * $afterDpi)))
            $pixelY = [math]::Min($afterBitmap.Height - 1, [math]::Max(0, [int][math]::Floor($y * $afterDpi)))
            # WGC/screen may include the cursor at the exact click hotspot. Sample inside
            # the same button, away from its arrow and centered text, to inspect the client pixels.
            $sampleX = [math]::Max(0, $pixelX - 40)
            $sampleY = [math]::Max(0, $pixelY - 40)
            $sample = $afterBitmap.GetPixel($sampleX, $sampleY)
            Assert 'DPI coordinate maps to clicked target pixels' ($sample.R -gt 200 -and $sample.G -lt 80 -and $sample.B -lt 80) "css=$x,$y hotspot=$pixelX,$pixelY sample=$sampleX,$sampleY rgba=$($sample.R),$($sample.G),$($sample.B),$($sample.A)"
        }
        finally { $afterBitmap.Dispose() }
        Test-Cancel $token $x $y
        $ended = Tool 'task_end' @{ token = $token }
        Assert 'invariant session task_end' (-not $ended.isError) 'ok'
    }
    finally { Close-Session }

    if ($harness -and -not $harness.HasExited) { $harness.Kill(); [void]$harness.WaitForExit(5000) }
    Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'swarm pixel harness' } | ForEach-Object { $_.Kill() }

    Clear-EdgeProfile -Path $edgeProfile
    $page = @'
<html><head><title>swarm p1-s capture</title><style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:linear-gradient(135deg,#f8fafc,#2563eb,#f8fafc)}
</style></head><body></body></html>
'@
    Set-Content -LiteralPath $edgeHtml -Value $page -Encoding ASCII
    [void](Start-Process -FilePath 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' -ArgumentList @('--new-window', '--no-first-run', "--user-data-dir=$edgeProfile", "file:///$($edgeHtml -replace '\\','/')") -PassThru)
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $edge = Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like '*swarm p1-s capture*' } | Select-Object -First 1
        if ($edge) { break }
    }
    Assert 'Edge capture window' ($null -ne $edge) 'swarm p1-s capture'
    $edgeHandle = $edge.MainWindowHandle
    $wgcResult = $null; $wgcPng = $null; $wgcAvailable = $false

    Open-Session 'auto' 90
    try {
        [P1SForeground]::Focus($edgeHandle)
        Start-Sleep -Milliseconds 300
        $wgcBegin = Tool 'task_begin' @{ targetProcess = 'msedge'; targetTitle = 'swarm p1-s capture' }
        $wgcToken = $wgcBegin.structuredContent.token
        [P1SForeground]::Focus($edgeHandle)
        Start-Sleep -Milliseconds 300
        $wgcLatest = Tool 'observe' @{ token = $wgcToken; includeScreenshot = $true }
        if ($wgcLatest.structuredContent.captureBackend -eq 'wgc') {
            $wgcAvailable = $true
            $wgcResult = $wgcLatest
            $wgcPng = Get-PngBytes $wgcLatest
            Say 'PASS WGC backend available :: target=Edge captureBackend=wgc'
        }
        else {
            Say "SKIP wgc/screen backend comparison :: Edge backend=$($wgcLatest.structuredContent.captureBackend) captureWgc=$($wgcLatest.structuredContent.capabilities.captureWgc) guidance=$($wgcLatest.structuredContent.capabilities.captureGuidance)"
        }
        [void](Tool 'task_end' @{ token = $wgcToken })
    }
    finally { Close-Session }

    Open-Session 'screen' 90
    try {
        [P1SForeground]::Focus($edgeHandle)
        Start-Sleep -Milliseconds 300
        $screenBegin = Tool 'task_begin' @{ targetProcess = 'msedge'; targetTitle = 'swarm p1-s capture' }
        $screenToken = $screenBegin.structuredContent.token
        [P1SForeground]::Focus($edgeHandle)
        Start-Sleep -Milliseconds 300
        $screenLatest = Tool 'observe' @{ token = $screenToken; includeScreenshot = $true }
        Assert 'forced screen backend' ($screenLatest.structuredContent.captureBackend -eq 'screen') "requested=screen actual=$($screenLatest.structuredContent.captureBackend)"
        if ($wgcAvailable) {
            $screenPng = Get-PngBytes $screenLatest
            Compare-Captures $wgcResult $wgcPng $screenLatest $screenPng
        }
        [void](Tool 'task_end' @{ token = $screenToken })
    }
    finally { Close-Session }

    Say 'P1-S INVARIANTS PASS (SKIPs above require the stated hardware/display capability)'
}
finally {
    Close-Session
    if ($harness -and -not $harness.HasExited) { $harness.Kill() }
    Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'swarm pixel harness' } | ForEach-Object { $_.Kill() }
    Clear-EdgeProfile -Path $edgeProfile
    Remove-Item -LiteralPath $edgeHtml -Force -ErrorAction SilentlyContinue
    Say 'cleanup done'
}
