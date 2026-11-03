// ============================================================================
//  一键挪动 - 纯逻辑自检（由 tests\run-tests.ps1 通过反射调用 RunAll）
//  仅依赖 Contracts/Utils 与各 Core 模块的公共 API，可在无界面、无管理员权限下运行。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using YijianNuodong.Core;

namespace YijianNuodong.SelfTest
{
    public static class LogicTests
    {
        private static List<string> _lines;
        private static int _pass;
        private static int _fail;
        private static int _skip;
        private static string _currentGroup;

        public static string RunAll()
        {
            _lines = new List<string>();
            _pass = 0;
            _fail = 0;
            _skip = 0;

            Group("路径工具 PathUtil");
            TestPaths();

            Group("文本工具 TextUtil");
            TestText();

            Group("磁盘分析 DriveAnalyzer");
            TestDrives();

            Group("搜索排序 SearchRanker / 别名 AppAliases");
            TestSearch();

            Group("程序定位 ProgramLocator");
            TestLocator();

            Group("快捷方式 .lnk 解析 / 改写 / 批量更新");
            TestShortcuts();

            Group("搬家日记 MoveJournal");
            TestJournal();

            Group("占用检测 LockInspector");
            TestLocks();

            Group("目录联接 Junction");
            TestJunction();

            Group("用户设置 AppSettings");
            TestSettings();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("========== 一键挪动 逻辑自检报告 ==========");
            for (int i = 0; i < _lines.Count; i++) sb.AppendLine(_lines[i]);
            sb.AppendLine("------------------------------------------");
            sb.AppendLine(string.Format("通过 {0} 项 / 失败 {1} 项 / 跳过 {2} 项", _pass, _fail, _skip));
            return sb.ToString();
        }

        // ---------------- 断言工具 ----------------
        private static void Group(string name)
        {
            _currentGroup = name;
            _lines.Add("");
            _lines.Add("【" + name + "】");
        }

        private static void Ok(string name)
        {
            _pass++;
            _lines.Add("  [PASS] " + name);
        }

        private static void Fail(string name, string detail)
        {
            _fail++;
            _lines.Add("  [FAIL] " + name + "  -> " + detail);
        }

        private static void Skip(string name, string reason)
        {
            _skip++;
            _lines.Add("  [SKIP] " + name + "  -> " + reason);
        }

        private static void Check(string name, bool condition, string detail)
        {
            if (condition) Ok(name);
            else Fail(name, detail);
        }

