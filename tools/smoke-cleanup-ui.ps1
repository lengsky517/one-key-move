#requires -version 5.1
<#
.SYNOPSIS
    C 盘清理窗口界面冒烟测试。
.DESCRIPTION
    通过反射加载程序集并使用演示数据，只验证窗口布局和三态消息循环，不扫描、打开或删除真实文件。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [int]$Millis = 500
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$pass = 0
$fail = 0
function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) { $script:pass++; Write-Host ('  [PASS] ' + $Name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ('  [FAIL] ' + $Name + $(if ($Detail) { ' -> ' + $Detail } else { '' })) -ForegroundColor Red }
}
Write-Host '== C 盘清理窗口界面冒烟 ==' -ForegroundColor Cyan
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $AssemblyPath).Path))
$type = $asm.GetType('YijianNuodong.UI.CleanupDialog', $true)
$dialog = $null
try {
    $ctor = $type.GetConstructor([Type[]]@())
    $dialog = $ctor.Invoke(@())
    Check '创建清理窗口' ($null -ne $dialog)
    Check '窗口尺寸合理' ([int]$dialog.Width -ge 900 -and [int]$dialog.Height -ge 650) (([int]$dialog.Width).ToString() + 'x' + ([int]$dialog.Height))
    $dialog.PreviewFillDemoData()
    $listField = $type.GetField('_fileList', [Reflection.BindingFlags]'Instance,NonPublic')
    $cleanField = $type.GetField('_cleanButton', [Reflection.BindingFlags]'Instance,NonPublic')
    $scanField = $type.GetField('_scan', [Reflection.BindingFlags]'Instance,NonPublic')
    $list = $listField.GetValue($dialog)
    $clean = $cleanField.GetValue($dialog)
    $scan = $scanField.GetValue($dialog)
    Check '演示数据显示六个条目' ([int]$list.Items.Count -eq 6) ('实际 ' + [int]$list.Items.Count)
    Check '演示模式禁止清理按钮' (-not [bool]$clean.Enabled)
    Check '演示数据不带可执行候选' ($null -eq $scan.Candidates)
    $dialog.Show()
    $deadline = [DateTime]::UtcNow.AddMilliseconds($Millis)
    while ([DateTime]::UtcNow -lt $deadline) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
    Check '消息循环运行无异常' $dialog.Visible
}
catch { Check '清理窗口冒烟过程' $false $_.Exception.Message }
finally { if ($null -ne $dialog) { try { $dialog.Close(); $dialog.Dispose() } catch { } } }
Write-Host ''
Write-Host ('界面冒烟：通过 ' + $pass + ' 项 / 失败 ' + $fail + ' 项') -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
if ($fail -gt 0) { exit 1 }
exit 0
