// ============================================================================
//  一键挪动 (OneKeyMove) - 注册表路径改写
//  覆盖范围：
//    a) 卸载信息：HKLM/HKCU 的 Uninstall 键（含 32 位视图 WOW6432Node）里所有
//       字符串值中包含旧安装路径的项（InstallLocation 直接写成新路径）
//    b) App Paths：默认值与 Path 值
//    c) 服务 ImagePath：指向旧安装目录的服务
//  安全约定：
//    - 环境变量（%ProgramFiles% 等）只用于“匹配”，写回时保持原始 %VAR% 形式；
//    - 每条变更都记录旧值，失败可 Revert；
//    - 权限不足时返回 false 并给出中文提示，已应用的变更保留由调用方决定回滚。
//  目标框架：.NET Framework 4.8 / C# 5
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Win32;

namespace YijianNuodong.Core
{
    /// <summary>注册表路径改写（卸载信息 / App Paths / 服务 ImagePath）</summary>
    public static class RegistryRewriter
    {
        private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        private const string AppPathsKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        private const string ServicesKeyPath = @"SYSTEM\CurrentControlSet\Services";
        private const string Wow6432NodePrefix = @"WOW6432Node\";
        private const string HiveLocalMachine = "HKLM";
        private const string HiveCurrentUser = "HKCU";

        /// <summary>找出需要修改的注册表值（不写入）</summary>
        public static List<RegistryChange> FindChanges(string oldPath, string newPath)
        {
            List<RegistryChange> changes = new List<RegistryChange>();
            string oldDirectory = PathUtil.Normalize(oldPath);
            string newDirectory = PathUtil.Normalize(newPath);
            if (string.IsNullOrEmpty(oldDirectory) || string.IsNullOrEmpty(newDirectory))
            {
                return changes;
            }

            // a) 卸载信息：HKLM 64 位视图
            ScanUninstall(changes, HiveLocalMachine, UninstallKeyPath, oldDirectory, newDirectory);
            // a) 卸载信息：HKLM 32 位视图（WOW6432Node 用字面路径表示，写入时用同一把钥匙）
            if (Environment.Is64BitOperatingSystem)
            {
                ScanUninstall(changes, HiveLocalMachine, Wow6432NodePrefix + UninstallKeyPath, oldDirectory, newDirectory);
            }
            // a) 卸载信息：HKCU（HKCU\Software 不区分 32/64 位视图）
            ScanUninstall(changes, HiveCurrentUser, UninstallKeyPath, oldDirectory, newDirectory);

            // b) App Paths
            ScanAppPaths(changes, HiveLocalMachine, AppPathsKeyPath, oldDirectory, newDirectory);
            if (Environment.Is64BitOperatingSystem)
            {
                ScanAppPaths(changes, HiveLocalMachine, Wow6432NodePrefix + AppPathsKeyPath, oldDirectory, newDirectory);
            }
            ScanAppPaths(changes, HiveCurrentUser, AppPathsKeyPath, oldDirectory, newDirectory);

            // c) 服务 ImagePath
            ScanServiceImagePaths(changes, oldDirectory, newDirectory);

            return Deduplicate(changes);
        }

        /// <summary>应用变更；全部成功返回 true</summary>
        public static bool Apply(List<RegistryChange> changes, out string error)
        {
            error = null;
            if (changes == null || changes.Count == 0)
            {
                return true;
            }
            List<string> failures = new List<string>();
            for (int i = 0; i < changes.Count; i++)
            {
                RegistryChange change = changes[i];
                if (change == null)
                {
                    continue;
                }
                string writeError;
                if (!WriteValue(change, change.NewValue, out writeError))
                {
                    failures.Add(writeError);
                }
            }
            if (failures.Count > 0)
            {
                error = JoinMessages(failures);
                Log.Warn("应用注册表变更失败：" + error);
                return false;
            }
            Log.Info("已应用 " + changes.Count + " 条注册表变更。");
            return true;
        }