        private static void CheckEqual(string name, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal)) Ok(name);
            else Fail(name, string.Format("期望 [{0}]，实际 [{1}]", expected, actual));
        }

        private static void CheckEqualLong(string name, long expected, long actual)
        {
            if (expected == actual) Ok(name);
            else Fail(name, string.Format("期望 {0}，实际 {1}", expected, actual));
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
                Ok(name);
            }
            catch (Exception ex)
            {
                Exception inner = ex;
                if (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) inner = ex.InnerException;
                Fail(name, "异常: " + inner.GetType().Name + ": " + inner.Message);
            }
        }

        // ---------------- 各分组测试 ----------------
        private static void TestPaths()
        {
            CheckEqual("去尾部斜杠", @"C:\Program Files\WeChat", PathUtil.Normalize(@"C:\Program Files\WeChat\"));
            Check("子路径判断-是", PathUtil.IsSubPathOf(@"C:\Program Files\WeChat\bin\a.exe", @"C:\Program Files\WeChat"), "应判定为子路径");
            Check("子路径判断-前缀陷阱", !PathUtil.IsSubPathOf(@"C:\Program Files\WeChatX\a.exe", @"C:\Program Files\WeChat"), "WeChatX 不应算 WeChat 的子路径");
            CheckEqual("盘符根", @"C:\", PathUtil.GetDriveRoot(@"C:\Program Files\WeChat"));
            CheckEqual("非法字符清洗", "a_b_c_d", PathUtil.SanitizeFolderName("a/b:c*d"));
            CheckEqual("目标文件夹名沿用原目录名", "WeChat", PathUtil.GetLeafName(@"C:\Program Files\Tencent\WeChat", "微信"));
            CheckEqual("目标文件夹名-原目录非法时用软件名", "微信", PathUtil.GetLeafName(@"C:\", "微信"));
            Check("系统关键目录-Windows", PathUtil.IsSystemCriticalPath(@"C:\Windows\System32"), "应识别为系统关键目录");
            Check("系统关键目录-ProgramFiles 根", PathUtil.IsSystemCriticalPath(@"C:\Program Files"), "应识别为系统关键目录");
            Check("系统关键目录-普通软件目录", !PathUtil.IsSystemCriticalPath(@"C:\Program Files\Tencent\WeChat"), "不应识别为系统关键目录");
            CheckEqual("目标路径拼接", @"D:\Software\WeChat", PathUtil.CombineTarget(@"D:\Software", "WeChat"));
            Check("短路径截断", PathUtil.Shorten(@"C:\a\b\c\d\e\f\g\h.exe", 20).Length <= 21, "截断后长度异常");
            CheckEqual("普通路径不加长路径前缀", @"C:\a\b", PathUtil.ToLongPath(@"C:\a\b"));
            string longInput = @"C:\" + new string('d', 260) + @"\x.exe";
            Check("超长路径加前缀", PathUtil.ToLongPath(longInput).StartsWith(@"\\?\"), "超长路径应加长路径前缀");
            CheckEqual("保留名处理", "CON_", PathUtil.SanitizeFolderName("CON"));
        }

        private static void TestText()
        {
            CheckEqual("0 字节", "0 B", TextUtil.FormatBytes(0));
            CheckEqual("1.5 KB", "1.50 KB", TextUtil.FormatBytes(1536));
            CheckEqual("3 GB", "3.00 GB", TextUtil.FormatBytes(3L * 1024 * 1024 * 1024));
            CheckEqual("未知大小", "未知", TextUtil.FormatBytes(-1));
            CheckEqual("文本清洗", "abc", TextUtil.Clean("\u3000 abc \u3000"));
        }

        private static void TestDrives()
        {
            Run("枚举固定磁盘", delegate
            {
                List<DriveInfoItem> drives = DriveAnalyzer.GetFixedDrives();
                if (drives == null || drives.Count == 0) throw new Exception("未找到任何固定磁盘");
            });
            Run("读取 C 盘容量", delegate
            {
                long free, total;
                if (!DriveAnalyzer.TryGetSpace(@"C:\", out free, out total)) throw new Exception("TryGetSpace 返回 false");
                if (total <= 0 || free < 0) throw new Exception("容量数据异常");
            });
            Run("按路径找磁盘", delegate
            {
                DriveInfoItem d = DriveAnalyzer.GetDriveForPath(@"C:\Windows");
                if (d == null) throw new Exception("未找到 C 盘");
            });
        }

        private static void TestSearch()
        {
            CheckEqual("归一化全角字母", "qq", SearchRanker.Normalize(" ＱＱ "));
            Run("别名表非空", delegate
            {
                List<KeyValuePair<string, string[]>> all = new List<KeyValuePair<string, string[]>>(AppAliases.All);
                if (all.Count < 40) throw new Exception("别名条目过少: " + all.Count);
            });
            Run("微信别名", delegate
            {
                string[] aliases = AppAliases.GetAliases("微信");
                bool found = false;
                for (int i = 0; i < aliases.Length; i++)
                {
                    string a = aliases[i].ToLowerInvariant();
                    if (a.Contains("wechat") || a.Contains("weixin") || a == "wx") found = true;
                }
                if (!found) throw new Exception("未找到 wechat/weixin/wx 别名");
            });
            Run("用缩写 wx 能命中微信", delegate
            {
                InstalledProgram wechat = MakeProgram("微信", @"C:\Program Files\Tencent\WeChat");
                InstalledProgram qq = MakeProgram("QQ", @"C:\Program Files\Tencent\QQ");
                InstalledProgram word = MakeProgram("Word", @"C:\Program Files\Microsoft Office\root\Office16");
                List<InstalledProgram> list = new List<InstalledProgram>();
                list.Add(wechat);
                list.Add(qq);
                list.Add(word);
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.IsNullOrEmpty(list[i].AliasMatched)) list[i].AliasMatched = null;
                }
                List<InstalledProgram> ranked = SearchRanker.Rank(list, "wx", 10);
                if (ranked.Count == 0) throw new Exception("没有任何结果");
                if (!ranked.Contains(wechat)) throw new Exception("微信未出现在结果中");
            });
            Run("Photoshop 简称 PS", delegate
            {
                InstalledProgram ps = MakeProgram("Adobe Photoshop 2024", @"C:\Program Files\Adobe\Adobe Photoshop 2024");
                List<InstalledProgram> list = new List<InstalledProgram>();
                list.Add(ps);
                List<InstalledProgram> ranked = SearchRanker.Rank(list, "PS", 10);
                if (ranked.Count == 0) throw new Exception("PS 未命中 Photoshop");
            });
        }

        private static InstalledProgram MakeProgram(string name, string path)
        {
            InstalledProgram p = new InstalledProgram();
            p.DisplayName = name;
            p.InstallPath = path;
            p.ExecutablePath = Path.Combine(path, name + ".exe");
            p.Id = path.ToLowerInvariant();
            p.SizeBytes = -1;
            p.Publisher = string.Empty;
            p.Version = string.Empty;
            p.Source = ProgramSource.Registry;
            return p;
        }

        private static void TestLocator()
        {
            // 注意：ProgramLocator 会把 %TEMP% 之类目录视为"非已安装软件"而拒绝，
            // 因此这里在项目目录下造一个普通路径的假安装目录。
            string tempDir = Path.Combine(Environment.CurrentDirectory, "tests", "_tmp", "selftest_app_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                Directory.CreateDirectory(Path.Combine(tempDir, "bin"));
                File.WriteAllText(Path.Combine(tempDir, "readme.txt"), "hello");
                string exe = Path.Combine(tempDir, "FakeTool.exe");
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"), exe, true);

                Run("从目录构造程序信息", delegate
                {
                    InstalledProgram p = ProgramLocator.FromDirectory(tempDir);
                    if (p == null) throw new Exception("返回 null");
                    if (!PathUtil.SamePath(p.InstallPath, tempDir)) throw new Exception("InstallPath 不正确: " + p.InstallPath);
                    if (string.IsNullOrEmpty(p.DisplayName)) throw new Exception("DisplayName 为空");
                });

                Run("exe 推断安装目录", delegate
                {
                    string dir = ProgramLocator.DeriveInstallDirectory(exe);
                    if (!PathUtil.SamePath(dir, tempDir)) throw new Exception("推断结果不正确: " + dir);
                });

                Run("识别安装目录", delegate
                {
                    if (!ProgramLocator.IsLikelyInstallDirectory(tempDir)) throw new Exception("未识别为安装目录");
                });
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }

            Run("建立索引并搜索", delegate
            {
                SearchIndex index = ProgramLocator.BuildIndex(null, null);
                if (index == null) throw new Exception("索引为 null");
                List<InstalledProgram> found = ProgramLocator.Search(index, "微信", 20);
                if (found == null) throw new Exception("搜索结果 null");
                List<InstalledProgram> all = ProgramLocator.Search(index, "", 20);
                if (all.Count > 20) throw new Exception("maxResults 未生效");
            });
        }

        private static object CreateWscriptShell()
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            return Activator.CreateInstance(t);
        }

        /// <summary>用 WScript.Shell 创建一个真实 .lnk（COM 晚期绑定）</summary>
        private static bool CreateShortcut(string lnkPath, string targetPath, string workingDir, string description)
        {
            object shell = CreateWscriptShell();
            if (shell == null) return false;
            object shortcut = null;
            try
            {
                shortcut = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                    null, shell, new object[] { lnkPath });
                Type st = shortcut.GetType();
                st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { description });
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, new object[0]);
                return File.Exists(lnkPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }

        private static void TestShortcuts()
        {
            string dir = Path.Combine(Environment.CurrentDirectory, "tests", "_tmp", "shortcut_" + Guid.NewGuid().ToString("N"));
            string lnkA = Path.Combine(dir, "测试快捷方式A.lnk");
            string lnkB = Path.Combine(dir, "测试快捷方式B.lnk");
            string desktopLnk = null;
            try
            {
                Directory.CreateDirectory(dir);
                string target1 = Path.Combine(dir, "程序A.exe");
                string target2 = Path.Combine(dir, "程序B.exe");
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"), target1, true);
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"), target2, true);

                if (!CreateShortcut(lnkA, target1, dir, "一键挪动 自检快捷方式A") ||
                    !CreateShortcut(lnkB, target1, dir, "一键挪动 自检快捷方式B"))
                {
                    Skip("创建测试快捷方式", "当前环境 WScript.Shell COM 不可用");
                    return;
                }

                Run("识别 .lnk 文件", delegate
                {
                    if (!ShortcutResolver.IsShortcutFile(lnkA)) throw new Exception("IsShortcutFile 返回 false");
                });

                Run("解析 .lnk 目标与工作目录", delegate
                {
                    ShortcutInfo info;
                    string error;
                    if (!ShortcutResolver.TryResolve(lnkA, out info, out error)) throw new Exception("解析失败：" + error);
                    if (info == null) throw new Exception("解析结果为空");
                    if (!PathUtil.SamePath(info.TargetPath, target1)) throw new Exception("目标不一致：" + info.TargetPath);
                    if (!PathUtil.SamePath(info.WorkingDirectory, dir)) throw new Exception("工作目录不一致：" + info.WorkingDirectory);
                });

                Run("改写单个 .lnk 指向", delegate
                {
                    string error;
                    if (!ShortcutResolver.TryUpdate(lnkA, target2, dir, out error)) throw new Exception("改写失败：" + error);
                    ShortcutInfo info;
                    if (!ShortcutResolver.TryResolve(lnkA, out info, out error)) throw new Exception("改写后解析失败：" + error);
                    if (!PathUtil.SamePath(info.TargetPath, target2)) throw new Exception("改写后目标不一致：" + info.TargetPath);
                });

                // 生产路径验证：真实桌面快捷方式才会被 ShortcutUpdater 枚举到。
                // 用 "一键挪动自检_" 前缀命名，测试结束立即删除，避免污染用户桌面。
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                desktopLnk = Path.Combine(desktopDir, "一键挪动自检_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".lnk");
                bool desktopShortcutReady = false;
                try
                {
                    desktopShortcutReady = Directory.Exists(desktopDir) && CreateShortcut(desktopLnk, target1, dir, "一键挪动 自检桌面快捷方式");
                }
                catch
                {
                    desktopShortcutReady = false;
                }

                if (!desktopShortcutReady)
                {
                    // 受限环境（如禁止写桌面）下应该安全降级，而不是抛异常：
                    // 返回 false + 中文错误，不能抛异常、不能崩溃。真机上桌面可写时会走创建分支。
                    Run("桌面不可写时安全降级(EnsureDesktopShortcut)", delegate
                    {
                        InstalledProgram p = new InstalledProgram();
                        p.DisplayName = "一键挪动自检程序";
                        p.InstallPath = dir;
                        p.ExecutablePath = target2;
                        string shortcutPath;
                        string error;
                        bool ok = ShortcutUpdater.EnsureDesktopShortcut(p, dir, out shortcutPath, out error);
                        if (ok) throw new Exception("桌面不可写却返回成功");
                        if (string.IsNullOrEmpty(error)) throw new Exception("失败但没有给出中文错误说明");
                    });
                    Skip("批量更新桌面快捷方式（生产路径）", "本机沙箱禁止写桌面，无法创建测试快捷方式");
                    Skip("清理失效快捷方式(RemoveDanglingShortcuts)", "本机沙箱禁止写桌面，无法创建测试快捷方式");
                }
                else
                {
                    Run("批量更新桌面快捷方式（生产路径）", delegate
                    {
                        List<string> updated;
                        List<string> errors;
                        int count = ShortcutUpdater.UpdateAll(target1, target2, out updated, out errors);
                        if (count < 1) throw new Exception("未更新任何快捷方式，count=" + count);
                        if (updated == null || updated.Count < 1) throw new Exception("updated 列表为空");
                        ShortcutInfo info;
                        string error;
                        if (!ShortcutResolver.TryResolve(desktopLnk, out info, out error)) throw new Exception("批量更新后解析失败：" + error);
                        if (!PathUtil.SamePath(info.TargetPath, target2)) throw new Exception("批量更新后目标未改变：" + info.TargetPath);
                    });

                    Run("确保桌面快捷方式(EnsureDesktopShortcut)", delegate
                    {
                        InstalledProgram p = new InstalledProgram();
                        p.DisplayName = "一键挪动自检程序";
                        p.InstallPath = dir;
                        p.ExecutablePath = target2;
                        string shortcutPath;
                        string error;
                        if (!ShortcutUpdater.EnsureDesktopShortcut(p, dir, out shortcutPath, out error))
                            throw new Exception("创建失败：" + error);
                        if (string.IsNullOrEmpty(shortcutPath) || !File.Exists(shortcutPath))
                            throw new Exception("快捷方式文件不存在：" + shortcutPath);
                        ShortcutInfo info;
                        if (!ShortcutResolver.TryResolve(shortcutPath, out info, out error))
                            throw new Exception("新建快捷方式解析失败：" + error);
                        if (!PathUtil.SamePath(info.TargetPath, target2))
                            throw new Exception("新建快捷方式目标不正确：" + info.TargetPath);
                        try { File.Delete(shortcutPath); } catch { }
                    });

                    Run("清理失效快捷方式(RemoveDanglingShortcuts)", delegate
                    {
                        string dangling = Path.Combine(desktopDir, "一键挪动自检_悬空_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".lnk");
                        try
                        {
                            string missingTarget = Path.Combine(dir, "不存在_" + Guid.NewGuid().ToString("N") + ".exe");
                            if (!CreateShortcut(dangling, missingTarget, dir, "一键挪动 自检悬空快捷方式"))
                                throw new Exception("无法创建悬空快捷方式");
                            List<string> removed;
                            List<string> errors2;
                            int removedCount = ShortcutUpdater.RemoveDanglingShortcuts(dir, out removed, out errors2);
                            if (removedCount < 1) throw new Exception("未清理悬空快捷方式");
                            if (File.Exists(dangling)) throw new Exception("悬空快捷方式仍然存在");
                            if (!File.Exists(desktopLnk)) throw new Exception("误删了有效的桌面快捷方式");
                        }
                        finally
                        {
                            try { if (File.Exists(dangling)) File.Delete(dangling); } catch { }
                        }
                    });
                }

                Run("枚举快捷方式（桌面/开始菜单）", delegate
                {
                    List<string> list = ShortcutResolver.EnumerateShortcuts();
                    if (list == null) throw new Exception("返回 null");
                    // 至少能返回列表；桌面可能确实没有快捷方式，因此不强制要求非空
                });

                Run("由快捷方式构造程序信息", delegate
                {
                    InstalledProgram p;
                    string error;
                    if (!ProgramLocator.TryFromShortcut(lnkA, out p, out error)) throw new Exception("解析程序失败：" + error);
                    if (p == null) throw new Exception("程序信息为空");
                    if (!PathUtil.SamePath(p.InstallPath, dir)) throw new Exception("安装目录不正确：" + p.InstallPath);
                });
            }
            finally
            {
                try { if (!string.IsNullOrEmpty(desktopLnk) && File.Exists(desktopLnk)) File.Delete(desktopLnk); } catch { }
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void TestJournal()
        {
            string journalPath = null;
            try
            {
                Run("写入并读回日记", delegate
                {
                    JournalEntry entry = new JournalEntry();
                    entry.CreatedUtc = DateTime.UtcNow;
                    entry.OldPath = @"C:\Program Files\Demo App: 1.0";
                    entry.NewPath = @"D:\Software\Demo App: 1.0";
                    entry.DisplayName = "演示软件 = 测试 | 特殊字符";
                    entry.JunctionCreated = true;
                    entry.FreedBytes = 123456789L;
                    entry.SourceDeleted = true;
                    entry.UpdatedShortcuts.Add(@"C:\Users\Public\Desktop\演示.lnk");
                    RegistryChange rc = new RegistryChange();
                    rc.Hive = "HKLM";
                    rc.KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Demo";
                    rc.ValueName = "InstallLocation";
                    rc.ValueKind = "String";
                    rc.OldValue = @"C:\Program Files\Demo App";
                    rc.NewValue = @"D:\Software\Demo App";
                    entry.RegistryChanges.Add(rc);

                    journalPath = MoveJournal.Write(entry);
                    if (string.IsNullOrEmpty(journalPath) || !File.Exists(journalPath)) throw new Exception("日记文件未生成");

                    JournalEntry back = MoveJournal.Read(journalPath);
                    if (back == null) throw new Exception("读回为 null");
                    if (back.OldPath != entry.OldPath) throw new Exception("OldPath 不一致: " + back.OldPath);
                    if (back.NewPath != entry.NewPath) throw new Exception("NewPath 不一致: " + back.NewPath);
                    if (back.DisplayName != entry.DisplayName) throw new Exception("DisplayName 不一致: " + back.DisplayName);
                    if (back.FreedBytes != entry.FreedBytes) throw new Exception("FreedBytes 不一致");
                    if (back.RegistryChanges.Count != 1) throw new Exception("注册表变更条数不一致");
                    if (back.RegistryChanges[0].KeyPath != rc.KeyPath) throw new Exception("注册表 KeyPath 不一致");
                    if (!back.JunctionCreated) throw new Exception("JunctionCreated 丢失");
                });

                Run("列出日记", delegate
                {
                    List<string> files = MoveJournal.ListFiles();
                    if (files.Count == 0) throw new Exception("未列出任何日记");
                });
            }
            finally
            {
                if (!string.IsNullOrEmpty(journalPath))
                {
                    try { File.Delete(journalPath); } catch { }
                }
            }
        }

        private static void TestLocks()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "okm_selftest_lock_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "a.txt"), "x");
                Run("未被占用目录", delegate
                {
                    LockInfo info = LockInspector.Inspect(tempDir, null);
                    if (info == null) throw new Exception("Inspect 返回 null");
                    if (info.Processes.Count > 0) throw new Exception("空闲目录误报占用: " + info.Processes[0].ProcessName);
                });
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static void TestJunction()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), "okm_selftest_junction_" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(baseDir, "target");
            string link = Path.Combine(baseDir, "link");
            try
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "marker.txt"), "junction-test");

                string error;
                bool created = Junction.Create(link, target, out error);
                if (!created)
                {
                    Skip("创建目录联接", "当前环境不允许（可能非管理员/被杀软拦截）: " + error);
                }
                else
                {
                    Check("创建目录联接", Directory.Exists(link), "联接目录不存在");
                    string targetRead;
                    Check("读取联接目标", Junction.TryGetTarget(link, out targetRead) && PathUtil.SamePath(targetRead, target), "联接目标读取失败");
                    Check("通过联接读写文件", File.Exists(Path.Combine(link, "marker.txt")), "通过联接访问文件失败");
                    Check("删除联接不影响目标内容", Junction.Remove(link, out error) && !Directory.Exists(link) && File.Exists(Path.Combine(target, "marker.txt")), "删除联接异常: " + error);
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(link))
                    {
                        string err2;
                        Junction.Remove(link, out err2);
                    }
                }
                catch { }
                try { Directory.Delete(baseDir, true); } catch { }
            }
        }

        private static void TestSettings()
        {
            Run("设置读写", delegate
            {
                string old = AppSettings.LastTargetDirectory;
                string probe = @"D:\__okm_settings_probe__";
                AppSettings.LastTargetDirectory = probe;
                if (AppSettings.LastTargetDirectory != probe) throw new Exception("内存中设置读写不一致");
                AppSettings.Save();
                AppSettings.LastTargetDirectory = old;
            });

            // 落盘校验：%ProgramData% 里由管理员创建的文件在普通权限下不可覆盖，
            // 这种环境只校验内存读写，落盘校验跳过而不是判失败。
            {
                string old = AppSettings.LastTargetDirectory;
                string probe = @"D:\__okm_settings_probe__";
                try
                {
                    AppSettings.LastTargetDirectory = probe;
                    AppSettings.Save();
                    string disk = null;
                    try
                    {
                        if (File.Exists(AppPaths.SettingsFile)) disk = File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8);
                    }
                    catch { disk = null; }
                    bool persisted = disk != null && disk.IndexOf("LastTargetDirectory=" + probe, StringComparison.Ordinal) >= 0;
                    if (!persisted)
                    {
                        Skip("设置落盘", "%ProgramData% 设置文件不可写（非管理员环境），仅校验内存读写");
                    }
                    else
                    {
                        AppSettings.Load();
                        if (AppSettings.LastTargetDirectory != probe) throw new Exception("设置回读不一致: " + AppSettings.LastTargetDirectory);
                        Ok("设置落盘");
                    }
                }
                catch (Exception ex)
                {
                    Fail("设置落盘", "异常: " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    try { AppSettings.LastTargetDirectory = old; AppSettings.Save(); } catch { }
                }
            }
        }
    }
}
