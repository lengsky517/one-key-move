// 软件列表排序：把比较逻辑放在 Core 里，UI 只负责点击事件，便于自检覆盖
using System;
using System.Collections.Generic;

namespace YijianNuodong.Core
{
    /// <summary>第 1 步软件列表的排序规则（列序号与界面表头一致）。</summary>
    public static class ProgramSort
    {
        public const int ColumnName = 0;
        public const int ColumnVersion = 1;
        public const int ColumnPublisher = 2;
        public const int ColumnPath = 3;
        public const int ColumnSize = 4;
        public const int ColumnSource = 5;

        /// <summary>点同一列切换升降序时用的默认方向：体积默认大的在前，其它默认 A→Z。</summary>
        public static bool DefaultAscending(int column)
        {
            return column != ColumnSize;
        }

        public static void Sort(List<InstalledProgram> programs, int column, bool ascending)
        {
            if (programs == null || programs.Count < 2) return;
            programs.Sort(delegate(InstalledProgram a, InstalledProgram b)
            {
                return Compare(column, ascending, a, b);
            });
        }

        /// <summary>
        /// 比较两个软件。约定：体积未知（-1）的永远排在最后，不随升降序翻转，
        /// 否则「按大小排序」时一排「计算中…」会顶到最前面。
        /// </summary>
        public static int Compare(int column, bool ascending, InstalledProgram a, InstalledProgram b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;

            int result;
            switch (column)
            {
                case ColumnName:
                    result = CompareText(a.DisplayName, b.DisplayName);
                    break;
                case ColumnVersion:
                    result = CompareVersion(a.Version, b.Version);
                    break;
                case ColumnPublisher:
                    result = CompareText(a.Publisher, b.Publisher);
                    break;
                case ColumnPath:
                    result = CompareText(a.InstallPath, b.InstallPath);
                    break;
                case ColumnSize:
                    if (a.SizeBytes < 0 && b.SizeBytes < 0) result = 0;
                    else if (a.SizeBytes < 0) return 1;
                    else if (b.SizeBytes < 0) return -1;
                    else if (a.SizeBytes < b.SizeBytes) result = -1;
                    else if (a.SizeBytes > b.SizeBytes) result = 1;
                    else result = 0;
                    if (result == 0) result = CompareText(a.DisplayName, b.DisplayName);
                    return ascending ? result : -result;
                case ColumnSource:
                    result = CompareText(SourceLabel(a.Source), SourceLabel(b.Source));
                    break;
                default:
                    result = 0;
                    break;
            }

            // 主键相同按名称兜底，保证每次排序结果一致（List.Sort 不稳定）
            if (result == 0 && column != ColumnName) result = CompareText(a.DisplayName, b.DisplayName);
            return ascending ? result : -result;
        }

        public static int CompareText(string a, string b)
        {
            bool emptyA = string.IsNullOrEmpty(a);
            bool emptyB = string.IsNullOrEmpty(b);
            if (emptyA && emptyB) return 0;
            if (emptyA) return 1;
            if (emptyB) return -1;
            return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>版本号自然序：1.9 排在 1.10 前面，而不是按字符串比。</summary>
        public static int CompareVersion(string a, string b)
        {
            bool emptyA = string.IsNullOrEmpty(a);
            bool emptyB = string.IsNullOrEmpty(b);
            if (emptyA && emptyB) return 0;
            if (emptyA) return 1;
            if (emptyB) return -1;

            int i = 0;
            int j = 0;
            while (i < a.Length && j < b.Length)
            {
                bool digitA = char.IsDigit(a[i]);
                bool digitB = char.IsDigit(b[j]);
                if (digitA && digitB)
                {
                    int startA = i;
                    int startB = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string numA = a.Substring(startA, i - startA).TrimStart('0');
                    string numB = b.Substring(startB, j - startB).TrimStart('0');
                    if (numA.Length == 0) numA = "0";
                    if (numB.Length == 0) numB = "0";
                    if (numA.Length != numB.Length) return numA.Length < numB.Length ? -1 : 1;
                    int cmp = string.CompareOrdinal(numA, numB);
                    if (cmp != 0) return cmp < 0 ? -1 : 1;
                    continue;
                }
                char charA = char.ToLowerInvariant(a[i]);
                char charB = char.ToLowerInvariant(b[j]);
                if (charA != charB) return charA < charB ? -1 : 1;
                i++;
                j++;
            }
            int restA = a.Length - i;
            int restB = b.Length - j;
            if (restA == restB) return 0;
            return restA < restB ? -1 : 1;
        }

        public static string SourceLabel(ProgramSource source)
        {
            if ((source & ProgramSource.Shortcut) != 0) return "快捷方式";
            if ((source & ProgramSource.Registry) != 0) return "注册表";
            if ((source & ProgramSource.AppPaths) != 0) return "应用路径";
            if ((source & ProgramSource.StartMenu) != 0) return "开始菜单";
            if ((source & ProgramSource.RunningProcess) != 0) return "运行进程";
            if ((source & ProgramSource.FolderScan) != 0) return "目录扫描";
            if ((source & ProgramSource.ManualPath) != 0) return "手动指定";
            return "未知";
        }
    }
}
