// ============================================================================
//  一键挪动 (OneKeyMove) - 重启后自动完成搬家
//  场景：目标程序始终被占用（复制/改名阶段失败），登记为“开机时自动完成”。
//  做法：
//    1) 把 MoveRequest 序列化到 %ProgramData%\YijianNuodong\pending\pending.job；
//    2) 用 schtasks.exe 创建 ONSTART（SYSTEM / 最高权限）计划任务，开机 30 秒后执行
//       "<本程序>" --complete-pending "<任务文件>"；
//    3) 计划任务创建失败时（非管理员等）退到 HKCU RunOnce；
//    4) 开机由 Program.cs 的 --complete-pending 分支调用 CompletePending 完成搬家。
//  目标框架：.NET Framework 4.8 / C# 5
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace YijianNuodong.Core
{
    /// <summary>重启后完成搬家（挂起任务 + 开机计划任务）</summary>
    public static class RebootMover
    {
        private const string TaskName = "YijianNuodong_PendingMove";
        private const string JobFileName = "pending.job";
        private const string RunOnceValueName = "YijianNuodong_PendingMove";
        private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
        private const string SchtasksPath = @"C:\Windows\System32\schtasks.exe";
        private const int SchtasksTimeoutMs = 30000;

        private static string _cachedPendingDir;

        /// <summary>
        /// 登记重启后完成的任务：写任务文件并创建开机计划任务（失败退到 HKCU RunOnce）。
        /// 成功返回 true，jobFile 为任务文件路径。
        /// </summary>
        public static bool Schedule(string exePath, MoveRequest request, out string jobFile, out string error)
        {
            jobFile = null;
            error = null;
            if (request == null || request.Program == null)
            {
                error = "搬家请求无效：缺少要搬移的程序信息，无法登记开机任务。";
                return false;
            }
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                error = "程序路径无效，无法登记开机任务：" + exePath;
                return false;
            }

            string pendingDirectory = ResolvePendingDirectory();
            string path = Path.Combine(pendingDirectory, JobFileName);
            try
            {
                if (!Directory.Exists(pendingDirectory))
                {
                    Directory.CreateDirectory(pendingDirectory);
                }
                List<string> lines = BuildJobLines(request);
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                error = "写入挂起任务文件失败：" + ex.Message;
                jobFile = null;
                return false;
            }
            jobFile = path;
            Log.Info("已写入挂起搬家任务文件：" + path);

            // 依次尝试：带 /DELAY 的计划任务 -> 不带 /DELAY 的计划任务 -> HKCU RunOnce
            string schtasksError;
            if (TryCreateScheduledTask(exePath, path, true, out schtasksError))
            {
                Log.Info("已登记开机自动完成计划任务（含 30 秒延迟）：" + TaskName);
                return true;
            }
            Log.Warn("创建计划任务失败（带延迟），将去掉 /DELAY 重试：" + schtasksError);

            if (TryCreateScheduledTask(exePath, path, false, out schtasksError))
            {
                Log.Info("已登记开机自动完成计划任务（无延迟）：" + TaskName);
                return true;
            }
            Log.Warn("创建计划任务失败（无延迟），将尝试 HKCU RunOnce：" + schtasksError);

            string runOnceError;
            if (TryCreateRunOnce(exePath, path, out runOnceError))
            {
                Log.Warn("已登记到当前用户 RunOnce（计划任务创建失败，可能需要该用户下次登录才能完成）：" + schtasksError);
                return true;
            }

            error = "登记开机自动完成任务失败：计划任务错误（" + schtasksError + "）；RunOnce 错误（" + runOnceError + "）。";
            Log.Error(error);
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // 删除失败忽略
            }
            jobFile = null;
            return false;
        }

        /// <summary>开机时执行挂起的搬家任务（无界面，由 Program --complete-pending 调用）</summary>
        public static MoveResult CompletePending(string jobFile, MoveProgressHandler progress, Func<bool> isCancelled)
        {
            string path = jobFile;
            if (string.IsNullOrEmpty(path))
            {
                path = Path.Combine(ResolvePendingDirectory(), JobFileName);
            }

            MoveResult result = new MoveResult();
            if (!File.Exists(path))
            {
                result.Success = false;
                result.FinalStage = MoveStage.Failed;
                result.ErrorMessage = "找不到挂起的搬家任务文件：" + path;
                Log.Error(result.ErrorMessage);
                return result;
            }

            MoveRequest request = ReadJob(path);
            if (request == null)
            {
                result.Success = false;
                result.FinalStage = MoveStage.Failed;
                result.ErrorMessage = "挂起的搬家任务文件已损坏，无法执行：" + path;
                Log.Error(result.ErrorMessage);
                return result;
            }

            Log.Info("开始执行开机挂起的搬家任务：" + path
                + "（" + (request.Program == null ? "未知程序" : request.Program.DisplayName)
                + "：" + (request.Program == null ? string.Empty : request.Program.InstallPath) + "）");
            try
            {
                result = MoveEngine.ExecuteCore(request, progress, isCancelled);
            }
            catch (Exception ex)
            {
                Log.Error("执行挂起搬家任务时出现异常", ex);
                result = new MoveResult();
                result.Success = false;
                result.FinalStage = MoveStage.Failed;
                result.ErrorMessage = "执行挂起搬家任务时出现未预期错误：" + ex.Message;
            }

            if (result != null && result.Success)
            {
                try
                {
                    File.Delete(path);
                    Log.Info("挂起搬家任务已完成，任务文件已删除：" + path);
                }
                catch (Exception ex)
                {
                    Log.Warn("挂起搬家任务已完成，但任务文件删除失败：" + ex.Message);
                }
                string deleteError;
                RemoveScheduledTask(out deleteError);
                RemoveRunOnce();
            }
            else
            {
                Log.Warn("挂起搬家任务未成功，保留任务文件以便下次开机重试：" + path);
            }
            return result;
        }

        /// <summary>是否存在挂起任务</summary>
        public static bool HasPendingJob(out string jobFile)
        {
            jobFile = null;
            List<string> directories = CandidatePendingDirectories();
            for (int i = 0; i < directories.Count; i++)
            {
                if (string.IsNullOrEmpty(directories[i]))
                {
                    continue;
                }
                try
                {
                    string path = Path.Combine(directories[i], JobFileName);
                    if (File.Exists(path))
                    {
                        jobFile = path;
                        return true;
                    }
                }
                catch (Exception)
                {
                    // 忽略：继续检查下一个目录
                }
            }
            return false;
        }

        /// <summary>取消挂起任务（删除计划任务、RunOnce 项与任务文件）</summary>
        public static bool CancelPending(out string error)
        {
            error = null;
            string taskError;
            RemoveScheduledTask(out taskError);
            RemoveRunOnce();

            bool deleted = true;
            List<string> directories = CandidatePendingDirectories();
            for (int i = 0; i < directories.Count; i++)
            {
                if (string.IsNullOrEmpty(directories[i]))
                {
                    continue;
                }
                try
                {
                    string path = Path.Combine(directories[i], JobFileName);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        Log.Info("已删除挂起任务文件：" + path);
                    }
                }
                catch (Exception ex)
                {
                    deleted = false;
                    error = "删除挂起任务文件失败（" + directories[i] + "）：" + ex.Message;
                    Log.Warn(error);
                }
            }
            return deleted;
        }

        #region 计划任务 / RunOnce

        /// <summary>创建 ONSTART 计划任务（可能带 /DELAY）</summary>
        private static bool TryCreateScheduledTask(string exePath, string jobFile, bool withDelay, out string error)
        {
            string arguments = BuildScheduledTaskArguments(exePath, jobFile, withDelay);
            string output;
            if (RunSchtasks(arguments, out output))
            {
                error = null;
                return true;
            }
            error = string.IsNullOrEmpty(output) ? "schtasks 执行失败" : output;
            return false;
        }

        /// <summary>拼装 schtasks /Create 参数（/TR 里的内层引号用 \" 转义）</summary>
        private static string BuildScheduledTaskArguments(string exePath, string jobFile, bool withDelay)
        {
            string innerCommand = "\"" + exePath + "\" --complete-pending \"" + jobFile + "\"";
            string escapedCommand = innerCommand.Replace("\"", "\\\"");
            StringBuilder builder = new StringBuilder();
            builder.Append("/Create /F /SC ONSTART /RU SYSTEM /RL HIGHEST");
            builder.Append(" /TN \"");
            builder.Append(TaskName);
            builder.Append("\" /TR \"");
            builder.Append(escapedCommand);
            builder.Append("\"");
            if (withDelay)
            {
                builder.Append(" /DELAY 0000:30");
            }
            return builder.ToString();
        }

        /// <summary>删除计划任务（不存在时也视为成功）</summary>
        private static bool RemoveScheduledTask(out string error)
        {
            error = null;
            string output;
            if (RunSchtasks("/Delete /F /TN \"" + TaskName + "\"", out output))
            {
                Log.Info("已删除开机计划任务：" + TaskName);
                return true;
            }
            // 任务不存在属于正常情况，不当作失败
            Log.Info("删除计划任务返回：" + (string.IsNullOrEmpty(output) ? "任务可能不存在" : output));
            return true;
        }

        /// <summary>在执行计划任务失败时，退到当前用户 RunOnce</summary>
        private static bool TryCreateRunOnce(string exePath, string jobFile, out string error)
        {
            error = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunOnceKeyPath, true))
                {
                    if (key == null)
                    {
                        error = "无法打开 HKCU RunOnce 注册表项。";
                        return false;
                    }
                    string command = "\"" + exePath + "\" --complete-pending \"" + jobFile + "\"";
                    key.SetValue(RunOnceValueName, command, RegistryValueKind.String);
                }
                Log.Warn("已把开机完成任务登记到 HKCU RunOnce：" + RunOnceValueName);
                return true;
            }
            catch (Exception ex)
            {
                error = "写入 HKCU RunOnce 失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>删除 HKCU RunOnce 项（不存在时忽略）</summary>
        private static void RemoveRunOnce()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunOnceKeyPath, true))
                {
                    if (key == null)
                    {
                        return;
                    }
                    if (key.GetValue(RunOnceValueName) != null)
                    {
                        key.DeleteValue(RunOnceValueName, false);
                        Log.Info("已删除 RunOnce 登记项。");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("删除 RunOnce 登记项失败：" + ex.Message);
            }
        }

        /// <summary>执行 schtasks.exe 并捕获输出</summary>
        private static bool RunSchtasks(string arguments, out string output)
        {
            output = null;
            try
            {
                string schtasks = SchtasksPath;
                if (!File.Exists(schtasks))
                {
                    string systemDirectory = Environment.SystemDirectory;
                    if (!string.IsNullOrEmpty(systemDirectory))
                    {
                        schtasks = Path.Combine(systemDirectory, "schtasks.exe");
                    }
                }

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = schtasks;
                startInfo.Arguments = arguments;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        output = "无法启动 schtasks.exe";
                        return false;
                    }
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(SchtasksTimeoutMs))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception)
                        {
                            // 忽略
                        }
                        output = "schtasks 执行超时";
                        return false;
                    }
                    string stdout = outputTask.Result;
                    string stderr = errorTask.Result;
                    if (process.ExitCode == 0)
                    {
                        output = TextUtil.Clean(stdout);
                        return true;
                    }
                    string detail = TextUtil.Clean(stderr);
                    if (string.IsNullOrEmpty(detail))
                    {
                        detail = TextUtil.Clean(stdout);
                    }
                    if (string.IsNullOrEmpty(detail))
                    {
                        detail = "schtasks 退出码 " + process.ExitCode;
                    }
                    output = detail;
                    return false;
                }
            }
            catch (Exception ex)
            {
                output = "调用 schtasks 失败：" + ex.Message;
                return false;
            }
        }

        #endregion

        #region 任务文件序列化

        /// <summary>挂起任务文件候选目录（优先 %ProgramData%，不可写时依次退化）</summary>
        private static List<string> CandidatePendingDirectories()
        {
            List<string> directories = new List<string>();
            if (!string.IsNullOrEmpty(_cachedPendingDir))
            {
                directories.Add(_cachedPendingDir);
            }
            try
            {
                string primary = Path.Combine(AppPaths.BaseDir, "pending");
                if (!ContainsPath(directories, primary))
                {
                    directories.Add(primary);
                }
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
                    string path = Path.Combine(localAppData, "YijianNuodong", "pending");
                    if (!ContainsPath(directories, path))
                    {
                        directories.Add(path);
                    }
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            try
            {
                string temp = Path.Combine(Path.GetTempPath(), "YijianNuodong", "pending");
                if (!ContainsPath(directories, temp))
                {
                    directories.Add(temp);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            return directories;
        }

        /// <summary>解析出真正可用的挂起任务目录（会尝试创建）</summary>
        private static string ResolvePendingDirectory()
        {
            List<string> directories = CandidatePendingDirectories();
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
                    _cachedPendingDir = directory;
                    return directory;
                }
                catch (Exception ex)
                {
                    Log.Warn("准备挂起任务目录失败（" + directory + "）：" + ex.Message);
                }
            }
            string fallback = Path.Combine(Path.GetTempPath(), "YijianNuodong", "pending");
            try
            {
                Directory.CreateDirectory(fallback);
            }
            catch (Exception)
            {
                // 交给后续写入时报错
            }
            _cachedPendingDir = fallback;
            return fallback;
        }

        /// <summary>把 MoveRequest 序列化成文本行（值一律 Base64）</summary>
        private static List<string> BuildJobLines(MoveRequest request)
        {
            List<string> lines = new List<string>();
            lines.Add("# 一键挪动 挂起搬家任务 v1");
            lines.Add("[request]");
            InstalledProgram program = request.Program;
            lines.Add("program.id=" + Encode(program.Id));
            lines.Add("program.displayname=" + Encode(program.DisplayName));
            lines.Add("program.installpath=" + Encode(program.InstallPath));
            lines.Add("program.executablepath=" + Encode(program.ExecutablePath));
            lines.Add("program.publisher=" + Encode(program.Publisher));
            lines.Add("program.version=" + Encode(program.Version));
            lines.Add("program.sizebytes=" + Encode(program.SizeBytes.ToString()));
            lines.Add("program.source=" + Encode(((int)program.Source).ToString()));
            lines.Add("program.uninstallstring=" + Encode(program.UninstallString));
            lines.Add("program.shortcutpath=" + Encode(program.ShortcutPath));
            lines.Add("program.driveroot=" + Encode(program.DriveRoot));
            lines.Add("request.targetdirectory=" + Encode(request.TargetDirectory));
            lines.Add("request.createjunction=" + Encode(request.CreateJunction ? "1" : "0"));
            lines.Add("request.killblockingprocesses=" + Encode(request.KillBlockingProcesses ? "1" : "0"));
            lines.Add("request.stoprelatedservices=" + Encode(request.StopRelatedServices ? "1" : "0"));
            lines.Add("request.updateshortcuts=" + Encode(request.UpdateShortcuts ? "1" : "0"));
            lines.Add("request.updateregistry=" + Encode(request.UpdateRegistry ? "1" : "0"));
            lines.Add("request.createrestorepoint=" + Encode(request.CreateRestorePoint ? "1" : "0"));
            lines.Add("createdutc=" + Encode(DateTime.UtcNow.ToString("o")));
            return lines;
        }

        /// <summary>读取挂起任务文件；格式损坏返回 null</summary>
        private static MoveRequest ReadJob(string path)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Warn("读取挂起任务文件失败：" + path + "：" + ex.Message);
                return null;
            }

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool hasRequestSection = false;
            string section = string.Empty;
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null)
                    {
                        continue;
                    }
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }
                    if (trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                    {
                        section = trimmed.Substring(1, trimmed.Length - 2).Trim().ToLowerInvariant();
                        if (section == "request")
                        {
                            hasRequestSection = true;
                        }
                        continue;
                    }
                    int index = trimmed.IndexOf('=');
                    if (index <= 0)
                    {
                        continue;
                    }
                    string key = trimmed.Substring(0, index).Trim();
                    string value = Decode(trimmed.Substring(index + 1));
                    values[key] = value;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("解析挂起任务文件失败：" + path + "：" + ex.Message);
                return null;
            }

            if (!hasRequestSection)
            {
                return null;
            }

            InstalledProgram program = new InstalledProgram();
            program.Id = GetValue(values, "program.id");
            program.DisplayName = GetValue(values, "program.displayname");
            program.InstallPath = GetValue(values, "program.installpath");
            program.ExecutablePath = GetValue(values, "program.executablepath");
            program.Publisher = GetValue(values, "program.publisher");
            program.Version = GetValue(values, "program.version");
            program.SizeBytes = ParseLong(GetValue(values, "program.sizebytes"), -1);
            program.Source = (ProgramSource)ParseInt(GetValue(values, "program.source"), 0);
            program.UninstallString = GetValue(values, "program.uninstallstring");
            program.ShortcutPath = GetValue(values, "program.shortcutpath");
            program.DriveRoot = GetValue(values, "program.driveroot");

            if (string.IsNullOrEmpty(program.InstallPath))
            {
                Log.Warn("挂起任务文件缺少安装路径，视为损坏：" + path);
                return null;
            }

            MoveRequest request = new MoveRequest();
            request.Program = program;
            request.TargetDirectory = GetValue(values, "request.targetdirectory");
            request.CreateJunction = ParseBool(GetValue(values, "request.createjunction"), true);
            request.KillBlockingProcesses = ParseBool(GetValue(values, "request.killblockingprocesses"), false);
            request.StopRelatedServices = ParseBool(GetValue(values, "request.stoprelatedservices"), true);
            request.UpdateShortcuts = ParseBool(GetValue(values, "request.updateshortcuts"), true);
            request.UpdateRegistry = ParseBool(GetValue(values, "request.updateregistry"), true);
            request.CreateRestorePoint = ParseBool(GetValue(values, "request.createrestorepoint"), false);
            if (string.IsNullOrEmpty(request.TargetDirectory))
            {
                Log.Warn("挂起任务文件缺少目标目录，视为损坏：" + path);
                return null;
            }
            return request;
        }

        #endregion

        #region 小工具

        private static string GetValue(Dictionary<string, string> values, string key)
        {
            string value;
            if (values.TryGetValue(key, out value))
            {
                return value;
            }
            return string.Empty;
        }

        private static long ParseLong(string text, long fallback)
        {
            long value;
            if (long.TryParse(text, out value))
            {
                return value;
            }
            return fallback;
        }

        private static int ParseInt(string text, int fallback)
        {
            int value;
            if (int.TryParse(text, out value))
            {
                return value;
            }
            return fallback;
        }

        private static bool ParseBool(string text, bool fallback)
        {
            if (text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (text == "0" || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return fallback;
        }

        private static bool ContainsPath(List<string> list, string path)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Encode(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        private static string Decode(string base64)
        {
            if (string.IsNullOrEmpty(base64))
            {
                return string.Empty;
            }
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        #endregion
    }
}
