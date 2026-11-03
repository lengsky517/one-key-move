// T3 拥有：主窗口 - 第 1 步「选择软件」界面与逻辑
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using YijianNuodong.Core;
using Ctl = YijianNuodong.UI.Controls;

namespace YijianNuodong.UI
{
    public partial class MainForm
    {
        private const string SearchPlaceholder = "输入软件名称或常用叫法，例如：微信、QQ、PS、剪映";

        private Ctl.DropZonePanel _dropZone;
        private Ctl.BorderedPanel _searchPanel;
        private TextBox _searchBox;
        private Ctl.FlatButton _searchButton;
        private Ctl.FlatCheckBox _onlyCDrive;
        private Label _statusLabel;
        private Ctl.FlatButton _refreshLink;
        private Ctl.BorderedPanel _listHost;
        private ListView _programList;
        private Label _emptyLabel;
        private Ctl.SelectionSummary _selectionSummary;
        private Ctl.FlatButton _nextButton;
        private ImageList _rowIcons;

        private SearchIndex _index;
        private List<InstalledProgram> _visiblePrograms = new List<InstalledProgram>();
        private bool _scanning;
        private Thread _scanThread;
        private int _sizeVersion;
        private long _scanElapsedMs = -1;
        private bool _scanFailed;
        private string _scanErrorText = string.Empty;
        private bool _searchPlaceholderActive;
        private bool _selectingProgram;

        // ------------------------------------------------------------ 界面构建

