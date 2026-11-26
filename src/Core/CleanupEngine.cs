// 仅扫描白名单目录；删除使用扫描快照 + 文件标识 + 独占句柄复核，不递归删除目录。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YijianNuodong.Core
{
    public static class CleanupEngine
    {
        private const int MaxItems = 20000;
        private const int MaxVisited = 200000;
        private const int MaxSeconds = 60;
        private const uint ReadAttributes = 0x80, DeleteAccess = 0x10000;
        private const uint OpenExisting = 3, OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
        private const uint Reparse = 0x400, DirectoryAttribute = 0x10, ReadOnlyAttribute = 1, SystemAttribute = 4;
        private static readonly string[] InstallerExtensions = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".iso" };
        private static readonly string[] PersonalExtensions = { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".mp3", ".wav", ".mp4", ".mov", ".zip", ".7z", ".rar", ".psd", ".ai", ".db", ".sqlite" };

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
            public System.Runtime.InteropServices.ComTypes.FILETIME Access;
            public System.Runtime.InteropServices.ComTypes.FILETIME Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Disposition { [MarshalAs(UnmanagedType.U1)] public bool Delete; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref Disposition info, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        public static CleanupScanResult Scan(Action<CleanupProgress> report, Func<bool> canceled)
        {
            List<string> warnings = new List<string>();
            string systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            List<CleanupRoot> roots = GetProductionRoots(systemRoot, warnings);
            CleanupScanResult result = ScanRoots(roots, DateTime.UtcNow, report, canceled);
            result.DriveLabel = systemRoot;
            foreach (string warning in warnings) AddWarning(result, warning);
            return result;
        }

        // 内部入口只用于隔离测试；应用界面不能传入任意目录。
        internal static CleanupScanResult ScanRoots(List<CleanupRoot> roots, DateTime utcNow, Action<CleanupProgress> report, Func<bool> canceled)
        {
            CleanupScanResult result = new CleanupScanResult();
            result.DriveLabel = "隔离测试";
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stopwatch clock = Stopwatch.StartNew();
            int visited = 0;
            long totalBytes = 0;
            foreach (CleanupRoot input in roots)
            {
                if (IsCanceled(canceled)) { result.Canceled = true; break; }
                if (result.LimitReached) break;
                string rootPath;
                if (!TryCanonical(input.Path, out rootPath) || !Directory.Exists(rootPath)) continue;
                CleanupRoot root = new CleanupRoot { Path = rootPath, Category = input.Category, MinimumAgeDays = Math.Max(1, input.MinimumAgeDays), Description = input.Description };
                if (result.Roots.ContainsKey(rootPath)) continue;
                try { using (DirectoryGuards guard = GuardParents(Path.Combine(rootPath, "_guard_only_"))) { } }
                catch (Exception ex) { AddWarning(result, "跳过扫描目录：" + rootPath + " | " + ex.GetType().Name + "：" + ex.Message); continue; }
                result.Roots.Add(rootPath, root);
                result.RootCount++;
                Stack<string> pending = new Stack<string>();
                pending.Push(rootPath);
                while (pending.Count > 0)
                {
                    if (IsCanceled(canceled)) { result.Canceled = true; break; }
                    if (visited >= MaxVisited || result.MutableItems.Count >= MaxItems || clock.Elapsed.TotalSeconds >= MaxSeconds)
                    { result.LimitReached = true; AddWarning(result, "为保持响应，本次扫描达到数量或 60 秒上限；只列出已检查的文件，可以分批清理后重新扫描。"); break; }
                    string directory = pending.Pop();
                    try
                    {
                        // 枚举期间锁住整个父路径，不跟随目录联接/符号链接。
                        using (DirectoryGuards guards = GuardParents(Path.Combine(directory, "_guard_only_")))
                        {
                            foreach (string sub in System.IO.Directory.EnumerateDirectories(directory))
                            {
                                visited++;
                                if (pending.Count + visited >= MaxVisited) { result.LimitReached = true; break; }
                                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
                                else AddWarning(result, "保留目录链接：" + sub);
                            }
                            foreach (string file in System.IO.Directory.EnumerateFiles(directory))
                            {
                                visited++;
                                if (IsCanceled(canceled)) { result.Canceled = true; break; }
                                if (visited >= MaxVisited || result.MutableItems.Count >= MaxItems || clock.Elapsed.TotalSeconds >= MaxSeconds) { result.LimitReached = true; break; }
                                string canonical;
                                if (!TryCanonical(file, out canonical) || !IsInside(canonical, rootPath) || !seen.Add(canonical)) continue;
                                if (root.Category == CleanupCategory.DownloadedInstallers && !HasExtension(canonical, InstallerExtensions)) continue;
                                if (root.Category == CleanupCategory.TemporaryFiles && HasExtension(canonical, PersonalExtensions)) continue;
                                try
                                {
                                    using (SafeFileHandle handle = Open(canonical, ReadAttributes, 7, OpenReparsePoint))
                                    {
                                        FileInformation info = ReadInfo(handle);
                                        if (!Eligible(info) || FileTime(info.Write) > utcNow.AddDays(-root.MinimumAgeDays).ToFileTimeUtc() || FileTime(info.Creation) > utcNow.AddDays(-root.MinimumAgeDays).ToFileTimeUtc()) continue;
                                        string id = Guid.NewGuid().ToString("N");
                                        CleanupItem item = new CleanupItem
                                        {
                                            Id = id, FullPath = canonical, RootPath = rootPath, DisplayName = Path.GetFileName(canonical), Category = root.Category,
                                            Description = root.Description, SizeBytes = Size(info), LastWriteTimeUtc = DateTime.FromFileTimeUtc(FileTime(info.Write)),
                                            DefaultSelected = root.Category != CleanupCategory.DownloadedInstallers,
                                            Identity = Identity(info), CreationTime = FileTime(info.Creation), WriteTime = FileTime(info.Write)
                                        };
                                        result.MutableItems.Add(item); result.Candidates.Add(id, item); totalBytes += item.SizeBytes;
                                    }
                                }
                                catch (Exception ex) { AddWarning(result, "无法检查文件：" + canonical + " | " + ex.GetType().Name + "：" + ex.Message); }
                                if (visited % 150 == 0) Report(report, "正在检查：" + directory, result.MutableItems.Count, 0, totalBytes);
                            }
                        }
                    }
                    catch (Exception ex) { AddWarning(result, "无法读取目录：" + directory + " | " + ex.GetType().Name + "：" + ex.Message); }
                    if (result.Canceled || result.LimitReached) break;
                }
                if (result.Canceled) break;
            }
            result.MutableItems.Sort(delegate(CleanupItem a, CleanupItem b) { return b.SizeBytes.CompareTo(a.SizeBytes); });
            Report(report, result.Canceled ? "扫描已取消，请重新扫描" : "扫描完成（仅列出超过保留时间的文件）", result.Items.Count, result.Items.Count, totalBytes);
            Log.Info("清理扫描结束：目录 " + result.RootCount + "，候选文件 " + result.Items.Count + "，取消=" + result.Canceled + "，上限=" + result.LimitReached);
            return result;
        }

        public static CleanupResult Execute(CleanupRequest request, Action<CleanupProgress> report, Func<bool> canceled)
        {
            CleanupResult result = new CleanupResult();
            if (request == null || request.Scan == null || !request.ConfirmPermanentDelete)
            { result.ErrorMessage = "请先扫描并确认永久删除；未删除任何文件。"; return result; }
            if (request.Scan.Canceled) { result.ErrorMessage = "扫描已取消，请重新扫描后再清理。"; return result; }
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            List<CleanupItem> selected = new List<CleanupItem>();
            if (request.SelectedIds != null)
                foreach (string id in request.SelectedIds)
                {
                    CleanupItem item;
                    if (id == null || !request.Scan.Candidates.TryGetValue(id, out item)) { result.ErrorMessage = "选择项已失效，请重新扫描；未删除任何文件。"; return result; }
                    if (!unique.Add(id)) continue;
                    if (item.Category == CleanupCategory.DownloadedInstallers && !request.ConfirmInstallers)
                    { result.ErrorMessage = "下载的安装包和可执行文件需要单独确认；未删除任何文件。"; return result; }
                    selected.Add(item);
                }
            if (selected.Count == 0) { result.ErrorMessage = "尚未勾选文件。"; return result; }
            StringBuilder journal = new StringBuilder();
            journal.AppendLine("一键挪动 C 盘清理记录");
            journal.AppendLine("版本：" + typeof(CleanupEngine).Assembly.GetName().Version);
            journal.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            journal.AppendLine("方式：逐文件永久删除，不进回收站。字节数为逻辑文件大小，不保证磁盘可用空间等量增加。");
            for (int i = 0; i < selected.Count; i++)
            {
                if (IsCanceled(canceled)) { result.Canceled = true; break; }
                CleanupItem item = selected[i];
                string reason;
                bool deleted = TryDeleteCandidate(request.Scan, item, out reason);
                string detail;
                if (deleted) { result.DeletedFiles++; result.DeletedBytes += item.SizeBytes; detail = "已清理 | " + item.FullPath + " | " + item.SizeBytes + " 字节"; }
                else { result.SkippedFiles++; detail = "已跳过 | " + item.FullPath + " | " + reason; }
                result.Details.Add(detail); journal.AppendLine(detail);
                Report(report, "正在清理：" + item.DisplayName, i + 1, selected.Count, result.DeletedBytes);
            }
            journal.AppendLine("已清理 " + result.DeletedFiles + " 个文件，" + result.DeletedBytes + " 字节；跳过 " + result.SkippedFiles + " 个；已取消=" + result.Canceled);
            result.LogPath = WriteJournal(journal.ToString(), result);
            Log.Info("C 盘清理结束：清理 " + result.DeletedFiles + "，跳过 " + result.SkippedFiles + "，取消=" + result.Canceled + "，日志=" + result.LogPath);
            return result;
        }

        public static CleanupResult ArchiveInstallers(CleanupScanResult scan, IList<string> ids, string destination, bool confirmed, Action<CleanupProgress> report, Func<bool> canceled)
        {
            CleanupResult result = new CleanupResult();
            string canonical;
            if (!confirmed || scan == null || scan.Canceled || ids == null || ids.Count == 0 || !TryCanonical(destination, out canonical)) { result.ErrorMessage = "请完成扫描、勾选并确认归档目标。"; return result; }
            try
            {
                DataMoveEngine.CheckParents(canonical);
                if (Junction.IsReparsePoint(canonical) || File.Exists(canonical)) throw new IOException("归档目标不能是链接或普通文件。");
                if (PathUtil.SamePath(Path.GetPathRoot(canonical), Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)))) throw new IOException("归档目标必须在非系统盘。");
                List<CleanupItem> selected = new List<CleanupItem>(); HashSet<string> unique = new HashSet<string>();
                foreach (string id in ids) { CleanupItem item; if (!scan.Candidates.TryGetValue(id, out item) || item.Category != CleanupCategory.DownloadedInstallers) throw new IOException("包含无效或非安装包扫描项，未归档。"); if (unique.Add(id)) selected.Add(item); }
                Directory.CreateDirectory(canonical);
                foreach (CleanupItem item in selected)
                {
                    if (IsCanceled(canceled)) { result.Canceled = true; break; }
                    string target = Path.Combine(canonical, Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + item.DisplayName);
                    string reason;
                    bool archived = TryDeleteCandidate(scan, item, out reason, target);
                    if (archived) { result.DeletedFiles++; result.DeletedBytes += item.SizeBytes; result.Details.Add("已归档：" + item.FullPath + " -> " + target); }
                    else { result.SkippedFiles++; result.Details.Add("已保留源文件：" + item.FullPath + "；" + reason + "；若目标副本存在请核对：" + target); }
                    Report(report, "归档：" + item.DisplayName, result.DeletedFiles + result.SkippedFiles, selected.Count, result.DeletedBytes);
                }
                result.LogPath = WriteJournal("安装包归档（不是永久删除）：\r\n" + string.Join("\r\n", result.Details.ToArray()) + "\r\n恢复方法：关闭安装器后将归档文件复制回原路径，原路径有文件时不要覆盖。", result);
            }
            catch (Exception ex) { result.ErrorMessage = ex.Message; }
            return result;
        }

        private static bool TryDeleteCandidate(CleanupScanResult scan, CleanupItem item, out string reason)
        { return TryDeleteCandidate(scan, item, out reason, null); }

        private static bool TryDeleteCandidate(CleanupScanResult scan, CleanupItem item, out string reason, string archiveTarget)
        {
            reason = null;
            try
            {
                string canonical;
                CleanupRoot root;
                if (!TryCanonical(item.FullPath, out canonical) || !scan.Roots.TryGetValue(item.RootPath, out root) || !IsInside(canonical, root.Path))
                    throw new IOException("文件不属于本次扫描的允许目录");
                if (root.Category != item.Category || (root.Category == CleanupCategory.DownloadedInstallers && !HasExtension(canonical, InstallerExtensions)))
                    throw new IOException("文件类型与扫描记录不符");
                using (DirectoryGuards guards = GuardParents(canonical))
                using (SafeFileHandle handle = Open(canonical, ReadAttributes | DeleteAccess | (archiveTarget == null ? 0u : 0x80000000u), 0, OpenReparsePoint))
                {
                    FileInformation info = ReadInfo(handle);
                    if (!Eligible(info)) throw new IOException("文件为只读/系统/链接/多重硬链接，已保留");
                    if (Identity(info) != item.Identity || Size(info) != item.SizeBytes || FileTime(info.Write) != item.WriteTime || FileTime(info.Creation) != item.CreationTime)
                        throw new IOException("文件在扫描后已变化，请重新扫描");
                    StringBuilder final = new StringBuilder(32768);
                    uint chars = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
                    string actual;
                    string finalText = final.ToString();
                    if (finalText.StartsWith(@"\\?\", StringComparison.Ordinal)) finalText = finalText.Substring(4);
                    if (chars == 0 || chars >= final.Capacity || !TryCanonical(finalText, out actual) || !string.Equals(actual, canonical, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("文件实际路径不一致，已保留");
                    if (archiveTarget != null)
                    {
                        using (DirectoryGuards targetGuards = GuardParents(archiveTarget))
                        using (FileStream source = new FileStream(handle, FileAccess.Read))
                        {
                            using (FileStream copy = new FileStream(archiveTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(copy);
                            source.Position = 0;
                            using (System.Security.Cryptography.SHA256 hash = System.Security.Cryptography.SHA256.Create())
                            using (FileStream copy = new FileStream(archiveTarget, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                string originalHash = Convert.ToBase64String(hash.ComputeHash(source));
                                if (copy.Length != item.SizeBytes || originalHash != Convert.ToBase64String(hash.ComputeHash(copy))) throw new IOException("归档 SHA256 校验未通过，保留源文件。");
                                using (FileStream receipt = new FileStream(archiveTarget + ".restore.txt", FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                using (StreamWriter writer = new StreamWriter(receipt, new UTF8Encoding(true)))
                                    writer.WriteLine("原路径：" + canonical + "\r\n归档路径：" + archiveTarget + "\r\n字节：" + item.SizeBytes + "\r\nSHA256（Base64）：" + originalHash + "\r\n恢复：手动复制到原路径；不要覆盖既有文件。此凭据在移除源文件前写入，不能单独证明任务已完成。");
                            }
                            Disposition archivedDisposition = new Disposition { Delete = true };
                            if (!SetFileInformationByHandle(handle, 4, ref archivedDisposition, (uint)Marshal.SizeOf(typeof(Disposition)))) throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                    }
                    else
                    {
                        Disposition disposition = new Disposition { Delete = true };
                        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf(typeof(Disposition)))) throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
                return true;
            }
            catch (Exception ex) { reason = ex.GetType().Name + "：" + ex.Message + "（可能被占用、无权限或已变化；不会强制关闭程序）"; return false; }
        }

        private sealed class DirectoryGuards : IDisposable
        {
            internal readonly List<SafeFileHandle> Handles = new List<SafeFileHandle>();
            public void Dispose() { for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
        }
        internal static IDisposable GuardPath(string path) { return GuardParents(path); }
        private static DirectoryGuards GuardParents(string file)
        {
            string canonical;
            if (!TryCanonical(file, out canonical)) throw new IOException("不支持的文件路径");
            string parent = Path.GetDirectoryName(canonical);
            string drive = Path.GetPathRoot(canonical);
            DirectoryGuards guards = new DirectoryGuards();
            try
            {
                string current = drive;
                string[] parts = parent.Substring(drive.Length).Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                for (int index = -1; index < parts.Length; index++)
                {
                    if (index >= 0) current = Path.Combine(current, parts[index]);
                    SafeFileHandle handle = Open(current, ReadAttributes, 3, BackupSemantics | OpenReparsePoint);
                    guards.Handles.Add(handle);
                    FileInformation info = ReadInfo(handle);
                    if ((info.Attributes & Reparse) != 0 || (info.Attributes & DirectoryAttribute) == 0) throw new IOException("父路径含目录链接或不是目录：" + current);
                }
                return guards;
            }
            catch { guards.Dispose(); throw; }
        }
        private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
        {
            SafeFileHandle handle = CreateFileW(PathUtil.ToLongPath(path), access, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
            if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
            return handle;
        }
        private static FileInformation ReadInfo(SafeFileHandle handle)
        {
            FileInformation info;
            if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info;
        }
        private static bool Eligible(FileInformation info)
        { return (info.Attributes & (Reparse | DirectoryAttribute | ReadOnlyAttribute | SystemAttribute)) == 0 && info.Links == 1; }
        private static long FileTime(System.Runtime.InteropServices.ComTypes.FILETIME value) { return ((long)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime; }
        private static long Size(FileInformation info) { return ((long)info.SizeHigh << 32) | info.SizeLow; }
        private static string Identity(FileInformation info) { return info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow; }
        internal static bool TryCanonical(string input, out string path)
        {
            path = null;
            try
            {
                if (string.IsNullOrWhiteSpace(input) || input.Length < 3 || !char.IsLetter(input[0]) || input[1] != ':' || (input[2] != '\\' && input[2] != '/')) return false;
                if (input.IndexOf(':', 2) >= 0) return false;
                string value = Path.GetFullPath(input).TrimEnd('\\');
                if (value.Length <= 3) return false;
                foreach (string part in value.Substring(3).Split('\\')) if (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)) return false;
                path = value; return true;
            }
            catch { return false; }
        }
        private static bool IsInside(string path, string root) { return path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
        private static bool HasExtension(string path, string[] extensions) { string ext = Path.GetExtension(path); foreach (string value in extensions) if (string.Equals(ext, value, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        private static bool IsCanceled(Func<bool> canceled) { return canceled != null && canceled(); }
        private static void Report(Action<CleanupProgress> report, string message, int processed, int total, long bytes)
        { if (report != null) try { report(new CleanupProgress { Message = message, Processed = processed, Total = total, Bytes = bytes }); } catch { } }
        private static void AddWarning(CleanupScanResult scan, string warning) { if (scan.MutableWarnings.Count < 100) scan.MutableWarnings.Add(warning); }

        private static List<CleanupRoot> GetProductionRoots(string systemRoot, List<string> warnings)
        {
            List<CleanupRoot> roots = new List<CleanupRoot>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            AddRoot(roots, systemRoot, Path.Combine(local, "Temp"), CleanupCategory.TemporaryFiles, 7, "超过 7 天未修改且创建已满 7 天的临时文件；保留常见文档、图片和压缩包。");
            AddRoot(roots, systemRoot, Path.Combine(windows, "Temp"), CleanupCategory.TemporaryFiles, 7, "Windows 临时目录中超过 7 天的文件；占用、只读、系统文件会保留。");
            string[] userData = { Path.Combine(local, @"Microsoft\Edge\User Data"), Path.Combine(local, @"Google\Chrome\User Data"), Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data") };
            foreach (string browser in userData)
            {
                foreach (string profile in SafeDirectories(browser, warnings))
                {
                    string leaf = Path.GetFileName(profile);
                    if (leaf != "Default" && !leaf.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string cache in new string[] { "Cache", "Code Cache", "GPUCache" })
                        AddRoot(roots, systemRoot, Path.Combine(profile, cache), CleanupCategory.BrowserCache, 1, "浏览器缓存（超过 1 天），再次打开网页会重新下载；不扫描密码、Cookie、收藏夹和历史记录。");
                }
            }
            foreach (string profile in SafeDirectories(Path.Combine(local, @"Mozilla\Firefox\Profiles"), warnings))
                AddRoot(roots, systemRoot, Path.Combine(profile, "cache2"), CleanupCategory.BrowserCache, 1, "Firefox 页面缓存（超过 1 天）；不扫描个人资料和登录信息。");
            string downloads = GetDownloadsFolder();
            if (!string.IsNullOrEmpty(downloads))
            {
                if (string.Equals(Path.GetPathRoot(downloads), systemRoot, StringComparison.OrdinalIgnoreCase))
                    AddRoot(roots, systemRoot, downloads, CleanupCategory.DownloadedInstallers, 7, "下载目录里超过 7 天的安装包、安装镜像或可执行文件；EXE 也可能是便携软件，需自行确认后勾选。压缩包、文档和下载中的文件不扫描。");
                else warnings.Add("下载目录已移到其他盘：本次仅扫描系统盘，不会清理该下载目录。");
            }
            warnings.Add("范围限定为系统盘的临时目录、指定浏览器缓存及下载目录；不会扫描聊天记录、Windows Installer/WinSxS、软件安装目录或回收站。");
            return roots;
        }
        private static void AddRoot(List<CleanupRoot> roots, string systemRoot, string path, CleanupCategory category, int age, string description)
        {
            string canonical;
            if (TryCanonical(path, out canonical) && string.Equals(Path.GetPathRoot(canonical), systemRoot, StringComparison.OrdinalIgnoreCase))
                roots.Add(new CleanupRoot { Path = canonical, Category = category, MinimumAgeDays = age, Description = description });
        }
        private static string[] SafeDirectories(string path, List<string> warnings)
        {
            try { if (!Directory.Exists(path)) return new string[0]; using (DirectoryGuards guard = GuardParents(Path.Combine(path, "_guard_only_"))) return Directory.GetDirectories(path); }
            catch (Exception ex) { warnings.Add("无法检查浏览器缓存目录：" + path + " | " + ex.GetType().Name + "：" + ex.Message); return new string[0]; }
        }
        private static string GetDownloadsFolder()
        {
            IntPtr pointer = IntPtr.Zero;
            try
            {
                Guid id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out pointer) == 0) return Marshal.PtrToStringUni(pointer);
            }
            catch { }
            finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
            return null;
        }
        private static string WriteJournal(string text, CleanupResult result)
        {
            string name = "cleanup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt";
            string[] parents = { Path.Combine(AppPaths.LogDir, "cleanup"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"YijianNuodong\logs\cleanup") };
            foreach (string parent in parents)
                try { Directory.CreateDirectory(parent); string path = Path.Combine(parent, name); File.WriteAllText(path, text, new UTF8Encoding(true)); return path; }
                catch (Exception ex) { Log.Warn("清理日志无法写入：" + parent + " | " + ex.GetType().Name + "：" + ex.Message); }
            result.Details.Add("清理记录无法写入磁盘，请复制本页结果保存。");
            return null;
        }
    }
}
