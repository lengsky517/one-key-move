// ============================================================================
//  一键挪动 (OneKeyMove) - 服务控制与迁移
//  职责：按安装目录枚举服务、停止/启动服务、读写服务 ImagePath（保留引号与参数）。
//  说明：服务注册表位于 HKLM，写操作需要管理员权限；失败一律转成中文提示。
//  目标框架：.NET Framework 4.8 / C# 5
// ============================================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ServiceProcess;

namespace YijianNuodong.Core
{
    /// <summary>Windows 服务辅助方法</summary>
    public static class ServiceHelper
    {
        private const string ServicesRegistryPath = @"SYSTEM\CurrentControlSet\Services";
        private const int DefaultStopTimeoutMs = 15000;
        private const int DefaultStartTimeoutMs = 20000;

        /// <summary>枚举 ImagePath 位于指定目录下的服务</summary>
        public static List<LockingService> EnumerateServicesUnder(string directoryPath)
        {
            List<LockingService> result = new List<LockingService>();
            if (string.IsNullOrEmpty(directoryPath))
            {
                return result;
            }
            string directory = PathUtil.Normalize(directoryPath);
            if (directory.Length == 0)
            {
                return result;
            }

            using (RegistryKeyHolder holder = OpenServicesRoot(false))
            {
                if (!holder.IsOpened)
                {
                    Log.Warn("无法读取服务注册表（可能权限不足）：" + holder.Error);
                    return result;
                }

                string[] names;
                try
                {
                    names = holder.Key.GetSubKeyNames();
                }
                catch (Exception ex)
                {
                    Log.Warn("枚举服务注册表子键失败：" + ex.Message);
                    return result;
                }

                for (int i = 0; i < names.Length; i++)
                {
                    string serviceName = names[i];
                    string rawImagePath = ReadImagePathFromRoot(holder.Key, serviceName);
                    if (string.IsNullOrEmpty(rawImagePath))
                    {
                        continue;
                    }
                    string executable = ExtractExecutablePath(rawImagePath);
                    if (string.IsNullOrEmpty(executable))
                    {
                        continue;
                    }
                    string expanded = Environment.ExpandEnvironmentVariables(executable);
                    string normalized = PathUtil.Normalize(expanded);
                    if (normalized.Length == 0)
                    {
                        continue;
                    }
                    if (!PathUtil.SamePath(normalized, directory) && !PathUtil.IsSubPathOf(normalized, directory))
                    {
                        continue;
                    }

                    LockingService service = new LockingService();
                    service.ServiceName = serviceName;
                    service.ImagePath = expanded;
                    FillServiceState(service);
                    result.Add(service);
                }
            }

            if (result.Count > 0)
            {
                Log.Info("发现 " + result.Count + " 个可执行文件位于目录内的服务：" + directory);
            }
            return result;
        }

