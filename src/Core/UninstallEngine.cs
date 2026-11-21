// ============================================================================
//  一键挪动 (OneKeyMove) - 卸载引擎
//  目标框架: .NET Framework 4.8 / C# 5 (系统内置 csc.exe 编译)
//
//  对外只有四个入口：IsUninstallSupported / FindRegistryEntries / Precheck / Execute。
//  设计原则：
//    1. 任何注册表访问都 try/catch，绝不让异常冒出去；
//    2. 任何删除操作都先过 CanDeleteDirectory（非空、非盘符根、非系统关键目录、
//       非本程序自身目录），删不掉一律写入 Leftovers，绝不静默；
//    3. 不结束系统关键进程，只删除 Uninstall 之下的注册表键；
//    4. 卸载器超时不杀进程（用户可能正在点向导），只跳过残留清理。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace YijianNuodong.Core
{
    /// <summary>
    /// 软件卸载引擎。
    /// </summary>
    public static class UninstallEngine
    {
        #region 常量与静态字段

        private const int DefaultTimeoutSeconds = 600;
        private const int UninstallerWaitPollMs = 500;
        private const int CompletionPollTotalSeconds = 60;
        private const int CompletionPollIntervalMs = 2000;
        private const int GracefulCloseWaitMs = 2000;
        private const int RegTimeoutMs = 60000;
        private const int RegDeleteTimeoutMs = 30000;
        private const int MaxRecentLogLines = 40;

        /// <summary>MSI 产品代码 {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}</summary>
        private static readonly Regex ProductCodeRegex = new Regex(
            @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}",
            RegexOptions.CultureInvariant);

        /// <summary>不带大括号的 GUID（少数注册表键名是这种形式）</summary>
        private static readonly Regex BareGuidRegex = new Regex(
            @"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$",
            RegexOptions.CultureInvariant);

        /// <summary>绝不结束的进程（名字包含以下任一片段即跳过）</summary>
        private static readonly string[] CriticalProcessNames = new string[]
        {
            "system", "wininit", "csrss", "services", "lsass", "smss",
            "winlogon", "explorer", "msmpeng"
        };

        /// <summary>禁止卸载的系统组件名单（名称已去掉空格并转小写后做包含匹配）</summary>
        private static readonly string[] ProtectedComponentNames = new string[]
        {
            ".netframework",
            "microsoft.netframework",
            ".netruntime",
            "windowsdesktopruntime",
            "windowsappruntime",
            "windowsruntime",
            "windowssdk",
            "microsoftedge",
            "windowssubsystemfor",
            "microsoftwindowsdesktopruntime"
        };

        private static readonly string[] UninstallKeySubPath = new string[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        private static readonly Func<bool> AlwaysFalseDelegate = new Func<bool>(ReturnFalse);
        private static readonly UninstallProgressHandler IgnoreProgressDelegate =
            new UninstallProgressHandler(IgnoreProgressCallback);

        #endregion

        #region 对外入口

        /// <summary>是否具备卸载条件（列表里用来决定「卸载」按钮是否可点）</summary>
        public static bool IsUninstallSupported(InstalledProgram program)
        {
            if (program == null) return false;
            try
            {
                if (!string.IsNullOrEmpty(program.UninstallString)) return true;
                if (!string.IsNullOrEmpty(program.InstallPath))
                {
                    return Directory.Exists(program.InstallPath);
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        /// <summary>枚举注册表卸载信息项（HKLM 64/32 位 + HKCU），只返回属于该软件的项</summary>
        public static List<UninstallRegistryEntry> FindRegistryEntries(InstalledProgram program)
        {
            List<UninstallRegistryEntry> result = new List<UninstallRegistryEntry>();
            if (program == null) return result;

            try
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1) HKLM 64 位
                ScanHive(result, seen, RegistryHive.LocalMachine, RegistryView.Registry64,
                    UninstallKeySubPath[0], program);
                // 2) HKLM 32 位：显式 WOW6432Node 路径（reg.exe 备份/删除时同样使用该完整路径）
                bool scanned32 = ScanHive(result, seen, RegistryHive.LocalMachine, RegistryView.Registry32,
                    UninstallKeySubPath[1], program);
                // 某些系统上 32 位视图不暴露显式 WOW6432Node 路径，退回平面路径再扫一次
                if (!scanned32)
                {
                    ScanHive(result, seen, RegistryHive.LocalMachine, RegistryView.Registry32,
                        UninstallKeySubPath[0], program);
                }
                // 3) HKCU
                ScanHive(result, seen, RegistryHive.CurrentUser, RegistryView.Registry64,
                    UninstallKeySubPath[0], program);
            }
            catch (Exception ex)
            {
                Log.Warn("枚举注册表卸载项失败：" + ex.Message);
            }

            Log.Info("注册表卸载项匹配完成：[" + (program.DisplayName ?? string.Empty) + "] 命中 "
                + result.Count + " 条。");
            return result;
        }

        /// <summary>预检：判断卸载方式、风险、占用、将删除的内容</summary>
        public static UninstallTarget Precheck(InstalledProgram program)
        {
            UninstallTarget target = new UninstallTarget();
            target.Program = program;
            try
            {
                if (program == null)
                {
                    target.Errors.Add("卸载目标为空，无法预检。");
                    target.CanProceed = false;
                    return target;
                }

                // ---------- 名称 ----------
                target.DisplayName = TextUtil.Clean(program.DisplayName);
                if (target.DisplayName.Length == 0) target.DisplayName = "未知软件";

                // ---------- 安装目录（处理「搬家后原路径是目录联接」） ----------
                string originalPath = PathUtil.Normalize(program.InstallPath);
                string installPath = originalPath;
                if (originalPath.Length > 0 && PathUtil.IsJunctionOrLink(originalPath))
                {
                    target.LinkPath = originalPath;
                    target.InstallPathIsJunction = true;
                    string realTarget;
                    if (Junction.TryGetTarget(originalPath, out realTarget) && !string.IsNullOrEmpty(realTarget))
                    {
                        installPath = PathUtil.Normalize(realTarget);
                        target.Warnings.Add("该软件之前被搬到其他盘（原路径是目录联接），卸载会同时清理联接和真实目录："
                            + installPath);
                    }
                    else
                    {
                        target.Warnings.Add("原路径是目录联接，但无法解析真实目录，将按原路径处理：" + originalPath);
                    }
                }
                target.InstallPath = installPath;

                // ---------- 系统目录保护（提前判定） ----------
                // 放在体积扫描之前：像 C:\Windows 这种目标根本不允许卸载，
                // 没必要（也很慢）去统计几十万个文件，直接终止预检。
                if (installPath.Length > 0 && IsProtectedInstallPath(installPath))
                {
                    target.IsProtected = true;
                    target.ProtectedReason = "这是 Windows 系统目录、用户目录或盘符根目录，删除会导致系统无法启动。";
                    target.Errors.Add("这是 Windows 系统组件目录，禁止卸载：" + installPath);
                    target.CanProceed = false;
                    Log.Warn("卸载预检：目标位于受保护路径，已提前终止：" + installPath);
                    return target;
                }

                // ---------- 目录大小 / 文件数 ----------
                if (installPath.Length > 0 && Directory.Exists(installPath))
                {
                    try
                    {
                        target.InstallBytes = FileUtil.GetDirectorySize(installPath, null);
                    }
                    catch (Exception ex)
                    {
                        target.InstallBytes = -1;
                        Log.Warn("统计安装目录大小失败：" + ex.Message);
                    }
                    try
                    {
                        target.FileCount = FileUtil.CountFiles(installPath);
                    }
                    catch (Exception ex)
                    {
                        target.FileCount = 0;
                        Log.Warn("统计安装目录文件数失败：" + ex.Message);
                    }
                }

                // ---------- 注册表卸载项 ----------
                target.RegistryEntries = FindRegistryEntries(program);

                // ---------- 卸载方式 ----------
                UninstallRegistryEntry msiEntry = null;
                UninstallRegistryEntry commandEntry = null;
                for (int i = 0; i < target.RegistryEntries.Count; i++)
                {
                    UninstallRegistryEntry entry = target.RegistryEntries[i];
                    if (entry == null) continue;
                    bool looksMsi = !string.IsNullOrEmpty(entry.ProductCode) || entry.WindowsInstaller;
                    if (looksMsi && msiEntry == null) msiEntry = entry;
                    if (commandEntry == null && !string.IsNullOrEmpty(TextUtil.Clean(entry.UninstallString)))
                    {
                        commandEntry = entry;
                    }
                }

                if (msiEntry != null)
                {
                    target.Method = UninstallMethod.MsiProduct;
                    target.IsMsi = true;
                    target.ProductCode = msiEntry.ProductCode ?? string.Empty;
                    if (target.ProductCode.Length > 0)
                    {
                        target.Command = "MsiExec.exe /X" + target.ProductCode;
                    }
                    else
                    {
                        // WindowsInstaller=1 但没解析出产品代码：退回到注册表命令（Execute 会按命令执行）
                        string fallback = TextUtil.Clean(msiEntry.UninstallString);
                        target.Command = fallback;
                    }
                }
                else if (commandEntry != null)
                {
                    target.Method = UninstallMethod.RegistryCommand;
                    target.Command = TextUtil.Clean(commandEntry.UninstallString);
                }
                else if (!string.IsNullOrEmpty(TextUtil.Clean(program.UninstallString)))
                {
                    target.Method = UninstallMethod.RegistryCommand;
                    target.Command = TextUtil.Clean(program.UninstallString);
                }
                else
                {
                    target.Method = UninstallMethod.ManualOnly;
                    target.Command = string.Empty;
                    target.Warnings.Add("这个软件没有登记官方卸载程序，只能手工删除安装目录和快捷方式。删除前请确认它没有后台服务或开机启动项，否则会留下占用。");
                }

                target.UninstallerExe = ResolveUninstallerExe(target.Command);

                // ---------- 是否需要管理员 ----------
                bool hklmEntry = false;
                for (int i = 0; i < target.RegistryEntries.Count; i++)
                {
                    UninstallRegistryEntry entry = target.RegistryEntries[i];
                    if (entry != null && string.Equals(entry.Hive, "HKLM", StringComparison.OrdinalIgnoreCase))
                    {
                        hklmEntry = true;
                        break;
                    }
                }
                target.RequiresAdmin = target.Method == UninstallMethod.MsiProduct
                    || IsUnderProgramFiles(installPath)
                    || hklmEntry;
                if (target.RequiresAdmin && !ElevationUtil.IsElevated)
                {
                    target.Errors.Add("需要以管理员身份运行才能卸载这个软件。");
                }

                // ---------- 保护判定（最重要） ----------
                EvaluateProtection(target, program);

                // ---------- 占用检测 ----------
                LockInfo locks = new LockInfo();
                locks.Path = installPath;
                if (installPath.Length > 0 && Directory.Exists(installPath))
                {
                    try
                    {
                        locks = LockInspector.Inspect(installPath, null);
                        if (locks == null) locks = new LockInfo();
                    }
                    catch (Exception ex)
                    {
                        locks = new LockInfo();
                        locks.Path = installPath;
                        target.Warnings.Add("占用检测失败（不影响继续操作，但可能因占用导致清理不完整）：" + ex.Message);
                        Log.Warn("卸载预检占用检测失败：" + ex.Message);
                    }
                }
                target.Locks = locks;
                if (locks.Processes != null && locks.Processes.Count > 0)
                {
                    target.Warnings.Add("检测到 " + locks.Processes.Count + " 个正在使用该程序的进程："
                        + DescribeProcesses(locks.Processes)
                        + "。执行时可勾选「自动结束占用进程」。");
                }
                if (locks.Services != null && locks.Services.Count > 0)
                {
                    target.Warnings.Add("检测到 " + locks.Services.Count + " 个相关服务："
                        + DescribeServices(locks.Services) + "。");
                    if (!ElevationUtil.IsElevated)
                    {
                        target.Errors.Add("检测到 " + locks.Services.Count
                            + " 个相关服务，需要以管理员身份运行才能停止它们。");
                    }
                }

                // ---------- 快捷方式 ----------
                if (installPath.Length > 0)
                {
                    try
                    {
                        List<string> shortcuts = ShortcutUpdater.FindShortcutsPointingTo(installPath);
                        if (shortcuts != null) target.Shortcuts = shortcuts;
                    }
                    catch (Exception ex)
                    {
                        target.Warnings.Add("查找快捷方式失败（不影响卸载）：" + ex.Message);
                        Log.Warn("查找快捷方式失败：" + ex.Message);
                    }
                }

                // ---------- 可能的用户数据目录（默认保留） ----------
                CollectDataFolders(target, installPath);

                // ---------- 给人看的步骤 ----------
                target.Steps.Add("1. 备份注册表卸载项");
                target.Steps.Add("2. 关闭正在运行的程序");
                target.Steps.Add("3. 运行官方卸载程序");
                target.Steps.Add("4. 校验是否卸载干净");
                target.Steps.Add("5. 清理残留文件与快捷方式");

                // ---------- 兜底：目录为空且没有卸载项 ----------
                bool dirExists = installPath.Length > 0 && Directory.Exists(installPath);
                if (!dirExists && target.RegistryEntries.Count == 0)
                {
                    target.Errors.Add("找不到安装目录，也没有可用的卸载程序。");
                    target.Method = UninstallMethod.ManualOnly;
                    target.Command = string.Empty;
                    target.UninstallerExe = string.Empty;
                }
                else if (!dirExists)
                {
                    target.Warnings.Add("安装目录不存在，将只清理注册表卸载项与快捷方式。");
                }
                else if (target.Method == UninstallMethod.ManualOnly)
                {
                    target.Warnings.Add("没有可用的官方卸载程序，将直接删除安装目录，请确认后再继续。");
                }
            }
            catch (Exception ex)
            {
                target.Errors.Add("预检时发生错误：" + ex.Message);
                Log.Error("卸载预检异常", ex);
            }

            target.CanProceed = target.Errors.Count == 0;
            Log.Info("卸载预检完成：[" + (target.DisplayName ?? string.Empty) + "] 可继续=" + target.CanProceed
                + "，方式=" + target.Method.ToString()
                + "，错误 " + target.Errors.Count + " 条，警告 " + target.Warnings.Count + " 条。");
            return target;
        }

        /// <summary>执行卸载。progress 可为 null；isCancelled 返回 true 时中止（已做的清理不回滚）</summary>
        public static UninstallResult Execute(UninstallRequest request, UninstallProgressHandler progress,
            Func<bool> isCancelled)
        {
            UninstallResult result = new UninstallResult();
            UninstallContext context = new UninstallContext();
            context.Result = result;
            context.Request = request;
            context.IsCancelled = isCancelled == null ? AlwaysFalseDelegate : isCancelled;
            UninstallProgressHandler report = progress == null ? IgnoreProgressDelegate : progress;

            try
            {
                if (request == null)
                {
                    result.FinalStage = UninstallStage.Failed;
                    result.ErrorMessage = "卸载请求为空。";
                    context.LastStage = UninstallStage.Precheck;
                    result.FailureLogPath = WriteFailureLog(result, context, "无（没有可执行的卸载请求）");
                    return result;
                }

                UninstallTarget target = request.Target;
                context.Target = target;
                if (target == null)
                {
                    result.FinalStage = UninstallStage.Failed;
                    result.ErrorMessage = "卸载目标为空，请先执行预检。";
                    context.LastStage = UninstallStage.Precheck;
                    result.FailureLogPath = WriteFailureLog(result, context, "无（没有可执行的卸载目标）");
                    return result;
                }

                context.DisplayName = string.IsNullOrEmpty(target.DisplayName) ? "未知软件" : target.DisplayName;
                context.InstallPath = target.InstallPath;
                context.Command = target.Command;
                result.DisplayName = context.DisplayName;
                result.InstallPath = target.InstallPath;
                if (target.Warnings != null) result.Warnings.AddRange(target.Warnings);
                if (target.Locks != null) context.Locks = target.Locks;

                // ---------- 1. 预检 ----------
                context.LastStage = UninstallStage.Precheck;
                Report(report, UninstallStage.Precheck, "正在预检", "确认卸载方式、权限与将清理的内容…", 1);
                Log.Info("卸载开始：[" + context.DisplayName + "] 路径=" + (context.InstallPath ?? string.Empty)
                    + " 方式=" + target.Method.ToString());

                if (target.Errors != null && target.Errors.Count > 0)
                {
                    return FailEarly(context, report, "预检未通过：" + JoinText(target.Errors, "；"));
                }
                if (!target.CanProceed)
                {
                    return FailEarly(context, report, "预检未通过：卸载目标不可继续。");
                }
                // 没有官方卸载程序时必须由用户显式确认，否则一次手误就会删掉整个目录
                if (target.Method == UninstallMethod.ManualOnly && !request.ConfirmManualDelete)
                {
                    return FailEarly(context, report,
                        "这个软件没有官方卸载程序，需要手工删除安装目录。请先勾选「我确认手工清理」再继续。");
                }
                if (IsCancelled(context))
                {
                    context.Canceled = true;
                    return Finish(context, report);
                }

                // ---------- 2. 系统还原点（尽力而为） ----------
                if (request.CreateRestorePoint)
                {
                    context.LastStage = UninstallStage.BackingUp;
                    Report(report, UninstallStage.BackingUp, "正在备份", "尝试创建系统还原点…", 3);
                    string restoreError;
                    if (!TryCreateRestorePoint(context.DisplayName, out restoreError))
                    {
                        result.Warnings.Add("创建系统还原点失败（已跳过，不影响卸载）：" + restoreError);
                        Log.Warn("创建系统还原点失败：" + restoreError);
                    }
                    else
                    {
                        Log.Info("已创建系统还原点。");
                    }
                }
                if (IsCancelled(context))
                {
                    context.Canceled = true;
                    return Finish(context, report);
                }

                // ---------- 3. 备份注册表卸载项 ----------
                context.LastStage = UninstallStage.BackingUp;
                Report(report, UninstallStage.BackingUp, "正在备份", "导出注册表卸载项…", 5);
                if (request.BackupRegistry)
                {
                    BackupRegistry(context, report);
                }
                else
                {
                    Log.Info("按用户设置跳过注册表备份。");
                }
                if (IsCancelled(context))
                {
                    context.Canceled = true;
                    return Finish(context, report);
                }

                // ---------- 4. 关闭占用进程 ----------
                if (request.CloseRunningPrograms)
                {
                    context.LastStage = UninstallStage.ClosingProgram;
                    Report(report, UninstallStage.ClosingProgram, "正在关闭程序", "结束占用安装目录的进程…", 12);
                    CloseRunningPrograms(context, report);
                }
                else
                {
                    Log.Info("按用户设置跳过结束占用进程。");
                }
                if (IsCancelled(context))
                {
                    context.Canceled = true;
                    return Finish(context, report);
                }

                // ---------- 5. 运行官方卸载程序 ----------
                context.LastStage = UninstallStage.RunningUninstaller;
                Report(report, UninstallStage.RunningUninstaller, "正在运行官方卸载程序", "启动卸载程序…", 20);
                RunUninstaller(context, report);
                if (context.TimedOut)
                {
                    // 超时：不杀进程、不清理（避免与卸载器抢文件），直接收尾
                    return Finish(context, report);
                }
                if (IsCancelled(context))
                {
                    context.Canceled = true;
                    return Finish(context, report);
                }

                // ---------- 6. 校验 ----------
                context.LastStage = UninstallStage.Verifying;
                Report(report, UninstallStage.Verifying, "正在校验", "检查安装目录与注册表项是否已清理…", 55);
                Verify(context, report);
                if (IsCancelled(context))
                {
                    context.Canceled = true;
                    return Finish(context, report);
                }

                // ---------- 7. 清理残留 ----------
                if (request.RemoveLeftovers)
                {
                    context.LastStage = UninstallStage.CleaningLeftovers;
                    Report(report, UninstallStage.CleaningLeftovers, "正在清理残留",
                        "清理安装目录、快捷方式、注册表项与空文件夹…", 60);
                    CleanLeftovers(context, report);
                }
                else
                {
                    result.Warnings.Add("未勾选「清理残留」，已跳过安装目录、快捷方式与注册表项的清理。");
                    Log.Info("按用户设置跳过残留清理。");
                }

                // ---------- 8. 收尾 ----------
                context.LastStage = UninstallStage.Finalizing;
                return Finish(context, report);
            }
            catch (Exception ex)
            {
                context.LastException = ex;
                result.Success = false;
                result.PartialSuccess = false;
                result.FinalStage = UninstallStage.Failed;
                result.ErrorMessage = "卸载过程中发生错误（" + StageText(context.LastStage) + "）：" + ex.Message;
                Log.Error("卸载失败[" + context.LastStage.ToString() + "]：" + ex.Message, ex);
                Report(report, UninstallStage.Failed, "卸载失败", result.ErrorMessage, 100);
                result.FailureLogPath = WriteFailureLog(result, context, "未回滚（已删除的内容保留）");
                return result;
            }
        }

        #endregion

        #region 注册表枚举与匹配

        /// <summary>扫描一处卸载注册表位置；返回是否成功打开了该位置</summary>
        private static bool ScanHive(List<UninstallRegistryEntry> results, HashSet<string> seen,
            RegistryHive hive, RegistryView view, string subKeyPath, InstalledProgram program)
        {
            RegistryKey baseKey = null;
            RegistryKey uninstallKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                if (baseKey == null) return false;
                uninstallKey = baseKey.OpenSubKey(subKeyPath, false);
                if (uninstallKey == null) return false;

                string hiveName = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
                string[] names;
                try
                {
                    names = uninstallKey.GetSubKeyNames();
                }
                catch (Exception ex)
                {
                    Log.Warn("枚举卸载项失败（" + hiveName + " / " + view.ToString() + "）：" + ex.Message);
                    return true;
                }

                for (int i = 0; i < names.Length; i++)
                {
                    string keyName = names[i];
                    RegistryKey subKey = null;
                    try
                    {
                        subKey = uninstallKey.OpenSubKey(keyName, false);
                        if (subKey == null) continue;
                        UninstallRegistryEntry entry = ReadEntry(subKey, hiveName, subKeyPath, keyName);
                        if (entry == null) continue;
                        if (!MatchesProgram(program, entry)) continue;

                        string dedupeKey = entry.Hive + "|" + entry.KeyName + "|"
                            + (entry.UninstallString ?? string.Empty);
                        if (!seen.Add(dedupeKey)) continue;
                        results.Add(entry);
                    }
                    catch (SecurityException ex)
                    {
                        Log.Warn("读取卸载项无权限（" + keyName + "）：" + ex.Message);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Log.Warn("读取卸载项被拒绝（" + keyName + "）：" + ex.Message);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("读取卸载项失败（" + keyName + "）：" + ex.Message);
                    }
                    finally
                    {
                        if (subKey != null)
                        {
                            try { subKey.Close(); }
                            catch (Exception) { }
                        }
                    }
                }
                return true;
            }
            catch (SecurityException ex)
            {
                Log.Warn("打开卸载注册表位置无权限（" + hive.ToString() + " / " + view.ToString()
                    + "）：" + ex.Message);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warn("打开卸载注册表位置被拒绝（" + hive.ToString() + " / " + view.ToString()
                    + "）：" + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn("打开卸载注册表位置失败（" + hive.ToString() + " / " + view.ToString()
                    + "）：" + ex.Message);
                return false;
            }
            finally
            {
                if (uninstallKey != null)
                {
                    try { uninstallKey.Close(); }
                    catch (Exception) { }
                }
                if (baseKey != null)
                {
                    try { baseKey.Close(); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>从一条卸载项读取字段；失败返回 null</summary>
        private static UninstallRegistryEntry ReadEntry(RegistryKey subKey, string hiveName,
            string subKeyPath, string keyName)
        {
            try
            {
                UninstallRegistryEntry entry = new UninstallRegistryEntry();
                entry.Hive = hiveName;
                entry.KeyPath = subKeyPath + "\\" + keyName;
                entry.KeyName = keyName;
                entry.DisplayName = ReadString(subKey, "DisplayName");
                entry.DisplayVersion = ReadString(subKey, "DisplayVersion");
                entry.Publisher = ReadString(subKey, "Publisher");
                entry.InstallLocation = ReadString(subKey, "InstallLocation");
                entry.UninstallString = ReadString(subKey, "UninstallString");
                entry.QuietUninstallString = ReadString(subKey, "QuietUninstallString");
                entry.DisplayIcon = ReadString(subKey, "DisplayIcon");
                entry.WindowsInstaller = ReadInt(subKey, "WindowsInstaller") == 1;
                entry.EstimatedSizeKB = ReadLong(subKey, "EstimatedSize");
                entry.ProductCode = ResolveProductCode(keyName, entry.UninstallString,
                    entry.QuietUninstallString, entry.WindowsInstaller);
                return entry;
            }
            catch (Exception ex)
            {
                Log.Warn("解析卸载项失败（" + keyName + "）：" + ex.Message);
                return null;
            }
        }

        /// <summary>该注册表项是否属于给定软件（命中任一规则）</summary>
        private static bool MatchesProgram(InstalledProgram program, UninstallRegistryEntry entry)
        {
            if (program == null || entry == null) return false;

            string targetPath = PathUtil.Normalize(program.InstallPath);
            string realPath = targetPath;
            if (targetPath.Length > 0 && PathUtil.IsJunctionOrLink(targetPath))
            {
                string resolved;
                if (Junction.TryGetTarget(targetPath, out resolved) && !string.IsNullOrEmpty(resolved))
                {
                    realPath = PathUtil.Normalize(resolved);
                }
            }
            string exePath = PathUtil.Normalize(program.ExecutablePath);

            // 1) InstallLocation 相同或互为子路径（含搬家后的联接解析）
            string installLocation = PathUtil.Normalize(entry.InstallLocation);
            if (installLocation.Length > 0)
            {
                if (PathMatches(installLocation, targetPath) || PathMatches(installLocation, realPath))
                {
                    return true;
                }
                if (PathUtil.IsJunctionOrLink(installLocation))
                {
                    string locationTarget;
                    if (Junction.TryGetTarget(installLocation, out locationTarget)
                        && !string.IsNullOrEmpty(locationTarget))
                    {
                        string resolvedLocation = PathUtil.Normalize(locationTarget);
                        if (PathMatches(resolvedLocation, targetPath) || PathMatches(resolvedLocation, realPath))
                        {
                            return true;
                        }
                    }
                }
            }

            // 2) DisplayName 相等（忽略大小写与空格）
            if (NamesEqual(program.DisplayName, entry.DisplayName)) return true;

            // 3) 卸载命令里出现安装目录或主程序路径
            if (ContainsPath(entry.UninstallString, targetPath)
                || ContainsPath(entry.UninstallString, realPath)
                || ContainsPath(entry.UninstallString, exePath)) return true;
            if (ContainsPath(entry.QuietUninstallString, targetPath)
                || ContainsPath(entry.QuietUninstallString, realPath)
                || ContainsPath(entry.QuietUninstallString, exePath)) return true;

            return false;
        }

        /// <summary>两个路径是否相同或互为父子目录</summary>
        private static bool PathMatches(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (PathUtil.SamePath(a, b)) return true;
            if (PathUtil.IsSubPathOf(a, b)) return true;
            if (PathUtil.IsSubPathOf(b, a)) return true;
            return false;
        }

        /// <summary>显示名是否相等（忽略大小写、忽略所有空白字符）</summary>
        private static bool NamesEqual(string a, string b)
        {
            string na = NormalizeName(a);
            string nb = NormalizeName(b);
            if (na.Length == 0 || nb.Length == 0) return false;
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>去掉空白与商标符号并转小写，用于名称比较</summary>
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (char.IsWhiteSpace(ch)) continue;
                if (ch == '\u00ae' || ch == '\u2122' || ch == '\u00a9') continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>文本里是否包含指定路径（忽略大小写）</summary>
        private static bool ContainsPath(string text, string path)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(path)) return false;
            if (path.Length <= 3) return false;
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(text);
                return expanded.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>解析 MSI 产品代码</summary>
        private static string ResolveProductCode(string keyName, string uninstallString,
            string quietUninstallString, bool windowsInstaller)
        {
            // 键名本身就是 GUID（MSI 的标准形式）
            string code = ExtractProductCode(keyName);
            if (code.Length > 0) return code;

            // 只有 MSI 相关命令才从命令行里抓 GUID，避免把普通卸载器的参数误判成产品代码
            bool msiHint = windowsInstaller
                || ContainsIgnoreCase(uninstallString, "msiexec")
                || ContainsIgnoreCase(quietUninstallString, "msiexec");
            if (!msiHint) return string.Empty;

            code = ExtractProductCode(uninstallString);
            if (code.Length > 0) return code;
            code = ExtractProductCode(quietUninstallString);
            if (code.Length > 0) return code;
            return string.Empty;
        }

        /// <summary>从文本中提取第一个 {GUID}（支持不带大括号的纯 GUID）</summary>
        private static string ExtractProductCode(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            try
            {
                Match match = ProductCodeRegex.Match(text);
                if (match.Success) return match.Value.ToUpperInvariant();
                string trimmed = text.Trim();
                if (BareGuidRegex.IsMatch(trimmed)) return ("{" + trimmed + "}").ToUpperInvariant();
            }
            catch (Exception)
            {
                return string.Empty;
            }
            return string.Empty;
        }

        private static bool ContainsIgnoreCase(string text, string value)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value)) return false;
            return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadString(RegistryKey key, string name)
        {
            try
            {
                object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) return string.Empty;
                string text = value as string;
                if (text != null) return TextUtil.Clean(text);
                return TextUtil.Clean(value.ToString());
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static int ReadInt(RegistryKey key, string name)
        {
            try
            {
                object value = key.GetValue(name, null);
                if (value == null) return 0;
                return Convert.ToInt32(value);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static long ReadLong(RegistryKey key, string name)
        {
            try
            {
                object value = key.GetValue(name, null);
                if (value == null) return 0;
                long parsed;
                if (long.TryParse(value.ToString(), out parsed)) return parsed;
                return 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        #endregion

        #region 预检辅助

        /// <summary>保护判定：系统路径 / 系统组件名单 / Microsoft 官方组件提示</summary>
        /// <summary>路径本身是否属于绝不允许卸载的位置（不查注册表，可在预检早期直接调用）</summary>
        private static bool IsProtectedInstallPath(string installPath)
        {
            if (string.IsNullOrEmpty(installPath)) return false;
            try
            {
                if (PathUtil.IsSystemCriticalPath(installPath)) return true;
            }
            catch (Exception)
            {
                return true; // 判断失败按危险处理
            }
            try
            {
                string windowsDir = PathUtil.Normalize(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                if (windowsDir.Length > 0
                    && (PathUtil.SamePath(installPath, windowsDir) || PathUtil.IsSubPathOf(installPath, windowsDir)))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                return true;
            }
            return false;
        }

        private static void EvaluateProtection(UninstallTarget target, InstalledProgram program)
        {
            string installPath = target.InstallPath ?? string.Empty;

            // 1) 系统关键路径
            if (installPath.Length > 0)
            {
                bool critical = false;
                try
                {
                    critical = PathUtil.IsSystemCriticalPath(installPath);
                }
                catch (Exception)
                {
                    critical = true; // 判断失败时按危险处理
                }
                string windowsDir = string.Empty;
                try
                {
                    windowsDir = PathUtil.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                }
                catch (Exception)
                {
                    windowsDir = string.Empty;
                }
                bool underWindows = windowsDir.Length > 0
                    && (PathUtil.SamePath(installPath, windowsDir)
                        || PathUtil.IsSubPathOf(installPath, windowsDir));

                if (critical || underWindows)
                {
                    target.IsProtected = true;
                    target.ProtectedReason = "这是 Windows 系统目录、用户目录或盘符根目录，删除会导致系统无法启动。";
                    target.Errors.Add("这是 Windows 系统组件目录，禁止卸载：" + installPath);
                }
            }

            // 2) 系统组件名单（显示名 / 注册表键名）
            string matchedName;
            bool nameBlocked = IsProtectedComponentName(target.DisplayName, out matchedName);
            if (!nameBlocked)
            {
                for (int i = 0; i < target.RegistryEntries.Count && !nameBlocked; i++)
                {
                    UninstallRegistryEntry entry = target.RegistryEntries[i];
                    if (entry == null) continue;
                    if (IsProtectedComponentName(entry.DisplayName, out matchedName)
                        || IsProtectedComponentName(entry.KeyName, out matchedName))
                    {
                        nameBlocked = true;
                    }
                }
            }
            if (nameBlocked)
            {
                target.IsProtected = true;
                if (string.IsNullOrEmpty(target.ProtectedReason))
                {
                    target.ProtectedReason = "这是 Windows 系统组件（" + matchedName
                        + "），卸载会导致依赖它的软件无法运行。";
                }
                target.Errors.Add("这是系统组件，禁止卸载：" + target.DisplayName);
            }

            // 3) Microsoft 官方组件：不封锁，只提示
            string publisher = TextUtil.Clean(program.Publisher);
            if (publisher.Length == 0)
            {
                for (int i = 0; i < target.RegistryEntries.Count; i++)
                {
                    UninstallRegistryEntry entry = target.RegistryEntries[i];
                    if (entry != null && !string.IsNullOrEmpty(entry.Publisher))
                    {
                        publisher = TextUtil.Clean(entry.Publisher);
                        break;
                    }
                }
            }
            if (!target.IsProtected && ContainsIgnoreCase(publisher, "microsoft"))
            {
                target.Warnings.Add("这是 Microsoft 官方组件，卸载可能影响其他软件，请确认后再继续。");
            }
        }

        /// <summary>名称是否命中系统组件名单；命中时返回命中的片段</summary>
        private static bool IsProtectedComponentName(string name, out string matched)
        {
            matched = null;
            string normalized = NormalizeName(name);
            if (normalized.Length == 0) return false;
            for (int i = 0; i < ProtectedComponentNames.Length; i++)
            {
                if (normalized.IndexOf(ProtectedComponentNames[i], StringComparison.Ordinal) >= 0)
                {
                    matched = ProtectedComponentNames[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>安装目录是否位于 Program Files（含根目录）下</summary>
        private static bool IsUnderProgramFiles(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string[] roots = new string[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                };
                for (int i = 0; i < roots.Length; i++)
                {
                    string root = PathUtil.Normalize(roots[i]);
                    if (root.Length == 0) continue;
                    if (PathUtil.SamePath(path, root) || PathUtil.IsSubPathOf(path, root)) return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        /// <summary>收集可能的用户数据目录（存在才加入，默认不删）</summary>
        private static void CollectDataFolders(UninstallTarget target, string installPath)
        {
            List<string> candidates = new List<string>();
            try
            {
                string leaf = PathUtil.GetLeafName(installPath, target.DisplayName);
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                if (!string.IsNullOrEmpty(appData) && !string.IsNullOrEmpty(leaf))
                    candidates.Add(Path.Combine(appData, leaf));
                if (!string.IsNullOrEmpty(localAppData) && !string.IsNullOrEmpty(leaf))
                    candidates.Add(Path.Combine(localAppData, leaf));
                if (!string.IsNullOrEmpty(localAppData) && !string.IsNullOrEmpty(leaf))
                    candidates.Add(Path.Combine(Path.Combine(localAppData, "Programs"), leaf));
                if (!string.IsNullOrEmpty(documents) && !string.IsNullOrEmpty(target.DisplayName))
                    candidates.Add(Path.Combine(documents, target.DisplayName));
                else if (!string.IsNullOrEmpty(documents) && !string.IsNullOrEmpty(userProfile)
                    && !string.IsNullOrEmpty(leaf))
                    candidates.Add(Path.Combine(documents, leaf));
            }
            catch (Exception ex)
            {
                Log.Warn("收集用户数据目录失败：" + ex.Message);
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];
                try
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    if (!Directory.Exists(candidate)) continue;
                    if (PathMatches(candidate, installPath)) continue;
                    bool duplicate = false;
                    for (int j = 0; j < target.DataFolders.Count; j++)
                    {
                        if (PathUtil.SamePath(target.DataFolders[j], candidate)) { duplicate = true; break; }
                    }
                    if (!duplicate) target.DataFolders.Add(PathUtil.Normalize(candidate));
                }
                catch (Exception ex)
                {
                    Log.Warn("检查用户数据目录失败（" + candidate + "）：" + ex.Message);
                }
            }
        }

        /// <summary>从卸载命令解析可执行文件路径</summary>
        private static string ResolveUninstallerExe(string command)
        {
            string text = TextUtil.Clean(command);
            if (text.Length == 0) return string.Empty;
            try
            {
                string fileName;
                string arguments;
                ParseCommand(text, out fileName, out arguments);
                if (string.IsNullOrEmpty(fileName)) return string.Empty;
                string resolved = ResolveExecutablePath(fileName, null);
                return resolved;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        #endregion

        #region 执行：备份

        /// <summary>导出所有命中的注册表卸载项，并写一份恢复说明</summary>
        private static void BackupRegistry(UninstallContext context, UninstallProgressHandler progress)
        {
            UninstallResult result = context.Result;
            UninstallTarget target = context.Target;
            try
            {
                List<UninstallRegistryEntry> entries = target.RegistryEntries;
                if (entries == null || entries.Count == 0)
                {
                    result.Warnings.Add("没有可备份的注册表卸载项。");
                    Log.Warn("没有可备份的注册表卸载项。");
                    return;
                }

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string leaf = BuildSafeLeaf(context);
                string directory = Path.Combine(AppPaths.LogDir, "uninstall_backup", stamp + "_" + leaf);
                context.BackupFolder = directory;
                result.BackupFolder = directory;

                string ensureError;
                if (!FileUtil.EnsureDirectory(directory, out ensureError))
                {
                    result.Warnings.Add("创建注册表备份目录失败，已跳过备份：" + ensureError);
                    Log.Warn("创建注册表备份目录失败：" + ensureError);
                    return;
                }

                List<string> backupLines = new List<string>();
                int index = 1;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (IsCancelled(context)) return;
                    UninstallRegistryEntry entry = entries[i];
                    if (entry == null) continue;
                    if (!IsUninstallKeyPath(entry.Hive, entry.KeyPath))
                    {
                        result.Warnings.Add("注册表路径不在 Uninstall 之下，已跳过备份：" + entry.KeyPath);
                        Log.Warn("拒绝备份非 Uninstall 注册表路径：" + entry.KeyPath);
                        continue;
                    }

                    string fullPath = entry.Hive + "\\" + entry.KeyPath;
                    string safeKey = PathUtil.SanitizeFolderName(entry.KeyName);
                    if (safeKey.Length == 0) safeKey = "entry";
                    string fileName = index.ToString("00") + "_" + safeKey + ".reg";
                    string filePath = Path.Combine(directory, fileName);

                    Report(progress, UninstallStage.BackingUp, "正在备份",
                        "导出卸载项 " + fullPath, 6);

                    string output;
                    int exitCode = RunReg(new string[] { "export", fullPath, filePath, "/y" },
                        RegTimeoutMs, out output);
                    if (exitCode == 0 && File.Exists(filePath))
                    {
                        entry.BackupFilePath = filePath;
                        backupLines.Add(fullPath + "  ->  " + fileName);
                        Log.Info("已备份注册表卸载项：" + fullPath + " -> " + filePath);
                    }
                    else
                    {
                        result.Warnings.Add("备份注册表项失败：" + fullPath
                            + "（reg.exe 退出码 " + exitCode + "）");
                        Log.Warn("备份注册表项失败：" + fullPath + "，输出：" + output);
                    }
                    index++;
                }

                WriteRecoveryNotes(directory, context, backupLines);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("备份注册表时出错（已跳过，不影响卸载）：" + ex.Message);
                Log.Warn("备份注册表时出错：" + ex.Message);
            }
        }

        /// <summary>写「如何恢复.txt」</summary>
        private static void WriteRecoveryNotes(string directory, UninstallContext context, List<string> backupLines)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== 一键挪动 - 注册表卸载项备份 ===");
                sb.AppendLine("备份时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("软件名称: " + SafeText(context.DisplayName));
                sb.AppendLine("安装路径: " + SafeText(context.InstallPath));
                sb.AppendLine();
                sb.AppendLine("如何恢复：");
                sb.AppendLine("1. 双击本目录下的 .reg 文件，在提示框里选择「是」，即可恢复被删除的卸载项。");
                sb.AppendLine("2. 如果系统提示需要管理员权限，请右键 .reg 文件选择「合并」，或以管理员身份运行。");
                sb.AppendLine("3. 恢复后「设置 -> 应用」里会重新出现该软件条目（程序文件不会恢复）。");
                sb.AppendLine();
                sb.AppendLine("注意：本备份只恢复注册表里的卸载信息，不恢复已被删除的程序文件。");
                sb.AppendLine();
                sb.AppendLine("原始注册表路径（" + backupLines.Count + " 条）：");
                if (backupLines.Count == 0)
                {
                    sb.AppendLine("（本次没有任何注册表项备份成功）");
                }
                else
                {
                    for (int i = 0; i < backupLines.Count; i++)
                    {
                        sb.AppendLine((i + 1).ToString() + ". " + backupLines[i]);
                    }
                }
                string path = Path.Combine(directory, "如何恢复.txt");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log.Warn("写入恢复说明失败：" + ex.Message);
            }
        }

        #endregion

        #region 执行：关闭进程

        /// <summary>结束占用安装目录的进程（跳过系统关键进程）</summary>
        private static void CloseRunningPrograms(UninstallContext context, UninstallProgressHandler progress)
        {
            UninstallResult result = context.Result;
            LockInfo locks = context.Locks;
            if (locks == null || locks.Processes == null || locks.Processes.Count == 0)
            {
                Log.Info("没有需要结束的占用进程。");
                return;
            }

            int currentPid = 0;
            try
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    currentPid = current.Id;
                }
            }
            catch (Exception)
            {
                currentPid = 0;
            }

            for (int i = 0; i < locks.Processes.Count; i++)
            {
                if (IsCancelled(context)) return;
                LockingProcess lockingProcess = locks.Processes[i];
                if (lockingProcess == null) continue;
                if (lockingProcess.ProcessId <= 0) continue;
                if (lockingProcess.ProcessId == currentPid) continue;

                string name = string.IsNullOrEmpty(lockingProcess.ProcessName)
                    ? "未知进程" : lockingProcess.ProcessName;

                if (IsCriticalProcessName(name))
                {
                    Log.Warn("跳过系统关键进程：" + name + "（PID " + lockingProcess.ProcessId + "）");
                    result.Warnings.Add("已跳过系统关键进程 " + name + "，未结束它。");
                    continue;
                }

                if (!ProcessHelper.IsRunning(lockingProcess.ProcessId)) continue;

                Report(progress, UninstallStage.ClosingProgram, "正在关闭程序",
                    "关闭进程 " + name + "（PID " + lockingProcess.ProcessId + "）", 13);

                string error;
                bool closed = ProcessHelper.TryCloseGracefully(lockingProcess.ProcessId,
                    GracefulCloseWaitMs, out error);
                if (closed)
                {
                    Log.Info("已优雅关闭进程：" + name + "（PID " + lockingProcess.ProcessId + "）");
                    continue;
                }

                if (!ProcessHelper.IsRunning(lockingProcess.ProcessId)) continue;

                bool killed = ProcessHelper.TryKill(lockingProcess.ProcessId, out error);
                if (killed)
                {
                    Log.Info("已强制结束进程：" + name + "（PID " + lockingProcess.ProcessId + "）");
                }
                else
                {
                    Log.Warn("无法结束进程：" + name + "（PID " + lockingProcess.ProcessId + "）：" + error);
                    result.Warnings.Add("无法结束进程 " + name + "（PID " + lockingProcess.ProcessId
                        + "）：" + error);
                }
            }

            // 重新检测一次，仍有占用只警告、不阻断
            LockInfo after = null;
            if (!string.IsNullOrEmpty(context.InstallPath) && Directory.Exists(context.InstallPath))
            {
                try
                {
                    after = LockInspector.Inspect(context.InstallPath, null);
                }
                catch (Exception ex)
                {
                    Log.Warn("重新检测占用失败：" + ex.Message);
                }
            }
            if (after != null && after.Processes != null && after.Processes.Count > 0)
            {
                result.Warnings.Add("文件仍被占用（" + after.Processes.Count + " 个进程），卸载可能不完整。");
                Log.Warn("文件仍被占用，卸载可能不完整。");
            }
            context.Locks = after;
        }

        /// <summary>是否系统关键进程（名字包含即跳过）</summary>
        private static bool IsCriticalProcessName(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            string name = processName.ToLowerInvariant();
            for (int i = 0; i < CriticalProcessNames.Length; i++)
            {
                if (name.IndexOf(CriticalProcessNames[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        #endregion

        #region 执行：运行卸载程序

        /// <summary>运行官方卸载程序并等待退出（超时不杀进程）</summary>
        private static void RunUninstaller(UninstallContext context, UninstallProgressHandler progress)
        {
            UninstallTarget target = context.Target;
            UninstallResult result = context.Result;
            bool silent = context.Request.SilentWhenPossible;

            if (target.Method == UninstallMethod.ManualOnly)
            {
                result.Warnings.Add("没有可用的官方卸载程序，已跳过卸载器，只清理文件与注册表项。");
                Log.Warn("卸载方式为 ManualOnly，跳过卸载程序。");
                return;
            }

            string fileName = string.Empty;
            string arguments = string.Empty;
            bool isMsi = target.Method == UninstallMethod.MsiProduct;
            bool msiWithCode = isMsi && !string.IsNullOrEmpty(target.ProductCode);

            if (msiWithCode)
            {
                fileName = GetMsiExecPath();
                arguments = "/X" + target.ProductCode + (silent ? " /qn" : string.Empty) + " /norestart";
            }
            else
            {
                string command = TextUtil.Clean(target.Command);
                if (command.Length == 0)
                {
                    result.Warnings.Add("卸载命令为空，无法运行官方卸载程序，只清理文件与注册表项。");
                    Log.Warn("卸载命令为空，跳过卸载程序。");
                    return;
                }
                string rawFileName;
                string rawArguments;
                ParseCommand(command, out rawFileName, out rawArguments);
                if (isMsi) rawArguments = RewriteMsiArguments(rawArguments);
                fileName = ResolveExecutablePath(rawFileName, context.InstallPath);
                arguments = rawArguments;
                if (string.IsNullOrEmpty(fileName))
                {
                    result.Warnings.Add("无法从卸载命令解析出可执行文件：" + command);
                    Log.Warn("无法解析卸载命令：" + command);
                    return;
                }
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments;
            startInfo.UseShellExecute = true;
            try
            {
                if (!string.IsNullOrEmpty(context.InstallPath) && Directory.Exists(context.InstallPath))
                {
                    startInfo.WorkingDirectory = context.InstallPath;
                }
            }
            catch (Exception)
            {
                // 工作目录设置失败不影响启动
            }

            bool interactive = !(msiWithCode && silent);
            if (interactive)
            {
                Report(progress, UninstallStage.RunningUninstaller, "正在运行官方卸载程序",
                    "请在弹出的卸载窗口里完成操作…", 25, null, true);
            }

            Log.Info("运行卸载程序：" + fileName + " " + arguments);

            Process process = null;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("启动卸载程序失败：" + ex.Message);
                Log.Error("启动卸载程序失败：" + fileName + " " + arguments, ex);
                return;
            }
            if (process == null)
            {
                result.Warnings.Add("启动卸载程序失败：系统未返回进程句柄。");
                Log.Warn("启动卸载程序失败：系统未返回进程句柄。");
                return;
            }

            context.UninstallerRan = true;
            result.UninstallerRan = true;

            int timeoutSeconds = context.Request.TimeoutSeconds > 0
                ? context.Request.TimeoutSeconds : DefaultTimeoutSeconds;
            long deadlineMs = (long)timeoutSeconds * 1000L;
            long waitedMs = 0;
            bool exited = false;

            while (true)
            {
                if (IsCancelled(context))
                {
                    context.Canceled = true;
                    Log.Warn("用户取消：卸载程序可能仍在后台运行，未强制结束它。");
                    result.Warnings.Add("用户取消时卸载程序可能仍在后台运行，残留清理已跳过。");
                    break;
                }

                bool done;
                try
                {
                    done = process.WaitForExit(UninstallerWaitPollMs);
                }
                catch (Exception)
                {
                    done = true;
                }
                if (done)
                {
                    exited = true;
                    break;
                }

                waitedMs += UninstallerWaitPollMs;
                if (waitedMs >= deadlineMs)
                {
                    context.TimedOut = true;
                    break;
                }
            }

            if (context.TimedOut)
            {
                result.TimedOut = true;
                result.UninstallerRan = true;
                result.UninstallerExitCode = -1;
                result.Warnings.Add("卸载程序超时未结束（超过 " + timeoutSeconds
                    + " 秒），残留清理已跳过，以免与卸载程序争抢文件。");
                Log.Warn("卸载程序超时未结束（" + timeoutSeconds + " 秒），跳过残留清理。");
                return;
            }

            if (!exited)
            {
                // 用户取消
                result.UninstallerRan = true;
                result.UninstallerExitCode = -1;
                try { process.Dispose(); }
                catch (Exception) { }
                return;
            }

            int exitCode = -1;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception)
            {
                exitCode = -1;
            }
            try { process.Dispose(); }
            catch (Exception) { }

            context.ExitCode = exitCode;
            result.UninstallerExitCode = exitCode;
            result.UninstallerRan = true;

            if (exitCode != 0 && exitCode != 3010)
            {
                result.Warnings.Add("卸载程序返回退出码 " + exitCode + "（0 或 3010 表示成功），卸载可能未完成。");
                Log.Warn("卸载程序返回退出码 " + exitCode + "。");
            }
            else
            {
                Log.Info("卸载程序正常结束，退出码 " + exitCode + "。");
            }

            // 很多卸载器会先退出、由子进程继续干活，这里最多等 60 秒
            WaitForCompletion(context, progress);
        }

        /// <summary>等待卸载真正完成（目录与注册表项都消失，最多 60 秒）</summary>
        private static void WaitForCompletion(UninstallContext context, UninstallProgressHandler progress)
        {
            UninstallTarget target = context.Target;
            if (target == null) return;

            bool dirGone = string.IsNullOrEmpty(context.InstallPath)
                || !Directory.Exists(context.InstallPath);
            bool regGone = !AnyRegistryEntryExists(target);
            if (dirGone && regGone) return;

            Report(progress, UninstallStage.WaitingForUninstaller, "正在等待卸载完成",
                "卸载器已退出，等待后台进程完成清理…", 45);

            int waitedMs = 0;
            while (waitedMs < CompletionPollTotalSeconds * 1000)
            {
                if (IsCancelled(context))
                {
                    context.Canceled = true;
                    return;
                }
                try
                {
                    Thread.Sleep(CompletionPollIntervalMs);
                }
                catch (Exception)
                {
                    // 忽略
                }
                waitedMs += CompletionPollIntervalMs;

                dirGone = string.IsNullOrEmpty(context.InstallPath)
                    || !Directory.Exists(context.InstallPath);
                regGone = !AnyRegistryEntryExists(target);
                if (dirGone && regGone)
                {
                    Log.Info("安装目录与注册表卸载项均已清理。");
                    return;
                }
            }
            Log.Info("等待卸载完成超时（" + CompletionPollTotalSeconds + " 秒），继续后续校验与清理。");
        }

        /// <summary>把卸载命令拆成 (可执行文件, 参数)，处理引号与 msiexec</summary>
        private static void ParseCommand(string command, out string fileName, out string arguments)
        {
            fileName = string.Empty;
            arguments = string.Empty;
            string text = TextUtil.Clean(command);
            if (text.Length == 0) return;

            if (text[0] == '"')
            {
                int end = text.IndexOf('"', 1);
                if (end > 1)
                {
                    fileName = text.Substring(1, end - 1).Trim();
                    arguments = text.Substring(end + 1).Trim();
                    return;
                }
            }

            int exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex > 0)
            {
                int cut = exeIndex + 4;
                fileName = text.Substring(0, cut).Trim();
                arguments = text.Substring(cut).Trim();
                return;
            }

            int spaceIndex = text.IndexOf(' ');
            if (spaceIndex > 0)
            {
                fileName = text.Substring(0, spaceIndex).Trim();
                arguments = text.Substring(spaceIndex + 1).Trim();
            }
            else
            {
                fileName = text;
            }
        }

        /// <summary>把 msiexec /I{GUID} 改写成 /X{GUID}</summary>
        private static string RewriteMsiArguments(string arguments)
        {
            if (string.IsNullOrEmpty(arguments)) return arguments;
            string trimmed = arguments.TrimStart();
            if (trimmed.Length >= 2 && trimmed[0] == '/'
                && char.ToUpperInvariant(trimmed[1]) == 'I')
            {
                return "/X" + trimmed.Substring(2);
            }
            return arguments;
        }

        /// <summary>解析可执行文件路径（展开环境变量、补全 msiexec、相对安装目录）</summary>
        private static string ResolveExecutablePath(string rawFile, string installPath)
        {
            if (string.IsNullOrEmpty(rawFile)) return string.Empty;
            string file = rawFile.Trim().Trim('"');
            if (file.Length == 0) return string.Empty;
            try
            {
                file = Environment.ExpandEnvironmentVariables(file);
            }
            catch (Exception)
            {
                // 保持原样
            }

            string name = string.Empty;
            try
            {
                name = Path.GetFileName(file);
            }
            catch (Exception)
            {
                name = string.Empty;
            }

            if (string.Equals(name, "msiexec.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "msiexec", StringComparison.OrdinalIgnoreCase))
            {
                bool rooted = false;
                try { rooted = Path.IsPathRooted(file); }
                catch (Exception) { rooted = false; }
                if (!rooted || !File.Exists(file)) return GetMsiExecPath();
            }

            if (File.Exists(file)) return PathUtil.Normalize(file);

            bool isRooted = false;
            try { isRooted = Path.IsPathRooted(file); }
            catch (Exception) { isRooted = false; }

            if (!isRooted && !string.IsNullOrEmpty(installPath))
            {
                try
                {
                    string candidate = Path.Combine(installPath, file);
                    if (File.Exists(candidate)) return PathUtil.Normalize(candidate);
                }
                catch (Exception)
                {
                    // 忽略
                }
            }
            return file;
        }

        /// <summary>msiexec.exe 绝对路径</summary>
        private static string GetMsiExecPath()
        {
            try
            {
                string systemDir = Environment.SystemDirectory;
                if (!string.IsNullOrEmpty(systemDir))
                {
                    string path = Path.Combine(systemDir, "msiexec.exe");
                    if (File.Exists(path)) return path;
                }
            }
            catch (Exception)
            {
                // 继续尝试
            }
            try
            {
                string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windowsDir))
                {
                    string path = Path.Combine(Path.Combine(windowsDir, "System32"), "msiexec.exe");
                    if (File.Exists(path)) return path;
                }
            }
            catch (Exception)
            {
                // 继续尝试
            }
            return "msiexec.exe";
        }

        #endregion

        #region 执行：校验与清理

        /// <summary>校验安装目录与注册表项是否仍在</summary>
        private static void Verify(UninstallContext context, UninstallProgressHandler progress)
        {
            bool dirExists = !string.IsNullOrEmpty(context.InstallPath)
                && Directory.Exists(context.InstallPath);
            bool regExists = AnyRegistryEntryExists(context.Target);

            context.Result.InstallDirRemoved = !dirExists;
            context.Result.RegistryCleaned = !regExists;

            string message = "安装目录" + (dirExists ? "仍存在" : "已删除")
                + "，注册表卸载项" + (regExists ? "仍存在" : "已清理") + "。";
            Log.Info("卸载校验：" + message);
            Report(progress, UninstallStage.Verifying, "正在校验", message, 55);
        }

        /// <summary>清理残留：联接、安装目录、快捷方式、注册表项、空开始菜单父目录、用户数据</summary>
        private static void CleanLeftovers(UninstallContext context, UninstallProgressHandler progress)
        {
            UninstallTarget target = context.Target;
            UninstallResult result = context.Result;
            UninstallRequest request = context.Request;

            string measurePath = context.InstallPath;
            long freeBefore = GetFreeSpace(measurePath);
            long installBytes = 0;
            if (!string.IsNullOrEmpty(context.InstallPath) && Directory.Exists(context.InstallPath))
            {
                try
                {
                    installBytes = FileUtil.GetDirectorySize(context.InstallPath, context.IsCancelled);
                }
                catch (Exception)
                {
                    installBytes = 0;
                }
                if (installBytes < 0) installBytes = 0;
            }

            // 1) 目录联接
            if (target.InstallPathIsJunction && !string.IsNullOrEmpty(target.LinkPath))
            {
                if (IsCancelled(context)) { context.Canceled = true; return; }
                string error;
                if (Junction.Remove(target.LinkPath, out error))
                {
                    result.RemovedItems.Add("目录联接：" + target.LinkPath);
                    Log.Info("已删除目录联接：" + target.LinkPath);
                }
                else
                {
                    result.Leftovers.Add("目录联接未能删除：" + target.LinkPath + "（" + error + "）");
                    Log.Warn("目录联接未能删除：" + target.LinkPath + "：" + error);
                }
            }

            // 2) 安装目录
            if (!string.IsNullOrEmpty(context.InstallPath) && Directory.Exists(context.InstallPath))
            {
                if (IsCancelled(context)) { context.Canceled = true; return; }
                string reason;
                if (!CanDeleteDirectory(context.InstallPath, out reason))
                {
                    result.Leftovers.Add("安装目录保留：" + context.InstallPath + "（" + reason + "）");
                    Log.Warn("安装目录拒绝删除：" + context.InstallPath + "：" + reason);
                }
                else
                {
                    Report(progress, UninstallStage.CleaningLeftovers, "正在清理残留",
                        "删除安装目录 " + context.InstallPath, 70);
                    try
                    {
                        FileUtil.ClearReadOnlyRecursive(PathUtil.ToLongPath(context.InstallPath));
                        FileUtil.ClearReadOnlyRecursive(context.InstallPath);
                        string deleteError;
                        if (FileUtil.TryDeleteDirectory(context.InstallPath, out deleteError))
                        {
                            context.InstallDirDeleted = true;
                            result.InstallDirRemoved = true;
                            result.RemovedItems.Add("安装目录：" + context.InstallPath);
                            Log.Info("已删除安装目录：" + context.InstallPath);
                        }
                        else
                        {
                            result.Leftovers.Add("安装目录：" + context.InstallPath
                                + "（删除失败：" + deleteError + "）");
                            Log.Warn("删除安装目录失败：" + context.InstallPath + "：" + deleteError);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Leftovers.Add("安装目录：" + context.InstallPath + "（删除异常：" + ex.Message + "）");
                        Log.Warn("删除安装目录异常：" + context.InstallPath + "：" + ex.Message);
                    }
                }
            }
            else
            {
                context.InstallDirDeleted = true;
            }

            // 3) 快捷方式
            List<string> deletedShortcuts = new List<string>();
            if (target.Shortcuts != null)
            {
                for (int i = 0; i < target.Shortcuts.Count; i++)
                {
                    if (IsCancelled(context)) { context.Canceled = true; return; }
                    string shortcut = target.Shortcuts[i];
                    if (string.IsNullOrEmpty(shortcut)) continue;
                    try
                    {
                        if (!File.Exists(shortcut)) continue;
                        File.Delete(shortcut);
                        result.ShortcutsRemoved++;
                        result.RemovedItems.Add("快捷方式：" + shortcut);
                        deletedShortcuts.Add(shortcut);
                        Log.Info("已删除快捷方式：" + shortcut);
                    }
                    catch (Exception ex)
                    {
                        result.Leftovers.Add("快捷方式：" + shortcut + "（删除失败：" + ex.Message + "）");
                        Log.Warn("删除快捷方式失败：" + shortcut + "：" + ex.Message);
                    }
                }
            }

            // 4) 注册表卸载项
            if (target.RegistryEntries != null)
            {
                for (int i = 0; i < target.RegistryEntries.Count; i++)
                {
                    if (IsCancelled(context)) { context.Canceled = true; return; }
                    UninstallRegistryEntry entry = target.RegistryEntries[i];
                    if (entry == null) continue;
                    if (!IsUninstallKeyPath(entry.Hive, entry.KeyPath))
                    {
                        result.Leftovers.Add("注册表项未删除（路径不在 Uninstall 之下，已拒绝）："
                            + entry.KeyPath);
                        Log.Warn("拒绝删除非 Uninstall 注册表路径：" + entry.KeyPath);
                        continue;
                    }
                    string fullPath = entry.Hive + "\\" + entry.KeyPath;
                    string output;
                    int exitCode = RunReg(new string[] { "delete", fullPath, "/f" },
                        RegDeleteTimeoutMs, out output);
                    if (exitCode == 0)
                    {
                        result.RemovedItems.Add("注册表项：" + fullPath);
                        Log.Info("已删除注册表卸载项：" + fullPath);
                    }
                    else
                    {
                        result.Leftovers.Add("注册表项未能删除：" + fullPath
                            + "（reg.exe 退出码 " + exitCode + "）");
                        Log.Warn("删除注册表项失败：" + fullPath + "，输出：" + output);
                    }
                }
            }

            // 5) 变空的开始菜单父文件夹（最多往上两层）
            for (int i = 0; i < deletedShortcuts.Count; i++)
            {
                if (IsCancelled(context)) { context.Canceled = true; return; }
                CleanEmptyShortcutParents(deletedShortcuts[i], result);
            }

            // 6) 用户数据目录（仅在用户显式勾选时）
            long userDataBytes = 0;
            if (request.RemoveUserData && target.DataFolders != null)
            {
                for (int i = 0; i < target.DataFolders.Count; i++)
                {
                    if (IsCancelled(context)) { context.Canceled = true; return; }
                    string folder = target.DataFolders[i];
                    if (string.IsNullOrEmpty(folder)) continue;
                    string reason;
                    if (!CanDeleteDataFolder(folder, out reason))
                    {
                        result.Leftovers.Add("用户数据目录保留：" + folder + "（" + reason + "）");
                        Log.Warn("用户数据目录拒绝删除：" + folder + "：" + reason);
                        continue;
                    }
                    if (!Directory.Exists(folder)) continue;
                    long size = 0;
                    try { size = FileUtil.GetDirectorySize(folder, null); }
                    catch (Exception) { size = 0; }
                    if (size < 0) size = 0;

                    Report(progress, UninstallStage.CleaningLeftovers, "正在清理残留",
                        "删除用户数据 " + folder, 80);
                    try
                    {
                        FileUtil.ClearReadOnlyRecursive(folder);
                        string deleteError;
                        if (FileUtil.TryDeleteDirectory(folder, out deleteError))
                        {
                            userDataBytes += size;
                            result.RemovedItems.Add("用户数据：" + folder);
                            Log.Info("已删除用户数据目录：" + folder);
                        }
                        else
                        {
                            result.Leftovers.Add("用户数据目录：" + folder
                                + "（删除失败：" + deleteError + "）");
                            Log.Warn("删除用户数据目录失败：" + folder + "：" + deleteError);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Leftovers.Add("用户数据目录：" + folder + "（删除异常：" + ex.Message + "）");
                        Log.Warn("删除用户数据目录异常：" + folder + "：" + ex.Message);
                    }
                }
            }
            else if (target.DataFolders != null && target.DataFolders.Count > 0)
            {
                Log.Info("按用户设置保留用户数据目录（" + target.DataFolders.Count + " 个）。");
            }

            // 7) 注册表清理状态
            result.RegistryCleaned = !AnyRegistryEntryExists(target);

            // 8) 释放空间：可用空间差与目录大小估算取较大值，绝不为负
            long freeAfter = GetFreeSpace(measurePath);
            long spaceDiff = 0;
            if (freeBefore >= 0 && freeAfter >= 0 && freeAfter > freeBefore)
            {
                spaceDiff = freeAfter - freeBefore;
            }
            long estimate = userDataBytes;
            if (context.InstallDirDeleted) estimate += installBytes;
            long freed = spaceDiff > estimate ? spaceDiff : estimate;
            if (freed < 0) freed = 0;
            context.FreedBytes = freed;
            result.FreedBytes = freed;
            Log.Info("卸载释放空间估算：" + TextUtil.FormatBytes(freed)
                + "（可用空间差 " + TextUtil.FormatBytes(spaceDiff)
                + "，目录估算 " + TextUtil.FormatBytes(estimate) + "）");
        }

        /// <summary>删除因快捷方式消失而变空的开始菜单父目录（最多两层，且只删 Programs 之下）</summary>
        private static void CleanEmptyShortcutParents(string shortcutPath, UninstallResult result)
        {
            string directory = string.Empty;
            try
            {
                directory = Path.GetDirectoryName(shortcutPath);
            }
            catch (Exception)
            {
                return;
            }

            for (int level = 0; level < 2; level++)
            {
                if (string.IsNullOrEmpty(directory)) return;
                if (!IsUnderStartMenu(directory)) return;
                if (IsStartMenuProgramsRoot(directory)) return;
                if (!Directory.Exists(directory))
                {
                    directory = GetParentDirectory(directory);
                    continue;
                }
                if (!FileUtil.IsDirectoryEmpty(directory)) return;
                string reason;
                if (!CanDeleteDirectory(directory, out reason)) return;
                string error;
                if (!FileUtil.TryDeleteDirectory(directory, out error)) return;
                result.RemovedItems.Add("空开始菜单文件夹：" + directory);
                Log.Info("已删除空开始菜单文件夹：" + directory);
                directory = GetParentDirectory(directory);
            }
        }

        private static string GetParentDirectory(string path)
        {
            try
            {
                return Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>目录是否位于开始菜单 Programs 之下</summary>
        private static bool IsUnderStartMenu(string directory)
        {
            string[] roots = StartMenuProgramsRoots();
            for (int i = 0; i < roots.Length; i++)
            {
                string root = roots[i];
                if (root.Length == 0) continue;
                if (PathUtil.SamePath(directory, root) || PathUtil.IsSubPathOf(directory, root)) return true;
            }
            return false;
        }

        /// <summary>目录是否就是开始菜单 Programs 根目录</summary>
        private static bool IsStartMenuProgramsRoot(string directory)
        {
            string[] roots = StartMenuProgramsRoots();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].Length == 0) continue;
                if (PathUtil.SamePath(directory, roots[i])) return true;
            }
            return false;
        }

        private static string[] StartMenuProgramsRoots()
        {
            string common = string.Empty;
            string perUser = string.Empty;
            try
            {
                common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            }
            catch (Exception)
            {
                common = string.Empty;
            }
            try
            {
                perUser = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            catch (Exception)
            {
                perUser = string.Empty;
            }
            string[] roots = new string[2];
            roots[0] = common.Length == 0 ? string.Empty
                : PathUtil.Normalize(Path.Combine(common, @"Microsoft\Windows\Start Menu\Programs"));
            roots[1] = perUser.Length == 0 ? string.Empty
                : PathUtil.Normalize(Path.Combine(perUser, @"Microsoft\Windows\Start Menu\Programs"));
            return roots;
        }

        #endregion

        #region 安全校验

        /// <summary>删除目录前的红线校验</summary>
        private static bool CanDeleteDirectory(string path, out string reason)
        {
            reason = null;
            string normalized;
            try
            {
                normalized = PathUtil.Normalize(path);
            }
            catch (Exception)
            {
                reason = "路径无效。";
                return false;
            }
            if (string.IsNullOrEmpty(normalized))
            {
                reason = "路径为空，拒绝删除。";
                return false;
            }
            if (normalized.Length <= 3)
            {
                reason = "这是盘符根目录，拒绝删除。";
                return false;
            }
            if (PathUtil.IsSystemCriticalPath(normalized))
            {
                reason = "这是系统关键目录，拒绝删除。";
                return false;
            }

            string windowsDir = string.Empty;
            try
            {
                windowsDir = PathUtil.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            }
            catch (Exception)
            {
                windowsDir = string.Empty;
            }
            if (windowsDir.Length > 0
                && (PathUtil.SamePath(normalized, windowsDir)
                    || PathUtil.IsSubPathOf(normalized, windowsDir)))
            {
                reason = "位于 Windows 目录内，拒绝删除。";
                return false;
            }

            try
            {
                string driveRoot = PathUtil.GetDriveRoot(windowsDir);
                if (!string.IsNullOrEmpty(driveRoot))
                {
                    string usersRoot = PathUtil.Normalize(Path.Combine(driveRoot, "Users"));
                    if (PathUtil.SamePath(normalized, usersRoot))
                    {
                        reason = "这是用户目录的上级目录，拒绝删除。";
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                // 忽略：继续后面的检查
            }

            if (IsSelfDirectory(normalized))
            {
                reason = "这是本程序自身所在的目录，拒绝删除。";
                return false;
            }
            return true;
        }

        /// <summary>路径是否是本程序所在目录（含其上级与内部）</summary>
        private static bool IsSelfDirectory(string path)
        {
            string normalized;
            try
            {
                normalized = PathUtil.Normalize(path);
            }
            catch (Exception)
            {
                return false;
            }
            if (normalized.Length == 0) return false;

            List<string> selves = new List<string>();
            try
            {
                selves.Add(PathUtil.Normalize(AppDomain.CurrentDomain.BaseDirectory));
            }
            catch (Exception)
            {
                // 忽略
            }
            try
            {
                string assemblyPath = typeof(UninstallEngine).Assembly.Location;
                if (!string.IsNullOrEmpty(assemblyPath))
                {
                    selves.Add(PathUtil.Normalize(Path.GetDirectoryName(assemblyPath)));
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            try
            {
                selves.Add(PathUtil.Normalize(Environment.CurrentDirectory));
            }
            catch (Exception)
            {
                // 忽略
            }

            for (int i = 0; i < selves.Count; i++)
            {
                string self = selves[i];
                if (string.IsNullOrEmpty(self)) continue;
                if (PathUtil.SamePath(normalized, self)) return true;
                if (PathUtil.IsSubPathOf(self, normalized)) return true;
                if (PathUtil.IsSubPathOf(normalized, self)) return true;
            }
            return false;
        }

        /// <summary>用户数据目录删除前的校验（必须位于用户配置/文档目录下，且不是这些根目录）</summary>
        private static bool CanDeleteDataFolder(string folder, out string reason)
        {
            reason = null;
            string normalized;
            try
            {
                normalized = PathUtil.Normalize(folder);
            }
            catch (Exception)
            {
                reason = "路径无效。";
                return false;
            }
            if (string.IsNullOrEmpty(normalized))
            {
                reason = "路径为空，拒绝删除。";
                return false;
            }

            string appData = ReadSpecialFolder(Environment.SpecialFolder.ApplicationData);
            string localAppData = ReadSpecialFolder(Environment.SpecialFolder.LocalApplicationData);
            string documents = ReadSpecialFolder(Environment.SpecialFolder.MyDocuments);

            bool underUserData = false;
            if (appData.Length > 0 && (PathUtil.SamePath(normalized, appData)
                || PathUtil.IsSubPathOf(normalized, appData))) underUserData = true;
            if (!underUserData && localAppData.Length > 0 && (PathUtil.SamePath(normalized, localAppData)
                || PathUtil.IsSubPathOf(normalized, localAppData))) underUserData = true;
            if (!underUserData && documents.Length > 0 && (PathUtil.SamePath(normalized, documents)
                || PathUtil.IsSubPathOf(normalized, documents))) underUserData = true;

            if (!underUserData)
            {
                reason = "不在用户数据目录内，拒绝删除。";
                return false;
            }
            if (PathUtil.SamePath(normalized, appData)
                || PathUtil.SamePath(normalized, localAppData)
                || PathUtil.SamePath(normalized, documents))
            {
                reason = "不能删除用户数据根目录，拒绝删除。";
                return false;
            }
            return true;
        }

        private static string ReadSpecialFolder(Environment.SpecialFolder folder)
        {
            try
            {
                return PathUtil.Normalize(Environment.GetFolderPath(folder));
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>注册表路径是否位于 Uninstall 之下（且只有一级键名）</summary>
        private static bool IsUninstallKeyPath(string hive, string keyPath)
        {
            if (string.IsNullOrEmpty(hive) || string.IsNullOrEmpty(keyPath)) return false;
            if (!string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase)) return false;

            string path = keyPath.Trim('\\').Replace('/', '\\');
            const string marker = @"Microsoft\Windows\CurrentVersion\Uninstall\";
            int index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            string rest = path.Substring(index + marker.Length);
            return rest.Length > 0 && rest.IndexOf('\\') < 0;
        }

        #endregion

        #region 执行：收尾与结果

        /// <summary>预检失败：直接写失败日志并返回</summary>
        private static UninstallResult FailEarly(UninstallContext context, UninstallProgressHandler report,
            string message)
        {
            UninstallResult result = context.Result;
            result.Success = false;
            result.PartialSuccess = false;
            result.FinalStage = UninstallStage.Failed;
            result.ErrorMessage = message;
            Log.Error("卸载失败[预检]：" + message);
            Report(report, UninstallStage.Failed, "卸载失败", message, 100);
            result.FailureLogPath = WriteFailureLog(result, context, "无（预检未通过，未执行任何修改）");
            return result;
        }

        /// <summary>收尾：判定最终状态、写卸载记录、汇报进度</summary>
        private static UninstallResult Finish(UninstallContext context, UninstallProgressHandler report)
        {
            UninstallResult result = context.Result;
            UninstallTarget target = context.Target;

            if (!context.Canceled && IsCancelled(context)) context.Canceled = true;

            bool manual = target != null && target.Method == UninstallMethod.ManualOnly;
            bool dirGone = string.IsNullOrEmpty(context.InstallPath)
                || !Directory.Exists(context.InstallPath);
            bool uninstallerOk = context.UninstallerRan
                && (context.ExitCode == 0 || context.ExitCode == 3010);

            bool success;
            if (context.Canceled) success = false;
            else if (manual) success = dirGone;
            else success = uninstallerOk;

            result.Success = success;
            result.PartialSuccess = success
                && (result.Leftovers.Count > 0 || result.TimedOut);
            result.InstallDirRemoved = dirGone;
            if (target != null) result.RegistryCleaned = !AnyRegistryEntryExists(target);

            if (context.Canceled) result.FinalStage = UninstallStage.Canceled;
            else if (!success) result.FinalStage = UninstallStage.Failed;
            else if (result.PartialSuccess) result.FinalStage = UninstallStage.PartialSuccess;
            else result.FinalStage = UninstallStage.Completed;

            if (!success && string.IsNullOrEmpty(result.ErrorMessage))
            {
                if (context.TimedOut)
                {
                    result.ErrorMessage = "卸载程序超时未结束，残留清理已跳过；请稍后在「设置 -> 应用」里确认卸载结果。";
                }
                else if (context.Canceled)
                {
                    result.ErrorMessage = "用户取消了卸载。";
                }
                else if (!context.UninstallerRan && !manual)
                {
                    result.ErrorMessage = "卸载程序没有运行（命令无法执行）。";
                }
                else
                {
                    result.ErrorMessage = "卸载程序返回退出码 " + context.ExitCode + "，卸载可能未成功。";
                }
                if (result.Leftovers.Count > 0)
                {
                    result.ErrorMessage += " 残留：" + JoinText(result.Leftovers, "；");
                }
            }

            if (context.FreedBytes < 0) context.FreedBytes = 0;
            result.FreedBytes = context.FreedBytes;
            result.Summary = BuildSummary(context);

            WriteUninstallRecord(context);

            if (result.FinalStage == UninstallStage.Failed && string.IsNullOrEmpty(result.FailureLogPath))
            {
                result.FailureLogPath = WriteFailureLog(result, context, "未回滚（已删除的内容保留）");
            }

            Log.Info("卸载结束：[" + context.DisplayName + "] 最终状态=" + result.FinalStage.ToString()
                + "，成功=" + result.Success + "，部分成功=" + result.PartialSuccess
                + "，残留 " + result.Leftovers.Count + " 条，警告 " + result.Warnings.Count + " 条。");

            string message;
            if (result.FinalStage == UninstallStage.Completed) message = "卸载完成。";
            else if (result.FinalStage == UninstallStage.PartialSuccess) message = "卸载完成，但有残留未清理干净。";
            else if (result.FinalStage == UninstallStage.Canceled) message = "卸载已取消（已做的清理不回滚）。";
            else message = result.ErrorMessage;
            Report(report, result.FinalStage, StageText(result.FinalStage), message, 100);
            return result;
        }

        /// <summary>组装给用户看的结果摘要</summary>
        private static string BuildSummary(UninstallContext context)
        {
            UninstallResult result = context.Result;
            UninstallTarget target = context.Target;
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("卸载结果：" + ResultText(result));
            sb.AppendLine("软件：" + SafeText(context.DisplayName));
            sb.AppendLine("安装路径：" + SafeText(context.InstallPath));
            if (target != null && target.InstallPathIsJunction)
            {
                sb.AppendLine("原路径（目录联接）：" + SafeText(target.LinkPath));
            }
            if (target != null)
            {
                sb.AppendLine("卸载方式：" + MethodText(target.Method));
            }
            if (!string.IsNullOrEmpty(context.Command))
            {
                sb.AppendLine("卸载命令：" + SafeText(context.Command));
            }
            sb.AppendLine("卸载程序是否运行：" + (context.UninstallerRan ? "是" : "否"));
            if (context.UninstallerRan)
            {
                sb.AppendLine("卸载程序退出码：" + context.ExitCode);
            }
            if (context.TimedOut) sb.AppendLine("卸载程序超时：" + "是（残留清理已跳过）");
            sb.AppendLine("安装目录已删除：" + (result.InstallDirRemoved ? "是" : "否"));
            sb.AppendLine("注册表卸载项已清理：" + (result.RegistryCleaned ? "是" : "否"));
            sb.AppendLine("清理项：" + result.RemovedItems.Count + " 项");
            sb.AppendLine("释放空间：" + TextUtil.FormatBytes(result.FreedBytes));
            if (!string.IsNullOrEmpty(context.BackupFolder))
            {
                sb.AppendLine("注册表备份目录：" + context.BackupFolder);
            }
            if (result.Leftovers.Count > 0)
            {
                sb.AppendLine("--- 未能清理的残留（" + result.Leftovers.Count + " 条）---");
                for (int i = 0; i < result.Leftovers.Count; i++)
                {
                    sb.AppendLine((i + 1).ToString() + ". " + SafeText(result.Leftovers[i]));
                }
            }
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("--- 警告（" + result.Warnings.Count + " 条）---");
                for (int i = 0; i < result.Warnings.Count; i++)
                {
                    sb.AppendLine((i + 1).ToString() + ". " + SafeText(result.Warnings[i]));
                }
            }
            if (context.Canceled)
            {
                sb.AppendLine("取消说明：用户取消后未回滚，已删除的内容无法自动恢复；"
                    + "注册表卸载项可用备份目录里的 .reg 文件导入恢复。");
            }
            if (result.RemovedItems.Count > 0)
            {
                sb.AppendLine("--- 已删除的项（" + result.RemovedItems.Count + " 条）---");
                for (int i = 0; i < result.RemovedItems.Count; i++)
                {
                    sb.AppendLine((i + 1).ToString() + ". " + SafeText(result.RemovedItems[i]));
                }
            }
            return sb.ToString();
        }

        private static string ResultText(UninstallResult result)
        {
            if (result == null) return "未知";
            if (result.FinalStage == UninstallStage.Canceled) return "已取消";
            if (result.Success) return result.PartialSuccess ? "部分成功（有残留）" : "成功";
            return "失败";
        }

        /// <summary>写卸载记录到 logs\uninstall\</summary>
        private static void WriteUninstallRecord(UninstallContext context)
        {
            try
            {
                string fileName = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_"
                    + BuildSafeLeaf(context) + ".txt";
                string content = context.Result.Summary + Environment.NewLine;
                string path = TryWriteTextFile(AppPaths.LogDir, "uninstall", fileName, content);
                if (!string.IsNullOrEmpty(path))
                {
                    Log.Info("已写入卸载记录：" + path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("写入卸载记录失败：" + ex.Message);
            }
        }

        /// <summary>先尝试主目录，失败退回用户目录 / 临时目录；返回实际路径</summary>
        private static string TryWriteTextFile(string baseDir, string subDir, string fileName, string content)
        {
            List<string> directories = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(baseDir)) directories.Add(Path.Combine(baseDir, subDir));
            }
            catch (Exception)
            {
                // 忽略
            }
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    directories.Add(Path.Combine(Path.Combine(localAppData, "YijianNuodong"), subDir));
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            try
            {
                string temp = Path.GetTempPath();
                if (!string.IsNullOrEmpty(temp))
                {
                    directories.Add(Path.Combine(Path.Combine(temp, "YijianNuodong"), subDir));
                }
            }
            catch (Exception)
            {
                // 忽略
            }

            for (int i = 0; i < directories.Count; i++)
            {
                string directory = directories[i];
                try
                {
                    if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, fileName);
                    int suffix = 2;
                    while (File.Exists(path) && suffix < 100)
                    {
                        path = Path.Combine(directory,
                            Path.GetFileNameWithoutExtension(fileName) + "_" + suffix.ToString() + ".txt");
                        suffix++;
                    }
                    File.WriteAllText(path, content, new UTF8Encoding(true));
                    return path;
                }
                catch (Exception ex)
                {
                    Log.Warn("写入文件失败（" + directory + "）：" + ex.Message);
                }
            }
            return null;
        }

        #endregion

        #region 失败诊断

        /// <summary>生成卸载失败诊断文件；任何情况下都不抛出</summary>
        private static string WriteFailureLog(UninstallResult result, UninstallContext context, string extra)
        {
            try
            {
                string content = BuildFailureLogText(result, context, extra);
                string fileName = "uninstall_fail_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_"
                    + BuildSafeLeaf(context) + ".txt";
                string path = TryWriteTextFile(AppPaths.LogDir, "failures", fileName, content);
                if (!string.IsNullOrEmpty(path))
                {
                    Log.Warn("已生成卸载失败诊断日志：" + path);
                }
                return path;
            }
            catch (Exception ex)
            {
                Log.Warn("生成卸载失败诊断日志时出错：" + ex.Message);
                return null;
            }
        }

        /// <summary>组装诊断文件正文</summary>
        private static string BuildFailureLogText(UninstallResult result, UninstallContext context, string extra)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== 一键挪动 卸载失败诊断 ===");
            sb.AppendLine("产品版本: " + GetProductVersion());
            sb.AppendLine("发生时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("管理员权限: " + (ElevationUtil.IsElevated ? "是" : "否"));
            sb.AppendLine("操作系统: " + GetOsVersion());
            sb.AppendLine("软件名称: " + SafeText(context == null ? string.Empty : context.DisplayName));
            sb.AppendLine("安装路径: " + SafeText(context == null ? string.Empty : context.InstallPath));
            if (context != null && context.Target != null)
            {
                sb.AppendLine("卸载方式: " + MethodText(context.Target.Method));
                sb.AppendLine("产品代码: " + SafeText(context.Target.ProductCode));
            }
            sb.AppendLine("卸载命令: " + SafeText(context == null ? string.Empty : context.Command));
            sb.AppendLine("最终阶段: " + StageText(result == null ? UninstallStage.Idle : result.FinalStage)
                + "（" + (result == null ? "Idle" : result.FinalStage.ToString()) + "）");
            sb.AppendLine("错误信息: " + SafeText(result == null ? string.Empty : result.ErrorMessage));
            sb.AppendLine("处理说明: " + (string.IsNullOrEmpty(extra) ? "无" : SafeText(extra)));

            Exception exception = context == null ? null : context.LastException;
            sb.AppendLine("--- 异常信息 ---");
            if (exception == null)
            {
                sb.AppendLine("（无异常对象：属于预检未通过或卸载结果不成功）");
            }
            else
            {
                sb.AppendLine("异常类型: " + exception.GetType().FullName);
                sb.AppendLine("异常消息: " + SafeText(exception.Message));
                sb.AppendLine("异常堆栈:");
                sb.AppendLine(exception.StackTrace ?? "（无堆栈）");
            }

            if (context != null && context.Target != null && context.Target.RegistryEntries != null)
            {
                sb.AppendLine("--- 命中的注册表卸载项（" + context.Target.RegistryEntries.Count + " 条）---");
                for (int i = 0; i < context.Target.RegistryEntries.Count; i++)
                {
                    UninstallRegistryEntry entry = context.Target.RegistryEntries[i];
                    if (entry == null) continue;
                    sb.AppendLine((i + 1).ToString() + ". " + entry.Hive + "\\" + entry.KeyPath
                        + (string.IsNullOrEmpty(entry.DisplayName) ? string.Empty : "（" + entry.DisplayName + "）")
                        + (string.IsNullOrEmpty(entry.BackupFilePath) ? string.Empty : " 备份：" + entry.BackupFilePath));
                }
            }

            sb.AppendLine("--- 已删除的项（" + (result == null ? 0 : result.RemovedItems.Count) + " 条）---");
            if (result != null && result.RemovedItems.Count > 0)
            {
                for (int i = 0; i < result.RemovedItems.Count; i++)
                {
                    sb.AppendLine((i + 1).ToString() + ". " + SafeText(result.RemovedItems[i]));
                }
            }
            else
            {
                sb.AppendLine("（无）");
            }

            sb.AppendLine("--- 残留（" + (result == null ? 0 : result.Leftovers.Count) + " 条）---");
            if (result != null && result.Leftovers.Count > 0)
            {
                for (int i = 0; i < result.Leftovers.Count; i++)
                {
                    sb.AppendLine((i + 1).ToString() + ". " + SafeText(result.Leftovers[i]));
                }
            }
            else
            {
                sb.AppendLine("（无）");
            }

            sb.AppendLine("--- 最近 " + MaxRecentLogLines + " 行运行日志 ---");
            try
            {
                string recent = Log.GetRecentText(MaxRecentLogLines);
                if (string.IsNullOrEmpty(recent))
                {
                    sb.AppendLine("（无运行日志）");
                }
                else
                {
                    sb.Append(recent);
                    if (!recent.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                    {
                        sb.AppendLine();
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("（读取运行日志失败：" + ex.Message + "）");
            }

            sb.AppendLine("--- 建议 ---");
            sb.AppendLine("1. 以管理员身份重新运行本程序，再试一次卸载。");
            sb.AppendLine("2. 先手动退出该软件（含托盘图标），必要时重启电脑后再卸载。");
            sb.AppendLine("3. 临时关闭杀毒/安全软件，避免它拦截文件或注册表操作。");
            if (context != null && !string.IsNullOrEmpty(context.BackupFolder))
            {
                sb.AppendLine("4. 若卸载项已被删除但软件还在，可双击备份目录里的 .reg 文件恢复卸载信息："
                    + context.BackupFolder);
            }
            else
            {
                sb.AppendLine("4. 若卸载项被删除但软件还在，可手工在控制面板/设置里修复安装后重试。");
            }
            sb.AppendLine("5. 请把本文件附在反馈里，便于我们定位并修复问题。");
            sb.AppendLine("=== 诊断文件结束 ===");
            return sb.ToString();
        }

        #endregion

        #region 系统还原点

        private const int BeginSystemChange = 100;
        private const int EndSystemChange = 101;
        private const int ModifySettingsRestorePoint = 12;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RESTOREPOINTINFO
        {
            public int dwEventType;
            public int dwRestorePtType;
            public long llSequenceNumber;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STATEMGRSTATUS
        {
            public uint nStatus;
            public long llSequenceNumber;
        }

        [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SRSetRestorePointW(ref RESTOREPOINTINFO pRestorePtSpec,
            out STATEMGRSTATUS pSMgrStatus);

        /// <summary>尽力创建系统还原点（需要管理员权限与已启用系统保护），失败只给出原因</summary>
        private static bool TryCreateRestorePoint(string displayName, out string error)
        {
            error = null;
            try
            {
                string description = TextUtil.Clean(displayName);
                if (string.IsNullOrEmpty(description)) description = "应用卸载";
                if (description.Length > 200) description = description.Substring(0, 200);

                RESTOREPOINTINFO info = new RESTOREPOINTINFO();
                info.dwEventType = BeginSystemChange;
                info.dwRestorePtType = ModifySettingsRestorePoint;
                info.llSequenceNumber = 0;
                info.szDescription = "一键挪动卸载前自动创建（" + description + "）";

                STATEMGRSTATUS status;
                bool ok = SRSetRestorePointW(ref info, out status);
                int win32Error = Marshal.GetLastWin32Error();
                if (!ok || status.nStatus != 0)
                {
                    int code = ok ? (int)status.nStatus : win32Error;
                    error = "系统拒绝创建还原点（错误码 " + code
                        + "），通常是因为未启用系统保护或权限不足。";
                    return false;
                }

                RESTOREPOINTINFO endInfo = new RESTOREPOINTINFO();
                endInfo.dwEventType = EndSystemChange;
                endInfo.dwRestorePtType = ModifySettingsRestorePoint;
                endInfo.llSequenceNumber = status.llSequenceNumber;
                endInfo.szDescription = info.szDescription;
                STATEMGRSTATUS endStatus;
                try
                {
                    SRSetRestorePointW(ref endInfo, out endStatus);
                }
                catch (Exception)
                {
                    // 结束通知失败不影响还原点本身
                }
                return true;
            }
            catch (DllNotFoundException)
            {
                error = "系统不支持创建还原点（找不到 srclient.dll）。";
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                error = "系统不支持创建还原点（缺少 SRSetRestorePoint 接口）。";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        #endregion

        #region 通用辅助

        /// <summary>检查注册表卸载项是否仍然存在</summary>
        private static bool RegistryEntryExists(UninstallRegistryEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Hive) || string.IsNullOrEmpty(entry.KeyPath))
            {
                return false;
            }
            RegistryKey baseKey = null;
            RegistryKey key = null;
            try
            {
                RegistryHive hive = string.Equals(entry.Hive, "HKCU", StringComparison.OrdinalIgnoreCase)
                    ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                if (baseKey == null) return false;
                key = baseKey.OpenSubKey(entry.KeyPath, false);
                return key != null;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (key != null)
                {
                    try { key.Close(); }
                    catch (Exception) { }
                }
                if (baseKey != null)
                {
                    try { baseKey.Close(); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>是否还有任一注册表卸载项存在</summary>
        private static bool AnyRegistryEntryExists(UninstallTarget target)
        {
            if (target == null || target.RegistryEntries == null) return false;
            for (int i = 0; i < target.RegistryEntries.Count; i++)
            {
                if (RegistryEntryExists(target.RegistryEntries[i])) return true;
            }
            return false;
        }

        /// <summary>执行一次 reg.exe；返回退出码，-1 表示启动失败/超时</summary>
        private static int RunReg(string[] arguments, int timeoutMs, out string output)
        {
            output = string.Empty;
            try
            {
                string regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
                if (!File.Exists(regExe)) regExe = "reg.exe";

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = regExe;
                startInfo.Arguments = QuoteArguments(arguments);
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                using (Process process = new Process())
                {
                    process.StartInfo = startInfo;
                    process.Start();
                    string standardOutput = process.StandardOutput.ReadToEnd();
                    string standardError = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); }
                        catch (Exception) { }
                        output = "reg.exe 执行超时。";
                        return -1;
                    }
                    output = (standardOutput + " " + standardError).Trim();
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                Log.Warn("执行 reg.exe 失败：" + ex.Message);
                return -1;
            }
        }

        /// <summary>给每个参数加引号（reg.exe 对含空格的路径需要）</summary>
        private static string QuoteArguments(string[] arguments)
        {
            if (arguments == null || arguments.Length == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append('"');
                sb.Append(arguments[i] == null ? string.Empty : arguments[i].Replace("\"", ""));
                sb.Append('"');
            }
            return sb.ToString();
        }

        /// <summary>获取路径所在磁盘可用空间；失败返回 -1</summary>
        private static long GetFreeSpace(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return -1;
                string root = PathUtil.GetDriveRoot(path);
                if (string.IsNullOrEmpty(root)) return -1;
                DriveInfo drive = new DriveInfo(root);
                if (!drive.IsReady) return -1;
                return drive.AvailableFreeSpace;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>诊断/记录文件名里的安全叶名</summary>
        private static string BuildSafeLeaf(UninstallContext context)
        {
            string source = string.Empty;
            if (context != null)
            {
                source = context.DisplayName;
                if (string.IsNullOrEmpty(source)) source = context.InstallPath;
            }
            string leaf = PathUtil.SanitizeFolderName(source);
            if (string.IsNullOrEmpty(leaf))
            {
                try
                {
                    leaf = PathUtil.GetLeafName(context == null ? string.Empty : context.InstallPath,
                        context == null ? string.Empty : context.DisplayName);
                }
                catch (Exception)
                {
                    leaf = string.Empty;
                }
            }
            if (string.IsNullOrEmpty(leaf)) leaf = "uninstall";
            return leaf;
        }

        private static string GetProductVersion()
        {
            try
            {
                Version version = typeof(UninstallEngine).Assembly.GetName().Version;
                if (version != null)
                {
                    return version.Major.ToString() + "." + version.Minor.ToString() + "."
                        + version.Build.ToString();
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            return "未知";
        }

        private static string GetOsVersion()
        {
            try
            {
                return Environment.OSVersion.VersionString;
            }
            catch (Exception)
            {
                return "未知";
            }
        }

        private static string SafeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string value = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (value.Length > 500) value = value.Substring(0, 500) + "…";
            return value;
        }

        private static string MethodText(UninstallMethod method)
        {
            switch (method)
            {
                case UninstallMethod.MsiProduct: return "MSI 产品（msiexec /X）";
                case UninstallMethod.RegistryCommand: return "注册表卸载程序";
                case UninstallMethod.ManualOnly: return "仅清理文件（无官方卸载程序）";
                default: return "未知";
            }
        }

        private static string StageText(UninstallStage stage)
        {
            switch (stage)
            {
                case UninstallStage.Idle: return "空闲";
                case UninstallStage.Precheck: return "正在预检";
                case UninstallStage.BackingUp: return "正在备份";
                case UninstallStage.ClosingProgram: return "正在关闭程序";
                case UninstallStage.RunningUninstaller: return "正在运行官方卸载程序";
                case UninstallStage.WaitingForUninstaller: return "正在等待卸载完成";
                case UninstallStage.Verifying: return "正在校验";
                case UninstallStage.CleaningLeftovers: return "正在清理残留";
                case UninstallStage.Finalizing: return "正在收尾";
                case UninstallStage.Completed: return "卸载完成";
                case UninstallStage.Failed: return "卸载失败";
                case UninstallStage.Canceled: return "已取消";
                case UninstallStage.PartialSuccess: return "卸载完成（有残留）";
                default: return stage.ToString();
            }
        }

        /// <summary>进度回调（带 null 保护，回调异常不影响主流程）</summary>
        private static void Report(UninstallProgressHandler progress, UninstallStage stage,
            string stageText, string message, int percent)
        {
            Report(progress, stage, stageText, message, percent, null, false);
        }

        /// <summary>进度回调（可指定当前项与「等待用户操作」）</summary>
        private static void Report(UninstallProgressHandler progress, UninstallStage stage,
            string stageText, string message, int percent, string currentItem, bool waitingForUser)
        {
            if (progress == null) return;
            try
            {
                UninstallProgress item = new UninstallProgress();
                item.Stage = stage;
                item.StageText = stageText;
                item.Message = message;
                item.CurrentItem = currentItem;
                item.Percent = percent;
                item.WaitingForUser = waitingForUser;
                progress(item);
            }
            catch (Exception ex)
            {
                Log.Warn("卸载进度回调失败：" + ex.Message);
            }
        }

        private static bool IsCancelled(UninstallContext context)
        {
            if (context == null || context.IsCancelled == null) return false;
            try
            {
                return context.IsCancelled();
            }
            catch (Exception ex)
            {
                Log.Warn("取消检测回调出错（按未取消处理）：" + ex.Message);
                return false;
            }
        }

        private static string DescribeProcesses(List<LockingProcess> processes)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < processes.Count; i++)
            {
                LockingProcess process = processes[i];
                if (process == null) continue;
                if (sb.Length > 0) sb.Append("、");
                sb.Append(string.IsNullOrEmpty(process.ProcessName) ? "未知进程" : process.ProcessName);
                sb.Append("（PID ").Append(process.ProcessId).Append("）");
                if (sb.Length > 300)
                {
                    sb.Append(" 等");
                    break;
                }
            }
            return sb.ToString();
        }

        private static string DescribeServices(List<LockingService> services)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < services.Count; i++)
            {
                LockingService service = services[i];
                if (service == null) continue;
                if (sb.Length > 0) sb.Append("、");
                sb.Append(string.IsNullOrEmpty(service.DisplayName)
                    ? service.ServiceName : service.DisplayName);
                if (!string.IsNullOrEmpty(service.State))
                {
                    sb.Append("（").Append(service.State).Append("）");
                }
                if (sb.Length > 300)
                {
                    sb.Append(" 等");
                    break;
                }
            }
            return sb.ToString();
        }

        private static string JoinText(List<string> items, string separator)
        {
            if (items == null || items.Count == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (string.IsNullOrEmpty(items[i])) continue;
                if (sb.Length > 0) sb.Append(separator);
                sb.Append(items[i]);
            }
            return sb.ToString();
        }

        private static bool ReturnFalse()
        {
            return false;
        }

        private static void IgnoreProgressCallback(UninstallProgress progress)
        {
        }

        #endregion

        #region 内部数据结构

        /// <summary>一次卸载的上下文（只存在于 Execute 内部）</summary>
        private sealed class UninstallContext
        {
            public UninstallRequest Request;
            public UninstallTarget Target;
            public UninstallResult Result;
            public Func<bool> IsCancelled;
            public string DisplayName;
            public string InstallPath;
            public string Command;
            public string BackupFolder;
            public LockInfo Locks;
            public bool Canceled;
            public bool TimedOut;
            public bool UninstallerRan;
            public int ExitCode = -1;
            public bool InstallDirDeleted;
            public long FreedBytes;
            public UninstallStage LastStage = UninstallStage.Idle;
            public Exception LastException;
        }

        #endregion
    }
}
