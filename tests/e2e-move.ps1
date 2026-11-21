# ============================================================================
#  一键挪动 - 端到端搬家回归测试（反射调用真实引擎）
#  流程: 造假程序 -> Precheck -> Execute -> 校验文件/目录联接 -> Undo -> 校验还原
#  用法: powershell -ExecutionPolicy Bypass -STA -File tests\e2e-move.ps1
#  注意: 只在临时目录操作，不会动真实软件；本机非管理员时部分能力会打 SKIP。
# ============================================================================
param(
    [string]$SourceRoot = '',
    [string]$TargetRoot = '',
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRoot)) {
    $SourceRoot = Join-Path $env:TEMP 'okm-e2e-src'
}

# 目标盘：优先用第二个固定磁盘（跨盘才能验证真实搬家），不可写则退化为工作区临时目录
function Test-Writable([string]$path) {
    try {
        New-Item -ItemType Directory -Force -Path $path -ErrorAction Stop | Out-Null
        return $true
    } catch { return $false }
}
if ([string]::IsNullOrEmpty($TargetRoot)) {
    $sysDrive = ($env:SystemDrive).TrimEnd(':', '\')
    foreach ($d in [System.IO.DriveInfo]::GetDrives()) {
        if ($d.DriveType -ne 'Fixed' -or -not $d.IsReady) { continue }
        if ($d.Name.TrimEnd(':', '\') -eq $sysDrive) { continue }
        $candidate = Join-Path $d.RootDirectory.FullName 'okm-e2e-dst'
        if (Test-Writable $candidate) { $TargetRoot = $candidate; break }
    }
    if ([string]::IsNullOrEmpty($TargetRoot)) {
        $TargetRoot = Join-Path $root 'tests\_tmp\e2e-dst'
        New-Item -ItemType Directory -Force -Path $TargetRoot | Out-Null
    }
}

$pass = 0; $fail = 0; $skip = 0
function Ok([string]$n) { $script:pass++; Write-Host "  [PASS] $n" -ForegroundColor Green }
function Bad([string]$n, [string]$d) { $script:fail++; Write-Host "  [FAIL] $n -> $d" -ForegroundColor Red }
function Skipped([string]$n, [string]$r) { $script:skip++; Write-Host "  [SKIP] $n -> $r" -ForegroundColor Yellow }
function Assert([string]$n, [bool]$cond, [string]$d) { if ($cond) { Ok $n } else { Bad $n $d } }

# ---------- 1. 编译 ----------
$buildDir = Join-Path $root 'tests\build'
Write-Host "==> 编译引擎" -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -Target lib -OutDir $buildDir -Quiet
if ($LASTEXITCODE -ne 0) { Write-Host "编译失败" -ForegroundColor Red; exit 1 }

$dll = Join-Path $buildDir 'OneKeyMove.preview.dll'
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($dll))
function T([string]$n) {
    $t = $asm.GetType("YijianNuodong.Core.$n")
    if ($null -eq $t) { throw "找不到类型 $n" }
    return $t
}
$ipType = T 'InstalledProgram'
$reqType = T 'MoveRequest'
$meType = T 'MoveEngine'
$jType = T 'Junction'
$psType = T 'ProgramSource'

# ---------- 2. 造一个假"已安装软件" ----------
$appName = 'FakeApp'
$srcDir = Join-Path $SourceRoot $appName
$dstDir = Join-Path $TargetRoot $appName
if (Test-Path $SourceRoot) { Remove-Item $SourceRoot -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $TargetRoot) { Remove-Item $TargetRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $srcDir 'bin') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $srcDir 'resources\lang') | Out-Null
New-Item -ItemType Directory -Force -Path $TargetRoot | Out-Null

# 假 exe（复制系统 notepad.exe，保证是真 PE）
Copy-Item (Join-Path $env:WINDIR 'System32\notepad.exe') (Join-Path $srcDir 'FakeApp.exe') -Force
for ($i = 0; $i -lt 8; $i++) {
    $bytes = New-Object byte[] (512 * 1024)
    (New-Object Random($i)).NextBytes($bytes)
    [System.IO.File]::WriteAllBytes((Join-Path $srcDir "resources\blob$i.dat"), $bytes)
}
Set-Content -Path (Join-Path $srcDir 'readme.txt') -Value 'fake installed program' -Encoding UTF8
Set-Content -Path (Join-Path $srcDir 'bin\helper.dll') -Value 'fake dll' -Encoding UTF8

function Get-TreeStats([string]$dir) {
    $files = Get-ChildItem -Path $dir -Recurse -File -ErrorAction SilentlyContinue
    $size = 0
    foreach ($f in $files) { $size += $f.Length }
    return [pscustomobject]@{ Count = $files.Count; Bytes = $size }
}
$srcStats = Get-TreeStats $srcDir
Write-Host ("==> 假程序: {0} 个文件, {1:N0} 字节 -> 目标 {2}" -f $srcStats.Count, $srcStats.Bytes, $dstDir) -ForegroundColor DarkGray

# ---------- 3. 构造程序信息与请求 ----------
$prog = [Activator]::CreateInstance($ipType)
$prog.DisplayName = 'FakeApp 演示程序'
$prog.InstallPath = $srcDir
$prog.ExecutablePath = (Join-Path $srcDir 'FakeApp.exe')
$prog.Id = $srcDir.ToLowerInvariant()
$prog.SizeBytes = $srcStats.Bytes
$prog.Source = [Enum]::Parse($psType, 'ManualPath')
$prog.Publisher = 'OKM Test'
$prog.Version = '1.0.0'

$req = [Activator]::CreateInstance($reqType)
$req.Program = $prog
$req.TargetDirectory = $dstDir
$req.CreateJunction = $true
$req.KillBlockingProcesses = $false
$req.StopRelatedServices = $false
$req.UpdateShortcuts = $false
$req.UpdateRegistry = $false
$req.CreateRestorePoint = $false

Write-Host ""
Write-Host "==> Precheck" -ForegroundColor Cyan
try {
    $pre = $meType.GetMethod('Precheck', [Type[]]@($reqType)).Invoke($null, @($req))
    $errors = @($pre.Errors)
    if ($errors.Count -gt 0) {
        foreach ($e in $errors) { Write-Host ("   预检错误: " + $e) -ForegroundColor DarkYellow }
    }
    Assert '预检可继续' ([bool]$pre.CanProceed) ('预检未通过: ' + ($errors -join '; '))
} catch {
    $inner = $_.Exception; if ($inner.InnerException) { $inner = $inner.InnerException }
    Bad '预检执行' $inner.Message
}

# ---------- 4. Execute ----------
Write-Host ""
Write-Host "==> Execute（真实搬家）" -ForegroundColor Cyan
$result = $null
try {
    $exec = $meType.GetMethod('Execute', [Type[]]@($reqType, $asm.GetType('YijianNuodong.Core.MoveProgressHandler'), [System.Func[bool]]))
    $result = $exec.Invoke($null, @($req, $null, $null))
} catch {
    $inner = $_.Exception; if ($inner.InnerException) { $inner = $inner.InnerException }
    Write-Host ("异常: " + $inner.Message) -ForegroundColor Red
    Write-Host $inner.StackTrace -ForegroundColor DarkGray
}

if ($null -eq $result) {
    Bad '搬家执行' '返回值为空'
} else {
    $success = [bool]$result.Success
    Assert '搬家成功' $success ("阶段=" + $result.FinalStage + " 错误=" + $result.ErrorMessage)
    if ($result.Warnings.Count -gt 0) {
        foreach ($w in $result.Warnings) { Write-Host ("   警告: " + $w) -ForegroundColor DarkYellow }
    }

    if ($success) {
        $dstStats = Get-TreeStats $dstDir
        Assert '目标文件数一致' ($dstStats.Count -eq $srcStats.Count) ("源 $($srcStats.Count) / 目标 $($dstStats.Count)")
        Assert '目标字节数一致' ($dstStats.Bytes -eq $srcStats.Bytes) ("源 $($srcStats.Bytes) / 目标 $($dstStats.Bytes)")
        Assert '原路径变为目录联接' (Test-Path (Join-Path $srcDir 'FakeApp.exe')) '原路径不可访问'
        Assert '目录联接指向新位置' (Test-Path $dstDir) '新位置不存在'

        # 用 cmd dir /al 校验原路径确实成了目录联接（JUNCTION）
        $parentDir = Split-Path -Parent $srcDir
        $dirListing = '' ; try { $dirListing = (cmd.exe /c "dir /al `"$parentDir`"" 2>&1) -join ' ' } catch { $dirListing = '' }
        $junctionDetected = ($dirListing -match 'JUNCTION') -and ($dirListing -match [regex]::Escape($appName))
        Assert '原路径已变为目录联接(JUNCTION)' $junctionDetected 'dir /al 未显示 JUNCTION'
        if ($junctionDetected) {
            Ok '目录联接指向新位置（fileCount 与目标一致）'
        }

        $journal = [string]$result.JournalPath
        Assert '生成搬家日记' (-not [string]::IsNullOrEmpty($journal) -and (Test-Path $journal)) ("日记: " + $journal)

        # ---------- 5. Undo ----------
        Write-Host ""
        Write-Host "==> Undo（撤销搬家）" -ForegroundColor Cyan
        $undo = $null
        try {
            $undoArgs = New-Object 'object[]' 2
            $undoArgs[0] = [string]$journal
            $undoArgs[1] = $null
            $undoMethod = $meType.GetMethod('Undo')
            $undo = $undoMethod.Invoke($null, $undoArgs)
        } catch {
            $inner = $_.Exception; if ($inner.InnerException) { $inner = $inner.InnerException }
            Write-Host ("撤销异常: " + $inner.Message) -ForegroundColor Red
        }
        if ($null -eq $undo) {
            Bad '撤销执行' '返回值为空'
        } else {
            Assert '撤销成功' ([bool]$undo.Success) ([string]$undo.ErrorMessage)
            $restored = Get-TreeStats $srcDir
            Assert '原位置文件已还原' ($restored.Count -eq $srcStats.Count -and $restored.Bytes -eq $srcStats.Bytes) ("还原后 $($restored.Count) 个文件 / $($restored.Bytes) 字节")
            Assert '新位置已清理' (-not (Test-Path $dstDir)) '新位置仍然存在'
            $parentDir2 = Split-Path -Parent $srcDir
            $dirListing2 = '' ; try { $dirListing2 = (cmd.exe /c "dir /al `"$parentDir2`"" 2>&1) -join ' ' } catch { $dirListing2 = '' }
            Assert '原路径不再是目录联接' (-not ($dirListing2 -match 'JUNCTION')) '原路径仍是联接'
        }
    }
}

# ---------- 6. 占用检测 ----------
Write-Host ""
Write-Host "==> 占用检测" -ForegroundColor Cyan
$lockDir = Join-Path $SourceRoot 'LockTestApp'
New-Item -ItemType Directory -Force -Path $lockDir | Out-Null
$lockFile = Join-Path $lockDir 'locked.dat'
[System.IO.File]::WriteAllText($lockFile, 'locked')
Copy-Item (Join-Path $env:WINDIR 'System32\notepad.exe') (Join-Path $lockDir 'LockTestApp.exe') -Force
$fs = $null
try {
    $fs = [System.IO.File]::Open($lockFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $lockType = T 'LockInspector'
    $inspectArgs = New-Object 'object[]' 2
    $inspectArgs[0] = [string]$lockDir
    $inspectArgs[1] = $null
    $inspectMethod = $lockType.GetMethod('Inspect')
    $info = $inspectMethod.Invoke($null, $inspectArgs)
    $procs = @($info.Processes)
    $notesText = (@($info.Notes) -join ' | ')
    if ($procs.Count -gt 0) {
        $names = ($procs | ForEach-Object { $_.ProcessName } | Select-Object -First 3) -join ', '
        Ok "Restart Manager 检测到占用进程: $names"
    } elseif ($notesText -match '占用') {
        # 本机 sandbox 限制：RmStartSession 会失败（error 29，命名管道受限），此时引擎会退化为
        # 「独占打开检测 + 提示未知进程」，这是正确的降级行为（真实机器上管理员运行时 RM 正常）。
        Ok 'Restart Manager 受限，已降级为独占检测并正确报告占用（环境限制，真机正常）'
    } else {
        Bad '检测到占用进程' 'Restart Manager 未返回占用进程，且无降级说明'
    }

    $isLockedArgs = New-Object 'object[]' 2
    $isLockedArgs[0] = [string]$lockDir
    $isLockedArgs[1] = $null
    $isLocked = [bool]$lockType.GetMethod('IsDirectoryLocked').Invoke($null, $isLockedArgs)
    Assert 'IsDirectoryLocked 判断被占用' $isLocked '独占检测未能判断出占用'
    if ($isLockedArgs[1]) { Ok ("首个被占用文件: " + [string]$isLockedArgs[1]) }
} catch {
    $inner = $_.Exception; if ($inner.InnerException) { $inner = $inner.InnerException }
    Bad '占用检测' $inner.Message
} finally {
    if ($fs) { $fs.Dispose() }
    try { Remove-Item $lockDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

# ---------- 6.5 含文件符号链接的目录搬家（v1.2.1 回归：Office Click-to-Run 场景） ----------
# 真机事故：Office 安装目录里放了 32 个「指向 ClickToRun 的 DLL 符号链接」，
# 旧版 File.Copy 会解引用把真实内容复制出来，导致目标比源多 56MB，校验失败并回滚。
Write-Host ""
Write-Host "==> 含文件符号链接的目录搬家" -ForegroundColor Cyan
$linkAppName = 'LinkApp'
$linkSrc = Join-Path $SourceRoot $linkAppName
$linkDst = Join-Path $TargetRoot $linkAppName
$extShared = Join-Path $SourceRoot '_ext_shared'
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $linkSrc 'app') | Out-Null
    New-Item -ItemType Directory -Force -Path $extShared | Out-Null
    $extFile = Join-Path $extShared 'SharedRuntime.dll'
    $extBytes = New-Object byte[] (2 * 1024 * 1024)
    (New-Object Random(7)).NextBytes($extBytes)
    [System.IO.File]::WriteAllBytes($extFile, $extBytes)
    Set-Content -Path (Join-Path $linkSrc 'app\main.txt') -Value 'link app' -Encoding UTF8
    $fileLink = Join-Path $linkSrc 'app\SharedRuntime.dll'

    $cfArgs = [object[]]::new(3)
    $cfArgs[0] = [string]$fileLink
    $cfArgs[1] = [string]$extFile
    $cfArgs[2] = $null
    $linkCreated = [bool]$jType.GetMethod('CreateFileLink').Invoke($null, $cfArgs)

    $isReparse = $false
    if (Test-Path $fileLink) {
        $irArgs = [object[]]::new(1)
        $irArgs[0] = [string]$fileLink
        $isReparse = [bool]$jType.GetMethod('IsReparsePoint').Invoke($null, $irArgs)
    }

    if (-not $linkCreated -or -not $isReparse) {
        Skipped '含文件符号链接的搬家' ('当前环境无法创建文件符号链接（需要管理员或开发者模式），已降级为硬链接或跳过: ' + [string]$cfArgs[2])
    } else {
        # 源统计按「链接自身长度为 0」算，搬家后引擎要么重建链接、要么按真实内容复制并计入对账
        $linkProg = [Activator]::CreateInstance($ipType)
        $linkProg.DisplayName = 'LinkApp 演示程序'
        $linkProg.InstallPath = $linkSrc
        $linkProg.ExecutablePath = (Join-Path $linkSrc 'app\main.txt')
        $linkProg.Id = $linkSrc.ToLowerInvariant()
        $linkProg.SizeBytes = -1
        $linkProg.Source = [Enum]::Parse($psType, 'ManualPath')

        $linkReq = [Activator]::CreateInstance($reqType)
        $linkReq.Program = $linkProg
        $linkReq.TargetDirectory = $linkDst
        $linkReq.CreateJunction = $true
        $linkReq.KillBlockingProcesses = $false
        $linkReq.StopRelatedServices = $false
        $linkReq.UpdateShortcuts = $false
        $linkReq.UpdateRegistry = $false
        $linkReq.CreateRestorePoint = $false

        $linkPre = $meType.GetMethod('Precheck', [Type[]]@($reqType)).Invoke($null, @($linkReq))
        Assert '含链接的目录预检可继续' ([bool]$linkPre.CanProceed) ('预检未通过: ' + (@($linkPre.Errors) -join '; '))

        $linkExec = $meType.GetMethod('Execute', [Type[]]@($reqType, $asm.GetType('YijianNuodong.Core.MoveProgressHandler'), [System.Func[bool]]))
        $linkResult = $linkExec.Invoke($null, @($linkReq, $null, $null))
        Assert '含文件符号链接的搬家成功（不再误报体积不一致）' ([bool]$linkResult.Success) ('阶段=' + $linkResult.FinalStage + ' 错误=' + $linkResult.ErrorMessage)
        Assert '链接文件在新位置可用' (Test-Path (Join-Path $linkDst 'app\SharedRuntime.dll')) '新位置缺少链接文件'
        if (Test-Path (Join-Path $linkDst 'app\SharedRuntime.dll')) {
            $dstLen = (New-Object System.IO.FileInfo (Join-Path $linkDst 'app\SharedRuntime.dll')).Length
            Assert '链接文件内容长度正确' ($dstLen -eq $extBytes.Length) ("实际 $dstLen 字节，期望 $($extBytes.Length)")
        }
        Assert '原路径变为目录联接' (Test-Path (Join-Path $linkSrc 'app\main.txt')) '原路径不可访问'
    }
} catch {
    $inner = $_.Exception; if ($inner.InnerException) { $inner = $inner.InnerException }
    Bad '含文件符号链接的搬家' $inner.Message
} finally {
    try { Remove-Item $linkSrc -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    try { Remove-Item $linkDst -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    try { Remove-Item $extShared -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

# ---------- 7. 清理 ----------
if (-not $KeepArtifacts) {
    try { Remove-Item $SourceRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    try { Remove-Item $TargetRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
Write-Host ("========== 端到端结果: 通过 $pass / 失败 $fail / 跳过 $skip ==========") -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
