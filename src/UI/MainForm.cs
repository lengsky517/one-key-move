// T3 拥有：主窗口（三步向导：选择软件 -> 选择位置 -> 开始搬移进度/结果）
// 本文件：窗口初始化、整体布局、步骤切换、公共辅助与预览用数据
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using YijianNuodong.Core;
using Ctl = YijianNuodong.UI.Controls;

namespace YijianNuodong.UI
{
    public partial class MainForm : Form
    {
        private readonly string _initialPath;
        private bool _previewMode;
        private bool _formClosing;
        private int _currentStep = 1;

        private Ctl.HeaderPanel _header;
        private Ctl.StepIndicator _stepIndicator;
        private Panel _contentHost;
        private Ctl.CardPanel _card;
        private Panel _pageHost;
        private Panel _page1;
        private Panel _page2;
        private Panel _page3;

        // 跨页面共享状态
        private InstalledProgram _selectedProgram;
        private MoveRequest _moveRequest;
        private MoveResult _moveResult;
        private PrecheckResult _precheck;
        private volatile bool _moveRunning;

        public MainForm() : this(null) { }

        public MainForm(string initialPath)
        {
            _initialPath = initialPath;
            InitializeWindow();
            BuildLayout();
            BuildPage1();
            BuildPage2();
            BuildPage3();
            ShowStep(1);
            EnableFormLevelDrop(this);

            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
            FormClosed += MainForm_FormClosed;
            Shown += MainForm_Shown;
        }

        // ------------------------------------------------------------ 窗口与整体布局

        private void InitializeWindow()
        {
            Text = "一键挪动";
            ClientSize = new Size(1100, 760);
            MinimumSize = new Size(1000, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Background;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;
            KeyPreview = true;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }
        }

        private void BuildLayout()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.BackColor = Theme.Background;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _header = new Ctl.HeaderPanel();
            _header.Dock = DockStyle.Fill;
            _header.Margin = new Padding(0);
            _header.HelpClicked += OnHelpClicked;
            root.Controls.Add(_header, 0, 0);

            _stepIndicator = new Ctl.StepIndicator();
            _stepIndicator.Dock = DockStyle.Fill;
            _stepIndicator.Margin = new Padding(0);
            root.Controls.Add(_stepIndicator, 0, 1);

            _contentHost = new Panel();
            _contentHost.Dock = DockStyle.Fill;
            _contentHost.BackColor = Theme.Background;
            _contentHost.Padding = new Padding(24, 10, 24, 22);
            root.Controls.Add(_contentHost, 0, 2);

            _card = new Ctl.CardPanel();
            _card.Dock = DockStyle.Fill;
            _card.CornerRadius = 14;
            _card.Padding = new Padding(24, 18, 24, 14);
            _card.FillColor = Theme.Surface;
            _card.BorderColor = Theme.Border;
            _card.BackdropColor = Theme.Background;
            _contentHost.Controls.Add(_card);

            _pageHost = new Panel();
            _pageHost.Dock = DockStyle.Fill;
            _pageHost.BackColor = Theme.Surface;
            _card.Controls.Add(_pageHost);

            Controls.Add(root);

            AllowDrop = true;
            DragEnter += Form_DragEnter;
            DragDrop += Form_DragDrop;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            try
            {
                _header.RefreshState();
            }
            catch { }

            if (_previewMode) return;

            RestoreWindowSize();
            StartScan();
            if (!string.IsNullOrEmpty(_initialPath)) HandleDroppedPath(_initialPath);
            CheckPendingRebootJob();
            UpdateNextButtonState();
        }

