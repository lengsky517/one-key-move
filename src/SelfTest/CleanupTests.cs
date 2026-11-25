// 隔离清理安全自检：仅 ScanRoots 自建目录，不调用生产 Scan，不扫描用户 Temp/Downloads。
// .NET Framework 4.8 / C# 5；由 Lead 统一编译后反射调用 RunAll。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using YijianNuodong.Core;

namespace YijianNuodong.SelfTest
{
    public static class CleanupTests
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string linkPath, string targetPath, IntPtr security);

        public static string RunAll() { return new Suite().RunAll(); }

        private sealed class SkipTestException : Exception
        {
            internal SkipTestException(string message) : base(message) { }
        }

        private sealed class Suite
        {
            private readonly List<string> lines = new List<string>();
            private int passed, failed, skipped;

            internal string RunAll()
            {
                Run("TryCanonical 拒绝盘符根、UNC、ADS、设备路径及相对路径", TestCanonical);
                Run("扫描只读，候选与根外 canary 的内容、双时间及属性不变", TestScanReadOnly);
                Run("临时文件双时间年龄、文档保护、大小排序与默认勾选", TestTemporaryPolicy);
                Run("浏览器缓存满一天才入选，默认勾选", TestBrowserPolicy);
                Run("安装包仅识别允许扩展名且默认不勾选", TestInstallerPolicy);
                Run("重复根与重叠根不重复列出文件", TestDuplicateRoots);
                Run("扫描候选和警告集合对调用方只读", TestReadOnlyCollections);
                Run("空请求、无扫描及未确认永久删除均拒绝", TestPermanentConfirmation);
                Run("安装包未单独确认时整批拒绝，确认后才删除", TestInstallerConfirmation);
                Run("合法 ID 后混入无效 ID 时整批拒绝", TestInvalidId);
                Run("合法 ID 后混入另一次扫描 ID 时整批拒绝", TestForeignScanId);
                Run("未选择任何 ID 时保留所有文件", TestEmptySelection);
                Run("仅删除勾选文件，重复 ID 不重复计数，字节和目录保留准确", TestSelectedFiles);
                Run("扫描后大小或修改时间变化均跳过且快照不变", TestChangedMetadata);
                Run("同大小同双时间替换仍通过文件身份识别并保留", TestReplacedIdentity);
                Run("占用期间扫描只读，句柄保留且释放后能重新扫描", TestBusyScan);
                Run("扫描后被占用的文件跳过，不关闭占用句柄", TestBusyExecute);
                Run("扫描前只读不列入，扫描后变只读则跳过", TestReadOnlyFiles);
                Run("取消扫描后不允许执行删除", TestCanceledScan);
                Run("执行前取消不删除文件且产生取消记录", TestCanceledExecute);
                Run("首个文件完成后取消，剩余文件保留且统计准确", TestPartialCancellation);
                Run("扫描根是目录联接时不扫描外部 canary", TestJunctionRoot);
                Run("扫描根上级含联接时不扫描外部 canary", TestJunctionAncestor);
                Run("扫描目录内的联接不递归，普通候选仍列入", TestJunctionChild);
                Run("扫描后中间父目录变联接时不删除外部文件", delegate(Fixture f) { TestParentReplacement(f, false); });
                Run("扫描后根目录变联接时不删除外部文件", delegate(Fixture f) { TestParentReplacement(f, true); });
                Run("多重硬链接不列入候选，普通文件仍列入", TestHardLinkScan);
                Run("扫描后新增硬链接时跳过，两个名字均保留", TestHardLinkExecute);
                Run("清理日志存在并包含成功、跳过及字节明细", TestJournal);
                StringBuilder text = new StringBuilder();
                text.AppendLine("========== 一键挪动 隔离清理安全自检 ==========");
                text.AppendLine("仅使用当前目录 tests/_tmp/cleanup_GUID；每项独立 finally 清理，不遍历链接。");
                foreach (string line in lines) text.AppendLine(line);
                text.AppendLine("----------------------------------------------");
                text.AppendLine(string.Format("通过 {0} 项 / 失败 {1} 项 / 跳过 {2} 项", passed, failed, skipped));
                return text.ToString();
            }

            private void Run(string name, Action<Fixture> test)
            {
                Fixture fixture = new Fixture();
                Exception failure = null, cleanupFailure = null;
                string skipReason = null;
                try { fixture.Create(); test(fixture); }
                catch (SkipTestException ex) { skipReason = ex.Message; }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { fixture.Dispose(); }
                    catch (Exception ex) { cleanupFailure = ex; }
                }
                if (failure != null) { failed++; lines.Add("[FAIL] " + name + " -> " + failure.ToString()); }
                else if (skipReason != null) { skipped++; lines.Add("[SKIP] " + name + " -> " + skipReason); }
                else { passed++; lines.Add("[PASS] " + name); }
                // 清理错误单独报告，不能覆盖测试本身抛出的断言或异常。
                if (cleanupFailure != null)
                {
                    failed++;
                    lines.Add("[FAIL] " + name + " fixture 清理 -> " + cleanupFailure.ToString());
                }
            }

            private static void TestCanonical(Fixture f)
            {
                string drive = Path.GetPathRoot(f.BasePath);
                string[] rejected = { null, "", " ", drive, drive.Replace('\\', '/'), drive.Substring(0, 2),
                    "relative\\a.bin", drive.Substring(0, 2) + "relative.bin", @"\\server\share\a.bin",
                    @"\\?\C:\fixture\a.bin", @"\\.\C:\fixture\a.bin", f.GetPath("a.bin") + ":stream" };
                foreach (string input in rejected)
                {
                    string canonical;
                    Need(!CleanupEngine.TryCanonical(input, out canonical), "不应接受路径：[" + input + "]");
                    Need(canonical == null, "拒绝路径时 out 值必须为空：[" + input + "]");
                }
                string expected = f.GetPath(@"普通目录 空格\样本.bin"), actual;
                Need(CleanupEngine.TryCanonical(expected, out actual), "普通盘符路径应可规范化");
                Need(SamePath(expected, actual), "规范化改变了普通文件路径");
                Need(CleanupEngine.TryCanonical(f.GetPath("parent") + @"\..\样本.bin", out actual), "应能规范化 ..");
                Need(SamePath(f.GetPath("样本.bin"), actual), ".. 规范化结果不正确");
                Need(CleanupEngine.TryCanonical(f.GetPath("allowed") + "\\", out actual), "应接受普通目录尾部分隔符");
                Need(SamePath(f.GetPath("allowed"), actual), "应去掉普通目录尾部分隔符");
            }

            private static void TestScanReadOnly(Fixture f)
            {
                string file = f.MakeFile(@"allowed\只读扫描样本.bin", 73, 0x31), canary = f.MakeFile(@"outside\canary.bin", 29, 0x61);
                FileSnapshot before = new FileSnapshot(file), outside = new FileSnapshot(canary);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7));
                Count(scan, 1);
                Need(Require(scan, file).SizeBytes == 73, "扫描大小必须等于逻辑文件大小");
                Need(scan.RootCount == 1 && !scan.Canceled && !scan.LimitReached, "普通隔离扫描状态错误");
                Need(Find(scan, canary) == null, "根外文件不能成为候选");
                before.AssertUnchanged(file); outside.AssertUnchanged(canary);
                Need(Directory.Exists(f.GetPath("allowed")), "扫描不能删除目录");
            }

            private static void TestTemporaryPolicy(Fixture f)
            {
                string large = f.MakeFile(@"allowed\子目录 空格\large.bin", 4099, 1), small = f.MakeFile(@"allowed\small.tmp", 17, 2);
                string empty = f.MakeFile(@"allowed\empty.bin", 0, 3), newCreation = f.MakeFile(@"allowed\new-creation.bin", 7, 4);
                string newWrite = f.MakeFile(@"allowed\new-write.tmp", 8, 5);
                SetTimes(newCreation, f.NowUtc, f.OldUtc); SetTimes(newWrite, f.OldUtc, f.NowUtc);
                string[] protectedNames = { "note.txt", "readme.md", "archive.zip", "report.docx", "sheet.xlsx",
                    "slides.pptx", "manual.pdf", "photo.jpg", "picture.png", "movie.mp4", "data.sqlite" };
                foreach (string name in protectedNames) f.MakeFile("allowed\\" + name, 13, 6);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7));
                Count(scan, 3);
                Need(Require(scan, large).SizeBytes == 4099 && Require(scan, small).SizeBytes == 17 && Require(scan, empty).SizeBytes == 0,
                    "候选字节必须保留真实长度，包括零字节文件");
                Need(SamePath(scan.Items[0].FullPath, large) && SamePath(scan.Items[2].FullPath, empty), "候选应按大小降序排列");
                foreach (CleanupItem item in scan.Items)
                    Need(item.DefaultSelected && item.Category == CleanupCategory.TemporaryFiles, "临时文件应默认勾选且类别正确");
                Need(Find(scan, newCreation) == null, "创建不足 7 天即使修改时间旧也必须排除");
                Need(Find(scan, newWrite) == null, "修改不足 7 天即使创建时间旧也必须排除");
                foreach (string name in protectedNames)
                    Need(Find(scan, f.GetPath("allowed\\" + name)) == null, "常见用户文件必须保留：" + name);
            }

            private static void TestBrowserPolicy(Fixture f)
            {
                string old = f.MakeFile(@"cache\old.bin", 23, 1), recent = f.MakeFile(@"cache\recent.bin", 21, 2);
                string newCreation = f.MakeFile(@"cache\new-creation.bin", 19, 3);
                SetTimes(old, f.NowUtc.AddDays(-2), f.NowUtc.AddDays(-2));
                SetTimes(recent, f.NowUtc.AddHours(-12), f.NowUtc.AddHours(-12));
                SetTimes(newCreation, f.NowUtc, f.NowUtc.AddDays(-2));
                CleanupScanResult scan = Scan(f, Root(f.GetPath("cache"), CleanupCategory.BrowserCache, 1));
                Count(scan, 1);
                CleanupItem item = Require(scan, old);
                Need(item.DefaultSelected && item.Category == CleanupCategory.BrowserCache && item.SizeBytes == 23, "缓存类别、大小或默认选择错误");
                Need(Find(scan, recent) == null && Find(scan, newCreation) == null, "缓存必须同时满足创建和修改年龄");
            }

            private static void TestInstallerPolicy(Fixture f)
            {
                string exe = f.MakeFile(@"downloads-fixture\portable.exe", 51, 1), msi = f.MakeFile(@"downloads-fixture\setup.MSI", 37, 2);
                string[] excluded = { "notes.txt", "archive.zip", "data.bin", "setup.exe.crdownload", "part.msi.download" };
                foreach (string name in excluded) f.MakeFile("downloads-fixture\\" + name, 19, 3);
                string fresh = f.MakeFile(@"downloads-fixture\fresh.exe", 11, 4);
                SetTimes(fresh, f.NowUtc, f.OldUtc);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("downloads-fixture"), CleanupCategory.DownloadedInstallers, 7));
                Count(scan, 2); Require(scan, exe); Require(scan, msi);
                foreach (CleanupItem item in scan.Items)
                    Need(!item.DefaultSelected && item.Category == CleanupCategory.DownloadedInstallers, "安装包必须默认不勾选");
                foreach (string name in excluded)
                    Need(Find(scan, f.GetPath("downloads-fixture\\" + name)) == null, "下载目录不应扫描：" + name);
                Need(Find(scan, fresh) == null, "新创建安装包必须保留");
            }

            private static void TestDuplicateRoots(Fixture f)
            {
                string a = f.MakeFile(@"allowed\a.bin", 11, 1), b = f.MakeFile(@"allowed\child\b.tmp", 13, 2), root = f.GetPath("allowed");
                CleanupScanResult scan = Scan(f, Root(root, CleanupCategory.TemporaryFiles, 7),
                    Root(root + "\\", CleanupCategory.TemporaryFiles, 7), Root(root.ToUpperInvariant(), CleanupCategory.TemporaryFiles, 7),
                    Root(f.GetPath(@"allowed\child"), CleanupCategory.TemporaryFiles, 7));
                Count(scan, 2);
                Need(scan.RootCount == 2, "同一路径的重复根应去重，重叠的普通子根仍可记录");
                CleanupItem first = Require(scan, a), second = Require(scan, b);
                Need(first.Id != second.Id && scan.Candidates.Count == 2, "候选 ID 必须唯一且不得重复统计重叠文件");
            }

            private static void TestReadOnlyCollections(Fixture f)
            {
                f.MakeFile(@"allowed\a.bin", 11, 1);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7));
                Count(scan, 1);
                bool itemsRejected = false, warningsRejected = false;
                try { ((IList<CleanupItem>)scan.Items).Clear(); } catch (NotSupportedException) { itemsRejected = true; }
                try { ((IList<string>)scan.Warnings).Add("forged"); } catch (NotSupportedException) { warningsRejected = true; }
                Need(itemsRejected && warningsRejected, "公开候选和警告集合必须拒绝修改");
                Need(scan.Items.Count == 1 && scan.Candidates.Count == 1, "修改尝试不得破坏扫描快照");
            }

            private static void TestPermanentConfirmation(Fixture f)
            {
                string file = f.MakeFile(@"allowed\a.bin", 19, 1);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7));
                string id = Require(scan, file).Id;
                Rejected(CleanupEngine.Execute(null, null, null), file);
                Rejected(CleanupEngine.Execute(new CleanupRequest { ConfirmPermanentDelete = true, SelectedIds = new List<string> { id } }, null, null), file);
                CleanupRequest request = Request(scan, id); request.ConfirmPermanentDelete = false;
                Rejected(CleanupEngine.Execute(request, null, null), file);
            }

            private static void TestInstallerConfirmation(Fixture f)
            {
                string regular = f.MakeFile(@"allowed\a.tmp", 11, 1), installer = f.MakeFile(@"installers\setup.exe", 37, 2);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7),
                    Root(f.GetPath("installers"), CleanupCategory.DownloadedInstallers, 7));
                CleanupRequest request = Request(scan, Require(scan, regular).Id, Require(scan, installer).Id);
                Rejected(CleanupEngine.Execute(request, null, null), regular, installer);
                request = Request(scan, Require(scan, installer).Id); request.ConfirmInstallers = true;
                CleanupResult result = CleanupEngine.Execute(request, null, null);
                Need(string.IsNullOrEmpty(result.ErrorMessage) && result.DeletedFiles == 1 && result.DeletedBytes == 37 && result.SkippedFiles == 0,
                    "单独确认后应准确删除所选安装包");
                Need(!File.Exists(installer) && File.Exists(regular), "仅确认选择的安装包可以删除，未选临时文件必须保留");
            }

            private static void TestInvalidId(Fixture f)
            {
                string file = f.MakeFile(@"allowed\a.bin", 17, 1);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7));
                string valid = Require(scan, file).Id;
                Rejected(CleanupEngine.Execute(Request(scan, valid, "missing_" + Guid.NewGuid().ToString("N")), null, null), file);
                Rejected(CleanupEngine.Execute(Request(scan, valid, null), null, null), file);
            }

            private static void TestForeignScanId(Fixture f)
            {
                string file = f.MakeFile(@"allowed\a.bin", 17, 1);
                CleanupRoot root = Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7);
                CleanupScanResult first = Scan(f, root), second = Scan(f, root);
                string valid = Require(first, file).Id, foreign = Require(second, file).Id;
                Need(valid != foreign, "同一文件的两次扫描应产生不同选择 ID");
                Rejected(CleanupEngine.Execute(Request(first, valid, foreign), null, null), file);
            }

            private static void TestEmptySelection(Fixture f)
            {
                string file = f.MakeFile(@"allowed\a.bin", 17, 1);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); Require(scan, file);
                Rejected(CleanupEngine.Execute(Request(scan), null, null), file);
                CleanupRequest request = Request(scan); request.SelectedIds = null;
                Rejected(CleanupEngine.Execute(request, null, null), file);
            }

            private static void TestSelectedFiles(Fixture f)
            {
                string a = f.MakeFile(@"allowed\a.bin", 17, 1), b = f.MakeFile(@"allowed\child\b.tmp", 39, 2);
                string keep = f.MakeFile(@"allowed\child\keep.bin", 53, 3), document = f.MakeFile(@"allowed\keep.txt", 23, 4);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); Count(scan, 3);
                CleanupItem first = Require(scan, a), second = Require(scan, b);
                CleanupResult result = CleanupEngine.Execute(Request(scan, first.Id, first.Id, second.Id), null, null);
                Need(string.IsNullOrEmpty(result.ErrorMessage) && !result.Canceled, "正常选择执行不应报错或取消");
                Need(result.DeletedFiles == 2 && result.DeletedBytes == 56 && result.SkippedFiles == 0 && result.Details.Count == 2,
                    "重复 ID 不得重复删除或累计字节，实际应为 2 个 / 56 字节");
                Need(!File.Exists(a) && !File.Exists(b) && File.Exists(keep) && File.Exists(document), "选中与未选中文件处理错误");
                Need(Directory.Exists(f.GetPath("allowed")) && Directory.Exists(f.GetPath(@"allowed\child")), "清理文件不能删除目录");
                Need(scan.Items.Count == 3 && first.SizeBytes == 17 && second.SizeBytes == 39, "执行不得改写扫描快照");
            }

            private static void TestChangedMetadata(Fixture f)
            {
                string sized = f.MakeFile(@"allowed\size.bin", 11, 1), timed = f.MakeFile(@"allowed\time.tmp", 17, 2);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7));
                CleanupItem sizeItem = Require(scan, sized), timeItem = Require(scan, timed); long originalWrite = timeItem.WriteTime;
                File.AppendAllText(sized, "changed"); File.WriteAllBytes(timed, Filled(17, 9)); File.SetLastWriteTimeUtc(timed, f.NowUtc);
                CleanupResult result = CleanupEngine.Execute(Request(scan, sizeItem.Id, timeItem.Id), null, null); Skipped(result, 2);
                Need(File.Exists(sized) && new FileInfo(sized).Length > 11, "扫描后变大的文件必须原样保留");
                Need(BytesEqual(File.ReadAllBytes(timed), Filled(17, 9)), "同大小但新修改时间的文件必须保留");
                Need(sizeItem.SizeBytes == 11 && timeItem.SizeBytes == 17 && timeItem.WriteTime == originalWrite, "文件变化不能反向修改扫描快照");
                Need(ContainsDetail(result, "变化"), "跳过明细应指出扫描后变化");
            }

            private static void TestReplacedIdentity(Fixture f)
            {
                string file = f.MakeFile(@"allowed\replace.bin", 31, 1);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); CleanupItem original = Require(scan, file);
                string held = f.GetPath("original-held.bin");
                // 保留旧文件对象，防止删除后 NTFS 复用其文件标识，导致测试不能证明身份复核。
                File.Move(file, held); f.MakeFile(@"allowed\replace.bin", 31, 7);
                SetTimes(file, DateTime.FromFileTimeUtc(original.CreationTime), DateTime.FromFileTimeUtc(original.WriteTime));
                CleanupItem replacement = Require(Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)), file);
                Need(replacement.SizeBytes == original.SizeBytes && replacement.CreationTime == original.CreationTime && replacement.WriteTime == original.WriteTime,
                    "替换 fixture 必须与快照大小和双时间完全一致");
                Need(replacement.Identity != original.Identity, "新旧 fixture 必须是两个不同文件对象");
                CleanupResult result = CleanupEngine.Execute(Request(scan, original.Id), null, null); Skipped(result, 1);
                Need(BytesEqual(File.ReadAllBytes(file), Filled(31, 7)) && BytesEqual(File.ReadAllBytes(held), Filled(31, 1)), "身份变化必须保留替换文件和移走的原文件");
                Need(ContainsDetail(result, "变化"), "替换身份应记录变化原因");
            }

            private static void TestBusyScan(Fixture f)
            {
                string busy = f.MakeFile(@"allowed\busy.bin", 17, 1), free = f.MakeFile(@"allowed\free.tmp", 19, 2);
                FileSnapshot before = new FileSnapshot(busy);
                using (FileStream handle = new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7));
                    Require(scan, free);
                    // FILE_READ_ATTRIBUTES 可能不受数据共享锁限制；可显示占用项，执行时必须再独占复核。
                    Need(scan.Items.Count >= 1 && scan.Items.Count <= 2, "占用扫描只能列出本项 fixture 中的文件");
                    Need(handle.ReadByte() == 1, "扫描不能关闭占用方的句柄");
                }
                before.AssertUnchanged(busy);
                Require(Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)), busy);
            }

            private static void TestBusyExecute(Fixture f)
            {
                string file = f.MakeFile(@"allowed\busy.bin", 17, 1);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); string id = Require(scan, file).Id;
                using (FileStream handle = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    CleanupResult result = CleanupEngine.Execute(Request(scan, id), null, null); Skipped(result, 1);
                    Need(File.Exists(file) && handle.ReadByte() == 1, "占用文件应保留，原句柄应继续可读");
                    Need(ContainsDetail(result, "占用"), "占用跳过明细必须说明原因");
                }
                Need(BytesEqual(File.ReadAllBytes(file), Filled(17, 1)), "释放占用后文件内容仍应完整");
            }

            private static void TestReadOnlyFiles(Fixture f)
            {
                string before = f.MakeFile(@"allowed\before.bin", 11, 1), after = f.MakeFile(@"allowed\after.tmp", 17, 2);
                File.SetAttributes(before, File.GetAttributes(before) | FileAttributes.ReadOnly);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); Count(scan, 1);
                Need(Find(scan, before) == null, "扫描时只读文件不能入选"); string id = Require(scan, after).Id;
                File.SetAttributes(after, File.GetAttributes(after) | FileAttributes.ReadOnly);
                CleanupResult result = CleanupEngine.Execute(Request(scan, id), null, null); Skipped(result, 1);
                Need(File.Exists(before) && File.Exists(after), "只读文件必须保留");
                Need((File.GetAttributes(after) & FileAttributes.ReadOnly) != 0, "引擎不能强制清除只读属性");
            }

            private static void TestCanceledScan(Fixture f)
            {
                string file = f.MakeFile(@"allowed\a.bin", 17, 1);
                CleanupScanResult scan = CleanupEngine.ScanRoots(new List<CleanupRoot> { Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7) }, f.NowUtc, null, delegate { return true; });
                Need(scan.Canceled && scan.Items.Count == 0, "扫描前取消应标记取消并停止枚举");
                CleanupResult result = CleanupEngine.Execute(Request(scan), null, null); Rejected(result, file);
                Need(result.ErrorMessage.IndexOf("取消", StringComparison.Ordinal) >= 0, "取消扫描的执行拒绝应说明需要重新扫描");
            }

            private static void TestCanceledExecute(Fixture f)
            {
                string file = f.MakeFile(@"allowed\a.bin", 17, 1);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7));
                CleanupResult result = CleanupEngine.Execute(Request(scan, Require(scan, file).Id), null, delegate { return true; });
                Need(result.Canceled && result.DeletedFiles == 0 && result.DeletedBytes == 0 && result.SkippedFiles == 0 && result.Details.Count == 0, "执行前取消不能计为删除或跳过");
                Need(File.Exists(file), "执行前取消必须保留文件");
                Need(!string.IsNullOrEmpty(result.LogPath) && File.Exists(result.LogPath), "取消也应有清理记录");
                Need(File.ReadAllText(result.LogPath).IndexOf("已取消=True", StringComparison.Ordinal) >= 0, "取消记录缺少取消状态");
            }

            private static void TestPartialCancellation(Fixture f)
            {
                string a = f.MakeFile(@"allowed\first.bin", 11, 1), b = f.MakeFile(@"allowed\second.bin", 17, 2), c = f.MakeFile(@"allowed\third.tmp", 23, 3);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); bool cancel = false;
                CleanupResult result = CleanupEngine.Execute(Request(scan, Require(scan, a).Id, Require(scan, b).Id, Require(scan, c).Id),
                    delegate(CleanupProgress progress) { if (progress.Processed == 1) cancel = true; }, delegate { return cancel; });
                Need(cancel && result.Canceled && result.DeletedFiles == 1 && result.DeletedBytes == 11 && result.SkippedFiles == 0 && result.Details.Count == 1,
                    "部分取消只能累计已完成的首个文件及其 11 字节");
                Need(!File.Exists(a) && File.Exists(b) && File.Exists(c), "取消后不得继续处理剩余文件");
                Need(Directory.Exists(f.GetPath("allowed")), "部分取消不能删除根目录");
            }

            private static void TestJunctionRoot(Fixture f)
            {
                string canary = f.MakeFile(@"outside\canary.bin", 31, 5); FileSnapshot before = new FileSnapshot(canary); string link = f.GetPath("linked-root");
                f.MakeDirectoryLink(link, f.GetPath("outside"));
                CleanupScanResult scan = Scan(f, Root(link, CleanupCategory.TemporaryFiles, 7)); Count(scan, 0);
                Need(scan.RootCount == 0 && scan.Warnings.Count > 0, "联接根应在纳入扫描根之前拒绝并警告"); before.AssertUnchanged(canary);
                Need(Junction.IsReparsePoint(link), "扫描不能删除或改写联接");
            }

            private static void TestJunctionAncestor(Fixture f)
            {
                string canary = f.MakeFile(@"outside\nested\canary.bin", 31, 5); FileSnapshot before = new FileSnapshot(canary); string link = f.GetPath("linked-parent");
                f.MakeDirectoryLink(link, f.GetPath("outside"));
                CleanupScanResult scan = Scan(f, Root(Path.Combine(link, "nested"), CleanupCategory.TemporaryFiles, 7)); Count(scan, 0);
                Need(scan.RootCount == 0 && scan.Warnings.Count > 0, "根自身普通但上级为联接时也必须拒绝"); before.AssertUnchanged(canary);
            }

            private static void TestJunctionChild(Fixture f)
            {
                string own = f.MakeFile(@"allowed\own.tmp", 17, 1), canary = f.MakeFile(@"outside\canary.bin", 31, 5);
                FileSnapshot before = new FileSnapshot(canary); string link = f.GetPath(@"allowed\external-link");
                f.MakeDirectoryLink(link, f.GetPath("outside"));
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); Count(scan, 1); Require(scan, own);
                Need(HasWarning(scan, link), "跳过子目录联接应带路径警告"); before.AssertUnchanged(canary);
                Need(Junction.IsReparsePoint(link), "扫描不得删除子目录联接");
            }

            private static void TestParentReplacement(Fixture f, bool replaceRoot)
            {
                string relative = replaceRoot ? @"allowed\canary.bin" : @"allowed\middle\canary.bin", file = f.MakeFile(relative, 31, 1);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); CleanupItem item = Require(scan, file);
                string external = f.MakeFile(@"outside\canary.bin", 31, 7);
                SetTimes(external, DateTime.FromFileTimeUtc(item.CreationTime), DateTime.FromFileTimeUtc(item.WriteTime)); FileSnapshot before = new FileSnapshot(external);
                string swapped = replaceRoot ? f.GetPath("allowed") : f.GetPath(@"allowed\middle"), held = f.GetPath("original-parent-held");
                Directory.Move(swapped, held); f.MakeDirectoryLink(swapped, f.GetPath("outside"));
                CleanupResult result = CleanupEngine.Execute(Request(scan, item.Id), null, null); Skipped(result, 1);
                Need(ContainsDetail(result, "父路径"), "父目录重解析应在父路径保护阶段被拒绝"); before.AssertUnchanged(external);
                Need(BytesEqual(File.ReadAllBytes(Path.Combine(held, "canary.bin")), Filled(31, 1)), "移走的原文件必须保留");
                Need(Junction.IsReparsePoint(swapped), "执行不能删除或改写替换的联接");
            }

            private static void TestHardLinkScan(Fixture f)
            {
                string original = f.MakeFile(@"allowed\original.bin", 31, 1), control = f.MakeFile(@"allowed\control.tmp", 17, 2), alias = f.GetPath(@"allowed\alias.bin");
                MakeHardLink(alias, original); FileSnapshot before = new FileSnapshot(original);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); Count(scan, 1); Require(scan, control);
                Need(Find(scan, original) == null && Find(scan, alias) == null, "同一文件的两个硬链接名字都应排除"); before.AssertUnchanged(original);
                Need(BytesEqual(File.ReadAllBytes(alias), Filled(31, 1)), "扫描不得改变硬链接内容");
            }

            private static void TestHardLinkExecute(Fixture f)
            {
                string original = f.MakeFile(@"allowed\original.bin", 31, 1);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); string id = Require(scan, original).Id, alias = f.GetPath("outside-alias.bin");
                MakeHardLink(alias, original); CleanupResult result = CleanupEngine.Execute(Request(scan, id), null, null); Skipped(result, 1);
                Need(File.Exists(original) && File.Exists(alias), "扫描后出现多重硬链接时两个名字都必须保留");
                Need(BytesEqual(File.ReadAllBytes(alias), Filled(31, 1)) && ContainsDetail(result, "硬链接"), "硬链接跳过应保留内容并记录原因");
            }

            private void TestJournal(Fixture f)
            {
                string gone = f.MakeFile(@"allowed\gone.bin", 31, 1), busy = f.MakeFile(@"allowed\busy.tmp", 17, 2);
                CleanupScanResult scan = Scan(f, Root(f.GetPath("allowed"), CleanupCategory.TemporaryFiles, 7)); CleanupResult result;
                using (FileStream handle = new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    result = CleanupEngine.Execute(Request(scan, Require(scan, gone).Id, Require(scan, busy).Id), null, null);
                    Need(handle.ReadByte() == 2, "日志测试不能关闭占用句柄");
                }
                Need(string.IsNullOrEmpty(result.ErrorMessage) && result.DeletedFiles == 1 && result.DeletedBytes == 31 && result.SkippedFiles == 1, "日志用例必须实际完成一项删除及一项跳过");
                Need(!File.Exists(gone) && File.Exists(busy) && result.Details.Count == 2, "日志与文件执行结果应一致");
                Need(!string.IsNullOrEmpty(result.LogPath) && File.Exists(result.LogPath), "清理记录必须写到磁盘"); string journal = File.ReadAllText(result.LogPath);
                Need(journal.IndexOf("已清理 | " + gone + " | 31 字节", StringComparison.Ordinal) >= 0, "磁盘日志缺少成功路径与准确字节");
                Need(journal.IndexOf("已跳过 | " + busy + " | ", StringComparison.Ordinal) >= 0, "磁盘日志缺少跳过路径与原因");
                foreach (string detail in result.Details) Need(journal.IndexOf(detail, StringComparison.Ordinal) >= 0, "返回明细与磁盘日志不一致：" + detail);
                Need(journal.IndexOf("已清理 1 个文件，31 字节；跳过 1 个", StringComparison.Ordinal) >= 0, "磁盘日志缺少准确合计");
                lines.Add("  清理记录（保留供 Lead 审阅）：" + result.LogPath);
            }
        }

        private static CleanupRoot Root(string path, CleanupCategory category, int days)
        { return new CleanupRoot { Path = path, Category = category, MinimumAgeDays = days, Description = "仅隔离自检 fixture" }; }

        private static CleanupScanResult Scan(Fixture f, params CleanupRoot[] roots)
        { return CleanupEngine.ScanRoots(new List<CleanupRoot>(roots), f.NowUtc, null, null); }

        private static CleanupRequest Request(CleanupScanResult scan, params string[] ids)
        { return new CleanupRequest { Scan = scan, SelectedIds = new List<string>(ids), ConfirmPermanentDelete = true, ConfirmInstallers = false }; }

        private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private static void Count(CleanupScanResult scan, int expected)
        { Need(scan.Items.Count == expected, "期望候选 " + expected + "，实际 " + scan.Items.Count + "；警告：" + string.Join(" | ", scan.Warnings)); }

        private static CleanupItem Find(CleanupScanResult scan, string path)
        { foreach (CleanupItem item in scan.Items) if (SamePath(item.FullPath, path)) return item; return null; }

        private static CleanupItem Require(CleanupScanResult scan, string path)
        {
            CleanupItem item = Find(scan, path); Need(item != null, "有效 fixture 未成为候选：" + path + "；警告：" + string.Join(" | ", scan.Warnings)); return item;
        }

        private static void Rejected(CleanupResult result, params string[] retained)
        {
            Need(!string.IsNullOrEmpty(result.ErrorMessage), "应拒绝请求并给出原因");
            Need(result.DeletedFiles == 0 && result.DeletedBytes == 0 && result.SkippedFiles == 0 && result.Details.Count == 0, "请求验证失败必须先整批拒绝，不得部分执行");
            foreach (string file in retained) Need(File.Exists(file), "拒绝请求前不能删除合法文件：" + file);
        }

        private static void Skipped(CleanupResult result, int count)
        {
            Need(string.IsNullOrEmpty(result.ErrorMessage) && !result.Canceled, "有效请求应在逐文件复核时安全跳过");
            Need(result.DeletedFiles == 0 && result.DeletedBytes == 0 && result.SkippedFiles == count && result.Details.Count == count,
                "跳过统计错误，期望 " + count + " 项且删除 0 字节，实际删除=" + result.DeletedFiles + "，跳过=" + result.SkippedFiles);
        }

        private static bool ContainsDetail(CleanupResult result, string text)
        { foreach (string detail in result.Details) if (detail.IndexOf(text, StringComparison.Ordinal) >= 0) return true; return false; }

        private static bool HasWarning(CleanupScanResult scan, string text)
        { foreach (string warning in scan.Warnings) if (warning.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }

        private static bool SamePath(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        private static void SetTimes(string path, DateTime created, DateTime written)
        { File.SetCreationTimeUtc(path, created); File.SetLastWriteTimeUtc(path, written); }

        private static byte[] Filled(int count, byte value)
        { byte[] bytes = new byte[count]; for (int i = 0; i < bytes.Length; i++) bytes[i] = value; return bytes; }

        private static bool BytesEqual(byte[] a, byte[] b)
        { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }

        private static void MakeHardLink(string alias, string original)
        {
            try
            {
                if (!CreateHardLink(alias, original, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new SkipTestException("当前文件系统或权限不能创建真实硬链接：" + error + " / " + new Win32Exception(error).Message);
                }
            }
            catch (DllNotFoundException ex) { throw new SkipTestException("硬链接 API 不可用：" + ex.Message); }
            catch (EntryPointNotFoundException ex) { throw new SkipTestException("硬链接入口不可用：" + ex.Message); }
            Need((File.GetAttributes(alias) & FileAttributes.ReparsePoint) == 0, "硬链接 fixture 不得退化为符号链接");
        }

        private sealed class FileSnapshot
        {
            private readonly byte[] bytes;
            private readonly DateTime created, written;
            private readonly FileAttributes attributes;
            internal FileSnapshot(string path)
            { bytes = File.ReadAllBytes(path); created = File.GetCreationTimeUtc(path); written = File.GetLastWriteTimeUtc(path); attributes = File.GetAttributes(path); }
            internal void AssertUnchanged(string path)
            {
                Need(File.Exists(path), "只读检查或保护边界不能删除文件：" + path);
                Need(BytesEqual(bytes, File.ReadAllBytes(path)), "文件内容变化：" + path);
                Need(File.GetCreationTimeUtc(path) == created && File.GetLastWriteTimeUtc(path) == written, "文件创建或修改时间变化：" + path);
                Need(File.GetAttributes(path) == attributes, "文件属性变化：" + path);
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly DateTime NowUtc = DateTime.UtcNow;
            internal readonly string BasePath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tests", "_tmp", "cleanup_" + Guid.NewGuid().ToString("N")));
            private readonly List<string> directoryLinks = new List<string>();
            private bool created;
            internal DateTime OldUtc { get { return NowUtc.AddDays(-8); } }

            internal void Create()
            {
                // 防止 tests/_tmp 或当前目录的既有联接把 fixture 写入引到测试区之外。
                string current = BasePath;
                while (!string.IsNullOrEmpty(current))
                {
                    if (Directory.Exists(current) || File.Exists(current)) Need(!Junction.IsReparsePoint(current), "fixture 祖先不可为重解析点：" + current);
                    current = Path.GetDirectoryName(current);
                }
                Need(!Directory.Exists(BasePath) && !File.Exists(BasePath), "唯一 fixture 路径已存在：" + BasePath);
                Directory.CreateDirectory(BasePath); created = true;
            }

            internal string GetPath(string relative)
            {
                string path = Path.GetFullPath(Path.Combine(BasePath, relative));
                Need(path.StartsWith(BasePath + "\\", StringComparison.OrdinalIgnoreCase), "路径超出本项 fixture：" + path); return path;
            }

            internal string MakeFile(string relative, int length, byte value)
            {
                string path = GetPath(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, Filled(length, value)); SetTimes(path, OldUtc, OldUtc);
                Need(File.GetCreationTimeUtc(path) <= NowUtc.AddDays(-8) && File.GetLastWriteTimeUtc(path) <= NowUtc.AddDays(-8), "有效 fixture 必须同时具备 8 天前的创建及修改时间：" + path); return path;
            }

            internal void MakeDirectoryLink(string link, string target)
            {
                Need(link.StartsWith(BasePath + "\\", StringComparison.OrdinalIgnoreCase) && target.StartsWith(BasePath + "\\", StringComparison.OrdinalIgnoreCase), "目录联接的两端都必须属于本项 fixture");
                // 在尝试创建之前登记，失败时的残留重解析点也会先被非递归移除。
                directoryLinks.Add(link); string error;
                if (!Junction.Create(link, target, out error)) throw new SkipTestException("当前环境无法创建目录联接：" + error);
                Need(Junction.IsReparsePoint(link) && Junction.Verify(link, target), "创建成功的目录联接必须验证指向");
            }

            public void Dispose()
            {
                if (!created) return;
                List<string> errors = new List<string>();
                // 先移除已登记的目录联接本身，再清理普通目录。不能使用 Directory.Delete(..., true)。
                for (int i = directoryLinks.Count - 1; i >= 0; i--)
                {
                    string link = directoryLinks[i];
                    try { if (Junction.IsReparsePoint(link)) Directory.Delete(link, false); }
                    catch (Exception ex) { errors.Add(link + " | " + ex.ToString()); }
                }
                try { DeleteOwnedEntry(BasePath); } catch (Exception ex) { errors.Add(ex.ToString()); }
                if (errors.Count != 0) throw new IOException("fixture 清理未完成：" + string.Join(" | ", errors));
            }

            private void DeleteOwnedEntry(string path)
            {
                string canonical = Path.GetFullPath(path);
                Need(SamePath(canonical, BasePath) || canonical.StartsWith(BasePath + "\\", StringComparison.OrdinalIgnoreCase), "拒绝清理 fixture 之外的路径：" + canonical);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(canonical); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(canonical, false); else File.Delete(canonical);
                    return; // 绝不能枚举或修改重解析点目标。
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    foreach (string child in Directory.GetFileSystemEntries(canonical)) DeleteOwnedEntry(child);
                    Directory.Delete(canonical, false);
                }
                else
                {
                    if ((attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) File.SetAttributes(canonical, attributes & ~(FileAttributes.ReadOnly | FileAttributes.System));
                    File.Delete(canonical); // 普通文件或硬链接均只删除当前 fixture 名字。
                }
            }
        }
    }
}
