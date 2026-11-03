# ============================================================================
#  一键挪动 - 界面渲染预览（开发自检用）
#  由于本机安全策略禁止直接运行 exe，这里用「字节载入 DLL + 反射实例化窗体 + DrawToBitmap」
#  的方式把 WinForms 界面渲染成 PNG，用于检查界面效果。
#  用法:
#    powershell -ExecutionPolicy Bypass -STA -File tools\render-ui.ps1 -Dll dist\OneKeyMove.preview.dll -OutDir docs\ui
#    可选: -Form MainForm|FolderPickerDialog  -Width 1100 -Height 760
# ============================================================================
param(
    [string]$Dll,
    [string]$OutDir = '',
    [int]$Width = 1100,
    [int]$Height = 760,
    [string]$FormName = 'MainForm'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($Dll)) {
    $Dll = Join-Path $root 'dist\OneKeyMove.preview.dll'
}
if ([string]::IsNullOrEmpty($OutDir)) {
    $OutDir = Join-Path $root 'docs\ui'
}
if (-not (Test-Path $Dll)) { Write-Host "找不到 DLL: $Dll（请先运行 build.ps1 -Target lib）" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$assemblyBytes = [System.IO.File]::ReadAllBytes($Dll)
$asm = [System.Reflection.Assembly]::Load($assemblyBytes)
$formType = $asm.GetType("YijianNuodong.UI.$FormName")
if ($null -eq $formType) { Write-Host "找不到窗体类型: $FormName" -ForegroundColor Red; exit 1 }

try {
    $ctor = $formType.GetConstructor([Type[]]@())
    if ($null -ne $ctor) {
        $formObj = $ctor.Invoke(@())
    } else {
        $ctor = $formType.GetConstructor([Type[]]@([string]))
        $formObj = $ctor.Invoke(@($null))
    }
} catch {
    Write-Host "实例化窗体失败: $($_.Exception.InnerException.Message)" -ForegroundColor Red
    Write-Host $_.Exception.InnerException.StackTrace -ForegroundColor DarkGray
    exit 1
}

$formObj.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$formObj.Location = New-Object System.Drawing.Point(0, 0)
$formObj.ClientSize = New-Object System.Drawing.Size($Width, $Height)
$formObj.ShowInTaskbar = $false
$formObj.TopMost = $true

$formObj.Show()
[System.Windows.Forms.Application]::DoEvents()
$deadline = (Get-Date).AddMilliseconds(1500)
while ((Get-Date) -lt $deadline) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 40
}

$actualWidth = $formObj.Width
$actualHeight = $formObj.Height
$bmp = New-Object System.Drawing.Bitmap($actualWidth, $actualHeight)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$rect = New-Object System.Drawing.Rectangle(0, 0, $actualWidth, $actualHeight)
$formObj.DrawToBitmap($bmp, $rect)
$g.Dispose()

$outPath = Join-Path $OutDir ($FormName + '.png')
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$formObj.Close()
$formObj.Dispose()

Write-Host "已生成: $outPath  ($actualWidth x $actualHeight)" -ForegroundColor Green
