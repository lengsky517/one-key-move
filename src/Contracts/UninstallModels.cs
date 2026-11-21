// ============================================================================
//  一键挪动 (OneKeyMove) - 卸载功能共享契约（Lead 维护，冻结后队友只读）
//  目标框架: .NET Framework 4.8 / C# 5 (系统内置 csc.exe 编译)
//  说明: 与 Models.cs 分离，避免影响已发布 1.0.3 的搬移契约。
// ============================================================================
using System;
using System.Collections.Generic;

namespace YijianNuodong.Core
{
    /// <summary>卸载方式</summary>
    public enum UninstallMethod
    {
        /// <summary>没有找到可用的卸载信息，只能手工清理</summary>
        None = 0,
        /// <summary>注册表 Uninstall 项里给了卸载命令（exe 卸载程序）</summary>
        RegistryCommand = 1,
        /// <summary>MSI 安装包，可用 msiexec /X 卸载</summary>
        MsiProduct = 2,
        /// <summary>只有目录，直接删除文件（危险，需用户二次确认）</summary>
        ManualOnly = 3
    }

    /// <summary>卸载阶段</summary>
    public enum UninstallStage
    {
        Idle = 0,
        Precheck = 1,
        BackingUp = 2,
        ClosingProgram = 3,
        RunningUninstaller = 4,
        WaitingForUninstaller = 5,
        Verifying = 6,
        CleaningLeftovers = 7,
        Finalizing = 8,
        Completed = 9,
        Failed = 10,
        Canceled = 11,
        /// <summary>卸载器执行了，但有残留没能清干净</summary>
        PartialSuccess = 12
    }

    /// <summary>注册表里的卸载信息项（一个软件可能有多条，如 32/64 位各一条）</summary>
    public class UninstallRegistryEntry
    {
        /// <summary>"HKLM" 或 "HKCU"</summary>
        public string Hive { get; set; }
        /// <summary>完整键路径，如 SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WeChat</summary>
        public string KeyPath { get; set; }
        /// <summary>键名（最后一段），通常等于 ProductCode 或软件名</summary>
        public string KeyName { get; set; }
        public string DisplayName { get; set; }
        public string DisplayVersion { get; set; }
        public string Publisher { get; set; }
        public string InstallLocation { get; set; }
        public string UninstallString { get; set; }
        public string QuietUninstallString { get; set; }
        public string DisplayIcon { get; set; }
        /// <summary>WindowsInstaller=1 表示 MSI 安装</summary>
        public bool WindowsInstaller { get; set; }
        /// <summary>MSI 产品代码 {XXXXXXXX-....}（能从键名或卸载命令解析出来时给出）</summary>
        public string ProductCode { get; set; }
        /// <summary>EstimatedSize（KB，注册表值）</summary>
        public long EstimatedSizeKB { get; set; }
        /// <summary>备份出的 .reg 文件路径（预检后由引擎填写）</summary>
        public string BackupFilePath { get; set; }
        /// <summary>
        /// 弱匹配：只按显示名对上、安装位置对不上。
        /// 这种项可用来取卸载命令，但绝不能用来决定删除哪个目录。
        /// </summary>
        public bool WeakMatch { get; set; }
    }

    /// <summary>卸载预检结果：告诉界面「要删什么、怎么删、有没有风险」</summary>
    public class UninstallTarget
    {
        public InstalledProgram Program { get; set; }
        public string DisplayName { get; set; }
        /// <summary>真实安装目录（若原路径是目录联接，这里给联接指向的真实目录）</summary>
        public string InstallPath { get; set; }
        /// <summary>原安装路径（可能是目录联接）</summary>
        public string LinkPath { get; set; }
        /// <summary>原路径是否为目录联接（被「一键挪动」搬过的软件会是 true）</summary>
        public bool InstallPathIsJunction { get; set; }
        /// <summary>安装目录大小（字节），-1 表示未知</summary>
        public long InstallBytes { get; set; }
        public int FileCount { get; set; }

        public UninstallMethod Method { get; set; }
        /// <summary>实际将执行的命令（MSI 会被改写成 msiexec /X{GUID} ...）</summary>
        public string Command { get; set; }
        /// <summary>卸载程序可执行文件路径（用于识别占用进程），可能为空</summary>
        public string UninstallerExe { get; set; }
        public string ProductCode { get; set; }
        public bool IsMsi { get; set; }
        /// <summary>是否必须管理员权限</summary>
        public bool RequiresAdmin { get; set; }

        public List<UninstallRegistryEntry> RegistryEntries { get; set; }
        /// <summary>占用安装目录的进程/服务</summary>
        public LockInfo Locks { get; set; }
        /// <summary>指向安装目录的桌面/开始菜单快捷方式</summary>
        public List<string> Shortcuts { get; set; }
        /// <summary>可能存在的用户数据目录（默认保留，除非用户勾选一起删）</summary>
        public List<string> DataFolders { get; set; }
        /// <summary>预检通过后将执行的步骤（给确认页逐条显示）</summary>
        public List<string> Steps { get; set; }

        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }

