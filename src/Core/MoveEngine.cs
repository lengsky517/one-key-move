// ============================================================================
//  一键挪动 (OneKeyMove) - 搬家引擎
//  流程：预检 -> 处理占用 -> 准备 -> 复制 -> 校验 -> 原目录改名备份 -> 建目录联接
//        -> 删备份 -> 更新快捷方式/注册表/服务 -> 写日记
//  安全原则：
//    * 任何一步失败都尝试把用户环境恢复原状（回滚），绝不让软件“消失”；
//    * 所有异常都在内部消化成中文错误信息，绝不向界面抛出；
//    * 复制阶段支持细粒度进度与取消（每 512KB 或 250ms 回报一次）。
//  目标框架：.NET Framework 4.8 / C# 5
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;

namespace YijianNuodong.Core
{
    /// <summary>搬家引擎：预检、执行、回滚、撤销</summary>
    public static class MoveEngine
    {
        /// <summary>目标盘预留空间（200MB）</summary>
        private const long SpaceReserveBytes = 200L * 1024 * 1024;

        /// <summary>超过该大小的文件改用流式复制，以便细粒度回报进度</summary>
        private const long LargeFileThreshold = 4L * 1024 * 1024;

        private const int CopyBufferSize = 1024 * 1024;
        private const long ReportIntervalBytes = 512L * 1024;
        private const int ReportIntervalMs = 250;
        private const int CopyRetryCount = 3;
        private const int CopyRetryDelayMs = 300;
        private const long SpaceCheckIntervalBytes = 64L * 1024 * 1024;

        private const int CopyPercentStart = 10;
        private const int CopyPercentSpan = 78;

        /// <summary>受保护的系统/应用目录（叶目录名，命中即拒绝搬移）</summary>
        private static readonly string[] ProtectedLeafNames = new string[]
        {
            "windows", "windowsapps", "common files", "microsoft", "onedrive",
            "$recycle.bin", "system volume information", "program files",
            "program files (x86)", "programdata", "appdata", "recovery",
            "windows.old", "boot", "system32", "syswow64", "winsxs"
        };

        /// <summary>受保护的容器目录（路径中任意一层命中即拒绝，避免搬动其内部内容）</summary>
        private static readonly string[] ProtectedContainerNames = new string[]
        {
            "windowsapps", "onedrive", "$recycle.bin", "system volume information",
            "common files", "windows.old"
        };

        private static readonly Func<bool> AlwaysFalseDelegate = new Func<bool>(ReturnFalse);
        private static readonly MoveProgressHandler IgnoreProgressDelegate = new MoveProgressHandler(IgnoreProgressCallback);

        #region 预检