        /// <summary>还原变更（写回旧值）；全部成功返回 true</summary>
        public static bool Revert(List<RegistryChange> changes, out string error)
        {
            error = null;
            if (changes == null || changes.Count == 0)
            {
                return true;
            }
            List<string> failures = new List<string>();
            for (int i = 0; i < changes.Count; i++)
            {
                RegistryChange change = changes[i];
                if (change == null)
                {
                    continue;
                }
                string writeError;
                if (!WriteValue(change, change.OldValue, out writeError))
                {
                    failures.Add(writeError);
                }
            }
            if (failures.Count > 0)
            {
                error = JoinMessages(failures);
                Log.Warn("还原注册表变更失败：" + error);
                return false;
            }
            Log.Info("已还原 " + changes.Count + " 条注册表变更。");
            return true;
        }

        /// <summary>一步完成：查找 + 应用，返回成功应用的条数</summary>
        public static int UpdatePaths(string oldPath, string newPath, out List<RegistryChange> applied, out string error)
        {
            applied = new List<RegistryChange>();
            error = null;
            string oldDirectory = PathUtil.Normalize(oldPath);
            string newDirectory = PathUtil.Normalize(newPath);
            if (string.IsNullOrEmpty(oldDirectory) || string.IsNullOrEmpty(newDirectory))
            {
                error = "旧路径或新路径为空，无法更新注册表。";
                return 0;
            }

            List<RegistryChange> changes;
            try
            {
                changes = FindChanges(oldDirectory, newDirectory);
            }
            catch (Exception ex)
            {
                error = "查找注册表变更失败：" + ex.Message;
                return 0;
            }
            if (changes.Count == 0)
            {
                return 0;
            }

            // 无论成功与否都把候选变更交给调用方：失败时可据此安全回滚（写回旧值不会造成损失）
            applied.AddRange(changes);

            string applyError;
            if (!Apply(changes, out applyError))
            {
                error = applyError;
                return 0;
            }
            return changes.Count;
        }

