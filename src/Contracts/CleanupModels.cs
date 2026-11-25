// 清理契约：扫描只读；执行仅接受同一次扫描中勾选的文件标识。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace YijianNuodong.Core
{
    public enum CleanupCategory { TemporaryFiles, BrowserCache, DownloadedInstallers }

    public sealed class CleanupItem
    {
        public string Id { get; internal set; }
        public string FullPath { get; internal set; }
        public string RootPath { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Description { get; internal set; }
        public CleanupCategory Category { get; internal set; }
        public long SizeBytes { get; internal set; }
        public DateTime LastWriteTimeUtc { get; internal set; }
        public bool DefaultSelected { get; internal set; }
        internal string Identity;
        internal long CreationTime;
        internal long WriteTime;
    }

    internal sealed class CleanupRoot
    {
        internal string Path;
        internal CleanupCategory Category;
        internal string Description;
        internal int MinimumAgeDays;
    }

    public sealed class CleanupScanResult
    {
        private readonly List<CleanupItem> _items = new List<CleanupItem>();
        private readonly List<string> _warnings = new List<string>();
        public ReadOnlyCollection<CleanupItem> Items { get { return _items.AsReadOnly(); } }
        public ReadOnlyCollection<string> Warnings { get { return _warnings.AsReadOnly(); } }
        public bool Canceled { get; internal set; }
        public bool LimitReached { get; internal set; }
        public int RootCount { get; internal set; }
        public string DriveLabel { get; internal set; }
        internal List<CleanupItem> MutableItems { get { return _items; } }
        internal List<string> MutableWarnings { get { return _warnings; } }
        internal readonly Dictionary<string, CleanupRoot> Roots = new Dictionary<string, CleanupRoot>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, CleanupItem> Candidates = new Dictionary<string, CleanupItem>(StringComparer.Ordinal);
    }

    public sealed class CleanupRequest
    {
        public CleanupScanResult Scan { get; set; }
        public IList<string> SelectedIds { get; set; }
        public bool ConfirmPermanentDelete { get; set; }
        public bool ConfirmInstallers { get; set; }
    }

    public sealed class CleanupProgress
    {
        public string Message { get; internal set; }
        public int Processed { get; internal set; }
        public int Total { get; internal set; }
        public long Bytes { get; internal set; }
    }

    public sealed class CleanupResult
    {
        public int DeletedFiles { get; internal set; }
        public long DeletedBytes { get; internal set; }
        public int SkippedFiles { get; internal set; }
        public bool Canceled { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public string LogPath { get; internal set; }
        public readonly List<string> Details = new List<string>();
    }
}