        /// <summary>搬家前预检：路径合法性、空间、占用、保护目录、目标冲突等</summary>
        public static PrecheckResult Precheck(MoveRequest request)
        {
            PrecheckResult result = new PrecheckResult();
            try
            {
                if (request == null || request.Program == null)
                {
                    result.Errors.Add("搬家请求无效：缺少要搬移的程序信息。");
                    result.CanProceed = false;
                    return result;
                }

                InstalledProgram program = request.Program;
                string sourcePath = PathUtil.Normalize(program.InstallPath);
                string targetPath = PathUtil.Normalize(request.TargetDirectory);
                result.TargetDirectory = targetPath;

                // ---------- 源目录 ----------
                if (sourcePath.Length == 0)
                {
                    result.Errors.Add("程序安装目录为空，无法搬移。");
                    result.CanProceed = false;
                    return result;
                }
                if (!Directory.Exists(sourcePath))
                {
                    result.Errors.Add("安装目录不存在：" + sourcePath);
                }
                else if (PathUtil.IsJunctionOrLink(sourcePath))
                {
                    result.Errors.Add("安装目录本身是目录联接或符号链接，无需再次搬移：" + sourcePath);
                }

                // ---------- 目标目录 ----------
                if (targetPath.Length == 0)
                {
                    result.Errors.Add("未指定目标目录。");
                }
                else
                {
                    if (PathUtil.SamePath(sourcePath, targetPath))
                    {
                        result.Errors.Add("目标目录不能与安装目录相同。");
                    }
                    else if (PathUtil.IsSubPathOf(targetPath, sourcePath))
                    {
                        result.Errors.Add("目标目录不能位于安装目录内部：" + targetPath);
                    }
                    else if (PathUtil.IsSubPathOf(sourcePath, targetPath))
                    {
                        result.Errors.Add("安装目录不能位于目标目录内部：" + targetPath);
                    }
                    if (PathUtil.IsSystemCriticalPath(targetPath))
                    {
                        result.Errors.Add("目标目录不能是 Windows 系统目录或盘符根目录：" + targetPath);
                    }
                }

                // ---------- 系统关键路径与保护名单 ----------
                if (PathUtil.IsSystemCriticalPath(sourcePath))
                {
                    result.IsProtectedSystemProgram = true;
                    result.ProtectedReason = "这是 Windows 系统目录、用户配置目录或盘符根目录，搬移会导致系统无法启动。";
                    result.Errors.Add("不能搬移系统关键目录：" + sourcePath);
                }
                string protectedName;
                if (IsProtectedFolderName(sourcePath, out protectedName))
                {
                    result.IsProtectedSystemProgram = true;
                    if (string.IsNullOrEmpty(result.ProtectedReason))
                    {
                        result.ProtectedReason = "该目录属于受保护的系统或应用目录（" + protectedName + "）。";
                    }
                    result.Errors.Add("该目录属于受保护的系统或应用目录（" + protectedName + "），为安全起见禁止搬移：" + sourcePath);
                }

                // ---------- 目标父目录 ----------
                string targetParent = string.Empty;
                if (targetPath.Length > 0)
                {
                    try
                    {
                        targetParent = Path.GetDirectoryName(targetPath);
                    }
                    catch (Exception)
                    {
                        targetParent = string.Empty;
                    }
                    if (string.IsNullOrEmpty(targetParent))
                    {
                        result.Errors.Add("目标目录无效：" + targetPath);
                    }
                    else if (!Directory.Exists(targetParent))
                    {
                        result.Errors.Add("目标目录的上级目录不存在，请先创建：" + targetParent);
                    }
                    else if (!FileUtil.HasWriteAccess(targetParent))
                    {
                        result.Errors.Add("没有目标目录的写入权限：" + targetParent + "（请更换位置，或以管理员身份运行）");
                    }
                    else if (Directory.Exists(targetPath) && !FileUtil.IsDirectoryEmpty(targetPath))
                    {
                        result.Errors.Add("目标目录已存在且不是空的，请换一个位置：" + targetPath);
                    }
                }

                // ---------- 源大小与目标可用空间 ----------
                if (Directory.Exists(sourcePath))
                {
                    long size = program.SizeBytes;
                    if (size <= 0)
                    {
                        size = FileUtil.GetDirectorySize(sourcePath, null);
                    }
                    result.SourceBytes = size;
                }
                if (Directory.Exists(sourcePath) && !string.IsNullOrEmpty(targetParent) && Directory.Exists(targetParent))
                {
                    long free = GetFreeSpace(targetParent);
                    result.TargetFreeBytes = free;
                    if (!result.HasEnoughSpace)
                    {
                        result.Errors.Add("目标盘空间不足：需要 " + TextUtil.FormatBytes(result.SourceBytes + SpaceReserveBytes)
                            + "（含 " + TextUtil.FormatBytes(SpaceReserveBytes) + " 余量），可用 "
                            + TextUtil.FormatBytes(free) + "。");
                    }
                    result.TargetOnSameVolume = PathUtil.SamePath(
                        PathUtil.GetDriveRoot(sourcePath), PathUtil.GetDriveRoot(targetPath));
                }

                // ---------- 占用检测 ----------
                LockInfo locks = null;
                try
                {
                    if (Directory.Exists(sourcePath))
                    {
                        locks = LockInspector.Inspect(sourcePath, null);
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("占用检测失败（不影响继续操作，但复制可能因占用而失败）：" + ex.Message);
                    Log.Warn("占用检测失败：" + ex.Message);
                }
                if (locks == null)
                {
                    locks = new LockInfo();
                    locks.Path = sourcePath;
                }
                result.Locks = locks;

                if (locks.Processes.Count > 0)
                {
                    result.Warnings.Add("检测到 " + locks.Processes.Count + " 个正在使用该程序的进程："
                        + DescribeProcesses(locks.Processes)
                        + "。可勾选「自动结束占用进程」，或选择「重启后完成」。");
                }
                if (locks.Services.Count > 0)
                {
                    if (request.StopRelatedServices)
                    {
                        result.Warnings.Add("检测到 " + locks.Services.Count + " 个相关服务（"
                            + DescribeServices(locks.Services) + "），搬家时会临时停止它们，完成后再启动。");
                    }
                    else
                    {
                        result.Warnings.Add("检测到 " + locks.Services.Count + " 个相关服务（"
                            + DescribeServices(locks.Services) + "），当前未启用「停止相关服务」，复制可能失败。");
                    }
                }
                if (!request.CreateJunction)
                {
                    result.Warnings.Add("未启用目录联接：搬家后原路径将不可用，部分软件可能无法启动。");
                }
                if (!ElevationUtil.IsElevated)
                {
                    result.Warnings.Add("当前不是管理员权限：停止服务、写入 HKLM 注册表、创建系统还原点等操作会失败。");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add("预检时发生错误：" + ex.Message);
                Log.Error("预检异常", ex);
            }

            result.CanProceed = result.Errors.Count == 0;
            Log.Info("预检完成：可继续=" + result.CanProceed + "，错误 " + result.Errors.Count + " 条，警告 " + result.Warnings.Count + " 条。");
            return result;
        }

        #endregion

        #region 执行

        /// <summary>执行搬家。progress 可为 null；isCancelled 返回 true 时中止并回滚</summary>
        public static MoveResult Execute(MoveRequest request, MoveProgressHandler progress, Func<bool> isCancelled)
        {
            return ExecuteCore(request, progress, isCancelled);
        }

        /// <summary>执行搬家的内部实现，供 RebootMover 复用</summary>
        internal static MoveResult ExecuteCore(MoveRequest request, MoveProgressHandler progress, Func<bool> isCancelled)
        {
            MoveResult result = new MoveResult();
            if (request == null || request.Program == null)
            {
                result.FinalStage = MoveStage.Failed;
                result.ErrorMessage = "搬家请求无效：缺少要搬移的程序信息。";
                return result;
            }

            if (isCancelled == null)
            {
                isCancelled = AlwaysFalseDelegate;
            }
            if (progress == null)
            {
                progress = IgnoreProgressDelegate;
            }

            MoveContext context = new MoveContext();
            try
            {
                string sourceDirectory = PathUtil.Normalize(request.Program.InstallPath);
                string targetDirectory = PathUtil.Normalize(request.TargetDirectory);
                context.SourceDir = sourceDirectory;
                context.TargetDir = targetDirectory;
                context.CreatedUtc = DateTime.UtcNow;
                context.DisplayName = request.Program == null || request.Program.DisplayName == null
                    ? string.Empty
                    : request.Program.DisplayName;
                result.OldPath = sourceDirectory;
                result.NewPath = targetDirectory;
                Log.Info("开始搬家：[" + request.Program.DisplayName + "] " + sourceDirectory + " -> " + targetDirectory);

                // ---------- 1. 预检 ----------
                Report(progress, MoveStage.Precheck, "正在预检", "检查路径、空间与占用情况…", 1);
                PrecheckResult precheck = Precheck(request);
                context.Locks = precheck.Locks;
                if (precheck.Warnings.Count > 0)
                {
                    context.Warnings.AddRange(precheck.Warnings);
                }
                if (!precheck.CanProceed)
                {
                    result.Success = false;
                    result.FinalStage = MoveStage.Failed;
                    result.ErrorMessage = "预检未通过：" + JoinText(precheck.Errors, "；");
                    result.Warnings = context.Warnings;
                    result.FailureLogPath = WriteFailureLog(result, context, "无（预检未通过，未执行回滚）");
                    Log.Error("搬家失败[预检]: " + result.ErrorMessage);
                    return result;
                }
                if (IsCancelled(isCancelled))
                {
                    return Canceled(result, context, progress, true);
                }

                // ---------- 2. 处理占用 ----------
                Report(progress, MoveStage.ResolvingLocks, "正在处理占用", "检测并处理正在使用该程序的进程与服务…", 3);
                LockInfo locks = precheck.Locks;
                if (locks == null)
                {
                    locks = LockInspector.Inspect(sourceDirectory, isCancelled);
                }
                context.Locks = locks;
                if (locks.Services != null)
                {
                    context.Services.AddRange(locks.Services);
                }
                ResolveLocks(request, locks, context, progress, isCancelled);
                if (IsCancelled(isCancelled))
                {
                    return Canceled(result, context, progress, false);
                }

                // ---------- 3. 准备 ----------
                Report(progress, MoveStage.Preparing, "正在准备", "创建目标目录、扫描文件并准备撤销日记…", 6);
                if (request.CreateRestorePoint)
                {
                    string restoreMessage;
                    if (!TryCreateRestorePoint(request.Program.DisplayName, out restoreMessage))
                    {
                        context.Warnings.Add("创建系统还原点失败（已跳过，不影响搬家）：" + restoreMessage);
                    }
                    else
                    {
                        Log.Info("已创建系统还原点。");
                    }
                }

                string createError;
                if (!FileUtil.EnsureDirectory(targetDirectory, out createError))
                {
                    return Failed(result, context, "创建目标目录失败：" + createError, progress, false);
                }
                context.TargetCreated = true;
                context.JournalPath = MoveJournal.NewJournalPath();

                SourceScan scan = ScanSource(sourceDirectory, context.Warnings);
                if (scan.Files.Count == 0 && scan.Directories.Count == 0 && scan.Links.Count == 0)
                {
                    return Failed(result, context, "安装目录是空的，没有可搬移的文件，已取消操作。", progress, false);
                }
                Report(progress, MoveStage.Preparing, "正在准备",
                    "共 " + scan.Files.Count + " 个文件，合计 " + TextUtil.FormatBytes(scan.TotalBytes) + "，开始复制…",
                    8, 0, scan.TotalBytes, 0, scan.Files.Count, null);

                // ---------- 4. 复制 ----------
                Report(progress, MoveStage.Copying, "正在复制文件", "正在复制到 " + targetDirectory, CopyPercentStart,
                    0, scan.TotalBytes, 0, scan.Files.Count, null);
                string transferError;
                if (!Transfer(scan, sourceDirectory, targetDirectory, progress, isCancelled, out transferError))
                {
                    if (IsCancelled(isCancelled))
                    {
                        return Canceled(result, context, progress, true);
                    }
                    return Failed(result, context, "复制文件失败：" + transferError, progress, true);
                }

                // ---------- 5. 校验 ----------
                Report(progress, MoveStage.Verifying, "正在校验", "比较文件数量与大小…", 90, scan.TotalBytes, scan.TotalBytes,
                    scan.Files.Count, scan.Files.Count, null);
                string verifyError;
                if (!VerifyTransfer(scan, targetDirectory, out verifyError))
                {
                    return Failed(result, context, "复制校验失败：" + verifyError, progress, true);
                }

                // ---------- 6. 原目录改名备份 ----------
                Report(progress, MoveStage.RemovingSource, "正在移走原目录", "把原目录改名备份…", 92, scan.TotalBytes, scan.TotalBytes, scan.Files.Count, scan.Files.Count, null);
                string backupPath = BuildBackupPath(sourceDirectory);
                try
                {
                    Directory.Move(sourceDirectory, backupPath);
                    context.BackupDir = backupPath;
                    context.SourceRenamed = true;
                    Log.Info("原目录已改名备份：" + backupPath);
                }
                catch (Exception ex)
                {
                    string hint = IsLockError(ex)
                        ? "原目录仍被进程占用，无法移走。建议勾选「自动结束占用进程」，或登记为「开机时自动完成」。"
                        : "无法重命名原目录。";
                    return Failed(result, context, hint + "原因：" + TranslateFsError(ex), progress, true);
                }
                if (IsCancelled(isCancelled))
                {
                    return Canceled(result, context, progress, true);
                }

                // ---------- 7. 创建目录联接 ----------
                if (request.CreateJunction)
                {
                    Report(progress, MoveStage.CreatingJunction, "正在创建目录联接", "在原路径建立指向新位置的目录联接…", 94, scan.TotalBytes, scan.TotalBytes, scan.Files.Count, scan.Files.Count, null);
                    string junctionError;
                    bool created = false;
                    try
                    {
                        created = Junction.Create(sourceDirectory, targetDirectory, out junctionError);
                    }
                    catch (Exception ex)
                    {
                        junctionError = ex.Message;
                        created = false;
                    }
                    if (!created || !Junction.Verify(sourceDirectory, targetDirectory))
                    {
                        return Failed(result, context,
                            "创建目录联接失败：" + (string.IsNullOrEmpty(junctionError) ? "联接校验未通过" : junctionError),
                            progress, true);
                    }
                    context.JunctionCreated = true;
                    result.JunctionCreated = true;
                    result.JunctionPath = sourceDirectory;
                    Log.Info("目录联接创建成功：" + sourceDirectory + " -> " + targetDirectory);

                    // 删除旧目录备份（失败只提示，不影响搬家结果）
                    string deleteError;
                    if (FileUtil.TryDeleteDirectory(backupPath, out deleteError))
                    {
                        context.BackupDeleted = true;
                        context.FreedBytes = scan.TotalBytes;
                        Log.Info("旧目录已删除：" + backupPath);
                    }
                    else
                    {
                        context.Warnings.Add("旧目录未能完全删除（可能仍被占用）：" + backupPath + "，请稍后手动清理。");
                        Log.Warn("旧目录删除不完整：" + backupPath + "：" + deleteError);
                    }
                }
                else
                {
                    // 不创建联接时，原路径直接消失，需要提示用户
                    string deleteError;
                    if (FileUtil.TryDeleteDirectory(backupPath, out deleteError))
                    {
                        context.BackupDeleted = true;
                        context.FreedBytes = scan.TotalBytes;
                    }
                    else
                    {
                        context.Warnings.Add("原目录未能完全删除（可能仍被占用）：" + backupPath + "，请稍后手动清理。");
                    }
                    context.Warnings.Add("未创建目录联接：原路径已不可用，部分软件可能无法启动。");
                }
                if (IsCancelled(isCancelled))
                {
                    return Canceled(result, context, progress, true);
                }

                // ---------- 8. 更新快捷方式 / 注册表 / 服务 ----------
                Report(progress, MoveStage.UpdatingReferences, "正在更新引用", "更新快捷方式、注册表与服务启动路径…", 96, scan.TotalBytes, scan.TotalBytes, scan.Files.Count, scan.Files.Count, null);
                UpdateReferences(request, context, result);

                // ---------- 9. 写日记 ----------
                Report(progress, MoveStage.Finalizing, "正在完成", "写入可撤销日记…", 99, scan.TotalBytes, scan.TotalBytes, scan.Files.Count, scan.Files.Count, null);
                JournalEntry journal = BuildJournalEntry(request, context);
                string journalPath = MoveJournal.Write(journal);
                if (string.IsNullOrEmpty(journalPath))
                {
                    // 日记没写成时不要给出无效路径，避免界面误以为可以撤销
                    result.JournalPath = null;
                    context.Warnings.Add("搬家日记写入失败（预留路径：" + context.JournalPath
                        + "）：本次操作无法自动撤销，请勿手动改动新位置的文件。");
                }
                else
                {
                    result.JournalPath = journalPath;
                }

                result.Success = true;
                result.FinalStage = MoveStage.Completed;
                result.JunctionCreated = context.JunctionCreated;
                result.FreedBytes = context.FreedBytes;
                result.Summary = BuildSummary(request, context);
                result.Warnings = context.Warnings;
                Report(progress, MoveStage.Completed, "搬家完成", result.Summary, 100, scan.TotalBytes, scan.TotalBytes, scan.Files.Count, scan.Files.Count, null);
                Log.Info("搬家完成：" + result.Summary);
                return result;
            }
            catch (Exception ex)
            {
                Log.Error("搬家过程中出现未预期异常", ex);
                string rollbackInfo = string.Empty;
                try
                {
                    rollbackInfo = Rollback(context, progress);
                }
                catch (Exception rollbackException)
                {
                    Log.Error("回滚时再次出现异常", rollbackException);
                    rollbackInfo = "回滚过程中出现异常：" + rollbackException.Message;
                }
                result.Success = false;
                result.FinalStage = context.HadIrreversibleProgress ? MoveStage.Failed : MoveStage.RolledBack;
                result.ErrorMessage = "搬家过程中出现未预期错误：" + ex.Message
                    + (string.IsNullOrEmpty(rollbackInfo) ? string.Empty : "。" + rollbackInfo);
                result.Warnings = context.Warnings;
                result.JournalPath = context.JournalPath;
                result.FailureLogPath = WriteFailureLog(result, context, rollbackInfo);
                Log.Error("搬家失败[" + StageText(result.FinalStage) + "]: " + result.ErrorMessage);
                return result;
            }
        }

        /// <summary>处理占用：停服务、优雅关闭/结束进程，然后复查</summary>
        private static void ResolveLocks(MoveRequest request, LockInfo locks, MoveContext context,
            MoveProgressHandler progress, Func<bool> isCancelled)
        {
            if (locks == null)
            {
                return;
            }

            if (request.StopRelatedServices && locks.Services != null && locks.Services.Count > 0)
            {
                for (int i = 0; i < locks.Services.Count; i++)
                {
                    if (IsCancelled(isCancelled))
                    {
                        return;
                    }
                    LockingService service = locks.Services[i];
                    if (service == null || string.IsNullOrEmpty(service.ServiceName))
                    {
                        continue;
                    }
                    string serviceLabel = string.IsNullOrEmpty(service.DisplayName) ? service.ServiceName : service.DisplayName;
                    Report(progress, MoveStage.ResolvingLocks, "正在处理占用", "正在停止服务：" + serviceLabel, 4);
                    string stopError;
                    if (ServiceHelper.TryStop(service.ServiceName, 15000, out stopError))
                    {
                        context.StoppedServices.Add(service.ServiceName);
                    }
                    else
                    {
                        context.Warnings.Add("无法停止服务「" + serviceLabel + "」：" + stopError + "（复制阶段可能失败）");
                    }
                }
            }

            if (request.KillBlockingProcesses && locks.Processes != null && locks.Processes.Count > 0)
            {
                for (int i = 0; i < locks.Processes.Count; i++)
                {
                    if (IsCancelled(isCancelled))
                    {
                        return;
                    }
                    LockingProcess lockingProcess = locks.Processes[i];
                    if (lockingProcess == null || lockingProcess.ProcessId <= 0)
                    {
                        continue;
                    }
                    string processLabel = string.IsNullOrEmpty(lockingProcess.ProcessName)
                        ? ("PID " + lockingProcess.ProcessId)
                        : (lockingProcess.ProcessName + "（PID " + lockingProcess.ProcessId + "）");
                    Report(progress, MoveStage.ResolvingLocks, "正在处理占用", "正在关闭进程：" + processLabel, 4);

                    string closeError;
                    bool closed = ProcessHelper.TryCloseGracefully(lockingProcess.ProcessId, 8000, out closeError);
                    if (!closed && ProcessHelper.IsRunning(lockingProcess.ProcessId))
                    {
                        string killError;
                        if (!ProcessHelper.TryKill(lockingProcess.ProcessId, out killError))
                        {
                            context.Warnings.Add("无法结束进程「" + processLabel + "」：" + killError);
                            continue;
                        }
                    }
                    if (!ProcessHelper.IsRunning(lockingProcess.ProcessId))
                    {
                        context.KilledProcesses.Add(processLabel);
                    }
                }
                if (context.KilledProcesses.Count > 0)
                {
                    context.Warnings.Add("已结束占用进程：" + JoinText(context.KilledProcesses, "、")
                        + "。如需要请手动重新启动这些程序。");
                }
            }
            else if (locks.Processes != null && locks.Processes.Count > 0 && !request.KillBlockingProcesses)
            {
                context.Warnings.Add("有 " + locks.Processes.Count + " 个进程正在使用该程序，未启用自动结束，"
                    + "若复制失败可选择「重启后完成」。");
            }

            // 复查一次占用情况，便于提前提示
            if (context.KilledProcesses.Count > 0 || context.StoppedServices.Count > 0)
            {
                try
                {
                    LockInfo again = LockInspector.Inspect(context.SourceDir, isCancelled);
                    if (again != null && again.IsLocked)
                    {
                        context.Warnings.Add("仍有进程或服务可能占用该目录，复制过程中可能会失败。");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("复查占用情况失败：" + ex.Message);
                }
            }
        }

        /// <summary>更新快捷方式、注册表与服务启动路径（失败只记警告）</summary>
        private static void UpdateReferences(MoveRequest request, MoveContext context, MoveResult result)
        {
            if (request.UpdateShortcuts)
            {
                try
                {
                    List<string> updated;
                    List<string> errors;
                    int count = ShortcutUpdater.UpdateAll(context.SourceDir, context.TargetDir, out updated, out errors);
                    if (updated != null && updated.Count > 0)
                    {
                        context.UpdatedShortcuts.AddRange(updated);
                    }
                    if (errors != null)
                    {
                        for (int i = 0; i < errors.Count; i++)
                        {
                            context.Warnings.Add("快捷方式更新失败：" + errors[i]);
                        }
                    }
                    result.ShortcutsUpdated = count;
                }
                catch (Exception ex)
                {
                    context.Warnings.Add("更新快捷方式时出错：" + ex.Message);
                }

                // 保证桌面有一个指向新位置的有效快捷方式（用户要求：搬完桌面仍可启动）
                try
                {
                    InstalledProgram program = request.Program;
                    string desktopShortcutPath;
                    string desktopError;
                    if (ShortcutUpdater.EnsureDesktopShortcut(program, context.TargetDir, out desktopShortcutPath, out desktopError))
                    {
                        result.DesktopShortcutCreated = true;
                        result.DesktopShortcutPath = desktopShortcutPath;
                        Log.Info("桌面快捷方式已就绪：" + desktopShortcutPath);
                    }
                    else
                    {
                        context.Warnings.Add("未能创建桌面快捷方式：" + desktopError);
                    }
                }
                catch (Exception ex)
                {
                    context.Warnings.Add("创建桌面快捷方式时出错：" + ex.Message);
                }

                // 清理因搬移而失效的旧快捷方式（目标仍存在的一律不删）
                try
                {
                    List<string> removed;
                    List<string> removeErrors;
                    int removedCount = ShortcutUpdater.RemoveDanglingShortcuts(context.SourceDir, out removed, out removeErrors);
                    result.ShortcutsRemoved = removedCount;
                    if (removedCount > 0)
                    {
                        context.Warnings.Add("已清理 " + removedCount + " 个失效的旧快捷方式。");
                    }
                    if (removeErrors != null)
                    {
                        for (int i = 0; i < removeErrors.Count; i++)
                        {
                            context.Warnings.Add("清理失效快捷方式时提示：" + removeErrors[i]);
                        }
                    }
                }
                catch (Exception ex)
                {
                    context.Warnings.Add("清理失效快捷方式时出错：" + ex.Message);
                }
            }

            if (request.UpdateRegistry)
            {
                try
                {
                    List<RegistryChange> applied;
                    string registryError;
                    int count = RegistryRewriter.UpdatePaths(context.SourceDir, context.TargetDir, out applied, out registryError);
                    if (applied != null && applied.Count > 0)
                    {
                        context.RegistryChanges.AddRange(applied);
                    }
                    result.RegistryEntriesUpdated = count;
                    if (!string.IsNullOrEmpty(registryError))
                    {
                        context.Warnings.Add("注册表更新不完整：" + registryError);
                    }
                }
                catch (Exception ex)
                {
                    context.Warnings.Add("更新注册表时出错：" + ex.Message);
                }
            }

            UpdateServiceImagePaths(context, result);
        }

        /// <summary>更新相关服务的 ImagePath 并重启之前停止的服务</summary>
        private static void UpdateServiceImagePaths(MoveContext context, MoveResult result)
        {
            for (int i = 0; i < context.Services.Count; i++)
            {
                LockingService service = context.Services[i];
                if (service == null || string.IsNullOrEmpty(service.ServiceName))
                {
                    continue;
                }
                string serviceLabel = string.IsNullOrEmpty(service.DisplayName) ? service.ServiceName : service.DisplayName;
                try
                {
                    string executable = ServiceHelper.GetExecutablePath(service.ServiceName);
                    if (string.IsNullOrEmpty(executable))
                    {
                        continue;
                    }
                    string normalizedExecutable = PathUtil.Normalize(executable);
                    if (!PathUtil.IsSubPathOf(normalizedExecutable, context.SourceDir))
                    {
                        continue;
                    }
                    string relative = normalizedExecutable.Substring(context.SourceDir.Length);
                    string newExecutable = PathUtil.Normalize(context.TargetDir + relative);
                    string setError;
                    if (ServiceHelper.SetImagePath(service.ServiceName, newExecutable, out setError))
                    {
                        Log.Info("已更新服务启动路径：" + serviceLabel + " -> " + newExecutable);
                    }
                    else
                    {
                        context.Warnings.Add("服务「" + serviceLabel + "」的启动路径未能更新：" + setError);
                    }
                }
                catch (Exception ex)
                {
                    context.Warnings.Add("更新服务「" + serviceLabel + "」启动路径时出错：" + ex.Message);
                }

                if (ContainsText(context.StoppedServices, service.ServiceName))
                {
                    string startError;
                    if (!ServiceHelper.TryStart(service.ServiceName, out startError))
                    {
                        context.Warnings.Add("服务「" + serviceLabel + "」重新启动失败：" + startError);
                    }
                }
            }
        }

        #endregion

        #region 复制与校验

        /// <summary>扫描源目录：文件、子目录、联接（跳过重解析点，记录其目标以便重建）</summary>
        private static SourceScan ScanSource(string sourceRoot, List<string> warnings)
        {
            SourceScan scan = new SourceScan();
            if (string.IsNullOrEmpty(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                return scan;
            }

            Queue<string> queue = new Queue<string>();
            queue.Enqueue(string.Empty);
            while (queue.Count > 0)
            {
                string relativeDirectory = queue.Dequeue();
                string directory = relativeDirectory.Length == 0
                    ? sourceRoot
                    : Path.Combine(sourceRoot, relativeDirectory);
                string longDirectory = PathUtil.ToLongPath(directory);

                string[] files;
                try
                {
                    files = Directory.GetFiles(longDirectory);
                }
                catch (Exception ex)
                {
                    warnings.Add("无法列目录中的文件：" + directory + "：" + ex.Message);
                    files = new string[0];
                }
                for (int i = 0; i < files.Length; i++)
                {
                    string filePath = files[i];
                    long length;
                    try
                    {
                        FileInfo info = new FileInfo(PathUtil.ToLongPath(filePath));
                        if (!info.Exists)
                        {
                            continue;
                        }
                        length = info.Length;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    FileItem item = new FileItem();
                    item.RelativePath = CombineRelative(relativeDirectory, SafeGetFileName(filePath));
                    item.SourcePath = filePath;
                    item.Length = length;
                    scan.Files.Add(item);
                    scan.TotalBytes += length;
                }

                string[] subDirectories;
                try
                {
                    subDirectories = Directory.GetDirectories(longDirectory);
                }
                catch (Exception ex)
                {
                    warnings.Add("无法列子目录：" + directory + "：" + ex.Message);
                    subDirectories = new string[0];
                }
                for (int i = 0; i < subDirectories.Length; i++)
                {
                    string subDirectory = subDirectories[i];
                    string subName = SafeGetFileName(subDirectory);
                    if (string.IsNullOrEmpty(subName))
                    {
                        continue;
                    }

                    bool isReparsePoint;
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(subDirectory);
                        isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                    }
                    catch (Exception)
                    {
                        warnings.Add("无法读取目录属性，已跳过：" + subDirectory);
                        continue;
                    }

                    if (isReparsePoint)
                    {
                        string linkTarget;
                        if (Junction.TryGetTarget(subDirectory, out linkTarget) && !string.IsNullOrEmpty(linkTarget))
                        {
                            LinkItem link = new LinkItem();
                            link.RelativePath = CombineRelative(relativeDirectory, subName);
                            link.Target = linkTarget;
                            scan.Links.Add(link);
                        }
                        else
                        {
                            warnings.Add("无法读取联接目标，已跳过该联接：" + subDirectory);
                        }
                        continue;
                    }

                    string subRelative = CombineRelative(relativeDirectory, subName);
                    scan.Directories.Add(subRelative);
                    queue.Enqueue(subRelative);
                }
            }
            return scan;
        }

        /// <summary>把扫描结果复制到目标目录：先建目录、再重建联接、最后逐文件复制</summary>
        private static bool Transfer(SourceScan scan, string sourceRoot, string targetRoot,
            MoveProgressHandler progress, Func<bool> isCancelled, out string error)
        {
            error = null;

            // 1. 创建目录结构
            for (int i = 0; i < scan.Directories.Count; i++)
            {
                string targetDirectory = Path.Combine(targetRoot, scan.Directories[i]);
                try
                {
                    Directory.CreateDirectory(PathUtil.ToLongPath(targetDirectory));
                }
                catch (Exception ex)
                {
                    error = "创建目标子目录失败：" + targetDirectory + "：" + TranslateFsError(ex);
                    return false;
                }
            }

            // 2. 重建目录联接（尽力而为，失败只记日志）
            for (int i = 0; i < scan.Links.Count; i++)
            {
                LinkItem link = scan.Links[i];
                string linkPath = Path.Combine(targetRoot, link.RelativePath);
                string linkError;
                try
                {
                    if (!Junction.Create(linkPath, link.Target, out linkError))
                    {
                        Log.Warn("无法在目标位置重建联接：" + linkPath + " -> " + link.Target + "：" + linkError);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("重建联接时出错：" + linkPath + "：" + ex.Message);
                }
            }

            // 3. 逐文件复制
            CopyState state = new CopyState();
            state.Scan = scan;
            state.Progress = progress;
            state.IsCancelled = isCancelled;

            for (int i = 0; i < scan.Files.Count; i++)
            {
                if (IsCancelled(isCancelled))
                {
                    error = "已取消";
                    return false;
                }
                FileItem item = scan.Files[i];
                string targetPath = Path.Combine(targetRoot, item.RelativePath);

                string parent = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    try
                    {
                        if (!Directory.Exists(PathUtil.ToLongPath(parent)))
                        {
                            Directory.CreateDirectory(PathUtil.ToLongPath(parent));
                        }
                    }
                    catch (Exception ex)
                    {
                        error = "创建目标目录失败：" + parent + "：" + TranslateFsError(ex);
                        return false;
                    }
                }

                string copyError;
                if (!CopyFileWithRetry(item, targetPath, state, out copyError))
                {
                    error = "复制文件 " + item.RelativePath + " 失败：" + copyError;
                    return false;
                }

                state.FilesDone++;

                // 剩余空间检查（每 64MB 检查一次，避免写满目标盘）
                if (state.BytesDone - state.LastSpaceCheckBytes >= SpaceCheckIntervalBytes)
                {
                    state.LastSpaceCheckBytes = state.BytesDone;
                    long remaining = scan.TotalBytes - state.BytesDone;
                    long free = GetFreeSpace(targetRoot);
                    if (free >= 0 && remaining > 0 && free < remaining + SpaceReserveBytes / 4)
                    {
                        error = "目标盘空间不足：剩余文件需要 " + TextUtil.FormatBytes(remaining)
                            + "，当前可用 " + TextUtil.FormatBytes(free) + "。";
                        return false;
                    }
                }

                ReportCopyProgress(state, item.RelativePath, true);
            }

            // 4. 复制完成后统计刷新
            ReportCopyProgress(state, null, true);
            return true;
        }

        /// <summary>带重试的单文件复制（失败 3 次后返回 false）</summary>
        private static bool CopyFileWithRetry(FileItem item, string targetPath, CopyState state, out string error)
        {
            error = null;
            for (int attempt = 1; attempt <= CopyRetryCount; attempt++)
            {
                try
                {
                    if (IsCancelled(state.IsCancelled))
                    {
                        error = "已取消";
                        return false;
                    }
                    CopySingleFile(item, targetPath, state);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    error = "已取消";
                    return false;
                }
                catch (Exception ex)
                {
                    error = TranslateFsError(ex);
                    if (attempt < CopyRetryCount)
                    {
                        Log.Warn("复制失败（第 " + attempt + " 次），" + CopyRetryDelayMs + " 毫秒后重试："
                            + item.RelativePath + "：" + ex.Message);
                        try
                        {
                            System.Threading.Thread.Sleep(CopyRetryDelayMs);
                        }
                        catch (Exception)
                        {
                            // 忽略线程休眠异常
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>复制单个文件（大文件用流式复制以便回报进度），并尽量保留时间/属性/ACL</summary>
        private static void CopySingleFile(FileItem item, string targetPath, CopyState state)
        {
            string longSource = PathUtil.ToLongPath(item.SourcePath);
            string longTarget = PathUtil.ToLongPath(targetPath);

            if (item.Length >= LargeFileThreshold)
            {
                CopyFileStream(item, longSource, longTarget, state);
            }
            else
            {
                File.Copy(longSource, longTarget, true);
                state.BytesDone += item.Length;
            }

            ApplyFileMetadata(longSource, longTarget);
        }

        /// <summary>流式复制大文件：1MB 缓冲，按 512KB/250ms 回报进度，支持取消</summary>
        private static void CopyFileStream(FileItem item, string longSource, string longTarget, CopyState state)
        {
            using (FileStream source = new FileStream(longSource, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (FileStream target = new FileStream(longTarget, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[CopyBufferSize];
                while (true)
                {
                    if (IsCancelled(state.IsCancelled))
                    {
                        throw new OperationCanceledException("用户取消了复制");
                    }
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }
                    target.Write(buffer, 0, read);
                    state.BytesDone += read;
                    ReportCopyProgress(state, item.RelativePath, false);
                }
                target.Flush();
            }
        }

        /// <summary>保留创建/修改/访问时间与只读、隐藏属性，并尽力复制 ACL（失败忽略）</summary>
        private static void ApplyFileMetadata(string longSource, string longTarget)
        {
            try
            {
                File.SetCreationTimeUtc(longTarget, File.GetCreationTimeUtc(longSource));
                File.SetLastWriteTimeUtc(longTarget, File.GetLastWriteTimeUtc(longSource));
                File.SetLastAccessTimeUtc(longTarget, File.GetLastAccessTimeUtc(longSource));
            }
            catch (Exception ex)
            {
                Log.Warn("复制文件时间属性失败：" + longTarget + "：" + ex.Message);
            }

            try
            {
                FileAttributes sourceAttributes = File.GetAttributes(longSource);
                FileAttributes targetAttributes = FileAttributes.Normal;
                if ((sourceAttributes & FileAttributes.ReadOnly) != 0)
                {
                    targetAttributes |= FileAttributes.ReadOnly;
                }
                if ((sourceAttributes & FileAttributes.Hidden) != 0)
                {
                    targetAttributes |= FileAttributes.Hidden;
                }
                File.SetAttributes(longTarget, targetAttributes);
            }
            catch (Exception ex)
            {
                Log.Warn("复制文件属性失败：" + longTarget + "：" + ex.Message);
            }

            // ACL 复制是尽力而为：目标盘文件系统不支持或权限不足时忽略
            try
            {
                FileSecurity security = File.GetAccessControl(longSource);
                File.SetAccessControl(longTarget, security);
            }
            catch (Exception)
            {
                // 忽略：ACL 复制失败不影响文件内容
            }
        }

        /// <summary>校验复制结果：文件数、总字节数、首尾文件大小</summary>
        private static bool VerifyTransfer(SourceScan scan, string targetRoot, out string error)
        {
            error = null;
            int targetFileCount;
            try
            {
                targetFileCount = FileUtil.CountFiles(targetRoot);
            }
            catch (Exception ex)
            {
                error = "统计目标文件数量失败：" + ex.Message;
                return false;
            }
            if (targetFileCount != scan.Files.Count)
            {
                error = "目标文件数量不一致（源 " + scan.Files.Count + " 个，目标 " + targetFileCount + " 个）。";
                return false;
            }

            long targetBytes = FileUtil.GetDirectorySize(targetRoot, null);
            if (scan.TotalBytes > 0 && targetBytes >= 0 && targetBytes != scan.TotalBytes)
            {
                error = "目标文件总大小不一致（源 " + TextUtil.FormatBytes(scan.TotalBytes)
                    + "，目标 " + TextUtil.FormatBytes(targetBytes) + "）。";
                return false;
            }

            if (scan.Files.Count > 0)
            {
                int[] indexes = scan.Files.Count > 1
                    ? new int[] { 0, scan.Files.Count - 1 }
                    : new int[] { 0 };
                for (int i = 0; i < indexes.Length; i++)
                {
                    FileItem item = scan.Files[indexes[i]];
                    string targetPath = Path.Combine(targetRoot, item.RelativePath);
                    long actualLength = GetFileLength(targetPath);
                    if (actualLength < 0)
                    {
                        error = "抽查文件不存在：" + targetPath;
                        return false;
                    }
                    if (actualLength != item.Length)
                    {
                        error = "抽查文件大小不一致：" + item.RelativePath
                            + "（源 " + item.Length + " 字节，目标 " + actualLength + " 字节）。";
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>上报复制进度（节流：至少 512KB 或 250ms 才回报一次）</summary>
        private static void ReportCopyProgress(CopyState state, string currentFile, bool force)
        {
            if (state == null || state.Progress == null || state.Scan == null)
            {
                return;
            }
            long sinceBytes = state.BytesDone - state.LastReportBytes;
            double elapsedMs = (DateTime.UtcNow - state.LastReportTime).TotalMilliseconds;
            if (!force && sinceBytes < ReportIntervalBytes && elapsedMs < ReportIntervalMs)
            {
                return;
            }
            state.LastReportBytes = state.BytesDone;
            state.LastReportTime = DateTime.UtcNow;

            int percent = CopyPercentStart;
            if (state.Scan.TotalBytes > 0)
            {
                long done = state.BytesDone;
                if (done > state.Scan.TotalBytes)
                {
                    done = state.Scan.TotalBytes;
                }
                percent = CopyPercentStart + (int)(done * CopyPercentSpan / state.Scan.TotalBytes);
            }
            else if (state.Scan.Files.Count > 0)
            {
                percent = CopyPercentStart + state.FilesDone * CopyPercentSpan / state.Scan.Files.Count;
            }
            if (percent > CopyPercentStart + CopyPercentSpan)
            {
                percent = CopyPercentStart + CopyPercentSpan;
            }

            string message = "已复制 " + TextUtil.FormatBytes(state.BytesDone) + " / " + TextUtil.FormatBytes(state.Scan.TotalBytes)
                + "（" + state.FilesDone + "/" + state.Scan.Files.Count + " 个文件）";
            Report(state.Progress, MoveStage.Copying, "正在复制文件", message, percent,
                state.BytesDone, state.Scan.TotalBytes, state.FilesDone, state.Scan.Files.Count, currentFile);
        }

        #endregion

        #region 回滚

        /// <summary>
        /// 回滚到搬家前状态：还原注册表、快捷方式、删除联接、源目录改回原名、删除目标副本。
        /// 返回回滚结果描述（中文），无回滚动作时返回空字符串。
        /// </summary>
        private static string Rollback(MoveContext context, MoveProgressHandler progress)
        {
            List<string> notes = new List<string>();
            if (context == null)
            {
                return string.Empty;
            }
            Report(progress, MoveStage.RolledBack, "正在回滚", "正在恢复到搬家前的状态…", -1);

            // 如果“联接已建立且旧目录已删除”，此时搬家实际上已经完成，不能盲目回滚
            if (context.JunctionCreated && context.BackupDeleted)
            {
                context.HadIrreversibleProgress = true;
                notes.Add("搬家已基本完成（原目录已删除并建立了目录联接），未执行自动回滚；如需还原请使用「撤销」功能");
                Log.Warn("回滚被跳过：" + notes[0]);
                return "回滚结果：" + JoinText(notes, "；");
            }

            // 1. 注册表
            if (context.RegistryChanges.Count > 0)
            {
                try
                {
                    string registryError;
                    if (RegistryRewriter.Revert(context.RegistryChanges, out registryError))
                    {
                        notes.Add("注册表已还原");
                    }
                    else
                    {
                        notes.Add("注册表还原不完整：" + registryError);
                    }
                }
                catch (Exception ex)
                {
                    notes.Add("注册表还原失败：" + ex.Message);
                }
            }

            // 2. 快捷方式
            if (context.UpdatedShortcuts.Count > 0 || context.JunctionCreated)
            {
                try
                {
                    List<string> restored;
                    List<string> errors;
                    int count = ShortcutUpdater.UpdateAll(context.TargetDir, context.SourceDir, out restored, out errors);
                    if (count > 0)
                    {
                        notes.Add("快捷方式已改回旧路径（" + count + " 个）");
                    }
                    if (errors != null)
                    {
                        for (int i = 0; i < errors.Count; i++)
                        {
                            notes.Add("快捷方式还原失败：" + errors[i]);
                        }
                    }
                }
                catch (Exception ex)
                {
                    notes.Add("快捷方式还原失败：" + ex.Message);
                }
            }

            // 3. 删除原路径上新建的联接（必须在恢复备份之前做）
            try
            {
                if (Junction.IsReparsePoint(context.SourceDir))
                {
                    string removeError;
                    if (Junction.Remove(context.SourceDir, out removeError))
                    {
                        notes.Add("已删除新建的目录联接");
                    }
                    else
                    {
                        notes.Add("删除新建目录联接失败：" + removeError);
                    }
                }
            }
            catch (Exception ex)
            {
                notes.Add("删除目录联接失败：" + ex.Message);
            }

            // 4. 源目录改回原名
            if (context.SourceRenamed
                && !string.IsNullOrEmpty(context.BackupDir)
                && Directory.Exists(context.BackupDir)
                && !Directory.Exists(context.SourceDir))
            {
                try
                {
                    Directory.Move(context.BackupDir, context.SourceDir);
                    notes.Add("原目录已恢复原名");
                }
                catch (Exception ex)
                {
                    notes.Add("原目录恢复失败（备份仍保留在 " + context.BackupDir + "）：" + TranslateFsError(ex));
                }
            }

            // 5. 重启之前停止的服务（尽力而为）
            for (int i = 0; i < context.StoppedServices.Count; i++)
            {
                try
                {
                    string startError;
                    if (!ServiceHelper.TryStart(context.StoppedServices[i], out startError))
                    {
                        notes.Add("服务「" + context.StoppedServices[i] + "」恢复启动失败：" + startError);
                    }
                }
                catch (Exception ex)
                {
                    notes.Add("服务「" + context.StoppedServices[i] + "」恢复启动失败：" + ex.Message);
                }
            }

            // 6. 删除目标副本（最后做，确保源目录已恢复）
            if (context.TargetCreated)
            {
                string deleteError;
                if (FileUtil.TryDeleteDirectory(context.TargetDir, out deleteError))
                {
                    notes.Add("目标副本已删除");
                }
                else
                {
                    notes.Add("目标副本删除不完整，请手动清理：" + context.TargetDir + "（" + deleteError + "）");
                }
            }

            string summary = notes.Count == 0 ? string.Empty : ("回滚结果：" + JoinText(notes, "；"));
            Log.Warn(string.IsNullOrEmpty(summary) ? "回滚完成：无需回滚操作。" : summary);
            return summary;
        }

        /// <summary>生成失败结果并附带回滚</summary>
        private static MoveResult Failed(MoveResult result, MoveContext context, string error,
            MoveProgressHandler progress, bool needRollback)
        {
            string rollbackInfo = string.Empty;
            if (needRollback)
            {
                try
                {
                    rollbackInfo = Rollback(context, progress);
                }
                catch (Exception ex)
                {
                    Log.Error("回滚时出现异常", ex);
                    rollbackInfo = "回滚过程中出现异常：" + ex.Message;
                }
            }
            else if (context != null && context.TargetCreated)
            {
                // 尚未复制任何内容，只需清掉空目标目录
                try
                {
                    if (FileUtil.IsDirectoryEmpty(context.TargetDir))
                    {
                        string deleteError;
                        if (FileUtil.TryDeleteDirectory(context.TargetDir, out deleteError))
                        {
                            rollbackInfo = "已清理空的目标目录。";
                        }
                        else
                        {
                            rollbackInfo = "空的目标目录未能清理：" + context.TargetDir + "（" + deleteError + "）。";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("清理目标目录失败：" + ex.Message);
                }
            }

            result.Success = false;
            result.FinalStage = MoveStage.RolledBack;
            result.ErrorMessage = error + (string.IsNullOrEmpty(rollbackInfo) ? string.Empty : "；" + rollbackInfo);
            result.Warnings = context == null ? new List<string>() : context.Warnings;
            result.JournalPath = context == null ? null : context.JournalPath;
            result.JunctionCreated = context != null && context.JunctionCreated;
            result.FreedBytes = 0;
            Report(progress, MoveStage.RolledBack, "操作失败", result.ErrorMessage, -1);
            result.FailureLogPath = WriteFailureLog(result, context, rollbackInfo);
            Log.Error("搬家失败[" + StageText(result.FinalStage) + "]: " + result.ErrorMessage);
            return result;
        }

        /// <summary>生成取消结果并回滚</summary>
        private static MoveResult Canceled(MoveResult result, MoveContext context, MoveProgressHandler progress, bool needRollback)
        {
            string rollbackInfo = string.Empty;
            if (needRollback)
            {
                try
                {
                    rollbackInfo = Rollback(context, progress);
                }
                catch (Exception ex)
                {
                    Log.Error("取消回滚时出现异常", ex);
                    rollbackInfo = "回滚过程中出现异常：" + ex.Message;
                }
            }

            result.Success = false;
            result.FinalStage = MoveStage.Canceled;
            result.ErrorMessage = "操作已取消。" + rollbackInfo;
            result.Warnings = context == null ? new List<string>() : context.Warnings;
            result.JournalPath = context == null ? null : context.JournalPath;
            Report(progress, MoveStage.Canceled, "已取消", result.ErrorMessage, -1);
            result.FailureLogPath = WriteFailureLog(result, context, rollbackInfo);
            Log.Warn("搬家失败[" + StageText(result.FinalStage) + "]: " + result.ErrorMessage);
            return result;
        }

        #endregion

        #region 撤销

        /// <summary>撤销一次搬家（依据日记文件）</summary>
        public static MoveResult Undo(string journalPath, MoveProgressHandler progress)
        {
            MoveResult result = new MoveResult();
            if (progress == null)
            {
                progress = IgnoreProgressDelegate;
            }
            try
            {
                string reason;
                if (!CanUndo(journalPath, out reason))
                {
                    result.Success = false;
                    result.FinalStage = MoveStage.Failed;
                    result.ErrorMessage = "无法撤销：" + (string.IsNullOrEmpty(reason) ? "未知原因" : reason);
                    return result;
                }

                JournalEntry journal = MoveJournal.Read(journalPath);
                if (journal == null)
                {
                    result.Success = false;
                    result.FinalStage = MoveStage.Failed;
                    result.ErrorMessage = "无法撤销：搬家日记已损坏或格式不正确。";
                    return result;
                }

                string oldDirectory = PathUtil.Normalize(journal.OldPath);
                string newDirectory = PathUtil.Normalize(journal.NewPath);
                result.OldPath = oldDirectory;
                result.NewPath = newDirectory;
                result.JournalPath = journalPath;
                Log.Info("开始撤销搬家：从 " + newDirectory + " 还原到 " + oldDirectory);

                Report(progress, MoveStage.Preparing, "正在准备撤销", "检查原路径与新位置的状态…", 2);

                // 1. 删除原路径上的目录联接
                if (Junction.IsReparsePoint(oldDirectory))
                {
                    if (!Directory.Exists(oldDirectory))
                    {
                        // 悬空联接，仍需删除链接本身
                    }
                    string removeError;
                    if (!Junction.Remove(oldDirectory, out removeError))
                    {
                        result.Success = false;
                        result.FinalStage = MoveStage.Failed;
                        result.ErrorMessage = "无法删除原路径上的目录联接：" + removeError;
                        return result;
                    }
                    Report(progress, MoveStage.Preparing, "正在准备撤销", "已移除原路径上的目录联接…", 4);
                }
                else if (Directory.Exists(oldDirectory))
                {
                    result.Success = false;
                    result.FinalStage = MoveStage.Failed;
                    result.ErrorMessage = "原路径已被一个真实目录占用，无法撤销：" + oldDirectory;
                    return result;
                }

                // 2. 把新位置的内容搬回旧路径
                string moveError;
                if (!MoveDirectoryBack(newDirectory, oldDirectory, progress, out moveError))
                {
                    result.Success = false;
                    result.FinalStage = MoveStage.Failed;
                    result.ErrorMessage = "搬回文件失败：" + moveError + "（原文件仍保留在 " + newDirectory + "）";
                    return result;
                }

                // 3. 还原注册表
                if (journal.RegistryChanges.Count > 0)
                {
                    string registryError;
                    if (RegistryRewriter.Revert(journal.RegistryChanges, out registryError))
                    {
                        Log.Info("撤销：注册表已还原 " + journal.RegistryChanges.Count + " 条。");
                    }
                    else
                    {
                        result.Warnings.Add("注册表还原不完整：" + registryError);
                    }
                }

                // 4. 快捷方式改回旧路径
                if (journal.UpdatedShortcuts.Count > 0)
                {
                    try
                    {
                        List<string> restored;
                        List<string> errors;
                        int count = ShortcutUpdater.UpdateAll(newDirectory, oldDirectory, out restored, out errors);
                        if (count > 0)
                        {
                            Log.Info("撤销：已把 " + count + " 个快捷方式改回旧路径。");
                        }
                        if (errors != null)
                        {
                            for (int i = 0; i < errors.Count; i++)
                            {
                                result.Warnings.Add("快捷方式还原失败：" + errors[i]);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add("快捷方式还原失败：" + ex.Message);
                    }
                }

                // 5. 标记日记为已撤销
                MoveJournal.MarkUndone(journal);

                result.Success = true;
                result.FinalStage = MoveStage.Completed;
                result.JunctionCreated = false;
                result.FreedBytes = 0;
                result.Summary = "已把「" + (string.IsNullOrEmpty(journal.DisplayName) ? PathUtil.GetLeafName(oldDirectory, "程序") : journal.DisplayName)
                    + "」从 " + newDirectory + " 还原到 " + oldDirectory + "。";
                Report(progress, MoveStage.Completed, "撤销完成", result.Summary, 100);
                Log.Info("撤销完成：" + result.Summary);
                return result;
            }
            catch (Exception ex)
            {
                Log.Error("撤销过程中出现异常", ex);
                result.Success = false;
                result.FinalStage = MoveStage.Failed;
                result.ErrorMessage = "撤销过程中出现未预期错误：" + ex.Message;
                return result;
            }
        }

        /// <summary>把新位置的目录搬回旧路径：同盘直接 Move，跨盘复制+校验+删除</summary>
        private static bool MoveDirectoryBack(string sourceDirectory, string targetDirectory,
            MoveProgressHandler progress, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(sourceDirectory) || !Directory.Exists(sourceDirectory))
            {
                error = "新位置目录不存在：" + sourceDirectory;
                return false;
            }
            try
            {
                string parent = Path.GetDirectoryName(targetDirectory);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                Directory.Move(sourceDirectory, targetDirectory);
                Log.Info("撤销：已直接移动目录 " + sourceDirectory + " -> " + targetDirectory);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("直接移动目录失败，改用复制方式（可能是跨盘）：" + ex.Message);
            }

            // 跨盘：复制 + 校验 + 删除源
            List<string> warnings = new List<string>();
            SourceScan scan = ScanSource(sourceDirectory, warnings);
            for (int i = 0; i < warnings.Count; i++)
            {
                Log.Warn("撤销扫描提示：" + warnings[i]);
            }
            if (!Transfer(scan, sourceDirectory, targetDirectory, progress, AlwaysFalseDelegate, out error))
            {
                return false;
            }
            string verifyError;
            if (!VerifyTransfer(scan, targetDirectory, out verifyError))
            {
                error = "校验失败：" + verifyError;
                return false;
            }
            string deleteError;
            if (!FileUtil.TryDeleteDirectory(sourceDirectory, out deleteError))
            {
                error = "新位置目录未能完全删除：" + sourceDirectory + "（" + deleteError + "）";
                return false;
            }
            return true;
        }

        /// <summary>列出所有可撤销的搬家日记（最新在前）</summary>
        public static List<JournalEntry> ListJournals()
        {
            List<JournalEntry> entries = new List<JournalEntry>();
            List<string> files = MoveJournal.ListFiles();
            for (int i = 0; i < files.Count; i++)
            {
                JournalEntry entry = MoveJournal.Read(files[i]);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
            return entries;
        }

        /// <summary>能否撤销，不能时给出原因</summary>
        public static bool CanUndo(string journalPath, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(journalPath))
            {
                reason = "未指定搬家日记文件。";
                return false;
            }
            if (!File.Exists(journalPath))
            {
                reason = "搬家日记不存在，可能已被清理：" + journalPath;
                return false;
            }
            JournalEntry entry = MoveJournal.Read(journalPath);
            if (entry == null)
            {
                reason = "搬家日记已损坏，无法读取。";
                return false;
            }
            if (MoveJournal.IsUndone(journalPath))
            {
                reason = "这次搬家已经撤销过了，无需重复撤销。";
                return false;
            }

            string oldDirectory = PathUtil.Normalize(entry.OldPath);
            string newDirectory = PathUtil.Normalize(entry.NewPath);
            if (oldDirectory.Length == 0 || newDirectory.Length == 0)
            {
                reason = "搬家日记中的路径信息不完整。";
                return false;
            }
            if (!Directory.Exists(newDirectory))
            {
                reason = "新位置目录已不存在，无法撤销：" + newDirectory;
                return false;
            }
            if (Directory.Exists(oldDirectory) && !Junction.IsReparsePoint(oldDirectory))
            {
                reason = "原路径已被一个真实目录占用，无法撤销：" + oldDirectory;
                return false;
            }
            return true;
        }

        #endregion

        #region 日记

        /// <summary>根据上下文组装日记条目</summary>
        private static JournalEntry BuildJournalEntry(MoveRequest request, MoveContext context)
        {
            JournalEntry journal = new JournalEntry();
            journal.Version = "1";
            journal.CreatedUtc = context.CreatedUtc;
            journal.OldPath = context.SourceDir;
            journal.NewPath = context.TargetDir;
            journal.JunctionCreated = context.JunctionCreated;
            journal.FreedBytes = context.FreedBytes;
            journal.SourceDeleted = context.BackupDeleted;
            journal.DisplayName = request.Program == null ? string.Empty : request.Program.DisplayName;
            journal.UpdatedShortcuts = new List<string>(context.UpdatedShortcuts);
            journal.RegistryChanges = new List<RegistryChange>(context.RegistryChanges);
            return journal;
        }

        /// <summary>生成人类可读的搬家结论</summary>
        private static string BuildSummary(MoveRequest request, MoveContext context)
        {
            string name = request.Program == null ? string.Empty : request.Program.DisplayName;
            if (string.IsNullOrEmpty(name))
            {
                name = PathUtil.GetLeafName(context.SourceDir, "程序");
            }
            StringBuilder builder = new StringBuilder();
            builder.Append("已成功把「");
            builder.Append(name);
            builder.Append("」从 ");
            builder.Append(context.SourceDir);
            builder.Append(" 搬到 ");
            builder.Append(context.TargetDir);
            if (context.FreedBytes > 0)
            {
                builder.Append("，释放 ");
                builder.Append(TextUtil.FormatBytes(context.FreedBytes));
            }
            if (context.JunctionCreated)
            {
                builder.Append("，原路径已建立目录联接");
            }
            builder.Append("。");
            return builder.ToString();
        }

        #endregion

        #region 辅助

        #region 失败诊断日志

        /// <summary>
        /// 写一份独立的失败诊断文件（便于用户反馈与后续迭代优化）。
        /// 优先写入 AppPaths.LogDir\failures；不可写时依次退到用户目录/临时目录。
        /// 任何异常都只记 Warn，绝不抛出；返回实际写入的文件路径（失败返回 null）。
        /// </summary>
        private static string WriteFailureLog(MoveResult result, MoveContext context, string rollbackInfo)
        {
            if (result == null)
            {
                return null;
            }
            try
            {
                string content = BuildFailureLogText(result, context, rollbackInfo);
                string fileName = "fail_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_"
                    + BuildFailureLeaf(context) + ".txt";
                List<string> directories = CandidateFailureDirectories();
                for (int i = 0; i < directories.Count; i++)
                {
                    string directory = directories[i];
                    if (string.IsNullOrEmpty(directory))
                    {
                        continue;
                    }
                    try
                    {
                        if (!Directory.Exists(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                        string path = Path.Combine(directory, fileName);
                        int suffix = 2;
                        while (File.Exists(path) && suffix < 100)
                        {
                            path = Path.Combine(directory,
                                Path.GetFileNameWithoutExtension(fileName) + "_" + suffix.ToString() + ".txt");
                            suffix++;
                        }
                        File.WriteAllText(path, content, new UTF8Encoding(true));
                        Log.Warn("已生成搬家失败诊断日志：" + path);
                        return path;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("写入失败诊断日志失败（" + directory + "）：" + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("生成失败诊断日志时出错：" + ex.Message);
            }
            return null;
        }

        /// <summary>组装诊断文件正文（中文标签，纯文本）</summary>
        private static string BuildFailureLogText(MoveResult result, MoveContext context, string rollbackInfo)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("=== 一键挪动 搬家失败诊断 ===");
            builder.AppendLine("产品版本: " + GetProductVersion());
            builder.AppendLine("发生时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("管理员权限: " + (ElevationUtil.IsElevated ? "是" : "否"));
            builder.AppendLine("操作系统: " + SafeOsVersion());
            builder.AppendLine("程序显示名: " + SafeText(context == null ? string.Empty : context.DisplayName));
            builder.AppendLine("原路径: " + SafeText(result.OldPath ?? (context == null ? string.Empty : context.SourceDir)));
            builder.AppendLine("目标路径: " + SafeText(result.NewPath ?? (context == null ? string.Empty : context.TargetDir)));
            builder.AppendLine("最终阶段: " + StageText(result.FinalStage) + "（" + result.FinalStage.ToString() + "）");
            builder.AppendLine("错误信息: " + SafeText(result.ErrorMessage));
            builder.AppendLine("回滚结果: " + (string.IsNullOrEmpty(rollbackInfo) ? "未执行回滚（或无需回滚）" : rollbackInfo));

            List<string> warnings = result.Warnings;
            builder.AppendLine("--- 警告列表（" + (warnings == null ? 0 : warnings.Count) + " 条）---");
            if (warnings != null && warnings.Count > 0)
            {
                for (int i = 0; i < warnings.Count; i++)
                {
                    builder.AppendLine((i + 1).ToString() + ". " + SafeText(warnings[i]));
                }
            }
            else
            {
                builder.AppendLine("（无）");
            }

            builder.AppendLine("--- 占用信息 ---");
            bool hasLockInfo = AppendLockInfo(builder, context);
            if (!hasLockInfo)
            {
                builder.AppendLine("（未检测到进程或服务占用，或占用检测不可用）");
            }

            builder.AppendLine("--- 最近 40 行运行日志 ---");
            try
            {
                string recent = Log.GetRecentText(40);
                if (string.IsNullOrEmpty(recent))
                {
                    builder.AppendLine("（无运行日志）");
                }
                else
                {
                    builder.Append(recent);
                    if (!recent.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                    {
                        builder.AppendLine();
                    }
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine("（读取运行日志失败：" + ex.Message + "）");
            }

            builder.AppendLine("=== 请把本文件附在反馈中，便于我们定位并修复问题 ===");
            return builder.ToString();
        }

        /// <summary>把占用进程/服务信息写进诊断文件；有内容返回 true</summary>
        private static bool AppendLockInfo(StringBuilder builder, MoveContext context)
        {
            bool hasContent = false;
            if (context == null)
            {
                return false;
            }
            LockInfo locks = context.Locks;
            if (locks != null && locks.Processes != null && locks.Processes.Count > 0)
            {
                for (int i = 0; i < locks.Processes.Count; i++)
                {
                    LockingProcess process = locks.Processes[i];
                    if (process == null)
                    {
                        continue;
                    }
                    string name = string.IsNullOrEmpty(process.ProcessName) ? "未知进程" : process.ProcessName;
                    builder.AppendLine("进程: " + name + "（PID " + process.ProcessId + "）"
                        + (string.IsNullOrEmpty(process.ExecutablePath) ? string.Empty : " 路径：" + process.ExecutablePath));
                    hasContent = true;
                }
            }
            if (locks != null && locks.Services != null && locks.Services.Count > 0)
            {
                for (int i = 0; i < locks.Services.Count; i++)
                {
                    LockingService service = locks.Services[i];
                    if (service == null)
                    {
                        continue;
                    }
                    string name = string.IsNullOrEmpty(service.DisplayName) ? service.ServiceName : service.DisplayName;
                    builder.AppendLine("服务: " + name + "（" + SafeText(service.State) + "）"
                        + (string.IsNullOrEmpty(service.ImagePath) ? string.Empty : " 路径：" + service.ImagePath));
                    hasContent = true;
                }
            }
            if (context.KilledProcesses != null && context.KilledProcesses.Count > 0)
            {
                builder.AppendLine("已被结束的进程: " + JoinText(context.KilledProcesses, "、"));
                hasContent = true;
            }
            if (context.StoppedServices != null && context.StoppedServices.Count > 0)
            {
                builder.AppendLine("被临时停止的服务: " + JoinText(context.StoppedServices, "、"));
                hasContent = true;
            }
            return hasContent;
        }

        /// <summary>诊断文件候选目录（优先程序数据目录，其次用户目录、临时目录）</summary>
        private static List<string> CandidateFailureDirectories()
        {
            List<string> directories = new List<string>();
            try
            {
                string logDir = AppPaths.LogDir;
                if (!string.IsNullOrEmpty(logDir))
                {
                    directories.Add(Path.Combine(logDir, "failures"));
                }
            }
            catch (Exception)
            {
                // 忽略：继续尝试其他目录
            }
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    directories.Add(Path.Combine(localAppData, "YijianNuodong", "logs", "failures"));
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
                    directories.Add(Path.Combine(temp, "YijianNuodong", "logs", "failures"));
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            return directories;
        }

        /// <summary>诊断文件名里的安全叶名（软件名或安装目录名）</summary>
        private static string BuildFailureLeaf(MoveContext context)
        {
            string source = string.Empty;
            if (context != null)
            {
                if (!string.IsNullOrEmpty(context.DisplayName))
                {
                    source = context.DisplayName;
                }
                else if (!string.IsNullOrEmpty(context.SourceDir))
                {
                    try
                    {
                        source = Path.GetFileName(context.SourceDir);
                    }
                    catch (Exception)
                    {
                        source = string.Empty;
                    }
                }
            }
            string leaf = PathUtil.SanitizeFolderName(source);
            if (string.IsNullOrEmpty(leaf))
            {
                leaf = "move";
            }
            return leaf;
        }

        /// <summary>产品版本（取程序集版本前三位，与界面版本号保持一致）</summary>
        private static string GetProductVersion()
        {
            try
            {
                Version version = typeof(MoveEngine).Assembly.GetName().Version;
                if (version != null)
                {
                    return version.Major.ToString() + "." + version.Minor.ToString() + "." + version.Build.ToString();
                }
            }
            catch (Exception)
            {
                // 取不到版本时退回固定值
            }
            return "1.0.0";
        }

        /// <summary>操作系统版本描述</summary>
        private static string SafeOsVersion()
        {
            try
            {
                return Environment.OSVersion.VersionString
                    + (Environment.Is64BitOperatingSystem ? "（64 位）" : "（32 位）");
            }
            catch (Exception)
            {
                return "未知";
            }
        }

        /// <summary>文本安全化：空值转成“（无）”占位</summary>
        private static string SafeText(string text)
        {
            return string.IsNullOrEmpty(text) ? "（无）" : text;
        }

        /// <summary>搬家阶段的中文名称（用于日志与诊断文件）</summary>
        private static string StageText(MoveStage stage)
        {
            switch (stage)
            {
                case MoveStage.Idle:
                    return "空闲";
                case MoveStage.Precheck:
                    return "预检";
                case MoveStage.ResolvingLocks:
                    return "处理占用";
                case MoveStage.WaitingForLock:
                    return "等待占用释放";
                case MoveStage.Preparing:
                    return "准备";
                case MoveStage.Copying:
                    return "复制文件";
                case MoveStage.Verifying:
                    return "校验";
                case MoveStage.RemovingSource:
                    return "移走原目录";
                case MoveStage.CreatingJunction:
                    return "创建目录联接";
                case MoveStage.UpdatingReferences:
                    return "更新引用";
                case MoveStage.Finalizing:
                    return "完成收尾";
                case MoveStage.Completed:
                    return "已完成";
                case MoveStage.Failed:
                    return "失败";
                case MoveStage.Canceled:
                    return "已取消";
                case MoveStage.RolledBack:
                    return "已回滚";
                case MoveStage.PendingReboot:
                    return "等待重启后完成";
                default:
                    return "未知阶段";
            }
        }

        #endregion
        /// <summary>上报进度（统一构造 MoveProgress，回调异常不外抛）</summary>
        private static void Report(MoveProgressHandler progress, MoveStage stage, string stageText, string message,
            int percent, long bytesDone, long bytesTotal, int filesDone, int filesTotal, string currentFile)
        {
            if (progress == null)
            {
                return;
            }
            try
            {
                MoveProgress item = new MoveProgress();
                item.Stage = stage;
                item.StageText = stageText;
                item.Message = message;
                item.Percent = percent;
                item.BytesDone = bytesDone;
                item.BytesTotal = bytesTotal;
                item.FilesDone = filesDone;
                item.FilesTotal = filesTotal;
                item.CurrentFile = currentFile;
                progress(item);
            }
            catch (Exception ex)
            {
                Log.Warn("进度回调执行失败：" + ex.Message);
            }
        }

        /// <summary>上报进度（简化重载）</summary>
        private static void Report(MoveProgressHandler progress, MoveStage stage, string stageText, string message, int percent)
        {
            Report(progress, stage, stageText, message, percent, 0, -1, 0, 0, null);
        }

        /// <summary>是否已取消</summary>
        private static bool IsCancelled(Func<bool> isCancelled)
        {
            if (isCancelled == null)
            {
                return false;
            }
            try
            {
                return isCancelled();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>生成旧目录备份路径：<原名>.__okm_old_<时间戳>（同父目录）</summary>
        private static string BuildBackupPath(string sourceDirectory)
        {
            string parent = Path.GetDirectoryName(sourceDirectory);
            string leaf = Path.GetFileName(sourceDirectory);
            if (string.IsNullOrEmpty(parent))
            {
                parent = sourceDirectory;
            }
            if (string.IsNullOrEmpty(leaf))
            {
                leaf = "program";
            }
            string baseName = leaf + ".__okm_old_" + DateTime.Now.ToString("yyyyMMddHHmmss");
            string path = Path.Combine(parent, baseName);
            int suffix = 2;
            while (Directory.Exists(path) || File.Exists(path))
            {
                path = Path.Combine(parent, baseName + "_" + suffix.ToString());
                suffix++;
            }
            return path;
        }

        /// <summary>判断是否为受保护的系统/应用目录</summary>
        private static bool IsProtectedFolderName(string path, out string matchedName)
        {
            matchedName = null;
            string normalized = PathUtil.Normalize(path);
            if (normalized.Length == 0)
            {
                return false;
            }
            string[] parts = normalized.Split('\\');
            string leaf = parts.Length > 0 ? parts[parts.Length - 1].Trim() : string.Empty;
            for (int i = 0; i < ProtectedLeafNames.Length; i++)
            {
                if (string.Equals(leaf, ProtectedLeafNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    matchedName = leaf;
                    return true;
                }
            }
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0)
                {
                    continue;
                }
                for (int j = 0; j < ProtectedContainerNames.Length; j++)
                {
                    if (string.Equals(part, ProtectedContainerNames[j], StringComparison.OrdinalIgnoreCase))
                    {
                        matchedName = part;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>取目录所在卷的可用空间；取不到返回 -1</summary>
        private static long GetFreeSpace(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return -1;
            }
            try
            {
                long free;
                long total;
                if (DriveAnalyzer.TryGetSpace(path, out free, out total))
                {
                    return free;
                }
            }
            catch (Exception)
            {
                // T1 组件不可用时退回系统接口
            }
            try
            {
                string root = PathUtil.GetDriveRoot(path);
                if (string.IsNullOrEmpty(root))
                {
                    return -1;
                }
                DriveInfo drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    return -1;
                }
                return drive.AvailableFreeSpace;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>取文件长度（支持超长路径），失败返回 -1</summary>
        private static long GetFileLength(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return -1;
            }
            try
            {
                FileInfo info = new FileInfo(PathUtil.ToLongPath(path));
                if (info.Exists)
                {
                    return info.Length;
                }
            }
            catch (Exception)
            {
                // 继续尝试普通路径
            }
            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Exists)
                {
                    return info.Length;
                }
            }
            catch (Exception)
            {
                return -1;
            }
            return -1;
        }

        /// <summary>把异常转成中文错误信息（保留系统原文便于排查）</summary>
        private static string TranslateFsError(Exception ex)
        {
            if (ex == null)
            {
                return "未知错误";
            }
            if (ex is OperationCanceledException)
            {
                return "已取消";
            }
            if (ex is UnauthorizedAccessException)
            {
                return "权限不足（" + ex.Message + "）";
            }
            if (ex is DirectoryNotFoundException)
            {
                return "目录不存在（" + ex.Message + "）";
            }
            if (ex is FileNotFoundException)
            {
                return "文件不存在（" + ex.Message + "）";
            }
            if (IsLockError(ex))
            {
                return "文件正被另一个程序使用（" + ex.Message + "）";
            }
            if (ex is PathTooLongException)
            {
                return "路径过长（" + ex.Message + "）";
            }
            if (ex is IOException)
            {
                return "磁盘读写失败（" + ex.Message + "）";
            }
            return ex.Message;
        }

        /// <summary>是否为文件占用类错误（ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION）</summary>
        private static bool IsLockError(Exception ex)
        {
            IOException ioException = ex as IOException;
            if (ioException == null)
            {
                return false;
            }
            int code = ioException.HResult & 0xFFFF;
            return code == 32 || code == 33;
        }

        /// <summary>拼接相对路径</summary>
        private static string CombineRelative(string parentRelative, string name)
        {
            if (string.IsNullOrEmpty(parentRelative))
            {
                return name;
            }
            return parentRelative + "\\" + name;
        }

        /// <summary>取文件名（失败返回空字符串）</summary>
        private static string SafeGetFileName(string path)
        {
            try
            {
                return Path.GetFileName(path) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>列表是否包含指定文本（忽略大小写）</summary>
        private static bool ContainsText(List<string> list, string text)
        {
            if (list == null || text == null)
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>拼接多行文本</summary>
        private static string JoinText(List<string> items, string separator)
        {
            if (items == null || items.Count == 0)
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(separator);
                }
                builder.Append(items[i]);
            }
            return builder.ToString();
        }

        /// <summary>进程列表转“微信.exe（PID 123）”形式</summary>
        private static string DescribeProcesses(List<LockingProcess> processes)
        {
            StringBuilder builder = new StringBuilder();
            int limit = processes.Count > 5 ? 5 : processes.Count;
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                {
                    builder.Append("、");
                }
                LockingProcess process = processes[i];
                string name = string.IsNullOrEmpty(process.ProcessName) ? "未知进程" : process.ProcessName;
                builder.Append(name);
                builder.Append("（PID ");
                builder.Append(process.ProcessId);
                builder.Append("）");
            }
            if (processes.Count > limit)
            {
                builder.Append(" 等 ").Append(processes.Count).Append(" 个");
            }
            return builder.ToString();
        }

        /// <summary>服务列表转中文摘要</summary>
        private static string DescribeServices(List<LockingService> services)
        {
            StringBuilder builder = new StringBuilder();
            int limit = services.Count > 5 ? 5 : services.Count;
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                {
                    builder.Append("、");
                }
                LockingService service = services[i];
                string name = string.IsNullOrEmpty(service.DisplayName) ? service.ServiceName : service.DisplayName;
                builder.Append(name);
            }
            if (services.Count > limit)
            {
                builder.Append(" 等 ").Append(services.Count).Append(" 个");
            }
            return builder.ToString();
        }

        /// <summary>回调占位</summary>
        private static bool ReturnFalse()
        {
            return false;
        }

        private static void IgnoreProgressCallback(MoveProgress progress)
        {
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
        private static extern bool SRSetRestorePointW(ref RESTOREPOINTINFO pRestorePtSpec, out STATEMGRSTATUS pSMgrStatus);

        /// <summary>尽力创建系统还原点（需要管理员权限与已启用系统保护），失败只给出原因</summary>
        private static bool TryCreateRestorePoint(string displayName, out string error)
        {
            error = null;
            try
            {
                string description = TextUtil.Clean(displayName);
                if (string.IsNullOrEmpty(description))
                {
                    description = "应用搬家";
                }
                if (description.Length > 200)
                {
                    description = description.Substring(0, 200);
                }

                RESTOREPOINTINFO info = new RESTOREPOINTINFO();
                info.dwEventType = BeginSystemChange;
                info.dwRestorePtType = ModifySettingsRestorePoint;
                info.llSequenceNumber = 0;
                info.szDescription = "一键挪动前自动创建（" + description + "）";

                STATEMGRSTATUS status;
                bool ok = SRSetRestorePointW(ref info, out status);
                int win32Error = Marshal.GetLastWin32Error();
                if (!ok || status.nStatus != 0)
                {
                    int code = ok ? (int)status.nStatus : win32Error;
                    error = "系统拒绝创建还原点（错误码 " + code + "），通常是因为未启用系统保护或权限不足。";
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

        #region 内部数据结构

        /// <summary>待复制文件</summary>
        private sealed class FileItem
        {
            public string RelativePath;
            public string SourcePath;
            public long Length;
        }

        /// <summary>需要在新位置重建的联接</summary>
        private sealed class LinkItem
        {
            public string RelativePath;
            public string Target;
        }

        /// <summary>源目录扫描结果</summary>
        private sealed class SourceScan
        {
            public List<FileItem> Files = new List<FileItem>();
            public List<string> Directories = new List<string>();
            public List<LinkItem> Links = new List<LinkItem>();
            public long TotalBytes;
        }

        /// <summary>复制进度状态</summary>
        private sealed class CopyState
        {
            public SourceScan Scan;
            public MoveProgressHandler Progress;
            public Func<bool> IsCancelled;
            public long BytesDone;
            public int FilesDone;
            public long LastReportBytes;
            public long LastSpaceCheckBytes;
            public DateTime LastReportTime;

            public CopyState()
            {
                LastReportTime = DateTime.UtcNow;
            }
        }

        /// <summary>一次搬家的运行时上下文（回滚依据）</summary>
        private sealed class MoveContext
        {
            public string SourceDir;
            public string TargetDir;
            public string DisplayName;
            public LockInfo Locks;
            public string BackupDir;
            public string JournalPath;
            public DateTime CreatedUtc;
            public bool TargetCreated;
            public bool SourceRenamed;
            public bool JunctionCreated;
            public bool BackupDeleted;
            public bool HadIrreversibleProgress;
            public long FreedBytes;
            public List<string> Warnings = new List<string>();
            public List<string> KilledProcesses = new List<string>();
            public List<string> StoppedServices = new List<string>();
            public List<LockingService> Services = new List<LockingService>();
            public List<string> UpdatedShortcuts = new List<string>();
            public List<RegistryChange> RegistryChanges = new List<RegistryChange>();
        }

        #endregion
    }
}

