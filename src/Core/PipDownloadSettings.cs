using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
namespace YijianNuodong.Core
{
    public static class PipDownloadSettings
    {
        public static string UserConfigPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"pip\pip.ini"); } }
        public static string ReadStatus()
        {
            string text = "未设置用户级 download.dest";
            try
            {
                string path = UserConfigPath;
                if (File.Exists(path))
                {
                    bool download = false; string dest = null;
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string trim = line.Trim();
                        if (trim.StartsWith("[")) download = Regex.IsMatch(trim, @"^\[download\]\s*(?:[#;].*)?$", RegexOptions.IgnoreCase);
                        else if (download && Regex.IsMatch(trim, @"^dest\s*[=:]", RegexOptions.IgnoreCase)) dest = trim;
                    }
                    text = path + Environment.NewLine + (dest ?? "未设置用户级 download.dest");
                }
                if (text.Length > 2500) text = text.Substring(0, 2500) + "…";
            }
            catch (Exception ex) { text = "配置读取失败：" + ex.Message; }
            return text + Environment.NewLine + "仅修改用户级 [download] dest；支持 PowerShell/CMD，无需包装器。命令行 -d 可覆盖；环境变量、PIP_CONFIG_FILE、虚拟环境配置可能有更高优先级。";
        }
        internal static string Patch(string text, string destination)
        {
            if (destination.IndexOfAny(new char[] { '\r', '\n', '%', ';', '#' }) >= 0) throw new IOException("下载路径不能含换行、%、; 或 #。");
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            int start = -1, end = lines.Count, key = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string trim = lines[i].Trim();
                if (Regex.IsMatch(trim, @"^\[download\]\s*(?:[#;].*)?$", RegexOptions.IgnoreCase))
                { if (start >= 0) throw new IOException("存在重复 [download] 节，拒绝自动修改，请先整理配置。"); start = i; }
                else if (start >= 0 && end == lines.Count && trim.StartsWith("[", StringComparison.Ordinal)) end = i;
            }
            if (start >= 0)
            {
                // Reject ambiguous multiline options rather than silently destroying user values.
                for (int i = start + 1; i < end; i++)
                {
                    string trim = lines[i].Trim();
                    if (trim.Length == 0 || trim.StartsWith("#") || trim.StartsWith(";")) continue;
                    if (char.IsWhiteSpace(lines[i][0])) throw new IOException("[download] 中存在缩进/多行值，请手工整理后重试。");
                    if (Regex.IsMatch(trim, @"^dest\s*[=:]", RegexOptions.IgnoreCase)) { if (key >= 0) throw new IOException("存在重复 dest，拒绝自动修改。"); key = i; }
                }
                if (key >= 0) lines[key] = "dest = " + destination;
                else lines.Insert(end, "dest = " + destination);
            }
            else { if (lines.Count > 0 && lines[lines.Count - 1].Length != 0) lines.Add(""); lines.Add("[download]"); lines.Add("dest = " + destination); }
            return string.Join(newline, lines.ToArray()).TrimEnd('\r', '\n') + newline;
        }
        public static PipSettingResult Apply(string destination)
        { return ApplyAt(UserConfigPath, destination, true); }
        internal static PipSettingResult ApplyAt(string config, string destination, bool requireOtherDrive)
        {
            string canonical; if (!CleanupEngine.TryCanonical(destination, out canonical)) throw new IOException("请选择本机绝对下载目录，不能是根、UNC 或设备路径。");
            DataMoveEngine.CheckParents(canonical); DataMoveEngine.CheckParents(config);
            if (Junction.IsReparsePoint(config) || Junction.IsReparsePoint(canonical) || File.Exists(canonical)) throw new IOException("配置或下载目录不能是链接/普通文件。");
            if (requireOtherDrive && PathUtil.SamePath(Path.GetPathRoot(canonical), Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)))) throw new IOException("请选择 D/E 等非系统盘。");
            Encoding encoding = new UTF8Encoding(false, true); bool existed = File.Exists(config); byte[] original = existed ? File.ReadAllBytes(config) : new byte[0]; string text = "";
            if (existed)
            {
                using (MemoryStream buffer = new MemoryStream(original))
                using (StreamReader reader = new StreamReader(buffer, encoding, true)) { text = reader.ReadToEnd(); encoding = reader.CurrentEncoding; }
            }
            string patched = Patch(text, canonical);
            string parent = Path.GetDirectoryName(config); Directory.CreateDirectory(parent);
            string backup = Path.Combine(parent, "pip.ini.onekeymove-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + (existed ? ".orig" : ".absent.orig"));
            // Backup is created before any config writes. Preserve original bytes exactly.
            using (FileStream b = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) b.Write(original, 0, original.Length);
            byte[] written;
            using (MemoryStream buffer = new MemoryStream())
            {
                using (StreamWriter writer = new StreamWriter(buffer, encoding, 1024, true)) writer.Write(patched);
                written = buffer.ToArray();
            }
            Directory.CreateDirectory(canonical);
            string temp = config + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, written);
                if (existed)
                {
                    if (!Same(File.ReadAllBytes(config), original)) throw new IOException("配置在保存期间发生变化，未覆盖；备份：" + backup);
                    File.Replace(temp, config, null);
                }
                else File.Move(temp, config);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return new PipSettingResult { ConfigPath = config, BackupPath = backup, Destination = canonical, Existed = existed, Original = original, Written = written };
        }
        public static void Undo(PipSettingResult result)
        {
            if (result == null || result.Written == null) throw new IOException("没有本次设置的撤销凭据。");
            DataMoveEngine.CheckParents(result.ConfigPath);
            if (Junction.IsReparsePoint(result.ConfigPath) || !File.Exists(result.ConfigPath) || !Same(File.ReadAllBytes(result.ConfigPath), result.Written)) throw new IOException("配置后来被修改/替换，拒绝覆盖；请手工对比备份。");
            if (result.Existed) { string temp = result.ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.WriteAllBytes(temp, result.Original); File.Replace(temp, result.ConfigPath, null); }
            else File.Delete(result.ConfigPath);
        }
        private static bool Same(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    }
}
