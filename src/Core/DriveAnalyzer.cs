// T1 实现：磁盘容量分析与默认目标盘推荐
using System;
using System.Collections.Generic;
using System.IO;

namespace YijianNuodong.Core
{
    /// <summary>固定磁盘分析：列出本地磁盘、查询路径容量、为软件推荐搬家目标目录</summary>
    public static class DriveAnalyzer
    {
        /// <summary>大小未知时按 2GB 估算所需空间</summary>
        private const long DefaultEstimatedSize = 2L * 1024 * 1024 * 1024;

        /// <summary>推荐目标盘时预留的余量（300MB）</summary>
        private const long SpaceMargin = 300L * 1024 * 1024;

        /// <summary>所有已就绪的固定磁盘（按盘符排序）</summary>
        public static List<DriveInfoItem> GetFixedDrives()
        {
            List<DriveInfoItem> result = new List<DriveInfoItem>();
            string systemRoot = GetSystemDriveRoot();

            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception ex)
            {
                Log.Warn("枚举磁盘失败：" + ex.Message);
                return result;
            }

            for (int i = 0; i < drives.Length; i++)
            {
                DriveInfo drive = drives[i];
                if (drive == null) continue;
                try
                {
                    if (drive.DriveType != DriveType.Fixed) continue;
                    if (!drive.IsReady) continue;

                    DriveInfoItem item = new DriveInfoItem();
                    item.RootPath = NormalizeRoot(drive);
                    if (string.IsNullOrEmpty(item.RootPath)) continue;
                    item.TotalBytes = drive.TotalSize;
                    item.FreeBytes = drive.AvailableFreeSpace;
                    item.IsSystemDrive = string.Equals(item.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase);
                    // 卷标/文件系统在个别情况下会抛异常，单独容错
                    try { item.VolumeLabel = drive.VolumeLabel; } catch { item.VolumeLabel = string.Empty; }
                    try { item.FileSystem = drive.DriveFormat; } catch { item.FileSystem = string.Empty; }
                    result.Add(item);
                }
                catch
                {
                    // 单个磁盘读取失败（无权限、设备未就绪等）不影响其它磁盘
                }
            }

            result.Sort(delegate(DriveInfoItem a, DriveInfoItem b)
            {
                string an = a.RootPath == null ? string.Empty : a.RootPath;
                string bn = b.RootPath == null ? string.Empty : b.RootPath;
                return string.Compare(an, bn, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>路径所在的固定磁盘；路径为空、在可移动/网络磁盘上时返回 null</summary>
        public static DriveInfoItem GetDriveForPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            path = StripExtendedPrefix(path.Trim());
            string root = PathUtil.GetDriveRoot(path);
            if (string.IsNullOrEmpty(root)) return null;

            List<DriveInfoItem> drives = GetFixedDrives();
            for (int i = 0; i < drives.Count; i++)
            {
                if (string.Equals(drives[i].RootPath, root, StringComparison.OrdinalIgnoreCase)) return drives[i];
            }
            return null;
        }

        /// <summary>
        /// 查询路径所在磁盘的剩余/总容量。路径不存在、网络路径、非固定磁盘时返回 false，不抛异常。
        /// </summary>
        public static bool TryGetSpace(string path, out long freeBytes, out long totalBytes)
        {
            freeBytes = -1;
            totalBytes = -1;
            if (string.IsNullOrEmpty(path)) return false;
            path = StripExtendedPrefix(path.Trim());
            // 网络路径（\\server\share）不做容量查询
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;

            bool exists;
            try { exists = Directory.Exists(path) || File.Exists(path); }
            catch { exists = false; }
            if (!exists) return false;

            string root = PathUtil.GetDriveRoot(path);
            if (string.IsNullOrEmpty(root)) return false;

            try
            {
                DriveInfo drive = new DriveInfo(root);
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return false;
                freeBytes = drive.AvailableFreeSpace;
                totalBytes = drive.TotalSize;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("读取磁盘容量失败（" + path + "）：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 为软件推荐默认目标父目录：优先选非系统盘中可用空间最大且满足需求的盘，
        /// 返回形如 "D:\Software"；没有非系统盘时返回空字符串。
        /// </summary>
        public static string SuggestDefaultTarget(InstalledProgram program)
        {
            long required = DefaultEstimatedSize;
            if (program != null && program.SizeBytes > 0) required = program.SizeBytes;
            long needed = required + SpaceMargin;

            List<DriveInfoItem> drives = GetFixedDrives();
            DriveInfoItem largest = null;   // 非系统盘中可用空间最大
            DriveInfoItem bestFit = null;   // 满足空间需求的非系统盘中可用空间最大
            for (int i = 0; i < drives.Count; i++)
            {
                DriveInfoItem drive = drives[i];
                if (drive.IsSystemDrive || drive.TotalBytes <= 0) continue;
                if (largest == null || drive.FreeBytes > largest.FreeBytes) largest = drive;
                if (drive.FreeBytes >= needed && (bestFit == null || drive.FreeBytes > bestFit.FreeBytes))
                {
                    bestFit = drive;
                }
            }

            DriveInfoItem chosen = bestFit != null ? bestFit : largest;
            if (chosen == null)
            {
                Log.Warn("没有可用的非系统固定磁盘，无法推荐搬家目标盘");
                return string.Empty;
            }
            if (bestFit == null)
            {
                Log.Warn("非系统盘空间不足以容纳该软件（需要 " + TextUtil.FormatBytes(needed) +
                         "），已推荐可用空间最大的磁盘 " + chosen.RootPath);
            }
            return Path.Combine(chosen.RootPath, "Software");
        }

        /// <summary>系统盘根目录（"C:\"），取不到时返回空字符串</summary>
        private static string GetSystemDriveRoot()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string root = PathUtil.GetDriveRoot(windows);
            return string.IsNullOrEmpty(root) ? string.Empty : root;
        }

        /// <summary>去掉 \\?\ 或 \\?\UNC\ 长路径前缀</summary>
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

        /// <summary>把 DriveInfo 规范成 "C:\" 形式</summary>
        private static string NormalizeRoot(DriveInfo drive)
        {
            try
            {
                string root = drive.RootDirectory.FullName;
                if (string.IsNullOrEmpty(root)) return string.Empty;
                string normalized = PathUtil.Normalize(root);
                if (normalized.Length == 0) return string.Empty;
                if (!normalized.EndsWith("\\", StringComparison.Ordinal)) normalized += "\\";
                return normalized;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
