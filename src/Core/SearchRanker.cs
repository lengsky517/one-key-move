// T1 实现：搜索排序（归一化 + 别名扩展 + 加权打分）+ 中文通用叫法别名表
using System;
using System.Collections.Generic;
using System.Text;

namespace YijianNuodong.Core
{
    /// <summary>
    /// 搜索排序：把用户输入（可能只有"p图""微信""wx"这类叫法）与软件信息做归一化匹配并打分。
    /// 分数越高越靠前，0 表示完全不匹配。
    /// </summary>
    public static class SearchRanker
    {
        // ------------------------- 打分权重（统一在此维护） -------------------------
        private const int ScoreDisplayRawEqual = 1000;      // 显示名与查询词完全一致
        private const int ScoreDisplayEqual = 950;          // 去掉空格/标点后完全一致
        private const int ScoreAliasEqual = 900;            // 别名完全一致
        private const int ScoreDisplayEqualExpanded = 880;  // 输入别名，但等于显示名（如输入 wx 命中"微信"）
        private const int ScoreDisplayPrefix = 800;         // 显示名以查询词开头
        private const int ScoreDisplayPrefixExpanded = 760;// 显示名以扩展词开头
        private const int ScoreDisplayContains = 700;       // 显示名包含查询词
        private const int ScoreDisplayContainsExpanded = 680;// 显示名包含扩展词
        private const int ScoreAliasPrefix = 620;           // 别名以查询词开头
        private const int ScoreAliasContains = 600;         // 别名包含查询词
        private const int ScoreMetadataContains = 500;      // 发布者/版本/目录名/exe 名包含查询词
        private const int ScorePathContains = 400;          // 安装路径包含查询词
        private const int ScoreMetadataExpanded = 350;      // 扩展词出现在发布者/路径等字段
        private const int MultiWordBonus = 200;             // 多词查询全部命中的加成

        /// <summary>查询词长度不超过该值时只做"完全一致 / 前缀"匹配，避免匹配到一大堆无关项</summary>
        private const int ShortQueryLength = 2;

