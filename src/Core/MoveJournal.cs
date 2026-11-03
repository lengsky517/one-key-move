// ============================================================================
//  一键挪动 (OneKeyMove) - 搬家日记（撤销依据）
//  文件位置：%ProgramData%\YijianNuodong\journal\yyyyMMdd_HHmmss_<安全叶名>.journal
//  格式说明（纯文本，值一律 Base64(UTF8)，避免路径/引号/等号破坏格式）：
//      # 一键挪动 搬家日记 v1
//      [move]
//      version=<B64>
//      createdutc=<B64>
//      oldpath=<B64>
//      ...
//      [shortcut]
//      path=<B64>
//      [reg]
//      <B64 hive>|<B64 key>|<B64 name>|<B64 kind>|<B64 old>|<B64 new>
//  说明：%ProgramData% 不可写时（非管理员/受限环境）自动退到用户临时目录，
//        保证“搬家一定有撤销依据”，不会出现搬完无法撤销的情况。
//  目标框架：.NET Framework 4.8 / C# 5
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace YijianNuodong.Core
{
    /// <summary>搬家日记的读、写、列举与标记撤销</summary>
    public static class MoveJournal
    {
        private const string HeaderLine = "# 一键挪动 搬家日记 v1";
        private const string SectionMove = "move";
        private const string SectionShortcut = "shortcut";
        private const string SectionReg = "reg";

        private static readonly object SyncRoot = new object();
        private static readonly ConditionalWeakTable<JournalEntry, string> EntryFiles =
            new ConditionalWeakTable<JournalEntry, string>();

        private static string _cachedDirectory;

        /// <summary>写入日记，返回文件路径；所有候选目录都不可写时返回 null</summary>
        public static string Write(JournalEntry entry)
        {
            if (entry == null)
            {
                return null;
            }
            List<string> lines = BuildLines(entry);
            List<string> directories = CandidateDirectories();
            for (int i = 0; i < directories.Count; i++)
            {
                string directory = directories[i];
                if (string.IsNullOrEmpty(directory))
                {
                    continue;
                }
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    string path = BuildFilePath(directory, entry);
                    File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
                    _cachedDirectory = directory;
                    try
                    {
                        EntryFiles.Remove(entry);
                        EntryFiles.Add(entry, path);
                    }
                    catch (Exception)
                    {
                        // 关联失败不影响日记本身
                    }
                    Log.Info("搬家日记已写入：" + path);
                    return path;
                }
                catch (Exception ex)
                {
                    Log.Warn("写入搬家日记失败（" + directory + "）：" + ex.Message);
                }
            }
            Log.Error("搬家日记写入失败：所有候选目录均不可写，本次搬家将无法自动撤销。");
            return null;
        }

        /// <summary>读取日记；文件不存在或格式无法识别时返回 null</summary>
        public static JournalEntry Read(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Warn("读取搬家日记失败：" + filePath + "：" + ex.Message);
                return null;
            }

            JournalEntry entry = new JournalEntry();
            string section = string.Empty;
            bool hasMoveSection = false;
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null)
                    {
                        continue;
                    }
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }
                    if (trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                    {
                        section = trimmed.Substring(1, trimmed.Length - 2).Trim().ToLowerInvariant();
                        if (section == SectionMove)
                        {
                            hasMoveSection = true;
                        }
                        continue;
                    }

                    if (section == SectionMove)
                    {
                        int index = trimmed.IndexOf('=');
                        if (index <= 0)
                        {
                            continue;
                        }
                        string key = trimmed.Substring(0, index).Trim().ToLowerInvariant();
                        string value = Decode(trimmed.Substring(index + 1));
                        ApplyMoveField(entry, key, value);
                    }
                    else if (section == SectionShortcut)
                    {
                        int index = trimmed.IndexOf('=');
                        if (index <= 0)
                        {
                            continue;
                        }
                        string key = trimmed.Substring(0, index).Trim().ToLowerInvariant();
                        if (key == "path")
                        {
                            string value = Decode(trimmed.Substring(index + 1));
                            if (!string.IsNullOrEmpty(value))
                            {
                                entry.UpdatedShortcuts.Add(value);
                            }
                        }
                    }
                    else if (section == SectionReg)
                    {
                        string[] parts = trimmed.Split('|');
                        if (parts.Length < 6)
                        {
                            continue;
                        }
                        RegistryChange change = new RegistryChange();
                        change.Hive = Decode(parts[0]);
                        change.KeyPath = Decode(parts[1]);
                        change.ValueName = Decode(parts[2]);
                        change.ValueKind = Decode(parts[3]);
                        change.OldValue = Decode(parts[4]);
                        change.NewValue = Decode(parts[5]);
                        entry.RegistryChanges.Add(change);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("解析搬家日记失败：" + filePath + "：" + ex.Message);
                return null;
            }

            if (!hasMoveSection || string.IsNullOrEmpty(entry.OldPath) || string.IsNullOrEmpty(entry.NewPath))
            {
                Log.Warn("搬家日记缺少必要字段，视为损坏：" + filePath);
                return null;
            }
            if (entry.CreatedUtc == DateTime.MinValue)
            {
                entry.CreatedUtc = SafeLastWriteTimeUtc(filePath);
            }
            try
            {
                EntryFiles.Remove(entry);
                EntryFiles.Add(entry, filePath);
            }
            catch (Exception)
            {
                // 忽略：定位文件时还有按内容匹配的兜底
            }
            return entry;
        }

        /// <summary>生成新的日记文件路径（不写入）</summary>
        public static string NewJournalPath()
        {
            List<string> directories = CandidateDirectories();
            for (int i = 0; i < directories.Count; i++)
            {
                string directory = directories[i];
                if (string.IsNullOrEmpty(directory))
                {
                    continue;
                }
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    _cachedDirectory = directory;
                    return BuildFilePath(directory, null);
                }
                catch (Exception ex)
                {
                    Log.Warn("准备日记目录失败（" + directory + "）：" + ex.Message);
                }
            }
            string fallback = Path.Combine(Path.GetTempPath(), "YijianNuodong", "journal");
            try
            {
                Directory.CreateDirectory(fallback);
            }
            catch (Exception)
            {
                // 连临时目录都不可写时，返回路径本身，写日记时再报错
            }
            return BuildFilePath(fallback, null);
        }

        /// <summary>列出所有日记文件（最新在前）</summary>
        public static List<string> ListFiles()
        {
            List<string> files = new List<string>();
            List<string> directories = ExistingDirectories();
            for (int i = 0; i < directories.Count; i++)
            {
                string directory = directories[i];
                try
                {
                    string[] found = Directory.GetFiles(directory, "*.journal");
                    for (int j = 0; j < found.Length; j++)
                    {
                        bool exists = false;
                        for (int k = 0; k < files.Count; k++)
                        {
                            if (string.Equals(files[k], found[j], StringComparison.OrdinalIgnoreCase))
                            {
                                exists = true;
                                break;
                            }
                        }
                        if (!exists)
                        {
                            files.Add(found[j]);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("列举日记目录失败（" + directory + "）：" + ex.Message);
                }
            }

            files.Sort(CompareByTimeDescending);
            return files;
        }

        /// <summary>把日记标记为已撤销（写 undone=1，保留原有内容）</summary>
        public static void MarkUndone(JournalEntry entry)
        {
            if (entry == null)
            {
                return;
            }
            string path = FindFileForEntry(entry);
            if (string.IsNullOrEmpty(path))
            {
                Log.Warn("找不到对应的搬家日记文件，无法标记为已撤销。");
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                List<string> output = new List<string>();
                bool inMoveSection = false;
                bool replaced = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string trimmed = line == null ? string.Empty : line.Trim();
                    if (trimmed.Length > 1 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                    {
                        inMoveSection = string.Equals(
                            trimmed.Substring(1, trimmed.Length - 2).Trim(), SectionMove,
                            StringComparison.OrdinalIgnoreCase);
                        output.Add(line);
                        continue;
                    }
                    if (inMoveSection && trimmed.StartsWith("undone=", StringComparison.OrdinalIgnoreCase))
                    {
                        output.Add("undone=" + Encode("1"));
                        replaced = true;
                        continue;
                    }
                    output.Add(line);
                }

                if (!replaced)
                {
                    int moveIndex = -1;
                    for (int i = 0; i < output.Count; i++)
                    {
                        string trimmed = output[i] == null ? string.Empty : output[i].Trim();
                        if (string.Equals(trimmed, "[" + SectionMove + "]", StringComparison.OrdinalIgnoreCase))
                        {
                            moveIndex = i;
                            break;
                        }
                    }
                    if (moveIndex >= 0)
                    {
                        output.Insert(moveIndex + 1, "undone=" + Encode("1"));
                    }
                    else
                    {
                        output.Add("[" + SectionMove + "]");
                        output.Add("undone=" + Encode("1"));
                    }
                }

                File.WriteAllLines(path, output.ToArray(), new UTF8Encoding(false));
                Log.Info("已把搬家日记标记为已撤销：" + path);
            }
            catch (Exception ex)
            {
                Log.Warn("标记搬家日记为已撤销失败：" + path + "：" + ex.Message);
            }
        }

        /// <summary>日记是否已经标记为撤销（内部使用）</summary>
        internal static bool IsUndone(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return false;
            }
            try
            {
                string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
                bool inMoveSection = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i] == null ? string.Empty : lines[i].Trim();
                    if (trimmed.Length > 1 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                    {
                        inMoveSection = string.Equals(
                            trimmed.Substring(1, trimmed.Length - 2).Trim(), SectionMove,
                            StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inMoveSection || !trimmed.StartsWith("undone=", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string value = Decode(trimmed.Substring("undone=".Length));
                    return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        /// <summary>所有候选日记目录（按优先级）</summary>
        private static List<string> CandidateDirectories()
        {
            List<string> directories = new List<string>();
            if (!string.IsNullOrEmpty(_cachedDirectory))
            {
                directories.Add(_cachedDirectory);
            }
            try
            {
                string primary = AppPaths.JournalDir;
                if (!string.IsNullOrEmpty(primary) && !Contains(directories, primary))
                {
                    directories.Add(primary);
                }
            }
            catch (Exception)
            {
                // 忽略：继续尝试其他目录
            }
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    string path = Path.Combine(localAppData, "YijianNuodong", "journal");
                    if (!Contains(directories, path))
                    {
                        directories.Add(path);
                    }
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            try
            {
                string temp = Path.Combine(Path.GetTempPath(), "YijianNuodong", "journal");
                if (!Contains(directories, temp))
                {
                    directories.Add(temp);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            return directories;
        }

        /// <summary>已存在的日记目录（用于列举）</summary>
        private static List<string> ExistingDirectories()
        {
            List<string> result = new List<string>();
            List<string> candidates = CandidateDirectories();
            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    if (!string.IsNullOrEmpty(candidates[i]) && Directory.Exists(candidates[i]))
                    {
                        result.Add(candidates[i]);
                    }
                }
                catch (Exception)
                {
                    // 忽略
                }
            }
            return result;
        }

        /// <summary>生成日记内容行</summary>
        private static List<string> BuildLines(JournalEntry entry)
        {
            List<string> lines = new List<string>();
            lines.Add(HeaderLine);
            lines.Add("[" + SectionMove + "]");
            lines.Add("version=" + Encode(entry.Version));
            lines.Add("createdutc=" + Encode(FormatDateTimeUtc(entry.CreatedUtc)));
            lines.Add("oldpath=" + Encode(entry.OldPath));
            lines.Add("newpath=" + Encode(entry.NewPath));
            lines.Add("junction=" + Encode(entry.JunctionCreated ? "1" : "0"));
            lines.Add("freed=" + Encode(entry.FreedBytes.ToString()));
            lines.Add("sourcedeleted=" + Encode(entry.SourceDeleted ? "1" : "0"));
            lines.Add("displayname=" + Encode(entry.DisplayName));
            lines.Add("undone=" + Encode("0"));

            lines.Add("[" + SectionShortcut + "]");
            for (int i = 0; i < entry.UpdatedShortcuts.Count; i++)
            {
                lines.Add("path=" + Encode(entry.UpdatedShortcuts[i]));
            }

            lines.Add("[" + SectionReg + "]");
            for (int i = 0; i < entry.RegistryChanges.Count; i++)
            {
                RegistryChange change = entry.RegistryChanges[i];
                if (change == null)
                {
                    continue;
                }
                StringBuilder builder = new StringBuilder();
                builder.Append(Encode(change.Hive));
                builder.Append('|');
                builder.Append(Encode(change.KeyPath));
                builder.Append('|');
                builder.Append(Encode(change.ValueName));
                builder.Append('|');
                builder.Append(Encode(change.ValueKind));
                builder.Append('|');
                builder.Append(Encode(change.OldValue));
                builder.Append('|');
                builder.Append(Encode(change.NewValue));
                lines.Add(builder.ToString());
            }
            return lines;
        }

        /// <summary>解析 [move] 段字段</summary>
        private static void ApplyMoveField(JournalEntry entry, string key, string value)
        {
            switch (key)
            {
                case "version":
                    if (!string.IsNullOrEmpty(value))
                    {
                        entry.Version = value;
                    }
                    break;
                case "createdutc":
                    DateTime created;
                    if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out created))
                    {
                        entry.CreatedUtc = created;
                    }
                    break;
                case "oldpath":
                    entry.OldPath = value;
                    break;
                case "newpath":
                    entry.NewPath = value;
                    break;
                case "junction":
                    entry.JunctionCreated = IsTrue(value);
                    break;
                case "freed":
                    long freed;
                    if (long.TryParse(value, out freed))
                    {
                        entry.FreedBytes = freed;
                    }
                    break;
                case "sourcedeleted":
                    entry.SourceDeleted = IsTrue(value);
                    break;
                case "displayname":
                    entry.DisplayName = value;
                    break;
            }
        }

        /// <summary>构造日记文件完整路径（同名时追加序号）</summary>
        private static string BuildFilePath(string directory, JournalEntry entry)
        {
            DateTime localTime;
            string leafSource;
            if (entry == null)
            {
                localTime = DateTime.Now;
                leafSource = string.Empty;
            }
            else
            {
                localTime = entry.CreatedUtc == DateTime.MinValue
                    ? DateTime.Now
                    : entry.CreatedUtc.ToLocalTime();
                leafSource = string.IsNullOrEmpty(entry.DisplayName)
                    ? entry.OldPath
                    : entry.DisplayName;
            }

            string leaf = PathUtil.SanitizeFolderName(PathUtil.GetLeafName(leafSource, leafSource));
            if (string.IsNullOrEmpty(leaf))
            {
                leaf = "move";
            }
            string baseName = localTime.ToString("yyyyMMdd_HHmmss") + "_" + leaf;
            string path = Path.Combine(directory, baseName + ".journal");
            int suffix = 2;
            while (File.Exists(path) && suffix < 100)
            {
                path = Path.Combine(directory, baseName + "_" + suffix.ToString() + ".journal");
                suffix++;
            }
            return path;
        }

        /// <summary>按修改时间倒序比较日记文件</summary>
        private static int CompareByTimeDescending(string left, string right)
        {
            DateTime leftTime = SafeLastWriteTimeUtc(left);
            DateTime rightTime = SafeLastWriteTimeUtc(right);
            int compare = rightTime.CompareTo(leftTime);
            if (compare != 0)
            {
                return compare;
            }
            return string.Compare(right, left, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>取文件最后写入时间（失败返回最小值）</summary>
        private static DateTime SafeLastWriteTimeUtc(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>为日记对象定位对应的文件（优先内部映射，其次按内容匹配）</summary>
        private static string FindFileForEntry(JournalEntry entry)
        {
            string mapped;
            if (EntryFiles.TryGetValue(entry, out mapped) && !string.IsNullOrEmpty(mapped) && File.Exists(mapped))
            {
                return mapped;
            }

            List<string> files = ListFiles();
            for (int i = 0; i < files.Count; i++)
            {
                JournalEntry candidate = Read(files[i]);
                if (candidate == null)
                {
                    continue;
                }
                if (PathUtil.SamePath(candidate.OldPath, entry.OldPath)
                    && PathUtil.SamePath(candidate.NewPath, entry.NewPath))
                {
                    return files[i];
                }
            }
            return null;
        }

        /// <summary>时间统一用 UTC 往返格式保存</summary>
        private static string FormatDateTimeUtc(DateTime value)
        {
            DateTime utc = value;
            if (utc.Kind != DateTimeKind.Utc)
            {
                if (utc.Kind == DateTimeKind.Local)
                {
                    utc = utc.ToUniversalTime();
                }
                else
                {
                    utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                }
            }
            return utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>判断 "1"/"true" 形式的布尔值</summary>
        private static bool IsTrue(string value)
        {
            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>字符串列表是否已包含指定路径（忽略大小写）</summary>
        private static bool Contains(List<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>UTF8 字符串转 Base64（空值返回空字符串）</summary>
        private static string Encode(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>Base64 转 UTF8 字符串（非法内容返回空字符串）</summary>
        private static string Decode(string base64)
        {
            if (string.IsNullOrEmpty(base64))
            {
                return string.Empty;
            }
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
