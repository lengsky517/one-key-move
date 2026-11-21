#requires -version 5.1
<#
.SYNOPSIS
    卸载功能端到端验证（真删真删，不是只调 API 断言）。

.DESCRIPTION
    在工作区里造一个假的「便携程序」目录（没有注册表卸载项），然后：
      1. Precheck 必须判定为手工清理（ManualOnly）且可继续；
      2. 不带确认标记执行 - 必须被拒绝，目录一个字节都不能少；
      3. 带确认标记执行 - 必须真的删掉目录、统计出释放空间、写出卸载记录。
    全过程只碰工作区里的临时目录，不动系统里任何真实安装的程序。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [string]$WorkRoot = ''
)

$ErrorActionPreference = 'Stop'

if ($WorkRoot.Length -eq 0) {
    $WorkRoot = Join-Path $env:TEMP 'okm-e2e-uninstall'
}

$pass = 0
$fail = 0
$failures = New-Object System.Collections.Generic.List[string]

function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) {
        $script:pass++
        Write-Host ("  [PASS] " + $Name) -ForegroundColor Green
    }
    else {
        $script:fail++
        $script:failures.Add($Name + $(if ($Detail.Length -gt 0) { ' -> ' + $Detail } else { '' }))
        Write-Host ("  [FAIL] " + $Name + $(if ($Detail.Length -gt 0) { ' -> ' + $Detail } else { '' })) -ForegroundColor Red
    }
}

Write-Host '== 卸载端到端 ==' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $AssemblyPath)) {
    Write-Host ("找不到程序集：" + $AssemblyPath) -ForegroundColor Red
    exit 2
}
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $AssemblyPath).Path))

$engineType = $asm.GetType('YijianNuodong.Core.UninstallEngine', $true)
$programType = $asm.GetType('YijianNuodong.Core.InstalledProgram', $true)
$requestType = $asm.GetType('YijianNuodong.Core.UninstallRequest', $true)
$methodType = $asm.GetType('YijianNuodong.Core.UninstallMethod', $true)

# ---------- 造一个假程序目录 ----------
if (Test-Path -LiteralPath $WorkRoot) {
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$appDir = Join-Path $WorkRoot 'FakeApp'
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $appDir 'bin') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $appDir 'data') -Force | Out-Null
$bytes = New-Object byte[] 32768
(New-Object System.Random 20261121).NextBytes($bytes)
$expectedFiles = 0
$expectedBytes = 0
foreach ($rel in @('FakeApp.exe', 'bin\core.dll', 'bin\helper.dll', 'data\a.dat', 'data\b.dat', 'readme.txt')) {
    $full = Join-Path $appDir $rel
    [System.IO.File]::WriteAllBytes($full, $bytes)
    $expectedFiles++
    $expectedBytes += $bytes.Length
}
Write-Host ("  造好假程序：" + $appDir + "（" + $expectedFiles + " 个文件，" + $expectedBytes + " 字节）")

# ---------- 构造 InstalledProgram ----------
$program = [Activator]::CreateInstance($programType)
$program.DisplayName = 'E2E测试程序'
$program.InstallPath = $appDir
$program.UninstallString = ''
$program.Publisher = 'E2E'
$program.Version = '1.0.0'

# ---------- 1. 预检 ----------
$target = $engineType.GetMethod('Precheck').Invoke($null, @($program))
Check '预检：判定为手工清理' ($target.Method.ToString() -eq 'ManualOnly') ("实际 " + $target.Method)
Check '预检：可继续' ([bool]$target.CanProceed) ((@($target.Errors) -join '；'))
Check '预检：文件数统计正确' ([int]$target.FileCount -eq $expectedFiles) ("实际 " + $target.FileCount)
Check '预检：不是受保护目标' (-not [bool]$target.IsProtected)
Check '预检：给出「无官方卸载程序」警告' (($target.Warnings -join ' ') -match '没有.*卸载程序|手工删除')

# ---------- 2. 不带确认执行：必须拒绝 ----------
$req = [Activator]::CreateInstance($requestType)
$req.Target = $target
$req.ConfirmManualDelete = $false
$req.CloseRunningPrograms = $false
$req.RemoveLeftovers = $true
$req.RemoveUserData = $false
$req.CreateRestorePoint = $false
$req.BackupRegistry = $false
$req.TimeoutSeconds = 60

