// .NET Framework 4.8 / C# 5. Scan results are display snapshots, never arbitrary path requests.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
namespace YijianNuodong.Core
{
    public sealed class DataMoveItem
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string SourcePath { get; internal set; }
        public string RelativeTarget { get; internal set; }
        public string ActualPath { get; internal set; }
        public string Advice { get; internal set; }
        public string Status { get; internal set; }
        public long SizeBytes { get; internal set; }
        public bool CanMove { get; internal set; }
        internal string Stamp;
    }
    public sealed class SpaceRow
    {
        public string Name { get; internal set; }
        public string Path { get; internal set; }
        public long SizeBytes { get; internal set; }
        public string Nature { get; internal set; }
        public string Advice { get; internal set; }
    }
    public sealed class DataScanResult
    {
        internal readonly List<DataMoveItem> MutableItems = new List<DataMoveItem>();
        internal readonly List<SpaceRow> MutableRows = new List<SpaceRow>();
        public ReadOnlyCollection<DataMoveItem> Items { get { return MutableItems.AsReadOnly(); } }
        public ReadOnlyCollection<SpaceRow> Rows { get { return MutableRows.AsReadOnly(); } }
        public string Summary { get; internal set; }
        public bool Complete { get; internal set; }
    }
    public sealed class PipSettingResult
    {
        public string ConfigPath { get; internal set; }
        public string BackupPath { get; internal set; }
        public string Destination { get; internal set; }
        public bool Existed { get; internal set; }
        internal byte[] Original, Written;
    }
}
