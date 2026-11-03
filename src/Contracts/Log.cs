// ============================================================================
//  一键挪动 (OneKeyMove) - 日志（Lead 维护）
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YijianNuodong.Core
{
    /// <summary>简单的线程安全日志（文件 + 内存最近记录）</summary>
    public static class Log
    {
        private static readonly object SyncRoot = new object();
        private static readonly List<string> RecentLines = new List<string>();
        private const int MaxRecentLines = 2000;
        private static bool _fileDisabled;

        public static string LogFilePath
        {
            get
            {
                return Path.Combine(AppPaths.LogDir, DateTime.Now.ToString("yyyyMMdd") + ".log");
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public static void Error(string message)
        {
            Write("ERROR", message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        private static void Write(string level, string message, Exception ex)
        {
            string line = string.Format("[{0}] [{1}] {2}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                level,
                message ?? string.Empty);
            if (ex != null)
            {
                line += Environment.NewLine + ex.ToString();
            }

            lock (SyncRoot)
            {
                RecentLines.Add(line);
                if (RecentLines.Count > MaxRecentLines)
                {
                    RecentLines.RemoveRange(0, RecentLines.Count - MaxRecentLines);
                }

                if (_fileDisabled) return;
                try
                {
                    AppPaths.EnsureCreated();
                    File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    _fileDisabled = true;
                }
            }
        }

        public static string[] GetRecentLines()
        {
            lock (SyncRoot)
            {
                return RecentLines.ToArray();
            }
        }

        public static string GetRecentText(int maxLines)
        {
            string[] lines = GetRecentLines();
            int start = 0;
            if (maxLines > 0 && lines.Length > maxLines) start = lines.Length - maxLines;
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < lines.Length; i++)
            {
                sb.AppendLine(lines[i]);
            }
            return sb.ToString();
        }

        public static void ClearRecent()
        {
            lock (SyncRoot)
            {
                RecentLines.Clear();
            }
        }
    }
}
