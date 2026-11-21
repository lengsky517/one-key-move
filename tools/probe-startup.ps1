param(
    [string]$AssemblyPath = 'tests\build\OneKeyMove.preview.dll',
    [int]$MaxSeconds = 90
)

# 启动耗时探针：测量 MainForm 构造 / Show / 首次出现软件行 / 大小全部算完 的时间线。
# 只读，不改任何数据。用于定位「界面出得慢、出来时大小已经有了」到底是哪一段慢。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
if (-not [System.IO.Path]::IsPathRooted($AssemblyPath)) { $AssemblyPath = Join-Path $root $AssemblyPath }
if (-not (Test-Path -LiteralPath $AssemblyPath)) { throw "找不到程序集: $AssemblyPath" }

$bytes = [System.IO.File]::ReadAllBytes($AssemblyPath)
$asm = [System.Reflection.Assembly]::Load($bytes)
$formType = $asm.GetType('YijianNuodong.UI.MainForm', $true)
Write-Host ("程序集: {0}" -f $AssemblyPath)

$flags = [System.Reflection.BindingFlags]'Instance,NonPublic,Public'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$form = [System.Activator]::CreateInstance($formType)
$tCtor = $sw.ElapsedMilliseconds

$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(-4000, -4000)
$form.Show()
$tShow = $sw.ElapsedMilliseconds

$itemsProp = $formType.GetField('_programList', $flags)
$visibleField = $formType.GetField('_visiblePrograms', $flags)
$list = $itemsProp.GetValue($form)

$tFirstRow = -1
$tSizesDone = -1
$lastPending = -1
$samples = New-Object System.Collections.ArrayList

while ($sw.Elapsed.TotalSeconds -lt $MaxSeconds) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 60

    $count = 0
    if ($list -ne $null) { $count = $list.Items.Count }
    if ($tFirstRow -lt 0 -and $count -gt 0) {
        $tFirstRow = $sw.ElapsedMilliseconds
        [void]$samples.Add(("首次出现行 t={0}ms 行数={1}" -f $tFirstRow, $count))
    }
    if ($count -gt 0) {
        $pending = 0
        $unknown = 0
        for ($i = 0; $i -lt $count; $i++) {
            $text = $list.Items[$i].SubItems[4].Text
            if ($text -eq '计算中…') { $pending++ }
            elseif ($text -eq '未知') { $unknown++ }
        }
        if ($pending -ne $lastPending) {
            [void]$samples.Add(("t={0}ms 行数={1} 计算中={2} 未知={3}" -f $sw.ElapsedMilliseconds, $count, $pending, $unknown))
            $lastPending = $pending
        }
        if ($tFirstRow -ge 0 -and $pending -eq 0 -and $tSizesDone -lt 0) {
            $tSizesDone = $sw.ElapsedMilliseconds
            break
        }
    }
}

Write-Host ''
Write-Host '===== 时间线 ====='
Write-Host ("构造 MainForm      : {0} ms" -f $tCtor)
Write-Host ("Show() 返回        : {0} ms  (距构造 {1} ms)" -f $tShow, ($tShow - $tCtor))
if ($tFirstRow -ge 0) {
    Write-Host ("第一行软件出现     : {0} ms  (距 Show {1} ms)" -f $tFirstRow, ($tFirstRow - $tShow))
} else {
    Write-Host "第一行软件出现     : 超时未出现"
}
if ($tSizesDone -ge 0) {
    Write-Host ("大小全部算完       : {0} ms  (距第一行 {1} ms)" -f $tSizesDone, ($tSizesDone - $tFirstRow))
} else {
    Write-Host ("大小全部算完       : 超时（>{0}s）" -f $MaxSeconds)
}
Write-Host ''
Write-Host '===== 采样 ====='
$samples | ForEach-Object { Write-Host $_ }

$list = $null
$form.Close()
$form.Dispose()
[System.Windows.Forms.Application]::DoEvents()
Write-Host ''
if ($tFirstRow -lt 0) { Write-Host 'RESULT: 未见列表行'; exit 1 }
Write-Host ("RESULT: 界面可交互 {0} ms 后才有行；大小耗时 {1} ms" -f $tFirstRow, ($(if ($tSizesDone -ge 0) { $tSizesDone - $tFirstRow } else { -1 }))) 
exit 0