        /// <summary>停止服务并等待进入停止状态</summary>
        public static bool TryStop(string serviceName, int timeoutMs, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(serviceName))
            {
                error = "服务名为空，无法停止。";
                return false;
            }
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultStopTimeoutMs;
            try
            {
                using (ServiceController controller = new ServiceController(serviceName))
                {
                    if (controller.Status == ServiceControllerStatus.Stopped)
                    {
                        return true;
                    }
                    if (!controller.CanStop)
                    {
                        error = "服务「" + serviceName + "」不允许停止（可能是驱动服务或已被策略禁止）。";
                        return false;
                    }
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(timeout));
                    Log.Info("已停止服务：" + serviceName);
                    return true;
                }
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                error = "停止服务「" + serviceName + "」超时（" + timeout + " 毫秒），服务可能仍在运行。";
                return false;
            }
            catch (InvalidOperationException ex)
            {
                error = "停止服务「" + serviceName + "」失败：" + ex.Message;
                return false;
            }
            catch (Win32Exception ex)
            {
                error = "停止服务「" + serviceName + "」失败：权限不足（" + ex.Message + "）。";
                return false;
            }
            catch (Exception ex)
            {
                error = "停止服务「" + serviceName + "」失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>启动服务并等待进入运行状态</summary>
        public static bool TryStart(string serviceName, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(serviceName))
            {
                error = "服务名为空，无法启动。";
                return false;
            }
            try
            {
                using (ServiceController controller = new ServiceController(serviceName))
                {
                    if (controller.Status == ServiceControllerStatus.Running)
                    {
                        return true;
                    }
                    if (controller.Status == ServiceControllerStatus.StartPending)
                    {
                        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(DefaultStartTimeoutMs));
                        return true;
                    }
                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(DefaultStartTimeoutMs));
                    Log.Info("已启动服务：" + serviceName);
                    return true;
                }
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                error = "启动服务「" + serviceName + "」超时（" + DefaultStartTimeoutMs + " 毫秒），服务可能仍未运行。";
                return false;
            }
            catch (InvalidOperationException ex)
            {
                error = "启动服务「" + serviceName + "」失败：" + ex.Message;
                return false;
            }
            catch (Win32Exception ex)
            {
                error = "启动服务「" + serviceName + "」失败：权限不足（" + ex.Message + "）。";
                return false;
            }
            catch (Exception ex)
            {
                error = "启动服务「" + serviceName + "」失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>读取服务 ImagePath（已展开环境变量），读不到返回空字符串</summary>
        public static string GetImagePath(string serviceName)
        {
            string raw = GetRawImagePath(serviceName);
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }
            return Environment.ExpandEnvironmentVariables(raw);
        }

        /// <summary>
        /// 改写服务 ImagePath：保留原有引号形式与参数后缀，只替换可执行文件路径部分。
        /// 新路径含 % 时使用 REG_EXPAND_SZ，否则使用 REG_SZ。
        /// </summary>
        public static bool SetImagePath(string serviceName, string newImagePath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(serviceName))
            {
                error = "服务名为空，无法修改启动路径。";
                return false;
            }
            if (string.IsNullOrEmpty(newImagePath))
            {
                error = "新的服务启动路径为空，无法修改。";
                return false;
            }

            string oldRaw = GetRawImagePath(serviceName);
            if (string.IsNullOrEmpty(oldRaw))
            {
                error = "服务「" + serviceName + "」没有 ImagePath，无法修改。";
                return false;
            }

            string suffix = string.Empty;
            bool wasQuoted = false;
            string trimmed = oldRaw.TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '"')
            {
                wasQuoted = true;
            }
            int pathEnd = FindPathEnd(oldRaw);
            if (pathEnd >= 0 && pathEnd < oldRaw.Length)
            {
                suffix = oldRaw.Substring(pathEnd);
            }

            bool needQuote = wasQuoted || newImagePath.IndexOf(' ') >= 0 || newImagePath.IndexOf('\t') >= 0;
            string combined = needQuote ? "\"" + newImagePath + "\"" : newImagePath;
            combined += suffix;

            RegistryValueKindForWrite valueKind;
            if (combined.IndexOf('%') >= 0)
            {
                valueKind = RegistryValueKindForWrite.ExpandString;
            }
            else
            {
                valueKind = RegistryValueKindForWrite.String;
            }

            using (RegistryKeyHolder holder = OpenServicesRoot(true))
            {
                if (!holder.IsOpened)
                {
                    error = "无法写入服务注册表（需要管理员权限）：" + holder.Error;
                    return false;
                }
                try
                {
                    using (Microsoft.Win32.RegistryKey key = holder.Key.OpenSubKey(serviceName, true))
                    {
                        if (key == null)
                        {
                            error = "服务「" + serviceName + "」不存在或没有修改权限。";
                            return false;
                        }
                        Microsoft.Win32.RegistryValueKind kind = valueKind == RegistryValueKindForWrite.ExpandString
                            ? Microsoft.Win32.RegistryValueKind.ExpandString
                            : Microsoft.Win32.RegistryValueKind.String;
                        key.SetValue("ImagePath", combined, kind);
                        Log.Info("已更新服务启动路径：" + serviceName + " -> " + combined);
                        return true;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    error = "修改服务「" + serviceName + "」的启动路径需要管理员权限：" + ex.Message;
                    return false;
                }
                catch (System.Security.SecurityException ex)
                {
                    error = "修改服务「" + serviceName + "」的启动路径需要管理员权限：" + ex.Message;
                    return false;
                }
                catch (Exception ex)
                {
                    error = "修改服务「" + serviceName + "」的启动路径失败：" + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>服务是否可被当前账号控制（可以停止）</summary>
        public static bool IsControllable(string serviceName)
        {
            if (string.IsNullOrEmpty(serviceName))
            {
                return false;
            }
            try
            {
                using (ServiceController controller = new ServiceController(serviceName))
                {
                    return controller.CanStop;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>读取服务原始 ImagePath（不展开环境变量），读不到返回空字符串</summary>
        internal static string GetRawImagePath(string serviceName)
        {
            if (string.IsNullOrEmpty(serviceName))
            {
                return string.Empty;
            }
            using (RegistryKeyHolder holder = OpenServicesRoot(false))
            {
                if (!holder.IsOpened)
                {
                    return string.Empty;
                }
                return ReadImagePathFromRoot(holder.Key, serviceName);
            }
        }

        /// <summary>服务可执行文件完整路径（已展开环境变量），读不到返回空字符串</summary>
        internal static string GetExecutablePath(string serviceName)
        {
            string raw = GetRawImagePath(serviceName);
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }
            string executable = ExtractExecutablePath(raw);
            if (string.IsNullOrEmpty(executable))
            {
                return string.Empty;
            }
            return Environment.ExpandEnvironmentVariables(executable);
        }

        /// <summary>
        /// 从服务的命令字符串中提取可执行文件路径。
        /// 支持带引号（含空格路径）、不带引号但带参数、以及无参数的写法。
        /// </summary>
        internal static string ExtractExecutablePath(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
            {
                return string.Empty;
            }
            string text = imagePath.Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            if (text[0] == '"')
            {
                int end = text.IndexOf('"', 1);
                if (end <= 1)
                {
                    return string.Empty;
                }
                return text.Substring(1, end - 1).Trim();
            }

            // 未加引号：优先截到 .exe（含扩展名），否则按第一个空格切分
            int exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
            {
                return text.Substring(0, exeIndex + 4).Trim();
            }
            int spaceIndex = text.IndexOf(' ');
            if (spaceIndex > 0)
            {
                return text.Substring(0, spaceIndex).Trim();
            }
            return text;
        }

        /// <summary>在原始 ImagePath 中定位可执行文件路径结束位置（返回“路径后第一个字符”的下标）</summary>
        internal static int FindPathEnd(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
            {
                return -1;
            }
            string text = imagePath;
            int start = 0;
            while (start < text.Length && (text[start] == ' ' || text[start] == '\t'))
            {
                start++;
            }
            if (start >= text.Length)
            {
                return -1;
            }
            if (text[start] == '"')
            {
                int end = text.IndexOf('"', start + 1);
                if (end < 0)
                {
                    return text.Length;
                }
                return end + 1;
            }
            int exeIndex = text.IndexOf(".exe", start, StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
            {
                return exeIndex + 4;
            }
            int spaceIndex = text.IndexOf(' ', start);
            if (spaceIndex > 0)
            {
                return spaceIndex;
            }
            return text.Length;
        }

        /// <summary>读某个服务键下的 ImagePath 值（不展开），失败返回空字符串</summary>
        private static string ReadImagePathFromRoot(Microsoft.Win32.RegistryKey servicesRoot, string serviceName)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = servicesRoot.OpenSubKey(serviceName, false))
                {
                    if (key == null)
                    {
                        return string.Empty;
                    }
                    object value = key.GetValue("ImagePath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null)
                    {
                        return string.Empty;
                    }
                    string text = value as string;
                    return text == null ? string.Empty : text;
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>补全服务的显示名/状态/是否可停止（取不到时标记为 unknown）</summary>
        private static void FillServiceState(LockingService service)
        {
            if (service == null)
            {
                return;
            }
            try
            {
                using (ServiceController controller = new ServiceController(service.ServiceName))
                {
                    try
                    {
                        service.DisplayName = controller.DisplayName;
                    }
                    catch (Exception)
                    {
                        service.DisplayName = service.ServiceName;
                    }
                    try
                    {
                        service.State = StateToText(controller.Status);
                    }
                    catch (Exception)
                    {
                        service.State = "未知";
                    }
                    try
                    {
                        service.CanStop = controller.CanStop;
                    }
                    catch (Exception)
                    {
                        service.CanStop = false;
                    }
                }
            }
            catch (Exception)
            {
                if (string.IsNullOrEmpty(service.DisplayName))
                {
                    service.DisplayName = service.ServiceName;
                }
                service.State = "未知";
                service.CanStop = false;
            }
        }

        /// <summary>服务状态转中文</summary>
        private static string StateToText(ServiceControllerStatus status)
        {
            switch (status)
            {
                case ServiceControllerStatus.Running:
                    return "正在运行";
                case ServiceControllerStatus.Stopped:
                    return "已停止";
                case ServiceControllerStatus.Paused:
                    return "已暂停";
                case ServiceControllerStatus.StartPending:
                    return "正在启动";
                case ServiceControllerStatus.StopPending:
                    return "正在停止";
                case ServiceControllerStatus.PausePending:
                    return "正在暂停";
                case ServiceControllerStatus.ContinuePending:
                    return "正在恢复";
                default:
                    return "未知";
            }
        }

        /// <summary>打开服务注册表根键（HKLM\SYSTEM\CurrentControlSet\Services）</summary>
        private static RegistryKeyHolder OpenServicesRoot(bool writable)
        {
            RegistryKeyHolder holder = new RegistryKeyHolder();
            try
            {
                Microsoft.Win32.RegistryView view = Environment.Is64BitOperatingSystem
                    ? Microsoft.Win32.RegistryView.Registry64
                    : Microsoft.Win32.RegistryView.Registry32;
                Microsoft.Win32.RegistryKey baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, view);
                if (baseKey == null)
                {
                    holder.Error = "无法打开 HKLM 注册表根键";
                    return holder;
                }
                holder.OwnerBaseKey = baseKey;
                Microsoft.Win32.RegistryKey services = baseKey.OpenSubKey(ServicesRegistryPath, writable);
                if (services == null)
                {
                    holder.Error = "无法打开服务注册表键（可能权限不足）";
                    return holder;
                }
                holder.Key = services;
                return holder;
            }
            catch (UnauthorizedAccessException ex)
            {
                holder.Error = ex.Message;
                return holder;
            }
            catch (System.Security.SecurityException ex)
            {
                holder.Error = ex.Message;
                return holder;
            }
            catch (Exception ex)
            {
                holder.Error = ex.Message;
                return holder;
            }
        }

        /// <summary>写入时使用的值类型（避免界面层直接依赖注册表枚举）</summary>
        private enum RegistryValueKindForWrite
        {
            String = 0,
            ExpandString = 1
        }

        /// <summary>注册表键持有器：负责释放子键与根键</summary>
        private sealed class RegistryKeyHolder : IDisposable
        {
            public Microsoft.Win32.RegistryKey Key;
            public Microsoft.Win32.RegistryKey OwnerBaseKey;
            public string Error = string.Empty;

            public bool IsOpened
            {
                get { return Key != null; }
            }

            public void Dispose()
            {
                if (Key != null)
                {
                    try
                    {
                        Key.Close();
                    }
                    catch
                    {
                    }
                    Key = null;
                }
                if (OwnerBaseKey != null)
                {
                    try
                    {
                        OwnerBaseKey.Close();
                    }
                    catch
                    {
                    }
                    OwnerBaseKey = null;
                }
            }
        }
    }
}
