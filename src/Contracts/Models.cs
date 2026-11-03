// ============================================================================
//  一键挪动 (OneKeyMove) - 共享数据模型（契约文件，Lead 维护，禁止队友修改结构）
//  目标框架: .NET Framework 4.8 / C# 5 (系统内置 csc.exe 编译)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;

namespace YijianNuodong.Core
{
    /// <summary>程序信息的来源（可组合，用位或合并）</summary>
    [Flags]
    public enum ProgramSource
    {
        None = 0,
        Shortcut = 1,          // 桌面 / 开始菜单快捷方式
        Registry = 2,          // 控制面板卸载信息注册表
        AppPaths = 4,          // 注册表 App Paths
        StartMenu = 8,         // 开始菜单快捷方式扫描
        RunningProcess = 16,   // 正在运行的进程
        FolderScan = 32,       // 常见安装目录扫描
        ManualPath = 64        // 用户手动指定
    }

    /// <summary>一个可被搬家的已安装程序</summary>
    public class InstalledProgram
    {
        /// <summary>稳定标识（规范化后的安装目录，小写）</summary>
        public string Id { get; set; }
        /// <summary>显示名称，如 "微信"</summary>
        public string DisplayName { get; set; }
        /// <summary>安装目录绝对路径（不带尾部斜杠），如 C:\Program Files\Tencent\WeChat</summary>
        public string InstallPath { get; set; }
        /// <summary>主程序可执行文件路径（可能为空）</summary>
        public string ExecutablePath { get; set; }
        /// <summary>发布者</summary>
        public string Publisher { get; set; }
        /// <summary>版本号</summary>
        public string Version { get; set; }
        /// <summary>大小（字节），小于 0 表示未知</summary>
        public long SizeBytes { get; set; }
        /// <summary>来源</summary>
        public ProgramSource Source { get; set; }
        /// <summary>卸载命令</summary>
        public string UninstallString { get; set; }
        /// <summary>来源快捷方式路径（若由 .lnk 解析而来）</summary>
        public string ShortcutPath { get; set; }
        /// <summary>命中的别名（用于展示“通过 微信/WeChat 匹配”），可空</summary>
        public string AliasMatched { get; set; }
        /// <summary>是否正在运行</summary>
        public bool IsRunning { get; set; }
        /// <summary>所在盘符根目录，如 "C:\"</summary>
        public string DriveRoot { get; set; }
        /// <summary>界面显示用图标（可为 null，UI 会按需提取）</summary>
        public Icon Icon { get; set; }

        public InstalledProgram()
        {
            SizeBytes = -1;
            Source = ProgramSource.None;
        }

        public bool IsOnSystemDrive
        {
            get
            {
                if (string.IsNullOrEmpty(DriveRoot)) return false;
                string sys = PathUtil.GetDriveRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                return string.Equals(DriveRoot, sys, StringComparison.OrdinalIgnoreCase);
            }
        }

        public override string ToString()
        {
            return (DisplayName ?? string.Empty) + "  [" + (InstallPath ?? string.Empty) + "]";
        }
    }

    /// <summary>驱动器信息</summary>
    public class DriveInfoItem
    {
        public string RootPath { get; set; }       // "D:\"
        public string VolumeLabel { get; set; }
        public string FileSystem { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public bool IsSystemDrive { get; set; }

        public long UsedBytes { get { return TotalBytes - FreeBytes; } }
        public int UsedPercent
        {
            get
            {
                if (TotalBytes <= 0) return 0;
                long p = UsedBytes * 100 / TotalBytes;
                if (p < 0) p = 0;
                if (p > 100) p = 100;
                return (int)p;
            }
        }

        public string DisplayText
        {
            get
            {
                string label = string.IsNullOrEmpty(VolumeLabel) ? "本地磁盘" : VolumeLabel;
                return RootPath + "  " + label + "  可用 " + TextUtil.FormatBytes(FreeBytes) + " / " + TextUtil.FormatBytes(TotalBytes);
            }
        }
    }

    /// <summary>占用文件的进程</summary>
    public class LockingProcess
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string ExecutablePath { get; set; }
        public string MainWindowTitle { get; set; }
        public bool HasMainWindow { get; set; }
    }

    /// <summary>与目标目录相关的服务</summary>
    public class LockingService
    {
        public string ServiceName { get; set; }
        public string DisplayName { get; set; }
        public string ImagePath { get; set; }
        public string State { get; set; }
        public bool CanStop { get; set; }
    }

    /// <summary>目录占用检测结果</summary>
    public class LockInfo
    {
        public string Path { get; set; }
        public List<LockingProcess> Processes { get; set; }
        public List<LockingService> Services { get; set; }
        public List<string> Notes { get; set; }

        public LockInfo()
        {
            Processes = new List<LockingProcess>();
            Services = new List<LockingService>();
            Notes = new List<string>();
        }

        public bool IsLocked
        {
            get { return Processes.Count > 0 || Services.Count > 0; }
        }
    }

    /// <summary>移动前预检结果</summary>
    public class PrecheckResult
    {
        public bool CanProceed { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }
        public LockInfo Locks { get; set; }
        public long SourceBytes { get; set; }
        public long TargetFreeBytes { get; set; }
        public string TargetDirectory { get; set; }
        public bool TargetOnSameVolume { get; set; }
        public bool IsProtectedSystemProgram { get; set; }
        public string ProtectedReason { get; set; }

        public PrecheckResult()
        {
            Errors = new List<string>();
            Warnings = new List<string>();
            SourceBytes = -1;
            TargetFreeBytes = -1;
        }

