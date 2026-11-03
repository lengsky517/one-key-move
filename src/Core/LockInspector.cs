// ============================================================================
//  一键挪动 (OneKeyMove) - 文件占用检测
//  职责：用 Windows Restart Manager 查找正在使用目录内文件的进程，
//        再补一次运行进程扫描（MainModule 路径匹配）与相关服务枚举。
//  说明：Restart Manager 需要先 RmStartSession，再注册资源，最后 RmGetList；
//        首次调用返回 ERROR_MORE_DATA(234) 时要按 needed 重新分配数组再取一次。
//  目标框架：.NET Framework 4.8 / C# 5
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace YijianNuodong.Core
{
    /// <summary>目录占用检测（进程 + 服务）</summary>
    public static class LockInspector
    {
        private const int ErrorSuccess = 0;
        private const int ErrorMoreData = 234;
        private const int MaxSampleFiles = 60;
        private const uint CchRmSessionKey = 32;

        /// <summary>RM_APP_TYPE.RmService，表示占用方是一个服务</summary>
        private const int RmAppTypeService = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public RM_FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;

            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;

            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(
            uint pSessionHandle,
            uint nFiles,
            string[] rgsFilenames,
            uint nApplications,
            RM_UNIQUE_PROCESS[] rgApplications,
            uint nServices,
            string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmGetList(
            uint dwSessionHandle,
            out uint pnProcInfoNeeded,
            ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[] rgAffectedApps,
            out uint lpdwRebootReasons);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        /// <summary>全面检查目录占用：Restart Manager 查占用进程 + 路径下服务 + 运行中进程</summary>
        public static LockInfo Inspect(string directoryPath, Func<bool> isCancelled)
        {
            LockInfo info = new LockInfo();
            string directory = PathUtil.Normalize(directoryPath);
            info.Path = directory;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                info.Notes.Add("目录不存在，跳过占用检测：" + directoryPath);
                return info;
            }
            if (isCancelled != null && isCancelled())
            {
                info.Notes.Add("已取消占用检测。");
                return info;
            }

            try
            {
                List<string> notes;
                List<LockingProcess> processes = FindProcessesUsingDirectory(directory, out notes);
                info.Processes.AddRange(processes);
                info.Notes.AddRange(notes);
            }
            catch (Exception ex)
            {
                info.Notes.Add("检测占用进程时出错：" + ex.Message);
                Log.Warn("检测占用进程时出错：" + ex.Message);
            }

            if (isCancelled != null && isCancelled())
            {
                info.Notes.Add("已取消占用检测。");
                return info;
            }

            try
            {
                List<LockingService> services = ServiceHelper.EnumerateServicesUnder(directory);
                info.Services.AddRange(services);
                if (services.Count > 0)
                {
                    info.Notes.Add("发现 " + services.Count + " 个服务的可执行文件位于该目录。");
                }
            }
            catch (Exception ex)
            {
                info.Notes.Add("检测相关服务时出错：" + ex.Message);
                Log.Warn("检测相关服务时出错：" + ex.Message);
            }

            Log.Info("占用检测完成：" + directory + "，进程 " + info.Processes.Count + " 个，服务 " + info.Services.Count + " 个");
            return info;
        }

        /// <summary>仅查找占用该目录文件的进程（Restart Manager + 运行进程路径扫描，按 PID 去重）</summary>
        public static List<LockingProcess> FindProcessesUsingDirectory(string directoryPath, out List<string> notes)
        {
            notes = new List<string>();
            List<LockingProcess> result = new List<LockingProcess>();
            string directory = PathUtil.Normalize(directoryPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                notes.Add("目录不存在，无法检测占用：" + directoryPath);
                return result;
            }

            List<string> sampleFiles = SampleFiles(directory, MaxSampleFiles);
            if (sampleFiles.Count == 0)
            {
                notes.Add("目录中没有可供检测的文件，Restart Manager 无法判断占用。");
            }
            else
            {
                string rmError;
                List<RM_PROCESS_INFO> processInfos = QueryRestartManager(sampleFiles, out rmError);
                if (!string.IsNullOrEmpty(rmError))
                {
                    notes.Add(rmError);
                }

                List<int> lockedPids = new List<int>();
                for (int i = 0; i < processInfos.Count; i++)
                {
                    RM_PROCESS_INFO processInfo = processInfos[i];
                    if (processInfo.ApplicationType == RmAppTypeService)
                    {
                        string serviceName = string.IsNullOrEmpty(processInfo.strServiceShortName)
                            ? processInfo.strAppName
                            : processInfo.strServiceShortName;
                        notes.Add("Restart Manager 报告有服务正在占用：" + (string.IsNullOrEmpty(serviceName) ? "未知服务" : serviceName));
                        continue;
                    }
                    int pid = processInfo.Process.dwProcessId;
                    if (pid > 0 && !lockedPids.Contains(pid))
                    {
                        lockedPids.Add(pid);
                    }
                }

                for (int i = 0; i < lockedPids.Count; i++)
                {
                    LockingProcess process = BuildLockingProcess(lockedPids[i]);
                    if (process != null)
                    {
                        result.Add(process);
                    }
                    else
                    {
                        notes.Add("Restart Manager 报告进程（PID " + lockedPids[i] + "）占用，但无法读取其详细信息。");
                    }
                }
                notes.Add("Restart Manager 已检测 " + sampleFiles.Count + " 个文件，发现 " + result.Count + " 个占用进程。");

                // Restart Manager 不可用或没有结果时，用“独占打开”探测被占用的文件，
                // 至少让用户知道“有文件被占用”，而不是误判为没有占用。
                if (result.Count == 0)
                {
                    int lockedFileCount = 0;
                    string firstLocked = null;
                    for (int i = 0; i < sampleFiles.Count; i++)
                    {
                        if (!CanOpenExclusive(sampleFiles[i]))
                        {
                            lockedFileCount++;
                            if (firstLocked == null)
                            {
                                firstLocked = sampleFiles[i];
                            }
                        }
                    }
                    if (lockedFileCount > 0)
                    {
                        notes.Add("检测到 " + lockedFileCount + " 个文件无法以独占方式打开（首个：" + firstLocked
                            + "），说明它们正被占用，但 Restart Manager 无法给出具体进程（可能权限不足或被安全软件限制）。");
                    }
                }
            }

            // 兜底扫描：遍历所有进程的主模块路径
            int scanAdded = ScanRunningProcesses(directory, result, notes);
            if (scanAdded > 0)
            {
                notes.Add("通过运行进程扫描额外发现 " + scanAdded + " 个运行在目录内的进程。");
            }

            return DeduplicateProcesses(result);
        }

        /// <summary>查找 ImagePath 指向该目录的服务</summary>
        public static List<LockingService> FindServicesUnder(string directoryPath)
        {
            try
            {
                return ServiceHelper.EnumerateServicesUnder(directoryPath);
            }
            catch (Exception ex)
            {
                Log.Warn("查找相关服务失败：" + ex.Message);
                return new List<LockingService>();
            }
        }

        /// <summary>
        /// 目录是否被占用；firstLockedFile 返回第一个确认被占用的文件路径。
        /// 优先用 Restart Manager 判断，为空时再尝试以独占方式打开主程序 exe。
        /// </summary>
        public static bool IsDirectoryLocked(string directoryPath, out string firstLockedFile)
        {
            firstLockedFile = null;
            string directory = PathUtil.Normalize(directoryPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            List<string> sampleFiles = SampleFiles(directory, MaxSampleFiles);
            if (sampleFiles.Count == 0)
            {
                return false;
            }

            string rmError;
            List<RM_PROCESS_INFO> processInfos = QueryRestartManager(sampleFiles, out rmError);
            if (!string.IsNullOrEmpty(rmError))
            {
                Log.Warn(rmError);
            }

            if (processInfos.Count > 0)
            {
                // 找出具体是哪个文件被独占（拿不到时退化为第一个采样文件）
                for (int i = 0; i < sampleFiles.Count; i++)
                {
                    if (!CanOpenExclusive(sampleFiles[i]))
                    {
                        firstLockedFile = sampleFiles[i];
                        return true;
                    }
                }
                firstLockedFile = sampleFiles[0];
                return true;
            }

            // Restart Manager 未报告时，用独占打开“主程序 exe”作为兜底判断
            string mainExecutable = FindMainExecutable(directory, sampleFiles);
            if (!string.IsNullOrEmpty(mainExecutable) && !CanOpenExclusive(mainExecutable))
            {
                firstLockedFile = mainExecutable;
                return true;
            }

            // 主程序没被占用时，再逐个探测采样文件（数据文件/日志被占用同样会导致搬家失败）
            for (int i = 0; i < sampleFiles.Count; i++)
            {
                if (string.Equals(sampleFiles[i], mainExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!CanOpenExclusive(sampleFiles[i]))
                {
                    firstLockedFile = sampleFiles[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>调用 Restart Manager 查询占用进程</summary>
        private static List<RM_PROCESS_INFO> QueryRestartManager(List<string> files, out string error)
        {
            List<RM_PROCESS_INFO> result = new List<RM_PROCESS_INFO>();
            error = null;
            if (files == null || files.Count == 0)
            {
                return result;
            }

            uint sessionHandle = 0;
            try
            {
                StringBuilder sessionKey = new StringBuilder((int)CchRmSessionKey + 1);
                int rc = RmStartSession(out sessionHandle, 0, sessionKey);
                if (rc != ErrorSuccess)
                {
                    error = "启动 Restart Manager 会话失败：" + Win32Text(rc);
                    return result;
                }

                rc = RmRegisterResources(sessionHandle, (uint)files.Count, files.ToArray(), 0, null, 0, null);
                if (rc != ErrorSuccess)
                {
                    error = "把文件注册到 Restart Manager 失败：" + Win32Text(rc);
                    return result;
                }

                uint needed = 0;
                uint count = 0;
                uint rebootReasons;
                rc = RmGetList(sessionHandle, out needed, ref count, null, out rebootReasons);
                if (rc == ErrorMoreData)
                {
                    count = needed;
                    if (count == 0)
                    {
                        return result;
                    }
                    if (count > 4096)
                    {
                        // 防御：异常巨大的返回值视为错误，避免申请过量内存
                        count = 4096;
                    }
                    RM_PROCESS_INFO[] buffer = new RM_PROCESS_INFO[count];
                    rc = RmGetList(sessionHandle, out needed, ref count, buffer, out rebootReasons);
                    if (rc != ErrorSuccess)
                    {
                        error = "读取占用进程列表失败：" + Win32Text(rc);
                        return result;
                    }
                    for (int i = 0; i < count && i < buffer.Length; i++)
                    {
                        result.Add(buffer[i]);
                    }
                }
                else if (rc != ErrorSuccess)
                {
                    error = "读取占用进程列表失败：" + Win32Text(rc);
                }
            }
            catch (DllNotFoundException)
            {
                error = "系统缺少 Restart Manager 组件（rstrtmgr.dll），无法精确检测占用。";
            }
            catch (EntryPointNotFoundException)
            {
                error = "系统 Restart Manager 接口不可用，无法精确检测占用。";
            }
            catch (Exception ex)
            {
                error = "Restart Manager 检测占用时出错：" + ex.Message;
            }
            finally
            {
                if (sessionHandle != 0)
                {
                    try
                    {
                        RmEndSession(sessionHandle);
                    }
                    catch (Exception)
                    {
                        // 结束会话失败不影响检测结果
                    }
                }
            }
            return result;
        }

        /// <summary>按 PID 补全进程信息；进程已退出或权限不足时返回 null</summary>
        private static LockingProcess BuildLockingProcess(int processId)
        {
            if (processId <= 0)
            {
                return null;
            }
            Process process = null;
            try
            {
                process = Process.GetProcessById(processId);
                if (process == null)
                {
                    return null;
                }
                LockingProcess info = new LockingProcess();
                info.ProcessId = processId;
                try
                {
                    info.ProcessName = process.ProcessName;
                }
                catch (Exception)
                {
                    info.ProcessName = string.Empty;
                }
                try
                {
                    info.HasMainWindow = process.MainWindowHandle != IntPtr.Zero;
                    info.MainWindowTitle = process.MainWindowTitle;
                }
                catch (Exception)
                {
                    info.HasMainWindow = false;
                    info.MainWindowTitle = string.Empty;
                }
                info.ExecutablePath = ProcessHelper.GetExecutablePath(processId);
                if (string.IsNullOrEmpty(info.ProcessName))
                {
                    info.ProcessName = string.IsNullOrEmpty(info.ExecutablePath)
                        ? "未知进程"
                        : Path.GetFileNameWithoutExtension(info.ExecutablePath);
                }
                return info;
            }
            catch (ArgumentException)
            {
                return null; // 进程已退出
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (process != null)
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>兜底扫描：主模块路径位于目标目录下的运行进程，返回新增数量</summary>
        private static int ScanRunningProcesses(string directory, List<LockingProcess> result, List<string> notes)
        {
            int added = 0;
            int denied = 0;
            Process[] processes = null;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception ex)
            {
                notes.Add("枚举运行进程失败：" + ex.Message);
                return 0;
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

            for (int i = 0; i < processes.Length; i++)
            {
                Process process = processes[i];
                if (process == null)
                {
                    continue;
                }
                try
                {
                    if (process.Id == currentPid)
                    {
                        continue;
                    }
                    string modulePath = null;
                    try
                    {
                        modulePath = process.MainModule.FileName;
                    }
                    catch (Exception)
                    {
                        denied++;
                        continue;
                    }
                    if (string.IsNullOrEmpty(modulePath))
                    {
                        continue;
                    }
                    string normalized = PathUtil.Normalize(modulePath);
                    if (!PathUtil.IsSubPathOf(normalized, directory))
                    {
                        continue;
                    }
                    LockingProcess info = new LockingProcess();
                    info.ProcessId = process.Id;
                    try
                    {
                        info.ProcessName = process.ProcessName;
                    }
                    catch (Exception)
                    {
                        info.ProcessName = Path.GetFileNameWithoutExtension(modulePath);
                    }
                    info.ExecutablePath = modulePath;
                    try
                    {
                        info.HasMainWindow = process.MainWindowHandle != IntPtr.Zero;
                        info.MainWindowTitle = process.MainWindowTitle;
                    }
                    catch (Exception)
                    {
                        info.HasMainWindow = false;
                        info.MainWindowTitle = string.Empty;
                    }
                    result.Add(info);
                    added++;
                }
                catch (Exception)
                {
                    // 单个进程失败不影响整体扫描
                }
                finally
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            if (denied > 0)
            {
                notes.Add("有 " + denied + " 个进程因权限不足无法读取路径，已跳过（如需彻底检测请以管理员身份运行）。");
            }
            return added;
        }

        /// <summary>按 PID 去重（保留首次出现的顺序）</summary>
        private static List<LockingProcess> DeduplicateProcesses(List<LockingProcess> processes)
        {
            List<LockingProcess> unique = new List<LockingProcess>();
            List<int> seen = new List<int>();
            for (int i = 0; i < processes.Count; i++)
            {
                LockingProcess process = processes[i];
                if (process == null || process.ProcessId <= 0)
                {
                    continue;
                }
                if (seen.Contains(process.ProcessId))
                {
                    continue;
                }
                seen.Add(process.ProcessId);
                unique.Add(process);
            }
            return unique;
        }

        /// <summary>采样目录内的文件（优先 .exe/.dll），最多 maxFiles 个</summary>
        private static List<string> SampleFiles(string directory, int maxFiles)
        {
            List<string> preferred = new List<string>();
            if (maxFiles <= 0)
            {
                return preferred;
            }
            List<string> others = new List<string>();
            try
            {
                Queue<string> queue = new Queue<string>();
                queue.Enqueue(directory);
                int visitedFiles = 0;
                int visitedDirectories = 0;
                while (queue.Count > 0 && preferred.Count < maxFiles && visitedDirectories < maxFiles * 4)
                {
                    string current = queue.Dequeue();
                    visitedDirectories++;
                    string longCurrent = PathUtil.ToLongPath(current);

                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(longCurrent);
                    }
                    catch (Exception)
                    {
                        files = new string[0];
                    }
                    for (int i = 0; i < files.Length; i++)
                    {
                        visitedFiles++;
                        if (IsPreferredFile(files[i]))
                        {
                            if (preferred.Count < maxFiles)
                            {
                                preferred.Add(ToNormalPath(files[i]));
                            }
                        }
                        else if (others.Count < maxFiles)
                        {
                            others.Add(ToNormalPath(files[i]));
                        }
                    }
                    if (preferred.Count >= maxFiles)
                    {
                        break;
                    }

                    string[] subs;
                    try
                    {
                        subs = Directory.GetDirectories(longCurrent);
                    }
                    catch (Exception)
                    {
                        subs = new string[0];
                    }
                    for (int i = 0; i < subs.Length; i++)
                    {
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(subs[i]);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        queue.Enqueue(ToNormalPath(subs[i]));
                    }
                    if (visitedFiles > maxFiles * 40)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("采样目录文件失败：" + ex.Message);
            }

            for (int i = 0; i < others.Count && preferred.Count < maxFiles; i++)
            {
                preferred.Add(others[i]);
            }
            return preferred;
        }

        /// <summary>是否优先采样的文件类型（可执行文件/动态库最容易被占用）</summary>
        private static bool IsPreferredFile(string path)
        {
            string extension = string.Empty;
            try
            {
                extension = Path.GetExtension(path) ?? string.Empty;
            }
            catch (Exception)
            {
                return false;
            }
            return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".sys", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ocx", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>去掉 \\?\ 前缀，Restart Manager 只接受普通路径</summary>
        private static string ToNormalPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path.Substring(4);
            }
            return path;
        }

        /// <summary>找出目录中的主程序 exe（优先目录根下的 exe）</summary>
        private static string FindMainExecutable(string directory, List<string> sampleFiles)
        {
            try
            {
                string[] rootFiles = Directory.GetFiles(PathUtil.ToLongPath(directory));
                for (int i = 0; i < rootFiles.Length; i++)
                {
                    string extension = Path.GetExtension(rootFiles[i]);
                    if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return ToNormalPath(rootFiles[i]);
                    }
                }
            }
            catch (Exception)
            {
                // 目录不可读时继续用采样结果
            }
            if (sampleFiles != null)
            {
                for (int i = 0; i < sampleFiles.Count; i++)
                {
                    string extension = Path.GetExtension(sampleFiles[i]);
                    if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return sampleFiles[i];
                    }
                }
            }
            return null;
        }

        /// <summary>能否以独占方式打开文件（被占用时返回 false）</summary>
        private static bool CanOpenExclusive(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }
            try
            {
                using (FileStream stream = new FileStream(PathUtil.ToLongPath(path), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    // 能打开说明没有被独占占用
                }
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // 只读/系统文件也可能以独占方式打不开，但不能据此判断被占用
                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>Restart Manager 错误码转中文提示</summary>
        private static string Win32Text(int code)
        {
            switch (code)
            {
                case 5:
                    return "访问被拒绝（需要管理员权限），错误码 5";
                case 87:
                    return "参数无效，错误码 87";
                case 234:
                    return "数据未一次取完，错误码 234";
                case 160:
                    return "存在未结束的会话，错误码 160";
                default:
                    try
                    {
                        return new System.ComponentModel.Win32Exception(code).Message + "（错误码 " + code + "）";
                    }
                    catch (Exception)
                    {
                        return "错误码 " + code;
                    }
            }
        }
    }
}
