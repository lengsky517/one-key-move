# ============================================================================
#  一键挪动 (OneKeyMove) - 构建脚本
#  适用: Windows 10 / 11（自带 .NET Framework 4.8 与 C# 编译器，无需安装 VS/SDK）
#  用法:
#    powershell -ExecutionPolicy Bypass -File build.ps1              # 生成 dist\一键挪动.exe
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Target lib  # 仅编译成 DLL（做自检/预览）
# ============================================================================
[CmdletBinding()]
param(
    [ValidateSet('exe', 'lib')]
    [string]$Target = 'exe',
    [string]$OutDir = '',
    [switch]$IncludeSelfTest,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path $root 'dist' }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path

function Write-Step([string]$text) { if (-not $Quiet) { Write-Host "==> $text" -ForegroundColor Cyan } }
function Fail([string]$text) { Write-Host "构建失败: $text" -ForegroundColor Red; exit 1 }

# 统一控制台编码，保证中文编译信息不乱码
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# ---------- 1. 定位 C# 编译器 ----------
function Find-Csc {
    $candidates = New-Object System.Collections.Generic.List[string]

    # 系统内置编译器（Win10/11 自带，C# 5）
    $fw64 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $fw32 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    $candidates.Add($fw64)
    $candidates.Add($fw32)

    # 有 Visual Studio / Build Tools 时使用 Roslyn 编译器
    $vsRoots = @()
    if ($env:ProgramFiles) { $vsRoots += (Join-Path $env:ProgramFiles 'Microsoft Visual Studio') }
    if (${env:ProgramFiles(x86)}) { $vsRoots += (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio') }
    foreach ($vsRoot in $vsRoots) {
        if ([string]::IsNullOrEmpty($vsRoot) -or -not (Test-Path $vsRoot)) { continue }
        $found = Get-ChildItem -Path $vsRoot -Filter 'csc.exe' -Recurse -ErrorAction SilentlyContinue |
                 Where-Object { $_.FullName -like '*\Roslyn\csc.exe' } | Select-Object -First 1
        if ($found) { $candidates.Add($found.FullName) }
    }

    foreach ($c in $candidates) {
        if (-not [string]::IsNullOrEmpty($c) -and (Test-Path $c)) { return $c }
    }
    return $null
}

$csc = Find-Csc
if ([string]::IsNullOrEmpty($csc)) { Fail '未找到 C# 编译器（csc.exe）。请确认系统为 Windows 10/11 且 .NET Framework 4.x 完整。' }
Write-Step "编译器: $csc"

# 框架程序集目录（与编译器同版本）
if ($csc -like '*\Roslyn\csc.exe') {
    $frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
} else {
    $frameworkDir = Split-Path -Parent $csc
}

# ---------- 2. 收集源文件 ----------
$srcRoot = Join-Path $root 'src'
$sources = @(
    Get-ChildItem -Path $srcRoot -Filter '*.cs' -Recurse -File |
    Where-Object { $_.FullName -notlike '*\SelfTest\*' -or $IncludeSelfTest } |
    Sort-Object FullName |
    ForEach-Object { $_.FullName }
)
if ($sources.Count -eq 0) { Fail '未找到任何源代码文件。' }
Write-Step "源文件: $($sources.Count) 个"

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.ServiceProcess.dll',
    'System.Security.dll',
    'System.Xml.dll'
)
$refPaths = @()
foreach ($r in $references) {
    $p = Join-Path $frameworkDir $r
    if (-not (Test-Path $p)) { $p = Join-Path (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319') $r }
    if (Test-Path $p) { $refPaths += $p }
}

# ---------- 3. 组装编译参数 ----------
$manifest = Join-Path $root 'app.manifest'
$icon = Join-Path $root 'resources\app.ico'

if ($Target -eq 'exe') {
    $outFile = Join-Path $OutDir '一键挪动.exe'
} else {
    $outFile = Join-Path $OutDir 'OneKeyMove.preview.dll'
}
if (Test-Path $outFile) { Remove-Item $outFile -Force -ErrorAction SilentlyContinue }

$cscArgs = New-Object System.Collections.Generic.List[string]
$cscArgs.Add('/nologo')
$cscArgs.Add('/codepage:65001')
$cscArgs.Add('/utf8output')
$cscArgs.Add('/warn:4')
$cscArgs.Add('/optimize+')
$cscArgs.Add('/platform:anycpu')
$cscArgs.Add('/out:' + $outFile)

if ($Target -eq 'exe') {
    $cscArgs.Add('/target:winexe')
    if (Test-Path $manifest) { $cscArgs.Add('/win32manifest:' + $manifest) }
    if (Test-Path $icon) { $cscArgs.Add('/win32icon:' + $icon) }
} else {
    $cscArgs.Add('/target:library')
}
foreach ($p in $refPaths) { $cscArgs.Add('/r:' + $p) }
foreach ($s in $sources) { $cscArgs.Add($s) }

# ---------- 4. 编译 ----------
Write-Step "编译 -> $outFile"
$output = & $csc @cscArgs 2>&1
$exitCode = $LASTEXITCODE

$errors = @($output | Where-Object { "$_" -match 'error\s+CS\d+' })
$warnings = @($output | Where-Object { "$_" -match 'warning\s+CS\d+' })

if (-not $Quiet) {
    foreach ($line in $output) { Write-Host "$line" }
}

if ($exitCode -ne 0 -or $errors.Count -gt 0) {
    Write-Host ""
    Write-Host ("编译失败：{0} 个错误，{1} 个警告" -f $errors.Count, $warnings.Count) -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $outFile)) { Fail '编译进程返回成功，但未生成输出文件。' }

$fileInfo = Get-Item $outFile
Write-Host ""
Write-Host ("构建成功：{0}" -f $outFile) -ForegroundColor Green
Write-Host ("文件大小：{0:N0} 字节    警告数：{1}" -f $fileInfo.Length, $warnings.Count) -ForegroundColor DarkGray
exit 0
