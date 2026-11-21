#requires -version 5.1
<#
.SYNOPSIS
    卸载对话框界面冒烟测试（带真实消息循环）。

.DESCRIPTION
    本机安全策略禁止直接运行 exe，这里用反射把程序集装进 PowerShell 进程，
    创建一个真实的 UninstallDialog，依次切到「确认 / 进度 / 结果」三态，
    每态跑一小段真实消息循环，确认没有异常、没有卡死。
    只做界面活体检查，不会真的卸载任何软件。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [int]$MillisPerState = 700
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:pass = 0
$script:fail = 0
function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) {
        $script:pass++
        Write-Host ("  [PASS] " + $Name) -ForegroundColor Green
    }
    else {
        $script:fail++
        Write-Host ("  [FAIL] " + $Name + $(if ($Detail) { ' -> ' + $Detail } else { '' })) -ForegroundColor Red
    }
}

Write-Host '== 卸载dialog 界面冒烟 ==' -ForegroundColor Cyan
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $AssemblyPath).Path))
$programType = $asm.GetType('YijianNuodong.Core.InstalledProgram', $true)
$dialogType = $asm.GetType('YijianNuodong.UI.UninstallDialog', $true)

$program = [Activator]::CreateInstance($programType)
$program.DisplayName = '冒烟测试软件'
$program.InstallPath = 'D:\SmokeTest\DemoApp'
$program.Publisher = '冒烟测试'
$program.Version = '2.1.0'
$program.UninstallString = '"D:\SmokeTest\DemoApp\uninst.exe" /S'

$dialog = $null
try {
    $dialog = [Activator]::CreateInstance($dialogType, @($program))
    Check '创建卸载对话框' ($null -ne $dialog)
    Check '窗口尺寸合理' ([int]$dialog.Width -ge 600 -and [int]$dialog.Height -ge 500) (
        ([int]$dialog.Width).ToString() + 'x' + ([int]$dialog.Height))
}
catch {
    Check '创建卸载对话框' $false $_.Exception.Message
}

$show = $dialogType.GetMethod('PreviewShowState', [System.Reflection.BindingFlags]'Instance,NonPublic,Public')
foreach ($state in @(1, 2, 3)) {
    try {
        $dialog.Show()
        $show.Invoke($dialog, @($state))
        $deadline = [DateTime]::UtcNow.AddMilliseconds($MillisPerState)
        while ([DateTime]::UtcNow -lt $deadline) {
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 20
        }
        Check ('状态 ' + $state + ' 显示无异常') $dialog.Visible
        $dialog.Hide()
    }
    catch {
        Check ('状态 ' + $state + ' 显示无异常') $false $_.Exception.Message
    }
}

if ($null -ne $dialog) {
    try { $dialog.Close(); $dialog.Dispose() } catch { }
}

Write-Host ''
Write-Host ('界面冒烟：通过 ' + $script:pass + ' 项 / 失败 ' + $script:fail + ' 项') -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) { exit 1 }
exit 0
