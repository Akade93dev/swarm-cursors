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
$button.Add_Click({
    if ($button.BackColor -eq [System.Drawing.Color]::White) {
        $button.BackColor = [System.Drawing.Color]::Red
    }
})
$form.Controls.Add($button)
[System.Windows.Forms.Application]::Run($form)
