// T1 实现：快捷方式解析（COM IShellLinkW + IPersistFile，不依赖任何 Interop 程序集）
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YijianNuodong.Core
{
    /// <summary>
    /// .lnk 快捷方式解析与改写：目标程序、工作目录、启动参数、图标、描述。
    /// 直接通过 COM 互操作调用系统 ShellLink 组件；所有失败都转换为中文错误说明，不向调用方抛异常。
    /// 注：ShellLink 是单线程套间（STA）COM 对象，若在 MTA 后台线程调用，会自动转到临时 STA 线程执行。
    /// </summary>
    public static class ShortcutResolver
    {
        // SLGP_RAWPATH：返回快捷方式里保存的原始路径（可能含环境变量）
        private const uint SlgpRawPath = 0x00000004;
        // IPersistFile::Load 的打开模式
        private const uint StgmRead = 0x00000000;
        private const uint StgmReadWrite = 0x00000002;

        private const int PathBufferSize = 1024;          // 允许超过 MAX_PATH（260）的路径
        private const int StaCallTimeoutMs = 15000;       // 单次 STA 调用超时
        private const int BatchTimeoutMs = 60000;         // 批量解析快捷方式总超时
        private const int MaxEnumerateDepth = 8;          // 开始菜单/桌面递归深度上限

        private static readonly Guid ShellLinkClsid = new Guid("00021401-0000-0000-C000-000000000046");

        /// <summary>是否为 .lnk 快捷方式文件（仅看扩展名，不区分大小写）</summary>
        public static bool IsShortcutFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                return string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>解析快捷方式；失败返回 false 并给出中文错误说明。目标必须是已存在的文件（exe 等）</summary>
        public static bool TryResolve(string shortcutPath, out ShortcutInfo info, out string error)
        {
            return TryResolveInternal(shortcutPath, false, out info, out error);
        }

        /// <summary>
        /// 解析快捷方式（内部使用）：目标可以是文件也可以是文件夹，供"拖入快捷方式定位"场景使用。
        /// </summary>
        internal static bool TryResolveLoose(string shortcutPath, out ShortcutInfo info, out string error)
        {
            return TryResolveInternal(shortcutPath, true, out info, out error);
        }

        /// <summary>解析快捷方式（失败抛异常，由调用方捕获）</summary>
        public static ShortcutInfo Resolve(string shortcutPath)
        {
            ShortcutInfo info;
            string error;
            if (!TryResolveInternal(shortcutPath, true, out info, out error))
            {
                throw new InvalidOperationException(error);
            }
            return info;
        }

        /// <summary>
        /// 修改快捷方式指向（搬家后调用）：载入 -> 设置新目标 -> 设置工作目录（为空则用新目标所在目录）-> 保存。
        /// </summary>
        public static bool TryUpdate(string shortcutPath, string newTargetPath, string newWorkingDirectory, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(shortcutPath))
            {
                error = "快捷方式路径为空";
                return false;
            }
            string shortcut;
            try { shortcut = PathUtil.Normalize(shortcutPath); }
            catch { shortcut = shortcutPath; }

            if (!IsShortcutFile(shortcut))
            {
                error = "不是 .lnk 快捷方式文件：" + shortcut;
                return false;
            }
            bool exists;
            try { exists = File.Exists(shortcut); }
            catch { exists = false; }
            if (!exists)
            {
                error = "快捷方式文件不存在：" + shortcut;
                return false;
            }

            string target = ExpandPath(newTargetPath);
            if (target.Length == 0)
            {
                error = "新的目标路径为空";
                return false;
            }
            if (!Path.IsPathRooted(target))
            {
                error = "新的目标路径必须是绝对路径：" + target;
                return false;
            }
            try { target = PathUtil.Normalize(target); }
            catch { }

            string workingDirectory = ExpandPath(newWorkingDirectory);
            if (workingDirectory.Length > 0 && !Path.IsPathRooted(workingDirectory))
            {
                workingDirectory = string.Empty; // 工作目录非法时退回新目标所在目录
            }

            if (MustUseStaThread())
            {
                return UpdateOnStaThread(shortcut, target, workingDirectory, out error);
            }
            try
            {
                return UpdateCore(shortcut, target, workingDirectory, out error);
            }
            catch (COMException ex)
            {
                error = "更新快捷方式失败（COM 错误 0x" + ex.ErrorCode.ToString("X8") + "）：" + ex.Message;
                Log.Warn(error);
                return false;
            }
            catch (Exception ex)
            {
                error = "更新快捷方式失败：" + ex.Message;
                Log.Warn(error);
                return false;
            }
        }

        /// <summary>枚举桌面(用户/公共)与开始菜单(用户/公共)以及启动项下所有 .lnk 绝对路径</summary>
        public static List<string> EnumerateShortcuts()
        {
            List<string> roots = new List<string>();
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.DesktopDirectory);
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.CommonDesktopDirectory);
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.Programs);
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.CommonPrograms);
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.Startup);
            AddSpecialFolderRoot(roots, Environment.SpecialFolder.CommonStartup);

            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < roots.Count; i++)
            {
                EnumerateLnkRecursive(roots[i], 0, result, seen);
            }
            result.Sort(delegate(string a, string b)
            {
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>批量解析快捷方式（内部使用，一次 STA 线程完成，避免逐条创建线程）；失败项自动跳过</summary>
        internal static List<ShortcutInfo> ResolveMany(IEnumerable<string> shortcutPaths)
        {
            List<ShortcutInfo> result = new List<ShortcutInfo>();
            List<string> paths = new List<string>();
            if (shortcutPaths != null)
            {
                foreach (string path in shortcutPaths)
                {
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
            }
            if (paths.Count == 0) return result;

            if (!MustUseStaThread())
            {
                ResolveManyCore(paths, result);
                return result;
            }

            object syncRoot = new object();
            Thread thread = new Thread(new ThreadStart(delegate()
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    ShortcutInfo info;
                    string error;
                    try
                    {
                        if (ResolveCore(paths[i], true, out info, out error) && info != null)
                        {
                            lock (syncRoot) { result.Add(info); }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("解析快捷方式失败（" + paths[i] + "）：" + ex.Message);
                    }
                }
            }));
            thread.IsBackground = true;
            try { thread.SetApartmentState(ApartmentState.STA); }
            catch { }
            try { thread.Start(); }
            catch (Exception ex)
            {
                Log.Warn("无法启动快捷方式解析线程：" + ex.Message);
                return result;
            }
            if (!thread.Join(BatchTimeoutMs))
            {
                Log.Warn("批量解析快捷方式超时，已返回部分结果");
                lock (syncRoot) { return new List<ShortcutInfo>(result); }
            }
            return result;
        }

        // ============================ 内部实现 ============================

        /// <summary>解析核心逻辑（在当前线程执行，要求当前线程是 STA）</summary>
        private static bool TryResolveInternal(string shortcutPath, bool allowDirectoryTarget, out ShortcutInfo info, out string error)
        {
            info = null;
            error = null;
            if (string.IsNullOrEmpty(shortcutPath))
            {
                error = "快捷方式路径为空";
                return false;
            }

            string shortcut;
            try { shortcut = PathUtil.Normalize(shortcutPath); }
            catch { shortcut = shortcutPath.Trim(); }
            if (!IsShortcutFile(shortcut))
            {
                error = "不是 .lnk 快捷方式文件：" + shortcut;
                return false;
            }
            bool exists;
            try { exists = File.Exists(shortcut); }
            catch { exists = false; }
            if (!exists)
            {
                error = "快捷方式文件不存在：" + shortcut;
                return false;
            }

            if (MustUseStaThread())
            {
                return ResolveOnStaThread(shortcut, allowDirectoryTarget, out info, out error);
            }
            try
            {
                return ResolveCore(shortcut, allowDirectoryTarget, out info, out error);
            }
            catch (COMException ex)
            {
                error = "解析快捷方式失败（COM 错误 0x" + ex.ErrorCode.ToString("X8") + "）：" + ex.Message;
                Log.Warn(error);
                return false;
            }
            catch (Exception ex)
            {
                error = "解析快捷方式失败：" + ex.Message;
                Log.Warn(error);
                return false;
            }
        }

        /// <summary>在当前 STA 线程上真正读取快捷方式内容</summary>
        private static bool ResolveCore(string lnkPath, bool allowDirectoryTarget, out ShortcutInfo info, out string error)
        {
            info = null;
            error = null;
            object comObject = null;
            object persistObject = null;
            object linkObject = null;
            try
            {
                comObject = CreateShellLink();
                if (comObject == null)
                {
                    error = "无法创建系统快捷方式解析组件（COM 初始化失败）";
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

                int hr = persistFile.Load(lnkPath, StgmRead);
                if (Failed(hr))
                {
                    error = "无法打开快捷方式文件（可能已损坏或被占用）：" + FormatHResult(hr);
                    return false;
                }

                string rawTarget = ReadTarget(shellLink);
                string workingDirectory = ReadWorkingDirectory(shellLink);
                string arguments = ReadArguments(shellLink);
                string description = ReadDescription(shellLink);
                string iconLocation = ReadIconLocation(shellLink);

                string expanded = ExpandPath(rawTarget);
                if (expanded.Length == 0)
                {
                    error = "快捷方式没有记录有效的目标路径（可能是应用商店应用或已损坏）";
                    return false;
                }
                if (!Path.IsPathRooted(expanded))
                {
                    error = "快捷方式使用相对路径指向目标，无法定位安装目录：" + expanded;
                    return false;
                }

                string target;
                try { target = PathUtil.Normalize(expanded); }
                catch
                {
                    error = "快捷方式目标路径非法：" + expanded;
                    return false;
                }
                if (target.Length == 0)
                {
                    error = "快捷方式目标路径非法：" + expanded;
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
                if (!isFile && !isDirectory)
                {
                    error = "快捷方式指向的目标不存在：" + target;
                    return false;
                }
                if (isDirectory && !allowDirectoryTarget)
                {
                    error = "快捷方式指向的是文件夹而不是程序文件：" + target;
                    return false;
                }

                ShortcutInfo result = new ShortcutInfo();
                result.ShortcutPath = lnkPath;
                result.TargetPath = target;
                result.Arguments = arguments;
                result.WorkingDirectory = NormalizeOptionalDirectory(workingDirectory);
                result.IconLocation = iconLocation;
                result.Description = description;
                info = result;
                return true;
            }
            catch (COMException ex)
            {
                error = "解析快捷方式失败（COM 错误 0x" + ex.ErrorCode.ToString("X8") + "）：" + ex.Message;
                Log.Warn(error);
                return false;
            }
            catch (Exception ex)
            {
                error = "解析快捷方式失败：" + ex.Message;
                Log.Warn(error);
                return false;
            }
            finally
            {
                ReleaseCom(linkObject);
                ReleaseCom(persistObject);
                ReleaseCom(comObject);
            }
        }

        /// <summary>真正写入快捷方式（在当前 STA 线程执行）</summary>
        private static bool UpdateCore(string lnkPath, string newTarget, string newWorkingDirectory, out string error)
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

                int hr = persistFile.Load(lnkPath, StgmReadWrite);
                if (Failed(hr))
                {
                    error = "无法以读写方式打开快捷方式（可能没有写入权限）：" + FormatHResult(hr);
                    return false;
                }

                hr = shellLink.SetPath(newTarget);
                if (Failed(hr))
                {
                    error = "设置快捷方式目标失败：" + FormatHResult(hr);
                    return false;
                }

                string workingDirectory = newWorkingDirectory;
                if (string.IsNullOrEmpty(workingDirectory))
                {
                    try { workingDirectory = Path.GetDirectoryName(newTarget); }
                    catch { workingDirectory = string.Empty; }
                }
                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    hr = shellLink.SetWorkingDirectory(workingDirectory);
                    if (Failed(hr))
                    {
                        // 工作目录设置失败不影响"指向"本身，仅记录警告
                        Log.Warn("设置快捷方式工作目录失败：" + FormatHResult(hr));
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
                error = "更新快捷方式失败（COM 错误 0x" + ex.ErrorCode.ToString("X8") + "）：" + ex.Message;
                Log.Warn(error);
                return false;
            }
            catch (Exception ex)
            {
                error = "更新快捷方式失败：" + ex.Message;
                Log.Warn(error);
                return false;
            }
            finally
            {
                ReleaseCom(linkObject);
                ReleaseCom(persistObject);
                ReleaseCom(comObject);
            }
        }

        /// <summary>在临时 STA 线程上解析（MTA 后台线程调用 COM 不可靠时使用）</summary>
        private static bool ResolveOnStaThread(string lnkPath, bool allowDirectoryTarget, out ShortcutInfo info, out string error)
        {
            ResolveRequest request = new ResolveRequest();
            request.LnkPath = lnkPath;
            request.AllowDirectory = allowDirectoryTarget;
            Thread thread = new Thread(new ThreadStart(delegate()
            {
                request.Success = ResolveCore(request.LnkPath, request.AllowDirectory, out request.Info, out request.Error);
            }));
            thread.IsBackground = true;
            try { thread.SetApartmentState(ApartmentState.STA); }
            catch { }
            try { thread.Start(); }
            catch (Exception ex)
            {
                info = null;
                error = "无法启动快捷方式解析线程：" + ex.Message;
                return false;
            }
            if (!thread.Join(StaCallTimeoutMs))
            {
                info = null;
                error = "解析快捷方式超时（超过 15 秒）：" + lnkPath;
                return false;
            }
            info = request.Info;
            error = request.Error;
            return request.Success;
        }

        /// <summary>在临时 STA 线程上更新快捷方式</summary>
        private static bool UpdateOnStaThread(string lnkPath, string newTarget, string newWorkingDirectory, out string error)
        {
            UpdateRequest request = new UpdateRequest();
            request.LnkPath = lnkPath;
            request.NewTarget = newTarget;
            request.WorkingDirectory = newWorkingDirectory;
            Thread thread = new Thread(new ThreadStart(delegate()
            {
                request.Success = UpdateCore(request.LnkPath, request.NewTarget, request.WorkingDirectory, out request.Error);
            }));
            thread.IsBackground = true;
            try { thread.SetApartmentState(ApartmentState.STA); }
            catch { }
            try { thread.Start(); }
            catch (Exception ex)
            {
                error = "无法启动快捷方式更新线程：" + ex.Message;
                return false;
            }
            if (!thread.Join(StaCallTimeoutMs))
            {
                error = "更新快捷方式超时（超过 15 秒）：" + lnkPath;
                return false;
            }
            error = request.Error;
            return request.Success;
        }

        /// <summary>批量解析（当前线程为 STA 时直接顺序执行）</summary>
        private static void ResolveManyCore(List<string> paths, List<ShortcutInfo> result)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                ShortcutInfo info;
                string error;
                try
                {
                    if (ResolveCore(paths[i], true, out info, out error) && info != null) result.Add(info);
                }
                catch (Exception ex)
                {
                    Log.Warn("解析快捷方式失败（" + paths[i] + "）：" + ex.Message);
                }
            }
        }

        // ---------------------------- COM 读取辅助 ----------------------------

        private static string ReadTarget(IShellLinkW shellLink)
        {
            StringBuilder buffer = new StringBuilder(PathBufferSize);
            int hr = shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SlgpRawPath);
            string raw = hr == 0 ? buffer.ToString() : string.Empty;
            if (raw.Length == 0)
            {
                // 部分快捷方式用 RAWPATH 取不到，退回默认解析
                buffer.Length = 0;
                hr = shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
                raw = hr == 0 ? buffer.ToString() : string.Empty;
            }
            return raw;
        }

        private static string ReadWorkingDirectory(IShellLinkW shellLink)
        {
            StringBuilder buffer = new StringBuilder(PathBufferSize);
            int hr = shellLink.GetWorkingDirectory(buffer, buffer.Capacity);
            return hr == 0 ? buffer.ToString() : string.Empty;
        }

        private static string ReadArguments(IShellLinkW shellLink)
        {
            StringBuilder buffer = new StringBuilder(PathBufferSize);
            int hr = shellLink.GetArguments(buffer, buffer.Capacity);
            return hr == 0 ? buffer.ToString() : string.Empty;
        }

        private static string ReadDescription(IShellLinkW shellLink)
        {
            StringBuilder buffer = new StringBuilder(PathBufferSize);
            int hr = shellLink.GetDescription(buffer, buffer.Capacity);
            return hr == 0 ? buffer.ToString() : string.Empty;
        }

        private static string ReadIconLocation(IShellLinkW shellLink)
        {
            StringBuilder buffer = new StringBuilder(PathBufferSize);
            int iconIndex;
            int hr = shellLink.GetIconLocation(buffer, buffer.Capacity, out iconIndex);
            if (Failed(hr)) return string.Empty;
            string path = buffer.ToString();
            if (path.Length == 0) return string.Empty;
            if (iconIndex <= 0) return path;
            return path + "," + iconIndex.ToString();
        }

        // ---------------------------- 通用小工具 ----------------------------

        /// <summary>创建 ShellLink COM 对象（失败返回 null）</summary>
        private static object CreateShellLink()
        {
            try
            {
                Type type = Type.GetTypeFromCLSID(ShellLinkClsid);
                if (type == null) return null;
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

        /// <summary>当前线程是否不是 STA（需要转到临时 STA 线程调用 COM）</summary>
        private static bool MustUseStaThread()
        {
            try
            {
                return Thread.CurrentThread.GetApartmentState() != ApartmentState.STA;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>扩展环境变量、去引号、去空白</summary>
        private static string ExpandPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string value = path.Trim();
            try { value = Environment.ExpandEnvironmentVariables(value); }
            catch { }
            return value.Trim().Trim('"').Trim();
        }

        /// <summary>工作目录规范化（不存在或非绝对路径时原样返回去引号结果）</summary>
        private static string NormalizeOptionalDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return string.Empty;
            string expanded = ExpandPath(directory);
            if (expanded.Length == 0) return string.Empty;
            if (!Path.IsPathRooted(expanded)) return expanded;
            try { return PathUtil.Normalize(expanded); }
            catch { return expanded; }
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
            if (comObject == null) return;
            try
            {
                if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
            }
            catch
            {
            }
        }

        private static void AddSpecialFolderRoot(List<string> roots, Environment.SpecialFolder folder)
        {
            try
            {
                string path = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(path)) return;
                string normalized = PathUtil.Normalize(path);
                if (normalized.Length == 0) return;
                if (!roots.Contains(normalized)) roots.Add(normalized);
            }
            catch
            {
            }
        }

        /// <summary>递归枚举目录下所有 .lnk（跳过重解析点，深度受限，单目录失败不影响整体）</summary>
        private static void EnumerateLnkRecursive(string root, int startDepth, List<string> result, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(root)) return;
            bool exists;
            try { exists = Directory.Exists(root); }
            catch { exists = false; }
            if (!exists) return;

            Stack<KeyValuePair<string, int>> stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, startDepth));
            while (stack.Count > 0)
            {
                KeyValuePair<string, int> current = stack.Pop();
                string directory = current.Key;
                int depth = current.Value;

                string[] files;
                try { files = Directory.GetFiles(directory, "*.lnk"); }
                catch { files = new string[0]; }
                for (int i = 0; i < files.Length; i++)
                {
                    if (!IsShortcutFile(files[i])) continue;
                    if (seen.Add(files[i])) result.Add(files[i]);
                }

                if (depth >= MaxEnumerateDepth) continue;
                string[] subDirectories;
                try { subDirectories = Directory.GetDirectories(directory); }
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
                    stack.Push(new KeyValuePair<string, int>(subDirectories[i], depth + 1));
                }
            }
        }

        // ---------------------------- COM 接口声明 ----------------------------

        /// <summary>IShellLinkW（IID 000214F9-0000-0000-C000-000000000046）</summary>
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
            // 注意：GetShowCmd / SetShowCmd 不能省略，否则后续方法（GetIconLocation / SetPath 等）的
            // vtable 槽位会整体错位，SetPath 将变成空操作（这是实际踩过的坑）
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

        /// <summary>STA 解析请求参数容器</summary>
        private sealed class ResolveRequest
        {
            public string LnkPath;
            public bool AllowDirectory;
            public ShortcutInfo Info;
            public string Error;
            public bool Success;
        }

        /// <summary>STA 更新请求参数容器</summary>
        private sealed class UpdateRequest
        {
            public string LnkPath;
            public string NewTarget;
            public string WorkingDirectory;
            public string Error;
            public bool Success;
        }
    }
}
