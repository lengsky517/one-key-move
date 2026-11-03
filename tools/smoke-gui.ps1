# ============================================================================
#  一键挪动 - 界面运行时冒烟测试（Lead 用）
#  本机安全策略禁止直接运行 exe，这里用「字节加载程序集 + 反射创建 MainForm +
#  真实 Application.Run 消息循环」，跑若干秒后自动退出，验证 主窗口加载/后台扫描/界面稳定。
#  用法: powershell -ExecutionPolicy Bypass -STA -File tools\smoke-gui.ps1 -Seconds 6
# ============================================================================
param(
    [string]$Dll = '',
    [int]$Seconds = 6
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($Dll)) { $Dll = Join-Path $root 'dist\一键挪动.exe' }
if (-not (Test-Path $Dll)) { Write-Host "找不到 $Dll（请先运行 build.ps1）" -ForegroundColor Red; exit 1 }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($Dll))
$formType = $asm.GetType('YijianNuodong.UI.MainForm')
if ($null -eq $formType) { Write-Host '找不到 MainForm' -ForegroundColor Red; exit 1 }

$script:uiError = $null

[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
[System.Windows.Forms.Application]::SetUnhandledExceptionMode([System.Windows.Forms.UnhandledExceptionMode]::CatchException)
$handler = {
    param($sender, $e)
    $script:uiError = $e.Exception.Message
    [System.Windows.Forms.Application]::Exit()
}
[System.Windows.Forms.Application]::add_ThreadException($handler)

Write-Host "==> 创建 MainForm 并进入真实消息循环 ${Seconds}s" -ForegroundColor Cyan
try {
    $ctor = $formType.GetConstructor([Type[]]@())
    $form = $ctor.Invoke(@())
    $form.ShowInTaskbar = $false
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object System.Drawing.Point(-3000, -3000)  # 移到屏幕外，避免打扰
    $form.Tag = 'smoke'

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = ($Seconds * 1000)
    $timer.add_Tick({
        try { [System.Windows.Forms.Application]::Exit() } catch { }
        $timer.Stop()
    })
    $timer.Start()

    [System.Windows.Forms.Application]::Run($form)
    $timer.Stop()
    $timer.Dispose()

    if (-not [string]::IsNullOrEmpty($script:uiError)) {
        Write-Host ("  [FAIL] 界面线程出现未处理异常: " + $script:uiError) -ForegroundColor Red
        exit 1
    }
    Write-Host "  [PASS] 主窗口在真实消息循环中稳定运行 ${Seconds}s，无未处理异常" -ForegroundColor Green
} catch {
    $inner = $_.Exception
    if ($inner.InnerException) { $inner = $inner.InnerException }
    Write-Host ("  [FAIL] 冒烟异常: " + $inner.Message) -ForegroundColor Red
    Write-Host $inner.StackTrace -ForegroundColor DarkGray
    exit 1
}

# 清理残留窗体
try {
    foreach ($open in @([System.Windows.Forms.Application]::OpenForms)) {
        if ($null -ne $open) { $open.Dispose() }
    }
} catch { }

Write-Host ""
Write-Host "界面运行时冒烟通过。" -ForegroundColor Green
exit 0
