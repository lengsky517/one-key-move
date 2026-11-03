# ============================================================================
#  一键挪动 - 构建产物校验（Lead 用）
#  1) 编译 exe；2) 校验清单已嵌入（requireAdministrator）；3) 校验图标资源；
#  4) 入口点冒烟：以"重启后完成模式"调用 Main，确认不会崩溃且能正常返回。
#  用法: powershell -ExecutionPolicy Bypass -File tools\verify-build.ps1
# ============================================================================
param(
    [string]$DistDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($DistDir)) { $DistDir = Join-Path $root 'dist' }

$pass = 0; $fail = 0
function Ok([string]$n) { $script:pass++; Write-Host "  [PASS] $n" -ForegroundColor Green }
function Bad([string]$n, [string]$d) { $script:fail++; Write-Host "  [FAIL] $n -> $d" -ForegroundColor Red }

Write-Host "==> 编译 exe" -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -Target exe -OutDir $DistDir
if ($LASTEXITCODE -ne 0) { Write-Host "编译失败" -ForegroundColor Red; exit 1 }

$exe = Join-Path $DistDir '一键挪动.exe'
Write-Host ""
Write-Host "==> 产物校验: $exe" -ForegroundColor Cyan
if (-not (Test-Path $exe)) { Bad '产物存在' '未生成 exe'; exit 1 }
$fi = Get-Item $exe
if ($fi.Length -gt 100KB) { Ok ("文件大小合理: {0:N0} 字节" -f $fi.Length) } else { Bad '文件大小' '产物过小，可能编译内容缺失' }

$bytes = [System.IO.File]::ReadAllBytes($exe)
$text = [System.Text.Encoding]::UTF8.GetString($bytes)
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)

if ($text.Contains('requireAdministrator') -or $ascii.Contains('requireAdministrator')) { Ok '已嵌入管理员权限清单(requireAdministrator)' } else { Bad '管理员清单' '未找到 requireAdministrator' }
if ($text.Contains('一键挪动') -or $ascii.Contains('一键挪动')) { Ok '已嵌入中文产品信息' } else { Bad '中文产品信息' '未找到产品名' }
if ($text.Contains('dpiAware') -or $ascii.Contains('dpiAware')) { Ok '已嵌入 DPI 感知清单' } else { Bad 'DPI 清单' '未找到 dpiAware' }

# v1.0.2 真机根因：清单声明 longPathAware 会让 .NET 把 \\?\ 前缀判为非法字符，必须保持移除
if (($text.Contains('longPathAware') -or $ascii.Contains('longPathAware'))) { Bad '长路径清单' '清单仍声明 longPathAware，真机上会报「路径中具有非法字符」' } else { Ok '清单未声明 longPathAware（避免 \\?\ 前缀失败）' }

# 图标资源：检查 RT_GROUP_ICON（按 PE 资源目录粗查 —— 用 ico 名称不现实，改查 ICO 数据特征）
$hasIconBytes = $false
for ($i = 0; $i -lt $bytes.Length - 4; $i++) {
    # PNG 图标数据（图标内含 PNG 压缩帧）
    if ($bytes[$i] -eq 0x89 -and $bytes[$i+1] -eq 0x50 -and $bytes[$i+2] -eq 0x4E -and $bytes[$i+3] -eq 0x47) { $hasIconBytes = $true; break }
}
if ($hasIconBytes) { Ok '已嵌入图标资源(PNG 帧)' } else { Bad '图标资源' '未找到图标数据' }

# ---------- 入口点冒烟 ----------
Write-Host ""
Write-Host "==> 入口点冒烟测试（重启后完成模式）" -ForegroundColor Cyan
$asm = [System.Reflection.Assembly]::Load($bytes)
$programType = $asm.GetType('YijianNuodong.Program')
if ($null -eq $programType) { Bad '入口类型' '找不到 YijianNuodong.Program' }
else {
    $mainMethod = $programType.GetMethod('Main', [System.Reflection.BindingFlags]'NonPublic,Static')
    if ($null -eq $mainMethod) { Bad '入口方法' '找不到 Main' }
    else {
        $badJob = Join-Path $env:TEMP ('okm_missing_' + [Guid]::NewGuid().ToString('N') + '.job')
        try {
            # 注意：Main(string[] args) 只有一个参数，必须包成 object[1] 再传，否则 PowerShell 会把数组展开
            $invokeArgs = New-Object 'object[]' 1
            $invokeArgs[0] = [string[]]@('--complete-pending', $badJob)
            $ret = $mainMethod.Invoke($null, $invokeArgs)
            if ($null -ne $ret) { Ok ("入口返回码: " + $ret + "（不存在的任务文件被安全处理，未崩溃）") } else { Ok '入口正常返回' }
        } catch {
            $inner = $_.Exception
            if ($inner.InnerException) { $inner = $inner.InnerException }
            Bad '入口冒烟' $inner.Message
        }
    }
}

# ---------- 长路径策略（v1.0.2 真机根因防回归） ----------
$longPathOk = $false
try {
    $pathUtil = $asm.GetType('YijianNuodong.Core.PathUtil')
    $toLong = $pathUtil.GetMethod('ToLongPath', [Type[]]@([string]))
    $shortPath = [string]$toLong.Invoke($null, @('C:\a\b'))
    $longInput = 'C:\' + ('d' * 260) + '\x.exe'
    $longOut = [string]$toLong.Invoke($null, @($longInput))
    $longPathOk = ($shortPath -eq 'C:\a\b' -and $longOut.StartsWith('\\?\'))
    if ($longPathOk) { Ok '长路径策略正确（普通路径不加前缀，超长路径加前缀）' }
    else { Bad '长路径策略' ("普通路径返回 {0}；超长路径是否加前缀 {1}" -f $shortPath, $longOut.StartsWith('\\?\')) }
} catch { Bad '长路径策略' $_.Exception.Message }

# ---------- 启动日志版本号一致性（v1.0.3） ----------
try {
    $programTypeForVersion = $asm.GetType('YijianNuodong.Program')
    $versionField = $programTypeForVersion.GetField('VersionText', [System.Reflection.BindingFlags]'NonPublic,Static')
    $versionText = [string]$versionField.GetValue($null)
    $asmVer = $asm.GetName().Version
    $expectedText = "{0}.{1}.{2}" -f $asmVer.Major, $asmVer.Minor, $asmVer.Build
    if ($versionText -eq $expectedText) { Ok ("启动日志版本号与程序集一致: v" + $versionText) }
    else { Bad '启动日志版本号' ("日志版本 {0} 与程序集版本 {1} 不一致" -f $versionText, $expectedText) }
} catch { Bad '启动日志版本号' $_.Exception.Message }

Write-Host ""
Write-Host ("========== 校验结果: 通过 $pass / 失败 $fail ==========") -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 } else { exit 0 }
