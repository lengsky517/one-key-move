// ============================================================================
//  一键挪动 (OneKeyMove) - 公共工具（Lead 维护）
//  路径规范化 / 字节格式化 / 目录大小 / 图标提取 / 权限判断
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace YijianNuodong.Core
{
    /// <summary>路径处理工具</summary>
    public static class PathUtil
    {
        private static readonly char[] InvalidNameChars = new char[]
        {
            '\\', '/', ':', '*', '?', '"', '<', '>', '|'
        };

        /// <summary>转成绝对路径并去掉末尾分隔符（根目录保留 "C:\" 形式）</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string p = path.Trim().Trim('"');
            if (p.Length == 0) return string.Empty;

            try
            {
                p = Path.GetFullPath(p);
            }
            catch
            {
                // 路径非法时原样返回去掉尾部斜杠的结果
                return p.TrimEnd('\\', '/');
            }

            string root = Path.GetPathRoot(p) ?? string.Empty;
            while (p.Length > root.Length && (p[p.Length - 1] == '\\' || p[p.Length - 1] == '/'))
            {
                p = p.Substring(0, p.Length - 1);
            }
            return p;
        }

        /// <summary>两者是否指向同一路径</summary>
        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
                                     Path.GetFullPath(b).TrimEnd('\\'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>child 是否位于 parent 内部（含相等时返回 false）</summary>
        public static bool IsSubPathOf(string child, string parent)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
            string c = Normalize(child);
            string p = Normalize(parent);
            if (c.Length == 0 || p.Length == 0) return false;
            if (p[p.Length - 1] != '\\') p += "\\";
            if (c.Length <= p.Length) return false;
            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>获取盘符根 "C:\"</summary>
        public static string GetDriveRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                string root = Path.GetPathRoot(Normalize(path));
                return root == null ? string.Empty : root;
            }
            catch
            {
                return string.Empty;
            }
        }

        // 长路径前缀阈值：.NET Framework 对接近 MAX_PATH(260) 的普通路径会报 PathTooLongException，
        // 所以只对超过该长度的路径加 \\?\ 前缀；普通路径保持原样。
        // 关键：程序清单里不能声明 longPathAware——一旦声明，.NET Framework 会把 \\?\ 前缀
        // 当成非法字符（ArgumentException: 路径中具有非法字符），导致目录枚举全部失败
        // （真机现象：选择器右侧列表空白、搬家时报“安装目录是空的”）。已移除该清单项。
        private const int LongPathThreshold = 240;

        /// <summary>按需添加扩展路径前缀以支持超长路径（普通路径不加前缀）</summary>
        public static string ToLongPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.Length < LongPathThreshold) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        /// <summary>目标文件夹名：优先沿用原安装目录的文件夹名，非法时用软件名清洗后替代</summary>
        public static string GetLeafName(string installPath, string displayName)
        {
            string leaf = string.Empty;
            try
            {
                leaf = Path.GetFileName(Normalize(installPath));
            }
            catch
            {
                leaf = string.Empty;
            }
            if (!string.IsNullOrEmpty(leaf) && leaf != "." && IsValidFolderName(leaf)) return leaf;

            leaf = SanitizeFolderName(displayName);
            if (string.IsNullOrEmpty(leaf)) leaf = "MovedProgram";
            return leaf;
        }

        /// <summary>清洗成合法文件夹名</summary>
        public static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                bool bad = false;
                for (int j = 0; j < InvalidNameChars.Length; j++)
                {
                    if (InvalidNameChars[j] == ch) { bad = true; break; }
                }
                if (bad || ch < 32) sb.Append('_');
                else sb.Append(ch);
            }
            string s = sb.ToString().Trim().TrimEnd('.');
            // Windows 保留名
            string upper = s.ToUpperInvariant();
            string[] reserved = new string[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4",
                                               "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2",
                                               "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            for (int i = 0; i < reserved.Length; i++)
            {
                if (upper == reserved[i]) return s + "_";
            }
            const int maxLen = 80;
            if (s.Length > maxLen) s = s.Substring(0, maxLen).Trim();
            return s;
        }

        /// <summary>是否为合法文件夹名</summary>
        public static bool IsValidFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name == "." || name == "..") return false;
            if (name != name.Trim()) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (ch < 32) return false;
                for (int j = 0; j < InvalidNameChars.Length; j++)
                {
                    if (InvalidNameChars[j] == ch) return false;
                }
            }
            if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)) return false;
            return name.Length <= 120;
        }

        /// <summary>是否为 Windows / 系统关键目录（禁止作为搬家源）</summary>
        public static bool IsSystemCriticalPath(string path)
        {
            string p = Normalize(path).ToLowerInvariant();
            if (p.Length == 0) return true;

            string[] critical = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)
            };

            for (int i = 0; i < critical.Length; i++)
            {
                string c = Normalize(critical[i]).ToLowerInvariant();
                if (c.Length == 0) continue;
                if (p == c || p == c + @"\") return true;
            }

            // Windows 目录下任何子目录都不允许
            string win = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).ToLowerInvariant();
            if (win.Length > 0 && (p == win || p.StartsWith(win + @"\", StringComparison.Ordinal))) return true;
            // Program Files 根目录本身
            string pf = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).ToLowerInvariant();
            string pf86 = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)).ToLowerInvariant();
            if (p == pf || p == pf86) return true;
            // 盘符根目录
            if (p == @"c:\" || p.Length <= 3) return true;

            return false;
        }

        /// <summary>路径是否为重解析点（目录联接 / 符号链接）</summary>
        public static bool IsJunctionOrLink(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return false;
                FileAttributes attr = File.GetAttributes(path);
                return (attr & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>拼接目标目录（父目录 + 子目录名）</summary>
        public static string CombineTarget(string parentDirectory, string leafName)
        {
            string parent = Normalize(parentDirectory);
            if (string.IsNullOrEmpty(parent)) return string.Empty;
            string leaf = SanitizeFolderName(leafName);
            if (string.IsNullOrEmpty(leaf)) return parent;
            return Normalize(Path.Combine(parent, leaf));
        }

        /// <summary>尽量缩短显示路径</summary>
        public static string Shorten(string path, int maxChars)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            if (maxChars < 8 || path.Length <= maxChars) return path;
            int keep = maxChars - 3;
            int head = keep / 3;
            int tail = keep - head;
            return path.Substring(0, head) + "..." + path.Substring(path.Length - tail);
        }
    }

    /// <summary>文本格式化</summary>
    public static class TextUtil
    {
        private static readonly string[] Units = new string[] { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>18.6 GB 这种人类可读格式</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "未知";
            if (bytes < 1024) return bytes.ToString() + " B";
            double v = bytes;
            int unit = 0;
            while (v >= 1024 && unit < Units.Length - 1)
            {
                v /= 1024;
                unit++;
            }
            if (v >= 100) return v.ToString("0") + " " + Units[unit];
            if (v >= 10) return v.ToString("0.0") + " " + Units[unit];
            return v.ToString("0.00") + " " + Units[unit];
        }

        /// <summary>去掉首尾空白、全角空格</summary>
        public static string Clean(string text)
        {
            if (text == null) return string.Empty;
            return text.Replace('\u3000', ' ').Trim();
        }

        public static string JoinNonEmpty(string separator, params string[] parts)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(separator);
                sb.Append(p);
            }
            return sb.ToString();
        }

        /// <summary>是否为中文等宽字符，用于简单对齐/截断</summary>
        public static bool IsWideChar(char ch)
        {
            return ch >= 0x1100 && (ch <= 0x115F || ch == 0x2329 || ch == 0x232A ||
                   (ch >= 0x2E80 && ch <= 0xA4CF) || (ch >= 0xAC00 && ch <= 0xD7A3) ||
                   (ch >= 0xF900 && ch <= 0xFAFF) || (ch >= 0xFE30 && ch <= 0xFE6F) ||
                   (ch >= 0xFF00 && ch <= 0xFF60) || (ch >= 0xFFE0 && ch <= 0xFFE6));
        }
    }

    /// <summary>文件系统辅助</summary>
    public static class FileUtil
    {
        /// <summary>统计目录大小（跳过重解析点），支持取消与进度回调</summary>
        public static long GetDirectorySize(string directory, Func<bool> isCancelled)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return -1;
            long total = 0;
            try
            {
                Stack<string> stack = new Stack<string>();
                stack.Push(PathUtil.ToLongPath(directory));
                while (stack.Count > 0)
                {
                    if (isCancelled != null && isCancelled()) return -1;
                    string dir = stack.Pop();
                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(dir);
                    }
                    catch
                    {
                        files = new string[0];
                    }
                    for (int i = 0; i < files.Length; i++)
                    {
                        try
                        {
                            FileInfo fi = new FileInfo(files[i]);
                            if (fi.Exists) total += fi.Length;
                        }
                        catch { }
                    }
                    string[] subs;
                    try
                    {
                        subs = Directory.GetDirectories(dir);
                    }
                    catch
                    {
                        subs = new string[0];
                    }
                    for (int i = 0; i < subs.Length; i++)
                    {
                        try
                        {
                            FileAttributes attr = File.GetAttributes(subs[i]);
                            if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch { continue; }
                        stack.Push(subs[i]);
                    }
                }
            }
            catch
            {
                return total > 0 ? total : -1;
            }
            return total;
        }

        /// <summary>目录是否为空</summary>
        public static bool IsDirectoryEmpty(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return true;
            try
            {
                string[] entries = Directory.GetFileSystemEntries(PathUtil.ToLongPath(directory));
                return entries.Length == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>统计文件数（跳过重解析点）</summary>
        public static int CountFiles(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;
            int count = 0;
            Stack<string> stack = new Stack<string>();
            stack.Push(directory);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try { count += Directory.GetFiles(dir).Length; } catch { }
                try
                {
                    string[] subs = Directory.GetDirectories(dir);
                    for (int i = 0; i < subs.Length; i++)
                    {
                        try
                        {
                            FileAttributes attr = File.GetAttributes(subs[i]);
                            if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch { continue; }
                        stack.Push(subs[i]);
                    }
                }
                catch { }
            }
            return count;
        }

        /// <summary>尝试删除目录（清除只读属性，处理长路径），失败返回错误信息</summary>
        public static bool TryDeleteDirectory(string directory, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(directory)) { error = "目录为空"; return false; }
            if (!Directory.Exists(directory)) return true;
            try
            {
                string lp = PathUtil.ToLongPath(directory);
                ClearReadOnlyRecursive(lp);
                Directory.Delete(lp, true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>递归清除只读属性</summary>
        public static void ClearReadOnlyRecursive(string directory)
        {
            try
            {
                string[] files = Directory.GetFiles(directory);
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        FileAttributes attr = File.GetAttributes(files[i]);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(files[i], attr & ~FileAttributes.ReadOnly);
                    }
                    catch { }
                }
                string[] subs = Directory.GetDirectories(directory);
                for (int i = 0; i < subs.Length; i++)
                {
                    ClearReadOnlyRecursive(subs[i]);
                }
            }
            catch { }
        }

        /// <summary>当前账号能否在目录内创建文件（用于目标目录校验）</summary>
        public static bool HasWriteAccess(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return false;
            string probe = Path.Combine(directory, ".okm_write_test_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream fs = File.Create(probe)) { fs.WriteByte(0); }
                File.Delete(probe);
                return true;
            }
            catch
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch { }
                return false;
            }
        }

        /// <summary>确保目录存在</summary>
        public static bool EnsureDirectory(string directory, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(directory)) { error = "目录为空"; return false; }
            try
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>应用数据目录</summary>
    public static class AppPaths
    {
        private static string _baseDir;
        private static string _logDir;
        private static string _journalDir;
        private static string _settingsFile;

        public static string BaseDir
        {
            get
            {
                if (_baseDir == null)
                {
                    string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    if (string.IsNullOrEmpty(root))
                        root = Path.GetDirectoryName(typeof(AppPaths).Assembly.Location);
                    _baseDir = Path.Combine(root, "YijianNuodong");
                }
                return _baseDir;
            }
        }

        public static string LogDir
        {
            get
            {
                if (_logDir == null) _logDir = Path.Combine(BaseDir, "logs");
                return _logDir;
            }
        }

        public static string JournalDir
        {
            get
            {
                if (_journalDir == null) _journalDir = Path.Combine(BaseDir, "journal");
                return _journalDir;
            }
        }

        public static string SettingsFile
        {
            get
            {
                if (_settingsFile == null) _settingsFile = Path.Combine(BaseDir, "settings.txt");
                return _settingsFile;
            }
        }

        /// <summary>确保目录存在，返回是否成功（无权限时不抛出）</summary>
        public static bool EnsureCreated()
        {
            try
            {
                if (!Directory.Exists(BaseDir)) Directory.CreateDirectory(BaseDir);
                if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
                if (!Directory.Exists(JournalDir)) Directory.CreateDirectory(JournalDir);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>图标提取（Shell 原生，支持 exe / 文件夹 / 快捷方式）</summary>
    public static class IconUtil
    {
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>提取路径对应图标，失败返回 null</summary>
        public static Icon GetIconFor(string path, bool small)
        {
            if (string.IsNullOrEmpty(path)) return null;
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                SHFILEINFO info = new SHFILEINFO();
                uint flags = SHGFI_ICON | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON);
                bool isDir = false;
                try { isDir = Directory.Exists(path); } catch { }

                if (isDir)
                {
                    flags |= SHGFI_USEFILEATTRIBUTES;
                    SHGetFileInfo(path, FILE_ATTRIBUTE_DIRECTORY, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                }
                else
                {
                    if (File.Exists(path))
                        SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                }

                hIcon = info.hIcon;
                if (hIcon == IntPtr.Zero) return null;
                Icon result = (Icon)Icon.FromHandle(hIcon).Clone();
                return result;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            }
        }

        /// <summary>为程序提取图标：优先主程序 exe，其次安装目录</summary>
        public static Icon GetProgramIcon(InstalledProgram program, bool small)
        {
            if (program == null) return null;
            if (!string.IsNullOrEmpty(program.ExecutablePath))
            {
                Icon ic = GetIconFor(program.ExecutablePath, small);
                if (ic != null) return ic;
            }
            if (!string.IsNullOrEmpty(program.InstallPath))
            {
                Icon ic = GetIconFor(program.InstallPath, small);
                if (ic != null) return ic;
            }
            return null;
        }
    }

    /// <summary>管理员权限判断与提升</summary>
    public static class ElevationUtil
    {
        /// <summary>当前进程是否以管理员身份运行</summary>
        public static bool IsElevated
        {
            get
            {
                try
                {
                    WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>以管理员身份重新启动自身，成功返回 true（调用方应随后退出）</summary>
        public static bool RelaunchElevated(string arguments)
        {
            try
            {
                string exe = typeof(ElevationUtil).Assembly.Location;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = arguments ?? string.Empty;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