$result1 = $engineType.GetMethod('Execute').Invoke($null, @($req, $null, $null))
$dirStillThere = Test-Path -LiteralPath $appDir
$filesStillThere = 0
if ($dirStillThere) { $filesStillThere = @(Get-ChildItem -LiteralPath $appDir -Recurse -File -ErrorAction SilentlyContinue).Count }

Check '未确认：返回失败' (-not [bool]$result1.Success) ("FinalStage=" + $result1.FinalStage)
Check '未确认：安装目录还在' $dirStillThere
Check '未确认：文件一个没少' ($filesStillThere -eq $expectedFiles) ("实际 " + $filesStillThere)
Check '未确认：给出确认提示' ([bool](($result1.ErrorMessage + ' ' + ($result1.Summary)) -match '确认'))
Check '未确认：写了失败诊断日志' (-not [string]::IsNullOrEmpty($result1.FailureLogPath) -and (Test-Path -LiteralPath $result1.FailureLogPath)) ([string]$result1.FailureLogPath)

# ---------- 3. 带确认执行：必须真删 ----------
$req2 = [Activator]::CreateInstance($requestType)
$req2.Target = $target
$req2.ConfirmManualDelete = $true
$req2.CloseRunningPrograms = $false
$req2.RemoveLeftovers = $true
$req2.RemoveUserData = $false
$req2.CreateRestorePoint = $false
$req2.BackupRegistry = $false
$req2.TimeoutSeconds = 60

$result2 = $engineType.GetMethod('Execute').Invoke($null, @($req2, $null, $null))
$dirGone = -not (Test-Path -LiteralPath $appDir)

Check '已确认：执行成功' ([bool]$result2.Success) ("FinalStage=" + $result2.FinalStage + " " + $result2.ErrorMessage)
Check '已确认：安装目录被真删' $dirGone
Check '已确认：标记目录已删除' ([bool]$result2.InstallDirRemoved)
Check '已确认：释放空间大于 0' ([long]$result2.FreedBytes -gt 0) ("实际 " + $result2.FreedBytes)
Check '已确认：释放空间不小于实际字节' ([long]$result2.FreedBytes -ge $expectedBytes) ("实际 " + $result2.FreedBytes + " / 期望>=" + $expectedBytes)
Check '已确认：记录了删除项' (@($result2.RemovedItems).Count -gt 0) ("实际 " + @($result2.RemovedItems).Count)

# ---------- 4. 卸载记录文件 ----------
# %ProgramData% 写不进去时（非管理员 / 受限环境）程序会退回到临时目录，
# 这里两个位置都算数，但必须真的存在一份记录。
$candidates = @(
    (Join-Path $env:ProgramData 'YijianNuodong\logs\uninstall'),
    (Join-Path $env:ProgramData 'YijianNuodong\uninstall'),
    (Join-Path $env:TEMP 'YijianNuodong\uninstall')
)
$foundRecord = $null
foreach ($dir in $candidates) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    $hit = @(Get-ChildItem -LiteralPath $dir -Filter '*E2E测试程序*' -File -ErrorAction SilentlyContinue)
    if ($hit.Count -gt 0) { $foundRecord = $hit[-1].FullName; break }
}
Check '卸载记录已写入（ProgramData 或临时目录）' ($null -ne $foundRecord) ("检查过：" + ($candidates -join ' | '))
if ($null -ne $foundRecord) {
    $recordText = Get-Content -LiteralPath $foundRecord -Raw -Encoding UTF8
    Check '卸载记录含软件名' ($recordText -match [regex]::Escape('E2E测试程序')) $foundRecord
    Check '卸载记录含结果' ($recordText -match '成功|卸载') $foundRecord
}

# 清理
if (Test-Path -LiteralPath $WorkRoot) {
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host ('卸载端到端：通过 ' + $pass + ' 项 / 失败 ' + $fail + ' 项') -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
if ($fail -gt 0) {
    foreach ($f in $failures) { Write-Host ('  - ' + $f) -ForegroundColor Red }
    exit 1
}
exit 0
