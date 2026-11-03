# ============================================================================
#  一键挪动 - 首次运行辅助脚本
#  作用：
#   1) 解除本目录所有文件的「网络来源」锁定（Mark-of-the-Web），解决
#      「Windows 安全中心：Internet 安全设置阻止打开一个或多个文件」
#   2) 自动：把本工具所在文件夹加入 Windows 安全中心（Defender）排除项
#      （可用 -NoExclude 跳过；升级时覆盖同一目录即可继续生效）
#   3) 启动 一键挪动.exe
#  若出现「Windows 已保护你的电脑」：点「更多信息」→「仍要运行」即可（见脚本提示）。
# ============================================================================
param(
    [switch]$AllowlistOnly,
    [switch]$NoExclude
)

$ErrorActionPreference = 'Continue'
$base = $PSScriptRoot
$exe = Join-Path $base '一键挪动.exe'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host $text -ForegroundColor Cyan
}

# ---------- 仅添加白名单（由提权后的子进程调用） ----------
if ($AllowlistOnly) {
    Write-Step '[白名单] 正在把本工具加入 Windows 安全中心排除项…'
    $ok = $true
    try {
        Add-MpPreference -ExclusionPath $base -ErrorAction Stop
        Write-Host ('  已添加文件夹排除项：' + $base) -ForegroundColor Green
    } catch {
        $ok = $false
        Write-Host ('  添加文件夹排除项失败：' + $_.Exception.Message) -ForegroundColor Yellow
    }
    try {
        Add-MpPreference -ExclusionProcess '一键挪动.exe' -ErrorAction Stop
        Write-Host '  已添加进程排除项：一键挪动.exe' -ForegroundColor Green
    } catch {
        Write-Host '  添加进程排除项失败（可忽略）' -ForegroundColor Yellow
    }
    if (-not $ok) {
        Write-Host ''
        Write-Host '  也可以手动添加：Windows 安全中心 → 病毒和威胁防护 → 管理设置 → 排除项 → 添加文件夹' -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host '  撤销方法：Windows 安全中心 → 病毒和威胁防护 → 管理设置 → 排除项 → 删除对应项' -ForegroundColor DarkGray
    exit 0
}

Write-Host '======================================================' -ForegroundColor Cyan
Write-Host '        一键挪动  -  首次运行辅助向导' -ForegroundColor Cyan
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host '本工具用于把装在 C 盘的软件搬到其他盘，需要管理员权限。' -ForegroundColor DarkGray
Write-Host '本程序没有购买代码签名证书，所以从网上下载后 Windows 可能' -ForegroundColor DarkGray
Write-Host '会弹窗提示"无法识别的应用"，这是正常现象，不是病毒。' -ForegroundColor DarkGray

# ---------- 1) 解除网络来源锁定 ----------
Write-Step '[1/3] 正在解除本目录文件的「网络来源」锁定…'
$unblocked = 0
try {
    $files = Get-ChildItem -LiteralPath $base -Recurse -File -Force -ErrorAction SilentlyContinue
    foreach ($f in $files) {
        try {
            $zone = Get-Item -LiteralPath $f.FullName -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue
            if ($null -ne $zone) {
                Unblock-File -LiteralPath $f.FullName -ErrorAction SilentlyContinue
                $unblocked++
            }
        } catch { }
    }
} catch { }
if ($unblocked -gt 0) {
    Write-Host ('  已解除 ' + $unblocked + ' 个文件的网络来源锁定。') -ForegroundColor Green
} else {
    Write-Host '  没有发现被锁定的文件（可能已经解除，或被浏览器直接打开）。' -ForegroundColor DarkGray
}
Write-Host '  若仍提示「Internet 安全设置阻止打开一个或多个文件」：' -ForegroundColor DarkGray
Write-Host '  右键 一键挪动.exe →「属性」→ 勾选/点击「解除锁定」→ 确定。' -ForegroundColor DarkGray

# ---------- 2) 提示 SmartScreen，并自动添加白名单 ----------
Write-Step '[2/3] 关于 Windows 的"无法识别的应用"提示'
Write-Host '  · 出现「Windows 已保护你的电脑」：点「更多信息」→「仍要运行」' -ForegroundColor Yellow
Write-Host '  · 出现「Internet 安全设置阻止…」：点「确定」继续，或按上面方法解除锁定' -ForegroundColor Yellow
Write-Host '  · 本程序未购买代码签名证书，每次版本更新都是"新文件"，所以 Windows' -ForegroundColor DarkGray
Write-Host '    会再提示一次；安全中心排除项按文件夹路径生效，换了目录也要重加。' -ForegroundColor DarkGray
Write-Host ''
if ($NoExclude) {
    Write-Host '  已按 -NoExclude 跳过白名单设置（以后仍可在 Windows 安全中心手动添加）。' -ForegroundColor DarkGray
} else {
    if (Test-Admin) {
        Write-Step '[白名单] 正在自动添加排除项（以后升级覆盖同一目录即可继续生效）…'
        try {
            Add-MpPreference -ExclusionPath $base -ErrorAction Stop
            Write-Host ('  已添加文件夹排除项：' + $base) -ForegroundColor Green
        } catch {
            Write-Host ('  添加失败：' + $_.Exception.Message) -ForegroundColor Yellow
            Write-Host '  可手动：Windows 安全中心 → 病毒和威胁防护 → 管理设置 → 排除项 → 添加文件夹' -ForegroundColor Yellow
        }
        try {
            Add-MpPreference -ExclusionProcess '一键挪动.exe' -ErrorAction Stop
            Write-Host '  已添加进程排除项：一键挪动.exe' -ForegroundColor Green
        } catch { }
        Write-Host '  提示：以后升级请把新 exe 覆盖到本目录，排除项就不用再加。' -ForegroundColor DarkGray
    } else {
        Write-Host '  正在请求管理员权限以添加排除项（请在 UAC 弹窗中点「是」）…' -ForegroundColor Yellow
        try {
            $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-AllowlistOnly')
            Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args -Wait -ErrorAction Stop
        } catch {
            Write-Host ('  提升权限失败：' + $_.Exception.Message) -ForegroundColor Yellow
            Write-Host '  可手动添加：Windows 安全中心 → 病毒和威胁防护 → 管理设置 → 排除项' -ForegroundColor Yellow
        }
    }
}

# ---------- 3) 启动程序 ----------
Write-Step '[3/3] 正在启动 一键挪动…'
if (Test-Path -LiteralPath $exe) {
    try {
        Start-Process -FilePath $exe
        Write-Host '  已发起启动；如弹出 UAC 授权框，请点「是」（搬移 Program Files 下的软件必须管理员权限）。' -ForegroundColor Green
    } catch {
        Write-Host ('  启动失败：' + $_.Exception.Message) -ForegroundColor Red
        Write-Host '  请直接双击 一键挪动.exe，并在 UAC 中点「是」。' -ForegroundColor Yellow
    }
} else {
    Write-Host '  未找到 一键挪动.exe，请确认本脚本与 exe 在同一个文件夹内。' -ForegroundColor Red
}

Write-Host ''
Write-Host '完成。若程序窗口没有出现，请直接双击 一键挪动.exe 再试一次。' -ForegroundColor DarkGray
