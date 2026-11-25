// C盘清理窗口；C#5 / .NET Framework 4.8。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using YijianNuodong.Core;
using Ctl = YijianNuodong.UI.Controls;

namespace YijianNuodong.UI
{
    public class CleanupDialog : Form
    {
        private enum Page { Selection, Progress, Result }
        private readonly List<CleanupItem> _items = new List<CleanupItem>();
        private readonly Dictionary<string, CleanupItem> _itemsById = new Dictionary<string, CleanupItem>(StringComparer.Ordinal);
        private readonly HashSet<string> _selectedIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _progressLock = new object();
        private readonly System.Windows.Forms.Timer _progressTimer;
        private CleanupScanResult _scan;
        private CleanupResult _result;
        private CleanupProgress _latestProgress;
        private int _progressVersion, _operationVersion;
        private bool _busy, _executing, _scanStarted, _scanConsumed, _previewMode, _rendering;
        private volatile bool _cancelRequested, _closing;
        private Page _page;
        private long _selectedBytes, _installerBytes;
        private int _installerCount;
        private string _logPath;
        private Label _subtitle, _emptyLabel, _selectionSummary, _progressTitle, _progressHint, _progressCount;
        private Label _resultTitle, _resultStats, _resultNote, _footerHint;
        private Panel _body;
        private TableLayoutPanel _selectionPage, _progressPage, _resultPage;
        private ComboBox _filter;
        private ListView _fileList;
        private TextBox _pathBox, _descriptionBox, _warningsBox, _progressMessage, _resultDetails, _logPathBox;
        private ProgressBar _progressBar;
        private Ctl.FlatButton _selectSafeButton, _selectNoneButton, _locationButton, _openLogButton, _logLocationButton;
        private Ctl.FlatButton _scanButton, _cleanButton, _cancelButton, _closeButton;

        public CleanupDialog()
        {
            Text = "C盘清理";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Theme.Body;
            ForeColor = Theme.TextPrimary;
            BackColor = Theme.Background;
            ClientSize = new Size(1040, 760);
            MinimumSize = new Size(960, 710);
            Theme.EnableDoubleBuffer(this);
            BuildInterface();
            _progressTimer = new System.Windows.Forms.Timer();
            _progressTimer.Interval = 150;
            _progressTimer.Tick += delegate { ApplyLatestProgress(); };
            _progressTitle.Text = "准备扫描";
            _progressHint.Text = "窗口打开后开始扫描。扫描只读取文件信息，不会删除文件。";
            _progressMessage.Text = "扫描完成后，你可以查看每个文件，再决定要清理哪些。";
            ShowPage(Page.Progress);
            Shown += delegate { if (!_scanStarted && !_previewMode) StartScan(); };
        }

        /// <summary>最近一次引擎返回的结果；尚未执行或异常中断时为 null。</summary>
        public CleanupResult Result { get { return _result; } }