        private void BuildPage1()
        {
            _page1 = new Panel();
            _page1.Dock = DockStyle.Fill;
            _page1.BackColor = Theme.Surface;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 9;
            layout.BackColor = Theme.Surface;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

            // 拖放区
            _dropZone = new Ctl.DropZonePanel();
            _dropZone.Dock = DockStyle.Fill;
            _dropZone.Margin = new Padding(0);
            _dropZone.PathDropped += HandleDroppedPath;
            _dropZone.BrowseRequested += OnBrowseShortcut;
            layout.Controls.Add(_dropZone, 0, 0);

            // 搜索行
            _searchPanel = new Ctl.BorderedPanel();
            _searchPanel.Dock = DockStyle.Fill;
            _searchPanel.Margin = new Padding(0);
            _searchPanel.CornerRadius = 10;
            _searchPanel.ShowSearchGlyph = true;
            _searchPanel.FillColor = Theme.Surface;
            _searchPanel.BackdropColor = Theme.Surface;
            _searchPanel.BorderColor = Theme.Border;
            layout.Controls.Add(_searchPanel, 0, 2);

            _searchBox = new TextBox();
            _searchBox.BorderStyle = BorderStyle.None;
            _searchBox.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
            _searchBox.ForeColor = Theme.TextMuted;
            _searchBox.BackColor = Theme.Surface;
            _searchBox.Text = SearchPlaceholder;
            _searchPlaceholderActive = true;
            _searchBox.MouseDown += delegate { ClearSearchPlaceholder(); };
            _searchBox.GotFocus += delegate
            {
                if (!_searchPlaceholderActive) return;
                try
                {
                    _searchBox.SelectionStart = 0;
                    _searchBox.SelectionLength = 0;
                }
                catch { }
            };
            _searchBox.LostFocus += SearchBox_LostFocus;
            _searchBox.TextChanged += delegate
            {
                if (_searchPlaceholderActive && _searchBox.Text != SearchPlaceholder)
                {
                    _searchPlaceholderActive = false;
                    _searchBox.ForeColor = Theme.TextPrimary;
                }
            };
            _searchBox.KeyDown += SearchBox_KeyDown;
            _searchPanel.Controls.Add(_searchBox);

            _searchButton = new Ctl.FlatButton();
            _searchButton.Text = "搜索";
            _searchButton.Style = Ctl.FlatButtonStyle.Primary;
            _searchButton.Font = Theme.BodyBold;
            _searchButton.Width = 96;
            _searchButton.Height = 32;
            _searchButton.BackdropColor = Theme.Surface;
            _searchButton.Click += delegate { DoSearch(); };
            _searchPanel.Controls.Add(_searchButton);
            _searchPanel.Resize += delegate { LayoutSearchRow(); };

            // 过滤行
            TableLayoutPanel filterRow = new TableLayoutPanel();
            filterRow.Dock = DockStyle.Fill;
            filterRow.Margin = new Padding(0);
            filterRow.ColumnCount = 3;
            filterRow.RowCount = 1;
            filterRow.BackColor = Theme.Surface;
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            filterRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _onlyCDrive = new Ctl.FlatCheckBox();
            _onlyCDrive.Text = "只看 C 盘软件";
            _onlyCDrive.Dock = DockStyle.Fill;
            _onlyCDrive.Margin = new Padding(0);
            _onlyCDrive.Font = Theme.Body;
            _onlyCDrive.BackdropColor = Theme.Surface;
            _onlyCDrive.Checked = true;
            _onlyCDrive.CheckedChanged += delegate { RefreshProgramList(); };
            filterRow.Controls.Add(_onlyCDrive, 0, 0);

            _statusLabel = new Label();
            _statusLabel.AutoSize = false;
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.Margin = new Padding(0);
            _statusLabel.Font = Theme.Small;
            _statusLabel.ForeColor = Theme.TextSecondary;
            _statusLabel.BackColor = Theme.Surface;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.AutoEllipsis = true;
            _statusLabel.Text = "正在准备…";
            filterRow.Controls.Add(_statusLabel, 1, 0);

            _refreshLink = new Ctl.FlatButton();
            _refreshLink.Text = "刷新";
            _refreshLink.Style = Ctl.FlatButtonStyle.Link;
            _refreshLink.Font = Theme.SmallBold;
            _refreshLink.Dock = DockStyle.Fill;
            _refreshLink.Margin = new Padding(0);
            _refreshLink.BackdropColor = Theme.Surface;
            _refreshLink.Click += delegate { StartScan(); };
            filterRow.Controls.Add(_refreshLink, 2, 0);
            layout.Controls.Add(filterRow, 0, 4);

            // 程序列表
            _listHost = new Ctl.BorderedPanel();
            _listHost.Dock = DockStyle.Fill;
            _listHost.Margin = new Padding(0);
            _listHost.CornerRadius = 10;
            _listHost.FillColor = Theme.Surface;
            _listHost.BackdropColor = Theme.Surface;
            _listHost.BorderColor = Theme.Border;
            _listHost.Padding = new Padding(1);
            layout.Controls.Add(_listHost, 0, 6);

            _programList = new ListView();
            _programList.Dock = DockStyle.Fill;
            _programList.View = View.Details;
            _programList.FullRowSelect = true;
            _programList.MultiSelect = false;
            _programList.HideSelection = false;
            _programList.GridLines = false;
            _programList.BorderStyle = BorderStyle.None;
            _programList.Font = Theme.Body;
            _programList.BackColor = Theme.Surface;
            _programList.ForeColor = Theme.TextPrimary;
            _programList.SmallImageList = BuildRowIcons();
            _programList.Columns.Add("软件名称", 230, HorizontalAlignment.Left);
            _programList.Columns.Add("版本", 90, HorizontalAlignment.Left);
            _programList.Columns.Add("发布者", 150, HorizontalAlignment.Left);
            _programList.Columns.Add("安装位置", 380, HorizontalAlignment.Left);
            _programList.Columns.Add("占用空间", 90, HorizontalAlignment.Right);
            _programList.Columns.Add("来源", 80, HorizontalAlignment.Left);
            _programList.SelectedIndexChanged += delegate { OnProgramSelectionChanged(); };
            _programList.DoubleClick += delegate { OnProgramActivated(); };
            _programList.KeyDown += ProgramList_KeyDown;
            _programList.Resize += delegate { AdjustProgramColumns(); };
            Theme.EnableDoubleBuffer(_programList);
            _listHost.Controls.Add(_programList);

            _emptyLabel = new Label();
            _emptyLabel.Dock = DockStyle.Fill;
            _emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            _emptyLabel.Font = Theme.Body;
            _emptyLabel.ForeColor = Theme.TextMuted;
            _emptyLabel.BackColor = Theme.Surface;
            _emptyLabel.Text = "没有找到匹配的软件\r\n试试输入常用叫法或英文名，如 微信 / WeChat";
            _emptyLabel.Visible = false;
            _listHost.Controls.Add(_emptyLabel);

            // 底部操作条
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.Margin = new Padding(0);
            bottom.ColumnCount = 2;
            bottom.RowCount = 1;
            bottom.BackColor = Theme.Surface;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _selectionSummary = new Ctl.SelectionSummary();
            _selectionSummary.Dock = DockStyle.Fill;
            _selectionSummary.Margin = new Padding(0, 4, 0, 4);
            _selectionSummary.BackColor = Theme.Surface;
            bottom.Controls.Add(_selectionSummary, 0, 0);

            _nextButton = new Ctl.FlatButton();
            _nextButton.Text = "下一步：选择位置 →";
            _nextButton.Style = Ctl.FlatButtonStyle.Primary;
            _nextButton.Font = Theme.BodyBold;
            _nextButton.Dock = DockStyle.Fill;
            _nextButton.Margin = new Padding(8, 12, 0, 12);
            _nextButton.BackdropColor = Theme.Surface;
            _nextButton.Enabled = false;
            _nextButton.Click += delegate { GoToStep2(); };
            bottom.Controls.Add(_nextButton, 1, 0);
            layout.Controls.Add(bottom, 0, 8);

            _page1.Controls.Add(layout);
            _pageHost.Controls.Add(_page1);

            LayoutSearchRow();
            AdjustProgramColumns();
        }

