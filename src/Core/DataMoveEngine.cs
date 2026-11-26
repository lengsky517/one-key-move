using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YijianNuodong.Core
{
    // Whitelist only. No whole AppData/profile/runtime/credential relocation.
    public static class DataMoveEngine
    {
        internal sealed class Root
        {
            internal string Name, Path, Target, Advice;
            internal Root(string name, string path, string target, string advice) { Name = name; Path = path; Target = target; Advice = advice; }
        }
        internal sealed class Tree
        {
            internal readonly SortedDictionary<string, string> Metadata = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal long Bytes;
            internal string Stamp;
        }
        internal static List<Root> Roots()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            List<Root> r = new List<Root>();
            r.Add(new Root("pip 包缓存", Path.Combine(local, @"pip\cache"), @"Python\pip\cache", "可重建的包缓存；不是 pip download 成品。先退出 Python/pip。"));
            r.Add(new Root("npm 缓存", Path.Combine(local, "npm-cache"), @"Node\npm-cache", "保留原路径联接；自定义 npm cache 配置不在本白名单内。"));
            r.Add(new Root("pnpm 缓存", Path.Combine(local, "pnpm-cache"), @"Node\pnpm-cache", "仅此缓存目录，不搬 store/全局包/项目 node_modules。"));
            r.Add(new Root("Codex 插件", Path.Combine(home, @".codex\plugins"), @"Codex\plugins", "只搬 plugins；不搬认证、会话和 packages 运行时。先退出 Codex。"));
            r.Add(new Root("Claude 插件", Path.Combine(home, @".claude\plugins"), @"Claude\plugins", "只搬 plugins；保留设置与认证。先退出 Claude。"));
            r.Add(new Root("WorkBuddy 插件", Path.Combine(home, @".workbuddy\plugins"), @"WorkBuddy\plugins", "只搬 plugins；先退出 WorkBuddy。"));
            r.Add(new Root("WorkBuddy AI 插件", Path.Combine(home, @".workbuddy-ai\plugins"), @"WorkBuddyAI\plugins", "只搬 plugins；先退出 WorkBuddy AI。"));
            return r;
        }
        public static DataScanResult Scan(Action<string> report, Func<bool> canceled)
        {
            DataScanResult scan = ScanRoots(Roots(), canceled);
            string system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            try { DriveInfo d = new DriveInfo(system); scan.Summary = system + " 总容量 " + TextUtil.FormatBytes(d.TotalSize) + " · 已用 " + TextUtil.FormatBytes(d.TotalSize - d.AvailableFreeSpace) + " · 可用 " + TextUtil.FormatBytes(d.AvailableFreeSpace); }
            catch (Exception ex) { scan.Summary = "磁盘容量读取失败：" + ex.Message; }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddRow(scan, "NVIDIA 着色器缓存", Path.Combine(local, @"NVIDIA\DXCache"), "可重建缓存", "建议通过清理入口评估；本页不搬驱动缓存。", canceled, report);
            AddRow(scan, "Windows", Environment.GetFolderPath(Environment.SpecialFolder.Windows), "系统核心 / 只读盘点", "禁止搬移；逻辑大小含硬链接，可能大于实际占用。", canceled, report);
            AddRow(scan, "用户 AppData", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"), "软件数据 / 汇总（含下方缓存子项）", "不整体搬；只允许“可搬数据”白名单。", canceled, report);
            AddRow(scan, "文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "个人数据 / 仅展示", "不自动迁移；OneDrive/目录联接跳过，避免重复计入系统盘。", canceled, report);
            AddRow(scan, "Program Files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "已安装程序", "使用主界面的软件搬家，不整体搬父目录。", canceled, report);
            string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!PathUtil.SamePath(x86, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))) AddRow(scan, "Program Files (x86)", x86, "已安装程序", "使用主界面的软件搬家。", canceled, report);
            AddSystemFile(scan, "休眠文件", Path.Combine(system, "hiberfil.sys"), "系统管理；不能直接搬。关闭休眠会影响休眠/快速启动，本程序不自动操作。");
            AddSystemFile(scan, "虚拟内存", Path.Combine(system, "pagefile.sys"), "系统管理；建议保留系统自动管理，本程序不移动或删除。");
            scan.MutableRows.Add(new SpaceRow { Name = "系统还原点 / 卷影副本", Path = Path.Combine(system, "System Volume Information"), SizeBytes = -1, Nature = "系统管理 / 未测量", Advice = "需要专门的系统接口统计；本页不推测大小、不删除还原点。" });
            if (canceled != null && canceled()) scan.Complete = false;
            return scan;
        }
        internal static DataScanResult ScanRoots(List<Root> roots, Func<bool> canceled)
        {
            DataScanResult scan = new DataScanResult { Complete = true };
            foreach (Root root in roots)
            {
                if (canceled != null && canceled()) { scan.Complete = false; break; }
                DataMoveItem item = new DataMoveItem { Id = Guid.NewGuid().ToString("N"), Name = root.Name, SourcePath = root.Path, RelativeTarget = root.Target, Advice = root.Advice, SizeBytes = -1, Status = "未安装 / 不存在" };
                scan.MutableItems.Add(item);
                try
                {
                    CheckParents(root.Path);
                    if (Junction.IsReparsePoint(root.Path))
                    {
                        string actual; item.Status = "已联接 / 不重复搬";
                        if (Junction.TryGetTarget(root.Path, out actual)) item.ActualPath = actual;
                        item.Advice += " 不遍历目标，不计入本地可释放容量。"; continue;
                    }
                    if (!Directory.Exists(root.Path)) continue;
                    Tree tree = ReadTree(root.Path, canceled, 100000, 8000);
                    item.SizeBytes = tree.Bytes; item.Stamp = tree.Stamp;
                    bool system = string.Equals(Path.GetPathRoot(root.Path), Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), StringComparison.OrdinalIgnoreCase);
                    item.CanMove = tree.Bytes > 0 && system;
                    item.Status = item.CanMove ? "可搬（先退出软件）" : (system ? "空目录" : "已在非系统盘 / 不搬");
                }
                catch (Exception ex) { item.Status = "暂不处理"; item.Advice += " 扫描未完整：" + ex.Message; }
            }
            return scan;
        }
        private static void AddRow(DataScanResult scan, string name, string path, string nature, string advice, Func<bool> canceled, Action<string> report)
        {
            if (report != null) report("只读盘点：" + name);
            SpaceRow row = new SpaceRow { Name = name, Path = path, Nature = nature, Advice = advice, SizeBytes = -1 };
            scan.MutableRows.Add(row);
            try
            {
                if (!string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), StringComparison.OrdinalIgnoreCase)) { row.Advice = "已在其他盘，不计 C 盘。" + advice; return; }
                CheckParents(path);
                if (Junction.IsReparsePoint(path)) { string target; Junction.TryGetTarget(path, out target); row.Advice = "已联接到 " + target + "；跳过，避免重复计数。"; return; }
                // Partial scans are not passed off as totals. Reparse children are skipped, not followed.
                row.SizeBytes = Measure(path, canceled, out nature); row.Nature += nature;
            }
            catch (Exception ex) { row.Advice += " 未完整测量：" + ex.Message; }
        }
        private static long Measure(string path, Func<bool> canceled, out string note)
        {
            Stopwatch clock = Stopwatch.StartNew(); int entries = 0, skips = 0; long bytes = 0; bool partial = false;
            Stack<string> dirs = new Stack<string>(); dirs.Push(path);
            while (dirs.Count > 0)
            {
                if ((canceled != null && canceled()) || clock.ElapsedMilliseconds > 6000 || entries > 200000) { partial = true; break; }
                string dir = dirs.Pop();
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                    {
                        if (++entries > 200000 || clock.ElapsedMilliseconds > 6000 || (canceled != null && canceled())) { partial = true; break; }
                        try { FileAttributes a = File.GetAttributes(entry); if ((a & FileAttributes.ReparsePoint) != 0) { skips++; continue; } if ((a & FileAttributes.Directory) != 0) dirs.Push(entry); else bytes += new FileInfo(entry).Length; }
                        catch { partial = true; }
                    }
                }
                catch { partial = true; }
            }
            note = partial ? " · 未完整（权限/超时/取消）" : " · 逻辑大小";
            if (skips > 0) note += " · 跳过 " + skips + " 个链接";
            return partial ? -1 : bytes;
        }
        private static void AddSystemFile(DataScanResult scan, string name, string path, string advice)
        {
            long size = -1; try { size = new FileInfo(path).Length; } catch { }
            scan.MutableRows.Add(new SpaceRow { Name = name, Path = path, Nature = "系统文件 / 只读", SizeBytes = size, Advice = advice });
        }
        internal static void CheckParents(string path)
        {
            string canonical; if (!CleanupEngine.TryCanonical(path, out canonical)) throw new IOException("仅支持本机绝对路径，不能为根、UNC、ADS 或设备路径。");
            DirectoryInfo d = Directory.GetParent(canonical);
            while (d != null) { if (Junction.IsReparsePoint(d.FullName)) throw new IOException("父路径含链接：" + d.FullName); d = d.Parent; }
        }
        internal static Tree ReadTree(string root, Func<bool> canceled, int limit, int milliseconds)
        {
            CheckParents(root); if (Junction.IsReparsePoint(root)) throw new IOException("根目录是链接。");
            Tree tree = new Tree(); Stack<string> dirs = new Stack<string>(); dirs.Push(root); Stopwatch clock = Stopwatch.StartNew();
            while (dirs.Count > 0)
            {
                string dir = dirs.Pop();
                foreach (string path in Directory.EnumerateFileSystemEntries(dir))
                {
                    if ((canceled != null && canceled())) throw new OperationCanceledException("扫描已取消。");
                    if (tree.Metadata.Count >= limit || clock.ElapsedMilliseconds > milliseconds) throw new IOException("目录过大或扫描超时，保留原地。");
                    FileAttributes a = File.GetAttributes(path);
                    if ((a & FileAttributes.ReparsePoint) != 0) throw new IOException("存在嵌套链接，不能整体搬：" + path);
                    string relative = path.Substring(root.TrimEnd('\\').Length + 1);
                    if ((a & FileAttributes.Directory) != 0) { tree.Metadata.Add(relative, "D"); dirs.Push(path); }
                    else { FileInfo f = new FileInfo(path); tree.Metadata.Add(relative, "F|" + f.Length + "|" + f.CreationTimeUtc.Ticks + "|" + f.LastWriteTimeUtc.Ticks); tree.Bytes += f.Length; }
                }
            }
            StringBuilder s = new StringBuilder(); foreach (KeyValuePair<string, string> e in tree.Metadata) s.Append(e.Key).Append('|').Append(e.Value).Append('\n');
            using (SHA256 hash = SHA256.Create()) tree.Stamp = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(s.ToString())));
            return tree;
        }
        public static string TargetFor(DataMoveItem item, string baseDirectory)
        {
            string canonical; if (item == null || !CleanupEngine.TryCanonical(baseDirectory, out canonical)) throw new IOException("请选择如 D:\\SoftwareData 的本机目标父目录。");
            return Path.Combine(canonical, item.RelativeTarget);
        }
        public static MoveResult Execute(DataScanResult scan, string id, string baseDirectory, bool softwareClosed, MoveProgressHandler progress, Func<bool> canceled)
        {
            return ExecuteItem(scan, id, baseDirectory, softwareClosed, progress, canceled, true);
        }
        internal static MoveResult ExecuteItem(DataScanResult scan, string id, string baseDirectory, bool softwareClosed, MoveProgressHandler progress, Func<bool> canceled, bool requireOtherDrive)
        {
            try
            {
                if (scan == null || !scan.Complete || !softwareClosed) throw new IOException("必须完成扫描并确认相关软件已退出。");
                DataMoveItem item = null; foreach (DataMoveItem candidate in scan.Items) if (candidate.Id == id) item = candidate;
                if (item == null || !item.CanMove) throw new IOException("无效或不可搬的扫描项；请重新扫描。");
                string target = TargetFor(item, baseDirectory);
                if (requireOtherDrive && PathUtil.SamePath(Path.GetPathRoot(item.SourcePath), Path.GetPathRoot(target))) throw new IOException("必须选择非系统盘的目标。");
                CheckParents(target); if (Directory.Exists(target) || File.Exists(target) || Junction.IsReparsePoint(target)) throw new IOException("目标已存在，禁止合并或覆盖：" + target);
                Tree now = ReadTree(item.SourcePath, canceled, 100000, 15000);
                if (now.Stamp != item.Stamp) throw new IOException("扫描后目录已变化，请关闭软件并重新扫描。");
                using (FrozenTree frozen = new FrozenTree(item.SourcePath, now, canceled))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (IDisposable sourceGuard = CleanupEngine.GuardPath(item.SourcePath))
                    using (IDisposable targetGuard = CleanupEngine.GuardPath(target))
                    {
                    string receiptPath = target + ".onekeymove-restore.txt";
                    using (FileStream receipt = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (StreamWriter writer = new StreamWriter(receipt, new UTF8Encoding(true)))
                        writer.WriteLine("数据搬家恢复信息（执行前写入，不能据此认定任务完成）\r\n原路径：" + item.SourcePath + "\r\n目标：" + target + "\r\n预期字节：" + now.Bytes + "\r\n先核对原路径是否为指向目标的目录联接。成功记录可在主窗口搬家日记中撤销；无日记时保留两处数据，手动恢复前勿删除联接/目标。失败时若存在 .__okm_old_* 目录请一并保留。");
                    MoveRequest request = new MoveRequest { Program = new InstalledProgram { DisplayName = item.Name, InstallPath = item.SourcePath, SizeBytes = now.Bytes }, TargetDirectory = target, CreateJunction = true, KillBlockingProcesses = false, StopRelatedServices = false, UpdateShortcuts = false, UpdateRegistry = false, CreateRestorePoint = false };
                    request.ReleaseDataHandles = frozen.Dispose;
                    request.ValidateDataTransfer = delegate(string backup, string destination) { frozen.Verify(backup, destination, canceled); };
                    MoveResult moved = MoveEngine.Execute(request, progress, canceled);
                    moved.Warnings.Add("目标旁恢复信息：" + receiptPath);
                    return moved;
                    }
                }
            }
            catch (Exception ex) { Log.Error("用户数据搬家失败", ex); return new MoveResult { Success = false, FinalStage = ex is OperationCanceledException ? MoveStage.Canceled : MoveStage.Failed, ErrorMessage = ex.Message }; }
        }
        // Hold source files against writes during copying; release for Windows directory rename,
        // then reopen and compare source backup and target with the captured hashes before commit.
        private sealed class FrozenTree : IDisposable
        {
            private readonly Dictionary<string, FileStream> files = new Dictionary<string, FileStream>();
            private readonly Dictionary<string, string> hashes = new Dictionary<string, string>();
            private readonly Tree tree;
            internal FrozenTree(string root, Tree snapshot, Func<bool> canceled)
            {
                tree = snapshot;
                try
                {
                    foreach (KeyValuePair<string, string> e in snapshot.Metadata)
                    {
                        if (canceled != null && canceled()) throw new OperationCanceledException();
                        if (e.Value == "D") continue;
                        FileStream stream = new FileStream(Path.Combine(root, e.Key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        files.Add(e.Key, stream); hashes.Add(e.Key, Hash(stream));
                    }
                    if (ReadTree(root, canceled, 100000, 15000).Stamp != snapshot.Stamp) throw new IOException("锁定源文件期间目录发生变化。");
                }
                catch { Dispose(); throw; }
            }
            internal void Verify(string backup, string target, Func<bool> canceled)
            {
                if (ReadTree(backup, canceled, 100000, 15000).Stamp != tree.Stamp) throw new IOException("提交前源目录已变化，取消并回滚。");
                Tree copied = ReadTree(target, canceled, 100000, 15000);
                if (copied.Metadata.Count != tree.Metadata.Count || copied.Bytes != tree.Bytes) throw new IOException("目标目录结构/大小校验失败。");
                foreach (KeyValuePair<string, string> e in tree.Metadata)
                {
                    if (canceled != null && canceled()) throw new OperationCanceledException();
                    string value; if (!copied.Metadata.TryGetValue(e.Key, out value) || (value == "D") != (e.Value == "D")) throw new IOException("目标结构不一致：" + e.Key);
                    if (e.Value == "D") continue;
                    using (FileStream source = new FileStream(Path.Combine(backup, e.Key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    using (FileStream destination = new FileStream(Path.Combine(target, e.Key), FileMode.Open, FileAccess.Read, FileShare.Read))
                        if (Hash(source) != hashes[e.Key] || Hash(destination) != hashes[e.Key]) throw new IOException("SHA256 不一致：" + e.Key);
                }
            }
            private static string Hash(Stream stream) { stream.Position = 0; using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(stream)); }
            public void Dispose() { foreach (FileStream stream in files.Values) stream.Dispose(); files.Clear(); }
        }
    }
}