        private void BuildInterface()
        {
            TableLayoutPanel frame = NewTable(3);
            frame.Padding = new Padding(20, 0, 20, 0);
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            Controls.Add(frame);
            TableLayoutPanel header = NewTable(2);
            header.Padding = new Padding(2, 12, 0, 8);
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.Controls.Add(NewLabel("C盘清理", Theme.H1Small, Theme.TextPrimary), 0, 0);
            _subtitle = NewLabel("先扫描，再逐项确认。只清理你勾选的文件。", Theme.Small, Theme.TextSecondary);
            header.Controls.Add(_subtitle, 0, 1);
            frame.Controls.Add(header, 0, 0);
            _body = new Panel();
            _body.Dock = DockStyle.Fill;
            _body.Margin = new Padding(0);
            _body.BackColor = Theme.Surface;
            frame.Controls.Add(_body, 0, 1);
            BuildSelectionPage();
            BuildProgressPage();
            BuildResultPage();
            TableLayoutPanel footer = NewTable(1);
            footer.ColumnCount = 2;
            footer.ColumnStyles.Clear();
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.Padding = new Padding(0, 12, 0, 12);
            _footerHint = NewLabel(string.Empty, Theme.Small, Theme.TextSecondary);
            footer.Controls.Add(_footerHint, 0, 0);
            FlowLayoutPanel buttons = NewButtonFlow();
            _scanButton = NewButton("重新扫描", 106, Ctl.FlatButtonStyle.Secondary);
            _scanButton.Click += delegate { StartScan(); };
            _cancelButton = NewButton("停止扫描", 106, Ctl.FlatButtonStyle.Secondary);
            _cancelButton.Click += delegate { RequestCancel(); };
            _closeButton = NewButton("关闭", 88, Ctl.FlatButtonStyle.Secondary);
            _closeButton.Click += delegate { Close(); };
            _cleanButton = NewButton("确认清理…", 128, Ctl.FlatButtonStyle.Danger);
            _cleanButton.Click += delegate { ConfirmAndExecute(); };
            buttons.Controls.Add(_scanButton);
            buttons.Controls.Add(_cancelButton);
            buttons.Controls.Add(_closeButton);
            buttons.Controls.Add(_cleanButton);
            footer.Controls.Add(buttons, 1, 0);
            frame.Controls.Add(footer, 0, 2);
            // 清理不设为默认按钮，避免按 Enter 时意外进入确认。
        }
        private void BuildSelectionPage()
        {
            _selectionPage = NewTable(6);
            _selectionPage.BackColor = Theme.Surface;
            _selectionPage.Padding = new Padding(14, 10, 14, 10);
            _selectionPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            _selectionPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            _selectionPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _selectionPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            _selectionPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
            _selectionPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            _body.Controls.Add(_selectionPage);
            Label advice = NewLabel("先关闭浏览器和不需要的程序。下载的安装包默认不勾选，其中可能有便携软件，请看清后再选。", Theme.Small, Theme.TextSecondary);
            advice.BackColor = Theme.WarningLight;
            advice.Padding = new Padding(10, 4, 10, 4);
            _selectionPage.Controls.Add(advice, 0, 0);
            TableLayoutPanel tools = NewTable(1);
            tools.ColumnCount = 5;
            tools.ColumnStyles.Clear();
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tools.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tools.Padding = new Padding(0, 5, 0, 3);
            tools.Controls.Add(NewLabel("分类", Theme.Small, Theme.TextSecondary), 0, 0);
            _filter = new ComboBox();
            _filter.DropDownStyle = ComboBoxStyle.DropDownList;
            _filter.Dock = DockStyle.Fill;
            _filter.Margin = new Padding(0, 3, 10, 0);
            _filter.Items.AddRange(new object[] { "全部分类", "临时文件", "浏览器缓存", "下载的安装包" });
            _filter.SelectedIndex = 0;
            _filter.AccessibleName = "按文件用途分类筛选";
            _filter.SelectedIndexChanged += delegate { if (!_rendering) RenderItems(); };
            tools.Controls.Add(_filter, 1, 0);
            _selectSafeButton = NewButton("仅选临时文件与缓存", 210, Ctl.FlatButtonStyle.Secondary);
            _selectSafeButton.Dock = DockStyle.Fill;
            _selectSafeButton.Click += delegate { SelectSafeItems(); };
            tools.Controls.Add(_selectSafeButton, 2, 0);
            _selectNoneButton = NewButton("取消全选", 106, Ctl.FlatButtonStyle.Secondary);
            _selectNoneButton.Dock = DockStyle.Fill;
            _selectNoneButton.Click += delegate { ClearSelection(); };
            tools.Controls.Add(_selectNoneButton, 3, 0);
            Label filterHint = NewLabel("切换分类会保留勾选", Theme.Small, Theme.TextSecondary);
            filterHint.TextAlign = ContentAlignment.MiddleRight;
            tools.Controls.Add(filterHint, 4, 0);
            _selectionPage.Controls.Add(tools, 0, 1);
            Panel listHost = new Panel();
            listHost.Dock = DockStyle.Fill;
            listHost.Margin = new Padding(0);
            _fileList = new ListView();
            _fileList.Dock = DockStyle.Fill;
            _fileList.View = View.Details;
            _fileList.CheckBoxes = true;
            _fileList.FullRowSelect = true;
            _fileList.MultiSelect = false;
            _fileList.HideSelection = false;
            _fileList.ShowItemToolTips = true;
            _fileList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _fileList.BorderStyle = BorderStyle.FixedSingle;
            _fileList.BackColor = Theme.Surface;
            _fileList.ForeColor = Theme.TextPrimary;
            _fileList.Columns.Add("用途分类", 136);
            _fileList.Columns.Add("文件名", 190);
            _fileList.Columns.Add("大小", 92, HorizontalAlignment.Right);
            _fileList.Columns.Add("修改日期", 146);
            _fileList.Columns.Add("所在目录", 350);
            _fileList.ItemCheck += OnItemCheck;
            _fileList.SelectedIndexChanged += delegate { ShowSelectedItem(); };
            _fileList.DoubleClick += delegate { OpenSelectedLocation(); };
            _fileList.AccessibleName = "可逐项勾选的清理文件列表";
            Theme.EnableDoubleBuffer(_fileList);
            listHost.Controls.Add(_fileList);
            _emptyLabel = NewLabel("还没有扫描结果。", Theme.Body, Theme.TextSecondary);
            _emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            _emptyLabel.BackColor = Theme.Surface;
            _emptyLabel.Padding = new Padding(24);
            listHost.Controls.Add(_emptyLabel);
            _emptyLabel.BringToFront();
            _selectionPage.Controls.Add(listHost, 0, 2);
            _selectionSummary = NewLabel(string.Empty, Theme.SmallBold, Theme.TextPrimary);
            _selectionPage.Controls.Add(_selectionSummary, 0, 3);
            TableLayoutPanel detail = NewTable(2);
            detail.ColumnCount = 2;
            detail.ColumnStyles.Clear();
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _pathBox = NewReadOnlyBox(true, false);
            _pathBox.AccessibleName = "所选文件完整路径，可复制";
            _pathBox.Text = "点选一行可查看完整路径。";
            detail.Controls.Add(_pathBox, 0, 0);
            _locationButton = NewButton("打开位置", 110, Ctl.FlatButtonStyle.Secondary);
            _locationButton.Dock = DockStyle.Fill;
            _locationButton.Margin = new Padding(10, 4, 0, 4);
            _locationButton.Click += delegate { OpenSelectedLocation(); };
            detail.Controls.Add(_locationButton, 1, 0);
            _descriptionBox = NewReadOnlyBox(true, false);
            _descriptionBox.AccessibleName = "所选文件用途说明";
            _descriptionBox.Text = "可先看用途说明，再决定是否勾选。双击文件行也只会打开所在位置。";
            detail.Controls.Add(_descriptionBox, 0, 1);
            detail.SetColumnSpan(_descriptionBox, 2);
            _selectionPage.Controls.Add(detail, 0, 4);
            TableLayoutPanel warnings = NewTable(1);
            warnings.ColumnCount = 2;
            warnings.ColumnStyles.Clear();
            warnings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            warnings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            warnings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            warnings.Padding = new Padding(0, 5, 0, 0);
            warnings.Controls.Add(NewLabel("扫描说明", Theme.Small, Theme.TextSecondary), 0, 0);
            _warningsBox = NewReadOnlyBox(true, false);
            _warningsBox.AccessibleName = "扫描提醒和无法读取的项目";
            warnings.Controls.Add(_warningsBox, 1, 0);
            _selectionPage.Controls.Add(warnings, 0, 5);
        }
        private void BuildProgressPage()
        {
            _progressPage = NewTable(8);
            _progressPage.Padding = new Padding(30, 24, 30, 20);
            _progressPage.BackColor = Theme.Surface;
            _progressPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            _progressPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            _progressPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            _progressPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            _progressPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            _progressPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
            _progressPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            _progressPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _body.Controls.Add(_progressPage);
            _progressTitle = NewLabel(string.Empty, Theme.H2, Theme.TextPrimary);
            _progressHint = NewLabel(string.Empty, Theme.Body, Theme.TextSecondary);
            _progressPage.Controls.Add(_progressTitle, 0, 0);
            _progressPage.Controls.Add(_progressHint, 0, 1);
            _progressBar = new ProgressBar();
            _progressBar.Dock = DockStyle.Fill;
            _progressBar.Margin = new Padding(0, 2, 0, 2);
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 1000;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 25;
            _progressPage.Controls.Add(_progressBar, 0, 2);
            _progressCount = NewLabel("正在准备…", Theme.SmallBold, Theme.TextPrimary);
            _progressPage.Controls.Add(_progressCount, 0, 3);
            _progressPage.Controls.Add(NewLabel("当前处理信息", Theme.SmallBold, Theme.TextSecondary), 0, 4);
            _progressMessage = NewReadOnlyBox(true, false);
            _progressMessage.Font = Theme.Body;
            _progressMessage.AccessibleName = "当前扫描或清理信息";
            _progressPage.Controls.Add(_progressMessage, 0, 5);
            Label stopHint = NewLabel("点击停止后会等待当前步骤结束。清理期间已经删除的文件不能还原，请等结果出现后再关闭窗口。", Theme.Small, Theme.TextSecondary);
            stopHint.Padding = new Padding(0, 8, 0, 0);
            _progressPage.Controls.Add(stopHint, 0, 6);
        }

