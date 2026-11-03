# ============================================================================
#  一键挪动 - 交付打包
#  把 dist 产物、使用说明、界面截图整理为可直接发给用户的文件夹
#  用法: powershell -ExecutionPolicy Bypass -File tools\package.ps1
# ============================================================================
param(
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path (Split-Path -Parent $root) '一键挪动' }

$exe = Join-Path $root 'dist\一键挪动.exe'
if (-not (Test-Path $exe)) {
    Write-Host "找不到 $exe，请先运行 build.ps1" -ForegroundColor Red
    exit 1
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir '界面预览') | Out-Null

Copy-Item $exe (Join-Path $OutDir '一键挪动.exe') -Force
Copy-Item (Join-Path $root 'README.md') (Join-Path $OutDir '使用说明.md') -Force

# 首次运行辅助脚本（解除网络来源锁定 / 加入安全中心排除项 / 启动）
$helperPs1 = Join-Path $root 'resources\first-run.ps1'
$helperCmd = Join-Path $root 'resources\首次运行.cmd'
if (Test-Path $helperPs1) { Copy-Item $helperPs1 (Join-Path $OutDir 'first-run.ps1') -Force }
if (Test-Path $helperCmd) { Copy-Item $helperCmd (Join-Path $OutDir '首次运行.cmd') -Force }

# 界面截图
$uiDir = Join-Path $root 'docs\ui'
if (Test-Path $uiDir) {
    Get-ChildItem $uiDir -Filter *.png | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $OutDir ('界面预览\' + $_.Name)) -Force
    }
}

# 快速上手
$quick = @'
========================================
  一键挪动 v1.0.3  快速上手
========================================

【首次运行】（被杀软/SmartScreen 拦截时看这里）
   · 最简单：先双击「首次运行.cmd」，它会自动解除文件的"网络来源"
     锁定，并自动把本目录加入 Windows 安全中心排除项，然后启动程序。
   · 若仍弹「Windows 已保护你的电脑」：点「更多信息」→「仍要运行」。
   · 若弹「Internet 安全设置阻止打开文件」：点「确定」继续；
     或右键 exe → 属性 → 勾选「解除锁定」→ 确定。
   · 这些都是"未签名程序"的正常提示，不是病毒。

【升级到新版本时，为什么 Windows 又提示一次？】
   · 本程序没有购买代码签名证书，每次重新编译都是"从未见过的新文件"，
     SmartScreen 会再提示一次「Windows 已保护你的电脑」——点「更多信息」→
     「仍要运行」即可，这不是病毒。
   · Windows 安全中心的"排除项"是按【文件夹路径】生效的：如果每次都把新版本
     解压到新文件夹（如 一键挪动_v1.0.2 / 一键挪动_v1.0.3），旧路径的排除项
     就不再匹配，需要重新添加。
   · 推荐做法（一次搞定，以后不用再加）：
       a) 固定一个目录，例如 D:\Tools\一键挪动\，只往这一个目录解压/覆盖；
       b) 首次运行「首次运行.cmd」会自动把该目录加入 Windows 安全中心排除项
          （v1.0.3 起默认自动添加，不再询问；想跳过可加 -NoExclude）；
       c) 以后升级只需把新版的 一键挪动.exe 覆盖到同一目录即可。
   · 想让 SmartScreen 完全不再提示，只能为 exe 购买代码签名证书（OV/EV）后签名；
     这是所有未签名小工具的通病，不是本程序的缺陷。

【使用步骤】
1. 双击「一键挪动.exe」，在 UAC 提示中点「是」（需要管理员权限）。
2. 三种方式选出要搬的软件：
   · 从桌面把它的快捷方式拖进蓝色虚线框；
   · 或输入「微信 / QQ / PS / 剪映」等常用叫法搜索；
   · 或直接在列表里挑（默认只显示 C 盘软件）。
3. 点「下一步：选择位置」，浏览并挑选目标父文件夹
   （可点「新建文件夹」直接建一个），可修改新文件夹名。
4. 点「开始搬移」。程序会：预检 → 关闭占用 → 复制 → 校验 →
   删除原目录 → 在原路径创建目录联接 → 更新快捷方式/注册表，
   并自动保证桌面快捷方式可用、清理失效的旧快捷方式。
5. 完成后软件照常使用（实际已在新盘）。
   若想还原，点「撤销这次搬移」。

遇到「文件被占用」：
   · 在提示面板点「结束这些进程并继续」（会先正常关闭，必要时强制结束）；
   · 或点「重启后自动完成」，下次开机会在大部分软件启动前完成搬移。

遇到底部「搬移失败」：
   · 失败页点「查看失败诊断日志」，把该日志文件附在反馈中即可定位原因。

注意事项（务必阅读 使用说明.md 第五章）：
   · 不要搬 Windows 系统目录、杀毒软件、驱动类软件、微软商店应用；
   · 目标盘必须是本地 NTFS 磁盘，且预留足够空间；
   · 搬移前建议退出该软件，重要数据先备份。

详细说明、常见问题与源码构建方式见 使用说明.md。
'@
Set-Content -Path (Join-Path $OutDir '快速上手.txt') -Value $quick -Encoding UTF8

$count = (Get-ChildItem $OutDir -Recurse -File).Count
$size = (Get-ChildItem $OutDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("已打包: {0}" -f $OutDir) -ForegroundColor Green
Write-Host ("文件数: {0}    总大小: {1:N0} 字节" -f $count, $size) -ForegroundColor DarkGray
Get-ChildItem $OutDir | Select-Object Name, Length | Format-Table -AutoSize
