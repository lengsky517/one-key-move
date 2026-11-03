// T3 拥有：主窗口 - 第 3 步「开始搬移」进度、日志与成功/失败结果页
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
    public partial class MainForm
    {
        private static readonly string[] StageTitles = new string[]
        {
            "预检", "处理占用", "准备", "复制文件", "校验",
            "删除原目录", "创建目录联接", "更新快捷方式与注册表", "完成"
        };

        private Panel _progressView;
        private Panel _resultView;
        private Panel _resultScroll;
        private Ctl.StageList _stageList;
        private Label _stageTextLabel;
        private Label _percentLabel;
        private Ctl.ProgressBarEx _progressBar;
        private Label _bytesLabel;
        private Label _currentFileLabel;
        private Ctl.BorderedPanel _logHost;
        private TextBox _logBox;
        private Ctl.FlatButton _cancelButton;
        private Ctl.FlatButton _openButton;
        private Ctl.FlatButton _viewLogButton;
        private Ctl.FlatButton _viewFailureLogButton;
        private Ctl.FlatButton _retryButton;
        private Ctl.FlatButton _undoButton;
        private Ctl.FlatButton _finishButton;
        private Ctl.ResultBanner _resultBanner;
        private Ctl.NoticePanel _failureNotice;
        private Ctl.NoticePanel _resultWarnings;
        private Ctl.NoticePanel _pendingNotice;
        private Label[] _summaryValues;
        private int _resultNoticesWidth = -1;
        private int _lastStageIndex = -1;
        private string _lastStageText = string.Empty;
        private bool _undoRunning;

        private volatile bool _cancelRequested;

        // ------------------------------------------------------------ 界面构建

        private void BuildPage3()
        {
            _page3 = new Panel();
            _page3.Dock = DockStyle.Fill;
            _page3.BackColor = Theme.Surface;

            _progressView = new Panel();
            _progressView.Dock = DockStyle.Fill;
            _progressView.BackColor = Theme.Surface;
            _progressView.Controls.Add(BuildProgressLayout());

            _resultView = new Panel();
            _resultView.Dock = DockStyle.Fill;
            _resultView.BackColor = Theme.Surface;
            _resultView.Visible = false;
            _resultView.Controls.Add(BuildResultLayout());
            _resultView.Resize += delegate { LayoutResultNotices(); };

            _page3.Controls.Add(_progressView);
            _page3.Controls.Add(_resultView);
            _pageHost.Controls.Add(_page3);

            _stageList.SetStages(StageTitles);
        }

        private Control BuildProgressLayout()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 11;
            layout.BackColor = Theme.Surface;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            _stageList = new Ctl.StageList();
            _stageList.Dock = DockStyle.Fill;
            _stageList.Margin = new Padding(0);
            layout.Controls.Add(_stageList, 0, 0);

            TableLayoutPanel stageRow = new TableLayoutPanel();
            stageRow.Dock = DockStyle.Fill;
            stageRow.Margin = new Padding(0);
            stageRow.ColumnCount = 2;
            stageRow.RowCount = 1;
            stageRow.BackColor = Theme.Surface;
            stageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            stageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            stageRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _stageTextLabel = new Label();
            _stageTextLabel.AutoSize = false;
            _stageTextLabel.Dock = DockStyle.Fill;
            _stageTextLabel.Font = Theme.BodyBold;
            _stageTextLabel.ForeColor = Theme.Primary;
            _stageTextLabel.BackColor = Theme.Surface;
            _stageTextLabel.TextAlign = ContentAlignment.MiddleLeft;
            _stageTextLabel.Text = "准备开始…";
            stageRow.Controls.Add(_stageTextLabel, 0, 0);

            _percentLabel = new Label();
            _percentLabel.AutoSize = false;
            _percentLabel.Dock = DockStyle.Fill;
            _percentLabel.Font = Theme.BodyBold;
            _percentLabel.ForeColor = Theme.TextPrimary;
            _percentLabel.BackColor = Theme.Surface;
            _percentLabel.TextAlign = ContentAlignment.MiddleRight;
            _percentLabel.Text = "0%";
            stageRow.Controls.Add(_percentLabel, 1, 0);
            layout.Controls.Add(stageRow, 0, 2);

            _progressBar = new Ctl.ProgressBarEx();
            _progressBar.Dock = DockStyle.Fill;
            _progressBar.Margin = new Padding(0);
            _progressBar.Height = 10;
            _progressBar.BackColor = Theme.Surface;
            layout.Controls.Add(_progressBar, 0, 4);

            _bytesLabel = new Label();
            _bytesLabel.AutoSize = false;
            _bytesLabel.Dock = DockStyle.Fill;
            _bytesLabel.Margin = new Padding(0);
            _bytesLabel.Font = Theme.Small;
            _bytesLabel.ForeColor = Theme.TextSecondary;
            _bytesLabel.BackColor = Theme.Surface;
            _bytesLabel.TextAlign = ContentAlignment.MiddleLeft;
            _bytesLabel.Text = "正在准备…";
            layout.Controls.Add(_bytesLabel, 0, 5);

            _currentFileLabel = new Label();
            _currentFileLabel.AutoSize = false;
            _currentFileLabel.Dock = DockStyle.Fill;
            _currentFileLabel.Margin = new Padding(0);
            _currentFileLabel.Font = Theme.Mono;
            _currentFileLabel.ForeColor = Theme.TextMuted;
            _currentFileLabel.BackColor = Theme.Surface;
            _currentFileLabel.TextAlign = ContentAlignment.MiddleLeft;
            _currentFileLabel.AutoEllipsis = true;
            layout.Controls.Add(_currentFileLabel, 0, 6);

            _logHost = new Ctl.BorderedPanel();
            _logHost.Dock = DockStyle.Fill;
            _logHost.Margin = new Padding(0);
            _logHost.CornerRadius = 10;
            _logHost.FillColor = Theme.OptionBackground;
            _logHost.BackdropColor = Theme.Surface;
            _logHost.BorderColor = Theme.Border;
            _logHost.Padding = new Padding(10, 8, 6, 8);
            layout.Controls.Add(_logHost, 0, 8);

            _logBox = new TextBox();
            _logBox.Dock = DockStyle.Fill;
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.BorderStyle = BorderStyle.None;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.Font = Theme.Mono;
            _logBox.ForeColor = Theme.TextSecondary;
            _logBox.BackColor = Theme.OptionBackground;
            _logBox.WordWrap = false;
            _logHost.Controls.Add(_logBox);

            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.Margin = new Padding(0);
            bottom.ColumnCount = 2;
            bottom.RowCount = 1;
            bottom.BackColor = Theme.Surface;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _cancelButton = new Ctl.FlatButton();
            _cancelButton.Text = "取消";
            _cancelButton.Style = Ctl.FlatButtonStyle.Secondary;
            _cancelButton.Font = Theme.BodyBold;
            _cancelButton.Size = new Size(100, 36);
            _cancelButton.Margin = new Padding(0, 6, 0, 6);
            _cancelButton.BackdropColor = Theme.Surface;
            _cancelButton.Enabled = false;
            _cancelButton.Click += delegate { OnCancelMove(); };
            bottom.Controls.Add(_cancelButton, 0, 0);

            Label hint = new Label();
            hint.AutoSize = false;
            hint.Dock = DockStyle.Fill;
            hint.Font = Theme.Small;
            hint.ForeColor = Theme.TextMuted;
            hint.BackColor = Theme.Surface;
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.Text = "搬移过程中请保持程序运行，不要关闭窗口";
            bottom.Controls.Add(hint, 1, 0);
            layout.Controls.Add(bottom, 0, 10);

            return layout;
        }

        private Control BuildResultLayout()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.BackColor = Theme.Surface;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

            // 内容区可滚动（窗口较小时也不会与底部按钮重叠）
            _resultScroll = new Panel();
            _resultScroll.Dock = DockStyle.Fill;
            _resultScroll.BackColor = Theme.Surface;
            _resultScroll.AutoScroll = true;
            _resultScroll.Resize += delegate { LayoutResultNotices(); };
            layout.Controls.Add(_resultScroll, 0, 0);

            TableLayoutPanel content = new TableLayoutPanel();
            content.Dock = DockStyle.Top;
            content.AutoSize = true;
            content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            content.ColumnCount = 1;
            content.RowCount = 5;
            content.BackColor = Theme.Surface;
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 192));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _resultScroll.Controls.Add(content);

            _resultBanner = new Ctl.ResultBanner();
            _resultBanner.Dock = DockStyle.Fill;
            _resultBanner.Margin = new Padding(0);
            content.Controls.Add(_resultBanner, 0, 0);

            _failureNotice = new Ctl.NoticePanel();
            _failureNotice.Dock = DockStyle.Top;
            _failureNotice.Margin = new Padding(0, 4, 0, 0);
            _failureNotice.BackdropColor = Theme.Surface;
            content.Controls.Add(_failureNotice, 0, 1);

            _summaryValues = new Label[8];
            string[] titles = new string[] { "原位置", "新位置", "释放 C 盘空间", "更新快捷方式", "更新注册表", "目录联接", "桌面快捷方式", "旧快捷方式" };
            TableLayoutPanel summary = new TableLayoutPanel();
            summary.Dock = DockStyle.Fill;
            summary.Margin = new Padding(0);
            summary.ColumnCount = 2;
            summary.RowCount = 8;
            summary.BackColor = Theme.Surface;
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 8; i++)
            {
                summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
                Label title = new Label();
                title.AutoSize = false;
                title.Dock = DockStyle.Fill;
                title.Font = Theme.Small;
                title.ForeColor = Theme.TextMuted;
                title.BackColor = Theme.Surface;
                title.TextAlign = ContentAlignment.MiddleLeft;
                title.Text = titles[i];
                summary.Controls.Add(title, 0, i);

                Label value = new Label();
                value.AutoSize = false;
                value.Dock = DockStyle.Fill;
                value.Font = i < 2 ? Theme.Small : Theme.Body;
                value.ForeColor = Theme.TextPrimary;
                value.BackColor = Theme.Surface;
                value.TextAlign = ContentAlignment.MiddleLeft;
                value.AutoEllipsis = true;
                summary.Controls.Add(value, 1, i);
                _summaryValues[i] = value;
            }
            content.Controls.Add(summary, 0, 2);

            _resultWarnings = new Ctl.NoticePanel();
            _resultWarnings.Dock = DockStyle.Top;
            _resultWarnings.Margin = new Padding(0, 8, 0, 0);
            _resultWarnings.BackdropColor = Theme.Surface;
            content.Controls.Add(_resultWarnings, 0, 3);

            _pendingNotice = new Ctl.NoticePanel();
            _pendingNotice.Dock = DockStyle.Top;
            _pendingNotice.Margin = new Padding(0, 8, 0, 0);
            _pendingNotice.BackdropColor = Theme.Surface;
            content.Controls.Add(_pendingNotice, 0, 4);

            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.Margin = new Padding(0);
            bottom.ColumnCount = 7;
            bottom.RowCount = 1;
            bottom.BackColor = Theme.Surface;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _openButton = CreateResultButton("打开新位置", Ctl.FlatButtonStyle.Secondary, 124);
            _openButton.Click += delegate { OpenNewLocation(); };
            bottom.Controls.Add(_openButton, 0, 0);

            _viewLogButton = CreateResultButton("查看日志", Ctl.FlatButtonStyle.Secondary, 108);
            _viewLogButton.Click += delegate { OpenLogFile(); };
            bottom.Controls.Add(_viewLogButton, 1, 0);

            _viewFailureLogButton = CreateResultButton("查看失败诊断日志", Ctl.FlatButtonStyle.Secondary, 156);
            _viewFailureLogButton.Click += delegate { OpenFailureLog(); };
            bottom.Controls.Add(_viewFailureLogButton, 2, 0);

            _retryButton = CreateResultButton("返回重试", Ctl.FlatButtonStyle.Secondary, 120);
            _retryButton.Click += delegate { RetryFromResult(); };
            bottom.Controls.Add(_retryButton, 4, 0);

            _undoButton = CreateResultButton("撤销这次搬移", Ctl.FlatButtonStyle.Danger, 148);
            _undoButton.Click += delegate { OnUndoClick(); };
            bottom.Controls.Add(_undoButton, 5, 0);

            _finishButton = CreateResultButton("完成", Ctl.FlatButtonStyle.Primary, 110);
            _finishButton.Click += delegate { FinishToStart(); };
            bottom.Controls.Add(_finishButton, 6, 0);

            layout.Controls.Add(bottom, 0, 2);
            return layout;
        }
        private Ctl.FlatButton CreateResultButton(string text, Ctl.FlatButtonStyle style, int width)
        {
            Ctl.FlatButton button = new Ctl.FlatButton();
            button.Text = text;
            button.Style = style;
            button.Font = Theme.BodyBold;
            button.Size = new Size(width, 38);
            button.Margin = new Padding(0, 11, 8, 11);
            button.BackdropColor = Theme.Surface;
            return button;
        }

        // ------------------------------------------------------------ 进度更新

        private void ResetProgressUi()
        {
            _progressView.Visible = true;
            _resultView.Visible = false;
            _stageList.ResetStates();
            _stageTextLabel.Text = "准备开始…";
            _percentLabel.Text = "0%";
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 0;
            _bytesLabel.Text = "正在准备…";
            _currentFileLabel.Text = string.Empty;
            _logBox.Text = string.Empty;
            _cancelButton.Enabled = true;
            _cancelButton.Text = "取消";
            _lastStageIndex = -1;
            _lastStageText = string.Empty;
            _cancelRequested = false;
            if (!_previewMode) _moveRunning = true;
        }

        private void StartMoveThread(MoveRequest request)
        {
            _cancelRequested = false;
            AppendLog("开始搬移：" + request.TargetDirectory);
            Thread worker = new Thread(() =>
            {
                MoveResult result = null;
                string error = null;
                try
                {
                    result = MoveEngine.Execute(request,
                        delegate(MoveProgress progress) { OnMoveProgressFromWorker(progress); },
                        delegate { return _cancelRequested; });
                }
                catch (NotImplementedException)
                {
                    error = "搬移模块暂不可用（后台模块尚未实现）";
                }
                catch (Exception ex)
                {
                    Log.Error("搬移过程异常", ex);
                    error = "搬移失败：" + ex.Message;
                }
                SafeUi(delegate { OnMoveThreadFinished(result, error); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void OnMoveProgressFromWorker(MoveProgress progress)
        {
            if (progress == null) return;
            SafeUi(delegate { ApplyProgress(progress); });
        }

        private static int MapStage(MoveStage stage)
        {
            switch (stage)
            {
                case MoveStage.Precheck: return 0;
                case MoveStage.ResolvingLocks: return 1;
                case MoveStage.WaitingForLock: return 1;
                case MoveStage.Preparing: return 2;
                case MoveStage.Copying: return 3;
                case MoveStage.Verifying: return 4;
                case MoveStage.RemovingSource: return 5;
                case MoveStage.CreatingJunction: return 6;
                case MoveStage.UpdatingReferences: return 7;
                case MoveStage.Finalizing: return 8;
                case MoveStage.Completed: return 8;
                case MoveStage.PendingReboot: return 8;
                default: return -1;
            }
        }

        private void ApplyProgress(MoveProgress progress)
        {
            int index = MapStage(progress.Stage);
            if (index >= 0)
            {
                if (progress.Stage == MoveStage.Completed) _stageList.MarkAllDone();
                else _stageList.SetCurrent(index);
                _lastStageIndex = index;
                string stageText = progress.StageText;
                if (string.IsNullOrEmpty(stageText) && index < StageTitles.Length) stageText = StageTitles[index];
                if (!string.IsNullOrEmpty(stageText)) _stageTextLabel.Text = stageText;
            }
            if (progress.Stage == MoveStage.Failed && _lastStageIndex >= 0)
            {
                _stageList.SetStageState(_lastStageIndex, Ctl.StageList.StateFailed);
            }
            if (progress.Stage == MoveStage.Canceled || progress.Stage == MoveStage.RolledBack)
            {
                _stageTextLabel.Text = "正在取消并回滚…";
            }

            int percent = progress.Percent;
            if (percent < 0 && progress.BytesTotal > 0)
            {
                percent = (int)(progress.BytesDone * 100 / progress.BytesTotal);
            }
            if (progress.Stage == MoveStage.Completed) percent = 100;
            if (percent >= 0)
            {
                if (_progressBar.IsIndeterminate) _progressBar.IsIndeterminate = false;
                _progressBar.Value = percent;
                _percentLabel.Text = percent + "%";
            }
            else
            {
                _progressBar.IsIndeterminate = true;
                _percentLabel.Text = "处理中…";
            }

            string bytes = string.Empty;
            if (progress.BytesTotal > 0)
            {
                bytes = "已复制 " + TextUtil.FormatBytes(progress.BytesDone) + " / " + TextUtil.FormatBytes(progress.BytesTotal);
                if (percent >= 0) bytes += " · " + percent + "%";
            }
            else if (progress.FilesTotal > 0)
            {
                bytes = "已处理 " + progress.FilesDone + " / " + progress.FilesTotal + " 个文件";
            }
            else if (progress.BytesDone > 0)
            {
                bytes = "已处理 " + TextUtil.FormatBytes(progress.BytesDone);
            }
            if (!string.IsNullOrEmpty(bytes)) _bytesLabel.Text = bytes;

            if (!string.IsNullOrEmpty(progress.CurrentFile))
            {
                _currentFileLabel.Text = "当前文件：" + PathUtil.Shorten(progress.CurrentFile, 96);
            }
            if (!string.IsNullOrEmpty(progress.Message) && progress.Message != progress.StageText)
            {
                AppendLog(progress.Message);
            }
            if (!string.IsNullOrEmpty(progress.StageText) && progress.StageText != _lastStageText)
            {
                _lastStageText = progress.StageText;
                AppendLog(progress.StageText);
            }
        }

        private void AppendLog(string message)
        {
            if (_logBox == null || string.IsNullOrEmpty(message)) return;
            try
            {
                if (_logBox.TextLength > 60000) _logBox.Text = string.Empty;
                string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
                _logBox.AppendText(line + "\r\n");
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
            catch { }
        }

        private void OnCancelMove()
        {
            if (!_moveRunning || _previewMode)
            {
                return;
            }
            _cancelRequested = true;
            _cancelButton.Enabled = false;
            _cancelButton.Text = "正在取消并回滚…";
            AppendLog("用户请求取消，正在取消并回滚…");
            _stageTextLabel.Text = "正在取消并回滚…";
            _progressBar.IsIndeterminate = true;
        }

        private void OnMoveThreadFinished(MoveResult result, string error)
        {
            _moveRunning = false;
            if (_cancelButton != null) _cancelButton.Enabled = false;
            if (result != null)
            {
                _moveResult = result;
                if (result.Success)
                {
                    ShowSuccess();
                    return;
                }
                string title = result.FinalStage == MoveStage.Canceled ? "已取消搬移" : "搬移失败";
                string message = result.ErrorMessage;
                if (string.IsNullOrEmpty(message))
                {
                    message = result.FinalStage == MoveStage.Canceled
                        ? "搬移已取消，文件已回滚到原位置。"
                        : "搬移过程中发生未知错误，请查看日志。";
                }
                ShowFailure(title, message, result);
                return;
            }
            ShowFailure("搬移失败", string.IsNullOrEmpty(error) ? "未知错误，请查看日志文件。" : error, null);
        }

        // ------------------------------------------------------------ 结果页

        private void ShowSuccess()
        {
            MoveResult result = _moveResult;
            _progressView.Visible = false;
            _resultView.Visible = true;
            ScrollResultToTop();
            _resultBanner.SetResult(Ctl.ResultBannerKind.Success, "搬移完成！",
                "软件已经成功搬到新位置，桌面快捷方式仍然可用");
            _failureNotice.Visible = false;

            string oldPath = result != null && !string.IsNullOrEmpty(result.OldPath) ? result.OldPath :
                (_selectedProgram != null ? _selectedProgram.InstallPath : string.Empty);
            string newPath = result != null && !string.IsNullOrEmpty(result.NewPath) ? result.NewPath :
                (_moveRequest != null ? _moveRequest.TargetDirectory : string.Empty);
            SetSummaryValue(0, oldPath);
            SetSummaryValue(1, newPath);
            SetSummaryValue(2, result != null && result.FreedBytes > 0 ? TextUtil.FormatBytes(result.FreedBytes) : "—");
            SetSummaryValue(3, result != null ? result.ShortcutsUpdated + " 个" : "—");
            SetSummaryValue(4, result != null ? result.RegistryEntriesUpdated + " 项" : "—");
            bool junctionRequested = _moveRequest == null || _moveRequest.CreateJunction;
            string junctionText;
            if (result != null && result.JunctionCreated) junctionText = "已创建（原路径仍然可用）";
            else if (junctionRequested) junctionText = "未创建";
            else junctionText = "未启用（原路径将失效）";
            SetSummaryValue(5, junctionText);

            bool updateShortcuts = _moveRequest == null || _moveRequest.UpdateShortcuts;
            string desktopShortcutText;
            if (!updateShortcuts)
            {
                desktopShortcutText = "未处理（搬移时关闭了快捷方式更新）";
            }
            else if (result != null && result.DesktopShortcutCreated)
            {
                desktopShortcutText = string.IsNullOrEmpty(result.DesktopShortcutPath)
                    ? "已重建"
                    : "已重建（" + result.DesktopShortcutPath + "）";
            }
            else
            {
                desktopShortcutText = "未创建（桌面没有对应快捷方式）";
            }
            SetSummaryValue(6, desktopShortcutText);

            int removed = result != null ? result.ShortcutsRemoved : 0;
            if (removed > 0) SetSummaryValue(7, "已清理失效旧快捷方式 " + removed + " 个");
            else SetSummaryValue(7, "—");
            // 清理行为中性灰字，弱化显示
            if (_summaryValues != null && _summaryValues[7] != null)
            {
                _summaryValues[7].ForeColor = Theme.TextSecondary;
                _summaryValues[6].ForeColor = Theme.TextPrimary;
            }

            SetResultWarnings(result != null ? result.Warnings : null);
            SetPendingNotice(result);
            SetResultButtons(true);
            LayoutResultNotices();
            AppendLog("搬移完成。新位置：" + newPath);
        }

        private void ShowFailure(string title, string message, MoveResult result)
        {
            _progressView.Visible = false;
            _resultView.Visible = true;
            ScrollResultToTop();
            _resultBanner.SetResult(Ctl.ResultBannerKind.Failure, title,
                "本次搬移没有完成，原文件已自动回滚到原位置");
            if (_lastStageIndex >= 0) _stageList.SetStageState(_lastStageIndex, Ctl.StageList.StateFailed);

            string errorText = string.IsNullOrEmpty(message) ? "未知错误，请查看日志文件。" : message;
            string failureLogPath = result != null ? result.FailureLogPath : null;
            if (!string.IsNullOrEmpty(failureLogPath))
            {
                errorText += "\r\n已生成诊断日志，反馈问题时可附上：" + failureLogPath;
            }
            _failureNotice.Configure(Ctl.NoticeKind.Danger, "失败原因", errorText);
            _failureNotice.Visible = true;

            string oldPath = result != null && !string.IsNullOrEmpty(result.OldPath) ? result.OldPath :
                (_selectedProgram != null ? _selectedProgram.InstallPath : string.Empty);
            string newPath = result != null && !string.IsNullOrEmpty(result.NewPath) ? result.NewPath :
                (_moveRequest != null ? _moveRequest.TargetDirectory : string.Empty);
            SetSummaryValue(0, oldPath);
            SetSummaryValue(1, newPath);
            SetSummaryValue(2, "—");
            SetSummaryValue(3, result != null && result.ShortcutsUpdated > 0 ? result.ShortcutsUpdated + " 个" : "—");
            SetSummaryValue(4, result != null && result.RegistryEntriesUpdated > 0 ? result.RegistryEntriesUpdated + " 项" : "—");
            SetSummaryValue(5, result != null && result.JunctionCreated ? "已创建" : "未创建");
            SetSummaryValue(6, "—");
            SetSummaryValue(7, result != null && result.ShortcutsRemoved > 0
                ? "已清理失效旧快捷方式 " + result.ShortcutsRemoved + " 个" : "—");
            if (_summaryValues != null && _summaryValues[7] != null) _summaryValues[7].ForeColor = Theme.TextSecondary;

            SetResultWarnings(result != null ? result.Warnings : null);
            SetPendingNotice(result);
            SetResultButtons(false);
            LayoutResultNotices();
            AppendLog(title + "：" + errorText);
        }

        private void ShowUndoSuccess(string oldPath)
        {
            _progressView.Visible = false;
            _resultView.Visible = true;
            ScrollResultToTop();
            _resultBanner.SetResult(Ctl.ResultBannerKind.Undo, "已撤销这次搬移",
                "软件已经回到原来的位置，快捷方式已恢复");
            _failureNotice.Visible = false;
            SetSummaryValue(0, oldPath);
            SetSummaryValue(1, "已恢复到原位置");
            SetSummaryValue(2, "—");
            SetSummaryValue(3, "—");
            SetSummaryValue(4, "—");
            SetSummaryValue(5, "—");
            SetSummaryValue(6, "—");
            SetSummaryValue(7, "—");
            SetResultWarnings(null);
            SetPendingNotice(null);
            _failureNotice.Visible = false;
            _resultWarnings.Visible = false;
            _pendingNotice.Visible = false;
            _openButton.Visible = true;
            _viewLogButton.Visible = false;
            _viewFailureLogButton.Visible = false;
            _retryButton.Visible = false;
            _undoButton.Visible = false;
            _finishButton.Visible = true;
            _finishButton.Text = "完成";
            LayoutResultNotices();
        }

        private void SetSummaryValue(int index, string text)
        {
            if (_summaryValues == null || index < 0 || index >= _summaryValues.Length) return;
            Label label = _summaryValues[index];
            if (label == null) return;
            label.Text = string.IsNullOrEmpty(text) ? "—" : text;
        }

        private void SetResultWarnings(List<string> warnings)
        {
            if (_resultWarnings == null) return;
            if (warnings == null || warnings.Count == 0)
            {
                _resultWarnings.Visible = false;
                return;
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < warnings.Count && i < 8; i++)
            {
                if (i > 0) sb.Append("\r\n");
                sb.Append("• ").Append(warnings[i]);
            }
            _resultWarnings.Configure(Ctl.NoticeKind.Warning, "以下提示供参考", sb.ToString());
            _resultWarnings.Visible = true;
        }

        private void SetPendingNotice(MoveResult result)
        {
            if (_pendingNotice == null) return;
            if (result == null || !result.PendingReboot)
            {
                _pendingNotice.Visible = false;
                return;
            }
            _pendingNotice.Configure(Ctl.NoticeKind.Info, "重启电脑后将自动完成",
                "部分文件被系统占用，已登记重启任务。重启电脑后会自动完成剩余步骤，无需重新操作。");
            _pendingNotice.Visible = true;
        }

        private void SetResultButtons(bool success)
        {
            _openButton.Visible = success;
            _viewLogButton.Visible = !success;
            _retryButton.Visible = !success;
            _failureNotice.Visible = !success;
            bool hasFailureLog = !success && _moveResult != null &&
                                 !string.IsNullOrEmpty(_moveResult.FailureLogPath);
            _viewFailureLogButton.Visible = hasFailureLog;
            _finishButton.Visible = true;
            _finishButton.Text = "完成";
            bool canUndo = false;
            if (success && _moveResult != null && !string.IsNullOrEmpty(_moveResult.JournalPath))
            {
                if (_previewMode)
                {
                    canUndo = true;
                }
                else
                {
                    try
                    {
                        string reason;
                        canUndo = MoveEngine.CanUndo(_moveResult.JournalPath, out reason);
                    }
                    catch
                    {
                        canUndo = true; // 无法判断时仍提供撤销入口（失败会给出提示）
                    }
                }
            }
            _undoButton.Visible = canUndo;
            _undoButton.Enabled = canUndo;
        }

        private void LayoutResultNotices()
        {
            if (_resultView == null) return;
            int width = _resultScroll != null ? _resultScroll.ClientSize.Width : _resultView.ClientSize.Width;
            if (width <= 0) return;
            if (width == _resultNoticesWidth) return;
            _resultNoticesWidth = width;
            if (_failureNotice != null) _failureNotice.SetLayoutWidth(width);
            if (_resultWarnings != null) _resultWarnings.SetLayoutWidth(width);
            if (_pendingNotice != null) _pendingNotice.SetLayoutWidth(width);
        }

        private void ScrollResultToTop()
        {
            try
            {
                if (_resultScroll != null) _resultScroll.AutoScrollPosition = new Point(0, 0);
            }
            catch { }
        }

        // ------------------------------------------------------------ 结果页操作

        private void OpenNewLocation()
        {
            string path = _moveResult != null && !string.IsNullOrEmpty(_moveResult.NewPath)
                ? _moveResult.NewPath
                : (_moveRequest != null ? _moveRequest.TargetDirectory : null);
            if (string.IsNullOrEmpty(path))
            {
                ShowInfoBox("没有可打开的位置。");
                return;
            }
            try
            {
                if (!Directory.Exists(path))
                {
                    ShowWarnBox("新位置文件夹不存在：\r\n" + path);
                    return;
                }
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                ShowErrorBox("打开位置失败", "无法打开文件夹：" + path + "\r\n" + ex.Message);
            }
        }

        private void OpenLogFile()
        {
            try
            {
                string logPath = Log.LogFilePath;
                if (File.Exists(logPath)) Process.Start("notepad.exe", "\"" + logPath + "\"");
                else ShowInfoBox("日志文件暂时不存在：\r\n" + logPath);
            }
            catch (Exception ex)
            {
                ShowErrorBox("打开日志失败", "日志文件位置：" + Log.LogFilePath + "\r\n" + ex.Message);
            }
        }

        private void OpenFailureLog()
        {
            string path = _moveResult != null ? _moveResult.FailureLogPath : null;
            if (string.IsNullOrEmpty(path))
            {
                ShowInfoBox("本次搬移没有生成失败诊断日志。");
                return;
            }
            try
            {
                if (!File.Exists(path))
                {
                    ShowWarnBox("诊断日志文件不存在：\r\n" + path);
                    return;
                }
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                ShowErrorBox("打开诊断日志失败",
                    "诊断日志位置：" + path + "\r\n" + ex.Message + "\r\n\r\n反馈问题时可附上该文件。");
            }
        }

        private void RetryFromResult()
        {
            if (_moveRunning || _undoRunning) return;
            ShowStep(2);
            _moveResult = null;
            if (_startButton != null)
            {
                _startButton.Enabled = true;
                _startButton.Text = "开始搬移";
            }
            RestartPrecheckTimer();
        }

        private void FinishToStart()
        {
            if (_moveRunning || _undoRunning) return;
            _moveRequest = null;
            _moveResult = null;
            _selectedProgram = null;
            if (_selectionSummary != null) _selectionSummary.Clear();
            UpdateNextButtonState();
            _sizeVersion++;
            ShowStep(1);
            if (_index == null) StartScan();
            else RefreshProgramList();
        }

        private void OnUndoClick()
        {
            if (_undoRunning || _moveRunning) return;
            if (_moveResult == null || string.IsNullOrEmpty(_moveResult.JournalPath))
            {
                ShowInfoBox("没有找到可撤销的搬移记录。");
                return;
            }
            string reason;
            try
            {
                if (!MoveEngine.CanUndo(_moveResult.JournalPath, out reason))
                {
                    ShowWarnBox("无法撤销：" + (string.IsNullOrEmpty(reason) ? "搬移记录不完整" : reason));
                    return;
                }
            }
            catch (NotImplementedException)
            {
                ShowWarnBox("撤销模块暂不可用（后台模块尚未实现）。");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("检查撤销条件失败: " + ex.Message);
            }
            if (!AskYesNo("确定要撤销这次搬移吗？\r\n软件将回到原来的位置：" +
                (_moveResult.OldPath ?? string.Empty))) return;

            _undoRunning = true;
            _undoButton.Enabled = false;
            _progressView.Visible = true;
            _resultView.Visible = false;
            _stageList.ResetStates();
            _stageTextLabel.Text = "正在撤销搬移…";
            _percentLabel.Text = "处理中…";
            _progressBar.IsIndeterminate = true;
            _bytesLabel.Text = "正在恢复文件，请稍候…";
            _currentFileLabel.Text = string.Empty;
            _logBox.Text = string.Empty;
            _cancelButton.Enabled = false;
            AppendLog("开始撤销搬移：" + _moveResult.OldPath);

            string journalPath = _moveResult.JournalPath;
            string oldPath = _moveResult.OldPath;
            Thread worker = new Thread(() =>
            {
                MoveResult undoResult = null;
                string error = null;
                try
                {
                    undoResult = MoveEngine.Undo(journalPath,
                        delegate(MoveProgress progress) { OnMoveProgressFromWorker(progress); });
                }
                catch (NotImplementedException)
                {
                    error = "撤销模块暂不可用（后台模块尚未实现）";
                }
                catch (Exception ex)
                {
                    Log.Error("撤销搬移异常", ex);
                    error = "撤销失败：" + ex.Message;
                }
                SafeUi(delegate { OnUndoFinished(undoResult, error, oldPath); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void OnUndoFinished(MoveResult undoResult, string error, string oldPath)
        {
            _undoRunning = false;
            if (undoResult != null && undoResult.Success)
            {
                _moveResult = undoResult;
                ShowUndoSuccess(oldPath);
                ShowInfoBox("已撤销这次搬移，软件已回到原来的位置。");
                return;
            }
            string message = error;
            if (undoResult != null && !string.IsNullOrEmpty(undoResult.ErrorMessage)) message = undoResult.ErrorMessage;
            ShowFailure("撤销失败", string.IsNullOrEmpty(message) ? "撤销失败，请查看日志。" : message, undoResult);
        }

        // ------------------------------------------------------------ 预览用

        private void PreviewProgressDemo()
        {
            _stageList.ResetStates();
            for (int i = 0; i < 4; i++) _stageList.SetStageState(i, Ctl.StageList.StateDone);
            _stageList.SetStageState(4, Ctl.StageList.StateActive);
            _lastStageIndex = 4;
            _stageTextLabel.Text = "正在校验文件…";
            _percentLabel.Text = "45%";
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 45;
            _bytesLabel.Text = "已复制 1.55 GB / 3.4 GB · 45%";
            _currentFileLabel.Text = @"当前文件：C:\Program Files\Tencent\WeChat\WeChat.exe";
            _logBox.Text = string.Empty;
            AppendLog("开始搬移：D:\\WeChat");
            AppendLog("预检通过，来源 3.4 GB，目标盘可用 180 GB");
            AppendLog("正在复制文件（共 5678 个）…");
            AppendLog("已复制 2456 / 5678 个文件");
            _cancelButton.Enabled = true;
            _moveRunning = false;
        }

        private void ShowSuccessDemo(bool pendingReboot)
        {
            string newPath = _targetPanel != null ? _targetPanel.TargetPath : @"D:\WeChat";
            MoveResult demo = new MoveResult();
            demo.Success = true;
            demo.FinalStage = pendingReboot ? MoveStage.PendingReboot : MoveStage.Completed;
            demo.OldPath = _selectedProgram != null ? _selectedProgram.InstallPath : @"C:\Program Files\Tencent\WeChat";
            demo.NewPath = newPath;
            demo.JunctionPath = demo.OldPath;
            demo.JournalPath = @"C:\ProgramData\YijianNuodong\journal\20261103-1530-微信.move";
            demo.FreedBytes = _selectedProgram != null ? _selectedProgram.SizeBytes : 3450L * 1024L * 1024L;
            demo.ShortcutsUpdated = 4;
            demo.RegistryEntriesUpdated = 12;
            demo.JunctionCreated = true;
            demo.PendingReboot = pendingReboot;
            demo.DesktopShortcutCreated = true;
            demo.DesktopShortcutPath = @"C:\Users\Public\Desktop\微信.lnk";
            demo.ShortcutsRemoved = 2;
            demo.Warnings.Add("已跳过 2 个正在使用中的临时文件，不影响软件正常使用。");
            demo.Warnings.Add("注册表中有 1 项旧路径无法识别，已忽略。");
            _moveResult = demo;
            _moveRequest = BuildRequestFromUi();
            _moveRequest.TargetDirectory = newPath;
            ShowSuccess();
        }

        private void ShowFailureDemo()
        {
            MoveResult demo = new MoveResult();
            demo.Success = false;
            demo.FinalStage = MoveStage.Failed;
            demo.OldPath = _selectedProgram != null ? _selectedProgram.InstallPath : @"C:\Program Files\Tencent\WeChat";
            demo.NewPath = _targetPanel != null ? _targetPanel.TargetPath : @"D:\WeChat";
            demo.ErrorMessage = "复制文件失败：目标文件被另一个程序占用（WeChat.exe，PID 14392）。";
            demo.FailureLogPath = @"C:\ProgramData\YijianNuodong\logs\20261103-failure-微信.log";
            demo.Warnings.Add("已自动回滚，原安装目录保持完整。");
            _moveResult = demo;
            _moveRequest = BuildRequestFromUi();
            _moveRequest.TargetDirectory = demo.NewPath;
            ShowFailure("搬移失败", demo.ErrorMessage, demo);
        }
    }
}