        /// <summary>是否为受保护的系统组件（禁止卸载）</summary>
        public bool IsProtected { get; set; }
        public string ProtectedReason { get; set; }
        /// <summary>预检是否通过</summary>
        public bool CanProceed { get; set; }

        public UninstallTarget()
        {
            InstallBytes = -1;
            RegistryEntries = new List<UninstallRegistryEntry>();
            Shortcuts = new List<string>();
            DataFolders = new List<string>();
            Steps = new List<string>();
            Errors = new List<string>();
            Warnings = new List<string>();
        }
    }

    /// <summary>卸载请求（用户在确认页上的勾选项）</summary>
    public class UninstallRequest
    {
        public UninstallTarget Target { get; set; }
        /// <summary>自动结束占用安装目录的进程（需用户显式勾选）</summary>
        public bool CloseRunningPrograms { get; set; }
        /// <summary>卸载后清理残留（目录、快捷方式、注册表项、空开始菜单文件夹）</summary>
        public bool RemoveLeftovers { get; set; }
        /// <summary>连用户数据目录（%APPDATA% 下的配置等）一起删除，默认 false</summary>
        public bool RemoveUserData { get; set; }
        /// <summary>是否创建系统还原点（尽力而为）</summary>
        public bool CreateRestorePoint { get; set; }
        /// <summary>是否导出注册表卸载项做备份，默认 true</summary>
        public bool BackupRegistry { get; set; }
        /// <summary>卸载程序最长等待秒数（超时后转为「后台继续」报告）</summary>
        public int TimeoutSeconds { get; set; }
        /// <summary>MSI 是否静默卸载（/qn），默认 true</summary>
        public bool SilentWhenPossible { get; set; }
        /// <summary>
        /// 没有官方卸载程序时，是否已由用户明确确认「手工删除文件」。
        /// 为 false 时引擎拒绝删除任何东西（防误删）。
        /// </summary>
        public bool ConfirmManualDelete { get; set; }

        public UninstallRequest()
        {
            RemoveLeftovers = true;
            BackupRegistry = true;
            SilentWhenPossible = true;
            TimeoutSeconds = 600;
        }
    }

    /// <summary>卸载进度</summary>
    public class UninstallProgress
    {
        public UninstallStage Stage { get; set; }
        /// <summary>阶段中文名，如 "正在运行官方卸载程序"</summary>
        public string StageText { get; set; }
        public string Message { get; set; }
        /// <summary>当前正在处理的文件/目录</summary>
        public string CurrentItem { get; set; }
        /// <summary>总体进度 0-100，未知时为 -1</summary>
        public int Percent { get; set; }
        /// <summary>是否在等待卸载程序窗口（界面显示「请在弹出的卸载窗口里完成操作」）</summary>
        public bool WaitingForUser { get; set; }

        public UninstallProgress()
        {
            Percent = -1;
        }
    }

    public delegate void UninstallProgressHandler(UninstallProgress progress);

    /// <summary>卸载结果</summary>
    public class UninstallResult
    {
        public bool Success { get; set; }
        /// <summary>卸载器跑完了但残留没清干净</summary>
        public bool PartialSuccess { get; set; }
        public UninstallStage FinalStage { get; set; }
        public string ErrorMessage { get; set; }
        public string Summary { get; set; }

        public string DisplayName { get; set; }
        public string InstallPath { get; set; }
        /// <summary>卸载程序退出码（-1 表示没跑起来）</summary>
        public int UninstallerExitCode { get; set; }
        public bool UninstallerRan { get; set; }
        /// <summary>卸载程序是否超时未退出（此时残留清理会被跳过，避免和安装器抢文件）</summary>
        public bool TimedOut { get; set; }

        public bool InstallDirRemoved { get; set; }
        public bool RegistryCleaned { get; set; }
        public int ShortcutsRemoved { get; set; }
        /// <summary>实际释放的字节数</summary>
        public long FreedBytes { get; set; }
        /// <summary>清理掉的残留路径</summary>
        public List<string> RemovedItems { get; set; }
        /// <summary>没能清理的残留（含原因）</summary>
        public List<string> Leftovers { get; set; }
        public List<string> Warnings { get; set; }

        /// <summary>注册表备份目录（失败时可手动恢复）</summary>
        public string BackupFolder { get; set; }
        /// <summary>失败时生成的详细诊断日志路径</summary>
        public string FailureLogPath { get; set; }

        public UninstallResult()
        {
            UninstallerExitCode = -1;
            RemovedItems = new List<string>();
            Leftovers = new List<string>();
            Warnings = new List<string>();
            FinalStage = UninstallStage.Idle;
        }
    }
}
