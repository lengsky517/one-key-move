# ============================================================================
#  一键挪动 - 逻辑自检运行器
#  编译（含 SelfTest）为 DLL -> 字节加载 -> 反射调用 LogicTests.RunAll()
#  用法: powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
# ============================================================================
param(
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path $root 'tests\build' }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

$buildScript = Join-Path $root 'build.ps1'
Write-Host "==> 编译（含自检代码）" -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildScript -Target lib -IncludeSelfTest -OutDir $OutDir -Quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败，无法运行自检。" -ForegroundColor Red
    exit 1
}

$dll = Join-Path $OutDir 'OneKeyMove.preview.dll'
if (-not (Test-Path $dll)) { Write-Host "找不到 $dll" -ForegroundColor Red; exit 1 }

Write-Host "==> 运行逻辑自检" -ForegroundColor Cyan
$bytes = [System.IO.File]::ReadAllBytes($dll)
$asm = [System.Reflection.Assembly]::Load($bytes)
$type = $asm.GetType('YijianNuodong.SelfTest.LogicTests')
if ($null -eq $type) { Write-Host "找不到自检类型 LogicTests" -ForegroundColor Red; exit 1 }

$method = $type.GetMethod('RunAll', [Type[]]@())
try {
    $report = $method.Invoke($null, @())
} catch {
    $inner = $_.Exception
    if ($inner.InnerException) { $inner = $inner.InnerException }
    Write-Host ("自检执行异常: " + $inner.Message) -ForegroundColor Red
    Write-Host $inner.StackTrace -ForegroundColor DarkGray
    exit 1
}

Write-Host $report
if ($report -match '失败 0 项') {
    Write-Host "逻辑自检通过。" -ForegroundColor Green
    exit 0
} else {
    Write-Host "逻辑自检存在失败项。" -ForegroundColor Red
    exit 1
}