        private ImageList BuildRowIcons()
        {
            _rowIcons = new ImageList();
            _rowIcons.ImageSize = new Size(16, 16);
            _rowIcons.ColorDepth = ColorDepth.Depth32Bit;
            _rowIcons.Images.Add(CreateDotIcon(Theme.Primary));
            _rowIcons.Images.Add(CreateDotIcon(Color.FromArgb(148, 163, 184)));
            return _rowIcons;
        }

        private static Bitmap CreateDotIcon(Color color)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(70, color)))
                {
                    g.FillEllipse(brush, 2, 2, 12, 12);
                }
                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillEllipse(brush, 5, 5, 6, 6);
                }
            }
            return bmp;
        }

        private void LayoutSearchRow()
        {
            if (_searchPanel == null || _searchBox == null || _searchButton == null) return;
            int width = _searchPanel.ClientSize.Width;
            int height = _searchPanel.ClientSize.Height;
            _searchButton.SetBounds(width - 96 - 6, (height - 32) / 2, 96, 32);
            _searchBox.SetBounds(44, (height - 22) / 2, Math.Max(60, width - 44 - 96 - 24), 22);
        }

        private void AdjustProgramColumns()
        {
            if (_programList == null) return;
            try
            {
                // 预留垂直滚动条宽度，避免出现横向滚动条
                int width = _programList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
                if (width <= 0) return;
                int name = 230;
                int version = 90;
                int publisher = 150;
                int size = 90;
                int source = 80;
                int path = width - name - version - publisher - size - source - 4;
                if (path < 180) path = 180;
                _programList.Columns[0].Width = name;
                _programList.Columns[1].Width = version;
                _programList.Columns[2].Width = publisher;
                _programList.Columns[3].Width = path;
                _programList.Columns[4].Width = size;
                _programList.Columns[5].Width = source;
            }
            catch { }
        }

        // ------------------------------------------------------------ 第 1 步逻辑

        private void SetStatusText(string text, Color color)
        {
            if (_statusLabel == null) return;
            _statusLabel.Text = text;
            _statusLabel.ForeColor = color;
        }

        private void OnBrowseShortcut()
        {
            try
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Title = "选择软件快捷方式";
                    dialog.Filter = "快捷方式 (*.lnk)|*.lnk|所有文件 (*.*)|*.*";
                    dialog.CheckFileExists = true;
                    dialog.Multiselect = false;
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) dialog.InitialDirectory = desktop;
                    if (dialog.ShowDialog(this) == DialogResult.OK) HandleDroppedPath(dialog.FileName);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("选择快捷方式失败: " + ex.Message);
                ShowWarnBox("打开文件选择框失败：" + ex.Message);
            }
        }

        private void HandleDroppedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            path = path.Trim().Trim('"');
            if (!Ctl.DropZonePanel.IsAcceptablePath(path))
            {
                _dropZone.ErrorText = "只支持 .lnk 快捷方式、程序文件夹或 exe 文件";
                return;
            }
            _dropZone.ErrorText = string.Empty;
            SetStatusText("正在读取：" + PathUtil.Shorten(path, 60), Theme.TextSecondary);
            Thread worker = new Thread(() => { ResolveDroppedPathWorker(path); });
            worker.IsBackground = true;
            worker.Start();
        }

        private void ResolveDroppedPathWorker(string path)
        {
            InstalledProgram program = null;
            string error = null;
            try
            {
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    InstalledProgram fromShortcut;
                    string err;
                    if (ProgramLocator.TryFromShortcut(path, out fromShortcut, out err)) program = fromShortcut;
                    else error = string.IsNullOrEmpty(err) ? "无法解析这个快捷方式" : err;
                }
                else if (Directory.Exists(path))
                {
                    program = ProgramLocator.FromDirectory(path);
                    if (program == null) error = "没能从该文件夹识别出已安装的软件";
                }
                else if (File.Exists(path))
                {
                    string directory = ProgramLocator.DeriveInstallDirectory(path);
                    if (!string.IsNullOrEmpty(directory)) program = ProgramLocator.FromDirectory(directory);
                    if (program == null) error = "没能从该文件推断出安装目录";
                }
                else
                {
                    error = "路径不存在：" + path;
                }
            }
            catch (NotImplementedException)
            {
                error = "读取路径模块暂不可用（后台模块尚未实现）";
            }
            catch (Exception ex)
            {
                Log.Warn("解析拖入路径失败: " + ex.Message);
                error = "读取失败：" + ex.Message;
            }

            SafeUi(delegate
            {
                if (program != null)
                {
                    _dropZone.ErrorText = string.Empty;
                    _selectedProgram = program;
                    _selectionSummary.SetProgram(program, null);
                    SelectProgramInList(program);
                    UpdateNextButtonState();
                    SetStatusText("已读取到：" + (string.IsNullOrEmpty(program.DisplayName) ? program.InstallPath : program.DisplayName),
                        Theme.TextSecondary);
                    GoToStep2();
                }
                else
                {
                    _dropZone.ErrorText = error == null ? "无法识别该路径" : error;
                    SetStatusText("未能识别拖入的内容", Theme.Danger);
                }
            });
        }

        private void StartScan()
        {
            if (_scanning) return;
            _scanning = true;
            _scanFailed = false;
            _scanErrorText = string.Empty;
            SetStatusText("正在扫描已安装软件…", Theme.TextSecondary);
            if (_refreshLink != null) _refreshLink.Enabled = false;
            RefreshLinkState();
            if (_programList.Items.Count == 0)
            {
                _emptyLabel.Text = "正在扫描已安装软件…";
                _emptyLabel.Visible = true;
                _programList.Visible = false;
                _emptyLabel.BringToFront();
            }
            Thread worker = new Thread(new ThreadStart(ScanWorker));
            worker.IsBackground = true;
            _scanThread = worker;
            worker.Start();
        }

        private void RefreshLinkState()
        {
            if (_refreshLink == null) return;
            _refreshLink.Enabled = !_scanning;
        }

        private void ScanWorker()
        {
            Stopwatch watch = Stopwatch.StartNew();
            long lastProgressTicks = 0;
            try
            {
                MoveProgressHandler onProgress = delegate(MoveProgress progress)
                {
                    if (progress == null) return;
                    long now = DateTime.Now.Ticks;
                    if (now - lastProgressTicks < 1600000L) return; // 160ms 节流
                    lastProgressTicks = now;
                    string message = progress.Message;
                    if (string.IsNullOrEmpty(message)) message = progress.StageText;
                    if (string.IsNullOrEmpty(message)) return;
                    string text = message;
                    SafeUi(delegate { if (_scanning) SetStatusText("正在扫描：" + text, Theme.TextSecondary); });
                };
                Func<bool> isCancelled = delegate { return !_scanning || _formClosing; };
                SearchIndex built = ProgramLocator.BuildIndex(isCancelled, onProgress);
                watch.Stop();
                SafeUi(delegate
                {
                    _scanning = false;
                    RefreshLinkState();
                    if (built == null)
                    {
                        _scanFailed = true;
                        _scanErrorText = "程序扫描模块没有返回结果";
                        ShowScanError();
                        return;
                    }
                    _index = built;
                    _scanFailed = false;
                    _scanElapsedMs = watch.ElapsedMilliseconds;
                    RefreshProgramList();
                });
            }
            catch (Exception ex)
            {
                watch.Stop();
                Log.Warn("扫描已安装软件失败: " + ex.Message);
                string friendly = FriendlyError("程序扫描", ex);
                SafeUi(delegate
                {
                    _scanning = false;
                    RefreshLinkState();
                    _scanFailed = true;
                    _scanErrorText = friendly;
                    ShowScanError();
                });
            }
        }

        private static string FriendlyError(string what, Exception ex)
        {
            if (ex is NotImplementedException) return what + "模块暂不可用（后台模块尚未实现）";
            return what + "失败：" + ex.Message;
        }

        private void ShowScanError()
        {
            _scanning = false;
            RefreshLinkState();
            if (_index != null && _index.Programs.Count > 0) return;
            SetStatusText(_scanErrorText + "，可点右侧「刷新」重试", Theme.Danger);
            _emptyLabel.Text = _scanErrorText + "\r\n可点击右侧「刷新」重新扫描";
            _emptyLabel.Visible = true;
            _programList.Visible = false;
            _emptyLabel.BringToFront();
        }

        private void DoSearch()
        {
            if (_searchBox == null) return;
            if (_searchPlaceholderActive) return;
            if (_scanning)
            {
                SetStatusText("正在扫描已安装软件，请稍候…", Theme.TextSecondary);
                return;
            }
            if (_index == null)
            {
                if (_scanFailed) ShowScanError();
                else SetStatusText("还没有扫描到软件，请稍候或点击「刷新」", Theme.TextSecondary);
                return;
            }
            try
            {
                RefreshProgramList();
            }
            catch (Exception ex)
            {
                Log.Warn("搜索失败: " + ex.Message);
                SetStatusText("搜索失败：" + ex.Message, Theme.Danger);
            }
        }

        private string CurrentKeyword()
        {
            if (_searchBox == null || _searchPlaceholderActive) return string.Empty;
            string text = _searchBox.Text.Trim();
            if (text == SearchPlaceholder) return string.Empty;
            return text;
        }

        private void RefreshProgramList()
        {
            if (_programList == null) return;
            if (_index == null)
            {
                _visiblePrograms = new List<InstalledProgram>();
                BindProgramList();
                return;
            }

            string keyword = CurrentKeyword();
            List<InstalledProgram> results;
            if (keyword.Length == 0)
            {
                results = new List<InstalledProgram>(_index.Programs);
            }
            else
            {
                try
                {
                    results = ProgramLocator.Search(_index, keyword, 200);
                }
                catch (Exception ex)
                {
                    Log.Warn("搜索模块失败: " + ex.Message);
                    SetStatusText("搜索模块暂不可用：" + ex.Message, Theme.Danger);
                    results = new List<InstalledProgram>();
                }
            }
            if (results == null) results = new List<InstalledProgram>();

            bool onlyC = _onlyCDrive != null && _onlyCDrive.Checked;
            _visiblePrograms = new List<InstalledProgram>();
            for (int i = 0; i < results.Count && _visiblePrograms.Count < 500; i++)
            {
                InstalledProgram program = results[i];
                if (program == null || string.IsNullOrEmpty(program.InstallPath)) continue;
                if (onlyC && !program.IsOnSystemDrive) continue;
                _visiblePrograms.Add(program);
            }

            BindProgramList();

            if (keyword.Length == 0)
            {
                string extra = onlyC ? " · 仅 C 盘" : string.Empty;
                string elapsed = _scanElapsedMs >= 0 ? " · 用时 " + (_scanElapsedMs / 1000.0).ToString("0.0") + " 秒" : string.Empty;
                SetStatusText("共找到 " + _visiblePrograms.Count + " 个软件" + extra + elapsed, Theme.TextSecondary);
            }
            else
            {
                SetStatusText("关键词「" + keyword + "」匹配到 " + _visiblePrograms.Count + " 个软件", Theme.TextSecondary);
            }
        }

        private int CountVisibleByFilter()
        {
            return _visiblePrograms == null ? 0 : _visiblePrograms.Count;
        }

        private void BindProgramList()
        {
            _programList.BeginUpdate();
            try
            {
                _programList.Items.Clear();
                for (int i = 0; i < _visiblePrograms.Count; i++)
                {
                    InstalledProgram program = _visiblePrograms[i];
                    string name = string.IsNullOrEmpty(program.DisplayName) ? "(未命名)" : program.DisplayName;
                    ListViewItem item = new ListViewItem(name);
                    item.SubItems.Add(string.IsNullOrEmpty(program.Version) ? "—" : program.Version);
                    item.SubItems.Add(string.IsNullOrEmpty(program.Publisher) ? "—" : program.Publisher);
                    item.SubItems.Add(string.IsNullOrEmpty(program.InstallPath) ? "—" : program.InstallPath);
                    item.SubItems.Add(program.SizeBytes >= 0 ? TextUtil.FormatBytes(program.SizeBytes) : "未知");
                    item.SubItems.Add(SourceText(program.Source));
                    item.ImageIndex = program.IsOnSystemDrive ? 0 : 1;
                    item.Tag = program;
                    _programList.Items.Add(item);
                }
            }
            finally
            {
                _programList.EndUpdate();
            }
            AdjustProgramColumns();

            bool empty = _programList.Items.Count == 0;
            if (empty)
            {
                string keyword = CurrentKeyword();
                if (keyword.Length > 0)
                    _emptyLabel.Text = "没有找到匹配的「" + keyword + "」\r\n试试输入常用叫法或英文名，如 微信 / WeChat";
                else
                    _emptyLabel.Text = "没有找到匹配的软件\r\n试试输入常用叫法或英文名，如 微信 / WeChat";
                _emptyLabel.Visible = true;
                _programList.Visible = false;
                _emptyLabel.BringToFront();
            }
            else
            {
                _emptyLabel.Visible = false;
                _programList.Visible = true;
            }

            if (_selectedProgram != null) SelectProgramInList(_selectedProgram);
        }

        private static string SourceText(ProgramSource source)
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

        private void SelectProgramInList(InstalledProgram program)
        {
            if (_programList == null || program == null) return;
            _selectingProgram = true;
            try
            {
                for (int i = 0; i < _programList.Items.Count; i++)
                {
                    InstalledProgram item = _programList.Items[i].Tag as InstalledProgram;
                    if (item == null) continue;
                    if (ReferenceEquals(item, program) ||
                        (item.InstallPath != null && program.InstallPath != null &&
                         PathUtil.SamePath(item.InstallPath, program.InstallPath)))
                    {
                        _programList.Items[i].Selected = true;
                        _programList.Items[i].Focused = true;
                        _programList.Items[i].EnsureVisible();
                        return;
                    }
                }
            }
            catch { }
            finally
            {
                _selectingProgram = false;
            }
        }

        private void ProgramList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OnProgramActivated();
            }
        }

        private void OnProgramActivated()
        {
            if (_programList.SelectedItems.Count == 0) return;
            InstalledProgram program = _programList.SelectedItems[0].Tag as InstalledProgram;
            if (program == null) return;
            OnProgramSelected(program);
            GoToStep2();
        }

        private void OnProgramSelectionChanged()
        {
            if (_selectingProgram) return;
            if (_programList.SelectedItems.Count == 0) return;
            InstalledProgram program = _programList.SelectedItems[0].Tag as InstalledProgram;
            if (program == null || ReferenceEquals(program, _selectedProgram)) return;
            OnProgramSelected(program);
        }

        private void OnProgramSelected(InstalledProgram program)
        {
            _selectedProgram = program;
            _selectionSummary.SetProgram(program, null);
            UpdateNextButtonState();
            int version = ++_sizeVersion;
            StartSelectionBackgroundWork(program, version);
        }

        private void StartSelectionBackgroundWork(InstalledProgram program, int version)
        {
            Thread worker = new Thread(() =>
            {
                Icon icon = null;
                long size = program.SizeBytes;
                try { icon = IconUtil.GetProgramIcon(program, false); }
                catch { }
                try
                {
                    if (size < 0)
                    {
                        size = FileUtil.GetDirectorySize(program.InstallPath,
                            delegate { return _formClosing || version != _sizeVersion; });
                    }
                }
                catch { }
                if (icon == null && size < 0) return;
                SafeUi(delegate
                {
                    if (_formClosing || version != _sizeVersion) return;
                    if (program.SizeBytes < 0 && size >= 0)
                    {
                        program.SizeBytes = size;
                        UpdateProgramRowSize(program);
                    }
                    if (ReferenceEquals(_selectedProgram, program))
                    {
                        _selectionSummary.SetProgram(program, icon);
                        if (_currentStep == 2) UpdatePage2Size();
                    }
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void UpdateProgramRowSize(InstalledProgram program)
        {
            if (_programList == null) return;
            for (int i = 0; i < _programList.Items.Count; i++)
            {
                if (ReferenceEquals(_programList.Items[i].Tag, program))
                {
                    _programList.Items[i].SubItems[4].Text =
                        program.SizeBytes >= 0 ? TextUtil.FormatBytes(program.SizeBytes) : "未知";
                    return;
                }
            }
        }

        private void UpdateNextButtonState()
        {
            if (_nextButton == null) return;
            _nextButton.Enabled = _selectedProgram != null;
        }

        private void ClearSearchPlaceholder()
        {
            if (!_searchPlaceholderActive) return;
            _searchPlaceholderActive = false;
            _searchBox.Text = string.Empty;
            _searchBox.ForeColor = Theme.TextPrimary;
        }

        private void SearchBox_LostFocus(object sender, EventArgs e)
        {
            if (_searchBox.TextLength == 0)
            {
                _searchPlaceholderActive = true;
                _searchBox.Text = SearchPlaceholder;
                _searchBox.ForeColor = Theme.TextMuted;
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (_searchPlaceholderActive && e.KeyCode != Keys.Left && e.KeyCode != Keys.Right &&
                e.KeyCode != Keys.Up && e.KeyCode != Keys.Down && e.KeyCode != Keys.Home &&
                e.KeyCode != Keys.End && e.KeyCode != Keys.Tab && e.KeyCode != Keys.ShiftKey &&
                e.KeyCode != Keys.ControlKey && e.KeyCode != Keys.Menu)
            {
                ClearSearchPlaceholder();
            }
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                DoSearch();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                _searchPlaceholderActive = false;
                _searchBox.Text = string.Empty;
                RefreshProgramList();
                _searchPlaceholderActive = true;
                _searchBox.Text = SearchPlaceholder;
                _searchBox.ForeColor = Theme.TextMuted;
            }
        }
    }
}