        /// <summary>
        /// 归一化：去首尾空白与全角空格、全角 ASCII 转半角、转小写，并去掉空格与常见分隔符/标点，
        /// 只保留中文、字母、数字（例如 "Adobe Photoshop 2024（64 位）" -> "adobephotoshop2024位"）。
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\u3000' || ch == '\u00A0') continue;          // 全角空格 / 不间断空格
                if (ch >= '\uFF01' && ch <= '\uFF5E') ch = (char)(ch - 0xFEE0); // 全角 ASCII -> 半角
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
                // 其余字符（空白、- _ . , ( ) [ ] · 、 等）一律丢弃
            }
            return sb.ToString();
        }

        /// <summary>
        /// 综合打分：0 表示不匹配。命中别名时会把 program.AliasMatched 设为命中的别名（供界面展示）。
        /// </summary>
        public static int Score(InstalledProgram program, string query)
        {
            if (program == null) return 0;
            string rawQuery = TextUtil.Clean(query);
            if (rawQuery.Length == 0) return 0;
            string queryNorm = Normalize(rawQuery);
            if (queryNorm.Length == 0) return 0;

            program.AliasMatched = null;

            string displayName = program.DisplayName == null ? string.Empty : program.DisplayName.Trim();
            string displayNorm = Normalize(displayName);
            string directoryName = GetLeafName(program.InstallPath);
            string directoryNorm = Normalize(directoryName);
            string exeName = GetFileNameWithoutExtension(program.ExecutablePath);
            string exeNorm = Normalize(exeName);
            string publisherNorm = Normalize(program.Publisher);
            string versionNorm = Normalize(program.Version);
            string pathNorm = Normalize(program.InstallPath);

            string[] aliases = CollectAliases(displayName, directoryName, exeName);
            List<string> expanded = AppAliases.ExpandQuery(queryNorm);
            bool shortQuery = queryNorm.Length <= ShortQueryLength;

            int score = 0;
            string matchedAlias = null;

            // 1) 显示名与用户输入完全一致（忽略大小写与首尾空白）
            if (displayName.Length > 0 && string.Equals(displayName, rawQuery, StringComparison.OrdinalIgnoreCase))
            {
                score = ScoreDisplayRawEqual;
            }
            // 2) 归一化后完全一致
            if (score == 0 && displayNorm.Length > 0 && displayNorm == queryNorm)
            {
                score = ScoreDisplayEqual;
            }
            // 3) 别名完全一致（如输入 ps 命中 Photoshop）
            if (score == 0 && aliases.Length > 0)
            {
                int aliasScore = MatchAlias(aliases, queryNorm, shortQuery, out matchedAlias);
                if (aliasScore >= ScoreAliasEqual)
                {
                    score = aliasScore;
                    program.AliasMatched = matchedAlias;
                }
            }
            // 4) 显示名等于扩展词（如输入 wx，显示名是"微信"）
            if (score == 0 && displayNorm.Length > 0 && ContainsExpandedEqual(expanded, displayNorm, queryNorm))
            {
                score = ScoreDisplayEqualExpanded;
            }
            // 5) 显示名以查询词开头
            if (score == 0 && displayNorm.Length > 0 && displayNorm.StartsWith(queryNorm, StringComparison.Ordinal))
            {
                score = ScoreDisplayPrefix;
            }
            // 6) 显示名以扩展词开头
            if (score == 0 && displayNorm.Length > 0)
            {
                string hit = FindExpandedPrefix(expanded, displayNorm, queryNorm);
                if (hit != null) score = ScoreDisplayPrefixExpanded;
            }
            // 7) 显示名包含查询词（短查询不做包含匹配；英文查询词要求落在单词边界上）
            if (score == 0 && !shortQuery && displayNorm.Length > 0 &&
                IsDisplayContains(displayName, displayNorm, queryNorm))
            {
                score = ScoreDisplayContains;
            }
            // 8) 显示名包含扩展词（如输入 wx 而显示名是"微信电脑版"）
            if (score == 0 && displayNorm.Length > 0)
            {
                string hit = FindExpandedContains(expanded, displayNorm, queryNorm, 2);
                if (hit != null) score = ScoreDisplayContainsExpanded;
            }
            // 9) 别名前缀 / 包含
            if (score == 0 && aliases.Length > 0)
            {
                int aliasScore = MatchAlias(aliases, queryNorm, shortQuery, out matchedAlias);
                if (aliasScore > 0)
                {
                    score = aliasScore;
                    program.AliasMatched = matchedAlias;
                }
            }
            // 10) 发布者 / 版本 / 安装目录名 / exe 名包含查询词
            if (score == 0 && !shortQuery)
            {
                if (Contains(publisherNorm, queryNorm) || Contains(versionNorm, queryNorm) ||
                    Contains(directoryNorm, queryNorm) || Contains(exeNorm, queryNorm))
                {
                    score = ScoreMetadataContains;
                }
                else if (Contains(pathNorm, queryNorm))
                {
                    score = ScorePathContains;
                }
            }
            // 11) 扩展词出现在目录/exe 名中（如输入 wx，目录名是 WeChat）
            if (score == 0 && !shortQuery)
            {
                string hit = FindExpandedContains(expanded, directoryNorm, queryNorm, 3);
                if (hit == null) hit = FindExpandedContains(expanded, exeNorm, queryNorm, 3);
                if (hit == null) hit = FindExpandedContains(expanded, pathNorm, queryNorm, 3);
                if (hit != null) score = ScoreMetadataExpanded;
            }

            if (score == 0) return 0;

            // 多词查询：所有词都必须命中，否则视为不匹配；全命中加固定分
            int wordBonus = CheckMultiWords(rawQuery, queryNorm, displayNorm, publisherNorm, versionNorm,
                                            directoryNorm, exeNorm, pathNorm, aliases, expanded);
            if (wordBonus < 0) return 0;
            score += wordBonus;

            return score;
        }

        /// <summary>
        /// 排序并截断：分数降序，同分时"大小已知优先、体积大的优先"，再按显示名排序。
        /// 不匹配的程序不会返回；query 为空或 maxResults &lt;= 0 时返回空列表。
        /// </summary>
        public static List<InstalledProgram> Rank(IEnumerable<InstalledProgram> programs, string query, int maxResults)
        {
            List<InstalledProgram> result = new List<InstalledProgram>();
            if (programs == null || maxResults <= 0) return result;
            string rawQuery = TextUtil.Clean(query);
            if (rawQuery.Length == 0) return result;

            List<KeyValuePair<int, InstalledProgram>> scored = new List<KeyValuePair<int, InstalledProgram>>();
            foreach (InstalledProgram p in programs)
            {
                if (p == null) continue;
                int s = Score(p, rawQuery);
                if (s > 0) scored.Add(new KeyValuePair<int, InstalledProgram>(s, p));
            }

            scored.Sort(CompareScored);
            int count = scored.Count < maxResults ? scored.Count : maxResults;
            for (int i = 0; i < count; i++)
            {
                result.Add(scored[i].Value);
            }
            return result;
        }

        // ------------------------------ 内部辅助 ------------------------------

        /// <summary>分数降序；同分时已知大小优先、大的优先；再按显示名排序</summary>
        private static int CompareScored(KeyValuePair<int, InstalledProgram> a, KeyValuePair<int, InstalledProgram> b)
        {
            if (a.Key != b.Key) return b.Key.CompareTo(a.Key);

            InstalledProgram pa = a.Value;
            InstalledProgram pb = b.Value;
            bool aKnown = pa != null && pa.SizeBytes >= 0;
            bool bKnown = pb != null && pb.SizeBytes >= 0;
            if (aKnown != bKnown) return aKnown ? -1 : 1;
            if (aKnown && bKnown && pa.SizeBytes != pb.SizeBytes) return pb.SizeBytes.CompareTo(pa.SizeBytes);

            string an = pa == null || pa.DisplayName == null ? string.Empty : pa.DisplayName;
            string bn = pb == null || pb.DisplayName == null ? string.Empty : pb.DisplayName;
            return string.Compare(an, bn, StringComparison.CurrentCulture);
        }

        /// <summary>收集程序可用的别名（显示名、安装目录名、exe 名三路查询，去重）</summary>
        private static string[] CollectAliases(string displayName, string directoryName, string exeName)
        {
            List<string> raw = new List<string>();
            List<string> normalized = new List<string>();
            AddAliases(raw, normalized, displayName);
            AddAliases(raw, normalized, directoryName);
            AddAliases(raw, normalized, exeName);
            return raw.ToArray();
        }

        private static void AddAliases(List<string> raw, List<string> normalized, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            // 打分使用"强匹配"别名，避免"微信"误拿到"企业微信"的别名（如 wxwork）造成误命中
            string[] found = AppAliases.GetRankingAliases(name);
            for (int i = 0; i < found.Length; i++)
            {
                string alias = found[i];
                if (string.IsNullOrEmpty(alias)) continue;
                string n = Normalize(alias);
                if (n.Length == 0) continue;
                if (normalized.Contains(n)) continue;
                normalized.Add(n);
                raw.Add(alias.Trim());
            }
        }

        /// <summary>
        /// 别名匹配：先找完全一致（900），再找前缀（620），最后（非短查询）包含（600）。找不到返回 0。
        /// </summary>
        private static int MatchAlias(string[] aliases, string queryNorm, bool shortQuery, out string matched)
        {
            matched = null;
            if (aliases == null || aliases.Length == 0) return 0;

            for (int i = 0; i < aliases.Length; i++)
            {
                string a = Normalize(aliases[i]);
                if (a.Length == 0) continue;
                if (a == queryNorm)
                {
                    matched = aliases[i];
                    return ScoreAliasEqual;
                }
            }
            for (int i = 0; i < aliases.Length; i++)
            {
                string a = Normalize(aliases[i]);
                if (a.Length == 0 || a.Length < queryNorm.Length) continue;
                if (a.StartsWith(queryNorm, StringComparison.Ordinal))
                {
                    matched = aliases[i];
                    return ScoreAliasPrefix;
                }
                if (!shortQuery && a.IndexOf(queryNorm, StringComparison.Ordinal) >= 0)
                {
                    matched = aliases[i];
                    return ScoreAliasContains;
                }
            }
            return 0;
        }

        /// <summary>显示名是否等于某个扩展词（排除原查询词本身）</summary>
        private static bool ContainsExpandedEqual(List<string> expanded, string displayNorm, string queryNorm)
        {
            for (int i = 0; i < expanded.Count; i++)
            {
                string c = expanded[i];
                if (c.Length < 2 || string.Equals(c, queryNorm, StringComparison.Ordinal)) continue;
                if (string.Equals(c, displayNorm, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 扩展词是否可以参与"包含/前缀"匹配：必须比原查询更具体（更长），
        /// 或为中文别名（如"微信"），否则像 ps-&gt;photoshop 的扩展会反向误伤其它软件。
        /// </summary>
        private static bool AcceptExpandedCandidate(string candidate, string queryNorm, int minLength)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            if (candidate.Length < minLength) return false;
            if (string.Equals(candidate, queryNorm, StringComparison.Ordinal)) return false;
            if (HasCjk(candidate)) return true;                 // 中文别名较短也可靠（微信、钉钉等）
            if (candidate.Length <= queryNorm.Length) return false;
            return candidate.Length >= 4;                       // 英文别名至少 4 个字母才做包含匹配
        }

        private static string FindExpandedPrefix(List<string> expanded, string text, string queryNorm)
        {
            if (text.Length == 0) return null;
            for (int i = 0; i < expanded.Count; i++)
            {
                string c = expanded[i];
                if (!AcceptExpandedCandidate(c, queryNorm, 2)) continue;
                if (text.StartsWith(c, StringComparison.Ordinal)) return c;
            }
            return null;
        }

        private static string FindExpandedContains(List<string> expanded, string text, string queryNorm, int minLength)
        {
            if (text.Length == 0) return null;
            for (int i = 0; i < expanded.Count; i++)
            {
                string c = expanded[i];
                if (!AcceptExpandedCandidate(c, queryNorm, minLength)) continue;
                if (text.IndexOf(c, StringComparison.Ordinal) >= 0) return c;
            }
            return null;
        }

        /// <summary>显示名包含查询词：中文按子串匹配，英文要求落在单词边界上（避免 tim 命中 runtime）</summary>
        private static bool IsDisplayContains(string displayName, string displayNorm, string queryNorm)
        {
            if (displayNorm.Length == 0 || queryNorm.Length == 0) return false;
            if (displayNorm.IndexOf(queryNorm, StringComparison.Ordinal) < 0) return false;
            if (HasCjk(queryNorm)) return true;
            List<string> tokens = Tokenize(displayName);
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (string.Equals(token, queryNorm, StringComparison.Ordinal)) return true;
                if (token.StartsWith(queryNorm, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>把名称拆成归一化后的英文/数字单词（"DeepSeek Harness" -&gt; deepseek, harness）</summary>
        internal static List<string> Tokenize(string name)
        {
            List<string> tokens = new List<string>();
            if (string.IsNullOrEmpty(name)) return tokens;
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (ch == '\u3000') ch = ' ';
                if (ch >= '\uFF01' && ch <= '\uFF5E') ch = (char)(ch - 0xFEE0);
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
                else if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Length = 0;
                }
            }
            if (builder.Length > 0) tokens.Add(builder.ToString());
            return tokens;
        }

        /// <summary>字符串是否包含中日韩等宽字符（用于区分中文查询词与英文查询词）</summary>
        private static bool HasCjk(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch >= '\u2E80' && ch <= '\u9FFF') return true;
                if (ch >= '\uF900' && ch <= '\uFAFF') return true;
            }
            return false;
        }

        private static bool Contains(string text, string queryNorm)
        {
            if (string.IsNullOrEmpty(text) || queryNorm.Length == 0) return false;
            return text.IndexOf(queryNorm, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 多词查询校验：所有词都必须命中某个字段。全部命中返回加成值，否则返回 -1（视为不匹配）。
        /// 单词查询返回 0。
        /// </summary>
        private static int CheckMultiWords(string rawQuery, string queryNorm, string displayNorm, string publisherNorm,
            string versionNorm, string directoryNorm, string exeNorm, string pathNorm, string[] aliases, List<string> expanded)
        {
            string[] terms = rawQuery.Split(new char[] { ' ', '\t', '\u3000', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length <= 1) return 0;

            int hitCount = 0;
            for (int i = 0; i < terms.Length; i++)
            {
                string termNorm = Normalize(terms[i]);
                if (termNorm.Length == 0) continue;
                if (!TermMatches(termNorm, displayNorm, publisherNorm, versionNorm, directoryNorm,
                                 exeNorm, pathNorm, aliases, expanded))
                {
                    return -1;
                }
                hitCount++;
            }
            return hitCount > 0 ? MultiWordBonus : 0;
        }

        private static bool TermMatches(string term, string displayNorm, string publisherNorm, string versionNorm,
            string directoryNorm, string exeNorm, string pathNorm, string[] aliases, List<string> expanded)
        {
            bool strict = term.Length <= ShortQueryLength;
            if (displayNorm.Length > 0)
            {
                if (displayNorm.StartsWith(term, StringComparison.Ordinal)) return true;
                if (!strict && displayNorm.IndexOf(term, StringComparison.Ordinal) >= 0) return true;
            }
            for (int i = 0; i < aliases.Length; i++)
            {
                string a = Normalize(aliases[i]);
                if (a.Length == 0) continue;
                if (a.StartsWith(term, StringComparison.Ordinal)) return true;
                if (!strict && a.IndexOf(term, StringComparison.Ordinal) >= 0) return true;
            }
            if (directoryNorm.StartsWith(term, StringComparison.Ordinal)) return true;
            if (exeNorm.StartsWith(term, StringComparison.Ordinal)) return true;
            if (!strict)
            {
                if (Contains(directoryNorm, term) || Contains(exeNorm, term)) return true;
                if (Contains(publisherNorm, term) || Contains(versionNorm, term) || Contains(pathNorm, term)) return true;
            }
            // 扩展词（例如用户输入 wx，界面显示"微信 电脑版"）
            List<string> subExpanded = AppAliases.ExpandQuery(term);
            if (subExpanded.Count > 0)
            {
                if (FindExpandedContains(subExpanded, displayNorm, term, 2) != null) return true;
                if (!strict && FindExpandedContains(subExpanded, directoryNorm, term, 2) != null) return true;
            }
            return false;
        }

        private static string GetLeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                string p = path.TrimEnd('\\', '/');
                int idx = p.LastIndexOfAny(new char[] { '\\', '/' });
                return idx >= 0 && idx + 1 < p.Length ? p.Substring(idx + 1) : p;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetFileNameWithoutExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                return System.IO.Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// 常见软件的中文通用叫法 / 英文名 / 拼音 / 拼音首字母别名表。
    /// 第一项为官方或常用名称，其后为别名；搜索时双向匹配（"微信"与"WeChat"互相能找到）。
    /// </summary>
    public static class AppAliases
    {
        /// <summary>别名原始数据：每行第一项是官方/常见名称，其余是别名</summary>
        private static readonly string[][] Rows = new string[][]
        {
            new string[] { "微信", "wechat", "weixin", "wx", "微信电脑版" },
            new string[] { "企业微信", "wxwork", "wecom", "qywx", "workwechat" },
            new string[] { "QQ", "tencentqq", "qq", "tim", "腾讯qq" },
            new string[] { "钉钉", "dingtalk", "dd", "dingding" },
            new string[] { "飞书", "feishu", "lark" },
            new string[] { "WPS", "wps", "kingsoft", "金山办公", "jinshan" },
            new string[] { "剪映", "jianying", "capcut", "jy", "剪映专业版" },
            new string[] { "Photoshop", "ps", "adobe", "photoshop", "pscc" },
            new string[] { "Premiere", "pr", "premiere", "adobepremiere", "prcc" },
            new string[] { "After Effects", "ae", "aftereffects", "adobeae" },
            new string[] { "Illustrator", "ai", "illustrator", "adobeillustrator" },
            new string[] { "Steam", "steam", "蒸汽平台" },
            new string[] { "网易云音乐", "cloudmusic", "netease", "163music", "neteasemusic", "wangyiyun" },
            new string[] { "QQ音乐", "qqmusic", "qqyy", "qq音乐" },
            new string[] { "酷狗音乐", "kugou", "kugoumusic", "酷狗" },
            new string[] { "酷我音乐", "kuwo", "kuwomusic", "酷我" },
            new string[] { "百度网盘", "baidunetdisk", "netdisk", "baiduyun", "百度云" },
            new string[] { "阿里云盘", "aliyundrive", "aliyundisk", "阿里云" },
            new string[] { "夸克", "quark", "夸克浏览器", "夸克网盘" },
            new string[] { "迅雷", "thunder", "xunlei", "xl", "迅雷下载" },
            new string[] { "抖音", "douyin", "douyinapp", "tiktok" },
            new string[] { "哔哩哔哩", "bilibili", "bilibiliapp", "blbl", "b站" },
            new string[] { "腾讯会议", "wemeet", "tencentmeeting", "txhy" },
            new string[] { "ToDesk", "todesk", "td" },
            new string[] { "向日葵", "sunlogin", "xiangrikui", "向日葵远程" },
            new string[] { "搜狗输入法", "sogou", "sogoupinyin", "搜狗拼音", "搜狗" },
            new string[] { "QQ浏览器", "qqbrowser", "qq浏览器" },
            new string[] { "360安全卫士", "360", "qihoo", "360safe", "安全卫士" },
            new string[] { "火绒", "huorong", "huorongsecurity", "火绒安全" },
            new string[] { "腾讯视频", "tencentvideo", "qqvideo", "txsp" },
            new string[] { "爱奇艺", "iqiyi", "aiqiyi", "奇艺" },
            new string[] { "优酷", "youku", "youkutudou", "土豆" },
            new string[] { "芒果TV", "mgtv", "mango", "芒果" },
            new string[] { "喜马拉雅", "ximalaya", "xmly" },
            new string[] { "Visual Studio Code", "vscode", "code", "vsc", "visualstudiocode" },
            new string[] { "Git", "git", "gitbash", "gitforwindows" },
            new string[] { "Python", "python", "py", "python3" },
            new string[] { "Anaconda", "anaconda", "conda", "anaconda3", "python" },
            new string[] { "Node.js", "node", "nodejs", "npm" },
            new string[] { "Java", "jdk", "java", "jre", "openjdk" },
            new string[] { "Docker", "docker", "dockerdesktop" },
            new string[] { "MySQL", "mysql", "mysqlworkbench" },
            new string[] { "VMware", "vmware", "vmwareworkstation" },
            new string[] { "VirtualBox", "virtualbox", "vbox" },
            new string[] { "7-Zip", "7zip", "7z", "sevenzip" },
            new string[] { "WinRAR", "winrar", "rar" },
            new string[] { "Bandizip", "bandizip", "bz", "bandi" },
            new string[] { "Office", "office", "word", "excel", "powerpoint", "微软office", "microsoftoffice" },
            new string[] { "OneNote", "onenote", "微软笔记" },
            new string[] { "Teams", "teams", "microsoftteams", "微软teams" },
            new string[] { "微信开发者工具", "wechatdevtools", "微信开发工具", "devtools" },
            new string[] { "千牛", "qianniu", "qn", "千牛工作台" },
            new string[] { "阿里旺旺", "aliwangwang", "wangwang", "旺旺" },
            new string[] { "知乎", "zhihu" },
            new string[] { "小红书", "xiaohongshu", "xhs", "rednote" },
            new string[] { "有道词典", "youdaodict", "youdao", "有道" },
            new string[] { "有道翻译", "youdaotranslate", "youdaofanyi" },
            new string[] { "百度", "baidu", "baidusearch" },
            new string[] { "QQ影音", "qqplayer", "qq影音" },
            new string[] { "腾讯文档", "tencentdocs", "qqdocs", "txwd" },
            new string[] { "驱动精灵", "qudongjingling", "drivergenius" },
            new string[] { "腾讯电脑管家", "qqpcmanager", "tencentpcmanager", "电脑管家", "qq管家" },
            new string[] { "Geek Uninstaller", "geek", "geekuninstaller" },
            new string[] { "印象笔记", "evernote", "yinxiangbiji", "印象" },
            new string[] { "石墨文档", "shimo", "shimodocs" },
            new string[] { "Microsoft Edge", "edge", "microsoftedge", "msedge" },
            new string[] { "Google Chrome", "chrome", "googlechrome", "谷歌浏览器" },
            new string[] { "Firefox", "firefox", "火狐", "火狐浏览器" },
            new string[] { "GitHub Desktop", "githubdesktop", "github" },
            new string[] { "Sublime Text", "sublime", "sublimetext" },
            new string[] { "Notepad++", "notepadpp", "npp", "notepad" },
            new string[] { "Typora", "typora" },
            new string[] { "XMind", "xmind", "思维导图" },
            new string[] { "百度输入法", "baiduinput", "百度拼音" },
            new string[] { "搜狗浏览器", "sogoubrowser" },
            new string[] { "360浏览器", "360browser", "360se", "360极速浏览器" },
            new string[] { "讯飞输入法", "iflytek", "讯飞" },
            new string[] { "金山毒霸", "kingsoft", "duba", "毒霸" },
            new string[] { "360压缩", "360zip" },
            new string[] { "好压", "haozip", "2345好压" },
            new string[] { "微信输入法", "weixininput", "微信键盘" },
            new string[] { "微信读书", "weread" },
            new string[] { "钉钉打卡", "dingtalkcheck" },
            new string[] { "腾讯QQ", "tencentqq", "qq" },
            new string[] { "WPS Office", "wpsoffice", "wps" }
        };

        /// <summary>官方名 -> 别名数组（保持表格顺序，便于界面展示）</summary>
        private static readonly List<KeyValuePair<string, string[]>> Table = BuildTable();

        /// <summary>
        /// 打分专用的"强匹配"别名：只有名称/键/别名确实属于该软件时才返回，
        /// 避免"微信"拿到"企业微信"的 wxwork 这类弱关联别名导致搜索结果跑偏。
        /// </summary>
        internal static string[] GetRankingAliases(string programName)
        {
            string nameNorm = SearchRanker.Normalize(programName);
            if (nameNorm.Length == 0) return new string[0];
            List<string> tokens = SearchRanker.Tokenize(programName);

            List<string> result = new List<string>();
            List<string> normalized = new List<string>();
            for (int i = 0; i < Rows.Length; i++)
            {
                string[] row = Rows[i];
                if (row == null || row.Length < 2) continue;
                if (!RowStronglyMatchesName(row, nameNorm, tokens)) continue;

                for (int j = 1; j < row.Length; j++)
                {
                    string alias = row[j];
                    if (string.IsNullOrEmpty(alias)) continue;
                    string aliasNorm = SearchRanker.Normalize(alias);
                    if (aliasNorm.Length == 0 || normalized.Contains(aliasNorm)) continue;
                    normalized.Add(aliasNorm);
                    result.Add(alias.Trim());
                }
            }
            return result.ToArray();
        }

        /// <summary>按软件显示名/安装目录名查别名列表；双向匹配（如 WeChat 与 微信 互认）</summary>
        public static string[] GetAliases(string programName)
        {
            string nameNorm = SearchRanker.Normalize(programName);
            if (nameNorm.Length == 0) return new string[0];
            List<string> tokens = SearchRanker.Tokenize(programName);

            List<string> result = new List<string>();
            List<string> normalized = new List<string>();
            for (int i = 0; i < Rows.Length; i++)
            {
                string[] row = Rows[i];
                if (row == null || row.Length < 2) continue;
                if (!RowMatchesName(row, nameNorm, tokens)) continue;

                for (int j = 1; j < row.Length; j++)
                {
                    string alias = row[j];
                    if (string.IsNullOrEmpty(alias)) continue;
                    string aliasNorm = SearchRanker.Normalize(alias);
                    if (aliasNorm.Length == 0) continue;
                    if (normalized.Contains(aliasNorm)) continue;
                    normalized.Add(aliasNorm);
                    result.Add(alias.Trim());
                }
            }
            return result.ToArray();
        }

        /// <summary>把用户输入扩展成候选词（归一化后的形式）：原词 + 对应官方名 + 同组其它别名</summary>
        public static List<string> ExpandQuery(string query)
        {
            List<string> result = new List<string>();
            string queryNorm = SearchRanker.Normalize(query);
            if (queryNorm.Length == 0) return result;
            AddCandidate(result, queryNorm);

            for (int i = 0; i < Rows.Length; i++)
            {
                string[] row = Rows[i];
                if (row == null || row.Length < 2) continue;
                if (!RowMatchesQuery(row, queryNorm)) continue;

                AddCandidate(result, SearchRanker.Normalize(row[0]));
                for (int j = 1; j < row.Length; j++)
                {
                    string aliasNorm = SearchRanker.Normalize(row[j]);
                    if (aliasNorm.Length >= 2) AddCandidate(result, aliasNorm);
                }
            }
            return result;
        }

        /// <summary>全部别名数据：官方名 -> 别名数组</summary>
        public static IEnumerable<KeyValuePair<string, string[]>> All
        {
            get { return Table; }
        }

        // ------------------------------ 内部辅助 ------------------------------

        /// <summary>
        /// 行是否与软件名匹配（用于界面展示"通过 xx 匹配"）：
        /// 键出现在软件名里 / 软件名包含键 / 别名与软件名相等（或完整英文词相等）。
        /// </summary>
        private static bool RowMatchesName(string[] row, string nameNorm, List<string> tokens)
        {
            string keyNorm = SearchRanker.Normalize(row[0]);
            if (keyNorm.Length > 0)
            {
                if (string.Equals(keyNorm, nameNorm, StringComparison.Ordinal)) return true;
                if (keyNorm.Length >= 2 && nameNorm.Length >= 2 &&
                    nameNorm.IndexOf(keyNorm, StringComparison.Ordinal) >= 0) return true;
                if (keyNorm.Length >= 2 && nameNorm.Length >= 2 &&
                    keyNorm.IndexOf(nameNorm, StringComparison.Ordinal) >= 0) return true;
            }
            // 英文名/别名的软件（如 WeChat、Microsoft VS Code）：别名相等、完整单词相等或长别名包含在名称里
            for (int j = 1; j < row.Length; j++)
            {
                string aliasNorm = SearchRanker.Normalize(row[j]);
                if (aliasNorm.Length < 2) continue;
                if (string.Equals(aliasNorm, nameNorm, StringComparison.Ordinal)) return true;
                if (tokens.Contains(aliasNorm)) return true;
                if (aliasNorm.Length >= 6 && nameNorm.IndexOf(aliasNorm, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 行是否"确实属于"该软件（打分专用）：名称与键相等、完整英文词相等，
        /// 或 6 字符以上的键/别名出现在名称中。用于避免 "tim" 命中 "runtime" 这类误匹配。
        /// </summary>
        private static bool RowStronglyMatchesName(string[] row, string nameNorm, List<string> tokens)
        {
            string keyNorm = SearchRanker.Normalize(row[0]);
            if (keyNorm.Length > 0)
            {
                if (string.Equals(keyNorm, nameNorm, StringComparison.Ordinal)) return true;
                if (tokens.Contains(keyNorm)) return true;
                if (keyNorm.Length >= 6 && nameNorm.IndexOf(keyNorm, StringComparison.Ordinal) >= 0) return true;
            }
            for (int j = 1; j < row.Length; j++)
            {
                string aliasNorm = SearchRanker.Normalize(row[j]);
                if (aliasNorm.Length < 2) continue;
                if (string.Equals(aliasNorm, nameNorm, StringComparison.Ordinal)) return true;
                if (tokens.Contains(aliasNorm)) return true;
                if (aliasNorm.Length >= 6 && nameNorm.IndexOf(aliasNorm, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>行是否与用户输入匹配（输入可能是官方名或任意别名）</summary>
        private static bool RowMatchesQuery(string[] row, string queryNorm)
        {
            string keyNorm = SearchRanker.Normalize(row[0]);
            if (string.Equals(keyNorm, queryNorm, StringComparison.Ordinal)) return true;
            for (int j = 1; j < row.Length; j++)
            {
                string aliasNorm = SearchRanker.Normalize(row[j]);
                if (string.Equals(aliasNorm, queryNorm, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void AddCandidate(List<string> list, string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return;
            if (candidate.Length < 2) return;
            if (list.Contains(candidate)) return;
            list.Add(candidate);
        }

        private static List<KeyValuePair<string, string[]>> BuildTable()
        {
            List<KeyValuePair<string, string[]>> table = new List<KeyValuePair<string, string[]>>(Rows.Length);
            for (int i = 0; i < Rows.Length; i++)
            {
                string[] row = Rows[i];
                if (row == null || row.Length < 2) continue;
                string[] aliases = new string[row.Length - 1];
                Array.Copy(row, 1, aliases, 0, aliases.Length);
                table.Add(new KeyValuePair<string, string[]>(row[0], aliases));
            }
            return table;
        }
    }
}
