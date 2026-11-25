#requires -version 5.1
# C 盘清理窗口演示截图（只使用 PreviewFillDemoData，不扫描或删除真实文件）。
param(
    [string]$Dll,
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($Dll)) { $Dll = Join-Path $root 'tests\build\OneKeyMove.preview.dll' }
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path $root 'docs\ui' }
if (-not (Test-Path $Dll)) { throw "找不到 DLL: $Dll" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Dll).Path))
$type = $asm.GetType('YijianNuodong.UI.CleanupDialog', $true)
$ctor = $type.GetConstructor([Type[]]@())
$form = $ctor.Invoke(@())
try {
    $form.PreviewFillDemoData()
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object System.Drawing.Point(0, 0)
    $form.ShowInTaskbar = $false
    $form.TopMost = $true
    $form.Show()
    [System.Windows.Forms.Application]::DoEvents()
    $deadline = (Get-Date).AddMilliseconds(1200)
    while ((Get-Date) -lt $deadline) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 40 }
    $bmp = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::White)
    $form.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
    $g.Dispose()
    $path = Join-Path $OutDir 'cleanup-1-confirm.png'
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("已生成: " + $path + " (" + $form.Width + " x " + $form.Height + ")") -ForegroundColor Green
}
finally { $form.Close(); $form.Dispose() }
