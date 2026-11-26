#requires -version 5.1
# 空间盘点/数据搬家演示截图：先启用演示模式，所有写入按钮禁用，不扫描真实目录。
param(
    [string]$Executable = '',
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($Executable)) { $Executable = Join-Path $root 'dist\一键挪动.exe' }
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path $root 'docs\screenshots' }
if (-not (Test-Path -LiteralPath $Executable)) { throw "找不到正式程序: $Executable" }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
$assembly = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Executable).Path))
$type = $assembly.GetType('YijianNuodong.UI.DataMoveDialog', $true)
$form = [Activator]::CreateInstance($type)
try {
    $form.PreviewFillDemoData()
    $flags = [System.Reflection.BindingFlags]'Instance,NonPublic'
    if ($null -ne $type.GetField('scan', $flags).GetValue($form)) { throw '演示模式不应存在扫描快照' }
    foreach ($name in @('scanButton','moveButton','archiveButton','pipButton','pipUndoButton','undoButton')) {
        if ($type.GetField($name, $flags).GetValue($form).Enabled) { throw "演示写入按钮未禁用: $name" }
    }
    $installers = $type.GetField('installers', $flags).GetValue($form)
    $demoItem = New-Object System.Windows.Forms.ListViewItem -ArgumentList (,[string[]]@('ExampleSetup.exe', '128 MB', 'C:\Users\Demo\Downloads\ExampleSetup.exe', '演示候选；归档前自行确认，默认不勾选'))
    [void]$installers.Items.Add($demoItem)
    $form.ShowInTaskbar = $false
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object System.Drawing.Point(0, 0)
    $form.Show()
    [System.Windows.Forms.Application]::DoEvents()
    $tabs = $type.GetField('tabs', $flags).GetValue($form)
    $names = @('data-move-1-space.png','data-move-2-data.png','data-move-3-installers.png','data-move-4-pip.png')
    for ($i = 0; $i -lt $tabs.TabCount; $i++) {
        $tabs.SelectedIndex = $i
        [System.Windows.Forms.Application]::DoEvents()
        $image = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
        try {
            $form.DrawToBitmap($image, (New-Object System.Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
            $path = Join-Path $OutDir $names[$i]
            $image.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host ("已生成演示截图: " + $names[$i])
        }
        finally { $image.Dispose() }
    }
}
finally { $form.Close(); $form.Dispose() }
