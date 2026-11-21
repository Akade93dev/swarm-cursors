$ErrorActionPreference = 'Stop'
$root = (Join-Path $PSScriptRoot '..\publish-verify')
function Say([string]$text) { Write-Output $text }
function Assert([string]$name, [bool]$ok, $detail) {
    if (-not $ok) { Say "FAIL $name :: $detail"; throw "assertion failed: $name" }
    Say "PASS $name :: $detail"
}

# 读取另一个进程里 EDIT 控件的文本（GetWindowText 跨进程不可用，必须走 WM_GETTEXT）。
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class EditProbe {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, StringBuilder l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO {
        public int cbSize; public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    private static List<Tuple<int, IntPtr, string>> Collect(IntPtr parent) {
        var found = new List<Tuple<int, IntPtr, string>>();
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
            var cls = new StringBuilder(128);
            GetClassName(h, cls, 128);
            if (cls.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0) {
                RECT r;
                if (GetWindowRect(h, out r)) {
                    var sb = new StringBuilder(1024);
                    SendMessage(h, 0x000D, (IntPtr)1024, sb);
                    found.Add(Tuple.Create(r.Top, h, sb.ToString()));
                }
            }
            return true;
        }, IntPtr.Zero);
        found.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return found;
    }
    public static List<string> Texts(IntPtr parent) {
        var result = new List<string>();
        foreach (var item in Collect(parent)) result.Add(item.Item3);
        return result;
    }
    public static List<IntPtr> Handles(IntPtr parent) {
        var result = new List<IntPtr>();
        foreach (var item in Collect(parent)) result.Add(item.Item2);
        return result;
    }
    public static IntPtr FocusOf(IntPtr window) {
        uint pid;
        var tid = GetWindowThreadProcessId(window, out pid);
        var info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        return GetGUIThreadInfo(tid, ref info) ? info.hwndFocus : IntPtr.Zero;
    }
}
'@

$desktop = $null; $gw = $null; $harness = $null; $authorizer = $null
try {
    $harness = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'text-harness.ps1')) -PassThru
    $target = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $target = Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -eq 'swarm text harness' } | Select-Object -First 1
        if ($target) { break }
    }
    Assert 'harness window' ($null -ne $target) 'swarm text harness'
    $harnessHandle = $target.MainWindowHandle

    $desktop = Start-Process -FilePath "$root\Swarm.Desktop\Swarm.Desktop.exe" -PassThru
    Start-Sleep -Seconds 3
    Assert 'desktop alive' (-not $desktop.HasExited) "pid $($desktop.Id)"

    $desktopPid = $desktop.Id
    $authorizer = Start-Job -ArgumentList $desktopPid -ScriptBlock {
        param($targetPid)
        Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class DlgAuth {
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
            $cb = [DlgAuth+EnumProc]{
                param($h, $l)
                $sb = New-Object System.Text.StringBuilder 64
                [void][DlgAuth]::GetClassName($h, $sb, 64)
                $p = 0
                [void][DlgAuth]::GetWindowThreadProcessId($h, [ref]$p)
                if ($p -eq $targetPid -and $sb.ToString() -eq '#32770' -and [DlgAuth]::IsWindowVisible($h)) {
                    $script:found = $h
                    return $false
                }
                return $true
            }
            [void][DlgAuth]::EnumWindows($cb, [IntPtr]::Zero)
            if ($script:found -ne [IntPtr]::Zero) {
                [void][DlgAuth]::PostMessage($script:found, 0x0111, [IntPtr]6, [IntPtr]::Zero)
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
        $line = $gw.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "gateway closed: $($gw.StandardError.ReadToEnd())" }
        return $line | ConvertFrom-Json
    }
    function Tool([string]$name, $arguments) {
        $response = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
        if ($response.error) { throw "rpc error: $($response.error | ConvertTo-Json -Compress)" }
        return $response.result
    }

    [void](Rpc 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'e2e'; version = '1.0' } })

    $begin = Tool 'task_begin' @{ targetTitle = 'swarm text harness' }
    $token = $begin.structuredContent.token
    Assert 'task_begin token' (-not [string]::IsNullOrWhiteSpace($token)) "token length $($token.Length)"

    $focused = Tool 'focus' @{ token = $token }
    Assert 'focus executed' ($focused.isError -ne $true) ($focused.structuredContent | ConvertTo-Json -Compress)

    $observed = Tool 'observe' @{ token = $token }
    $edits = @($observed.structuredContent.elements | Where-Object { $_.type -eq 'Edit' -or $_.type -eq 'Document' } | Sort-Object { $_.bounds.y })
    Assert 'two text fields observed' ($edits.Count -eq 2) "count=$($edits.Count) types=$(($observed.structuredContent.elements | ForEach-Object { $_.type }) -join ',')"
    $firstBox = $edits[0]
    $secondBox = $edits[1]
    Assert 'second field is below first' ($secondBox.bounds.y -gt $firstBox.bounds.y) "first.y=$($firstBox.bounds.y) second.y=$($secondBox.bounds.y)"

    # 前提：键盘焦点确实在上面的输入框，否则“文本进入下面输入框”可能是空过。
    $editHandles = [EditProbe]::Handles($harnessHandle)
    $focusHandle = [EditProbe]::FocusOf($harnessHandle)
    Assert 'keyboard focus starts on first field' ($focusHandle -eq $editHandles[0]) "focus=0x$($focusHandle.ToString('x')) first=0x$($editHandles[0].ToString('x')) second=0x$($editHandles[1].ToString('x'))"

    $text = 'target-field'
    $typed = Tool 'type_text' @{ token = $token; elementId = $secondBox.id; text = $text }
    Assert 'type_text executed' ($typed.isError -ne $true -and $typed.structuredContent.executed -eq $true) ($typed.structuredContent | ConvertTo-Json -Depth 5 -Compress)
    Say "INFO type tier=$($typed.structuredContent.tier) evidence=$($typed.structuredContent.evidence) note=$($typed.structuredContent.note)"

    # SendInput 注入后目标应用是异步处理字符的，读回前留出处理时间。
    $texts = $null
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Milliseconds 200
        $texts = [EditProbe]::Texts($harnessHandle)
        if ($texts.Count -eq 2 -and $texts[1].Trim() -eq $text) { break }
    }
    Assert 'both fields readable' ($texts.Count -eq 2) "count=$($texts.Count) texts=$(($texts | ForEach-Object { $_.Trim() }) -join ' | ')"
    Assert 'target field received text' ($texts[1].Trim() -eq $text) "second='$($texts[1].Trim())'"
    Assert 'focused field untouched' ($texts[0].Trim() -eq '') "first='$($texts[0].Trim())'"

    $ended = Tool 'task_end' @{ token = $token }
    Assert 'task_end' ($ended.isError -ne $true) 'ok'
}
finally {
    if ($gw -and -not $gw.HasExited) { $gw.Kill() }
    if ($desktop -and -not $desktop.HasExited) { $desktop.Kill() }
    if ($harness -and -not $harness.HasExited) { $harness.Kill() }
    Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'swarm text harness' } | ForEach-Object { $_.Kill() }
    if ($authorizer) { Receive-Job $authorizer -ErrorAction SilentlyContinue | Out-Null; Remove-Job $authorizer -Force -ErrorAction SilentlyContinue }
    Say 'cleanup done'
}