        private void RestoreWindowSize()
        {
            try
            {
                int w = AppSettings.WindowWidth;
                int h = AppSettings.WindowHeight;
                if (w >= MinimumSize.Width && h >= MinimumSize.Height && w <= 4000 && h <= 3000)
                {
                    ClientSize = new Size(w, h);
                }
            }
            catch { }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                if (_moveRunning)
                {
                    e.Cancel = true;
                    MessageBox.Show(this,
                        "正在搬移文件，请等待完成后再关闭窗口。\r\n中途关闭可能导致文件不完整。",
                        "一键挪动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            catch { }

            _formClosing = true;
            try
            {
                if (WindowState == FormWindowState.Normal)
                {
                    AppSettings.WindowWidth = ClientSize.Width;
                    AppSettings.WindowHeight = ClientSize.Height;
                }
                AppSettings.Save();
            }
            catch { }
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            _formClosing = true;
        }

        private bool _previewScrollToLocks;

        private void MainForm_Shown(object sender, EventArgs e)
        {
            if (!_previewScrollToLocks) return;
            _previewScrollToLocks = false;
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    try
                    {
                        if (_page2Scroll != null && _locksPanel != null && _locksPanel.Visible)
                        {
                            _page2Scroll.PerformLayout();
                            _page2Scroll.ScrollControlIntoView(_locksPanel);
                        }
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void ShowStep(int step)
        {
            _currentStep = step;
            if (_page1 == null) return;
            _page1.Visible = step == 1;
            _page2.Visible = step == 2;
            _page3.Visible = step == 3;
            _stepIndicator.ActiveStep = step - 1;
        }

        // ------------------------------------------------------------ 公共辅助

        /// <summary>安全地在界面线程执行操作（后台线程调用时自动 BeginInvoke）</summary>
        private void SafeUi(Action action)
        {
            if (action == null) return;
            try
            {
                if (IsDisposed || Disposing) return;
                if (!IsHandleCreated)
                {
                    action();
                    return;
                }
                if (InvokeRequired)
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        try
                        {
                            if (!IsDisposed) action();
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("界面更新异常: " + ex.Message);
                        }
                    }));
                }
                else
                {
                    action();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("界面更新失败: " + ex.Message);
            }
        }

        private void ShowInfoBox(string message)
        {
            try { MessageBox.Show(this, message, "一键挪动", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch { }
        }

        private void ShowWarnBox(string message)
        {
            try { MessageBox.Show(this, message, "一键挪动", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch { }
        }

        private void ShowErrorBox(string title, string message)
        {
            try { MessageBox.Show(this, message, string.IsNullOrEmpty(title) ? "一键挪动" : title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { }
        }

        private bool AskYesNo(string message)
        {
            try
            {
                return MessageBox.Show(this, message, "一键挪动", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            }
            catch
            {
                return false;
            }
        }

        private void OnHelpClicked()
        {
            string text = "使用方法\r\n" +
                "1. 把桌面上的软件快捷方式拖进窗口，或输入软件名称 / 常用叫法（如 微信、QQ、PS、剪映）进行搜索；\r\n" +
                "2. 选中软件后点「下一步」，选择目标位置，可以新建文件夹或修改文件夹名；\r\n" +
                "3. 点「开始搬移」，等待进度完成即可。\r\n\r\n" +
                "风险提示\r\n" +
                "• 搬移前请先退出正在运行的软件；\r\n" +
                "• 建议保留「在原位置保留目录联接」，否则原路径将失效，部分软件可能无法启动；\r\n" +
                "• 带内核驱动 / 系统服务的软件不建议搬移，搬移前建议创建系统还原点；\r\n" +
                "• 搬移完成后如发现问题，可以使用「撤销这次搬移」恢复原状。";
            ShowInfoBox(text);
        }

        private void CheckPendingRebootJob()
        {
            try
            {
                string jobFile;
                if (RebootMover.HasPendingJob(out jobFile))
                {
                    Log.Info("检测到重启后待完成的搬移任务: " + jobFile);
                }
            }
            catch
            {
                // 后台模块未实现时忽略
            }
        }

        // ------------------------------------------------------------ 拖放（窗口任意位置）

        private void Form_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        private void Form_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                HandleDroppedPath(files[0]);
            }
            catch (Exception ex)
            {
                Log.Warn("窗口拖放处理失败: " + ex.Message);
            }
        }

        /// <summary>让窗口内大部分区域都能接收文件拖放（用户可能拖到列表、卡片等位置）</summary>
        private void EnableFormLevelDrop(Control parent)
        {
            if (parent == null) return;
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control child = parent.Controls[i];
                bool skip = child is TextBoxBase || child is ComboBox || child is ListBox ||
                            child is ButtonBase || child is CheckBox || child is Ctl.DropZonePanel ||
                            child is Ctl.FlatButton;
                if (!skip)
                {
                    try
                    {
                        child.AllowDrop = true;
                        child.DragEnter += Form_DragEnter;
                        child.DragDrop += Form_DragDrop;
                    }
                    catch { }
                }
                EnableFormLevelDrop(child);
            }
        }

        // ------------------------------------------------------------ 预览辅助（仅开发渲染用，正式运行无副作用）

        /// <summary>填入演示数据，便于界面预览（正式运行不会调用）</summary>
        public void PreviewFillDemoData()
        {
            _previewMode = true;
            _scanFailed = false;
            _scanElapsedMs = 900;
            _index = BuildDemoIndex();
            try
            {
                _onlyCDrive.Checked = true;
            }
            catch { }
            RefreshProgramList();
            SetStatusText("共找到 " + CountVisibleByFilter() + " 个软件 · 用时 0.9 秒", Theme.TextSecondary);
        }

        /// <summary>渲染预览切换到指定步骤：1 列表 / 2 选择位置 / 3 进度 / 4 成功 / 5 失败 / 6 占用提示 / 7 重启提示</summary>
        public void PreviewStep(int step)
        {
            _previewMode = true;
            try
            {
                if (step == 1)
                {
                    ShowStep(1);
                    if (_index == null && !_scanFailed) PreviewScanUnavailable();
                    return;
                }
                if (_index == null) PreviewFillDemoData();
                EnsureDemoProgramSelected();
                if (step == 2)
                {
                    PreparePage2ForPreview(false, false);
                }
                else if (step == 3)
                {
                    PreparePage2ForPreview(false, false);
                    ShowStep(3);
                    PreviewProgressDemo();
                }
                else if (step == 4 || step == 7)
                {
                    PreparePage2ForPreview(false, false);
                    ShowStep(3);
                    ShowSuccessDemo(step == 7);
                }
                else if (step == 5)
                {
                    PreparePage2ForPreview(false, false);
                    ShowStep(3);
                    ShowFailureDemo();
                }
                else if (step == 6)
                {
                    PreparePage2ForPreview(true, true);
                    ShowStep(2);
                    _previewScrollToLocks = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("预览步骤切换失败: " + ex.Message);
            }
        }

        /// <summary>模拟“扫描模块不可用”的界面状态（用于验证异常不崩溃）</summary>
        public void PreviewScanUnavailable()
        {
            _previewMode = true;
            _index = null;
            _scanning = false;
            _scanFailed = true;
            _scanErrorText = "程序扫描模块暂不可用（后台模块尚未实现）";
            ShowStep(1);
            ShowScanError();
        }

        private void EnsureDemoProgramSelected()
        {
            if (_selectedProgram != null) return;
            if (_index == null || _index.Programs.Count == 0) return;
            InstalledProgram demo = _index.Programs[0];
            _selectedProgram = demo;
            _selectionSummary.SetProgram(demo, null);
            SelectProgramInList(demo);
            UpdateNextButtonState();
        }

        private static InstalledProgram DemoProgram(string name, string path, string version, string publisher,
            long megabytes, bool running, ProgramSource source)
        {
            InstalledProgram program = new InstalledProgram();
            program.DisplayName = name;
            program.InstallPath = path;
            program.Version = version;
            program.Publisher = publisher;
            program.SizeBytes = megabytes * 1024L * 1024L;
            program.IsRunning = running;
            program.Source = source;
            program.DriveRoot = PathUtil.GetDriveRoot(path);
            program.Id = path.ToLowerInvariant();
            return program;
        }

        private static SearchIndex BuildDemoIndex()
        {
            SearchIndex index = new SearchIndex();
            index.BuiltAt = DateTime.Now;
            index.Programs.Add(DemoProgram("微信", @"C:\Program Files\Tencent\WeChat", "3.9.12.55",
                "腾讯科技（深圳）有限公司", 3450, true, ProgramSource.Registry | ProgramSource.Shortcut));
            index.Programs.Add(DemoProgram("QQ", @"C:\Program Files\Tencent\QQNT", "9.9.9",
                "腾讯科技（深圳）有限公司", 2180, true, ProgramSource.Registry));
            index.Programs.Add(DemoProgram("Adobe Photoshop 2024", @"C:\Program Files\Adobe\Adobe Photoshop 2024", "25.5.0",
                "Adobe Inc.", 6890, false, ProgramSource.Registry));
            index.Programs.Add(DemoProgram("剪映专业版", @"C:\Program Files\JianyingPro", "5.9.0",
                "深圳市脸萌科技有限公司", 1240, false, ProgramSource.Shortcut));
            index.Programs.Add(DemoProgram("钉钉", @"C:\Program Files (x86)\DingDing", "7.6.20",
                "阿里巴巴（中国）有限公司", 1830, true, ProgramSource.Registry | ProgramSource.StartMenu));
            index.Programs.Add(DemoProgram("网易云音乐", @"C:\Program Files (x86)\Netease\CloudMusic", "3.0.6",
                "网易（杭州）网络有限公司", 1620, false, ProgramSource.Shortcut));
            index.Programs.Add(DemoProgram("WPS Office", @"C:\Program Files (x86)\Kingsoft\WPS Office", "12.1.0",
                "北京金山办公软件股份有限公司", 2760, false, ProgramSource.Registry));
            index.Programs.Add(DemoProgram("Steam", @"D:\Steam", "2.10.91.91",
                "Valve Corporation", 25600, false, ProgramSource.Shortcut));
            return index;
        }

        private void Form_DragOver(object sender, DragEventArgs e)
        {
        }
    }
}
