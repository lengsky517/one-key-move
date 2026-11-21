# ============================================================================
#  一键挪动 - 列表占用大小后台统计 冒烟测试
#  验证：列表里未知大小的行会显示「计算中…」，随后自动变成真实大小（无需点选）
#  用法: powershell -ExecutionPolicy Bypass -STA -File tools\smoke-size-scan.ps1
# ============================================================================

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outDir = Join-Path $root 'tests\build'
Write-Host '==> 编译引擎/界面' -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -Target lib -OutDir $outDir -Quiet
if ($LASTEXITCODE -ne 0) { Write-Host '编译失败' -ForegroundColor Red; exit 1 }

$dll = Join-Path $outDir 'OneKeyMove.preview.dll'
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($dll))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$pass = 0; $fail = 0
function Ok([string]$n) { $script:pass++; Write-Host "  [PASS] $n" -ForegroundColor Green }
function Bad([string]$n, [string]$d) { $script:fail++; Write-Host "  [FAIL] $n -> $d" -ForegroundColor Red }
function Assert([string]$n, [bool]$c, [string]$d) { if ($c) { Ok $n } else { Bad $n $d } }

# ---------- 造两个假程序目录，大小已知 ----------
$base = Join-Path $env:TEMP ('okm-size-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$appA = Join-Path $base 'SizeAppA'
$appB = Join-Path $base 'SizeAppB'
New-Item -ItemType Directory -Force -Path $appA, (Join-Path $appA 'sub'), $appB | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $appA 'a.bin'), (New-Object byte[] 150000))
[System.IO.File]::WriteAllBytes((Join-Path $appA 'sub\b.bin'), (New-Object byte[] 50000))
[System.IO.File]::WriteAllBytes((Join-Path $appB 'c.bin'), (New-Object byte[] 24000))

$formType = $asm.GetType('YijianNuodong.UI.MainForm')
$progType = $asm.GetType('YijianNuodong.Core.InstalledProgram')
if ($null -eq $formType -or $null -eq $progType) { Write-Host '找不到类型' -ForegroundColor Red; exit 1 }

$flags = [System.Reflection.BindingFlags]'Instance,NonPublic,Public'
$sourceType = $asm.GetType('YijianNuodong.Core.ProgramSource')

function New-FakeProgram([string]$name, [string]$path) {
    $p = [Activator]::CreateInstance($progType)
    $progType.GetProperty('DisplayName').SetValue($p, $name, $null)
    $progType.GetProperty('InstallPath').SetValue($p, $path, $null)
    $progType.GetProperty('SizeBytes').SetValue($p, [long]-1, $null)
    $progType.GetProperty('Version').SetValue($p, '1.0', $null)
    $progType.GetProperty('Publisher').SetValue($p, 'OKM Test', $null)
    $sp = $progType.GetProperty('Source'); if ($sourceType -and $sp -and $sp.CanWrite) { $sp.SetValue($p, [Enum]::Parse($sourceType, 'Registry'), $null) }
    return $p
}

[System.Windows.Forms.Application]::EnableVisualStyles()
$script:uiError = $null
[System.Windows.Forms.Application]::add_ThreadException({
    param($sender, $e)
    $script:uiError = $e.Exception.Message
    [System.Windows.Forms.Application]::Exit()
})

$form = $formType.GetConstructor([Type[]]@()).Invoke(@())
$form.ShowInTaskbar = $false
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(-3000, -3000)
$form.Show()
[System.Windows.Forms.Application]::DoEvents()

$visibleField = $formType.GetField('_visiblePrograms', $flags)
$listField = $formType.GetField('_programList', $flags)
$bindMethod = $formType.GetMethod('BindProgramList', $flags)
if (-not $visibleField -or -not $listField -or -not $bindMethod) {
    Write-Host '  [FAIL] 找不到私有成员 _visiblePrograms/_programList/BindProgramList' -ForegroundColor Red
    $form.Dispose(); exit 1
}

function Inject-And-Bind() {
    $list = $visibleField.GetValue($form)
    $list.Clear()
    $list.Add((New-FakeProgram 'SizeAppA' $appA))
    $list.Add((New-FakeProgram 'SizeAppB' $appB))
    $bindMethod.Invoke($form, @()) | Out-Null
    # 注意：这里不能 DoEvents —— 目录很小时后台线程会立刻算完，
    # 消息一泵就把「计算中…」换成了真实数值，占位态就测不到了。
}

# 真实扫描可能在后台跑完并重绑列表，这里按住它：注入后立刻检查
$listView = $listField.GetValue($form)
Inject-And-Bind

$texts = @()
foreach ($i in $listView.Items) { $texts += $i.SubItems[4].Text }
Assert '未知大小的行先显示「计算中…」' (($texts -join ',') -notmatch '^\s*$' -and ($texts -contains '计算中…')) ('实际: ' + ($texts -join ' | '))

# ---------- 泵消息等待后台统计完成 ----------
$deadline = (Get-Date).AddSeconds(25)
$done = $false
while ((Get-Date) -lt $deadline) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 80
    $count = $listView.Items.Count
    if ($count -ne 2) { Inject-And-Bind; $count = $listView.Items.Count }
    $all = @()
    foreach ($i in $listView.Items) { $all += $i.SubItems[4].Text }
    if ($all.Count -eq 2 -and ($all -notcontains '计算中…') -and ($all -notcontains '未知')) { $done = $true; break }
}
$final = @()
foreach ($i in $listView.Items) { $final += ($i.Text + '=' + $i.SubItems[4].Text) }
Assert '无需点选，行内大小自动从「计算中…」变成真实数值' $done ('实际: ' + ($final -join ' | '))
Assert '统计结果与磁盘实际占用一致' (($final -join ' ') -match 'SizeAppA=195(\.\d+)? KB' -and ($final -join ' ') -match 'SizeAppB=23\.4 KB') ($final -join ' | ')
Assert '界面线程无未处理异常' ([string]::IsNullOrEmpty($script:uiError)) $script:uiError

$form.Close()
$form.Dispose()
try { Remove-Item $base -Recurse -Force -ErrorAction SilentlyContinue } catch { }

Write-Host ''
Write-Host ("占用大小统计冒烟：通过 $pass 项 / 失败 $fail 项")
if ($fail -gt 0) { exit 1 }
