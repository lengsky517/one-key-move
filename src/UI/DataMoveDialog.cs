using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using YijianNuodong.Core;

namespace YijianNuodong.UI
{
    public sealed class DataMoveDialog : Form
    {
        private readonly TabControl tabs = new TabControl();
        private readonly ListView space = MakeList(false), data = MakeList(true), installers = MakeList(true);
        private readonly Label summary = new Label(), status = new Label();
        private readonly TextBox target = new TextBox(), pipTarget = new TextBox(), pipStatus = new TextBox(), result = new TextBox();
        private readonly Button scanButton = new Button(), moveButton = new Button(), archiveButton = new Button(), pipButton = new Button(), pipUndoButton = new Button(), undoButton = new Button();
        private readonly CheckBox closed = new CheckBox();
        private readonly BackgroundWorker worker = new BackgroundWorker();
        private DataScanResult scan;
        private CleanupScanResult installerScan;
        private PipSettingResult pipChange;
        private readonly List<string> journals = new List<string>();
        private volatile bool cancel;
        private bool demo;
        public DataMoveDialog()
        {
            Text = "空间盘点 / 数据搬家"; StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1160, 810); MinimumSize = new Size(1000, 740); Font = Theme.Body;
            BackColor = Theme.Surface; ForeColor = Theme.TextPrimary;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            summary.Text = "先扫描、再勾选、核对最终路径后执行。扫描全程只读，不自动移动或删除。\r\n逻辑大小不是实际释放量；汇总与子项有重叠，不能相加。未知/权限不足显示“未完整”。";
            summary.Dock = DockStyle.Fill; layout.Controls.Add(summary, 0, 0);
            tabs.Dock = DockStyle.Fill; layout.Controls.Add(tabs, 0, 1);
            TabPage spacePage = new TabPage("空间盘点（只读）"), dataPage = new TabPage("可搬数据（白名单）"), installerPage = new TabPage("安装包归档"), pipPage = new TabPage("pip 默认下载目录");
            tabs.TabPages.AddRange(new TabPage[] { spacePage, dataPage, installerPage, pipPage });
            AddColumns(space, new string[] { "项目", "逻辑大小", "性质 / 完整性", "建议", "路径" }, new int[] { 150, 95, 235, 360, 360 }); spacePage.Controls.Add(space);
            AddColumns(data, new string[] { "数据项", "大小", "状态", "原路径 / 实际位置", "最终目标目录", "用途 / 提醒" }, new int[] { 150, 95, 180, 330, 290, 360 }); dataPage.Controls.Add(data);
            data.ItemCheck += delegate(object sender, ItemCheckEventArgs e) { DataMoveItem item = data.Items[e.Index].Tag as DataMoveItem; if (demo || item == null || !item.CanMove || scan == null || !scan.Complete) e.NewValue = CheckState.Unchecked; };
            AddColumns(installers, new string[] { "文件名（默认不选）", "大小", "原路径", "说明" }, new int[] { 250, 100, 430, 420 }); installerPage.Controls.Add(installers);
            installers.ItemCheck += delegate(object sender, ItemCheckEventArgs e) { if (demo || installerScan == null || installerScan.Canceled) e.NewValue = CheckState.Unchecked; };
            TableLayoutPanel pipLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
            pipLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55)); pipLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); pipLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pipLayout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "这是 pip download 的下载成品目录，和 pip 包缓存分开。\r\n仅修改用户级 pip.ini 的 [download] dest；PowerShell/CMD 下条命令生效，-d 可覆盖。" }, 0, 0);
            pipTarget.Dock = DockStyle.Fill; pipLayout.Controls.Add(pipTarget, 0, 1);
            pipStatus.Multiline = true; pipStatus.ReadOnly = true; pipStatus.ScrollBars = ScrollBars.Both; pipStatus.Dock = DockStyle.Fill; pipStatus.Text = PipDownloadSettings.ReadStatus(); pipLayout.Controls.Add(pipStatus, 0, 2); pipPage.Controls.Add(pipLayout);
            TableLayoutPanel destination = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
            destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146)); destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            destination.Controls.Add(new Label { Text = "目标父目录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            target.Dock = DockStyle.Fill; target.Text = DefaultTarget(); destination.Controls.Add(target, 1, 0);
            Button browse = new Button { Text = "选择目录", Dock = DockStyle.Fill }; browse.Click += delegate { using (FolderBrowserDialog picker = new FolderBrowserDialog { Description = "选择数据父目录，例如 D:\\SoftwareData；下表实时显示每项完整目标路径" }) if (picker.ShowDialog(this) == DialogResult.OK) target.Text = picker.SelectedPath; }; destination.Controls.Add(browse, 2, 0);
            closed.Text = "我已退出相关软件、终端和自动更新进程（不强杀、不停服务）；执行后请重新扫描核对。"; closed.Dock = DockStyle.Fill; destination.Controls.Add(closed, 0, 1); destination.SetColumnSpan(closed, 3); layout.Controls.Add(destination, 0, 2);
            target.TextChanged += delegate { RefreshTargets(); }; pipTarget.Text = Path.Combine(target.Text, @"Python\pip\downloads");
            result.Multiline = true; result.ReadOnly = true; result.ScrollBars = ScrollBars.Both; result.Dock = DockStyle.Fill; result.WordWrap = false;
            result.Text = "安装包归档到目标父目录\\Installers；逐文件 SHA256 核验后才移走源文件。可按记录手动复制回原处。\r\nWindows、休眠、虚拟内存、还原点只展示，不提供直接移动/删除。"; layout.Controls.Add(result, 0, 3);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
            Configure(scanButton, "只读扫描", delegate { StartScan(); });
            Configure(moveButton, "搬移勾选数据", delegate { StartMove(); });
            Configure(archiveButton, "归档勾选安装包", delegate { StartArchive(); });
            Configure(pipButton, "保存 pip 设置", delegate { SavePip(); });
            Configure(pipUndoButton, "撤销 pip 设置", delegate { UndoPip(); });
            Configure(undoButton, "撤销本次搬家", delegate { UndoMove(); });
            Button stop = new Button(); Configure(stop, "取消任务", delegate { cancel = true; status.Text = "正在安全停止，请勿强关窗口…"; });
            actions.Controls.AddRange(new Control[] { scanButton, moveButton, archiveButton, pipButton, pipUndoButton, undoButton, stop }); layout.Controls.Add(actions, 0, 4);
            Controls.Add(layout); status.Dock = DockStyle.Bottom; status.Height = 26; status.Padding = new Padding(20, 0, 0, 0); Controls.Add(status);
            worker.WorkerReportsProgress = true;
            worker.ProgressChanged += delegate(object sender, ProgressChangedEventArgs e) { status.Text = Convert.ToString(e.UserState); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (worker.IsBusy) { cancel = true; e.Cancel = true; status.Text = "正在安全停止；任务结束后可关闭窗口。"; } };
            Shown += delegate { if (!demo) StartScan(); };
            SetBusy(false);
        }
        private static string DefaultTarget()
        {
            foreach (string drive in new string[] { @"E:\", @"D:\" }) if (Directory.Exists(drive)) return Path.Combine(drive, "SoftwareData");
            return @"D:\SoftwareData";
        }
        private static ListView MakeList(bool checks) { return new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = checks, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true }; }
        private static void AddColumns(ListView list, string[] names, int[] widths) { for (int i = 0; i < names.Length; i++) list.Columns.Add(names[i], widths[i]); }
        private static void Configure(Button button, string text, Action click) { button.Text = text; button.AutoSize = true; button.Height = 32; button.Click += delegate { click(); }; }
        private static string SizeText(long bytes) { return bytes < 0 ? "未测量/未完整" : TextUtil.FormatBytes(bytes); }
        private void SetBusy(bool busy)
        {
            scanButton.Enabled = !busy && !demo; moveButton.Enabled = !busy && !demo && scan != null && scan.Complete;
            archiveButton.Enabled = !busy && !demo && installerScan != null && !installerScan.Canceled;
            pipButton.Enabled = !busy && !demo; pipUndoButton.Enabled = !busy && !demo && pipChange != null;
            undoButton.Enabled = !busy && !demo && journals.Count > 0; tabs.Enabled = !busy; target.Enabled = !busy; closed.Enabled = !busy;
        }
        private void Run(Action work, Action done)
        {
            if (worker.IsBusy || demo) return; cancel = false; SetBusy(true);
            DoWorkEventHandler execute = null; RunWorkerCompletedEventHandler complete = null;
            execute = delegate { work(); };
            complete = delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                worker.DoWork -= execute; worker.RunWorkerCompleted -= complete;
                if (e.Error != null) { Log.Error("空间盘点/数据搬家失败", e.Error); result.Text = e.Error.ToString(); }
                else if (done != null) done();
                status.Text = cancel ? "已取消，请重新扫描。" : (e.Error == null ? "任务完成，请查看记录。" : "失败；未完成的项目请重新扫描核对。"); SetBusy(false);
            };
            worker.DoWork += execute; worker.RunWorkerCompleted += complete; worker.RunWorkerAsync();
        }
        private void Report(string text) { worker.ReportProgress(0, text); }
        private void StartScan()
        {
            scan = null; installerScan = null; data.Items.Clear(); installers.Items.Clear(); space.Items.Clear();
            Run(delegate
            {
                scan = DataMoveEngine.Scan(Report, delegate { return cancel; });
                if (!cancel) installerScan = CleanupEngine.Scan(delegate(CleanupProgress p) { Report(p.Message); }, delegate { return cancel; });
            }, FillScan);
        }
        private void FillScan()
        {
            if (scan == null) return;
            summary.Text = scan.Summary + "\r\n全程只读；逻辑大小有重叠/硬链接，不能相加。跳过链接；未知不当作零；未完整扫描不允许搬移。";
            foreach (SpaceRow row in scan.Rows) { ListViewItem li = new ListViewItem(new string[] { row.Name, SizeText(row.SizeBytes), row.Nature, row.Advice, row.Path }); li.ToolTipText = row.Path + "\r\n" + row.Advice; space.Items.Add(li); }
            foreach (DataMoveItem item in scan.Items) { ListViewItem li = new ListViewItem(new string[] { item.Name, SizeText(item.SizeBytes), item.Status, item.SourcePath + (string.IsNullOrEmpty(item.ActualPath) ? "" : " → " + item.ActualPath), "", item.Advice }); li.Tag = item; li.ToolTipText = item.SourcePath + "\r\n" + item.Advice; if (!item.CanMove) li.ForeColor = SystemColors.GrayText; data.Items.Add(li); }
            if (installerScan != null) foreach (CleanupItem item in installerScan.Items) if (item.Category == CleanupCategory.DownloadedInstallers) { ListViewItem li = new ListViewItem(new string[] { item.DisplayName, SizeText(item.SizeBytes), item.FullPath, "下载目录中的白名单安装包/可执行文件；请确认不是仍需留原位的便携软件。" }); li.Tag = item; installers.Items.Add(li); }
            RefreshTargets();
            result.Text = "扫描完成；未改动任何文件。数据 " + data.Items.Count + " 项，安装包候选 " + installers.Items.Count + " 个，全部默认不勾选。\r\n" + (installerScan == null ? "安装包扫描未完成。" : string.Join("\r\n", new List<string>(installerScan.Warnings).ToArray()));
        }
        private void RefreshTargets()
        {
            foreach (ListViewItem li in data.Items)
            {
                DataMoveItem item = li.Tag as DataMoveItem;
                try { li.SubItems[4].Text = DataMoveEngine.TargetFor(item, target.Text); } catch { li.SubItems[4].Text = "目标目录无效"; }
            }
        }
        private void StartMove()
        {
            if (!closed.Checked) { MessageBox.Show(this, "请先退出相关软件，再勾选退出确认。", Text); return; }
            List<DataMoveItem> selected = new List<DataMoveItem>(); StringBuilder preview = new StringBuilder("将逐项复制 → SHA256 校验 → 原路径联接；不合并/覆盖既有目标。\r\n\r\n");
            try
            {
                foreach (ListViewItem li in data.CheckedItems) { DataMoveItem item = li.Tag as DataMoveItem; if (item != null && item.CanMove) { selected.Add(item); preview.Append(item.Name).Append("\r\n").Append(item.SourcePath).Append("\r\n→ ").Append(DataMoveEngine.TargetFor(item, target.Text)).Append("\r\n\r\n"); } }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "目标目录无效"); return; }
            if (selected.Count == 0) { MessageBox.Show(this, "尚未勾选可搬数据。", Text); return; }
            if (MessageBox.Show(this, preview.ToString(), "确认源路径与最终目标", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            string root = target.Text; DataScanResult snapshot = scan; StringBuilder log = new StringBuilder();
            Run(delegate
            {
                foreach (DataMoveItem item in selected)
                {
                    if (cancel) break;
                    MoveResult moved = DataMoveEngine.Execute(snapshot, item.Id, root, true, delegate(MoveProgress p) { Report(item.Name + "：" + p.Message); }, delegate { return cancel; });
                    log.AppendLine(item.Name + "：" + (moved.Success ? "成功" : "失败：" + moved.ErrorMessage));
                    if (moved.Success && !string.IsNullOrEmpty(moved.JournalPath)) { journals.Add(moved.JournalPath); log.AppendLine("撤销日记：" + moved.JournalPath); }
                    foreach (string warning in moved.Warnings) log.AppendLine(warning);
                    if (!moved.Success) break;
                }
            }, delegate { result.Text = log.ToString() + "\r\n请重新扫描确认已联接状态；本批扫描已作废。"; scan = null; foreach (ListViewItem li in data.Items) { li.Checked = false; li.SubItems[2].Text = "请重新扫描"; } closed.Checked = false; });
        }
        private void StartArchive()
        {
            List<string> ids = new List<string>(); StringBuilder preview = new StringBuilder();
            foreach (ListViewItem li in installers.CheckedItems) { CleanupItem item = li.Tag as CleanupItem; if (item != null) { ids.Add(item.Id); preview.AppendLine(item.FullPath); } }
            if (ids.Count == 0) { MessageBox.Show(this, "请勾选确认不用留原位的安装包。", Text); return; }
            string destination = Path.Combine(target.Text, "Installers");
            if (MessageBox.Show(this, "仅归档勾选的文件，SHA256 校验后移除原文件，不进回收站。\r\n目标：" + destination + "\r\n为防重名添加随机前缀；按记录可手动复制回原路径。\r\n\r\n" + preview, "确认安装包归档", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            CleanupScanResult snapshot = installerScan; CleanupResult archived = null;
            Run(delegate { archived = CleanupEngine.ArchiveInstallers(snapshot, ids, destination, true, delegate(CleanupProgress p) { Report(p.Message); }, delegate { return cancel; }); }, delegate { result.Text = "已归档 " + archived.DeletedFiles + " 个；跳过 " + archived.SkippedFiles + " 个。\r\n" + archived.ErrorMessage + "\r\n" + string.Join("\r\n", archived.Details.ToArray()) + "\r\n记录：" + archived.LogPath; installerScan = null; installers.Items.Clear(); });
        }
        private void SavePip()
        {
            if (MessageBox.Show(this, "备份并修改用户 pip.ini：\r\n" + PipDownloadSettings.UserConfigPath + "\r\n[download] dest = " + pipTarget.Text + "\r\n不改缓存、PowerShell 配置或环境变量。", "确认 pip 下载目录", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { pipChange = PipDownloadSettings.Apply(pipTarget.Text); pipStatus.Text = PipDownloadSettings.ReadStatus(); result.Text = "已保存。原配置备份：" + pipChange.BackupPath + "\r\n可撤销本次设置；关闭窗口后可按备份恢复。目录不自动删除。"; SetBusy(false); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private void UndoPip()
        {
            if (MessageBox.Show(this, "恢复本次保存前的 pip.ini？后续修改将阻止自动撤销。", "确认撤销", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try { PipDownloadSettings.Undo(pipChange); pipChange = null; pipStatus.Text = PipDownloadSettings.ReadStatus(); result.Text = "已恢复保存前的配置；备份和下载目录保留。"; SetBusy(false); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "撤销未完成"); }
        }
        private void UndoMove()
        {
            if (journals.Count == 0 || !closed.Checked) { MessageBox.Show(this, "请先退出相关软件，并勾选确认。", Text); return; }
            string journal = journals[journals.Count - 1]; JournalEntry entry = MoveJournal.Read(journal);
            if (entry == null || !Junction.Verify(entry.OldPath, entry.NewPath)) { MessageBox.Show(this, "日记或链接已变化，拒绝自动撤销。", Text); return; }
            if (MessageBox.Show(this, "把当前目标数据搬回原目录？不会使用旧快照覆盖新数据。\r\n" + entry.NewPath + "\r\n→ " + entry.OldPath, "确认撤销最近一项", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            MoveResult undone = null;
            Run(delegate { undone = MoveEngine.Undo(journal, delegate(MoveProgress p) { Report(p.Message); }); }, delegate { result.Text = undone.Success ? "撤销成功，请重新扫描。" : undone.ErrorMessage; if (undone.Success) journals.RemoveAt(journals.Count - 1); scan = null; closed.Checked = false; });
        }
        public void PreviewFillDemoData()
        {
            demo = true; scan = null; installerScan = null; summary.Text = "C:\\ 总容量 200 GB · 已用 176 GB · 可用 24 GB（演示数据，非本机实测）\r\n全程只读；系统项不可勾选，已联接项不重复计算。";
            space.Items.Clear(); data.Items.Clear(); installers.Items.Clear();
            space.Items.Add(new ListViewItem(new string[] { "NVIDIA 着色器缓存", "49.4 GB", "可重建缓存 · 逻辑大小", "建议清理评估，不直接搬驱动缓存", @"C:\Users\Demo\AppData\Local\NVIDIA\DXCache" }));
            space.Items.Add(new ListViewItem(new string[] { "休眠文件", "18.8 GB", "系统文件 / 只读", "仅说明，不直接搬移或删除", @"C:\hiberfil.sys" }));
            space.Items.Add(new ListViewItem(new string[] { "系统还原点 / 卷影副本", "未测量/未完整", "系统管理", "不推测大小，不删除还原点", @"C:\System Volume Information" }));
            data.Items.Add(new ListViewItem(new string[] { "pip 包缓存", "未测量/未完整", "已联接 / 不重复搬", @"C:\Users\Demo\AppData\Local\pip\cache → E:\SoftwareData\Python\pip\cache", @"E:\SoftwareData\Python\pip\cache", "缓存与下载成品分开" }));
            data.Items.Add(new ListViewItem(new string[] { "Codex 插件", "476 MB", "可搬（先退出软件）", @"C:\Users\Demo\.codex\plugins", @"E:\SoftwareData\Codex\plugins", "仅 plugins；不搬认证、会话和运行时" }));
            pipStatus.Text = "演示模式：不读取或修改本机配置。"; result.Text = "演示模式：所有写入按钮关闭，无可执行扫描快照。"; target.Text = @"E:\SoftwareData"; SetBusy(false);
        }
    }
}
