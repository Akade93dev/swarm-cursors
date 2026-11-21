Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$script:count = 0
$form = New-Object System.Windows.Forms.Form
$form.Text = 'swarm click harness 0'
$form.Width = 520
$form.Height = 360
$form.StartPosition = 'CenterScreen'
$button = New-Object System.Windows.Forms.Button
$button.Text = 'click'
$button.Dock = 'Fill'
$button.BackColor = [System.Drawing.Color]::White
$button.Add_Click({
    $script:count++
    $form.Text = "swarm click harness $script:count"
})
$form.Controls.Add($button)
[System.Windows.Forms.Application]::Run($form)
