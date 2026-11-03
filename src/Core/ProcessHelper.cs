// ============================================================================
//  一键挪动 (OneKeyMove) - 进程辅助
//  职责：向占用进程的窗口发送关闭消息、强制结束进程（含系统关键进程保护）、
//        查询进程路径与运行状态。所有异常都转成中文提示，不向界面抛出。
//  目标框架：.NET Framework 4.8 / C# 5
// ============================================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YijianNuodong.Core
{
    /// <summary>进程辅助方法（优雅关闭窗口 / 强制结束 / 等待退出 / 查询状态）</summary>
    public static class ProcessHelper
    {
        private const int WM_CLOSE = 0x0010;
        private const int KillWaitMilliseconds = 5000;

        /// <summary>
        /// 绝对不能结束的系统关键进程（小写进程名，不含 .exe）。
        /// 结束它们会导致系统蓝屏、注销或桌面崩溃。
        /// </summary>
        private static readonly string[] ProtectedProcessNames = new string[]
        {
            "system", "idle", "registry", "smss", "csrss", "wininit", "services",
            "lsass", "winlogon", "logonui", "userinit", "explorer", "dwm", "audiodg",
            "svchost", "spoolsv", "fontdrvhost", "sihost", "taskhostw", "ctfmon",
            "lsm", "wudfhost"
        };

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>
        /// 优雅关闭：向该进程的所有可见顶层窗口发送 WM_CLOSE，并在 waitMs 内等待进程退出。
        /// 返回 true 表示进程已退出（或本来就不存在）；进程不响应时返回 false 并在 error 中说明。
        /// </summary>
        public static bool TryCloseGracefully(int processId, int waitMs, out string error)
        {
            error = null;
            if (processId <= 0)
            {
                error = "进程 ID 无效，无法关闭。";
                return false;
            }

            Process process = FindProcess(processId);
            if (process == null)
            {
                return true; // 进程已经不存在
            }
            try
            {
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 忽略：后面统一按“可能已退出”处理
            }
            finally
            {
                process.Dispose();
            }

            int windowCount = 0;
            try
            {
                windowCount = PostCloseToAllWindows(processId);
            }
            catch (Exception ex)
            {
                error = "向进程发送关闭消息失败：" + ex.Message;
            }

            if (windowCount == 0)
            {
                Log.Info("进程（PID " + processId + "）没有可见的顶层窗口，无法优雅关闭。");
            }

            if (WaitForExit(processId, waitMs))
            {
                Log.Info("进程（PID " + processId + "）已响应关闭请求并退出。");
                return true;
            }

            if (string.IsNullOrEmpty(error))
            {
                error = "进程在 " + waitMs + " 毫秒内没有响应关闭请求。";
            }
            return false;
        }

        /// <summary>
        /// 强制结束进程。系统关键进程会被直接拒绝（中文提示“系统关键进程，已跳过”）。
        /// </summary>
        public static bool TryKill(int processId, out string error)
        {
            error = null;
            if (processId <= 0)
            {
                error = "进程 ID 无效，无法结束。";
                return false;
            }

            Process process = FindProcess(processId);
            if (process == null)
            {
                return true; // 进程已经不存在
            }

            try
            {
                string name = SafeProcessName(process);
                if (IsProtectedProcess(name))
                {
                    error = "系统关键进程，已跳过：" + DisplayProcessName(name) + "（PID " + processId + "）";
                    Log.Warn(error);
                    return false;
                }
                if (process.HasExited)
                {
                    return true;
                }

                process.Kill();
                if (!process.WaitForExit(KillWaitMilliseconds))
                {
                    error = "结束进程 " + DisplayProcessName(name) + "（PID " + processId + "）超时。";
                    return false;
                }
                Log.Info("已强制结束进程：" + DisplayProcessName(name) + "（PID " + processId + "）");
                return true;
            }
            catch (Win32Exception ex)
            {
                error = "权限不足，无法结束进程（PID " + processId + "）：" + ex.Message;
                return false;
            }
            catch (InvalidOperationException)
            {
                return true; // 进程在结束过程中自行退出了
            }
            catch (Exception ex)
            {
                error = "结束进程（PID " + processId + "）失败：" + ex.Message;
                return false;
            }
            finally
            {
                process.Dispose();
            }
        }

        /// <summary>批量结束进程，汇总失败原因；全部成功时返回 true</summary>
        public static bool TryKillMany(IEnumerable<int> processIds, out List<string> errors)
        {
            errors = new List<string>();
            if (processIds == null)
            {
                return true;
            }

            List<int> handled = new List<int>();
            foreach (int pid in processIds)
            {
                if (pid <= 0 || handled.Contains(pid))
                {
                    continue;
                }
                handled.Add(pid);
                string error;
                if (!TryKill(pid, out error) && !string.IsNullOrEmpty(error))
                {
                    errors.Add(error);
                }
            }
            return errors.Count == 0;
        }

        /// <summary>取进程可执行文件路径；权限不足或取不到时返回 null</summary>
        public static string GetExecutablePath(int processId)
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
                string path = process.MainModule.FileName;
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }
                return path;
            }
            catch (Exception)
            {
                // 权限不足（Win32Exception）、进程已退出（ArgumentException）、
                // 32/64 位不兼容（InvalidOperationException）等都在这里吞掉
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

        /// <summary>等待进程退出；进程已不存在时返回 true</summary>
        public static bool WaitForExit(int processId, int timeoutMs)
        {
            if (processId <= 0)
            {
                return true;
            }
            Process process = null;
            try
            {
                process = Process.GetProcessById(processId);
                if (process == null)
                {
                    return true;
                }
                if (process.HasExited)
                {
                    return true;
                }
                bool exited = process.WaitForExit(timeoutMs < 0 ? 0 : timeoutMs);
                if (exited)
                {
                    return true;
                }
                process.Refresh();
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
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

        /// <summary>进程是否还在运行</summary>
        public static bool IsRunning(int processId)
        {
            if (processId <= 0)
            {
                return false;
            }
            Process process = null;
            try
            {
                process = Process.GetProcessById(processId);
                if (process == null)
                {
                    return false;
                }
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
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

        /// <summary>取进程名（失败时返回空字符串）</summary>
        internal static string SafeProcessName(Process process)
        {
            if (process == null)
            {
                return string.Empty;
            }
            try
            {
                return process.ProcessName ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>是否为受保护的系统关键进程</summary>
        internal static bool IsProtectedProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName))
            {
                return false;
            }
            string name = processName.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }
            name = name.ToLowerInvariant();
            for (int i = 0; i < ProtectedProcessNames.Length; i++)
            {
                if (ProtectedProcessNames[i] == name)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>用于界面展示的进程名（补上 .exe）</summary>
        private static string DisplayProcessName(string processName)
        {
            if (string.IsNullOrEmpty(processName))
            {
                return "未知进程";
            }
            if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return processName;
            }
            return processName + ".exe";
        }

        /// <summary>向该进程所有可见顶层窗口发送 WM_CLOSE，返回找到的窗口数量</summary>
        private static int PostCloseToAllWindows(int processId)
        {
            int count = 0;
            int targetPid = processId;
            EnumWindowsProc callback = delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint windowPid;
                GetWindowThreadProcessId(hWnd, out windowPid);
                if (windowPid == (uint)targetPid)
                {
                    try
                    {
                        if (IsWindowVisible(hWnd))
                        {
                            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            count++;
                        }
                    }
                    catch (Exception)
                    {
                        // 单个窗口失败不影响其他窗口
                    }
                }
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return count;
        }

        /// <summary>按 PID 取进程对象；不存在或无权访问时返回 null</summary>
        private static Process FindProcess(int processId)
        {
            try
            {
                return Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
