// ============================================================================
//  一键挪动 (OneKeyMove) - 目录联接（Junction / SymbolicLink）
//  职责：创建联接（优先 cmd mklink /J，失败回退 CreateSymbolicLink）、
//        读取联接目标（IOCTL FSCTL_GET_REPARSE_POINT）、校验、只删联接本身。
//  安全要求：Remove 只允许删除重解析点，绝不递归删除目标内容。
//  目标框架：.NET Framework 4.8 / C# 5
// ============================================================================
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace YijianNuodong.Core
{
    /// <summary>目录联接（Junction）与目录符号链接的创建、读取、校验、删除</summary>
    public static class Junction
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        /// <summary>FSCTL_GET_REPARSE_POINT = CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9, 42, METHOD_BUFFERED, FILE_ANY_ACCESS)</summary>
        private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;

        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

        private const int MaximumReparseDataBufferSize = 16 * 1024;

        private const uint SYMBOLIC_LINK_FLAG_DIRECTORY = 0x00000001;
        private const uint SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE = 0x00000002;

        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLinkW(string lpSymlinkFileName, string lpTargetFileName, uint dwFlags);

        /// <summary>
        /// 创建目录联接 linkPath -> targetPath。
        /// 优先执行 cmd.exe /c mklink /J；失败时回退到 CreateSymbolicLink（目录型）。
        /// 成功后自动校验联接指向。
        /// </summary>
        public static bool Create(string linkPath, string targetPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(linkPath) || string.IsNullOrEmpty(targetPath))
            {
                error = "创建联接失败：联接路径或目标路径为空。";
                return false;
            }

            string link = PathUtil.Normalize(linkPath);
            string target = PathUtil.Normalize(targetPath);

            if (string.IsNullOrEmpty(link) || string.IsNullOrEmpty(target))
            {
                error = "创建联接失败：联接路径或目标路径无效。";
                return false;
            }
            if (!Directory.Exists(target))
            {
                error = "创建联接失败：目标目录不存在：" + target;
                return false;
            }
            if (Directory.Exists(link) || File.Exists(link) || IsReparsePoint(link))
            {
                error = "创建联接失败：联接路径已存在：" + link;
                return false;
            }

            string parent = string.Empty;
            try
            {
                parent = Path.GetDirectoryName(link);
            }
            catch (Exception)
            {
                parent = string.Empty;
            }
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                error = "创建联接失败：联接所在目录不存在：" + parent;
                return false;
            }

            // 方式一：cmd.exe /c mklink /J（Windows 自带，且 Junction 不要求管理员权限）
            string mklinkError;
            bool mklinkOk = RunMklink(link, target, out mklinkError);
            if (mklinkOk && Verify(link, target))
            {
                Log.Info("已创建目录联接：" + link + " -> " + target);
                return true;
            }
            string mklinkDetail = mklinkError;
            if (mklinkOk)
            {
                mklinkDetail = "mklink 已创建联接但校验指向失败";
                Log.Warn(mklinkDetail + "，清理后改用符号链接：" + link);
                string cleanupError;
                if (!Remove(link, out cleanupError) && IsReparsePoint(link))
                {
                    error = "创建目录联接失败：" + mklinkDetail + "，且无法清理残留联接：" + cleanupError;
                    return false;
                }
            }

            // 方式二：P/Invoke CreateSymbolicLink（目录型符号链接）
            try
            {
                uint flags = SYMBOLIC_LINK_FLAG_DIRECTORY | SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE;
                if (CreateSymbolicLinkW(link, target, flags))
                {
                    if (Verify(link, target))
                    {
                        Log.Info("已创建目录符号链接：" + link + " -> " + target);
                        return true;
                    }
                    error = "创建目录符号链接成功，但校验指向失败：" + link;
                    return false;
                }
                int code = Marshal.GetLastWin32Error();
                string detail = string.IsNullOrEmpty(mklinkDetail) ? "mklink 调用失败" : mklinkDetail;
                error = "创建目录联接失败：" + detail + "；改用符号链接也失败（" + Win32Message(code) + "）。";
                return false;
            }
            catch (Exception ex)
            {
                error = "创建目录联接失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 删除联接本身（绝不删除目标内容）。
        /// 如果路径不是重解析点而是真实目录/文件，会拒绝删除并给出中文原因。
        /// </summary>
        public static bool Remove(string linkPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(linkPath))
            {
                error = "删除联接失败：联接路径为空。";
                return false;
            }

            string link = PathUtil.Normalize(linkPath);
            if (string.IsNullOrEmpty(link))
            {
                error = "删除联接失败：联接路径无效。";
                return false;
            }

            string target;
            bool isLink = TryGetTarget(link, out target);
            if (!isLink)
            {
                // 读不出重解析信息时，只有“确实不存在”才视为删除成功
                if (Directory.Exists(link) || File.Exists(link))
                {
                    error = "拒绝删除：" + link + " 不是目录联接或符号链接（避免误删真实目录）。";
                    Log.Warn(error);
                    return false;
                }
                return true;
            }

            try
            {
                Directory.Delete(link, false); // 递归参数必须为 false，只删联接本身
                if (Directory.Exists(link) || File.Exists(link))
                {
                    error = "删除联接后路径仍然存在：" + link;
                    return false;
                }
                Log.Info("已删除目录联接：" + link);
                return true;
            }
            catch (Exception ex)
            {
                error = "删除目录联接失败：" + link + "：" + ex.Message;
                return false;
            }
        }

        /// <summary>是否为目录联接（IO_REPARSE_TAG_MOUNT_POINT）</summary>
        public static bool IsJunction(string path)
        {
            uint tag;
            if (!TryReadReparseTag(path, out tag))
            {
                return false;
            }
            return tag == IO_REPARSE_TAG_MOUNT_POINT;
        }

        /// <summary>是否为重解析点（目录联接 / 符号链接 / 其他 reparse point）</summary>
        public static bool IsReparsePoint(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 悬空联接（目标已不存在）时 GetAttributes 会失败，下面继续用 IOCTL 判断
            }

            uint tag;
            if (TryReadReparseTag(path, out tag))
            {
                return tag == IO_REPARSE_TAG_MOUNT_POINT || tag == IO_REPARSE_TAG_SYMLINK;
            }
            return false;
        }

        /// <summary>读取联接目标；不是重解析点或读取失败时返回 false</summary>
        public static bool TryGetTarget(string path, out string target)
        {
            target = null;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string normalized = PathUtil.Normalize(path);
            uint tag;
            byte[] buffer;
            if (!TryReadReparseData(normalized, out tag, out buffer))
            {
                return false;
            }
            return TryParseTarget(buffer, tag, normalized, out target);
        }

        /// <summary>校验联接是否存在且指向指定目标</summary>
        public static bool Verify(string linkPath, string expectedTarget)
        {
            if (string.IsNullOrEmpty(linkPath) || string.IsNullOrEmpty(expectedTarget))
            {
                return false;
            }
            string actual;
            if (!TryGetTarget(linkPath, out actual))
            {
                return false;
            }
            if (string.IsNullOrEmpty(actual))
            {
                return false;
            }
            string a = PathUtil.Normalize(actual);
            string e = PathUtil.Normalize(expectedTarget);
            if (a.Length == 0 || e.Length == 0)
            {
                return false;
            }
            return string.Equals(a, e, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>执行 cmd.exe /c mklink /J，返回是否成功</summary>
        private static bool RunMklink(string link, string target, out string error)
        {
            error = null;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                if (!File.Exists(cmdPath))
                {
                    cmdPath = "cmd.exe";
                }
                startInfo.FileName = cmdPath;
                startInfo.Arguments = "/d /c mklink /J \"" + link + "\" \"" + target + "\"";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = "无法启动 cmd.exe 创建联接。";
                        return false;
                    }
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(20000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                        error = "执行 mklink 超时。";
                        return false;
                    }
                    string stdout = outputTask.Result;
                    string stderr = errorTask.Result;
                    if (process.ExitCode == 0)
                    {
                        return true;
                    }
                    string detail = TextUtil.Clean(stderr);
                    if (string.IsNullOrEmpty(detail))
                    {
                        detail = TextUtil.Clean(stdout);
                    }
                    error = "mklink 返回码 " + process.ExitCode + (string.IsNullOrEmpty(detail) ? string.Empty : "：" + detail);
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = "调用 mklink 失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>读取重解析标签（读不到时返回 false）</summary>
        private static bool TryReadReparseTag(string path, out uint tag)
        {
            tag = 0;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            byte[] buffer;
            uint readTag;
            if (!TryReadReparseData(PathUtil.Normalize(path), out readTag, out buffer))
            {
                return false;
            }
            tag = readTag;
            return true;
        }

        /// <summary>通过 IOCTL 读取重解析数据（不跟随联接，悬空联接也能读）</summary>
        private static bool TryReadReparseData(string path, out uint tag, out byte[] buffer)
        {
            tag = 0;
            buffer = null;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            IntPtr handle = InvalidHandleValue;
            try
            {
                handle = CreateFileW(
                    path,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                    IntPtr.Zero);

                if (handle == InvalidHandleValue || handle == IntPtr.Zero)
                {
                    return false;
                }

                byte[] data = new byte[MaximumReparseDataBufferSize];
                uint bytesReturned;
                bool ok = DeviceIoControl(
                    handle,
                    FSCTL_GET_REPARSE_POINT,
                    IntPtr.Zero,
                    0,
                    data,
                    (uint)data.Length,
                    out bytesReturned,
                    IntPtr.Zero);
                if (!ok || bytesReturned < 8)
                {
                    return false;
                }

                tag = BitConverter.ToUInt32(data, 0);
                buffer = data;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (handle != InvalidHandleValue && handle != IntPtr.Zero)
                {
                    try
                    {
                        CloseHandle(handle);
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>解析 REPARSE_DATA_BUFFER 中的联接目标路径</summary>
        private static bool TryParseTarget(byte[] data, uint tag, string linkPath, out string target)
        {
            target = null;
            if (data == null || data.Length < 16)
            {
                return false;
            }

            // REPARSE_DATA_BUFFER: ReparseTag(4) + ReparseDataLength(2) + Reserved(2)
            // MountPoint: 从偏移 16 开始是 PathBuffer
            // SymbolicLink: 从偏移 20 开始是 PathBuffer（多一个 ULONG Flags）
            int pathBufferOffset;
            bool isSymbolicLink = false;
            if (tag == IO_REPARSE_TAG_MOUNT_POINT)
            {
                pathBufferOffset = 16;
            }
            else if (tag == IO_REPARSE_TAG_SYMLINK)
            {
                pathBufferOffset = 20;
                isSymbolicLink = true;
            }
            else
            {
                return false;
            }
            if (data.Length < pathBufferOffset)
            {
                return false;
            }

            int substituteOffset = BitConverter.ToUInt16(data, 8);
            int substituteLength = BitConverter.ToUInt16(data, 10);
            int printOffset = BitConverter.ToUInt16(data, 12);
            int printLength = BitConverter.ToUInt16(data, 14);

            string raw = ExtractUnicode(data, pathBufferOffset + substituteOffset, substituteLength);
            if (string.IsNullOrEmpty(raw))
            {
                raw = ExtractUnicode(data, pathBufferOffset + printOffset, printLength);
            }
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            raw = raw.TrimEnd('\0');
            raw = NormalizeReparseTarget(raw);

            bool relative = false;
            if (isSymbolicLink && data.Length >= 20)
            {
                uint flags = BitConverter.ToUInt32(data, 16);
                relative = (flags & 1) != 0; // SYMLINK_FLAG_RELATIVE
            }

            if (relative && !Path.IsPathRooted(raw))
            {
                string parent = string.Empty;
                try
                {
                    parent = Path.GetDirectoryName(linkPath);
                }
                catch (Exception)
                {
                    parent = string.Empty;
                }
                if (!string.IsNullOrEmpty(parent))
                {
                    raw = Path.Combine(parent, raw);
                }
            }

            target = PathUtil.Normalize(raw);
            return target.Length > 0;
        }

        /// <summary>
        /// 把重解析点里保存的目标名规范成普通路径。
        /// 需要处理的形式：
        ///   \??\C:\dir、\??\Global\C:\dir（Windows 11 新式联接）、\\?\C:\dir、
        ///   \??\UNC\server\share、\??\GLOBALROOT\...
        /// </summary>
        private static string NormalizeReparseTarget(string raw)
        {
            string text = raw;
            bool stripped = true;
            while (stripped)
            {
                stripped = false;
                if (text.StartsWith(@"\??\", StringComparison.Ordinal))
                {
                    text = text.Substring(4);
                    stripped = true;
                    continue;
                }
                if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    text = text.Substring(4);
                    stripped = true;
                    continue;
                }
                if (text.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    text = @"\\" + text.Substring(4);
                    stripped = true;
                    continue;
                }
                if (text.StartsWith(@"GLOBALROOT\", StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring(@"GLOBALROOT\".Length);
                    stripped = true;
                    continue;
                }
                if (text.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase))
                {
                    // Windows 11 部分版本的联接会写成 \??\Global\C:\...
                    text = text.Substring(@"Global\".Length);
                    stripped = true;
                    continue;
                }
            }
            return text;
        }

        /// <summary>按 UTF-16 读取一段 Unicode 字符串（越界时返回空）</summary>
        private static string ExtractUnicode(byte[] data, int offset, int byteLength)
        {
            if (byteLength <= 0 || offset < 0)
            {
                return string.Empty;
            }
            if (offset + byteLength > data.Length)
            {
                return string.Empty;
            }
            return Encoding.Unicode.GetString(data, offset, byteLength);
        }

        /// <summary>把 Win32 错误码转成中文提示</summary>
        private static string Win32Message(int code)
        {
            switch (code)
            {
                case 2:
                    return "系统找不到指定的路径";
                case 3:
                    return "系统找不到指定的路径";
                case 5:
                    return "访问被拒绝，需要管理员权限";
                case 32:
                    return "文件正被另一个程序使用";
                case 80:
                    return "文件已存在";
                case 1314:
                    return "当前账号缺少所需权限（需要管理员权限或开发者模式）";
                default:
                    try
                    {
                        return new Win32Exception(code).Message;
                    }
                    catch (Exception)
                    {
                        return "错误码 " + code;
                    }
            }
        }
    }
}
