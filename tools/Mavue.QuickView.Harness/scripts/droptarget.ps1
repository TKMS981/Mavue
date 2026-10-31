param([string]$Log)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.Text = "Mavue drop probe"
$form.Width = 700; $form.Height = 500
$form.AllowDrop = $true
$form.StartPosition = "Manual"
$form.Left = 100; $form.Top = 100
$form.Add_DragEnter({
    param($s, $e)
    $formats = $e.Data.GetFormats($false) -join ", "
    Add-Content -Path $Log -Value ("enter allowed=" + $e.AllowedEffect + " formats=" + $formats) -Encoding utf8
    if ($e.Data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) { $e.Effect = [System.Windows.Forms.DragDropEffects]::Copy } else { $e.Effect = [System.Windows.Forms.DragDropEffects]::Copy }
})
$form.Add_DragDrop({
    param($s, $e)
    $files = $e.Data.GetData([System.Windows.Forms.DataFormats]::FileDrop)
    Add-Content -Path $Log -Value ("drop effect=" + $e.Effect + " files=" + ($files -join "|")) -Encoding utf8
    $form.Close()
})
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 60000
$timer.Add_Tick({ $form.Close() })
$timer.Start()
Add-Content -Path $Log -Value "ready" -Encoding utf8
[void]$form.ShowDialog()
