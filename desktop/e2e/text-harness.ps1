Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.Text = 'swarm text harness'
$form.Width = 420
$form.Height = 280
$form.StartPosition = 'CenterScreen'
# first 单行、初始焦点在这里；second 多行、没有焦点。
# type_text 指定 second 的 elementId 时，必须把文本写进 second 而不是当前焦点的 first。
$first = New-Object System.Windows.Forms.TextBox
$first.Name = 'first'
$first.Location = New-Object System.Drawing.Point(20, 20)
$first.Width = 340
$second = New-Object System.Windows.Forms.TextBox
$second.Name = 'second'
$second.Multiline = $true
$second.Location = New-Object System.Drawing.Point(20, 60)
$second.Size = New-Object System.Drawing.Size(340, 140)
$form.Controls.Add($first)
$form.Controls.Add($second)
$form.ActiveControl = $first
[System.Windows.Forms.Application]::Run($form)
