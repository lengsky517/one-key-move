# 第 1 步界面探针：验证「软件说明区」尺寸 + 表头排序真的生效
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe-page1-info.ps1
param(
    [string]$AssemblyPath = 'dist\一键挪动.exe',
    [string]$ShotPath = 'docs\ui\page1-software-info.png'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
Set-Location $root

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $AssemblyPath)) { throw "找不到程序集：$AssemblyPath" }
$asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Resolve-Path $AssemblyPath).Path))

$pass = 0
$fail = 0
function Check($name, $cond, $detail) {
    if ($cond) { Write-Host ("  [PASS] " + $name); $script:pass++ }
    else { Write-Host ("  [FAIL] " + $name + "  -> " + $detail); $script:fail++ }
}

$formType = $asm.GetType('YijianNuodong.UI.MainForm')
$form = $formType.GetConstructor([type[]]@()).Invoke(@())
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(-4000, 0)
$form.Size = New-Object System.Drawing.Size(1180, 860)
$form.Show()
[System.Windows.Forms.Application]::DoEvents()

$bf = [Reflection.BindingFlags]'Instance,NonPublic,Public'

# 造一个「微信」，检查说明区能显示
$progType = $asm.GetType('YijianNuodong.Core.InstalledProgram')
if ($progType -eq $null) { throw '找不到 InstalledProgram 类型' }
$program = $progType.GetConstructor([type[]]@()).Invoke(@())
$program.DisplayName = '微信'
$program.Publisher = 'Tencent'
$program.Version = '3.9.11'
$program.InstallPath = 'C:\Program Files\Tencent\WeChat'
$program.SizeBytes = 524288000

$summary = $formType.GetField('_selectionSummary', $bf).GetValue($form)
$summary.GetType().GetMethod('SetProgram').Invoke($summary, @($program, $null)) | Out-Null
[System.Windows.Forms.Application]::DoEvents()

Check '说明区控件存在' ($summary -ne $null) '拿不到 _selectionSummary'
Check '说明区高度够放三行' ($summary.Height -ge 80) ("实际高度 " + $summary.Height)
Check '说明区宽度够放说明文字' ($summary.Width -ge 600) ("实际宽度 " + $summary.Width)

# 说明区不能和右侧按钮重叠
$uninstall = $formType.GetField('_uninstallButton', $bf).GetValue($form)
$next = $formType.GetField('_nextButton', $bf).GetValue($form)
$sumRight = $summary.Parent.Left + $summary.Right
$uninstallLeft = $uninstall.Parent.Left + $uninstall.Left
Check '说明区没有压到按钮上' ($summary.Right -le ($uninstall.Left - 4)) ("说明右边界 " + $summary.Right + "，卸载按钮左边界 " + $uninstall.Left)
Check '卸载按钮仍在窗口内' ($next.Right -le $form.ClientSize.Width) ("按钮右边界 " + $next.Right + "，窗口宽 " + $form.ClientSize.Width)

# 排序：注入 3 个软件后点表头
$list = $formType.GetField('_programList', $bf).GetValue($form)
$visible = $formType.GetField('_visiblePrograms', $bf).GetValue($form)
$visible.Clear()
foreach ($pair in @(@('Zeta 工具', 300, '1.10'), @('alpha 笔记', 100, '1.9'), @('Beta 压缩', 200, '2.0'))) {
    $item = $progType.GetConstructor([type[]]@()).Invoke(@())
    $item.DisplayName = $pair[0]
    $item.Version = $pair[2]
    $item.Publisher = 'Test'
    $item.InstallPath = 'D:\Software\' + $pair[0]
    $item.SizeBytes = [long]$pair[1]
    $visible.Add($item)
}
$formType.GetMethod('BindProgramList', $bf).Invoke($form, @()) | Out-Null
[System.Windows.Forms.Application]::DoEvents()

Check '默认顺序是扫描顺序（未排序）' ($list.Items[0].Text -eq 'Zeta 工具') ("实际第一行：" + $list.Items[0].Text)

# 反射调用 ColumnClick 事件参数会被 PSObject 包一层，这里直接设排序字段 + 重绑列表，
# 走的是同一条 _sortColumn/_sortAscending -> BindProgramList -> ComparePrograms 通路。
$fieldSort = $formType.GetField('_sortColumn', $bf)
$fieldAsc = $formType.GetField('_sortAscending', $bf)
$bind = $formType.GetMethod('BindProgramList', $bf)
$headers = $formType.GetMethod('UpdateColumnHeaders', $bf)

$fieldSort.SetValue($form, 0)
$fieldAsc.SetValue($form, $true)
$bind.Invoke($form, @()) | Out-Null
$headers.Invoke($form, @()) | Out-Null
[System.Windows.Forms.Application]::DoEvents()
Check '按名称升序：字母序排在第一' ($list.Items[0].Text -eq 'alpha 笔记') ("实际第一行：" + $list.Items[0].Text)
Check '表头出现升序标记' ($list.Columns[0].Text.Contains('▲')) ("表头文字：" + $list.Columns[0].Text)

$fieldAsc.SetValue($form, $false)
$bind.Invoke($form, @()) | Out-Null
$headers.Invoke($form, @()) | Out-Null
[System.Windows.Forms.Application]::DoEvents()
Check '按名称降序：Zeta 排第一' ($list.Items[0].Text -eq 'Zeta 工具') ("实际第一行：" + $list.Items[0].Text)
Check '表头出现降序标记' ($list.Columns[0].Text.Contains('▼')) ("表头文字：" + $list.Columns[0].Text)

$fieldSort.SetValue($form, 4)
$fieldAsc.SetValue($form, $false)
$bind.Invoke($form, @()) | Out-Null
[System.Windows.Forms.Application]::DoEvents()
Check '按体积降序：最大的排第一' ($list.Items[0].Text -eq 'Zeta 工具') ("实际第一行：" + $list.Items[0].Text)
Check '按体积排序列头有标记' ($list.Columns[4].Text.Contains('▼') -or $list.Columns[4].Text.Contains('▲')) ("表头文字：" + $list.Columns[4].Text)
# 截图给用户看（本机模型无法读图，人工核对用）
try {
    $parent = Split-Path -Parent $ShotPath
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $bmp = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
    $form.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
    $bmp.Save((Join-Path $root $ShotPath), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("  截图已保存：" + $ShotPath)
}
catch { Write-Host ("  截图失败：" + $_.Exception.Message) }

$form.Close()
$form.Dispose()

Write-Host ""
Write-Host ("通过 " + $pass + " 项 / 失败 " + $fail + " 项")
if ($fail -gt 0) { exit 1 }