        /// <summary>空间是否足够（未知大小按 -1 处理为未知）</summary>
        public bool HasEnoughSpace
        {
            get
            {
                if (SourceBytes < 0 || TargetFreeBytes < 0) return true;
                // 预留 200MB 余量
                return TargetFreeBytes > SourceBytes + (200L * 1024 * 1024);
            }
        }
    }

    /// <summary>移动请求</summary>
    public class MoveRequest
    {
        public InstalledProgram Program { get; set; }
        /// <summary>完整目标目录（含软件文件夹名），如 D:\Software\WeChat</summary>
        public string TargetDirectory { get; set; }
        /// <summary>是否在原位置创建目录联接（推荐开启）</summary>
        public bool CreateJunction { get; set; }
        /// <summary>是否自动结束占用进程（必须由用户显式确认）</summary>
        public bool KillBlockingProcesses { get; set; }
        /// <summary>是否停止并迁移相关服务</summary>
        public bool StopRelatedServices { get; set; }
        /// <summary>是否更新桌面/开始菜单快捷方式</summary>
        public bool UpdateShortcuts { get; set; }
        /// <summary>是否更新注册表中的路径值</summary>
        public bool UpdateRegistry { get; set; }
        /// <summary>是否创建系统还原点（尽力而为）</summary>
        public bool CreateRestorePoint { get; set; }

        public MoveRequest()
        {
            CreateJunction = true;
            UpdateShortcuts = true;
            UpdateRegistry = true;
            StopRelatedServices = true;
        }
    }

    /// <summary>移动阶段</summary>
    public enum MoveStage
    {
        Idle = 0,
        Precheck = 1,
        ResolvingLocks = 2,
        WaitingForLock = 3,
        Preparing = 4,
        Copying = 5,
        Verifying = 6,
        RemovingSource = 7,
        CreatingJunction = 8,
        UpdatingReferences = 9,
        Finalizing = 10,
        Completed = 11,
        Failed = 12,
        Canceled = 13,
        RolledBack = 14,
        PendingReboot = 15
    }

    /// <summary>进度回调数据</summary>
    public class MoveProgress
    {
        public MoveStage Stage { get; set; }
        /// <summary>阶段中文名，如 "正在复制文件"</summary>
        public string StageText { get; set; }
        /// <summary>详细消息</summary>
        public string Message { get; set; }
        public string CurrentFile { get; set; }
        /// <summary>总体进度 0-100，未知时为 -1</summary>
        public int Percent { get; set; }
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }
        public int FilesDone { get; set; }
        public int FilesTotal { get; set; }

        public MoveProgress()
        {
            Percent = -1;
            BytesTotal = -1;
        }
    }

    /// <summary>进度回调</summary>
    public delegate void MoveProgressHandler(MoveProgress progress);

    /// <summary>移动结果</summary>
    public class MoveResult
    {
        public bool Success { get; set; }
        public MoveStage FinalStage { get; set; }
        public string ErrorMessage { get; set; }
        public string Summary { get; set; }
        public string OldPath { get; set; }
        public string NewPath { get; set; }
        public string JunctionPath { get; set; }
        public string JournalPath { get; set; }
        public long FreedBytes { get; set; }
        public List<string> Warnings { get; set; }
        public int ShortcutsUpdated { get; set; }
        public int RegistryEntriesUpdated { get; set; }
        public bool JunctionCreated { get; set; }
        public bool PendingReboot { get; set; }
        public string PendingJobFile { get; set; }
        /// <summary>搬移后是否重建/确认了桌面快捷方式</summary>
        public bool DesktopShortcutCreated { get; set; }
        /// <summary>桌面快捷方式路径（重建或已存在并指向新位置时给出）</summary>
        public string DesktopShortcutPath { get; set; }
        /// <summary>清理掉的失效/重复快捷方式数量</summary>
        public int ShortcutsRemoved { get; set; }
        /// <summary>失败时生成的详细诊断日志文件路径（便于用户反馈与后续优化）</summary>
        public string FailureLogPath { get; set; }

        public MoveResult()
        {
            Warnings = new List<string>();
            FinalStage = MoveStage.Idle;
        }
    }

    /// <summary>注册表值变更记录（用于撤销）</summary>
    public class RegistryChange
    {
        /// <summary>"HKLM" 或 "HKCU"</summary>
        public string Hive { get; set; }
        public string KeyPath { get; set; }
        public string ValueName { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        /// <summary>"String" / "ExpandString" / "MultiString"</summary>
        public string ValueKind { get; set; }
    }

    /// <summary>移动日志条目（写入日记文件，支持撤销）</summary>
    public class JournalEntry
    {
        public string Version { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string OldPath { get; set; }
        public string NewPath { get; set; }
        public bool JunctionCreated { get; set; }
        public long FreedBytes { get; set; }
        public bool SourceDeleted { get; set; }
        public string DisplayName { get; set; }
        public List<string> UpdatedShortcuts { get; set; }
        public List<RegistryChange> RegistryChanges { get; set; }

        public JournalEntry()
        {
            Version = "1";
            UpdatedShortcuts = new List<string>();
            RegistryChanges = new List<RegistryChange>();
        }
    }

    /// <summary>快捷方式解析结果</summary>
    public class ShortcutInfo
    {
        public string ShortcutPath { get; set; }
        public string TargetPath { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string IconLocation { get; set; }
        public string Description { get; set; }
    }

    /// <summary>程序索引（内存中的已安装软件列表）</summary>
    public class SearchIndex
    {
        public List<InstalledProgram> Programs { get; set; }
        public DateTime BuiltAt { get; set; }

        public SearchIndex()
        {
            Programs = new List<InstalledProgram>();
        }

        public int Count { get { return Programs.Count; } }
    }

    /// <summary>系统还原点创建结果</summary>
    public class RestorePointResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}
