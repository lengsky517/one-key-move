// ============================================================================
//  一键挪动 (OneKeyMove) - 程序入口（Lead 维护）
// ============================================================================
using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using YijianNuodong.Core;
using YijianNuodong.UI;

namespace YijianNuodong
{
    internal static class Program
    {
        private const string MutexName = "Local\\YijianNuodong_SingleInstance_v1";
        private static readonly string VersionText = GetVersionText();

        /// <summary>取程序集版本作为启动日志版本号，避免写死导致日志与真实版本不一致</summary>
        private static string GetVersionText()
        {
            try
            {
                Version version = typeof(Program).Assembly.GetName().Version;
                if (version != null)
                {
                    return version.Major + "." + version.Minor + "." + version.Build;
                }
            }
            catch (Exception)
            {
                // 取不到时退回固定值
            }
            return "1.0.0";
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                AppPaths.EnsureCreated();
                Log.Info("===== 一键挪动 v" + VersionText + " 启动 =====");
                Log.Info("命令行: " + string.Join(" ", args));
                AppSettings.Load();

                // 重启后完成模式：由计划任务在开机时调用，无界面
                string pendingJob = GetOptionValue(args, "--complete-pending");
                if (!string.IsNullOrEmpty(pendingJob))
                {
                    return RunPendingCompletion(pendingJob);
                }

                // 普通界面模式
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += new ThreadExceptionEventHandler(OnThreadException);
                AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(OnDomainException);

                bool createdNew = false;
                using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
                {
                    if (!createdNew)
                    {
                        MessageBox.Show("一键挪动已经在运行中，请在任务栏查找它的窗口。",
                            "一键挪动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 0;
                    }

                    if (!ElevationUtil.IsElevated)
                    {
                        // 清单已要求管理员权限；若被绕过，这里再尝试提升一次
                        Log.Warn("当前不是管理员权限，尝试重新以管理员身份启动");
                        string argLine = BuildArgumentLine(args);
                        if (ElevationUtil.RelaunchElevated(argLine))
                        {
                            return 0;
                        }
                        Log.Warn("提升权限失败，继续以普通权限运行（部分操作会失败）");
                    }

                    string initialPath = null;
                    for (int i = 0; i < args.Length; i++)
                    {
                        string a = args[i];
                        if (a == null) continue;
                        if (a.StartsWith("-", StringComparison.Ordinal)) continue;
                        initialPath = a;
                        break;
                    }

                    MainForm form = new MainForm(initialPath);
                    Application.Run(form);
                    Log.Info("===== 一键挪动退出 =====");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Log.Error("启动失败", ex);
                try
                {
                    MessageBox.Show("一键挪动启动失败：" + ex.Message + Environment.NewLine +
                        "日志位置：" + Log.LogFilePath,
                        "一键挪动", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                return 1;
            }
        }

        /// <summary>重启后完成移动（无界面，返回进程退出码）</summary>
        private static int RunPendingCompletion(string jobFile)
        {
            try
            {
                Log.Info("进入重启后完成模式，任务文件: " + jobFile);
                MoveResult result = RebootMover.CompletePending(jobFile, null, null);
                if (result != null && result.Success)
                {
                    Log.Info("重启后移动完成: " + jobFile);
                    return 0;
                }
                Log.Error("重启后移动失败: " + (result == null ? "无结果" : result.ErrorMessage));
                return 2;
            }
            catch (Exception ex)
            {
                Log.Error("重启后移动异常", ex);
                return 3;
            }
        }

        private static string GetOptionValue(string[] args, string option)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        private static string BuildArgumentLine(string[] args)
        {
            if (args == null || args.Length == 0) return string.Empty;
            string[] quoted = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? string.Empty;
                if (a.IndexOf(' ') >= 0) quoted[i] = "\"" + a + "\"";
                else quoted[i] = a;
            }
            return string.Join(" ", quoted);
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Log.Error("界面线程未处理异常", e.Exception);
            try
            {
                MessageBox.Show("出现了一个错误：" + e.Exception.Message + Environment.NewLine +
                    "详细信息已写入日志：" + Log.LogFilePath,
                    "一键挪动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            Log.Error("程序域未处理异常", ex);
        }
    }
}
