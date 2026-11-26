using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using YijianNuodong.Core;
namespace YijianNuodong.SelfTest
{
    public static class DataMoveTests
    {
        public static string RunAll()
        {
            StringBuilder log = new StringBuilder();
            Test(log, "pip 保留其他节和选项，仅更新 download.dest", delegate(string root)
            {
                string patched = PipDownloadSettings.Patch("[global]\r\nindex-url = https://example.invalid/simple\r\n[download]\r\ntimeout = 60\r\ndest = C:\\old\r\n[install]\r\nuser = true\r\n", @"E:\new");
                Require(patched.Contains("index-url = https://example.invalid/simple") && patched.Contains("timeout = 60") && patched.Contains("[install]\r\nuser = true") && patched.Contains("dest = E:\\new") && !patched.Contains(@"C:\old"), patched);
            });
            Test(log, "pip 重复节/键、多行配置和特殊路径拒绝", delegate(string root)
            {
                Reject(delegate { PipDownloadSettings.Patch("[download]\n[download]\n", @"E:\new"); });
                Reject(delegate { PipDownloadSettings.Patch("[download]\ndest=x\ndest=y\n", @"E:\new"); });
                Reject(delegate { PipDownloadSettings.Patch("[download]\ndest=x\n  y\n", @"E:\new"); });
                Reject(delegate { PipDownloadSettings.Patch("", "E:\\bad\n[global]"); });
                Reject(delegate { PipDownloadSettings.Patch("", @"E:\bad%path"); });
            });
            Test(log, "pip 备份原字节，保存和撤销准确还原", delegate(string root)
            {
                string config = Path.Combine(root, "pip.ini"), downloads = Path.Combine(root, "downloads"); byte[] before = Encoding.Unicode.GetBytes("[global]\r\ntimeout = 55\r\n");
                byte[] bom = Encoding.Unicode.GetPreamble(); byte[] full = new byte[bom.Length + before.Length]; bom.CopyTo(full, 0); before.CopyTo(full, bom.Length); File.WriteAllBytes(config, full);
                PipSettingResult change = PipDownloadSettings.ApplyAt(config, downloads, false);
                Require(Convert.ToBase64String(File.ReadAllBytes(change.BackupPath)) == Convert.ToBase64String(full), "backup bytes");
                Require(File.ReadAllText(config).Contains("[download]") && Directory.Exists(downloads), "saved");
                PipDownloadSettings.Undo(change); Require(Convert.ToBase64String(File.ReadAllBytes(config)) == Convert.ToBase64String(full), "restore bytes");
            });
            Test(log, "pip 原配置不存在可撤销，后续改动阻止覆盖", delegate(string root)
            {
                string config = Path.Combine(root, "pip.ini"); PipSettingResult change = PipDownloadSettings.ApplyAt(config, Path.Combine(root, "downloads"), false);
                PipDownloadSettings.Undo(change); Require(!File.Exists(config) && File.Exists(change.BackupPath), "absent restored");
                change = PipDownloadSettings.ApplyAt(config, Path.Combine(root, "downloads"), false); File.AppendAllText(config, "# later\r\n");
                Reject(delegate { PipDownloadSettings.Undo(change); }); Require(File.ReadAllText(config).Contains("later"), "later value protected");
            });
            Test(log, "白名单仅七个已核对缓存/插件子目录", delegate(string root)
            {
                List<DataMoveEngine.Root> roots = DataMoveEngine.Roots(); Require(roots.Count == 7, "roots count");
                foreach (DataMoveEngine.Root r in roots) Require(!r.Path.EndsWith(".dsh", StringComparison.OrdinalIgnoreCase) && !r.Path.EndsWith("packages", StringComparison.OrdinalIgnoreCase) && !r.Path.EndsWith("AppData", StringComparison.OrdinalIgnoreCase), r.Path);
            });
            Test(log, "只读扫描与目标预览无写入，不自动勾选", delegate(string root)
            {
                string source = Seed(root); DateTime time = File.GetLastWriteTimeUtc(Path.Combine(source, "sample.txt"));
                DataScanResult scan = FixtureScan(source); Require(scan.Items.Count == 1 && scan.Items[0].SizeBytes == 6 && scan.Complete, "scan bytes");
                string dest = DataMoveEngine.TargetFor(scan.Items[0], Path.Combine(root, "SoftwareData"));
                Require(dest == Path.Combine(root, @"SoftwareData\Test\cache") && !Directory.Exists(Path.Combine(root, "SoftwareData")), "preview no write");
                Require(File.ReadAllText(Path.Combine(source, "sample.txt")) == "abcdef" && time == File.GetLastWriteTimeUtc(Path.Combine(source, "sample.txt")), "read-only");
            });
            Test(log, "扫描后的数据变化、未确认、外来 ID 和取消快照均拒绝", delegate(string root)
            {
                string source = Seed(root); DataScanResult scan = FixtureScan(source); scan.MutableItems[0].CanMove = true;
                string dest = Path.Combine(root, "SoftwareData"); File.AppendAllText(Path.Combine(source, "sample.txt"), "changed");
                Require(!DataMoveEngine.ExecuteItem(scan, scan.Items[0].Id, dest, true, null, null, false).Success, "changed");
                Require(!DataMoveEngine.ExecuteItem(scan, "foreign", dest, true, null, null, false).Success, "foreign");
                Require(!DataMoveEngine.ExecuteItem(scan, scan.Items[0].Id, dest, false, null, null, false).Success, "not closed");
                DataScanResult canceled = DataMoveEngine.ScanRoots(new List<DataMoveEngine.Root> { new DataMoveEngine.Root("test", source, @"Test\cache", "") }, delegate { return true; });
                Require(!canceled.Complete && !DataMoveEngine.ExecuteItem(canceled, "x", dest, true, null, null, false).Success, "cancel"); Require(!Directory.Exists(dest), "no destination written");
            });
            Test(log, "占用文件禁止搬移，不结束占用进程", delegate(string root)
            {
                string source = Seed(root); DataScanResult scan = FixtureScan(source); scan.MutableItems[0].CanMove = true;
                using (FileStream writer = new FileStream(Path.Combine(source, "sample.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    Require(!DataMoveEngine.ExecuteItem(scan, scan.Items[0].Id, Path.Combine(root, "SoftwareData"), true, null, null, false).Success && writer.CanWrite, "writer retained");
                Require(Directory.Exists(source) && !Junction.IsReparsePoint(source), "source retained");
            });
            Test(log, "嵌套 Junction 不进入可搬列表，根 Junction 显示真实位置且不计大小", delegate(string root)
            {
                string source = Seed(root), outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "canary"), "keep");
                string link = Path.Combine(source, "link"), error;
                if (!Junction.Create(link, outside, out error)) throw new Exception("junction unavailable: " + error);
                try { DataScanResult scan = FixtureScan(source); Require(!scan.Items[0].CanMove && scan.Items[0].Status == "暂不处理", "nested blocked"); } finally { Junction.Remove(link, out error); }
                link = Path.Combine(root, "rootlink"); Require(Junction.Create(link, outside, out error), error);
                try { DataScanResult scan = FixtureScan(link); Require(!scan.Items[0].CanMove && scan.Items[0].SizeBytes < 0 && PathUtil.SamePath(scan.Items[0].ActualPath, outside), "already linked"); } finally { Junction.Remove(link, out error); }
                Require(File.ReadAllText(Path.Combine(outside, "canary")) == "keep", "canary untouched");
            });
            Test(log, "白名单数据实际搬移、SHA256、联接及带新写入的撤销", delegate(string root)
            {
                string source = Seed(root); Directory.CreateDirectory(Path.Combine(source, "empty"));
                DataScanResult scan = FixtureScan(source); scan.MutableItems[0].CanMove = true;
                string dest = Path.Combine(root, "SoftwareData"); MoveResult moved = DataMoveEngine.ExecuteItem(scan, scan.Items[0].Id, dest, true, null, null, false);
                Require(moved.Success, moved.ErrorMessage); Require(Junction.Verify(source, moved.NewPath) && File.ReadAllText(Path.Combine(source, "sample.txt")) == "abcdef", "junction+bytes");
                Require(!string.IsNullOrEmpty(moved.JournalPath), "journal required");
                try { File.WriteAllText(Path.Combine(source, "new-write.txt"), "latest"); MoveResult undo = MoveEngine.Undo(moved.JournalPath, null); Require(undo.Success, undo.ErrorMessage); Require(!Junction.IsReparsePoint(source) && File.ReadAllText(Path.Combine(source, "new-write.txt")) == "latest" && Directory.Exists(Path.Combine(source, "empty")), "latest data preserved"); }
                finally { string error; if (Junction.IsReparsePoint(source)) Junction.Remove(source, out error); }
            });
            Test(log, "提交校验失败回滚原目录且不丢数据", delegate(string root)
            {
                string source = Seed(root), target = Path.Combine(root, "destination");
                MoveRequest request = new MoveRequest { Program = new InstalledProgram { DisplayName = "Fixture", InstallPath = source }, TargetDirectory = target, CreateJunction = true, StopRelatedServices = false, UpdateShortcuts = false, UpdateRegistry = false };
                request.ValidateDataTransfer = delegate { throw new IOException("injected validation failure"); };
                MoveResult moved = MoveEngine.Execute(request, null, null);
                Require(!moved.Success && Directory.Exists(source) && !Junction.IsReparsePoint(source) && File.ReadAllText(Path.Combine(source, "sample.txt")) == "abcdef", moved.ErrorMessage);
            });
            Test(log, "安装包归档校验字节与恢复凭据，仅处理勾选项", delegate(string root)
            {
                string downloads = Path.Combine(root, "downloads"), archive = Path.Combine(root, "archive"); Directory.CreateDirectory(downloads);
                string chosen = Path.Combine(downloads, "setup.exe"), keep = Path.Combine(downloads, "keep.msi"); File.WriteAllText(chosen, "installer-bytes"); File.WriteAllText(keep, "keep");
                CleanupScanResult scan = InstallerScan(downloads); string id = null; foreach (CleanupItem item in scan.Items) if (item.FullPath == chosen) id = item.Id;
                CleanupResult moved = CleanupEngine.ArchiveInstallers(scan, new List<string> { id, id }, archive, true, null, null);
                Require(moved.DeletedFiles == 1 && moved.SkippedFiles == 0 && string.IsNullOrEmpty(moved.ErrorMessage), moved.ErrorMessage + string.Join(";", moved.Details.ToArray()));
                Require(!File.Exists(chosen) && File.Exists(keep), "selection"); string[] copies = Directory.GetFiles(archive, "*.exe"); Require(copies.Length == 1 && File.ReadAllText(copies[0]) == "installer-bytes", "bytes");
                Require(File.ReadAllText(copies[0] + ".restore.txt").Contains(chosen), "recovery receipt");
            });
            Test(log, "归档未确认/混入外来 ID 整批拒绝，变化/占用源保留", delegate(string root)
            {
                string downloads = Path.Combine(root, "downloads"), archive = Path.Combine(root, "archive"); Directory.CreateDirectory(downloads); string file = Path.Combine(downloads, "setup.exe"); File.WriteAllText(file, "installer");
                CleanupScanResult scan = InstallerScan(downloads); string id = scan.Items[0].Id;
                Require(!string.IsNullOrEmpty(CleanupEngine.ArchiveInstallers(scan, new List<string> { id }, archive, false, null, null).ErrorMessage), "confirmation");
                Require(!string.IsNullOrEmpty(CleanupEngine.ArchiveInstallers(scan, new List<string> { id, "foreign" }, archive, true, null, null).ErrorMessage) && !Directory.Exists(archive), "foreign batch");
                File.AppendAllText(file, "changed"); CleanupResult changed = CleanupEngine.ArchiveInstallers(scan, new List<string> { id }, archive, true, null, null); Require(changed.DeletedFiles == 0 && changed.SkippedFiles == 1 && File.Exists(file), "changed retained");
                scan = InstallerScan(downloads); id = scan.Items[0].Id;
                using (FileStream held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { CleanupResult busy = CleanupEngine.ArchiveInstallers(scan, new List<string> { id }, archive, true, null, null); Require(busy.DeletedFiles == 0 && busy.SkippedFiles == 1 && held.CanWrite, "busy retained"); }
                CleanupResult canceled = CleanupEngine.ArchiveInstallers(scan, new List<string> { id }, archive, true, null, delegate { return true; }); Require(canceled.Canceled && canceled.DeletedFiles == 0 && File.Exists(file), "cancel retained");
            });
            return log.ToString();
        }
        private static CleanupScanResult InstallerScan(string path)
        {
            foreach (string file in Directory.GetFiles(path)) { File.SetCreationTimeUtc(file, DateTime.UtcNow.AddDays(-3)); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3)); }
            return CleanupEngine.ScanRoots(new List<CleanupRoot> { new CleanupRoot { Path = path, Category = CleanupCategory.DownloadedInstallers, MinimumAgeDays = 1, Description = "fixture" } }, DateTime.UtcNow, null, null);
        }
        private static void Test(StringBuilder log, string name, Action<string> action)
        {
            string root = Path.Combine(Environment.CurrentDirectory, "tests", "_tmp", "data-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { action(root); log.AppendLine("[PASS] " + name); }
            catch (Exception ex) { log.AppendLine("[FAIL] " + name + "：" + ex); }
            finally { try { DeleteSafe(root); } catch (Exception ex) { log.AppendLine("[FAIL] fixture cleanup：" + ex.Message); } }
        }
        private static DataScanResult FixtureScan(string source) { return DataMoveEngine.ScanRoots(new List<DataMoveEngine.Root> { new DataMoveEngine.Root("fixture", source, @"Test\cache", "fixture") }, null); }
        private static string Seed(string root) { string source = Path.Combine(root, "source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "sample.txt"), "abcdef", new UTF8Encoding(false)); return source; }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Reject(Action action) { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } Require(rejected, "expected IOException"); }
        private static void DeleteSafe(string path)
        {
            string error; if (Junction.IsReparsePoint(path)) { if (!Junction.Remove(path, out error)) throw new IOException(error); return; }
            foreach (string file in Directory.GetFiles(path)) File.Delete(file);
            foreach (string dir in Directory.GetDirectories(path)) DeleteSafe(dir);
            Directory.Delete(path);
        }
    }
}
