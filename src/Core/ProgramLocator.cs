// T1 实现：已安装程序扫描 / 定位 / 搜索
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace YijianNuodong.Core
{
    /// <summary>
    /// 已安装程序发现与定位：
    /// 1) 注册表卸载项 + App Paths；2) 桌面/开始菜单快捷方式；3) 正在运行的进程；4) 常见安装目录扫描。
    /// 所有来源按安装目录去重合并；全部操作容错，失败写日志并返回中文说明，不向调用方抛异常。
    /// </summary>
    public static class ProgramLocator
    {
        // ------------------------------ 扫描参数 ------------------------------
        private const int ProgramFilesScanDepth = 3;      // Program Files 下最多下钻 3 层
        private const int LocalAppDataScanDepth = 2;      // %LOCALAPPDATA%\Programs / %APPDATA% 下最多下钻 2 层
        private const int MaxScanDirectories = 4000;      // 目录扫描总量上限，防止卡死
        private const int ProgressInterval = 25;          // 每处理 N 条回报一次进度

        /// <summary>系统盘根目录（"C:\"），用于排序时"C 盘优先"</summary>
        private static readonly string SystemDriveRoot =
            PathUtil.GetDriveRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        // “KB 补丁”名称，例如 Security Update for Windows (KB5031234)
        private static readonly Regex PatchKbRegex =
            new Regex(@"\bKB\d{5,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 纯版本号文件夹，例如 1.2.3 / v2.0 / 2024（4 位以内纯数字，避免把用户名等长数字目录当版本号）
        private static readonly Regex VersionFolderRegex =
            new Regex(@"^v?(\d{1,4}|\d+(\.\d+)+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Office 的版本子目录，例如 Office16
        private static readonly Regex OfficeFolderRegex =
            new Regex(@"^office\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 安装缓存里常见的 GUID 子目录，例如 Installer2\Display.NvApp.{AEF12F35-...}
        private static readonly Regex GuidSegmentRegex =
            new Regex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}",
                      RegexOptions.Compiled);

        // 显示名末尾的版本号，例如 "Everything 1.4.1.969"
        private static readonly Regex TrailingVersionRegex =
            new Regex(@"[\s\-_]+v?\d+(\.\d+)+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 显示名末尾的位数说明，例如 "Python 3.13 (64-bit)"、"Notepad++ (32-bit x86)"、"（64 位）"
        private static readonly Regex TrailingBitnessRegex =
            new Regex(@"\s*[\(（][^\(\)（）]*(x64|x86|64\s*位|32\s*位|64-bit|32-bit)[^\(\)（）]*[\)）]\s*$",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 显示名末尾残留的 "version" / "版本" 字样
        private static readonly Regex TrailingVersionWordRegex =
            new Regex(@"[\s\-_]*(version|版本)[\s\-_]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>不能作为安装目录（不收录、也不再往下扫）的文件夹名（小写）</summary>
        private static readonly string[] IgnoredFolderNames = new string[]
        {
            "common files", "common files (x86)", "windowsapps", "modifiablewindowsapps",
            "microsoft", "windows", "internet explorer", "windows defender", "windows mail",
            "windows media player", "windows nt", "windows photo viewer", "windowspowershell",
            "microsoft.net", "dotnet", "installshield installation information", "package cache",
            "$recycle.bin", "system volume information", "boot", "recovery",
            "program files", "program files (x86)", "temp", "tmp",
            // 多为安装目录内部的子目录，避免把 ...\App\bin 误当安装根
            "bin", "bin32", "bin64", "binaries", "x64", "x86", "x86_64", "amd64",
            "win32", "win64", "cmd", "lib", "libs", "lib64", "jre", "jre64",
            "runtime", "framework", "plugins", "plugin", "modules", "node_modules",
            "resources", "locales", "assets", "application", "apps", "updater", "update",
            "cef", "usr", "mingw64", "ucrt64", "clang64", "libexec", "share", "etc",
            "include", "sbin", "doc", "docs", "man", "installer2"
        };

        /// <summary>
        /// 出现在安装路径任意一层就不算独立软件的目录名（小写）。
        /// 注意这里不包含 "program files"：D:\Program Files\App 仍是合法安装位置，
        /// 只有安装根正好是 Program Files 本身时才由 IgnoredFolderNames 拦截。
        /// </summary>
        private static readonly string[] ForbiddenPathSegments = new string[]
        {
            "common files", "common files (x86)", "windows", "windowsapps", "modifiablewindowsapps",
            "microsoft", "microsoft.net", "dotnet", "package cache", "installer2",
            "installshield installation information", "$recycle.bin", "system volume information",
            "boot", "recovery", "temp", "tmp",
            "internet explorer", "windows defender", "windows mail", "windows media player",
            "windows nt", "windows photo viewer", "windowspowershell"
        };

        /// <summary>主程序选择时要排除的 exe 关键字（小写）</summary>
        private static readonly string[] IgnoredExeKeywords = new string[]
        {
            "unins", "uninstall", "setup", "install", "update", "updater", "crash", "helper",
            "report", "repair", "diagnostic", "elevat", "vcredist", "vc_redist", "dxsetup",
            "dotnet", "installer", "register", "regsvr", "reboot", "notif", "sethc"
        };

        /// <summary>不像正常软件名的通用显示名（小写）</summary>
        private static readonly string[] BadDisplayNames = new string[]
        {
            "快捷方式", "新建快捷方式", "shortcut", "new shortcut", "launcher", "start", "run",
            "app", "application", "main", "program", "uninstall", "卸载"
        };

        /// <summary>快捷方式文件名里出现这些词时，通常不是软件主入口（而是文档/设置/卸载入口）</summary>
        private static readonly string[] BadShortcutNameKeywords = new string[]
        {
            "docs", "documentation", "manual", "readme", "help", "reset", "settings", "uninstall",
            "remove", "repair", "update", "setup", "install", "configuration", "console",
            "卸载", "文档", "手册", "帮助", "说明", "修复", "安装", "设置"
        };

        // ================================ 公共 API ================================

        /// <summary>
        /// 建立已安装程序索引：注册表卸载项(HKLM/HKCU 32/64) + App Paths + 桌面/开始菜单快捷方式
        /// + 正在运行的进程 + 常见安装目录扫描。按安装目录去重合并。
        /// </summary>
        public static SearchIndex BuildIndex(Func<bool> isCancelled, MoveProgressHandler progress)
        {
            SearchIndex index = new SearchIndex();
            Dictionary<string, InstalledProgram> map =
                new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ReportProgress(progress, "开始扫描已安装的软件…");

                ReportProgress(progress, "读取注册表卸载信息");
                CollectFromUninstallRegistry(map, isCancelled, progress);
                if (IsCanceled(isCancelled)) return Finish(index, map);

                ReportProgress(progress, "读取 App Paths 注册表项");
                CollectFromAppPaths(map, isCancelled, progress);
                if (IsCanceled(isCancelled)) return Finish(index, map);

                ReportProgress(progress, "扫描开始菜单快捷方式");
                CollectFromShortcuts(map, isCancelled, progress);
                if (IsCanceled(isCancelled)) return Finish(index, map);

                ReportProgress(progress, "扫描正在运行的程序");
                CollectFromRunningProcesses(map, isCancelled, progress);
                if (IsCanceled(isCancelled)) return Finish(index, map);

                ReportProgress(progress, "扫描安装目录");
                CollectFromFolderScan(map, isCancelled, progress);

                Log.Info("程序索引建立完成，共 " + map.Count.ToString() + " 项");
            }
            catch (Exception ex)
            {
                Log.Error("建立程序索引时发生异常（已返回已收集结果）", ex);
            }
            return Finish(index, map);
        }

        /// <summary>建立索引（无进度回调）</summary>
        public static SearchIndex BuildIndex()
        {
            return BuildIndex(null, null);
        }

        /// <summary>
        /// 按关键词（含中文通用叫法/别名）搜索并排序，最多返回 maxResults 条。
        /// keyword 为空时按"大小已知优先、C 盘优先、名称排序"返回全部（截断）。
        /// </summary>
        public static List<InstalledProgram> Search(SearchIndex index, string keyword, int maxResults)
        {
            List<InstalledProgram> result = new List<InstalledProgram>();
            if (index == null || index.Programs == null || index.Programs.Count == 0) return result;
            if (maxResults <= 0) maxResults = index.Programs.Count;

            string query = TextUtil.Clean(keyword);
            if (query.Length == 0)
            {
                List<InstalledProgram> all = new List<InstalledProgram>(index.Programs);
                all.Sort(CompareDefaultOrder);
                int count = all.Count < maxResults ? all.Count : maxResults;
                for (int i = 0; i < count; i++) result.Add(all[i]);
                return result;
            }
            try
            {
                return SearchRanker.Rank(index.Programs, query, maxResults);
            }
            catch (Exception ex)
            {
                Log.Error("搜索软件失败（关键词：" + query + "）", ex);
                return result;
            }
        }

        /// <summary>由快捷方式解析出程序信息（供拖入 .lnk 定位使用）</summary>
        public static bool TryFromShortcut(string lnkPath, out InstalledProgram program, out string error)
        {
            program = null;
            error = null;
            try
            {
                if (string.IsNullOrEmpty(lnkPath))
                {
                    error = "快捷方式路径为空";
                    return false;
                }
                string lnk;
                try { lnk = PathUtil.Normalize(lnkPath); }
                catch { lnk = lnkPath; }
                if (!ShortcutResolver.IsShortcutFile(lnk))
                {
                    error = "不是 .lnk 快捷方式文件：" + lnk;
                    return false;
                }

                ShortcutInfo info;
                string resolveError;
                if (!ShortcutResolver.TryResolveLoose(lnk, out info, out resolveError))
                {
                    error = resolveError;
                    return false;
                }

                string target = info == null ? string.Empty : info.TargetPath;
                if (string.IsNullOrEmpty(target))
                {
                    error = "快捷方式没有记录目标路径";
                    return false;
                }

                bool isFile = false;
                bool isDirectory = false;
                try
                {
                    isFile = File.Exists(target);
                    isDirectory = Directory.Exists(target);
                }
                catch
                {
                    isFile = false;
                    isDirectory = false;
                }

                string executablePath = string.Empty;
                string installDirectory = string.Empty;
                if (isFile)
                {
                    string extension = Path.GetExtension(target).ToLowerInvariant();
                    if (extension != ".exe" && extension != ".com" && extension != ".bat" && extension != ".cmd")
                    {
                        error = "快捷方式指向的是文档或其它文件，不是可搬家的程序：" + target;
                        return false;
                    }
                    if (PathUtil.IsSystemCriticalPath(target))
                    {
                        error = "快捷方式指向系统程序，不能搬家：" + target;
                        return false;
                    }
                    executablePath = target;
                    installDirectory = DeriveInstallDirectory(target);
                }
                else if (isDirectory)
                {
                    installDirectory = DeriveInstallDirectory(target);
                }
                else
                {
                    error = "快捷方式指向的目标不存在：" + target;
                    return false;
                }

                if (string.IsNullOrEmpty(installDirectory) || !Directory.Exists(installDirectory))
                {
                    error = "无法从快捷方式推断出有效的安装目录：" + target;
                    return false;
                }
                if (PathUtil.IsSystemCriticalPath(installDirectory) || IsForbiddenInstallPath(installDirectory))
                {
                    error = "快捷方式指向系统目录或共享组件目录，不能搬家：" + installDirectory;
                    return false;
                }

                string displayHint = Path.GetFileNameWithoutExtension(lnk);
                InstalledProgram item = CreateProgramFromDirectory(installDirectory, executablePath, displayHint);
                if (item == null)
                {
                    error = "无法读取程序信息：" + installDirectory;
                    return false;
                }
                item.Source = ProgramSource.Shortcut;
                item.ShortcutPath = lnk;
                item.SizeBytes = -1; // 大小由界面按需计算
                program = item;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("由快捷方式解析程序失败（" + lnkPath + "）", ex);
                error = "解析快捷方式失败：" + ex.Message;
                program = null;
                return false;
            }
        }

        /// <summary>由目录构造程序信息（拖入文件夹/手动输入路径时用）；目录无效时返回 null</summary>
        public static InstalledProgram FromDirectory(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath)) return null;
            try
            {
                string directory = ExpandAndTrim(directoryPath);
                if (directory.Length == 0) return null;
                // 用户拖入的可能是内部子目录（如 ...\App\bin），尽量推断出真正的安装根
                string derived = DeriveInstallDirectory(directory);
                if (derived.Length > 0 && Directory.Exists(derived)) directory = derived;
                if (!Directory.Exists(directory)) return null;
                directory = PathUtil.Normalize(directory);
                if (!IsUsableInstallDirectory(directory)) return null;

                string executablePath = FindMainExecutable(directory, Path.GetFileName(directory));
                if (executablePath.Length == 0 && !IsLikelyInstallDirectory(directory)) return null;

                InstalledProgram item = CreateProgramFromDirectory(directory, executablePath, null);
                if (item == null) return null;
                item.Source = ProgramSource.ManualPath;
                item.SizeBytes = -1;
                return item;
            }
            catch (Exception ex)
            {
                Log.Error("由目录构造程序信息失败（" + directoryPath + "）", ex);
                return null;
            }
        }

        /// <summary>
        /// 由 exe 路径推断安装目录：默认用 exe 所在目录；若目录名是 bin/x64/app-版本号/纯版本号等内部目录，
        /// 则向上找到更像安装根的目录；不会越界到 Program Files 根或盘符根，越界时返回空字符串。
        /// </summary>
        public static string DeriveInstallDirectory(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath)) return string.Empty;
            try
            {
                string path = ExpandAndTrim(executablePath);
                if (path.Length == 0) return string.Empty;

                // 传入的本身就是目录
                if (Directory.Exists(path)) return DeriveFromDirectory(path);

                if (!Path.IsPathRooted(path)) return string.Empty;
                string normalized = PathUtil.Normalize(path);
                if (normalized.Length == 0) return string.Empty;

                string directory;
                try { directory = Path.GetDirectoryName(normalized); }
                catch { return string.Empty; }
                if (string.IsNullOrEmpty(directory)) return string.Empty;
                return DeriveFromDirectory(directory);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>目录看起来是否像一个软件安装目录（含可执行文件、非系统关键目录、名字不像内部子目录）</summary>
        public static bool IsLikelyInstallDirectory(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath)) return false;
            try
            {
                string input = ExpandAndTrim(directoryPath);
                if (input.Length == 0 || !Directory.Exists(input)) return false;
                string directory = PathUtil.Normalize(input);
                if (directory.Length == 0 || directory.Length <= 3) return false;
                if (!IsUsableInstallDirectory(directory)) return false;
                if (IsIgnoredFolderName(Path.GetFileName(directory))) return false;

                string[] files;
                try { files = Directory.GetFiles(PathUtil.ToLongPath(directory), "*.exe"); }
                catch { return false; }
                for (int i = 0; i < files.Length; i++)
                {
                    if (!IsIgnoredExecutable(files[i])) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // ============================ 各来源收集 ============================

        /// <summary>收集注册表卸载项：HKLM/HKCU × 32/64 视图</summary>
        private static void CollectFromUninstallRegistry(Dictionary<string, InstalledProgram> map,
            Func<bool> isCancelled, MoveProgressHandler progress)
        {
            CollectUninstallFromView(RegistryHive.LocalMachine, RegistryView.Registry64, map, isCancelled, progress);
            CollectUninstallFromView(RegistryHive.LocalMachine, RegistryView.Registry32, map, isCancelled, progress);
            CollectUninstallFromView(RegistryHive.CurrentUser, RegistryView.Registry64, map, isCancelled, progress);
            CollectUninstallFromView(RegistryHive.CurrentUser, RegistryView.Registry32, map, isCancelled, progress);
        }

        /// <summary>读取某个注册表视图下的卸载项</summary>
        private static void CollectUninstallFromView(RegistryHive hive, RegistryView view,
            Dictionary<string, InstalledProgram> map, Func<bool> isCancelled, MoveProgressHandler progress)
        {
            RegistryKey baseKey = null;
            RegistryKey uninstallKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                if (baseKey == null) return;
                uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false);
                if (uninstallKey == null) return;

                string[] subNames = uninstallKey.GetSubKeyNames();
                for (int i = 0; i < subNames.Length; i++)
                {
                    if (IsCanceled(isCancelled)) return;
                    RegistryKey subKey = null;
                    try
                    {
                        subKey = uninstallKey.OpenSubKey(subNames[i], false);
                        if (subKey != null)
                        {
                            InstalledProgram item = ReadUninstallEntry(subKey);
                            if (item != null) Merge(map, item);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("读取卸载项失败（" + subNames[i] + "）：" + ex.Message);
                    }
                    finally
                    {
                        if (subKey != null) subKey.Close();
                    }
                    if (progress != null && (i % ProgressInterval == 0))
                    {
                        ReportProgress(progress, "读取注册表卸载信息（" + i.ToString() + "/" + subNames.Length.ToString() + "）");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取注册表卸载信息失败（" + hive.ToString() + " / " + view.ToString() + "）：" + ex.Message);
            }
            finally
            {
                if (uninstallKey != null) uninstallKey.Close();
                if (baseKey != null) baseKey.Close();
            }
        }

        /// <summary>从一条卸载项注册表子键构造程序信息；不需要收录时返回 null</summary>
        private static InstalledProgram ReadUninstallEntry(RegistryKey subKey)
        {
            string displayName = GetRegistryString(subKey, "DisplayName");
            if (displayName.Length == 0) return null;

            // 系统组件 / 组件子项不收录
            if (GetRegistryInt(subKey, "SystemComponent") == 1) return null;
            if (GetRegistryString(subKey, "ParentKeyName").Length > 0) return null;

            string installLocation = GetRegistryString(subKey, "InstallLocation");
            string displayIcon = GetRegistryString(subKey, "DisplayIcon");
            string uninstallString = GetRegistryString(subKey, "UninstallString");
            string publisher = GetRegistryString(subKey, "Publisher");
            string version = GetRegistryString(subKey, "DisplayVersion");

            // 纯补丁（名称带 KB 编号且没有安装目录）不收录
            if (installLocation.Length == 0 && PatchKbRegex.IsMatch(displayName)) return null;
            // 明显是补丁/更新的条目
            if (LooksLikePatch(displayName)) return null;

            string installDirectory = ResolveInstallDirectory(installLocation, displayIcon, uninstallString);
            if (installDirectory.Length == 0) return null;
            if (!IsUsableInstallDirectory(installDirectory)) return null;

            string executablePath = FindMainExecutable(installDirectory, displayName);
            string iconPath = StripIconIndex(displayIcon);
            if (executablePath.Length == 0 && iconPath.Length > 0 && File.Exists(iconPath) &&
                string.Equals(Path.GetExtension(iconPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                executablePath = iconPath;
            }

            InstalledProgram item = new InstalledProgram();
            item.DisplayName = CleanDisplayName(displayName);
            if (item.DisplayName.Length == 0) item.DisplayName = TextUtil.Clean(displayName);
            item.InstallPath = installDirectory;
            item.ExecutablePath = executablePath;
            item.Publisher = CleanPublisher(publisher);
            item.Version = CleanVersion(version);
            item.UninstallString = TextUtil.Clean(uninstallString);
            item.Source = ProgramSource.Registry;

            int estimatedKb = GetRegistryInt(subKey, "EstimatedSize");
            item.SizeBytes = estimatedKb > 0 ? (long)estimatedKb * 1024L : -1;
            return item;
        }

        /// <summary>名称是否为补丁/更新类条目（避免把 KB 补丁当软件）</summary>
        private static bool LooksLikePatch(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return false;
            string name = displayName.ToLowerInvariant();
            if (PatchKbRegex.IsMatch(displayName)) return true;
            if (name.StartsWith("update for ", StringComparison.Ordinal)) return true;
            if (name.StartsWith("security update", StringComparison.Ordinal)) return true;
            if (name.StartsWith("hotfix for ", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>收集 App Paths（HKLM 32/64 + HKCU 32/64）</summary>
        private static void CollectFromAppPaths(Dictionary<string, InstalledProgram> map,
            Func<bool> isCancelled, MoveProgressHandler progress)
        {
            CollectAppPathsFromView(RegistryHive.LocalMachine, RegistryView.Registry64, map, isCancelled, progress);
            CollectAppPathsFromView(RegistryHive.LocalMachine, RegistryView.Registry32, map, isCancelled, progress);
            CollectAppPathsFromView(RegistryHive.CurrentUser, RegistryView.Registry64, map, isCancelled, progress);
            CollectAppPathsFromView(RegistryHive.CurrentUser, RegistryView.Registry32, map, isCancelled, progress);
        }

        private static void CollectAppPathsFromView(RegistryHive hive, RegistryView view,
            Dictionary<string, InstalledProgram> map, Func<bool> isCancelled, MoveProgressHandler progress)
        {
            RegistryKey baseKey = null;
            RegistryKey appPathsKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                if (baseKey == null) return;
                appPathsKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", false);
                if (appPathsKey == null) return;

                string[] subNames = appPathsKey.GetSubKeyNames();
                for (int i = 0; i < subNames.Length; i++)
                {
                    if (IsCanceled(isCancelled)) return;
                    RegistryKey subKey = null;
                    try
                    {
                        subKey = appPathsKey.OpenSubKey(subNames[i], false);
                        if (subKey == null) continue;

                        string exePath = GetRegistryDefaultString(subKey);
                        if (exePath.Length == 0) exePath = GetRegistryString(subKey, "Path");
                        string pathValue = GetRegistryString(subKey, "Path");
                        if (exePath.Length == 0 && pathValue.Length == 0) continue;

                        string executablePath = ExpandAndTrim(exePath);
                        string installDirectory = string.Empty;
                        if (executablePath.Length > 0 && File.Exists(executablePath))
                        {
                            installDirectory = DeriveInstallDirectory(executablePath);
                        }
                        if (installDirectory.Length == 0 && pathValue.Length > 0)
                        {
                            string directory = ExpandAndTrim(pathValue);
                            if (directory.Length > 0 && Directory.Exists(directory))
                            {
                                installDirectory = PathUtil.Normalize(directory);
                            }
                        }
                        if (installDirectory.Length == 0 || !IsUsableInstallDirectory(installDirectory)) continue;

                        // 注册表里常见 MsiExec.exe、卸载程序等，不作为软件收录
                        if (IsIgnoredExecutable(subNames[i]) || IsIgnoredExecutable(executablePath)) continue;

                        string displayName = CleanDisplayName(Path.GetFileNameWithoutExtension(subNames[i]));
                        if (displayName.Length == 0) displayName = Path.GetFileNameWithoutExtension(subNames[i]);

                        InstalledProgram item = new InstalledProgram();
                        item.DisplayName = displayName;
                        item.InstallPath = installDirectory;
                        item.ExecutablePath = File.Exists(executablePath) ? executablePath : FindMainExecutable(installDirectory, displayName);
                        item.Source = ProgramSource.AppPaths;
                        item.SizeBytes = -1;
                        Merge(map, item);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("读取 App Paths 项失败（" + subNames[i] + "）：" + ex.Message);
                    }
                    finally
                    {
                        if (subKey != null) subKey.Close();
                    }
                    if (progress != null && (i % ProgressInterval == 0))
                    {
                        ReportProgress(progress, "读取 App Paths 注册表项（" + i.ToString() + "/" + subNames.Length.ToString() + "）");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取 App Paths 失败（" + hive.ToString() + " / " + view.ToString() + "）：" + ex.Message);
            }
            finally
            {
                if (appPathsKey != null) appPathsKey.Close();
                if (baseKey != null) baseKey.Close();
            }
        }

        /// <summary>收集桌面/开始菜单/启动项快捷方式</summary>
        private static void CollectFromShortcuts(Dictionary<string, InstalledProgram> map,
            Func<bool> isCancelled, MoveProgressHandler progress)
        {
            List<string> shortcuts = ShortcutResolver.EnumerateShortcuts();
            if (shortcuts.Count == 0) return;
            ReportProgress(progress, "扫描开始菜单快捷方式（共 " + shortcuts.Count.ToString() + " 个）");

            List<ShortcutInfo> resolved = ShortcutResolver.ResolveMany(shortcuts);
            for (int i = 0; i < resolved.Count; i++)
            {
                if (IsCanceled(isCancelled)) return;
                try
                {
                    InstalledProgram item = CreateProgramFromShortcut(resolved[i]);
                    if (item != null) Merge(map, item);
                }
                catch (Exception ex)
                {
                    Log.Warn("处理快捷方式失败：" + ex.Message);
                }
                if (progress != null && (i % ProgressInterval == 0))
                {
                    ReportProgress(progress, "扫描开始菜单快捷方式（" + i.ToString() + "/" + resolved.Count.ToString() + "）");
                }
            }
        }

        /// <summary>由一条快捷方式解析结果构造程序信息；不适合收录时返回 null</summary>
        private static InstalledProgram CreateProgramFromShortcut(ShortcutInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.TargetPath)) return null;
            string target = info.TargetPath;

            bool isFile = false;
            bool isDirectory = false;
            try
            {
                isFile = File.Exists(target);
                isDirectory = Directory.Exists(target);
            }
            catch
            {
                return null;
            }

            string executablePath = string.Empty;
            string installDirectory = string.Empty;
            if (isFile)
            {
                // 快捷方式指向文档/网址文件时不收录（只收录程序类目标）
                string extension;
                try { extension = Path.GetExtension(target).ToLowerInvariant(); }
                catch { return null; }
                if (extension != ".exe" && extension != ".com" && extension != ".bat" && extension != ".cmd") return null;
                executablePath = target;
                installDirectory = DeriveInstallDirectory(target);
            }
            else if (isDirectory)
            {
                installDirectory = DeriveInstallDirectory(target);
            }
            else
            {
                return null;
            }

            if (!IsUsableInstallDirectory(installDirectory)) return null;

            string displayHint = null;
            string shortcutName = Path.GetFileNameWithoutExtension(info.ShortcutPath);
            // 带启动参数的快捷方式（如"Python 3.9 Module Docs"用 pythonw.exe + 参数打开文档）
            // 以及"帮助/文档/重置设置"类快捷方式，不用其文件名当软件名，改用程序自身的产品名
            if (string.IsNullOrEmpty(info.Arguments) && !IsBadShortcutDisplayName(shortcutName))
            {
                displayHint = CleanDisplayName(shortcutName);
                if (displayHint.Length == 0) displayHint = null;
            }
            if (executablePath.Length == 0) executablePath = FindMainExecutable(installDirectory, displayHint);
            if (executablePath.Length == 0 && !IsLikelyInstallDirectory(installDirectory)) return null;

            InstalledProgram item = CreateProgramFromDirectory(installDirectory, executablePath, displayHint);
            if (item == null) return null;
            item.Source = ProgramSource.Shortcut;
            if (IsUnderStartMenu(info.ShortcutPath)) item.Source = item.Source | ProgramSource.StartMenu;
            item.ShortcutPath = info.ShortcutPath;
            item.SizeBytes = -1;
            return item;
        }

        /// <summary>收集正在运行的进程（用于发现装在非标准目录里的软件）</summary>
        private static void CollectFromRunningProcesses(Dictionary<string, InstalledProgram> map,
            Func<bool> isCancelled, MoveProgressHandler progress)
        {
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch (Exception ex)
            {
                Log.Warn("枚举正在运行的进程失败：" + ex.Message);
                return;
            }

            for (int i = 0; i < processes.Length; i++)
            {
                if (IsCanceled(isCancelled)) break;
                Process process = processes[i];
                if (process == null) continue;
                try
                {
                    string executablePath = null;
                    try { executablePath = process.MainModule.FileName; }
                    catch { executablePath = null; } // 系统进程权限不足，正常跳过
                    if (string.IsNullOrEmpty(executablePath)) continue;

                    executablePath = ExpandAndTrim(executablePath);
                    if (executablePath.Length == 0) continue;
                    bool exists;
                    try { exists = File.Exists(executablePath); }
                    catch { exists = false; }
                    if (!exists) continue;

                    string installDirectory = DeriveInstallDirectory(executablePath);
                    if (!IsUsableInstallDirectory(installDirectory)) continue;
                    if (!IsLikelyInstallDirectory(installDirectory)) continue;

                    InstalledProgram item = CreateProgramFromDirectory(
                        installDirectory, executablePath, Path.GetFileNameWithoutExtension(executablePath));
                    if (item == null) continue;
                    item.Source = ProgramSource.RunningProcess;
                    item.IsRunning = true;
                    item.SizeBytes = -1;
                    Merge(map, item);
                }
                catch
                {
                    // 单个进程读取失败（权限/已退出）直接跳过
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
                if (progress != null && (i % ProgressInterval == 0))
                {
                    ReportProgress(progress, "扫描正在运行的程序（" + i.ToString() + "/" + processes.Length.ToString() + "）");
                }
            }
        }

        /// <summary>扫描常见安装目录（限制深度与总量，避免卡顿）</summary>
        private static void CollectFromFolderScan(Dictionary<string, InstalledProgram> map,
            Func<bool> isCancelled, MoveProgressHandler progress)
        {
            List<string> roots = new List<string>();
            List<int> depths = new List<int>();

            AddScanRoot(roots, depths, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProgramFilesScanDepth);
            AddScanRoot(roots, depths, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), ProgramFilesScanDepth);
            AddScanRoot(roots, depths,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                LocalAppDataScanDepth);
            AddScanRoot(roots, depths, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LocalAppDataScanDepth);

            // 其它固定盘根目录下的 Program Files（用户把软件装到 D:\Program Files 的情况）
            List<DriveInfoItem> drives = DriveAnalyzer.GetFixedDrives();
            for (int i = 0; i < drives.Count; i++)
            {
                string rootPath = drives[i].RootPath;
                if (string.IsNullOrEmpty(rootPath)) continue;
                AddScanRoot(roots, depths, Path.Combine(rootPath, "Program Files"), ProgramFilesScanDepth);
                AddScanRoot(roots, depths, Path.Combine(rootPath, "Program Files (x86)"), ProgramFilesScanDepth);
            }

            int scannedDirectories = 0;
            int foundPrograms = 0;
            for (int i = 0; i < roots.Count; i++)
            {
                if (IsCanceled(isCancelled)) return;
                if (scannedDirectories >= MaxScanDirectories) break;
                ScanDirectoryTree(roots[i], depths[i], map, isCancelled, progress,
                    ref scannedDirectories, ref foundPrograms);
            }
            Log.Info("安装目录扫描完成：检查 " + scannedDirectories.ToString() + " 个目录，发现 " +
                     foundPrograms.ToString() + " 个软件目录");
        }

        private static void AddScanRoot(List<string> roots, List<int> depths, string path, int depth)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!Directory.Exists(path)) return;
                string normalized = PathUtil.Normalize(path);
                if (normalized.Length == 0) return;
                for (int i = 0; i < roots.Count; i++)
                {
                    if (string.Equals(roots[i], normalized, StringComparison.OrdinalIgnoreCase)) return;
                }
                roots.Add(normalized);
                depths.Add(depth);
            }
            catch
            {
            }
        }

        /// <summary>广度优先扫描目录树，收录"看起来像安装目录"的目录（收录后不再向下钻）</summary>
        private static void ScanDirectoryTree(string root, int maxDepth, Dictionary<string, InstalledProgram> map,
            Func<bool> isCancelled, MoveProgressHandler progress, ref int scannedDirectories, ref int foundPrograms)
        {
            Stack<KeyValuePair<string, int>> stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, 0));
            while (stack.Count > 0)
            {
                if (IsCanceled(isCancelled)) return;
                if (scannedDirectories >= MaxScanDirectories) return;

                KeyValuePair<string, int> current = stack.Pop();
                string directory = current.Key;
                int depth = current.Value;
                scannedDirectories++;

                if (progress != null && (scannedDirectories % 100 == 0))
                {
                    ReportProgress(progress, "扫描安装目录（已检查 " + scannedDirectories.ToString() + " 个目录）");
                }

                if (depth > 0)
                {
                    if (PathUtil.IsSystemCriticalPath(directory)) continue;
                    string leaf = Path.GetFileName(directory);
                    if (IsIgnoredFolderName(leaf)) continue;
                    if (IsTemporaryPath(directory)) continue;

                    if (IsLikelyInstallDirectory(directory))
                    {
                        try
                        {
                            string executablePath = FindMainExecutable(directory, leaf);
                            // 目录里只有别的产品的辅助 exe（如 NVIDIA App 目录下的 7z.exe）时不算安装目录
                            if (executablePath.Length > 0 && !IsPlausibleExecutable(executablePath, leaf))
                            {
                                continue;
                            }
                            InstalledProgram item = CreateProgramFromDirectory(directory, executablePath, null);
                            if (item != null)
                            {
                                item.Source = ProgramSource.FolderScan;
                                item.SizeBytes = -1;
                                Merge(map, item);
                                foundPrograms++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("收录安装目录失败（" + directory + "）：" + ex.Message);
                        }
                        continue; // 已确认为安装目录，不再向下扫描
                    }
                }

                if (depth >= maxDepth) continue;
                string[] subDirectories;
                try { subDirectories = Directory.GetDirectories(PathUtil.ToLongPath(directory)); }
                catch { subDirectories = new string[0]; }
                for (int i = 0; i < subDirectories.Length; i++)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(subDirectories[i]);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch
                    {
                        continue;
                    }
                    stack.Push(new KeyValuePair<string, int>(StripExtendedPrefix(subDirectories[i]), depth + 1));
                }
            }
        }

        // ============================ 程序信息组装 ============================

        /// <summary>
        /// 由安装目录组装 InstalledProgram：选择主 exe、读取版本信息补全名称/版本/发布者。
        /// executablePath 为空时自动在目录内挑选主程序。
        /// </summary>
        private static InstalledProgram CreateProgramFromDirectory(string directory, string executablePath, string displayHint)
        {
            string installDirectory;
            try { installDirectory = PathUtil.Normalize(directory); }
            catch { installDirectory = directory == null ? string.Empty : directory.Trim(); }
            if (string.IsNullOrEmpty(installDirectory)) return null;
            if (installDirectory.Length <= 3) return null;

            if (string.IsNullOrEmpty(executablePath))
            {
                executablePath = FindMainExecutable(installDirectory, displayHint);
            }

            FileVersionInfo versionInfo = null;
            if (!string.IsNullOrEmpty(executablePath))
            {
                try
                {
                    if (File.Exists(executablePath)) versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                }
                catch
                {
                    versionInfo = null;
                }
            }

            string displayName = string.Empty;
            if (IsGoodDisplayName(displayHint)) displayName = TextUtil.Clean(displayHint);
            if (displayName.Length == 0 && versionInfo != null) displayName = CleanProductName(versionInfo.ProductName);
            if (displayName.Length == 0 && versionInfo != null) displayName = CleanProductName(versionInfo.FileDescription);
            if (displayName.Length == 0) displayName = Path.GetFileName(installDirectory);

            InstalledProgram item = new InstalledProgram();
            item.DisplayName = displayName;
            item.InstallPath = installDirectory;
            item.ExecutablePath = executablePath;
            item.Version = versionInfo == null ? string.Empty : CleanVersion(
                string.IsNullOrEmpty(versionInfo.FileVersion) ? versionInfo.ProductVersion : versionInfo.FileVersion);
            item.Publisher = versionInfo == null ? string.Empty : CleanPublisher(versionInfo.CompanyName);
            item.SizeBytes = -1;
            return item;
        }

        /// <summary>在目录中挑选主程序 exe：优先名字与软件名/目录名相似、体积大的非卸载/更新类程序</summary>
        private static string FindMainExecutable(string directory, string displayHint)
        {
            if (string.IsNullOrEmpty(directory)) return string.Empty;
            bool exists;
            try { exists = Directory.Exists(directory); }
            catch { exists = false; }
            if (!exists) return string.Empty;

            string[] files;
            try { files = Directory.GetFiles(PathUtil.ToLongPath(directory), "*.exe"); }
            catch { return string.Empty; }
            if (files.Length == 0) return string.Empty;

            string hintNorm = SearchRanker.Normalize(displayHint);
            string directoryNorm = SearchRanker.Normalize(Path.GetFileName(directory));
            string best = string.Empty;
            long bestScore = long.MinValue;

            for (int i = 0; i < files.Length; i++)
            {
                string file = StripExtendedPrefix(files[i]);
                if (IsIgnoredExecutable(file)) continue;

                string name = Path.GetFileNameWithoutExtension(file);
                string nameNorm = SearchRanker.Normalize(name);
                long score = 0;

                if (hintNorm.Length > 0 && nameNorm.Length > 0)
                {
                    if (string.Equals(nameNorm, hintNorm, StringComparison.Ordinal)) score += 600;
                    else if (nameNorm.IndexOf(hintNorm, StringComparison.Ordinal) >= 0 ||
                             hintNorm.IndexOf(nameNorm, StringComparison.Ordinal) >= 0) score += 400;
                }
                if (directoryNorm.Length > 0 && nameNorm.Length > 0)
                {
                    if (string.Equals(nameNorm, directoryNorm, StringComparison.Ordinal)) score += 500;
                    else if (nameNorm.IndexOf(directoryNorm, StringComparison.Ordinal) >= 0 ||
                             directoryNorm.IndexOf(nameNorm, StringComparison.Ordinal) >= 0) score += 300;
                }

                try
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(file);
                    if (!string.IsNullOrEmpty(info.ProductName)) score += 50;
                    if (!string.IsNullOrEmpty(info.FileDescription)) score += 30;
                }
                catch
                {
                }

                try
                {
                    long size = new FileInfo(file).Length;
                    long sizeScore = size / (1024 * 1024);
                    if (sizeScore > 100) sizeScore = 100;
                    score += sizeScore;
                }
                catch
                {
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = file;
                }
            }
            return best;
        }

        /// <summary>
        /// 目录扫描时校验主 exe 是否真的属于这个目录：exe 名称、产品名、文件描述
        /// 需要与目录名相同/互相包含或共享较长的英文前缀。
        /// </summary>
        private static bool IsPlausibleExecutable(string executablePath, string directoryName)
        {
            string directoryNorm = SearchRanker.Normalize(directoryName);
            if (directoryNorm.Length == 0) return true;

            List<string> candidates = new List<string>();
            candidates.Add(SearchRanker.Normalize(Path.GetFileNameWithoutExtension(executablePath)));
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
                candidates.Add(SearchRanker.Normalize(info.ProductName));
                candidates.Add(SearchRanker.Normalize(info.FileDescription));
            }
            catch
            {
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];
                if (string.IsNullOrEmpty(candidate)) continue;
                if (string.Equals(candidate, directoryNorm, StringComparison.Ordinal)) return true;
                if (candidate.IndexOf(directoryNorm, StringComparison.Ordinal) >= 0) return true;
                if (directoryNorm.IndexOf(candidate, StringComparison.Ordinal) >= 0) return true;
                if (CommonPrefixLength(candidate, directoryNorm) >= 5) return true;
            }
            return false;
        }

        /// <summary>两个字符串共同的英文前缀长度</summary>
        private static int CommonPrefixLength(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            int count = 0;
            int max = a.Length < b.Length ? a.Length : b.Length;
            while (count < max && a[count] == b[count]) count++;
            return count;
        }

        // ============================ 目录推断辅助 ============================

        /// <summary>
        /// 从目录名开始向上推断安装根：跳过 bin/x64/app-版本号/纯版本号等内部目录，最多向上 3 层；
        /// 路径中出现 Program Files 根 / Common Files / Windows / Package Cache 等时返回空字符串。
        /// </summary>
        private static string DeriveFromDirectory(string directory)
        {
            string current;
            try { current = PathUtil.Normalize(directory); }
            catch { return string.Empty; }
            if (string.IsNullOrEmpty(current)) return string.Empty;

            for (int level = 0; level < 4; level++)
            {
                if (current.Length <= 3) return string.Empty;              // 盘符根
                if (PathUtil.IsSystemCriticalPath(current)) return string.Empty;
                if (IsForbiddenInstallPath(current)) return string.Empty;   // Common Files / Windows / Package Cache 等

                // 路径中间出现内部子目录（如 App\plugins\Sub、Git\usr\bin）时跳到该内部目录的上一级
                string trimmed = TrimInternalSegments(current);
                if (!string.Equals(trimmed, current, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(trimmed)) return string.Empty;
                    current = trimmed;
                    continue;
                }

                string leaf = Path.GetFileName(current);
                if (string.IsNullOrEmpty(leaf)) return string.Empty;
                if (IsIgnoredFolderName(leaf)) return string.Empty;         // 安装根不能是 Program Files 等
                return current;
            }
            return string.Empty;
        }

        /// <summary>
        /// 若路径中存在"安装目录内部子目录"这一层（如 App\plugins\Sub、Git\usr\bin），
        /// 返回到最靠前那个内部目录的上一级；否则原样返回。
        /// </summary>
        private static string TrimInternalSegments(string normalizedPath)
        {
            string[] segments = normalizedPath.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            int cut = -1;
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (segment.Length == 2 && segment[1] == ':') continue; // 盘符
                if (IsInternalFolderName(segment))
                {
                    cut = i;
                    break;
                }
            }
            if (cut <= 0) return normalizedPath;

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < cut; i++)
            {
                if (builder.Length > 0) builder.Append('\\');
                builder.Append(segments[i]);
            }
            return builder.ToString();
        }

        /// <summary>安装目录是否可用：存在、非系统关键目录、不在 Common Files/Package Cache 等禁止位置、非网络/临时路径</summary>
        private static bool IsUsableInstallDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return false;
            directory = ExpandAndTrim(directory);
            if (directory.Length == 0) return false;
            if (directory.StartsWith(@"\\", StringComparison.Ordinal)) return false; // 网络路径不参与搬家
            bool exists;
            try { exists = Directory.Exists(directory); }
            catch { exists = false; }
            if (!exists) return false;

            string normalized;
            try { normalized = PathUtil.Normalize(directory); }
            catch { return false; }
            if (normalized.Length <= 3) return false;
            if (PathUtil.IsSystemCriticalPath(normalized)) return false;
            if (IsForbiddenInstallPath(normalized)) return false;
            if (IsTemporaryPath(normalized)) return false;

            string leaf = Path.GetFileName(normalized);
            if (string.IsNullOrEmpty(leaf)) return false;
            if (IsIgnoredFolderName(leaf) || IsInternalFolderName(leaf)) return false;
            return !HasInternalAncestor(normalized);
        }

        /// <summary>路径的中间层是否出现安装目录内部子目录（如 ...\App\bin\Sub）</summary>
        private static bool HasInternalAncestor(string normalizedPath)
        {
            string[] segments = normalizedPath.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                string segment = segments[i];
                if (segment.Length == 2 && segment[1] == ':') continue;
                if (IsInternalFolderName(segment)) return true;
            }
            return false;
        }

        /// <summary>路径任意一层是否为禁止的目录（Common Files / Windows / Package Cache / 安装缓存 GUID 目录等）</summary>
        private static bool IsForbiddenInstallPath(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath)) return true;
            if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal)) return true;

            string[] segments = normalizedPath.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i].Trim().ToLowerInvariant();
                if (segment.Length == 0) continue;
                if (segment.Length == 2 && segment[1] == ':') continue; // 盘符
                if (GuidSegmentRegex.IsMatch(segment)) return true;      // Installer2\...{GUID} 之类的安装缓存
                if (IsForbiddenSegment(segment)) return true;
            }
            return false;
        }

        /// <summary>单层目录名是否为禁止出现在安装路径中的目录名</summary>
        private static bool IsForbiddenSegment(string segment)
        {
            for (int i = 0; i < ForbiddenPathSegments.Length; i++)
            {
                if (string.Equals(segment, ForbiddenPathSegments[i], StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>是否为安装目录内部的子目录名（bin/x64/app-1.2.3/纯版本号等）</summary>
        private static bool IsInternalFolderName(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return false;
            string name = folderName.Trim().ToLowerInvariant();
            if (VersionFolderRegex.IsMatch(name)) return true;           // 1.2.3 / v2.0 / 2024
            if (OfficeFolderRegex.IsMatch(name)) return true;            // Office16
            if (name.StartsWith("app-", StringComparison.Ordinal))
            {
                string rest = name.Substring(4);
                if (rest.Length > 0 && IsVersionLike(rest)) return true; // app-1.2.3
            }
            string[] internalNames = new string[]
            {
                "bin", "bin32", "bin64", "binaries", "x64", "x86", "x86_64", "amd64",
                "win32", "win64", "cmd", "lib", "libs", "lib64", "jre", "jre64",
                "runtime", "framework", "plugins", "plugin", "modules", "node_modules",
                "resources", "locales", "assets", "application", "apps", "core", "current",
                "release", "debug", "dist", "build", "out", "target", "data", "cache", "temp",
                "root", "cef", "usr", "mingw64", "ucrt64", "clang64", "libexec", "share",
                "etc", "include", "sbin", "doc", "docs", "man", "installer2"
            };
            for (int i = 0; i < internalNames.Length; i++)
            {
                if (string.Equals(name, internalNames[i], StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>字符串是否形如版本号（1.2.3 / 1.2 / 2024 等）</summary>
        private static bool IsVersionLike(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Length > 24) return false;
            bool hasDigit = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch >= '0' && ch <= '9') { hasDigit = true; continue; }
                if (ch == '.' || ch == '_' || ch == '-') continue;
                return false;
            }
            return hasDigit;
        }

        /// <summary>文件夹名是否属于"不收录/不再下钻"的目录</summary>
        private static bool IsIgnoredFolderName(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return true;
            string name = folderName.Trim().ToLowerInvariant();
            for (int i = 0; i < IgnoredFolderNames.Length; i++)
            {
                if (string.Equals(name, IgnoredFolderNames[i], StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>是否为临时目录（其中的 exe 不是已安装软件）</summary>
        private static bool IsTemporaryPath(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return false;
            string path = directory.ToLowerInvariant();
            if (path.IndexOf(@"\temp\", StringComparison.Ordinal) >= 0) return true;
            if (path.IndexOf(@"\tmp\", StringComparison.Ordinal) >= 0) return true;
            if (path.IndexOf(@"\appdata\local\temp", StringComparison.Ordinal) >= 0) return true;
            if (path.IndexOf(@"\$recycle.bin\", StringComparison.Ordinal) >= 0) return true;
            if (path.IndexOf(@"\downloads\", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>安装路径是否位于开始菜单（用于给 Source 加 StartMenu 位）</summary>
        private static bool IsUnderStartMenu(string shortcutPath)
        {
            if (string.IsNullOrEmpty(shortcutPath)) return false;
            string[] folders = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            };
            for (int i = 0; i < folders.Length; i++)
            {
                if (string.IsNullOrEmpty(folders[i])) continue;
                if (PathUtil.IsSubPathOf(shortcutPath, folders[i])) return true;
            }
            return false;
        }

        /// <summary>exe 文件名是否属于卸载/更新/崩溃报告等辅助程序</summary>
        private static bool IsIgnoredExecutable(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath)) return true;
            string name;
            try { name = Path.GetFileNameWithoutExtension(executablePath); }
            catch { return true; }
            if (string.IsNullOrEmpty(name)) return true;
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < IgnoredExeKeywords.Length; i++)
            {
                if (lower.IndexOf(IgnoredExeKeywords[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>显示名是否可用（过滤"快捷方式""启动"这类无意义名称）</summary>
        private static bool IsGoodDisplayName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return false;
            string name = TextUtil.Clean(displayName);
            if (name.Length == 0) return false;
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < BadDisplayNames.Length; i++)
            {
                if (string.Equals(lower, BadDisplayNames[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>快捷方式文件名是否不适合作为软件显示名（文档/设置/卸载入口）</summary>
        private static bool IsBadShortcutDisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (!IsGoodDisplayName(name)) return true;
            string lower = TextUtil.Clean(name).ToLowerInvariant();
            for (int i = 0; i < BadShortcutNameKeywords.Length; i++)
            {
                if (lower.IndexOf(BadShortcutNameKeywords[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // ============================ 字符串/注册表辅助 ============================

        /// <summary>
        /// 由 InstallLocation / DisplayIcon / UninstallString 推断安装目录。
        /// 优先级：InstallLocation（目录或可推断的 exe）-> DisplayIcon -> UninstallString。
        /// </summary>
        private static string ResolveInstallDirectory(string installLocation, string displayIcon, string uninstallString)
        {
            string directory = NormalizeExistingDirectory(installLocation);
            if (directory.Length > 0) return directory;
            string derived = DeriveInstallDirectory(installLocation);
            if (derived.Length > 0) return derived;

            string iconPath = StripIconIndex(displayIcon);
            directory = NormalizeExistingDirectory(iconPath);
            if (directory.Length > 0) return directory;
            derived = DeriveInstallDirectory(iconPath);
            if (derived.Length > 0) return derived;

            string uninstallExe = ExtractExecutablePath(uninstallString);
            derived = DeriveInstallDirectory(uninstallExe);
            if (derived.Length > 0) return derived;

            return string.Empty;
        }

        /// <summary>把注册表值规范成已存在的目录路径，否则返回空字符串</summary>
        private static string NormalizeExistingDirectory(string value)
        {
            string path = ExpandAndTrim(value);
            if (path.Length == 0) return string.Empty;
            try
            {
                if (!Directory.Exists(path)) return string.Empty;
                return PathUtil.Normalize(path);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>去掉 DisplayIcon 末尾的 ",0" / ",-1" 图标序号</summary>
        private static string StripIconIndex(string displayIcon)
        {
            string value = ExpandAndTrim(displayIcon);
            if (value.Length == 0) return string.Empty;
            int comma = value.LastIndexOf(',');
            if (comma > 0)
            {
                string tail = value.Substring(comma + 1).Trim();
                int index;
                if (tail.Length == 0 || int.TryParse(tail, out index))
                {
                    value = value.Substring(0, comma).Trim();
                }
            }
            return value.Trim().Trim('"').Trim();
        }

        /// <summary>从卸载命令行里提取可执行文件路径（形如 "C:\App\unins000.exe" /S）</summary>
        private static string ExtractExecutablePath(string commandLine)
        {
            string value = ExpandAndTrim(commandLine);
            if (value.Length == 0) return string.Empty;

            if (value[0] == '"')
            {
                int end = value.IndexOf('"', 1);
                if (end > 1) return value.Substring(1, end - 1).Trim();
                return string.Empty;
            }

            int exeIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex > 0) return value.Substring(0, exeIndex + 4).Trim();

            int space = value.IndexOf(' ');
            if (space > 0) return value.Substring(0, space).Trim();
            return value;
        }

        /// <summary>展开环境变量、去引号、去空白，并去掉目录枚举时可能出现的 \\?\ 前缀</summary>
        private static string ExpandAndTrim(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string result = value.Trim();
            try { result = Environment.ExpandEnvironmentVariables(result); }
            catch { }
            result = result.Trim().Trim('"').Trim();
            return StripExtendedPrefix(result);
        }

        /// <summary>去掉 \\?\ 或 \\?\UNC\ 长路径前缀，保证界面与快捷方式里显示的是普通路径</summary>
        private static string StripExtendedPrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(8);
            }
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path.Substring(4);
            }
            return path;
        }

        /// <summary>清理显示名：去掉末尾版本号与位数说明、多余分隔符</summary>
        private static string CleanDisplayName(string displayName)
        {
            string name = TextUtil.Clean(displayName);
            if (name.Length == 0) return string.Empty;
            string previous;
            do
            {
                previous = name;
                name = TrailingBitnessRegex.Replace(name, string.Empty);
                name = TrailingVersionRegex.Replace(name, string.Empty);
                name = TrailingVersionWordRegex.Replace(name, string.Empty);
                name = name.Trim();
                name = name.TrimEnd('-', '_', ' ', '·', ',');
                name = name.Trim();
            }
            while (name.Length > 0 && !string.Equals(previous, name, StringComparison.Ordinal));
            // 个别程序的文件描述直接就是 "xxx.exe"，去掉扩展名更好看
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
            {
                name = name.Substring(0, name.Length - 4).Trim();
            }
            return name;
        }

        /// <summary>读取程序的产品名并清理；"TODO: &lt;Product name&gt;" 这类占位符直接丢弃</summary>
        private static string CleanProductName(string productName)
        {
            string name = TextUtil.Clean(productName);
            if (name.Length == 0) return string.Empty;
            string lower = name.ToLowerInvariant();
            if (lower.IndexOf("todo", StringComparison.Ordinal) >= 0) return string.Empty;
            if (lower.IndexOf("<product", StringComparison.Ordinal) >= 0) return string.Empty;
            if (lower.IndexOf("unknown", StringComparison.Ordinal) >= 0) return string.Empty;
            if (lower.IndexOf("placeholder", StringComparison.Ordinal) >= 0) return string.Empty;
            return CleanDisplayName(name);
        }

        /// <summary>清理版本号：去掉空格之后的多余内容（如 "10.0.1 (WinBuild.xxx)"）</summary>
        private static string CleanVersion(string version)
        {
            string value = TextUtil.Clean(version);
            if (value.Length == 0) return string.Empty;
            int space = value.IndexOf(' ');
            if (space > 0) value = value.Substring(0, space);
            return value.Trim();
        }

        /// <summary>清理发布者名称</summary>
        private static string CleanPublisher(string publisher)
        {
            string value = TextUtil.Clean(publisher);
            if (value.Length == 0) return string.Empty;
            return value;
        }

        /// <summary>读取注册表字符串值</summary>
        private static string GetRegistryString(RegistryKey key, string name)
        {
            if (key == null) return string.Empty;
            try
            {
                object value = key.GetValue(name);
                string text = value as string;
                return text == null ? string.Empty : TextUtil.Clean(text);
            }
            catch (Exception ex)
            {
                Log.Warn("读取注册表值失败（" + name + "）：" + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>读取注册表默认值（未命名值）</summary>
        private static string GetRegistryDefaultString(RegistryKey key)
        {
            if (key == null) return string.Empty;
            try
            {
                object value = key.GetValue(null);
                string text = value as string;
                return text == null ? string.Empty : TextUtil.Clean(text);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>读取注册表 DWORD 值</summary>
        private static int GetRegistryInt(RegistryKey key, string name)
        {
            if (key == null) return 0;
            try
            {
                object value = key.GetValue(name);
                if (value == null) return 0;
                if (value is int) return (int)value;
                if (value is long) return (int)(long)value;
                string text = value as string;
                int parsed;
                if (text != null && int.TryParse(text, out parsed)) return parsed;
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        // ============================ 合并/排序/进度 ============================

        /// <summary>把一个程序合并进索引：按安装目录去重，合并来源位并保留更完整的字段</summary>
        private static void Merge(Dictionary<string, InstalledProgram> map, InstalledProgram candidate)
        {
            if (map == null || candidate == null) return;
            string installPath;
            try { installPath = PathUtil.Normalize(ExpandAndTrim(candidate.InstallPath)); }
            catch { return; }
            if (string.IsNullOrEmpty(installPath)) return;
            if (installPath.Length <= 3) return;
            if (installPath.StartsWith(@"\\", StringComparison.Ordinal)) return;   // 网络路径不作为搬家源
            if (IsForbiddenInstallPath(installPath)) return;                        // Common Files / Package Cache 等

            candidate.InstallPath = installPath;
            candidate.DriveRoot = PathUtil.GetDriveRoot(installPath);
            candidate.Id = installPath.ToLowerInvariant();
            if (candidate.SizeBytes < -1) candidate.SizeBytes = -1;

            InstalledProgram existing;
            if (!map.TryGetValue(candidate.Id, out existing))
            {
                map.Add(candidate.Id, candidate);
                return;
            }

            // 显示名：按来源可信度决定是否覆盖，避免"Reset Spyder Settings"之类快捷方式名盖掉注册表里的正式软件名
            ProgramSource oldSource = existing.Source;
            if (IsBetterDisplayName(candidate.DisplayName, existing.DisplayName, candidate.Source, oldSource))
            {
                existing.DisplayName = candidate.DisplayName;
            }
            // 合并来源位与运行状态
            existing.Source = oldSource | candidate.Source;
            if (candidate.IsRunning) existing.IsRunning = true;
            if (string.IsNullOrEmpty(existing.Version) && !string.IsNullOrEmpty(candidate.Version))
                existing.Version = candidate.Version;
            if (string.IsNullOrEmpty(existing.Publisher) && !string.IsNullOrEmpty(candidate.Publisher))
                existing.Publisher = candidate.Publisher;
            if (string.IsNullOrEmpty(existing.UninstallString) && !string.IsNullOrEmpty(candidate.UninstallString))
                existing.UninstallString = candidate.UninstallString;
            if (string.IsNullOrEmpty(existing.ShortcutPath) && !string.IsNullOrEmpty(candidate.ShortcutPath))
                existing.ShortcutPath = candidate.ShortcutPath;

            // 主程序：已经存在的 exe 优先；都不存在时保留较新的
            if (string.IsNullOrEmpty(existing.ExecutablePath) ||
                (!FileExistsSafe(existing.ExecutablePath) && FileExistsSafe(candidate.ExecutablePath)))
            {
                existing.ExecutablePath = candidate.ExecutablePath;
            }
            if (existing.SizeBytes < 0 && candidate.SizeBytes >= 0) existing.SizeBytes = candidate.SizeBytes;
        }

        /// <summary>
        /// 候选显示名是否应覆盖现有的：来源更可信（注册表/App Paths &gt; 快捷方式 &gt; 进程/目录扫描）时优先，
        /// 同来源时取更完整的名称；"卸载/Uninstall"类名称一律不用。
        /// </summary>
        private static bool IsBetterDisplayName(string candidate, string existing, ProgramSource candidateSource, ProgramSource existingSource)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            if (candidate.Length < 2) return false;
            string lower = candidate.ToLowerInvariant();
            if (lower.IndexOf("卸载", StringComparison.Ordinal) >= 0) return false;
            if (lower.IndexOf("uninstall", StringComparison.Ordinal) >= 0) return false;
            if (string.IsNullOrEmpty(existing)) return true;
            if (existing.Length == 0) return true;

            int candidateAuthority = GetSourceAuthority(candidateSource);
            int existingAuthority = GetSourceAuthority(existingSource);
            if (candidateAuthority > existingAuthority) return true;

            string existingLower = existing.ToLowerInvariant();
            bool existingIsUninstall = existingLower.IndexOf("卸载", StringComparison.Ordinal) >= 0 ||
                                       existingLower.IndexOf("uninstall", StringComparison.Ordinal) >= 0;
            if (existingIsUninstall) return true;

            if (candidateAuthority == existingAuthority) return candidate.Length > existing.Length + 1;

            // 来源可信度更低：一般保留现有的正式名称；只有现有名称明显是占位符/无意义名称时才替换
            if (existingLower.IndexOf("todo", StringComparison.Ordinal) >= 0) return true;
            if (existingLower.IndexOf("<product", StringComparison.Ordinal) >= 0) return true;
            if (existingLower.IndexOf("unknown", StringComparison.Ordinal) >= 0) return true;
            return !IsGoodDisplayName(existing);
        }

        /// <summary>显示名来源可信度：注册表/App Paths 最可靠，快捷方式次之，进程/目录扫描最低</summary>
        private static int GetSourceAuthority(ProgramSource source)
        {
            if ((source & ProgramSource.ManualPath) != 0) return 4;
            if ((source & ProgramSource.Registry) != 0) return 3;
            if ((source & ProgramSource.AppPaths) != 0) return 3;
            if ((source & ProgramSource.Shortcut) != 0) return 2;
            if ((source & ProgramSource.StartMenu) != 0) return 2;
            return 1;
        }

        private static bool FileExistsSafe(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return File.Exists(path); }
            catch { return false; }
        }

        /// <summary>把字典中的结果排序后填入索引</summary>
        private static SearchIndex Finish(SearchIndex index, Dictionary<string, InstalledProgram> map)
        {
            List<InstalledProgram> programs = new List<InstalledProgram>(map.Values.Count);
            foreach (InstalledProgram item in map.Values) programs.Add(item);
            programs.Sort(CompareDefaultOrder);
            index.Programs = programs;
            index.BuiltAt = DateTime.Now;
            return index;
        }

        /// <summary>默认排序：大小已知优先，其次 C 盘优先，再按名称排序</summary>
        private static int CompareDefaultOrder(InstalledProgram a, InstalledProgram b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;

            bool aSizeKnown = a.SizeBytes >= 0;
            bool bSizeKnown = b.SizeBytes >= 0;
            if (aSizeKnown != bSizeKnown) return aSizeKnown ? -1 : 1;

            bool aSystem = IsOnSystemDrive(a.DriveRoot);
            bool bSystem = IsOnSystemDrive(b.DriveRoot);
            if (aSystem != bSystem) return aSystem ? -1 : 1;

            string an = a.DisplayName == null ? string.Empty : a.DisplayName;
            string bn = b.DisplayName == null ? string.Empty : b.DisplayName;
            int nameCompare = string.Compare(an, bn, StringComparison.CurrentCulture);
            if (nameCompare != 0) return nameCompare;
            return string.Compare(a.InstallPath, b.InstallPath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOnSystemDrive(string driveRoot)
        {
            if (string.IsNullOrEmpty(driveRoot)) return false;
            return string.Equals(driveRoot, SystemDriveRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCanceled(Func<bool> isCancelled)
        {
            if (isCancelled == null) return false;
            try { return isCancelled(); }
            catch { return false; }
        }

        /// <summary>回报扫描进度（统一为 Preparing 阶段，"正在扫描…"）</summary>
        private static void ReportProgress(MoveProgressHandler progress, string message)
        {
            if (progress == null) return;
            try
            {
                MoveProgress item = new MoveProgress();
                item.Stage = MoveStage.Preparing;
                item.StageText = "正在扫描…";
                item.Message = message;
                item.Percent = -1;
                progress(item);
            }
            catch (Exception ex)
            {
                Log.Warn("扫描进度回调异常：" + ex.Message);
            }
        }
    }
}