        private void BuildResultPage()
        {
            _resultPage = NewTable(8);
            _resultPage.Padding = new Padding(22, 16, 22, 12);
            _resultPage.BackColor = Theme.Surface;
            _resultPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            _resultPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            _resultPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            _resultPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            _resultPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _resultPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            _resultPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            _resultPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            _body.Controls.Add(_resultPage);
            _resultTitle = NewLabel(string.Empty, Theme.H2, Theme.TextPrimary);
            _resultStats = NewLabel(string.Empty, Theme.BodyBold, Theme.TextPrimary);
            _resultNote = NewLabel(string.Empty, Theme.Small, Theme.TextSecondary);
            _resultPage.Controls.Add(_resultTitle, 0, 0);
            _resultPage.Controls.Add(_resultStats, 0, 1);
            _resultPage.Controls.Add(_resultNote, 0, 2);
            _resultPage.Controls.Add(NewLabel("处理记录（可选中复制）", Theme.SmallBold, Theme.TextSecondary), 0, 3);
            _resultDetails = NewReadOnlyBox(true, true);
            _resultDetails.Font = Theme.Mono;
            _resultDetails.AccessibleName = "清理结果和逐项处理记录";
            _resultPage.Controls.Add(_resultDetails, 0, 4);
            _resultPage.Controls.Add(NewLabel("本次日志位置", Theme.Small, Theme.TextSecondary), 0, 5);
            _logPathBox = NewReadOnlyBox(false, false);
            _logPathBox.AccessibleName = "清理日志完整路径";
            _resultPage.Controls.Add(_logPathBox, 0, 6);
            FlowLayoutPanel logButtons = NewButtonFlow();
            logButtons.Padding = new Padding(0, 4, 0, 0);
            _openLogButton = NewButton("查看日志", 106, Ctl.FlatButtonStyle.Secondary);
            _openLogButton.Click += delegate { OpenLog(); };
            _logLocationButton = NewButton("打开日志位置", 128, Ctl.FlatButtonStyle.Secondary);
            _logLocationButton.Click += delegate { OpenPathLocation(_logPath); };
            logButtons.Controls.Add(_openLogButton);
            logButtons.Controls.Add(_logLocationButton);
            _resultPage.Controls.Add(logButtons, 0, 7);
        }

