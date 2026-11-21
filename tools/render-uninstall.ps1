# ============================================================================
#  UninstallDialog 预览渲染（开发自检用）
#  通过反射实例化卸载对话框，用 PreviewShowState 灌入演示数据后 DrawToBitmap。
#  用法: powershell -ExecutionPolicy Bypass -STA -File tools\render-uninstall.ps1 -Dll dist\OneKeyMove.preview.dll -OutDir docs\ui
# ============================================================================
param(
    [string]$Dll,
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($Dll)) { $Dll = Join-Path $root 'dist\OneKeyMove.preview.dll' }
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path $root 'docs\ui' }
if (-not (Test-Path $Dll)) { Write-Host "找不到 DLL: $Dll" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($Dll))
$formType = $asm.GetType('YijianNuodong.UI.UninstallDialog')
if ($null -eq $formType) { Write-Host '找不到 UninstallDialog 类型' -ForegroundColor Red; exit 1 }

$ctor = $formType.GetConstructor([Type[]]@($asm.GetType('YijianNuodong.Core.InstalledProgram')))
$previewMethod = $formType.GetMethod('PreviewShowState', [System.Reflection.BindingFlags]'Instance,NonPublic,Public')

$states = @(
    @{ Index = 1; File = 'uninstall-1-confirm.png'; Label = '确认页' },
    @{ Index = 2; File = 'uninstall-2-progress.png'; Label = '进度页' },
    @{ Index = 3; File = 'uninstall-3-result.png'; Label = '结果页' }
)

foreach ($s in $states) {
    $form = $ctor.Invoke(@($null))
    $previewMethod.Invoke($form, @($s.Index)) | Out-Null

    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object System.Drawing.Point(0, 0)
    $form.ShowInTaskbar = $false
    $form.TopMost = $true
    $form.Show()
    [System.Windows.Forms.Application]::DoEvents()
    $deadline = (Get-Date).AddMilliseconds(1200)
    while ((Get-Date) -lt $deadline) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 40 }

    $w = $form.Width
    $h = $form.Height
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::White)
    $form.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle(0, 0, $w, $h)))
    $g.Dispose()

    $path = Join-Path $OutDir $s.File
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    $form.Close()
    $form.Dispose()
    Write-Host ("已生成: " + $path + "  (" + $s.Label + ", " + $w + " x " + $h + ")") -ForegroundColor Green
}
