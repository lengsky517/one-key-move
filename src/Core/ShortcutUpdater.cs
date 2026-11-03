// ============================================================================
//  一键挪动 (OneKeyMove) - 快捷方式批量改指 / 桌面快捷方式保障 / 失效快捷方式清理
//  职责：
//    1) UpdateAll：把目标位于旧安装目录下的 .lnk 改指到新位置；
//    2) EnsureDesktopShortcut：搬完后保证用户桌面有一个指向新位置的有效快捷方式
//       （已有则复用，没有则新建；同名但指向别处的快捷方式不覆盖，改用 "名称 (2).lnk"）；
//    3) RemoveDanglingShortcuts：只清理标准快捷方式位置（桌面/开始菜单/启动项）中
//       目标仍落在旧目录下、且该目标已不存在的悬空 .lnk（目标仍存在的一律不删）。
//  说明：解析/改写沿用 T1 的 ShortcutResolver；新建 .lnk 与“读取悬空目标”用最小 COM 封装
//        （IShellLinkW + IPersistFile），不依赖任何 Interop 程序集。
//  目标框架：.NET Framework 4.8 / C# 5
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YijianNuodong.Core
{
    /// <summary>快捷方式批量更新、桌面快捷方式保障与失效快捷方式清理</summary>
    public static class ShortcutUpdater
    {
        // SLGP_RAWPATH：返回快捷方式里保存的原始路径（目标不存在时也能读到）
        private const uint SlgpRawPath = 0x00000004;
        private const uint StgmRead = 0x00000000;
        private const uint StgmReadWrite = 0x00000002;

        private const int PathBufferSize = 1024;
        private const int StaCallTimeoutMs = 15000;
        private const int BatchTimeoutMs = 60000;
        private const int MaxEnumerateDepth = 8;
        private const int MaxDesktopShortcutSuffix = 100;

        private static readonly Guid ShellLinkClsid = new Guid("00021401-0000-0000-C000-000000000046");

        #region 原有接口：批量改指

        /// <summary>
        /// 把指向 oldPath 的快捷方式全部改指 newPath。
        /// updated 返回成功的快捷方式路径，errors 返回失败原因；返回成功数量。
        /// </summary>
        public static int UpdateAll(string oldPath, string newPath, out List<string> updated, out List<string> errors)
        {
            updated = new List<string>();
            errors = new List<string>();

            string oldDirectory = PathUtil.Normalize(oldPath);
            string newDirectory = PathUtil.Normalize(newPath);
            if (string.IsNullOrEmpty(oldDirectory) || string.IsNullOrEmpty(newDirectory))
            {
                errors.Add("旧路径或新路径为空，无法更新快捷方式。");
                return 0;
            }

            List<string> shortcuts = FindShortcutsPointingTo(oldDirectory);
            if (shortcuts.Count == 0)
            {
                return 0;
            }

            for (int i = 0; i < shortcuts.Count; i++)
            {
                string shortcutPath = shortcuts[i];
                ShortcutInfo info;
                string resolveError;
                if (!TryResolveSafely(shortcutPath, out info, out resolveError))
                {
                    errors.Add("无法解析快捷方式 " + shortcutPath + "：" + (string.IsNullOrEmpty(resolveError) ? "未知原因" : resolveError));
                    continue;
                }

                string mappedTarget = MapPath(info.TargetPath, oldDirectory, newDirectory);
                if (string.IsNullOrEmpty(mappedTarget))
                {
                    errors.Add("无法计算快捷方式 " + shortcutPath + " 的新目标路径。");
                    continue;
                }

                string mappedWorkingDirectory = info.WorkingDirectory;
                if (!string.IsNullOrEmpty(mappedWorkingDirectory)
                    && (PathUtil.SamePath(mappedWorkingDirectory, oldDirectory)
                        || PathUtil.IsSubPathOf(mappedWorkingDirectory, oldDirectory)))
                {
                    string mapped = MapPath(mappedWorkingDirectory, oldDirectory, newDirectory);
                    if (!string.IsNullOrEmpty(mapped))
                    {
                        mappedWorkingDirectory = mapped;
                    }
                }

                string updateError;
                if (TryUpdateSafely(shortcutPath, mappedTarget, mappedWorkingDirectory, out updateError))
                {
                    updated.Add(shortcutPath);
                    Log.Info("已更新快捷方式：" + shortcutPath + " -> " + mappedTarget);
                }
                else
                {
                    errors.Add("更新快捷方式 " + shortcutPath + " 失败：" + (string.IsNullOrEmpty(updateError) ? "未知原因" : updateError));
                }
            }

            if (updated.Count > 0)
            {
                Log.Info("共更新快捷方式 " + updated.Count + " 个。");
            }
            return updated.Count;
        }

        /// <summary>查找目标位于 oldPath 下的快捷方式</summary>
        public static List<string> FindShortcutsPointingTo(string oldPath)
        {
            List<string> matched = new List<string>();
            string oldDirectory = PathUtil.Normalize(oldPath);
            if (string.IsNullOrEmpty(oldDirectory))
            {
                return matched;
            }

            List<string> allShortcuts;
            try
            {
                allShortcuts = ShortcutResolver.EnumerateShortcuts();
            }
            catch (Exception ex)
            {
                Log.Warn("枚举快捷方式失败（无法更新快捷方式）：" + ex.Message);
                return matched;
            }
            if (allShortcuts == null || allShortcuts.Count == 0)
            {
                return matched;
            }

            for (int i = 0; i < allShortcuts.Count; i++)
            {
                string shortcutPath = allShortcuts[i];
                if (string.IsNullOrEmpty(shortcutPath))
                {
                    continue;
                }
                ShortcutInfo info;
                string error;
                if (!TryResolveSafely(shortcutPath, out info, out error))
                {
                    continue;
                }
                if (info == null || string.IsNullOrEmpty(info.TargetPath))
                {
                    continue;
                }
                if (PathUtil.SamePath(info.TargetPath, oldDirectory)
                    || PathUtil.IsSubPathOf(info.TargetPath, oldDirectory))
                {
                    if (!ContainsPath(matched, shortcutPath))
                    {
                        matched.Add(shortcutPath);
                    }
                }
            }
            return matched;
        }

        #endregion

        #region 新增接口：桌面快捷方式保障 / 失效快捷方式清理

        /// <summary>
        /// 搬完后确保用户桌面存在一个指向新位置的有效快捷方式。
        /// 若桌面已有快捷方式指向新路径（或新路径下的 exe）→ 复用并返回其路径；
        /// 若没有 → 在桌面新建 <显示名>.lnk，指向新位置下的主 exe（工作目录=新安装目录，图标=该 exe）；
        /// 同名但指向别处的快捷方式不覆盖，改用 "名称 (2).lnk"。
        /// </summary>
        public static bool EnsureDesktopShortcut(InstalledProgram program, string newInstallPath,
            out string shortcutPath, out string error)
        {
            string desktopFolder = string.Empty;
            try
            {
                desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            catch (Exception)
            {
                desktopFolder = string.Empty;
            }
            if (string.IsNullOrEmpty(desktopFolder))
            {
                shortcutPath = null;
                error = "无法定位用户桌面目录，未创建桌面快捷方式。";
                return false;
            }
            return EnsureDesktopShortcutInFolder(program, newInstallPath, desktopFolder, out shortcutPath, out error);
        }

        /// <summary>
        /// 在指定目录内确保存在指向新位置的有效快捷方式（内部实现，便于自动化测试）。
        /// </summary>
        internal static bool EnsureDesktopShortcutInFolder(InstalledProgram program, string newInstallPath,
            string shortcutFolder, out string shortcutPath, out string error)
        {
            shortcutPath = null;
            error = null;
            if (program == null)
            {
                error = "程序信息为空，无法创建桌面快捷方式。";
                return false;
            }
            string installPath = PathUtil.Normalize(newInstallPath);
            if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
            {
                error = "新安装目录不存在，无法创建桌面快捷方式：" + newInstallPath;
                return false;
            }
            string folder = PathUtil.Normalize(shortcutFolder);
            if (string.IsNullOrEmpty(folder))
            {
                error = "快捷方式目录无效，无法创建桌面快捷方式。";
                return false;
            }
            try
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }
            }
            catch (Exception ex)
            {
                error = "无法创建快捷方式目录：" + folder + "（" + ex.Message + "）";
                return false;
            }

            // 1. 找到新位置下的主程序
            string mainExecutable = FindMainExecutable(program, installPath);
            if (string.IsNullOrEmpty(mainExecutable))
            {
                error = "新位置中没有找到可执行文件，无法创建桌面快捷方式：" + installPath;
                return false;
            }

            // 2. 桌面已有指向新位置的有效快捷方式 → 直接复用
            List<string> existing = EnumerateShortcutFiles(folder, false);
            List<RawTargetInfo> targets = ReadRawTargetsBatch(existing);
            for (int i = 0; i < targets.Count; i++)
            {
                RawTargetInfo info = targets[i];
                if (string.IsNullOrEmpty(info.Target) || !TargetExists(info.Target))
                {
                    continue;
                }
                if (PathUtil.SamePath(info.Target, mainExecutable)
                    || PathUtil.SamePath(info.Target, installPath)
                    || PathUtil.IsSubPathOf(info.Target, installPath))
                {
                    shortcutPath = info.ShortcutPath;
                    Log.Info("桌面已有指向新位置的快捷方式，直接复用：" + shortcutPath);
                    return true;
                }
            }

            // 3. 生成不冲突的文件名（同名但指向别处的快捷方式不覆盖）
            string baseName = BuildShortcutBaseName(program, installPath);
            string candidate = Path.Combine(folder, baseName + ".lnk");
            int suffix = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(folder, baseName + " (" + suffix.ToString() + ").lnk");
                suffix++;
                if (suffix > MaxDesktopShortcutSuffix)
                {
                    error = "桌面同名快捷方式过多，无法创建新的快捷方式：" + baseName;
                    return false;
                }
            }

            // 4. 创建 .lnk：目标=主 exe，工作目录=新安装目录，图标=主 exe
            string createError;
            if (!CreateShortcutSafe(candidate, mainExecutable, installPath, mainExecutable,
                BuildShortcutDescription(program), out createError))
            {
                error = "创建桌面快捷方式失败：" + createError;
                return false;
            }

            // 5. 校验：解析回来的目标必须是主 exe，否则删除并报错
            ShortcutInfo verifyInfo = null;
            string verifyError = null;
            bool verified = false;
            try
            {
                verified = ShortcutResolver.TryResolve(candidate, out verifyInfo, out verifyError);
            }
            catch (Exception ex)
            {
                verifyError = ex.Message;
                verified = false;
            }
            if (!verified || verifyInfo == null || !PathUtil.SamePath(verifyInfo.TargetPath, mainExecutable))
            {
                try
                {
                    File.Delete(candidate);
                }
                catch (Exception)
                {
                    // 删除失败忽略：下面仍会返回错误
                }
                error = "创建桌面快捷方式后校验失败：" + (string.IsNullOrEmpty(verifyError) ? "目标指向不一致" : verifyError);
                return false;
            }

            shortcutPath = candidate;
            Log.Info("已创建桌面快捷方式：" + candidate + " -> " + mainExecutable);
            return true;
        }

        /// <summary>
        /// 清理因搬移而失效的快捷方式：仅处理标准快捷方式位置（桌面/开始菜单/启动项）中
        /// 解析目标仍落在 oldPath 下、且该目标已不存在的 .lnk（悬空链接）。
        /// 安全约束：只删 .lnk 文件本身；目标仍存在（例如目录联接生效）的一律不删；失败记 errors。
        /// 返回删除数量。
        /// </summary>
        public static int RemoveDanglingShortcuts(string oldPath, out List<string> removed, out List<string> errors)
        {
            List<string> roots = CollectStandardShortcutRoots();
            return RemoveDanglingShortcutsInRoots(oldPath, roots, out removed, out errors);
        }

        /// <summary>在指定目录集合内清理悬空快捷方式（内部实现，便于自动化测试）</summary>
        internal static int RemoveDanglingShortcutsInRoots(string oldPath, List<string> roots,
            out List<string> removed, out List<string> errors)
        {
            removed = new List<string>();
            errors = new List<string>();
            string oldDirectory = PathUtil.Normalize(oldPath);
            if (string.IsNullOrEmpty(oldDirectory))
            {
                errors.Add("旧路径为空，无法清理失效快捷方式。");
                return 0;
            }
            if (roots == null || roots.Count == 0)
            {
                return 0;
            }

            List<string> candidates = new List<string>();
            List<string> seen = new List<string>();
            for (int i = 0; i < roots.Count; i++)
            {
                List<string> found = EnumerateShortcutFiles(roots[i], true);
                for (int j = 0; j < found.Count; j++)
                {
                    if (!ContainsPath(seen, found[j]))
                    {
                        seen.Add(found[j]);
                        candidates.Add(found[j]);
                    }
                }
            }
            if (candidates.Count == 0)
            {
                return 0;
            }

            List<RawTargetInfo> targets = ReadRawTargetsBatch(candidates);
            for (int i = 0; i < targets.Count; i++)
            {
                RawTargetInfo info = targets[i];
                if (string.IsNullOrEmpty(info.ShortcutPath))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(info.Error))
                {
                    errors.Add("无法读取快捷方式 " + info.ShortcutPath + "：" + info.Error);
                    continue;
                }
                string target = PathUtil.Normalize(info.Target);
                if (target.Length == 0)
                {
                    errors.Add("快捷方式没有有效目标，已跳过：" + info.ShortcutPath);
                    continue;
                }

                // 只处理“目标落在旧路径下”的快捷方式
                if (!PathUtil.SamePath(target, oldDirectory) && !PathUtil.IsSubPathOf(target, oldDirectory))
                {
                    continue;
                }
                // 目标仍然存在（搬家的目录联接已生效）→ 绝不删除
                if (TargetExists(target))
                {
                    continue;
                }

                try
                {
                    File.Delete(info.ShortcutPath);
                    if (File.Exists(info.ShortcutPath))
                    {
                        errors.Add("删除失效快捷方式失败（文件仍存在）：" + info.ShortcutPath);
                    }
                    else
                    {
                        removed.Add(info.ShortcutPath);
                        Log.Info("已删除失效快捷方式：" + info.ShortcutPath + "（原目标：" + target + "）");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("删除失效快捷方式失败：" + info.ShortcutPath + "（" + ex.Message + "）");
                }
            }
            return removed.Count;
        }

        #endregion

        #region 主程序定位 / 命名

        /// <summary>找到新位置下的主程序：优先程序记录的可执行文件映射，其次新目录中最大的 exe</summary>
        private static string FindMainExecutable(InstalledProgram program, string installPath)
        {
            string executable = string.Empty;
            if (!string.IsNullOrEmpty(program.ExecutablePath))
            {
                try
                {
                    executable = PathUtil.Normalize(program.ExecutablePath);
                }
                catch (Exception)
                {
                    executable = string.Empty;
                }
            }
            string oldInstallPath = string.Empty;
            if (!string.IsNullOrEmpty(program.InstallPath))
            {
                try
                {
                    oldInstallPath = PathUtil.Normalize(program.InstallPath);
                }
                catch (Exception)
                {
                    oldInstallPath = string.Empty;
                }
            }

            // 情况一：可执行文件仍在原位置，按相对路径映射到新目录
            if (executable.Length > 0
                && oldInstallPath.Length > 0
                && (PathUtil.SamePath(executable, oldInstallPath) || PathUtil.IsSubPathOf(executable, oldInstallPath)))
            {
                string relative = executable.Substring(oldInstallPath.Length);
                string mapped = PathUtil.Normalize(installPath + relative);
                if (File.Exists(mapped))
                {
                    return mapped;
                }
            }
            // 情况二：可执行文件本身已在新目录下
            if (executable.Length > 0 && File.Exists(executable) && PathUtil.IsSubPathOf(executable, installPath))
            {
                return executable;
            }
            // 情况三：新目录中最大的 exe（通常就是主程序）
            return FindLargestExecutable(installPath);
        }

        /// <summary>递归寻找目录中体积最大的 .exe（跳过重解析点）</summary>
        private static string FindLargestExecutable(string directory)
        {
            string best = null;
            long bestSize = -1;
            try
            {
                Queue<string> queue = new Queue<string>();
                queue.Enqueue(directory);
                int visitedDirectories = 0;
                while (queue.Count > 0 && visitedDirectories < 200)
                {
                    string current = queue.Dequeue();
                    visitedDirectories++;
                    string longCurrent = PathUtil.ToLongPath(current);
                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(longCurrent);
                    }
                    catch (Exception)
                    {
                        files = new string[0];
                    }
                    for (int i = 0; i < files.Length; i++)
                    {
                        string extension = Path.GetExtension(files[i]);
                        if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        long length = 0;
                        try
                        {
                            FileInfo info = new FileInfo(files[i]);
                            if (!info.Exists)
                            {
                                continue;
                            }
                            length = info.Length;
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        if (length > bestSize)
                        {
                            bestSize = length;
                            best = ToNormalPath(files[i]);
                        }
                    }
                    string[] subDirectories;
                    try
                    {
                        subDirectories = Directory.GetDirectories(longCurrent);
                    }
                    catch (Exception)
                    {
                        subDirectories = new string[0];
                    }
                    for (int i = 0; i < subDirectories.Length; i++)
                    {
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(subDirectories[i]);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        queue.Enqueue(subDirectories[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("查找目录中最大的可执行文件失败：" + ex.Message);
            }
            return best;
        }

        /// <summary>快捷方式文件名（不含扩展名）：显示名优先，其次目录叶名</summary>
        private static string BuildShortcutBaseName(InstalledProgram program, string installPath)
        {
            string name = TextUtil.Clean(program.DisplayName);
            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    name = Path.GetFileName(installPath);
                }
                catch (Exception)
                {
                    name = string.Empty;
                }
            }
            string sanitized = PathUtil.SanitizeFolderName(name);
            if (string.IsNullOrEmpty(sanitized))
            {
                sanitized = "程序";
            }
            return sanitized;
        }

        /// <summary>快捷方式描述文字</summary>
        private static string BuildShortcutDescription(InstalledProgram program)
        {
            string name = TextUtil.Clean(program.DisplayName);
            if (string.IsNullOrEmpty(name))
            {
                return "一键挪动已搬移的程序";
            }
            return name + "（由一键挪动搬移）";
        }

        #endregion

        #region 路径与枚举辅助

        /// <summary>把路径在 oldDirectory 内的部分映射到 newDirectory；不在其内返回 null</summary>
        private static string MapPath(string path, string oldDirectory, string newDirectory)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            string normalized = PathUtil.Normalize(path);
            if (normalized.Length == 0)
            {
                return null;
            }
            if (PathUtil.SamePath(normalized, oldDirectory))
            {
                return newDirectory;
            }
            if (PathUtil.IsSubPathOf(normalized, oldDirectory))
            {
                string rest = normalized.Substring(oldDirectory.Length);
                return PathUtil.Normalize(newDirectory + rest);
            }
            return null;
        }

        /// <summary>标准快捷方式位置：用户/公共桌面、用户/公共开始菜单、用户/公共启动项</summary>
        private static List<string> CollectStandardShortcutRoots()
        {
            List<string> roots = new List<string>();
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.DesktopDirectory);
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.CommonDesktopDirectory);
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.Programs);
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.CommonPrograms);
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.Startup);
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.CommonStartup);
            return roots;
        }

        /// <summary>添加一个特殊文件夹作为枚举根（去重、规范化）</summary>
        private static void AddSpecialFolderRoot(List<string> roots, Environment.SpecialFolder folder)
        {
            try
            {
                string path = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }
                string normalized = PathUtil.Normalize(path);
                if (normalized.Length == 0)
                {
                    return;
                }
                if (!ContainsPath(roots, normalized))
                {
                    roots.Add(normalized);
                }
            }
            catch (Exception)
            {
                // 特殊文件夹不存在或不可访问时跳过
            }
        }

        /// <summary>枚举目录下所有 .lnk（recursive=false 只看当层）</summary>
        private static List<string> EnumerateShortcutFiles(string root, bool recursive)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(root))
            {
                return result;
            }
            try
            {
                if (!Directory.Exists(root))
                {
                    return result;
                }
            }
            catch (Exception)
            {
                return result;
            }

            Stack<KeyValuePair<string, int>> stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, 0));
            while (stack.Count > 0)
            {
                KeyValuePair<string, int> current = stack.Pop();
                string directory = current.Key;
                int depth = current.Value;

                string[] files;
                try
                {
                    files = Directory.GetFiles(PathUtil.ToLongPath(directory), "*.lnk");
                }
                catch (Exception)
                {
                    files = new string[0];
                }
                for (int i = 0; i < files.Length; i++)
                {
                    if (ShortcutResolver.IsShortcutFile(files[i]))
                    {
                        result.Add(ToNormalPath(files[i]));
                    }
                }

                if (!recursive || depth >= MaxEnumerateDepth)
                {
                    continue;
                }
                string[] subDirectories;
                try
                {
                    subDirectories = Directory.GetDirectories(PathUtil.ToLongPath(directory));
                }
                catch (Exception)
                {
                    subDirectories = new string[0];
                }
                for (int i = 0; i < subDirectories.Length; i++)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(subDirectories[i]);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    stack.Push(new KeyValuePair<string, int>(subDirectories[i], depth + 1));
                }
            }
            return result;
        }

        /// <summary>路径对应目标是否仍然存在（文件或目录）</summary>
        private static bool TargetExists(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            try
            {
                return File.Exists(path) || Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>去掉 \\?\ 长路径前缀（ShellLink COM 与界面展示只接受普通路径）</summary>
        private static string ToNormalPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path.Substring(4);
            }
            return path;
        }
        /// <summary>列表是否已包含指定路径（忽略大小写）</summary>
        private static bool ContainsPath(List<string> paths, string path)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                if (string.Equals(paths[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>调用 T1 解析接口（异常转成错误文本，不抛出）</summary>
        private static bool TryResolveSafely(string shortcutPath, out ShortcutInfo info, out string error)
        {
            info = null;
            error = null;
            try
            {
                return ShortcutResolver.TryResolve(shortcutPath, out info, out error);
            }
            catch (Exception ex)
            {
                info = null;
                error = "快捷方式解析组件不可用：" + ex.Message;
                return false;
            }
        }

        /// <summary>调用 T1 改写接口（异常转成错误文本，不抛出）</summary>
        private static bool TryUpdateSafely(string shortcutPath, string newTarget, string newWorkingDirectory, out string error)
        {
            error = null;
            try
            {
                return ShortcutResolver.TryUpdate(shortcutPath, newTarget, newWorkingDirectory, out error);
            }
            catch (Exception ex)
            {
                error = "快捷方式写入组件不可用：" + ex.Message;
                return false;
            }
        }

        #endregion

        #region 悬空目标读取（最小 COM 封装，目标不存在时也能读到原始路径）

        /// <summary>一次读取结果：快捷方式路径 + 原始目标（目标不存在也能读到）+ 错误</summary>
        private sealed class RawTargetInfo
        {
            public string ShortcutPath;
            public string Target;
            public string Error;
        }

        /// <summary>批量读取快捷方式的原始目标（MTA 线程时统一转到临时 STA 线程，避免逐条建线程）</summary>
        private static List<RawTargetInfo> ReadRawTargetsBatch(List<string> shortcutPaths)
        {
            List<RawTargetInfo> results = new List<RawTargetInfo>();
            if (shortcutPaths == null || shortcutPaths.Count == 0)
            {
                return results;
            }
            if (!MustUseStaThread())
            {
                for (int i = 0; i < shortcutPaths.Count; i++)
                {
                    results.Add(ReadRawTargetCore(shortcutPaths[i]));
                }
                return results;
            }

            BatchRequest request = new BatchRequest();
            request.Paths = shortcutPaths;
            request.Results = results;
            Thread thread = new Thread(new ThreadStart(delegate()
            {
                for (int i = 0; i < request.Paths.Count; i++)
                {
                    request.Results.Add(ReadRawTargetCore(request.Paths[i]));
                }
            }));
            thread.IsBackground = true;
            try
            {
                thread.SetApartmentState(ApartmentState.STA);
            }
            catch (Exception)
            {
                // 设置套间失败时仍尝试执行
            }
            try
            {
                thread.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("无法启动快捷方式读取线程：" + ex.Message);
                return results;
            }
            if (!thread.Join(BatchTimeoutMs))
            {
                Log.Warn("批量读取快捷方式超时，已返回部分结果。");
            }
            // 线程可能在超时后才写入，复制一份保证调用方拿到的是稳定快照
            lock (request.Results)
            {
                return new List<RawTargetInfo>(request.Results);
            }
        }

        /// <summary>读取单个快捷方式的原始目标（要求当前线程可调用 COM）</summary>
        private static RawTargetInfo ReadRawTargetCore(string shortcutPath)
        {
            RawTargetInfo info = new RawTargetInfo();
            info.ShortcutPath = shortcutPath;
            object comObject = null;
            object persistObject = null;
            object linkObject = null;
            try
            {
                if (!ShortcutResolver.IsShortcutFile(shortcutPath))
                {
                    info.Error = "不是 .lnk 快捷方式文件";
                    return info;
                }
                comObject = CreateShellLink();
                if (comObject == null)
                {
                    info.Error = "无法创建系统快捷方式组件（COM 初始化失败）";
                    return info;
                }
                IPersistFile persistFile = comObject as IPersistFile;
                IShellLinkW shellLink = comObject as IShellLinkW;
                persistObject = persistFile;
                linkObject = shellLink;
                if (persistFile == null || shellLink == null)
                {
                    info.Error = "系统快捷方式组件不支持所需接口";
                    return info;
                }
                int hr = persistFile.Load(shortcutPath, StgmRead);
                if (Failed(hr))
                {
                    info.Error = "无法打开快捷方式文件：" + FormatHResult(hr);
                    return info;
                }
                string raw = ReadRawPath(shellLink);
                if (string.IsNullOrEmpty(raw))
                {
                    info.Error = "快捷方式没有记录目标路径";
                    return info;
                }
                info.Target = PathUtil.Normalize(ExpandPath(raw));
                if (string.IsNullOrEmpty(info.Target))
                {
                    info.Error = "快捷方式目标路径非法：" + raw;
                }
                return info;
            }
            catch (COMException ex)
            {
                info.Error = "读取快捷方式失败（COM 错误 0x" + ex.ErrorCode.ToString("X8") + "）：" + ex.Message;
                return info;
            }
            catch (Exception ex)
            {
                info.Error = "读取快捷方式失败：" + ex.Message;
                return info;
            }
            finally
            {
                ReleaseCom(linkObject);
                ReleaseCom(persistObject);
                ReleaseCom(comObject);
            }
        }

        /// <summary>用 SLGP_RAWPATH 读取快捷方式保存的原始目标路径</summary>
        private static string ReadRawPath(IShellLinkW shellLink)
        {
            StringBuilder buffer = new StringBuilder(PathBufferSize);
            int hr = shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SlgpRawPath);
            string raw = hr == 0 ? buffer.ToString() : string.Empty;
            if (raw.Length == 0)
            {
                buffer.Length = 0;
                hr = shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
                raw = hr == 0 ? buffer.ToString() : string.Empty;
            }
            return raw;
        }

        #endregion

        #region 创建快捷方式（最小 COM 封装）

        /// <summary>创建 .lnk 的请求容器</summary>
        private sealed class BatchRequest
        {
            public List<string> Paths = new List<string>();
            public List<RawTargetInfo> Results = new List<RawTargetInfo>();
        }

        private sealed class CreateRequest
        {
            public string LnkPath;
            public string TargetPath;
            public string WorkingDirectory;
            public string IconPath;
            public string Description;
            public bool Success;
            public string Error;
        }

        /// <summary>在当前线程或临时 STA 线程上创建快捷方式（异常/超时都转成中文错误）</summary>
        private static bool CreateShortcutSafe(string lnkPath, string targetPath, string workingDirectory,
            string iconPath, string description, out string error)
        {
            if (!MustUseStaThread())
            {
                return CreateShortcutCore(lnkPath, targetPath, workingDirectory, iconPath, description, out error);
            }

            CreateRequest request = new CreateRequest();
            request.LnkPath = lnkPath;
            request.TargetPath = targetPath;
            request.WorkingDirectory = workingDirectory;
            request.IconPath = iconPath;
            request.Description = description;
            Thread thread = new Thread(new ThreadStart(delegate()
            {
                string coreError;
                request.Success = CreateShortcutCore(request.LnkPath, request.TargetPath, request.WorkingDirectory,
                    request.IconPath, request.Description, out coreError);
                request.Error = coreError;
            }));
            thread.IsBackground = true;
            try
            {
                thread.SetApartmentState(ApartmentState.STA);
            }
            catch (Exception)
            {
                // 设置套间失败时仍尝试执行
            }
            try
            {
                thread.Start();
            }
            catch (Exception ex)
            {
                error = "无法启动快捷方式创建线程：" + ex.Message;
                return false;
            }
            if (!thread.Join(StaCallTimeoutMs))
            {
                error = "创建快捷方式超时（超过 15 秒）：" + lnkPath;
                return false;
            }
            error = request.Error;
            return request.Success;
        }

        /// <summary>真正写入快捷方式（要求当前线程为 STA）</summary>
        private static bool CreateShortcutCore(string lnkPath, string targetPath, string workingDirectory,
            string iconPath, string description, out string error)
        {
            error = null;
            object comObject = null;
            object persistObject = null;
            object linkObject = null;
            try
            {
                comObject = CreateShellLink();
                if (comObject == null)
                {
                    error = "无法创建系统快捷方式组件（COM 初始化失败）";
                    return false;
                }
                IPersistFile persistFile = comObject as IPersistFile;
                IShellLinkW shellLink = comObject as IShellLinkW;
                persistObject = persistFile;
                linkObject = shellLink;
                if (persistFile == null || shellLink == null)
                {
                    error = "系统快捷方式组件不支持所需接口";
                    return false;
                }

                int hr = shellLink.SetPath(targetPath);
                if (Failed(hr))
                {
                    error = "设置快捷方式目标失败：" + FormatHResult(hr);
                    return false;
                }
                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    hr = shellLink.SetWorkingDirectory(workingDirectory);
                    if (Failed(hr))
                    {
                        Log.Warn("设置快捷方式工作目录失败：" + FormatHResult(hr));
                    }
                }
                if (!string.IsNullOrEmpty(iconPath))
                {
                    hr = shellLink.SetIconLocation(iconPath, 0);
                    if (Failed(hr))
                    {
                        Log.Warn("设置快捷方式图标失败：" + FormatHResult(hr));
                    }
                }
                if (!string.IsNullOrEmpty(description))
                {
                    hr = shellLink.SetDescription(description);
                    if (Failed(hr))
                    {
                        Log.Warn("设置快捷方式描述失败：" + FormatHResult(hr));
                    }
                }
                hr = persistFile.Save(lnkPath, true);
                if (Failed(hr))
                {
                    error = "保存快捷方式失败（可能没有写入权限）：" + FormatHResult(hr);
                    return false;
                }
                return true;
            }
            catch (COMException ex)
            {
                error = "创建快捷方式失败（COM 错误 0x" + ex.ErrorCode.ToString("X8") + "）：" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "创建快捷方式失败：" + ex.Message;
                return false;
            }
            finally
            {
                ReleaseCom(linkObject);
                ReleaseCom(persistObject);
                ReleaseCom(comObject);
            }
        }

        #endregion

        #region COM 基础设施

        /// <summary>创建 ShellLink COM 对象（失败返回 null）</summary>
        private static object CreateShellLink()
        {
            try
            {
                Type type = Type.GetTypeFromCLSID(ShellLinkClsid);
                if (type == null)
                {
                    return null;
                }
                return Activator.CreateInstance(type);
            }
            catch (COMException ex)
            {
                Log.Warn("创建 ShellLink COM 对象失败：0x" + ex.ErrorCode.ToString("X8") + " " + ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("创建 ShellLink COM 对象失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>当前线程是否不是 STA（COM 调用需要转到临时 STA 线程）</summary>
        private static bool MustUseStaThread()
        {
            try
            {
                return Thread.CurrentThread.GetApartmentState() != ApartmentState.STA;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>扩展环境变量、去引号、去空白</summary>
        private static string ExpandPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }
            string value = path.Trim();
            try
            {
                value = Environment.ExpandEnvironmentVariables(value);
            }
            catch (Exception)
            {
                // 展开失败时按原文处理
            }
            return value.Trim().Trim('"').Trim();
        }

        private static bool Failed(int hr)
        {
            return hr < 0;
        }

        private static string FormatHResult(int hr)
        {
            return "0x" + hr.ToString("X8");
        }

        private static void ReleaseCom(object comObject)
        {
            if (comObject == null)
            {
                return;
            }
            try
            {
                if (Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch (Exception)
            {
                // 释放失败忽略
            }
        }

        /// <summary>IShellLinkW（IID 000214F9-0000-0000-C000-000000000046），方法顺序必须与原生 vtable 一致</summary>
        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            [PreserveSig]
            int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            [PreserveSig]
            int GetIDList(out IntPtr ppidl);
            [PreserveSig]
            int SetIDList(IntPtr pidl);
            [PreserveSig]
            int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            [PreserveSig]
            int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            [PreserveSig]
            int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            [PreserveSig]
            int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            [PreserveSig]
            int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            [PreserveSig]
            int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            [PreserveSig]
            int GetHotkey(out ushort pwHotkey);
            [PreserveSig]
            int SetHotkey(ushort wHotkey);
            [PreserveSig]
            int GetShowCmd(out int piShowCmd);
            [PreserveSig]
            int SetShowCmd(int iShowCmd);
            [PreserveSig]
            int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            [PreserveSig]
            int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            [PreserveSig]
            int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            [PreserveSig]
            int Resolve(IntPtr hwnd, uint fFlags);
            [PreserveSig]
            int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        /// <summary>IPersistFile（IID 0000010b-0000-0000-C000-000000000046）</summary>
        [ComImport]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            [PreserveSig]
            int GetClassID(out Guid pClassID);
            [PreserveSig]
            int IsDirty();
            [PreserveSig]
            int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            [PreserveSig]
            int Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            [PreserveSig]
            int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            [PreserveSig]
            int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        #endregion
    }
}