        private static TableLayoutPanel NewTable(int rows)
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.Margin = new Padding(0);
            table.ColumnCount = 1;
            table.RowCount = rows;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.BackColor = Theme.Background;
            return table;
        }
        private static Label NewLabel(string text, Font font, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = font;
            label.ForeColor = color;
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.UseMnemonic = false;
            return label;
        }
        private static TextBox NewReadOnlyBox(bool multiline, bool horizontalScroll)
        {
            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 2, 0, 2);
            box.ReadOnly = true;
            box.Multiline = multiline;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Theme.OptionBackground;
            box.ForeColor = Theme.TextSecondary;
            box.Font = Theme.Small;
            box.WordWrap = !horizontalScroll;
            if (multiline) box.ScrollBars = horizontalScroll ? ScrollBars.Both : ScrollBars.Vertical;
            return box;
        }
        private static Ctl.FlatButton NewButton(string text, int width, Ctl.FlatButtonStyle style)
        {
            Ctl.FlatButton button = new Ctl.FlatButton();
            button.Text = text;
            button.Style = style;
            button.Size = new Size(width, 36);
            button.Margin = new Padding(0, 0, 8, 0);
            return button;
        }
        private static FlowLayoutPanel NewButtonFlow()
        {
            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.AutoSize = true;
            flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.WrapContents = false;
            flow.Margin = new Padding(0);
            return flow;
        }
        private void ShowPage(Page page)
        {
            _page = page;
            _selectionPage.Visible = page == Page.Selection;
            _progressPage.Visible = page == Page.Progress;
            _resultPage.Visible = page == Page.Result;
            if (page == Page.Selection) _selectionPage.BringToFront();
            else if (page == Page.Progress) _progressPage.BringToFront();
            else _resultPage.BringToFront();
            UpdateActions();
        }
        private bool CanChooseItems()
        {
            return !_busy && _scan != null && !_scan.Canceled && !_scanConsumed;
        }
        private void UpdateActions()
        {
            bool canChoose = CanChooseItems();
            _filter.Enabled = !_busy && _scan != null;
            _fileList.Enabled = !_busy && _scan != null;
            _selectSafeButton.Enabled = canChoose && _items.Count > 0;
            _selectNoneButton.Enabled = canChoose && _selectedIds.Count > 0;
            _scanButton.Visible = !_busy && _page != Page.Progress;
            _scanButton.Enabled = !_busy && !_previewMode;
            _cleanButton.Visible = _page == Page.Selection;
            _cleanButton.Enabled = canChoose && !_previewMode && _selectedIds.Count > 0;
            _cleanButton.Text = _previewMode ? "演示不可清理" : "确认清理…";
            _cancelButton.Visible = _busy;
            _cancelButton.Enabled = _busy && !_cancelRequested;
            _cancelButton.Text = _executing ? "停止清理" : "停止扫描";
            _closeButton.Enabled = !_busy;
            _locationButton.Enabled = !_busy && !_previewMode && SelectedItem() != null;
            bool hasLog = !_busy && !_previewMode && !string.IsNullOrEmpty(_logPath);
            _openLogButton.Enabled = hasLog;
            _logLocationButton.Enabled = hasLog;
        }
        private void StartScan()
        {
            if (_busy || _previewMode || _closing || IsDisposed) return;
            _scanStarted = true;
            _scan = null;
            _result = null;
            _scanConsumed = false;
            _logPath = null;
            _items.Clear();
            _itemsById.Clear();
            _selectedIds.Clear();
            RecountSelection();
            int version = BeginOperation(false);
            _subtitle.Text = "正在列出可清理文件。扫描只读取信息，不会删除文件。";
            try
            {
                bool queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    CleanupScanResult scan = null;
                    Exception failure = null;
                    try
                    {
                        scan = CleanupEngine.Scan(
                            delegate(CleanupProgress progress) { CaptureProgress(version, progress); },
                            delegate { return _cancelRequested || _closing; });
                    }
                    catch (Exception ex) { failure = ex; }
                    PostCompletion(version, delegate { CompleteScan(scan, failure); });
                });
                if (!queued) throw new InvalidOperationException("无法启动后台扫描。");
            }
            catch (Exception ex) { CompleteScan(null, ex); }
        }
        private int BeginOperation(bool executing)
        {
            _busy = true;
            _executing = executing;
            _cancelRequested = false;
            _operationVersion++;
            lock (_progressLock)
            {
                _progressVersion = _operationVersion;
                _latestProgress = null;
            }
            _progressTitle.Text = executing ? "正在清理勾选的文件" : "正在扫描可清理文件";
            _progressHint.Text = executing
                ? "仅处理本次确认的文件。可随时请求停止，已删除的文件不能还原。"
                : "请稍等。扫描完成后可以查看文件、取消勾选或打开所在位置。";
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.Value = 0;
            _progressCount.Text = executing ? "准备处理 " + _selectedIds.Count + " 个文件…" : "正在查找文件…";
            _progressMessage.Text = executing ? "正在准备清理…" : "正在准备扫描…";
            _footerHint.Text = executing ? "停止后，请等待当前文件处理结束。" : "扫描不会删除文件。";
            _footerHint.ForeColor = Theme.TextSecondary;
            ShowPage(Page.Progress);
            _progressTimer.Start();
            return _operationVersion;
        }
        private void EndOperation()
        {
            _progressTimer.Stop();
            ApplyLatestProgress();
            lock (_progressLock) { _progressVersion = -1; _latestProgress = null; }
            _busy = false;
        }
        private void CompleteScan(CleanupScanResult scan, Exception failure)
        {
            bool stopped = _cancelRequested;
            EndOperation();
            if (failure != null || scan == null) { ShowFailure("扫描没有完成", failure, false); return; }
            LoadScan(scan);
            // 取消与扫描返回恰好同时发生时，也要求重新扫描后再清理。
            if (stopped && !scan.Canceled)
            {
                _scanConsumed = true;
                _selectedIds.Clear();
                RecountSelection();
                RenderItems();
                _warningsBox.AppendText(Environment.NewLine + "你已停止这次扫描，请重新扫描后再清理。");
                _footerHint.Text = "扫描已停止，请重新扫描。";
                _footerHint.ForeColor = Theme.Warning;
                UpdateActions();
            }
        }
        private void LoadScan(CleanupScanResult scan)
        {
            _scan = scan;
            _scanConsumed = false;
            _items.Clear();
            _itemsById.Clear();
            _selectedIds.Clear();
            foreach (CleanupItem item in scan.Items)
            {
                if (item == null || string.IsNullOrEmpty(item.Id) || _itemsById.ContainsKey(item.Id)) continue;
                _items.Add(item);
                _itemsById.Add(item.Id, item);
                // 安装包永不自动勾选。其他项目尊重扫描结果的默认选择。
                if (!scan.Canceled && item.DefaultSelected && item.Category != CleanupCategory.DownloadedInstallers)
                    _selectedIds.Add(item.Id);
            }
            _rendering = true;
            try { _filter.SelectedIndex = 0; }
            finally { _rendering = false; }
            RecountSelection();
            RenderItems();
            StringBuilder warnings = new StringBuilder();
            if (_previewMode) warnings.AppendLine("演示数据不对应本机文件。这里会显示真实扫描中无法读取的目录等提醒。");
            else warnings.AppendLine("扫描位置：" + (string.IsNullOrEmpty(scan.DriveLabel) ? "C盘" : scan.DriveLabel) + "；已检查 " + scan.RootCount + " 个清理位置。");
            if (scan.Canceled) warnings.AppendLine("扫描已停止，下面可能只有部分文件。请重新扫描后再选择清理。");
            if (scan.LimitReached) warnings.AppendLine("找到的文件较多，本次扫描达到数量上限。这里只列出已找到的文件，之后可重新扫描继续查看。");
            foreach (string warning in scan.Warnings) if (!string.IsNullOrEmpty(warning)) warnings.AppendLine(warning);
            _warningsBox.Text = warnings.ToString().TrimEnd();
            _warningsBox.Select(0, 0);
            _subtitle.Text = _previewMode ? "演示：只展示界面，不扫描、不清理，也不打开演示路径。"
                : "扫描位置：" + (string.IsNullOrEmpty(scan.DriveLabel) ? "C盘" : scan.DriveLabel) + "。请逐项确认，切换分类会保留勾选。";
            _footerHint.Text = _previewMode ? "演示模式：不会执行清理。" : scan.Canceled ? "扫描已停止，请重新扫描。" : "未勾选的文件会保留。清理前还会再次确认。";
            _footerHint.ForeColor = scan.Canceled ? Theme.Warning : Theme.TextSecondary;
            ShowPage(Page.Selection);
        }
        private bool MatchesFilter(CleanupItem item)
        {
            if (_filter.SelectedIndex == 1) return item.Category == CleanupCategory.TemporaryFiles;
            if (_filter.SelectedIndex == 2) return item.Category == CleanupCategory.BrowserCache;
            if (_filter.SelectedIndex == 3) return item.Category == CleanupCategory.DownloadedInstallers;
            return true;
        }
        private void RenderItems()
        {
            CleanupItem previous = SelectedItem();
            string focusId = previous == null ? null : previous.Id;
            ListViewItem focusRow = null;
            _rendering = true;
            _fileList.BeginUpdate();
            try
            {
                _fileList.Items.Clear();
                foreach (CleanupItem item in _items)
                {
                    if (!MatchesFilter(item)) continue;
                    ListViewItem row = new ListViewItem(CategoryText(item.Category));
                    row.SubItems.Add(string.IsNullOrEmpty(item.DisplayName) ? SafeFileName(item.FullPath) : item.DisplayName);
                    row.SubItems.Add(FormatBytes(item.SizeBytes));
                    row.SubItems.Add(item.LastWriteTimeUtc == DateTime.MinValue ? "未知" : item.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                    row.SubItems.Add(SafeDirectory(item));
                    row.Tag = item;
                    row.ToolTipText = (item.FullPath ?? string.Empty) + Environment.NewLine + (item.Description ?? string.Empty);
                    row.Checked = _selectedIds.Contains(item.Id);
                    _fileList.Items.Add(row);
                    if (item.Id == focusId) focusRow = row;
                }
                if (focusRow == null && _fileList.Items.Count > 0) focusRow = _fileList.Items[0];
                if (focusRow != null) { focusRow.Selected = true; focusRow.Focused = true; }
            }
            finally { _fileList.EndUpdate(); _rendering = false; }
            _emptyLabel.Visible = _fileList.Items.Count == 0;
            _emptyLabel.Text = _items.Count == 0 ? "本次没有找到可列出的文件。\r\n可以查看下方扫描说明，或稍后重新扫描。"
                : "这个分类没有文件。\r\n切换分类可查看其他项目，原有勾选会保留。";
            if (_emptyLabel.Visible) _emptyLabel.BringToFront();
            ShowSelectedItem();
            UpdateSelectionSummary();
            UpdateActions();
        }
        private void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            // ListView 的勾选通知可能在批量填充后才回调；此时先忽略，避免半初始化控件触发空引用。
            if (_rendering || _fileList == null || _selectionSummary == null || _items == null || _selectedIds == null) return;
            if (!CanChooseItems() || e.Index < 0 || e.Index >= _fileList.Items.Count) { e.NewValue = e.CurrentValue; return; }
            CleanupItem item = _fileList.Items[e.Index].Tag as CleanupItem;
            if (item == null) { e.NewValue = e.CurrentValue; return; }
            if (e.NewValue == CheckState.Checked) _selectedIds.Add(item.Id);
            else _selectedIds.Remove(item.Id);
            RecountSelection();
            UpdateSelectionSummary();
            UpdateActions();
        }
        private void SelectSafeItems()
        {
            if (!CanChooseItems()) return;
            _selectedIds.Clear();
            foreach (CleanupItem item in _items)
                if (item.Category == CleanupCategory.TemporaryFiles || item.Category == CleanupCategory.BrowserCache) _selectedIds.Add(item.Id);
            RecountSelection();
            RenderItems();
        }
        private void ClearSelection()
        {
            if (!CanChooseItems()) return;
            _selectedIds.Clear();
            RecountSelection();
            RenderItems();
        }
        private void RecountSelection()
        {
            _selectedBytes = 0; _installerCount = 0; _installerBytes = 0;
            foreach (string id in _selectedIds)
            {
                CleanupItem item;
                if (!_itemsById.TryGetValue(id, out item)) continue;
                _selectedBytes = AddBytes(_selectedBytes, item.SizeBytes);
                if (item.Category == CleanupCategory.DownloadedInstallers) { _installerCount++; _installerBytes = AddBytes(_installerBytes, item.SizeBytes); }
            }
        }
        private void UpdateSelectionSummary()
        {
            ListView list = _fileList;
            Label summary = _selectionSummary;
            List<CleanupItem> items = _items;
            HashSet<string> selected = _selectedIds;
            if (list == null || summary == null || items == null || selected == null || list.IsDisposed || summary.IsDisposed) return;
            int visibleSelected = 0;
            ListView.ListViewItemCollection rows = list.Items;
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    ListViewItem row = rows[i];
                    if (row == null) continue;
                    CleanupItem item = row.Tag as CleanupItem;
                    if (item != null && selected.Contains(item.Id)) visibleSelected++;
                }
            }
            int selectedCount = selected.Count;
            int hiddenSelected = selectedCount - visibleSelected;
            string text = "全部分类共勾选 " + selectedCount + " 个文件，合计 " + FormatBytes(_selectedBytes)
                + "；安装包 " + _installerCount + " 个（" + FormatBytes(_installerBytes) + "）"
                + "  |  当前显示 " + (rows == null ? 0 : rows.Count) + " / " + items.Count + " 个"
                + (hiddenSelected > 0 ? "，另有 " + hiddenSelected + " 个已勾选文件在其他分类" : string.Empty);
            summary.Text = text;
        }
        private CleanupItem SelectedItem()
        {
            if (_fileList == null || _fileList.SelectedItems.Count == 0) return null;
            return _fileList.SelectedItems[0].Tag as CleanupItem;
        }
        private void ShowSelectedItem()
        {
            if (_rendering) return;
            CleanupItem item = SelectedItem();
            _pathBox.Text = item == null ? "点选一行可查看完整路径。" : item.FullPath ?? string.Empty;
            _pathBox.Select(0, 0);
            _descriptionBox.Text = item == null ? "可先看用途说明，再决定是否勾选。双击文件行也只会打开所在位置。"
                : CategoryText(item.Category) + "：" + (string.IsNullOrEmpty(item.Description) ? "请先确认用途，再决定是否清理。" : item.Description)
                + (item.Category == CleanupCategory.DownloadedInstallers ? "\r\n这里可能包含便携软件。若仍在使用，或不确定用途，请取消勾选。" : string.Empty);
            _descriptionBox.Select(0, 0);
            _locationButton.Enabled = item != null && !_busy && !_previewMode;
        }
        private void ConfirmAndExecute()
        {
            if (!CanChooseItems() || _previewMode || _selectedIds.Count == 0) return;
            RecountSelection();
            List<string> ids = new List<string>();
            foreach (CleanupItem item in _items) if (_selectedIds.Contains(item.Id)) ids.Add(item.Id);
            if (ids.Count == 0) return;
            bool installersConfirmed = false;
            if (_installerCount > 0)
            {
                string warning = "你勾选了 " + _installerCount + " 个下载的安装包，合计 " + FormatBytes(_installerBytes)
                    + "。\r\n\r\n其中可能包含解压后直接使用的便携软件，也可能是以后还需要的安装文件。\r\n"
                    + "如果不确定，请点“否”返回查看路径和用途。\r\n\r\n你已确认这些安装包和便携软件都不再需要，可以清理吗？";
                installersConfirmed = MessageBox.Show(this, warning, "再次确认安装包", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                if (!installersConfirmed) return;
            }
            string confirmation = "本次将清理 " + ids.Count + " 个文件，合计 " + FormatBytes(_selectedBytes)
                + "。\r\n其中下载的安装包：" + _installerCount + " 个，" + FormatBytes(_installerBytes) + "。"
                + "\r\n总数包含所有分类中已勾选的文件。\r\n\r\n这些文件会永久删除，不进回收站，无法撤销。\r\n"
                + "清理时可以请求停止，但已经删除的文件不能还原。"
                + (_installerCount > 0 ? "\r\n已确认的安装包也会一并永久删除，请确认没有仍在使用的便携软件。" : string.Empty)
                + "\r\n\r\n确认现在清理这些文件吗？";
            if (MessageBox.Show(this, confirmation, "确认永久删除", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            CleanupRequest request = new CleanupRequest();
            request.Scan = _scan;
            request.SelectedIds = ids;
            request.ConfirmPermanentDelete = true;
            request.ConfirmInstallers = installersConfirmed;
            // 同一份扫描只提交一次；异常中断后也要求重新扫描。
            _scanConsumed = true;
            _result = null;
            int version = BeginOperation(true);
            _subtitle.Text = "正在永久删除已确认的文件。已删除的文件无法撤销。";
            try
            {
                bool queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    CleanupResult result = null;
                    Exception failure = null;
                    try
                    {
                        result = CleanupEngine.Execute(request,
                            delegate(CleanupProgress progress) { CaptureProgress(version, progress); },
                            delegate { return _cancelRequested || _closing; });
                    }
                    catch (Exception ex) { failure = ex; }
                    PostCompletion(version, delegate { CompleteExecution(result, failure); });
                });
                if (!queued) throw new InvalidOperationException("无法启动后台清理。");
            }
            catch (Exception ex) { CompleteExecution(null, ex); }
        }
        private void CompleteExecution(CleanupResult result, Exception failure)
        {
            EndOperation();
            _selectedIds.Clear();
            RecountSelection();
            if (failure != null || result == null) { ShowFailure("清理意外停止", failure, true); return; }
            _result = result;
            _logPath = result.LogPath;
            bool hasError = !string.IsNullOrEmpty(result.ErrorMessage);
            _resultTitle.Text = result.Canceled ? "清理已停止" : hasError ? "清理未全部完成" : "本次清理已结束";
            _resultTitle.ForeColor = result.Canceled || hasError || result.SkippedFiles > 0 ? Theme.Warning : Theme.Success;
            _resultStats.Text = "已清理 " + result.DeletedFiles + " 个文件，文件逻辑大小合计 " + FormatBytes(result.DeletedBytes)
                + "。\r\n跳过 " + result.SkippedFiles + " 个文件。";
            _resultNote.Text = "字节数按被删除文件的逻辑大小统计。磁盘剩余空间可能不会马上增加，也不一定与这个数值相同。";
            StringBuilder details = new StringBuilder();
            if (hasError) details.AppendLine("提醒：" + result.ErrorMessage).AppendLine();
            if (result.Canceled) details.AppendLine("本次清理已停止。已经删除的文件不能还原，尚未处理的文件会保留。").AppendLine();
            foreach (string detail in result.Details) if (!string.IsNullOrEmpty(detail)) details.AppendLine(detail);
            if (details.Length == 0) details.AppendLine("没有额外处理记录。可以查看本次日志，或重新扫描确认剩余文件。");
            _resultDetails.Text = details.ToString().TrimEnd();
            _resultDetails.Select(0, 0);
            _logPathBox.Text = string.IsNullOrEmpty(_logPath) ? "本次没有可用的日志路径。" : _logPath;
            _logPathBox.Select(0, 0);
            _subtitle.Text = "请查看处理结果和跳过原因。需要继续清理时，请重新扫描。";
            _footerHint.Text = result.Canceled ? "已停止。可查看日志或重新扫描。" : "可查看日志，或重新扫描剩余文件。";
            _footerHint.ForeColor = Theme.TextSecondary;
            ShowPage(Page.Result);
        }
        private void ShowFailure(string title, Exception failure, bool mayHaveDeleted)
        {
            _scanConsumed = true; _result = null; _logPath = null;
            _resultTitle.Text = title;
            _resultTitle.ForeColor = Theme.Danger;
            _resultStats.Text = mayHaveDeleted ? "没有取得完整清理结果，无法确认已清理和跳过的数量。\r\n可能已有部分文件被删除，请重新扫描核对。"
                : "本次没有开始清理文件。请查看原因后重新扫描。";
            _resultNote.Text = mayHaveDeleted ? "已经删除的文件不能还原。结果不完整时，不把清理数量显示为零，也不估算磁盘增加的空间。"
                : "扫描只读取文件信息。若是权限或文件占用问题，可关闭相关程序后再试。";
            _resultDetails.Text = failure == null ? "操作没有返回有效结果。请重新扫描后重试。"
                : title + "：" + failure.GetType().Name + "\r\n" + failure.Message;
            _resultDetails.Select(0, 0);
            _logPathBox.Text = "未取得日志路径。";
            _subtitle.Text = "操作没有正常完成，请查看原因。";
            _footerHint.Text = "可以关闭窗口，或重新扫描。";
            _footerHint.ForeColor = Theme.Warning;
            ShowPage(Page.Result);
        }

        // 后台回调只保存快照，UI定时读取，避免逐文件排队窗口消息。
        private void CaptureProgress(int version, CleanupProgress progress)
        {
            if (_closing || progress == null) return;
            lock (_progressLock)
            {
                if (_progressVersion != version) return;
                _latestProgress = new CleanupProgress { Message = progress.Message, Processed = progress.Processed, Total = progress.Total, Bytes = progress.Bytes };
            }
        }
        private void ApplyLatestProgress()
        {
            if (_closing || IsDisposed || Disposing) return;
            CleanupProgress progress;
            lock (_progressLock) { progress = _latestProgress; _latestProgress = null; }
            if (progress == null || !_busy) return;
            if (!string.IsNullOrEmpty(progress.Message)) _progressMessage.Text = progress.Message;
            int processed = Math.Max(0, progress.Processed);
            int total = Math.Max(0, progress.Total);
            if (total > 0)
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
                double fraction = Math.Min(1.0, (double)processed / total);
                _progressBar.Value = Math.Max(0, Math.Min(1000, (int)(fraction * 1000)));
                _progressCount.Text = "已处理 " + processed + " / " + total + " 个文件";
            }
            else { _progressBar.Style = ProgressBarStyle.Marquee; _progressCount.Text = "已查看 " + processed + " 个项目"; }
            if (!_executing && progress.Bytes > 0) _progressCount.Text += "，已找到文件合计 " + FormatBytes(progress.Bytes);
            if (_cancelRequested) _progressCount.Text += "；已请求停止，正在等待当前步骤结束…";
        }
        private void PostCompletion(int version, Action completion)
        {
            if (_closing || IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (_closing || IsDisposed || Disposing || version != _operationVersion || !_busy) return;
                    try { completion(); }
                    catch (Exception ex)
                    {
                        bool executing = _executing;
                        EndOperation();
                        ShowFailure(executing ? "无法显示完整清理结果" : "无法显示扫描结果", ex, executing);
                    }
                }));
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        private void RequestCancel()
        {
            if (!_busy || _cancelRequested) return;
            _cancelRequested = true;
            _progressHint.Text = _executing ? "已请求停止，正在等当前文件处理结束。已经删除的文件无法恢复。"
                : "已请求停止扫描，正在等当前步骤结束。扫描不会删除文件。";
            _footerHint.Text = "正在停止，请等结果出现后再关闭。";
            _footerHint.ForeColor = Theme.Warning;
            UpdateActions();
        }
        private void OpenSelectedLocation()
        {
            if (_busy || _previewMode) return;
            CleanupItem item = SelectedItem();
            if (item != null) OpenPathLocation(item.FullPath);
        }
        private void OpenPathLocation(string path)
        {
            if (_busy || _previewMode) return;
            try
            {
                ValidateOpenPath(path);
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, "文件已不存在或暂时无法访问。请重新扫描，或复制完整路径手动查看。", "无法打开位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                start.Arguments = "/select,\"" + path + "\"";
                start.UseShellExecute = false;
                Process launched = Process.Start(start);
                if (launched != null) launched.Dispose();
            }
            catch (Exception ex) { MessageBox.Show(this, "无法打开所在位置：" + ex.Message + "\r\n可以复制完整路径后手动查看。", "打开位置失败", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        private void OpenLog()
        {
            if (_busy || _previewMode || string.IsNullOrEmpty(_logPath)) return;
            try
            {
                ValidateOpenPath(_logPath);
                if (!File.Exists(_logPath))
                {
                    MessageBox.Show(this, "日志已不存在或暂时无法读取。窗口中的处理记录仍可选中复制。", "无法读取日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // 指定文本查看器，不按扩展名执行日志文件。
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
                start.Arguments = "\"" + _logPath + "\"";
                start.UseShellExecute = false;
                Process launched = Process.Start(start);
                if (launched != null) launched.Dispose();
            }
            catch (Exception ex) { MessageBox.Show(this, "无法打开日志：" + ex.Message + "\r\n可以复制日志路径或窗口中的处理记录。", "查看日志失败", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        private static void ValidateOpenPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || path.IndexOfAny(new char[] { '"', '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("文件路径无效。");
        }
        private static string CategoryText(CleanupCategory category)
        {
            if (category == CleanupCategory.TemporaryFiles) return "临时文件";
            if (category == CleanupCategory.BrowserCache) return "浏览器缓存";
            if (category == CleanupCategory.DownloadedInstallers) return "下载的安装包";
            return "其他文件";
        }
        private static string SafeFileName(string path)
        {
            try { return string.IsNullOrEmpty(path) ? "未命名文件" : Path.GetFileName(path); }
            catch (Exception) { return path ?? "未命名文件"; }
        }
        private static string SafeDirectory(CleanupItem item)
        {
            try { return Path.GetDirectoryName(item.FullPath) ?? item.RootPath ?? string.Empty; }
            catch (Exception) { return item.RootPath ?? item.FullPath ?? string.Empty; }
        }
        private static long AddBytes(long current, long bytes)
        {
            if (bytes <= 0) return current;
            return current > long.MaxValue - bytes ? long.MaxValue : current + bytes;
        }
        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.##") + " " + units[unit];
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_busy) { e.Cancel = true; RequestCancel(); return; }
            if (!e.Cancel) { _closing = true; _cancelRequested = true; _progressTimer.Stop(); }
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closing = true; _cancelRequested = true;
            _progressTimer.Stop();
            base.OnFormClosed(e);
        }
        protected override void Dispose(bool disposing)
        {
            _closing = true; _cancelRequested = true;
            if (disposing && _progressTimer != null) _progressTimer.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>安全界面预览，应在显示窗口前调用；忙碌时忽略。示例只进 Items，Candidates 保持为空。</summary>
        public void PreviewFillDemoData()
        {
            if (_busy || _closing || IsDisposed) return;
            _previewMode = true; _scanStarted = true; _result = null; _logPath = null;
            CleanupScanResult demo = new CleanupScanResult();
            demo.DriveLabel = "C盘";
            demo.RootCount = 7;
            AddDemoItem(demo, "demo-temp-1", @"C:\CleanupPreview\Temp\setup-work.tmp", CleanupCategory.TemporaryFiles,
                38L * 1024 * 1024, "程序留下的临时工作文件。", true);
            AddDemoItem(demo, "demo-temp-2", @"C:\CleanupPreview\Temp\old-session.tmp", CleanupCategory.TemporaryFiles,
                12L * 1024 * 1024, "较早的临时会话文件，清理前请关闭相关程序。", true);
            AddDemoItem(demo, "demo-cache-1", @"C:\CleanupPreview\Browser\Cache\data_001", CleanupCategory.BrowserCache,
                96L * 1024 * 1024, "浏览器缓存。之后浏览网页时可能需要重新下载。", true);
            AddDemoItem(demo, "demo-cache-2", @"C:\CleanupPreview\Browser\Cache\data_002", CleanupCategory.BrowserCache,
                42L * 1024 * 1024, "网页图片与资源缓存，不是浏览器书签或密码。", true);
            AddDemoItem(demo, "demo-setup-1", @"C:\CleanupPreview\Downloads\ExampleApp-Setup.exe", CleanupCategory.DownloadedInstallers,
                185L * 1024 * 1024, "下载的安装程序。若准备留作重装，请保留。", false);
            AddDemoItem(demo, "demo-setup-2", @"C:\CleanupPreview\Downloads\ExampleTool.msi", CleanupCategory.DownloadedInstallers,
                62L * 1024 * 1024, "下载的安装包。也请留意是否仍需使用。", false);
            LoadScan(demo);
        }
        private static void AddDemoItem(CleanupScanResult scan, string id, string path, CleanupCategory category,
            long bytes, string description, bool selected)
        {
            CleanupItem item = new CleanupItem();
            item.Id = id;
            item.FullPath = path;
            item.RootPath = Path.GetDirectoryName(path);
            item.DisplayName = Path.GetFileName(path);
            item.Category = category;
            item.SizeBytes = bytes;
            item.LastWriteTimeUtc = new DateTime(2026, 10, 12, 9, 30, 0, DateTimeKind.Utc);
            item.Description = description;
            item.DefaultSelected = selected;
            scan.MutableItems.Add(item);
        }
    }
}
