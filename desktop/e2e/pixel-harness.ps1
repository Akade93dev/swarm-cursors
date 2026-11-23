Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PixelHarnessDpi {
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    public static void PerMonitorV2() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
'@
[PixelHarnessDpi]::PerMonitorV2()
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.Text = 'swarm pixel harness'
$form.Width = 640
$form.Height = 400
$form.StartPosition = 'CenterScreen'
$button = New-Object System.Windows.Forms.Button
$button.Text = 'change'
$button.Dock = 'Fill'
$button.BackColor = [System.Drawing.Color]::White
$marker = New-Object System.Windows.Forms.Panel
$marker.Dock = 'Top'
$marker.Height = 48
$marker.BackColor = [System.Drawing.Color]::LightGreen
$accent = New-Object System.Windows.Forms.Panel
$accent.Dock = 'Top'
$accent.Height = 48
$accent.BackColor = [System.Drawing.Color]::LightBlue
$button.Controls.Add($accent)
$button.Controls.Add($marker)
$button.Add_Click({
    if ($button.BackColor -eq [System.Drawing.Color]::White) {
        $button.BackColor = [System.Drawing.Color]::Red
    }
})
$form.Controls.Add($button)
[System.Windows.Forms.Application]::Run($form)