        /// <summary>扫描某个卸载信息位置下的所有字符串值</summary>
        private static void ScanUninstall(List<RegistryChange> changes, string hive, string uninstallKeyPath,
            string oldDirectory, string newDirectory)
        {
            try
            {
                using (RegistryKey root = OpenBaseKey(hive))
                {
                    if (root == null)
                    {
                        return;
                    }
                    using (RegistryKey uninstall = root.OpenSubKey(uninstallKeyPath, false))
                    {
                        if (uninstall == null)
                        {
                            return;
                        }
                        string[] subKeyNames = uninstall.GetSubKeyNames();
                        for (int i = 0; i < subKeyNames.Length; i++)
                        {
                            string appKeyPath = uninstallKeyPath + "\\" + subKeyNames[i];
                            try
                            {
                                using (RegistryKey appKey = uninstall.OpenSubKey(subKeyNames[i], false))
                                {
                                    if (appKey == null)
                                    {
                                        continue;
                                    }
                                    ScanStringValues(changes, appKey, hive, appKeyPath, oldDirectory, newDirectory);
                                }
                            }
                            catch (Exception)
                            {
                                // 单个软件项无权限时跳过，不影响其他项
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("扫描卸载信息注册表失败（" + hive + "\\" + uninstallKeyPath + "）：" + ex.Message);
            }
        }

        /// <summary>扫描 App Paths 下的默认值与 Path 值</summary>
        private static void ScanAppPaths(List<RegistryChange> changes, string hive, string appPathsKeyPath,
            string oldDirectory, string newDirectory)
        {
            try
            {
                using (RegistryKey root = OpenBaseKey(hive))
                {
                    if (root == null)
                    {
                        return;
                    }
                    using (RegistryKey appPaths = root.OpenSubKey(appPathsKeyPath, false))
                    {
                        if (appPaths == null)
                        {
                            return;
                        }
                        string[] subKeyNames = appPaths.GetSubKeyNames();
                        for (int i = 0; i < subKeyNames.Length; i++)
                        {
                            string exeKeyPath = appPathsKeyPath + "\\" + subKeyNames[i];
                            try
                            {
                                using (RegistryKey exeKey = appPaths.OpenSubKey(subKeyNames[i], false))
                                {
                                    if (exeKey == null)
                                    {
                                        continue;
                                    }
                                    // 默认值（exe 完整路径）
                                    ExamineValue(changes, exeKey, hive, exeKeyPath, string.Empty, oldDirectory, newDirectory);
                                    // Path 值（工作目录）
                                    ExamineValue(changes, exeKey, hive, exeKeyPath, "Path", oldDirectory, newDirectory);
                                }
                            }
                            catch (Exception)
                            {
                                // 跳过无权限项
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("扫描 App Paths 注册表失败（" + hive + "\\" + appPathsKeyPath + "）：" + ex.Message);
            }
        }

        /// <summary>扫描服务 ImagePath</summary>
        private static void ScanServiceImagePaths(List<RegistryChange> changes, string oldDirectory, string newDirectory)
        {
            try
            {
                using (RegistryKey root = OpenBaseKey(HiveLocalMachine))
                {
                    if (root == null)
                    {
                        return;
                    }
                    using (RegistryKey services = root.OpenSubKey(ServicesKeyPath, false))
                    {
                        if (services == null)
                        {
                            return;
                        }
                        string[] serviceNames = services.GetSubKeyNames();
                        for (int i = 0; i < serviceNames.Length; i++)
                        {
                            try
                            {
                                using (RegistryKey serviceKey = services.OpenSubKey(serviceNames[i], false))
                                {
                                    if (serviceKey == null)
                                    {
                                        continue;
                                    }
                                    RegistryValueKind kind;
                                    string raw;
                                    if (!ReadStringValue(serviceKey, "ImagePath", out kind, out raw))
                                    {
                                        continue;
                                    }
                                    string replacement;
                                    if (!TryBuildImagePathReplacement(raw, oldDirectory, newDirectory, out replacement))
                                    {
                                        continue;
                                    }
                                    if (string.Equals(raw, replacement, StringComparison.Ordinal))
                                    {
                                        continue;
                                    }
                                    RegistryChange change = new RegistryChange();
                                    change.Hive = HiveLocalMachine;
                                    change.KeyPath = ServicesKeyPath + "\\" + serviceNames[i];
                                    change.ValueName = "ImagePath";
                                    change.OldValue = raw;
                                    change.NewValue = replacement;
                                    change.ValueKind = (replacement.IndexOf('%') >= 0 || kind == RegistryValueKind.ExpandString)
                                        ? "ExpandString"
                                        : "String";
                                    changes.Add(change);
                                }
                            }
                            catch (Exception)
                            {
                                // 跳过无权限项
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("扫描服务 ImagePath 失败：" + ex.Message);
            }
        }

        /// <summary>扫描一个键下所有字符串/可扩展字符串值</summary>
        private static void ScanStringValues(List<RegistryChange> changes, RegistryKey key, string hive,
            string keyPath, string oldDirectory, string newDirectory)
        {
            string[] valueNames;
            try
            {
                valueNames = key.GetValueNames();
            }
            catch (Exception)
            {
                return;
            }
            for (int i = 0; i < valueNames.Length; i++)
            {
                ExamineValue(changes, key, hive, keyPath, valueNames[i], oldDirectory, newDirectory);
            }
        }

        /// <summary>检查单个值是否需要改写</summary>
        private static void ExamineValue(List<RegistryChange> changes, RegistryKey key, string hive,
            string keyPath, string valueName, string oldDirectory, string newDirectory)
        {
            RegistryValueKind kind;
            string raw;
            string name = valueName == null ? string.Empty : valueName;
            if (!ReadStringValue(key, name, out kind, out raw))
            {
                return;
            }
            string replacement;
            if (!TryBuildReplacement(raw, name, oldDirectory, newDirectory, out replacement))
            {
                return;
            }
            if (string.Equals(raw, replacement, StringComparison.Ordinal))
            {
                return;
            }

            RegistryChange change = new RegistryChange();
            change.Hive = hive;
            change.KeyPath = keyPath;
            change.ValueName = name;
            change.OldValue = raw;
            change.NewValue = replacement;
            change.ValueKind = kind == RegistryValueKind.ExpandString ? "ExpandString" : "String";
            changes.Add(change);
        }

        /// <summary>读取字符串类型的注册表值</summary>
        private static bool ReadStringValue(RegistryKey key, string valueName, out RegistryValueKind kind, out string raw)
        {
            kind = RegistryValueKind.String;
            raw = null;
            try
            {
                object value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null)
                {
                    return false;
                }
                string text = value as string;
                if (text == null)
                {
                    return false;
                }
                kind = key.GetValueKind(valueName);
                if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
                {
                    return false;
                }
                raw = text;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 计算某个值的新内容：
        /// 1) InstallLocation 且内容就是旧目录 → 直接写新目录；
        /// 2) 原文包含旧目录 → 直接替换（保留其余文字）；
        /// 3) 原文用环境变量拼出旧目录 → 按展开偏移映射回原文替换（保留 %VAR% 形式）。
        /// </summary>
        private static bool TryBuildReplacement(string rawValue, string valueName,
            string oldDirectory, string newDirectory, out string replacement)
        {
            replacement = null;
            if (string.IsNullOrEmpty(rawValue))
            {
                return false;
            }

            bool hasEnvironmentVariable = rawValue.IndexOf('%') >= 0;
            string expanded = hasEnvironmentVariable ? Environment.ExpandEnvironmentVariables(rawValue) : rawValue;

            if (string.Equals(valueName, "InstallLocation", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = expanded.Trim().TrimEnd('\\');
                if (string.Equals(trimmed, oldDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    replacement = newDirectory;
                    return true;
                }
            }

            if (rawValue.IndexOf(oldDirectory, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                replacement = ReplaceFirst(rawValue, oldDirectory, newDirectory);
                return true;
            }

            if (!hasEnvironmentVariable)
            {
                return false;
            }
            return TryReplaceWithEnvironmentVariables(rawValue, expanded, oldDirectory, newDirectory, out replacement);
        }

        /// <summary>计算服务 ImagePath 的新内容（保留引号与参数后缀）</summary>
        private static bool TryBuildImagePathReplacement(string rawValue, string oldDirectory, string newDirectory, out string replacement)
        {
            replacement = null;
            if (string.IsNullOrEmpty(rawValue))
            {
                return false;
            }

            string executable = ServiceHelper.ExtractExecutablePath(rawValue);
            if (string.IsNullOrEmpty(executable))
            {
                return false;
            }
            string expandedExecutable = Environment.ExpandEnvironmentVariables(executable);
            string normalizedExecutable = PathUtil.Normalize(expandedExecutable);
            if (!PathUtil.IsSubPathOf(normalizedExecutable, oldDirectory))
            {
                return false;
            }

            string mappedExecutable = PathUtil.Normalize(newDirectory + normalizedExecutable.Substring(oldDirectory.Length));
            string executableToken = mappedExecutable.IndexOf(' ') >= 0 ? "\"" + mappedExecutable + "\"" : mappedExecutable;

            int index = rawValue.IndexOf(executable, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                replacement = rawValue.Substring(0, index) + executableToken + rawValue.Substring(index + executable.Length);
                return true;
            }

            // 原文中可执行文件路径由环境变量拼成，退化为按展开偏移映射
            string expandedRaw = Environment.ExpandEnvironmentVariables(rawValue);
            return TryReplaceWithEnvironmentVariables(rawValue, expandedRaw, oldDirectory, newDirectory, out replacement);
        }

        /// <summary>替换第一次出现的路径（大小写不敏感）</summary>
        private static string ReplaceFirst(string text, string oldValue, string newValue)
        {
            int index = text.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return text;
            }
            return text.Substring(0, index) + newValue + text.Substring(index + oldValue.Length);
        }

        /// <summary>
        /// 在“展开后包含旧目录、但原文使用 %VAR%”的值中定位旧目录在原文里的位置并替换，
        /// 从而保持 %VAR% 形式与其余文字不变。
        /// </summary>
        private static bool TryReplaceWithEnvironmentVariables(string raw, string expanded,
            string oldDirectory, string newDirectory, out string replacement)
        {
            replacement = null;
            if (string.IsNullOrEmpty(expanded))
            {
                return false;
            }
            int matchIndex = expanded.IndexOf(oldDirectory, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return false;
            }
            int matchEnd = matchIndex + oldDirectory.Length;

            List<ExpandToken> tokens = TokenizeExpansion(raw);
            int rawStart = MapExpandedOffset(tokens, matchIndex, false);
            int rawEnd = MapExpandedOffset(tokens, matchEnd, true);
            if (rawStart < 0 || rawEnd < 0 || rawStart > rawEnd || rawEnd > raw.Length)
            {
                return false;
            }
            replacement = raw.Substring(0, rawStart) + newDirectory + raw.Substring(rawEnd);
            return true;
        }

        /// <summary>展开文本片段/环境变量片段（用于把“展开后偏移”映射回“原文偏移”）</summary>
        private sealed class ExpandToken
        {
            public int RawStart;
            public int RawEnd;
            public string Text;
            public bool IsEnvironmentVariable;
        }

        /// <summary>把原文按“普通文字 / %VAR%”切成片段</summary>
        private static List<ExpandToken> TokenizeExpansion(string raw)
        {
            List<ExpandToken> tokens = new List<ExpandToken>();
            if (string.IsNullOrEmpty(raw))
            {
                return tokens;
            }
            int index = 0;
            int literalStart = 0;
            StringBuilder literal = new StringBuilder();
            while (index < raw.Length)
            {
                char current = raw[index];
                if (current == '%')
                {
                    int close = raw.IndexOf('%', index + 1);
                    if (close > index + 1)
                    {
                        if (literal.Length > 0)
                        {
                            ExpandToken literalToken = new ExpandToken();
                            literalToken.RawStart = literalStart;
                            literalToken.RawEnd = index;
                            literalToken.Text = literal.ToString();
                            literalToken.IsEnvironmentVariable = false;
                            tokens.Add(literalToken);
                            literal.Length = 0;
                        }
                        string variableName = raw.Substring(index + 1, close - index - 1);
                        string variableValue = null;
                        try
                        {
                            variableValue = Environment.GetEnvironmentVariable(variableName);
                        }
                        catch (Exception)
                        {
                            variableValue = null;
                        }
                        if (variableValue == null)
                        {
                            // 变量不存在时按原文保留
                            variableValue = raw.Substring(index, close - index + 1);
                        }
                        ExpandToken variableToken = new ExpandToken();
                        variableToken.RawStart = index;
                        variableToken.RawEnd = close + 1;
                        variableToken.Text = variableValue;
                        variableToken.IsEnvironmentVariable = true;
                        tokens.Add(variableToken);
                        index = close + 1;
                        literalStart = index;
                        continue;
                    }
                }
                if (literal.Length == 0)
                {
                    literalStart = index;
                }
                literal.Append(current);
                index++;
            }
            if (literal.Length > 0)
            {
                ExpandToken lastToken = new ExpandToken();
                lastToken.RawStart = literalStart;
                lastToken.RawEnd = raw.Length;
                lastToken.Text = literal.ToString();
                lastToken.IsEnvironmentVariable = false;
                tokens.Add(lastToken);
            }
            return tokens;
        }

        /// <summary>把展开后的字符偏移映射回原文偏移</summary>
        private static int MapExpandedOffset(List<ExpandToken> tokens, int offset, bool isEnd)
        {
            int expandedPosition = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                ExpandToken token = tokens[i];
                int length = token.Text == null ? 0 : token.Text.Length;
                int start = expandedPosition;
                int end = start + length;
                if (!isEnd)
                {
                    if (offset >= start && offset < end)
                    {
                        if (!token.IsEnvironmentVariable)
                        {
                            return token.RawStart + (offset - start);
                        }
                        return token.RawStart;
                    }
                }
                else
                {
                    if (offset > start && offset <= end)
                    {
                        if (!token.IsEnvironmentVariable)
                        {
                            return token.RawStart + (offset - start);
                        }
                        return token.RawEnd;
                    }
                    if (offset == start)
                    {
                        return token.RawStart;
                    }
                }
                expandedPosition = end;
            }
            if (offset == expandedPosition)
            {
                return -1;
            }
            return -1;
        }

        /// <summary>把一条变更写入注册表</summary>
        private static bool WriteValue(RegistryChange change, string value, out string error)
        {
            error = null;
            if (change == null)
            {
                return true;
            }
            string valueName = change.ValueName == null ? string.Empty : change.ValueName;
            string displayPath = change.Hive + "\\" + change.KeyPath + "\\" + (valueName.Length == 0 ? "(默认)" : valueName);
            try
            {
                using (RegistryKey root = OpenBaseKey(change.Hive))
                {
                    if (root == null)
                    {
                        error = "无法打开注册表根键：" + change.Hive;
                        return false;
                    }
                    using (RegistryKey key = root.OpenSubKey(change.KeyPath, true))
                    {
                        if (key == null)
                        {
                            error = "注册表项不存在或无权写入：" + displayPath;
                            return false;
                        }
                        key.SetValue(valueName, value == null ? string.Empty : value, TextToKind(change.ValueKind));
                        return true;
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                error = "写入注册表需要管理员权限：" + displayPath + "（" + ex.Message + "）";
                return false;
            }
            catch (System.Security.SecurityException ex)
            {
                error = "写入注册表需要管理员权限：" + displayPath + "（" + ex.Message + "）";
                return false;
            }
            catch (Exception ex)
            {
                error = "写入注册表失败：" + displayPath + "（" + ex.Message + "）";
                return false;
            }
        }

        /// <summary>按 HKLM/HKCU 打开注册表根键（64 位系统固定用 64 位视图，WOW6432Node 用字面路径表达）</summary>
        private static RegistryKey OpenBaseKey(string hive)
        {
            RegistryHive registryHive;
            if (string.Equals(hive, HiveLocalMachine, StringComparison.OrdinalIgnoreCase))
            {
                registryHive = RegistryHive.LocalMachine;
            }
            else if (string.Equals(hive, HiveCurrentUser, StringComparison.OrdinalIgnoreCase))
            {
                registryHive = RegistryHive.CurrentUser;
            }
            else
            {
                return null;
            }
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
            return RegistryKey.OpenBaseKey(registryHive, view);
        }

        /// <summary>值类型文本转枚举</summary>
        private static RegistryValueKind TextToKind(string valueKind)
        {
            if (string.Equals(valueKind, "ExpandString", StringComparison.OrdinalIgnoreCase))
            {
                return RegistryValueKind.ExpandString;
            }
            return RegistryValueKind.String;
        }

        /// <summary>同一键值只保留最后一条（避免重复写入）</summary>
        private static List<RegistryChange> Deduplicate(List<RegistryChange> changes)
        {
            List<RegistryChange> unique = new List<RegistryChange>();
            for (int i = changes.Count - 1; i >= 0; i--)
            {
                RegistryChange change = changes[i];
                if (change == null)
                {
                    continue;
                }
                bool duplicated = false;
                for (int j = 0; j < unique.Count; j++)
                {
                    if (string.Equals(unique[j].Hive, change.Hive, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(unique[j].KeyPath, change.KeyPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(unique[j].ValueName ?? string.Empty, change.ValueName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicated = true;
                        break;
                    }
                }
                if (!duplicated)
                {
                    unique.Add(change);
                }
            }
            unique.Reverse();
            return unique;
        }

        /// <summary>把多条错误信息拼成一段中文提示</summary>
        private static string JoinMessages(List<string> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("；");
                }
                builder.Append(messages[i]);
            }
            return builder.ToString();
        }
    }
}
