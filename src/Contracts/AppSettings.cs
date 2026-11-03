// ============================================================================
//  一键挪动 (OneKeyMove) - 用户设置持久化（Lead 维护）
//  文件: %ProgramData%\YijianNuodong\settings.txt   (key=value 逐行)
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YijianNuodong.Core
{
    public static class AppSettings
    {
        private static readonly object SyncRoot = new object();
        private static bool _loaded;

        private static string _lastTargetDirectory = string.Empty;
        private static List<string> _recentTargets = new List<string>();
        private static bool _createJunctionByDefault = true;
        private static bool _createRestorePointByDefault = true;
        private static bool _updateShortcutsByDefault = true;
        private static bool _updateRegistryByDefault = true;
        private static int _windowWidth = 1100;
        private static int _windowHeight = 760;

        public static string LastTargetDirectory
        {
            get { lock (SyncRoot) { EnsureLoaded(); return _lastTargetDirectory; } }
            set
            {
                lock (SyncRoot)
                {
                    EnsureLoaded();
                    _lastTargetDirectory = value == null ? string.Empty : value;
                    if (!string.IsNullOrEmpty(_lastTargetDirectory))
                    {
                        _recentTargets.RemoveAll(delegate(string p)
                        {
                            return string.Equals(p, _lastTargetDirectory, StringComparison.OrdinalIgnoreCase);
                        });
                        _recentTargets.Insert(0, _lastTargetDirectory);
                        while (_recentTargets.Count > 8) _recentTargets.RemoveAt(_recentTargets.Count - 1);
                    }
                }
            }
        }

        public static List<string> RecentTargets
        {
            get { lock (SyncRoot) { EnsureLoaded(); return new List<string>(_recentTargets); } }
        }

        public static bool CreateJunctionByDefault
        {
            get { lock (SyncRoot) { EnsureLoaded(); return _createJunctionByDefault; } }
            set { lock (SyncRoot) { EnsureLoaded(); _createJunctionByDefault = value; } }
        }

        public static bool CreateRestorePointByDefault
        {
            get { lock (SyncRoot) { EnsureLoaded(); return _createRestorePointByDefault; } }
            set { lock (SyncRoot) { EnsureLoaded(); _createRestorePointByDefault = value; } }
        }

        public static bool UpdateShortcutsByDefault
        {
            get { lock (SyncRoot) { EnsureLoaded(); return _updateShortcutsByDefault; } }
            set { lock (SyncRoot) { EnsureLoaded(); _updateShortcutsByDefault = value; } }
        }

        public static bool UpdateRegistryByDefault
        {
            get { lock (SyncRoot) { EnsureLoaded(); return _updateRegistryByDefault; } }
            set { lock (SyncRoot) { EnsureLoaded(); _updateRegistryByDefault = value; } }
        }

        public static int WindowWidth
        {
            get { lock (SyncRoot) { EnsureLoaded(); return _windowWidth; } }
            set { lock (SyncRoot) { EnsureLoaded(); _windowWidth = value; } }
        }

        public static int WindowHeight
        {
            get { lock (SyncRoot) { EnsureLoaded(); return _windowHeight; } }
            set { lock (SyncRoot) { EnsureLoaded(); _windowHeight = value; } }
        }

        public static void Load()
        {
            lock (SyncRoot)
            {
                _loaded = true;
                try
                {
                    if (!File.Exists(AppPaths.SettingsFile)) return;
                    string[] lines = File.ReadAllLines(AppPaths.SettingsFile, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (line == null) continue;
                        line = line.Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                        int idx = line.IndexOf('=');
                        if (idx <= 0) continue;
                        string key = line.Substring(0, idx).Trim();
                        string val = line.Substring(idx + 1).Trim();
                        ApplyValue(key, val);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("读取设置失败: " + ex.Message);
                }
            }
        }

        private static void ApplyValue(string key, string val)
        {
            try
            {
                switch (key.ToLowerInvariant())
                {
                    case "lasttargetdirectory":
                        _lastTargetDirectory = val;
                        break;
                    case "recenttargets":
                        _recentTargets = new List<string>();
                        if (val.Length > 0)
                        {
                            string[] parts = val.Split(new string[] { "|" }, StringSplitOptions.RemoveEmptyEntries);
                            for (int i = 0; i < parts.Length; i++)
                            {
                                if (!string.IsNullOrEmpty(parts[i])) _recentTargets.Add(parts[i]);
                            }
                        }
                        break;
                    case "createjunction": _createJunctionByDefault = val == "1"; break;
                    case "createrestorepoint": _createRestorePointByDefault = val == "1"; break;
                    case "updateshortcuts": _updateShortcutsByDefault = val == "1"; break;
                    case "updateregistry": _updateRegistryByDefault = val == "1"; break;
                    case "windowwidth": _windowWidth = ParseInt(val, 1100, 800, 4000); break;
                    case "windowheight": _windowHeight = ParseInt(val, 760, 600, 3000); break;
                }
            }
            catch { }
        }

        private static int ParseInt(string text, int fallback, int min, int max)
        {
            int v;
            if (!int.TryParse(text, out v)) return fallback;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        public static void Save()
        {
            lock (SyncRoot)
            {
                try
                {
                    AppPaths.EnsureCreated();
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("# 一键挪动 设置文件");
                    sb.AppendLine("LastTargetDirectory=" + (_lastTargetDirectory ?? string.Empty));
                    sb.AppendLine("RecentTargets=" + string.Join("|", _recentTargets.ToArray()));
                    sb.AppendLine("CreateJunction=" + (_createJunctionByDefault ? "1" : "0"));
                    sb.AppendLine("CreateRestorePoint=" + (_createRestorePointByDefault ? "1" : "0"));
                    sb.AppendLine("UpdateShortcuts=" + (_updateShortcutsByDefault ? "1" : "0"));
                    sb.AppendLine("UpdateRegistry=" + (_updateRegistryByDefault ? "1" : "0"));
                    sb.AppendLine("WindowWidth=" + _windowWidth.ToString());
                    sb.AppendLine("WindowHeight=" + _windowHeight.ToString());
                    File.WriteAllText(AppPaths.SettingsFile, sb.ToString(), Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Log.Warn("保存设置失败: " + ex.Message);
                }
            }
        }

        private static void EnsureLoaded()
        {
            if (!_loaded)
            {
                _loaded = true; // 防止递归
                try
                {
                    if (File.Exists(AppPaths.SettingsFile))
                    {
                        string[] lines = File.ReadAllLines(AppPaths.SettingsFile, Encoding.UTF8);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            string line = (lines[i] ?? string.Empty).Trim();
                            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                            int idx = line.IndexOf('=');
                            if (idx <= 0) continue;
                            ApplyValue(line.Substring(0, idx).Trim(), line.Substring(idx + 1).Trim());
                        }
                    }
                }
                catch
                {
                }
            }
        }
    }
}
