// UI 卸载对话框拥有：直接卸载软件的三态对话框（确认 -> 进度 -> 结果，同一个窗体切换）
// 预检与执行都在后台线程，进度回调通过 BeginInvoke 回到 UI 线程；
// 视觉风格沿用 FolderPickerDialog / Page3 的写法（白底卡片、蓝色主色、危险色按钮）。
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
    public class UninstallDialog : Form
    {
        private const int StateConfirm = 1;
        private const int StateProgress = 2;
        private const int StateResult = 3;

        private const int HeaderHeight = 72;
        private const int FooterHeight = 64;
        private const int RowHeight = 28;
        private const int CaptionWidth = 88;

        // 同一时间只允许一个卸载对话框（静态标记，所见即所得）
        private static bool _instanceOpen;

        private readonly InstalledProgram _program;

        private UninstallTarget _target;
        private UninstallResult _result;
        private UninstallRequest _request;
        private bool _running;
        private volatile bool _cancelRequested;
        private volatile bool _closing;
        private volatile bool _precheckActive;
        private bool _previewDone;
        // 版式意图标记：控件自身的 Visible 在父级尚不可见时读出来恒为 false，
        // 所以排版不能依赖控件的 Visible，一律看下面这些标记。
        private bool _wantAnalyzing = true;
        private bool _wantJunction;
        private bool _wantLocks;
        private bool _wantData;
        private bool _wantWarn;
        private bool _wantError;
        private bool _wantManual;
        private int _state = StateConfirm;
        private int _activeStage = -1;
        private ToolTip _tips;

        // 头部
        private Ctl.ProgramIconBox _iconBox;
        private Label _titleLabel;
        private Label _subTitleLabel;

        // 底部
        private Ctl.FlatButton _primaryButton;
        private Ctl.FlatButton _secondaryButton;
        private Label _footerHint;

        // 确认页
        private Panel _confirmPanel;
        private Panel _confirmBody;
        private Label _analyzingLabel;
        private Ctl.ProgressBarEx _analyzingBar;
        private Label _pathCaption;
        private Label _pathValue;
        private Ctl.NoticePanel _junctionNotice;
        private Label _sizeCaption;
        private Label _sizeValue;
        private Label _methodCaption;
        private Label _methodValue;
        private Label _stepsTitle;
        private Label _stepsLabel;
        private Label _cleanupTitle;
        private Label _cleanupLabel;
        private Ctl.FlatCheckBox _chkClose;
        private Ctl.FlatCheckBox _chkLeftovers;
        private Ctl.FlatCheckBox _chkUserData;
        private Ctl.FlatCheckBox _chkRestore;
        private Ctl.FlatCheckBox _chkManual;
        private Label _locksLabel;
        private Label _dataLabel;
        private Ctl.NoticePanel _warnNotice;
        private Ctl.NoticePanel _errorNotice;

        // 进度页
        private Panel _progressPanel;
        private TableLayoutPanel _progressLayout;
        private Ctl.StageList _stageList;
        private Label _stageText;
        private Ctl.ProgressBarEx _progressBar;
        private Label _percentText;
        private Label _currentItem;
        private Ctl.NoticePanel _waitingHint;
        private Label _progressHint;

        // 结果页
        private Panel _resultPanel;
        private TableLayoutPanel _resultLayout;
        private Ctl.StateBanner _banner;
        private Label _statExit;
        private Label _statItems;
        private Label _statShortcuts;
        private Label _statRegistry;
        private Label _statFolder;
        private Label _leftoverTitle;
        private Label _leftoverLabel;
        private Ctl.FlatButton _logButton;
        private Label _logHint;

        // ------------------------------------------------------------------
        // 构造与静态入口
        // ------------------------------------------------------------------

        public UninstallDialog(InstalledProgram program)
        {
            _program = program;

            this.Text = "卸载软件";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.BackColor = Theme.Surface;
            this.Font = Theme.Body;
            this.KeyPreview = true;
            this.ClientSize = new Size(760, 800);
            Theme.EnableDoubleBuffer(this);

            _tips = new ToolTip();
            _tips.AutoPopDelay = 30000;
            _tips.InitialDelay = 320;
            _tips.ReshowDelay = 120;

            BuildHeader();
            BuildConfirmPanel();
            BuildProgressPanel();
            BuildResultPanel();
            BuildFooter();

            // WinForms 的停靠顺序是按 z 序从后往前：正文排到最前才会「最后停靠」，
            // 这样头部和底部先认领 72 / 64 的高度，正文只填中间那条。
            if (_body != null) _body.BringToFront();

            this.Shown += delegate { StartPrecheck(); };
            this.Resize += delegate { LayoutFooter(); };

            ShowState(StateConfirm);
        }

        /// <summary>真正执行完卸载后返回 UninstallResult；只是取消则返回 null。</summary>
        public UninstallResult Result
        {
            get { return _result; }
        }

        /// <summary>
        /// 打开卸载对话框；返回 null 表示用户取消了（没有执行卸载）。
        /// 同一时间只允许一个对话框，重复调用返回 null。
        /// </summary>
        public static UninstallResult ShowUninstall(IWin32Window owner, InstalledProgram program)
        {
            if (program == null) return null;
            if (_instanceOpen) return null;
            _instanceOpen = true;
            try
            {
                using (UninstallDialog dialog = new UninstallDialog(program))
                {
                    dialog.ShowDialog(owner);
                    return dialog.Result;
                }
            }
            finally
            {
                _instanceOpen = false;
            }
        }

        // ------------------------------------------------------------------
        // 界面搭建
        // ------------------------------------------------------------------

        private void BuildHeader()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = HeaderHeight;
            header.BackColor = Theme.Surface;
            this.Controls.Add(header);

            _iconBox = new Ctl.ProgramIconBox();
            _iconBox.Bounds = new Rectangle(24, 14, 44, 44);
            _iconBox.FallbackText = "?";
            Icon icon = null;
            try
            {
                if (_program != null) icon = IconUtil.GetProgramIcon(_program, false);
            }
            catch (Exception)
            {
                icon = null;
            }
            if (icon != null) _iconBox.SetIcon(icon);
            header.Controls.Add(_iconBox);

            _titleLabel = MakeLabel(string.Empty, Theme.H1Small, Theme.TextPrimary);
            _titleLabel.Bounds = new Rectangle(84, 9, 640, 30);
            header.Controls.Add(_titleLabel);

            _subTitleLabel = MakeLabel(string.Empty, Theme.Small, Theme.TextSecondary);
            _subTitleLabel.Bounds = new Rectangle(86, 40, 640, 20);
            header.Controls.Add(_subTitleLabel);

            Panel line = new Panel();
            line.BackColor = Theme.Border;
            line.Dock = DockStyle.Bottom;
            line.Height = 1;
            header.Controls.Add(line);
        }

        private TableLayoutPanel _body;

        private TableLayoutPanel Body()
        {
            if (_body != null) return _body;
            _body = new TableLayoutPanel();
            _body.Dock = DockStyle.Fill;
            _body.BackColor = Theme.Surface;
            _body.ColumnCount = 1;
            _body.RowCount = 1;
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _body.Padding = new Padding(0);
            this.Controls.Add(_body);
            return _body;
        }

        private void BuildConfirmPanel()
        {
            TableLayoutPanel body = Body();

            _confirmPanel = new Panel();
            _confirmPanel.Dock = DockStyle.Fill;
            _confirmPanel.BackColor = Theme.Surface;
            _confirmPanel.Visible = false;
            body.Controls.Add(_confirmPanel);

            _confirmBody = new Panel();
            _confirmBody.Dock = DockStyle.Fill;
            _confirmBody.BackColor = Theme.Surface;
            _confirmBody.AutoScroll = true;
            _confirmBody.Resize += delegate { LayoutConfirmBody(); };
            _confirmPanel.Controls.Add(_confirmBody);

            _analyzingLabel = MakeLabel("正在分析安装信息…", Theme.Body, Theme.TextSecondary);
            _confirmBody.Controls.Add(_analyzingLabel);

            _analyzingBar = new Ctl.ProgressBarEx();
            _analyzingBar.IsIndeterminate = true;
            _analyzingBar.Height = 6;
            _confirmBody.Controls.Add(_analyzingBar);

            _pathCaption = MakeCaption("安装位置");
            _pathValue = MakeLabel(string.Empty, Theme.Body, Theme.TextPrimary);
            _confirmBody.Controls.Add(_pathCaption);
            _confirmBody.Controls.Add(_pathValue);

            _sizeCaption = MakeCaption("占用空间");
            _sizeValue = MakeLabel(string.Empty, Theme.Body, Theme.TextPrimary);
            _confirmBody.Controls.Add(_sizeCaption);
            _confirmBody.Controls.Add(_sizeValue);

            _methodCaption = MakeCaption("卸载方式");
            _methodValue = MakeLabel(string.Empty, Theme.Body, Theme.TextPrimary);
            _confirmBody.Controls.Add(_methodCaption);
            _confirmBody.Controls.Add(_methodValue);

            _junctionNotice = new Ctl.NoticePanel();
            _junctionNotice.BackdropColor = Theme.Surface;
            _junctionNotice.Visible = false;
            _confirmBody.Controls.Add(_junctionNotice);

            _warnNotice = new Ctl.NoticePanel();
            _warnNotice.BackdropColor = Theme.Surface;
            _warnNotice.Visible = false;
            _confirmBody.Controls.Add(_warnNotice);

            _errorNotice = new Ctl.NoticePanel();
            _errorNotice.BackdropColor = Theme.Surface;
            _errorNotice.Visible = false;
            _confirmBody.Controls.Add(_errorNotice);

            _stepsTitle = MakeLabel("将执行以下操作", Theme.BodyBold, Theme.TextPrimary);
            _confirmBody.Controls.Add(_stepsTitle);

            _stepsLabel = MakeLabel(string.Empty, Theme.Body, Theme.TextSecondary);
            _stepsLabel.TextAlign = ContentAlignment.TopLeft;
            _confirmBody.Controls.Add(_stepsLabel);

            _cleanupTitle = MakeLabel("将被清理", Theme.BodyBold, Theme.TextPrimary);
            _confirmBody.Controls.Add(_cleanupTitle);

            _cleanupLabel = MakeLabel(string.Empty, Theme.Small, Theme.TextSecondary);
            _cleanupLabel.TextAlign = ContentAlignment.TopLeft;
            _confirmBody.Controls.Add(_cleanupLabel);

            _chkClose = MakeCheckBox("自动结束正在运行的程序");
            _chkClose.Checked = true;
            _confirmBody.Controls.Add(_chkClose);

            _locksLabel = MakeLabel(string.Empty, Theme.Small, Theme.Warning);
            _locksLabel.Visible = false;
            _confirmBody.Controls.Add(_locksLabel);

            _chkLeftovers = MakeCheckBox("卸载后清理残留文件和快捷方式");
            _chkLeftovers.Checked = true;
            _confirmBody.Controls.Add(_chkLeftovers);

            _chkUserData = MakeCheckBox("同时删除用户配置数据（%APPDATA% 等）");
            _chkUserData.Checked = false;
            _chkUserData.CheckedChanged += delegate { OnUserDataCheckedChanged(); };
            _confirmBody.Controls.Add(_chkUserData);

            _dataLabel = MakeLabel(string.Empty, Theme.Small, Theme.Danger);
            _dataLabel.TextAlign = ContentAlignment.TopLeft;
            _dataLabel.Visible = false;
            _confirmBody.Controls.Add(_dataLabel);

            _chkRestore = MakeCheckBox("创建系统还原点");
            _chkRestore.Checked = true;
            _confirmBody.Controls.Add(_chkRestore);

            // 没有官方卸载程序的软件：必须显式勾选，否则引擎会拒绝删任何东西
            _chkManual = MakeCheckBox("我确认手工清理（这个软件没有官方卸载程序）");
            _chkManual.Checked = false;
            _chkManual.Visible = false;
            _chkManual.CheckedChanged += delegate { UpdateConfirmFooterState(); };
            _confirmBody.Controls.Add(_chkManual);
        }

        private void BuildProgressPanel()
        {
            TableLayoutPanel body = Body();

            _progressPanel = new Panel();
            _progressPanel.Dock = DockStyle.Fill;
            _progressPanel.BackColor = Theme.Surface;
            _progressPanel.Visible = false;
            body.Controls.Add(_progressPanel);

            _progressLayout = new TableLayoutPanel();
            _progressLayout.Dock = DockStyle.Fill;
            _progressLayout.BackColor = Theme.Surface;
            _progressLayout.ColumnCount = 1;
            _progressLayout.RowCount = 13;
            _progressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _progressLayout.Padding = new Padding(24, 18, 24, 10);
            // 首尾各一个弹性行，让进度区在对话框里垂直居中（等待提示出现/消失时也不会贴顶跳动）
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            _progressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _progressPanel.Controls.Add(_progressLayout);

            _stageList = new Ctl.StageList();
            _stageList.Dock = DockStyle.Fill;
            _stageList.Margin = new Padding(0);
            _stageList.SetStages(BuildStageTitles());
            _progressLayout.Controls.Add(_stageList, 0, 1);

            TableLayoutPanel stageRow = new TableLayoutPanel();
            stageRow.Dock = DockStyle.Fill;
            stageRow.Margin = new Padding(0);
            stageRow.ColumnCount = 2;
            stageRow.RowCount = 1;
            stageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            stageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            _progressLayout.Controls.Add(stageRow, 0, 3);

            _stageText = MakeLabel("正在准备…", Theme.BodyBold, Theme.Primary);
            _stageText.Dock = DockStyle.Fill;
            _stageText.Margin = new Padding(0);
            stageRow.Controls.Add(_stageText, 0, 0);

            _percentText = MakeLabel(string.Empty, Theme.BodyBold, Theme.TextSecondary);
            _percentText.Dock = DockStyle.Fill;
            _percentText.Margin = new Padding(0);
            _percentText.TextAlign = ContentAlignment.MiddleRight;
            stageRow.Controls.Add(_percentText, 1, 0);

            _waitingHint = new Ctl.NoticePanel();
            _waitingHint.BackdropColor = Theme.Surface;
            _waitingHint.Dock = DockStyle.Fill;
            _waitingHint.Margin = new Padding(0);
            _waitingHint.Visible = false;
            _progressLayout.Controls.Add(_waitingHint, 0, 5);

            _progressBar = new Ctl.ProgressBarEx();
            _progressBar.Dock = DockStyle.Fill;
            _progressBar.Margin = new Padding(0);
            _progressLayout.Controls.Add(_progressBar, 0, 7);

            _currentItem = MakeLabel(string.Empty, Theme.Body, Theme.TextPrimary);
            _currentItem.Dock = DockStyle.Fill;
            _currentItem.Margin = new Padding(0);
            _progressLayout.Controls.Add(_currentItem, 0, 9);

            _progressHint = MakeLabel("卸载过程中请不要关闭软件窗口；点击「取消」会等待当前步骤结束后停下。",
                Theme.Small, Theme.TextMuted);
            _progressHint.Dock = DockStyle.Fill;
            _progressHint.Margin = new Padding(0);
            _progressLayout.Controls.Add(_progressHint, 0, 11);
        }

        private void BuildResultPanel()
        {
            TableLayoutPanel body = Body();

            _resultPanel = new Panel();
            _resultPanel.Dock = DockStyle.Fill;
            _resultPanel.BackColor = Theme.Surface;
            _resultPanel.Visible = false;
            body.Controls.Add(_resultPanel);

            _resultLayout = new TableLayoutPanel();
            _resultLayout.Dock = DockStyle.Fill;
            _resultLayout.BackColor = Theme.Surface;
            _resultLayout.ColumnCount = 1;
            _resultLayout.RowCount = 10;
            _resultLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _resultLayout.Padding = new Padding(24, 18, 24, 12);
            _resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            _resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            _resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            _resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            _resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            _resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            _resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            _resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            _resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            _resultLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _resultPanel.Controls.Add(_resultLayout);

            _banner = new Ctl.StateBanner();
            _banner.Dock = DockStyle.Fill;
            _banner.Margin = new Padding(0);
            _resultLayout.Controls.Add(_banner, 0, 0);

            TableLayoutPanel stats = new TableLayoutPanel();
            stats.Dock = DockStyle.Fill;
            stats.Margin = new Padding(0);
            stats.ColumnCount = 2;
            stats.RowCount = 5;
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) stats.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            _resultLayout.Controls.Add(stats, 0, 2);

            _statExit = MakeStat(stats, 0, "卸载程序退出码");
            _statItems = MakeStat(stats, 1, "清理项数");
            _statShortcuts = MakeStat(stats, 2, "删除快捷方式");
            _statRegistry = MakeStat(stats, 3, "注册表项");
            _statFolder = MakeStat(stats, 4, "安装目录");

            _leftoverTitle = MakeLabel("残留项", Theme.SmallBold, Theme.TextPrimary);
            _leftoverTitle.Dock = DockStyle.Fill;
            _leftoverTitle.Margin = new Padding(0);
            _leftoverTitle.Visible = false;
            _resultLayout.Controls.Add(_leftoverTitle, 0, 4);

            _leftoverLabel = MakeLabel(string.Empty, Theme.Small, Theme.TextSecondary);
            _leftoverLabel.Dock = DockStyle.Fill;
            _leftoverLabel.Margin = new Padding(0);
            _leftoverLabel.TextAlign = ContentAlignment.TopLeft;
            _leftoverLabel.Visible = false;
            _resultLayout.Controls.Add(_leftoverLabel, 0, 5);

            TableLayoutPanel logRow = new TableLayoutPanel();
            logRow.Dock = DockStyle.Fill;
            logRow.Margin = new Padding(0);
            logRow.ColumnCount = 2;
            logRow.RowCount = 1;
            logRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            logRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _resultLayout.Controls.Add(logRow, 0, 7);

            _logButton = new Ctl.FlatButton();
            _logButton.Text = "查看失败诊断日志";
            _logButton.Style = Ctl.FlatButtonStyle.Secondary;
            _logButton.Font = Theme.SmallBold;
            _logButton.Dock = DockStyle.Fill;
            _logButton.Margin = new Padding(0, 0, 8, 0);
            _logButton.BackdropColor = Theme.Surface;
            _logButton.Visible = false;
            _logButton.Click += delegate { OpenFailureLog(); };
            logRow.Controls.Add(_logButton, 0, 0);

            _logHint = MakeLabel("把日志发给开发者可以帮你定位问题。", Theme.Small, Theme.TextMuted);
            _logHint.Dock = DockStyle.Fill;
            _logHint.Margin = new Padding(0);
            _logHint.Visible = false;
            logRow.Controls.Add(_logHint, 1, 0);
        }

        private void BuildFooter()
        {
            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = FooterHeight;
            footer.BackColor = Theme.Surface;
            this.Controls.Add(footer);
            footer.BringToFront();

            Panel line = new Panel();
            line.BackColor = Theme.Border;
            line.Dock = DockStyle.Top;
            line.Height = 1;
            footer.Controls.Add(line);

            _footerHint = MakeLabel(string.Empty, Theme.Small, Theme.TextMuted);
            _footerHint.TextAlign = ContentAlignment.MiddleLeft;
            footer.Controls.Add(_footerHint);

            _primaryButton = new Ctl.FlatButton();
            _primaryButton.Text = "开始卸载";
            _primaryButton.Style = Ctl.FlatButtonStyle.Danger;
            _primaryButton.Font = Theme.BodyBold;
            _primaryButton.Size = new Size(140, 38);
            _primaryButton.BackdropColor = Theme.Surface;
            _primaryButton.Click += delegate { PrimaryButton_Click(); };
            footer.Controls.Add(_primaryButton);

            _secondaryButton = new Ctl.FlatButton();
            _secondaryButton.Text = "取消";
            _secondaryButton.Style = Ctl.FlatButtonStyle.Secondary;
            _secondaryButton.Font = Theme.BodyBold;
            _secondaryButton.Size = new Size(100, 38);
            _secondaryButton.BackdropColor = Theme.Surface;
            _secondaryButton.Click += delegate { CloseWithOk(); };
            footer.Controls.Add(_secondaryButton);

            LayoutFooter();
        }

        // ------------------------------------------------------------------
        // 排版辅助
        // ------------------------------------------------------------------

        private static Label MakeLabel(string text, Font font, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = font;
            label.ForeColor = color;
            label.BackColor = Theme.Surface;
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.UseMnemonic = false;
            return label;
        }

        private static Label MakeCaption(string text)
        {
            Label label = MakeLabel(text, Theme.Small, Theme.TextMuted);
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static Ctl.FlatCheckBox MakeCheckBox(string text)
        {
            Ctl.FlatCheckBox box = new Ctl.FlatCheckBox();
            box.AutoSize = false;
            box.Height = 24;
            box.Text = text;
            box.Font = Theme.Body;
            box.BackdropColor = Theme.Surface;
            return box;
        }

        private static Label MakeStat(TableLayoutPanel table, int row, string caption)
        {
            Label cap = MakeLabel(caption, Theme.Small, Theme.TextMuted);
            cap.Dock = DockStyle.Fill;
            cap.Margin = new Padding(0);
            table.Controls.Add(cap, 0, row);

            Label value = MakeLabel(string.Empty, Theme.Body, Theme.TextPrimary);
            value.Dock = DockStyle.Fill;
            value.Margin = new Padding(0);
            table.Controls.Add(value, 1, row);
            return value;
        }

        private void LayoutFooter()
        {
            if (_primaryButton == null || _secondaryButton == null || _footerHint == null) return;
            int right = this.ClientSize.Width - 24;
            int y = (FooterHeight - _primaryButton.Height) / 2;
            _primaryButton.Location = new Point(right - _primaryButton.Width, y);
            _secondaryButton.Location = new Point(_primaryButton.Left - 10 - _secondaryButton.Width, y);
            int hintWidth = _secondaryButton.Left - 24 - 16;
            if (hintWidth < 0) hintWidth = 0;
            _footerHint.Bounds = new Rectangle(24, y, hintWidth, _secondaryButton.Height);
        }

        private void LayoutConfirmBody()
        {
            if (_confirmBody == null) return;
            int width = _confirmBody.ClientSize.Width - 2;
            if (width < 400) width = 400;
            int left = 24;
            int contentWidth = width - left * 2;
            if (contentWidth < 280) contentWidth = 280;
            int y = 16;

            _analyzingLabel.Bounds = new Rectangle(left, y, contentWidth, 24);
            y += 24;
            if (_wantAnalyzing)
            {
                _analyzingBar.Bounds = new Rectangle(left, y, contentWidth, 6);
                y += 15;
            }
            y += 3;

            LayoutRow(_pathCaption, _pathValue, left, y, contentWidth);
            y += RowHeight;

            if (_wantJunction)
            {
                _junctionNotice.SetLayoutWidth(contentWidth);
                _junctionNotice.Bounds = new Rectangle(left, y, contentWidth, _junctionNotice.Height);
                y += _junctionNotice.Height + 6;
            }

            LayoutRow(_sizeCaption, _sizeValue, left, y, contentWidth);
            y += RowHeight;
            LayoutRow(_methodCaption, _methodValue, left, y, contentWidth);
            y += RowHeight + 6;

            _stepsTitle.Bounds = new Rectangle(left, y, contentWidth, 24);
            y += 26;
            _stepsLabel.Bounds = new Rectangle(left + 2, y, contentWidth - 2, TextHeight(_stepsLabel.Text, contentWidth, Theme.Body));
            y += _stepsLabel.Height + 8;

            _cleanupTitle.Bounds = new Rectangle(left, y, contentWidth, 22);
            y += 22;
            _cleanupLabel.Bounds = new Rectangle(left + 2, y, contentWidth - 2, 20);
            y += 28;

            _chkClose.Bounds = new Rectangle(left, y, contentWidth, 24);
            y += 26;
            if (_wantLocks)
            {
                _locksLabel.Bounds = new Rectangle(left + 26, y, contentWidth - 26, 20);
                y += 22;
            }
            y += 2;

            _chkLeftovers.Bounds = new Rectangle(left, y, contentWidth, 24);
            y += 28;

            _chkUserData.Bounds = new Rectangle(left, y, contentWidth, 24);
            y += 26;
            if (_wantData)
            {
                _dataLabel.Bounds = new Rectangle(left + 26, y, contentWidth - 26, 20);
                y += 24;
            }

            _chkRestore.Bounds = new Rectangle(left, y, contentWidth, 24);
            y += 28;

            _chkManual.Bounds = new Rectangle(left, y, contentWidth, 24);
            if (_wantManual) y += 28;

            if (_wantWarn)
            {
                _warnNotice.SetLayoutWidth(contentWidth);
                _warnNotice.Bounds = new Rectangle(left, y, contentWidth, _warnNotice.Height);
                y += _warnNotice.Height + 8;
            }
            if (_wantError)
            {
                _errorNotice.SetLayoutWidth(contentWidth);
                _errorNotice.Bounds = new Rectangle(left, y, contentWidth, _errorNotice.Height);
                y += _errorNotice.Height + 8;
            }

            y += 12;
            _confirmBody.AutoScrollMinSize = new Size(0, y);
        }

        private static void LayoutRow(Label caption, Label value, int left, int y, int contentWidth)
        {
            caption.Bounds = new Rectangle(left, y, CaptionWidth - 8, 26);
            int valueLeft = left + CaptionWidth;
            value.Bounds = new Rectangle(valueLeft, y, contentWidth - CaptionWidth, 26);
        }

        private static int TextHeight(string text, int width, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 24;
            Size proposed = new Size(width - 8, 1000);
            Size size = TextRenderer.MeasureText(text, font, proposed, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            if (size.Height < 24) return 24;
            if (size.Height > 240) return 240;
            return size.Height;
        }

        // ------------------------------------------------------------------
        // 状态切换
        // ------------------------------------------------------------------

        private void ShowState(int state)
        {
            _state = state;
            if (_confirmPanel != null) _confirmPanel.Visible = state == StateConfirm;
            if (_progressPanel != null) _progressPanel.Visible = state == StateProgress;
            if (_resultPanel != null) _resultPanel.Visible = state == StateResult;

            if (state == StateProgress)
            {
                _primaryButton.Text = "取消";
                _primaryButton.Style = Ctl.FlatButtonStyle.Secondary;
                _primaryButton.Width = 100;
                _primaryButton.Enabled = !_cancelRequested;
                _secondaryButton.Visible = false;
                _footerHint.Text = "卸载过程中请不要关闭软件窗口，也不要强行结束卸载程序。";
                _footerHint.ForeColor = Theme.TextSecondary;
                LayoutFooter();
                return;
            }

            if (state == StateResult)
            {
                _primaryButton.Style = Ctl.FlatButtonStyle.Primary;
                _primaryButton.Width = 120;
                _primaryButton.Enabled = true;
                _secondaryButton.Visible = false;
                LayoutFooter();
                return;
            }

            // 确认页
            _primaryButton.Width = 140;
            _primaryButton.Style = Ctl.FlatButtonStyle.Danger;
            _primaryButton.Visible = true;
            _secondaryButton.Visible = true;
            LayoutFooter();
            UpdateConfirmFooterState();
            LayoutConfirmBody();
        }

        private void UpdateConfirmFooterState()
        {
            if (_primaryButton == null) return;

            if (_target == null)
            {
                _primaryButton.Text = "开始卸载";
                _primaryButton.Enabled = false;
                _footerHint.Text = "正在分析安装信息…";
                _footerHint.ForeColor = Theme.TextSecondary;
                return;
            }

            bool hasErrors = _target.Errors != null && _target.Errors.Count > 0;

            if (_target.IsProtected)
            {
                _primaryButton.Text = "无法卸载";
                _primaryButton.Enabled = false;
                _footerHint.Text = "这个软件受系统保护，不能在这里卸载。";
                _footerHint.ForeColor = Theme.Danger;
            }
            else if (hasErrors)
            {
                _primaryButton.Text = "开始卸载";
                _primaryButton.Enabled = false;
                _footerHint.Text = "请先解决上面的问题再卸载。";
                _footerHint.ForeColor = Theme.Danger;
            }
            else if (_target.Method == UninstallMethod.ManualOnly && _chkManual != null && !_chkManual.Checked)
            {
                _primaryButton.Text = "开始卸载";
                _primaryButton.Enabled = false;
                _footerHint.Text = "这个软件没有官方卸载程序，勾选「我确认手工清理」后才能继续。";
                _footerHint.ForeColor = Theme.Warning;
            }
            else
            {
                _primaryButton.Text = "开始卸载";
                _primaryButton.Enabled = true;
                _footerHint.Text = "卸载无法撤销，勾选的还原点和注册表备份可用于回退。";
                _footerHint.ForeColor = Theme.TextMuted;
            }
        }

        private void PrimaryButton_Click()
        {
            if (_state == StateConfirm) StartUninstall();
            else if (_state == StateProgress) RequestCancel();
            else CloseWithOk();
        }

        private void CloseWithOk()
        {
            if (_running)
            {
                RequestCancel();
                return;
            }
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        // ------------------------------------------------------------------
        // 预检
        // ------------------------------------------------------------------

        private void StartPrecheck()
        {
            if (_precheckActive || _closing || _previewDone) return;
            _precheckActive = true;
            _analyzingLabel.Visible = true;
            _analyzingBar.Visible = true;
            _analyzingBar.IsIndeterminate = true;
            LayoutConfirmBody();
            UpdateConfirmFooterState();

            Thread worker = new Thread(PrecheckWorker);
            worker.IsBackground = true;
            worker.Name = "UninstallPrecheck";
            worker.Start();
        }

        private void PrecheckWorker()
        {
            UninstallTarget target = null;
            string error = null;
            try
            {
                target = UninstallEngine.Precheck(_program);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (target == null && error == null) error = "卸载引擎没有返回预检结果。";
            SafeUi(delegate { FinishPrecheck(target, error); });
        }

        private void FinishPrecheck(UninstallTarget target, string error)
        {
            _precheckActive = false;
            if (_closing) return;

            if (target == null)
            {
                target = new UninstallTarget();
                if (_program != null)
                {
                    target.DisplayName = _program.DisplayName;
                    target.InstallPath = _program.InstallPath;
                }
                if (string.IsNullOrEmpty(error)) error = "无法读取卸载信息。";
                target.Errors.Add(error);
            }
            ApplyTarget(target);
        }

        // ------------------------------------------------------------------
        // 确认页数据填充
        // ------------------------------------------------------------------

        private void ApplyTarget(UninstallTarget target)
        {
            if (target == null) return;
            _target = target;

            string name = target.DisplayName;
            if (string.IsNullOrEmpty(name) && _program != null) name = _program.DisplayName;
            if (string.IsNullOrEmpty(name)) name = "未知软件";
            _titleLabel.Text = "卸载 " + name;

            string sub = string.Empty;
            if (_program != null)
            {
                if (!string.IsNullOrEmpty(_program.Version)) sub = "版本 " + _program.Version;
                if (!string.IsNullOrEmpty(_program.Publisher))
                {
                    if (sub.Length > 0) sub += "  ·  ";
                    sub += _program.Publisher;
                }
            }
            _subTitleLabel.Text = sub;
            _iconBox.FallbackText = name.Length > 0 ? name.Substring(0, 1) : "?";

            string installPath = target.InstallPath;
            if (string.IsNullOrEmpty(installPath) && _program != null) installPath = _program.InstallPath;
            if (string.IsNullOrEmpty(installPath))
            {
                _pathValue.Text = "（未知）";
                _tips.SetToolTip(_pathValue, "没有找到安装目录。");
            }
            else
            {
                _pathValue.Text = PathUtil.Shorten(installPath, 96);
                _tips.SetToolTip(_pathValue, installPath);
            }

            // 目录联接提示
            if (target.InstallPathIsJunction)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("该软件之前被搬到其他盘，卸载会同时清理目录联接和真实目录。");
                if (!string.IsNullOrEmpty(target.LinkPath)) sb.Append("\r\n目录联接：" + target.LinkPath);
                if (!string.IsNullOrEmpty(target.InstallPath)) sb.Append("\r\n真实目录：" + target.InstallPath);
                _junctionNotice.Configure(Ctl.NoticeKind.Warning, "安装目录是目录联接", sb.ToString());
            }
            _junctionNotice.Visible = target.InstallPathIsJunction;
            _wantJunction = target.InstallPathIsJunction;
            _wantManual = target.Method == UninstallMethod.ManualOnly;
            if (_chkManual != null)
            {
                _chkManual.Visible = _wantManual;
                _chkManual.Checked = false;
            }

            string sizeText = target.InstallBytes >= 0 ? TextUtil.FormatBytes(target.InstallBytes) : "未知";
            string countText = target.FileCount > 0
                ? "  ·  " + target.FileCount.ToString("N0") + " 个文件"
                : string.Empty;
            _sizeValue.Text = sizeText + countText;

            _methodValue.Text = MethodText(target.Method);
            bool manual = target.Method == UninstallMethod.ManualOnly || target.Method == UninstallMethod.None;
            _methodValue.ForeColor = manual ? Theme.Danger : Theme.TextPrimary;
            string command = target.Command;
            if (string.IsNullOrEmpty(command)) command = _methodValue.Text;
            _tips.SetToolTip(_methodValue, command);

            _stepsLabel.Text = BuildStepsText(target);
            _cleanupLabel.Text = BuildCleanupText(target);

            ApplyLocks(target);
            ApplyCheckBoxes(target);
            ApplyNotices(target);

            _analyzingLabel.Visible = false;
            _analyzingBar.Visible = false;
            _analyzingBar.IsIndeterminate = false;
            _wantAnalyzing = false;

            LayoutConfirmBody();
            UpdateConfirmFooterState();
        }

        private void ApplyLocks(UninstallTarget target)
        {
            List<LockingProcess> processes = null;
            if (target.Locks != null) processes = target.Locks.Processes;
            bool has = processes != null && processes.Count > 0;
            if (has)
            {
                StringBuilder sb = new StringBuilder("正在占用：");
                for (int i = 0; i < processes.Count && i < 5; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append(processes[i].ProcessName);
                }
                if (processes.Count > 5) sb.Append(" 等 " + processes.Count + " 个进程");
                _locksLabel.Text = sb.ToString();
                _tips.SetToolTip(_locksLabel, _locksLabel.Text);
            }
            _locksLabel.Visible = has;
            _wantLocks = has;
        }

        private void ApplyCheckBoxes(UninstallTarget target)
        {
            List<string> folders = target.DataFolders;
            bool hasData = folders != null && folders.Count > 0;

            _chkUserData.Enabled = true;
            _chkUserData.Checked = false;

            StringBuilder sb = new StringBuilder("将删除：");
            if (hasData)
            {
                for (int i = 0; i < folders.Count && i < 3; i++)
                {
                    if (i > 0) sb.Append("；");
                    sb.Append(PathUtil.Shorten(folders[i], 48));
                }
                if (folders.Count > 3) sb.Append(" 等 " + folders.Count + " 个目录");
            }
            else
            {
                sb.Append("没有找到用户数据目录。");
            }
            _dataLabel.Text = sb.ToString();
            _dataLabel.Visible = false;
            _wantData = false;
            _tips.SetToolTip(_chkUserData, hasData
                ? "勾选后会删除这里的用户配置和数据，无法撤销。"
                : "没有检测到用户数据目录。");
        }

        private void OnUserDataCheckedChanged()
        {
            if (_dataLabel == null) return;
            _dataLabel.Visible = _chkUserData.Checked && _target != null;
            _wantData = _dataLabel.Visible;
            LayoutConfirmBody();
        }

        private void ApplyNotices(UninstallTarget target)
        {
            // 警告
            if (target.Warnings != null && target.Warnings.Count > 0)
            {
                _warnNotice.Configure(Ctl.NoticeKind.Warning, "卸载前请注意", JoinLines(target.Warnings));
                _warnNotice.Visible = true;
                _wantWarn = true;
            }
            else
            {
                _warnNotice.Visible = false;
                _wantWarn = false;
            }

            // 错误 / 受保护
            List<string> problems = new List<string>();
            if (target.IsProtected)
            {
                string reason = target.ProtectedReason;
                if (string.IsNullOrEmpty(reason)) reason = "这是系统受保护的程序，不能在这里卸载。";
                problems.Add(reason);
            }
            if (target.Errors != null)
            {
                for (int i = 0; i < target.Errors.Count; i++) problems.Add(target.Errors[i]);
            }
            if (problems.Count > 0)
            {
                string title = target.IsProtected ? "无法卸载这个软件" : "卸载前需要先解决问题";
                _errorNotice.Configure(Ctl.NoticeKind.Danger, title, JoinLines(problems));
                _errorNotice.Visible = true;
                _wantError = true;
            }
            else
            {
                _errorNotice.Visible = false;
                _wantError = false;
            }
        }

        private static string JoinLines(List<string> items)
        {
            if (items == null || items.Count == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < items.Count && i < 6; i++)
            {
                if (i > 0) sb.Append("\r\n");
                sb.Append("· ").Append(items[i]);
            }
            if (items.Count > 6) sb.Append("\r\n· 还有 " + (items.Count - 6) + " 条…");
            return sb.ToString();
        }

        private static string MethodText(UninstallMethod method)
        {
            if (method == UninstallMethod.MsiProduct) return "使用 Windows 安装服务（MSI）卸载";
            if (method == UninstallMethod.RegistryCommand) return "运行软件自带的卸载程序";
            if (method == UninstallMethod.ManualOnly) return "⚠ 未找到卸载程序，只能直接删除文件";
            return "⚠ 没有找到可用的卸载方式";
        }

        private static string BuildStepsText(UninstallTarget target)
        {
            List<string> steps = new List<string>();
            if (target.Steps != null && target.Steps.Count > 0)
            {
                steps.AddRange(target.Steps);
            }
            else if (target.Method == UninstallMethod.MsiProduct)
            {
                steps.Add("调用 Windows 安装服务（msiexec）执行卸载");
                steps.Add("等待卸载结束并检查退出码");
                steps.Add("清理残留文件、快捷方式与注册表项");
            }
            else if (target.Method == UninstallMethod.RegistryCommand)
            {
                steps.Add("运行软件自带的卸载程序");
                steps.Add("等待卸载窗口中的操作完成");
                steps.Add("清理残留文件、快捷方式与注册表项");
            }
            else if (target.Method == UninstallMethod.ManualOnly)
            {
                steps.Add("直接删除安装目录中的全部文件");
                steps.Add("清理快捷方式与注册表项");
            }
            else
            {
                steps.Add("没有找到可用的卸载方式");
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < steps.Count; i++)
            {
                if (i > 0) sb.Append("\r\n");
                sb.Append(i + 1).Append(". ").Append(steps[i]);
            }
            return sb.ToString();
        }

        private static string BuildCleanupText(UninstallTarget target)
        {
            int shortcuts = target.Shortcuts == null ? 0 : target.Shortcuts.Count;
            int registry = target.RegistryEntries == null ? 0 : target.RegistryEntries.Count;
            int folders = target.DataFolders == null ? 0 : target.DataFolders.Count;
            string text = "安装目录 1 个  ·  快捷方式 " + shortcuts + " 个  ·  注册表项 " + registry + " 项";
            if (folders > 0) text += "  ·  用户数据目录 " + folders + " 个（默认不删）";
            return text;
        }

        // ------------------------------------------------------------------
        // 执行卸载
        // ------------------------------------------------------------------

        private void StartUninstall()
        {
            if (_target == null || _running) return;
            bool blocked = _target.IsProtected || (_target.Errors != null && _target.Errors.Count > 0);
            if (blocked) return;
            // 没有官方卸载程序时，没有这句显式确认就绝不删除任何文件
            if (_target.Method == UninstallMethod.ManualOnly && (_chkManual == null || !_chkManual.Checked)) return;

            _request = new UninstallRequest();
            _request.Target = _target;
            _request.CloseRunningPrograms = _chkClose.Checked;
            _request.RemoveLeftovers = _chkLeftovers.Checked;
            _request.RemoveUserData = _chkUserData.Checked;
            _request.CreateRestorePoint = _chkRestore.Checked;
            _request.BackupRegistry = true;
            _request.SilentWhenPossible = true;
            _request.ConfirmManualDelete = _chkManual != null && _chkManual.Checked;

            _cancelRequested = false;
            _running = true;
            _activeStage = -1;
            _stageList.ResetStates();
            _stageList.SetStages(BuildStageTitles());
            _progressBar.IsIndeterminate = true;
            _progressBar.Value = 0;
            _stageText.Text = "正在准备…";
            _percentText.Text = "…";
            _currentItem.Text = string.Empty;
            _waitingHint.Visible = false;
            _progressLayout.RowStyles[5].Height = 0;
            _progressHint.Text = "卸载过程中请不要关闭软件窗口；点击「取消」会等待当前步骤结束后停下。";

            ShowState(StateProgress);

            Thread worker = new Thread(ExecuteWorker);
            worker.IsBackground = true;
            worker.Name = "UninstallExecute";
            worker.Start();
        }

        private void ExecuteWorker()
        {
            UninstallResult result = null;
            try
            {
                result = UninstallEngine.Execute(_request, OnEngineProgress, IsCancelRequested);
            }
            catch (Exception ex)
            {
                result = new UninstallResult();
                result.FinalStage = UninstallStage.Failed;
                result.ErrorMessage = "卸载过程出现异常：" + ex.Message;
                result.Summary = result.ErrorMessage;
                if (_target != null) result.DisplayName = _target.DisplayName;
            }

            if (result == null)
            {
                result = new UninstallResult();
                result.FinalStage = UninstallStage.Failed;
                result.ErrorMessage = "卸载引擎没有返回结果。";
                result.Summary = result.ErrorMessage;
            }

            SafeUi(delegate { FinishExecute(result); });
        }

        private bool IsCancelRequested()
        {
            return _cancelRequested;
        }

        private void OnEngineProgress(UninstallProgress progress)
        {
            if (progress == null || _closing) return;
            SafeUi(delegate { ApplyProgress(progress); });
        }

        private void ApplyProgress(UninstallProgress progress)
        {
            if (progress == null || _state != StateProgress) return;

            int index = StageIndex(progress.Stage);
            if (index >= 0)
            {
                if (index != _activeStage)
                {
                    if (_activeStage >= 0 && _activeStage < index)
                    {
                        _stageList.SetStageState(_activeStage, Ctl.StageList.StateDone);
                    }
                    _activeStage = index;
                    _stageList.SetCurrent(index);
                }
                if (!string.IsNullOrEmpty(progress.StageText)) _stageText.Text = progress.StageText;
                else _stageText.Text = BuildStageTitles()[index];
            }
            else if (!string.IsNullOrEmpty(progress.StageText))
            {
                _stageText.Text = progress.StageText;
            }

            if (progress.Percent >= 0)
            {
                _progressBar.IsIndeterminate = false;
                _progressBar.Value = progress.Percent;
                _percentText.Text = progress.Percent + "%";
            }
            else
            {
                _progressBar.IsIndeterminate = true;
                _percentText.Text = "…";
            }

            string item = progress.CurrentItem;
            if (string.IsNullOrEmpty(item)) item = progress.Message;
            if (string.IsNullOrEmpty(item))
            {
                _currentItem.Text = string.Empty;
                _tips.SetToolTip(_currentItem, string.Empty);
            }
            else
            {
                _currentItem.Text = PathUtil.Shorten(item, 96);
                _tips.SetToolTip(_currentItem, item);
            }

            bool waiting = progress.WaitingForUser;
            if (waiting)
            {
                _waitingHint.Configure(Ctl.NoticeKind.Info, "需要在卸载窗口里操作",
                    "请在弹出的卸载窗口中完成操作，完成后回到这里。");
            }
            _waitingHint.Visible = waiting;
            _progressLayout.RowStyles[5].Height = waiting ? 62 : 0;
        }

        private void FinishExecute(UninstallResult result)
        {
            _running = false;
            _request = null;

            if (result != null)
            {
                if (result.FinalStage == UninstallStage.Completed || result.FinalStage == UninstallStage.PartialSuccess)
                {
                    _stageList.MarkAllDone();
                    _progressBar.IsIndeterminate = false;
                    _progressBar.Value = 100;
                    _percentText.Text = "100%";
                }
                else
                {
                    int index = _activeStage < 0 ? 0 : _activeStage;
                    _stageList.SetStageState(index, Ctl.StageList.StateFailed);
                }
            }

            ShowResultPage(result);
        }

        private void RequestCancel()
        {
            if (!_running || _cancelRequested) return;
            _cancelRequested = true;
            _primaryButton.Enabled = false;
            _primaryButton.Text = "正在取消…";
            _footerHint.Text = "已请求取消，正在等待当前步骤结束…";
            _footerHint.ForeColor = Theme.Warning;
            LayoutFooter();
        }

        // ------------------------------------------------------------------
        // 结果页
        // ------------------------------------------------------------------

        private void ShowResultPage(UninstallResult result)
        {
            _result = result;
            ShowState(StateResult);

            bool success = result != null && result.Success;
            bool partial = result != null && !result.Success && result.PartialSuccess;

            if (success)
            {
                string sub;
                if (result.FreedBytes > 0) sub = "释放了 " + TextUtil.FormatBytes(result.FreedBytes);
                else sub = "卸载已完成，空间变化未知";
                _banner.SetState(Ctl.BannerState.Success, "已卸载", sub);
                _primaryButton.Text = "完成";
                _footerHint.Text = "改动已经生效，可以关闭这个窗口。";
                _footerHint.ForeColor = Theme.TextMuted;
            }
            else if (partial)
            {
                int count = result.Leftovers == null ? 0 : result.Leftovers.Count;
                string sub;
                if (count > 0) sub = "还有 " + count + " 项没有清理干净";
                else if (!string.IsNullOrEmpty(result.Summary)) sub = result.Summary;
                else sub = "卸载完成，但有残留";
                _banner.SetState(Ctl.BannerState.Partial, "卸载完成，但有残留", sub);
                _primaryButton.Text = "关闭";
                _footerHint.Text = "残留可以手工删除，或稍后重新卸载。";
                _footerHint.ForeColor = Theme.TextMuted;
            }
            else
            {
                string message = result == null ? string.Empty : result.ErrorMessage;
                if (string.IsNullOrEmpty(message)) message = "卸载没有完成。";
                _banner.SetState(Ctl.BannerState.Failed, "卸载失败", PathUtil.Shorten(message, 92));
                _primaryButton.Text = "关闭";
                _footerHint.Text = "卸载没有完成，可以关闭窗口后重试。";
                _footerHint.ForeColor = Theme.Danger;
            }

            _statExit.Text = result != null && result.UninstallerRan
                ? result.UninstallerExitCode.ToString()
                : "未运行";
            _statItems.Text = result == null || result.RemovedItems == null
                ? "0"
                : result.RemovedItems.Count.ToString();
            _statShortcuts.Text = (result == null ? 0 : result.ShortcutsRemoved) + " 个";
            _statRegistry.Text = result != null && result.RegistryCleaned ? "已清理" : "未清理";
            _statFolder.Text = result != null && result.InstallDirRemoved ? "已删除" : "未删除";

            ApplyLeftovers(result);
            ApplyFailureLog(result, success, partial);

            LayoutFooter();
            _resultPanel.PerformLayout();
        }

        private void ApplyLeftovers(UninstallResult result)
        {
            List<string> leftovers = result == null ? null : result.Leftovers;
            bool has = leftovers != null && leftovers.Count > 0;
            if (!has)
            {
                _leftoverTitle.Visible = false;
                _leftoverLabel.Visible = false;
                return;
            }

            StringBuilder sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < leftovers.Count && shown < 5; i++)
            {
                if (shown > 0) sb.Append("\r\n");
                sb.Append("· ").Append(PathUtil.Shorten(leftovers[i], 76));
                shown++;
            }
            if (leftovers.Count > shown) sb.Append("\r\n· 还有 " + (leftovers.Count - shown) + " 项…");
            _leftoverLabel.Text = sb.ToString();
            _tips.SetToolTip(_leftoverLabel, JoinLines(leftovers));
            _leftoverTitle.Text = "残留项（" + leftovers.Count + " 项）";
            _leftoverTitle.Visible = true;
            _leftoverLabel.Visible = true;
        }

        private void ApplyFailureLog(UninstallResult result, bool success, bool partial)
        {
            string path = result == null ? null : result.FailureLogPath;
            bool show = !success && !partial && !string.IsNullOrEmpty(path);
            _logButton.Visible = show;
            _logHint.Visible = show;
        }

        private void OpenFailureLog()
        {
            string path = _result == null ? null : _result.FailureLogPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, "日志文件已经不存在了：\r\n" + path, "查看失败诊断日志",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开日志文件。\r\n" + ex.Message, "查看失败诊断日志",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------------
        // 阶段映射
        // ------------------------------------------------------------------

        private static string[] BuildStageTitles()
        {
            return new string[]
            {
                "分析安装信息",
                "备份注册表与还原点",
                "结束正在运行的程序",
                "运行官方卸载程序",
                "清理残留文件",
                "完成"
            };
        }

        private static int StageIndex(UninstallStage stage)
        {
            if (stage == UninstallStage.Precheck) return 0;
            if (stage == UninstallStage.BackingUp) return 1;
            if (stage == UninstallStage.ClosingProgram) return 2;
            if (stage == UninstallStage.RunningUninstaller) return 3;
            if (stage == UninstallStage.WaitingForUninstaller) return 3;
            if (stage == UninstallStage.Verifying) return 4;
            if (stage == UninstallStage.CleaningLeftovers) return 4;
            if (stage == UninstallStage.Finalizing) return 5;
            return -1;
        }

        // ------------------------------------------------------------------
        // 线程安全的 UI 调度
        // ------------------------------------------------------------------

        private void SafeUi(Action action)
        {
            if (action == null) return;
            if (_closing || this.IsDisposed || this.Disposing) return;
            try
            {
                if (!this.IsHandleCreated) return;
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new MethodInvoker(delegate { SafeUi(action); }));
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                RequestCancel();
                _footerHint.Text = "卸载正在进行，已请求取消，正在等待当前步骤结束…";
                _footerHint.ForeColor = Theme.Warning;
                LayoutFooter();
                return;
            }
            if (_result != null) this.DialogResult = DialogResult.OK;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closing = true;
            if (_tips != null)
            {
                try
                {
                    _tips.Dispose();
                }
                catch (Exception)
                {
                }
                _tips = null;
            }
            base.OnFormClosed(e);
        }

        // ------------------------------------------------------------------
        // 预览入口（只给渲染脚本反射调用，正常运行时不会被触发）
        // ------------------------------------------------------------------

        /// <summary>1=确认页 2=进度页 3=结果页（部分完成）。</summary>
        internal void PreviewShowState(int state)
        {
            _previewDone = true;
            if (state == 2)
            {
                PreviewProgressDemo();
                return;
            }
            if (state == 3)
            {
                PreviewResultDemo();
                return;
            }
            PreviewConfirmDemo();
        }

        internal void PreviewConfirmDemo()
        {
            _previewDone = true;

            UninstallTarget target = new UninstallTarget();
            target.DisplayName = "Xmind";
            target.InstallPath = @"D:\Software\Xmind";
            target.LinkPath = @"C:\Program Files\Xmind";
            target.InstallPathIsJunction = true;
            target.InstallBytes = 404750336;
            target.FileCount = 3641;
            target.Method = UninstallMethod.RegistryCommand;
            target.Command = "\"C:\\Program Files\\Xmind\\uninstall.exe\" /S";
            target.UninstallerExe = @"C:\Program Files\Xmind\uninstall.exe";
            target.CanProceed = true;
            target.Steps.Add("运行 Xmind 自带的卸载程序");
            target.Steps.Add("等待卸载窗口中的操作完成");
            target.Steps.Add("清理残留文件、快捷方式与注册表项");
            target.Steps.Add("删除目录联接并核对真实目录");
            target.Shortcuts.Add(@"C:\Users\Public\Desktop\Xmind.lnk");
            target.Shortcuts.Add(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Xmind.lnk");
            target.Shortcuts.Add(@"C:\Users\Demo\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Xmind.lnk");
            target.RegistryEntries.Add(new UninstallRegistryEntry());
            target.RegistryEntries.Add(new UninstallRegistryEntry());
            target.RegistryEntries.Add(new UninstallRegistryEntry());
            target.RegistryEntries.Add(new UninstallRegistryEntry());
            target.DataFolders.Add(@"C:\Users\Demo\AppData\Roaming\Xmind");
            target.DataFolders.Add(@"C:\Users\Demo\AppData\Local\Xmind");
            target.Warnings.Add("安装目录中有 2 个文件正被其他程序引用，卸载后可能留下空目录。");

            LockInfo locks = new LockInfo();
            LockingProcess p1 = new LockingProcess();
            p1.ProcessId = 4128;
            p1.ProcessName = "Xmind.exe";
            p1.HasMainWindow = true;
            LockingProcess p2 = new LockingProcess();
            p2.ProcessId = 6972;
            p2.ProcessName = "XmindUpdater.exe";
            p2.HasMainWindow = false;
            locks.Processes.Add(p1);
            locks.Processes.Add(p2);
            target.Locks = locks;

            ApplyTarget(target);
            ShowState(StateConfirm);
        }

        internal void PreviewProgressDemo()
        {
            _previewDone = true;
            ShowState(StateProgress);
            _stageList.SetStages(BuildStageTitles());
            _stageList.ResetStates();
            for (int i = 0; i < 3; i++) _stageList.SetStageState(i, Ctl.StageList.StateDone);
            _stageList.SetCurrent(3);
            _stageText.Text = "正在运行官方卸载程序";
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 45;
            _percentText.Text = "45%";
            _currentItem.Text = "Xmind.exe 正在卸载，请稍候…";
            _tips.SetToolTip(_currentItem, "Xmind.exe 正在卸载，请稍候…");
            _waitingHint.Visible = true;
            _waitingHint.Configure(Ctl.NoticeKind.Info, "需要在卸载窗口里操作",
                "请在弹出的卸载窗口中完成操作，完成后回到这里。");
            _progressLayout.RowStyles[5].Height = 62;
            _footerHint.Text = "卸载过程中请不要关闭软件窗口，也不要强行结束卸载程序。";
        }

        internal void PreviewResultDemo()
        {
            _previewDone = true;

            UninstallTarget target = new UninstallTarget();
            target.DisplayName = "Xmind";
            _target = target;

            UninstallResult result = new UninstallResult();
            result.DisplayName = "Xmind";
            result.Success = false;
            result.PartialSuccess = true;
            result.FinalStage = UninstallStage.PartialSuccess;
            result.UninstallerRan = true;
            result.UninstallerExitCode = 0;
            result.InstallDirRemoved = true;
            result.RegistryCleaned = true;
            result.ShortcutsRemoved = 3;
            result.FreedBytes = 404750336;
            result.RemovedItems.Add(@"D:\Software\Xmind");
            result.RemovedItems.Add(@"C:\Program Files\Xmind（目录联接）");
            result.RemovedItems.Add(@"C:\Users\Public\Desktop\Xmind.lnk");
            result.Leftovers.Add(@"C:\ProgramData\Xmind\cache 下的 2 个临时文件");
            result.Leftovers.Add(@"开始菜单里残留的快捷方式 Xmind.lnk");
            result.Summary = "卸载完成，有 2 项残留";

            ShowResultPage(result);
        }
    }
}
