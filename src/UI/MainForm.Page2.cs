// T3 拥有：主窗口 - 第 2 步「选择位置」界面与逻辑（目标目录 / 选项 / 占用处理 / 预检）
using System;
using System.Collections.Generic;
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
        private Ctl.ProgramSummaryPanel _programSummary;
        private Ctl.TargetPanel _targetPanel;
        private Ctl.OptionsPanel _optionsPanel;
        private Ctl.NoticePanel _warningsPanel;
        private Ctl.NoticePanel _locksPanel;
        private Ctl.FlatButton _backButton2;
        private Ctl.FlatButton _startButton;
        private Ctl.FlatButton _killButton;
        private Ctl.FlatButton _rebootButton;
        private Panel _page2Scroll;
        private TableLayoutPanel _page2Inner;

        private System.Windows.Forms.Timer _precheckTimer;
        private int _precheckVersion;
        private bool _startingMove;
        private bool _forceKillLocks;
        private int _noticesLayoutWidth = -1;

        // ------------------------------------------------------------ 界面构建

        private void BuildPage2()
        {
            _page2 = new Panel();
            _page2.Dock = DockStyle.Fill;
            _page2.BackColor = Theme.Surface;

            TableLayoutPanel outer = new TableLayoutPanel();
            outer.Dock = DockStyle.Fill;
            outer.ColumnCount = 1;
            outer.RowCount = 2;
            outer.BackColor = Theme.Surface;
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

            _page2Scroll = new Panel();
            _page2Scroll.Dock = DockStyle.Fill;
            _page2Scroll.BackColor = Theme.Surface;
            _page2Scroll.AutoScroll = true;
            _page2Scroll.Resize += delegate { LayoutPage2Notices(); };
            outer.Controls.Add(_page2Scroll, 0, 0);

            _page2Inner = new TableLayoutPanel();
            _page2Inner.Dock = DockStyle.Top;
            _page2Inner.AutoSize = true;
            _page2Inner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _page2Inner.ColumnCount = 1;
            _page2Inner.RowCount = 7;
            _page2Inner.BackColor = Theme.Surface;
            _page2Inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _page2Inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            _page2Inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            _page2Inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
            _page2Inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            _page2Inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
            _page2Inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _page2Inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _page2Scroll.Controls.Add(_page2Inner);

            _programSummary = new Ctl.ProgramSummaryPanel();
            _programSummary.Dock = DockStyle.Fill;
            _programSummary.Margin = new Padding(0);
            _page2Inner.Controls.Add(_programSummary, 0, 0);

            _targetPanel = new Ctl.TargetPanel();
            _targetPanel.Dock = DockStyle.Fill;
            _targetPanel.Margin = new Padding(0);
            _targetPanel.BrowseClicked += OnBrowseTarget;
            _targetPanel.TargetChanged += OnTargetChanged;
            _page2Inner.Controls.Add(_targetPanel, 0, 2);

            _optionsPanel = new Ctl.OptionsPanel();
            _optionsPanel.Dock = DockStyle.Fill;
            _optionsPanel.Margin = new Padding(0);
            _page2Inner.Controls.Add(_optionsPanel, 0, 4);

            _warningsPanel = new Ctl.NoticePanel();
            _warningsPanel.Dock = DockStyle.Top;
            _warningsPanel.Margin = new Padding(0, 10, 0, 0);
            _warningsPanel.BackdropColor = Theme.Surface;
            _page2Inner.Controls.Add(_warningsPanel, 0, 5);

            _locksPanel = new Ctl.NoticePanel();
            _locksPanel.Dock = DockStyle.Top;
            _locksPanel.Margin = new Padding(0, 10, 0, 0);
            _locksPanel.BackdropColor = Theme.Surface;
            _page2Inner.Controls.Add(_locksPanel, 0, 6);

            // 底部操作条
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.Margin = new Padding(0);
            bottom.ColumnCount = 3;
            bottom.RowCount = 1;
            bottom.BackColor = Theme.Surface;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _backButton2 = new Ctl.FlatButton();
            _backButton2.Text = "← 上一步";
            _backButton2.Style = Ctl.FlatButtonStyle.Secondary;
            _backButton2.Font = Theme.BodyBold;
            _backButton2.Size = new Size(120, 40);
            _backButton2.Margin = new Padding(0, 12, 0, 12);
            _backButton2.BackdropColor = Theme.Surface;
            _backButton2.Click += delegate { ShowStep(1); };
            bottom.Controls.Add(_backButton2, 0, 0);

            Label hint = new Label();
            hint.AutoSize = false;
            hint.Dock = DockStyle.Fill;
            hint.Font = Theme.Small;
            hint.ForeColor = Theme.TextMuted;
            hint.BackColor = Theme.Surface;
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.Padding = new Padding(0, 0, 16, 0);
            hint.Text = "开始前会自动检查程序占用与目标盘空间";
            bottom.Controls.Add(hint, 1, 0);

            _startButton = new Ctl.FlatButton();
            _startButton.Text = "开始搬移";
            _startButton.Style = Ctl.FlatButtonStyle.Primary;
            _startButton.Font = Theme.BodyBold;
            _startButton.Size = new Size(160, 42);
            _startButton.Margin = new Padding(0, 11, 0, 11);
            _startButton.BackdropColor = Theme.Surface;
            _startButton.Click += delegate { OnStartMove(); };
            bottom.Controls.Add(_startButton, 2, 0);

            outer.Controls.Add(bottom, 0, 1);
            _page2.Controls.Add(outer);
            _pageHost.Controls.Add(_page2);
        }

        // ------------------------------------------------------------ 进入第 2 步

        private void GoToStep2()
        {
            if (_selectedProgram == null)
            {
                ShowInfoBox("请先选择要搬移的软件。");
                return;
            }
            ShowStep(2);
            _forceKillLocks = false;
            ResetPage2Notices();
            _programSummary.SetProgram(_selectedProgram);
            _programSummary.SetSizeText(_selectedProgram.SizeBytes);
            _programSummary.SetRunning(_selectedProgram.IsRunning);
            _optionsPanel.ApplyDefaults();

            string parent = ResolveDefaultParent(_selectedProgram);
            _targetPanel.SetParentDirectory(parent);
            _targetPanel.SetFolderName(PathUtil.GetLeafName(_selectedProgram.InstallPath, _selectedProgram.DisplayName));
            UpdateTargetDerivedInfo();

            if (_selectedProgram.SizeBytes < 0) StartPage2SizeWorker();
            RestartPrecheckTimer();
            _startButton.Enabled = true;
            _startButton.Text = "开始搬移";
            LayoutPage2Notices();
        }

        private string ResolveDefaultParent(InstalledProgram program)
        {
            try
            {
                string last = AppSettings.LastTargetDirectory;
                if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return PathUtil.Normalize(last);
            }
            catch { }
            try
            {
                string suggested = DriveAnalyzer.SuggestDefaultTarget(program);
                if (!string.IsNullOrEmpty(suggested) && Directory.Exists(suggested)) return PathUtil.Normalize(suggested);
            }
            catch { }
            try
            {
                string systemRoot = PathUtil.GetDriveRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    try
                    {
                        if (drives[i].DriveType != DriveType.Fixed || !drives[i].IsReady) continue;
                        string root = drives[i].RootDirectory.FullName;
                        if (PathUtil.SamePath(root, systemRoot)) continue;
                        return PathUtil.Normalize(root);
                    }
                    catch { }
                }
                for (int i = 0; i < drives.Length; i++)
                {
                    try
                    {
                        if (drives[i].DriveType != DriveType.Fixed || !drives[i].IsReady) continue;
                        return PathUtil.Normalize(drives[i].RootDirectory.FullName);
                    }
                    catch { }
                }
            }
            catch { }
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile) && Directory.Exists(profile)) return profile;
            return @"C:\";
        }

        private DriveInfoItem GetDriveInfoForPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                DriveInfoItem item = DriveAnalyzer.GetDriveForPath(path);
                if (item != null) return item;
            }
            catch { }
            try
            {
                string root = Path.GetPathRoot(PathUtil.Normalize(path));
                if (string.IsNullOrEmpty(root)) return null;
                DriveInfo drive = new DriveInfo(root);
                DriveInfoItem item = new DriveInfoItem();
                item.RootPath = drive.Name;
                item.VolumeLabel = drive.VolumeLabel;
                item.TotalBytes = drive.TotalSize;
                item.FreeBytes = drive.AvailableFreeSpace;
                item.IsSystemDrive = PathUtil.SamePath(drive.Name, PathUtil.GetDriveRoot(Environment.SystemDirectory));
                return item;
            }
            catch
            {
                return null;
            }
        }

        private void UpdateTargetDerivedInfo()
        {
            if (_selectedProgram == null || _targetPanel == null) return;
            string parent = _targetPanel.ParentDirectory;
            DriveInfoItem drive = GetDriveInfoForPath(parent);
            _targetPanel.SetDrive(drive);
            long free = drive != null ? drive.FreeBytes : -1;
            _targetPanel.SetSpaceInfo(_selectedProgram.SizeBytes, free);
        }

        private void UpdatePage2Size()
        {
            if (_selectedProgram == null || _programSummary == null) return;
            _programSummary.SetSizeText(_selectedProgram.SizeBytes);
            UpdateTargetDerivedInfo();
        }

        private void StartPage2SizeWorker()
        {
            InstalledProgram program = _selectedProgram;
            if (program == null) return;
            int version = _sizeVersion;
            Thread worker = new Thread(() =>
            {
                long size = -1;
                try
                {
                    size = FileUtil.GetDirectorySize(program.InstallPath,
                        delegate { return _formClosing || version != _sizeVersion; });
                }
                catch { }
                if (size < 0) return;
                SafeUi(delegate
                {
                    if (_formClosing || version != _sizeVersion) return;
                    if (program.SizeBytes < 0) program.SizeBytes = size;
                    if (ReferenceEquals(_selectedProgram, program))
                    {
                        _programSummary.SetSizeText(program.SizeBytes);
                        UpdateTargetDerivedInfo();
                    }
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void OnBrowseTarget()
        {
            FolderPickerDialog dialog = null;
            try
            {
                dialog = new FolderPickerDialog(_targetPanel.ParentDirectory);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    string path = dialog.SelectedPath;
                    if (!string.IsNullOrEmpty(path)) ApplyPickedTargetDirectory(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("文件夹选择器出错: " + ex.Message);
                ShowErrorBox("选择文件夹失败", "打开文件夹选择器时出错：" + ex.Message);
            }
            finally
            {
                try { if (dialog != null) dialog.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// 应用选择器返回的目录。修复用户反馈的「E:\Xmind\Xmind」重复嵌套：
        /// 选中的若是已存在的空文件夹（且不是盘符根），就直接把它当作最终目标目录；
        /// 选中的是非空文件夹时，维持「父目录 + 软件文件夹名」的旧行为（避免把软件塞进乱七八糟的目录里）；
        /// 选中盘符根则作为父目录，后面自动拼软件文件夹名。
        /// </summary>
        private void ApplyPickedTargetDirectory(string pickedPath)
        {
            try
            {
                string normalized = PathUtil.Normalize(pickedPath);
                if (string.IsNullOrEmpty(normalized))
                {
                    _targetPanel.SetParentDirectory(pickedPath);
                    _targetPanel.FocusFolderName();
                    return;
                }

                string defaultLeaf = string.Empty;
                if (_selectedProgram != null)
                {
                    defaultLeaf = PathUtil.GetLeafName(_selectedProgram.InstallPath, _selectedProgram.DisplayName);
                }

                string parent = normalized;
                string leaf = defaultLeaf;

                bool isDriveRoot = false;
                try { isDriveRoot = PathUtil.SamePath(normalized, PathUtil.GetDriveRoot(normalized)); }
                catch { isDriveRoot = false; }

                bool isEmpty = false;
                try { isEmpty = Directory.Exists(normalized) && FileUtil.IsDirectoryEmpty(normalized); }
                catch { isEmpty = false; }

                if (!isDriveRoot && isEmpty)
                {
                    string parentOfPicked = Path.GetDirectoryName(normalized);
                    string nameOfPicked = Path.GetFileName(normalized);
                    if (!string.IsNullOrEmpty(parentOfPicked) && !string.IsNullOrEmpty(nameOfPicked))
                    {
                        parent = parentOfPicked;
                        leaf = nameOfPicked;
                    }
                }

                _targetPanel.SetParentDirectory(parent);
                _targetPanel.SetFolderName(leaf);
                _targetPanel.FocusFolderName();
                Log.Info("已应用目标位置: " + _targetPanel.TargetPath);
            }
            catch (Exception ex)
            {
                Log.Warn("应用目标位置失败: " + ex.Message);
                try
                {
                    _targetPanel.SetParentDirectory(pickedPath);
                    _targetPanel.FocusFolderName();
                }
                catch { }
            }
        }

        private void OnTargetChanged()
        {
            if (_targetPanel == null || _selectedProgram == null) return;
            UpdateTargetDerivedInfo();
            if (!_previewMode && _currentStep == 2) RestartPrecheckTimer();
        }

        // ------------------------------------------------------------ 预检

        private void RestartPrecheckTimer()
        {
            if (_selectedProgram == null) return;
            if (_precheckTimer == null)
            {
                _precheckTimer = new System.Windows.Forms.Timer();
                _precheckTimer.Interval = 450;
                _precheckTimer.Tick += delegate
                {
                    _precheckTimer.Stop();
                    RunPrecheckAsync();
                };
            }
            _precheckTimer.Stop();
            _precheckTimer.Start();
        }

        private MoveRequest BuildRequestFromUi()
        {
            MoveRequest request = new MoveRequest();
            request.Program = _selectedProgram;
            request.TargetDirectory = _targetPanel == null ? string.Empty : _targetPanel.TargetPath;
            if (_optionsPanel != null)
            {
                request.CreateJunction = _optionsPanel.CreateJunction;
                request.CreateRestorePoint = _optionsPanel.CreateRestorePoint;
                request.UpdateShortcuts = _optionsPanel.UpdateReferences;
                request.UpdateRegistry = _optionsPanel.UpdateReferences;
            }
            request.KillBlockingProcesses = _forceKillLocks;
            request.StopRelatedServices = true;
            return request;
        }

        private void RunPrecheckAsync()
        {
            if (_selectedProgram == null || _currentStep != 2) return;
            string target = _targetPanel.TargetPath;
            if (string.IsNullOrEmpty(target)) return;
            MoveRequest request = BuildRequestFromUi();
            int version = ++_precheckVersion;
            Thread worker = new Thread(() =>
            {
                PrecheckResult result = null;
                string error = null;
                try
                {
                    result = MoveEngine.Precheck(request);
                }
                catch (NotImplementedException)
                {
                    error = "预检模块暂不可用（后台模块尚未实现）";
                }
                catch (Exception ex)
                {
                    Log.Warn("预检失败: " + ex.Message);
                    error = "预检失败：" + ex.Message;
                }
                SafeUi(delegate
                {
                    if (version != _precheckVersion || _currentStep != 2 || _selectedProgram == null) return;
                    if (result == null)
                    {
                        ShowPrecheckUnavailable(error);
                        return;
                    }
                    _precheck = result;
                    ApplyPrecheckResult(result);
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void ShowPrecheckUnavailable(string error)
        {
            if (_warningsPanel == null) return;
            _warningsPanel.Configure(Ctl.NoticeKind.Warning, "预检暂不可用",
                (string.IsNullOrEmpty(error) ? "预检模块暂不可用。" : error + "。") +
                "仍可以尝试开始搬移，但请先退出相关软件。");
            _warningsPanel.Visible = true;
            if (_locksPanel != null)
            {
                _locksPanel.ClearButtons();
                _locksPanel.Visible = false;
            }
            LayoutPage2Notices();
        }

        private void ApplyPrecheckResult(PrecheckResult result)
        {
            if (result == null) return;

            if (result.SourceBytes > 0)
            {
                _selectedProgram.SizeBytes = result.SourceBytes;
                _programSummary.SetSizeText(result.SourceBytes);
            }
            _targetPanel.SetSpaceInfo(result.SourceBytes, result.TargetFreeBytes);

            // 占用进程 / 服务
            if (result.Locks != null && result.Locks.IsLocked) ConfigureLocksPanel(result.Locks);
            else HideLocksPanel();

            // 警告 / 错误
            List<string> lines = new List<string>();
            Ctl.NoticeKind kind = Ctl.NoticeKind.Warning;
            string title = "搬移前提示";
            if (result.Errors != null && result.Errors.Count > 0)
            {
                kind = Ctl.NoticeKind.Danger;
                title = "预检发现问题，可能无法继续";
                for (int i = 0; i < result.Errors.Count; i++) lines.Add(result.Errors[i]);
            }
            else if (result.IsProtectedSystemProgram)
            {
                kind = Ctl.NoticeKind.Danger;
                title = "不建议搬移的系统关键程序";
                lines.Add(string.IsNullOrEmpty(result.ProtectedReason) ? "该程序属于系统关键组件。" : result.ProtectedReason);
            }
            else if (result.Warnings != null && result.Warnings.Count > 0)
            {
                for (int i = 0; i < result.Warnings.Count; i++) lines.Add(result.Warnings[i]);
            }
            if (!result.HasEnoughSpace)
            {
                if (lines.Count == 0) { kind = Ctl.NoticeKind.Danger; title = "目标盘空间不足"; }
                lines.Add("目标盘可用空间不足（可用 " + TextUtil.FormatBytes(result.TargetFreeBytes) +
                          "，需要 " + TextUtil.FormatBytes(result.SourceBytes) + "），请更换目标位置。");
            }

            if (lines.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < lines.Count && i < 8; i++)
                {
                    if (i > 0) sb.Append("\r\n");
                    sb.Append("• ").Append(lines[i]);
                }
                _warningsPanel.Configure(kind, title, sb.ToString());
                _warningsPanel.Visible = true;
            }
            else
            {
                _warningsPanel.Visible = false;
            }
            LayoutPage2Notices();
            if (kind == Ctl.NoticeKind.Danger && _warningsPanel.Visible && !_previewMode)
            {
                try { _page2Scroll.ScrollControlIntoView(_warningsPanel); }
                catch { }
            }
        }

        private void ConfigureLocksPanel(LockInfo locks)
        {
            if (_locksPanel == null || locks == null) return;
            StringBuilder sb = new StringBuilder();
            int count = 0;
            if (locks.Processes != null)
            {
                for (int i = 0; i < locks.Processes.Count && count < 6; i++)
                {
                    LockingProcess process = locks.Processes[i];
                    if (process == null) continue;
                    if (sb.Length > 0) sb.Append("\r\n");
                    sb.Append("• 进程：").Append(string.IsNullOrEmpty(process.ProcessName) ? "未知进程" : process.ProcessName)
                      .Append("（PID ").Append(process.ProcessId).Append("）");
                    count++;
                }
            }
            if (locks.Services != null)
            {
                for (int i = 0; i < locks.Services.Count && count < 8; i++)
                {
                    LockingService service = locks.Services[i];
                    if (service == null) continue;
                    if (sb.Length > 0) sb.Append("\r\n");
                    sb.Append("• 服务：").Append(string.IsNullOrEmpty(service.DisplayName) ? service.ServiceName : service.DisplayName)
                      .Append("（").Append(service.ServiceName).Append("）");
                    count++;
                }
            }
            if (sb.Length == 0) sb.Append("• 检测到文件正被其他程序使用");
            if (locks.Notes != null && locks.Notes.Count > 0)
            {
                sb.Append("\r\n• ").Append(locks.Notes[0]);
            }
            _locksPanel.Configure(Ctl.NoticeKind.Danger, "检测到正在使用该软件的程序", sb.ToString());
            _locksPanel.ClearButtons();
            _killButton = _locksPanel.AddButton("结束这些进程并继续", Ctl.FlatButtonStyle.Danger);
            _killButton.Width = 176;
            _killButton.Click += delegate { OnKillAndContinue(); };
            _rebootButton = _locksPanel.AddButton("重启后自动完成", Ctl.FlatButtonStyle.Secondary);
            _rebootButton.Width = 148;
            _rebootButton.Click += delegate { OnScheduleReboot(); };
            _locksPanel.Visible = true;
            _locksPanel.BringToFront();
        }

        private void HideLocksPanel()
        {
            if (_locksPanel == null) return;
            _locksPanel.ClearButtons();
            _locksPanel.Visible = false;
            _killButton = null;
            _rebootButton = null;
        }

        private void ResetPage2Notices()
        {
            HideLocksPanel();
            if (_warningsPanel != null) _warningsPanel.Visible = false;
            _precheck = null;
            _noticesLayoutWidth = -1;
        }

        private void LayoutPage2Notices()
        {
            if (_page2Inner == null) return;
            int width = _page2Inner.ClientSize.Width;
            if (width <= 0) width = _page2Scroll.ClientSize.Width;
            if (width <= 0) return;
            if (width == _noticesLayoutWidth) return;
            _noticesLayoutWidth = width;
            if (_warningsPanel != null) _warningsPanel.SetLayoutWidth(width);
            if (_locksPanel != null) _locksPanel.SetLayoutWidth(width);
        }

        // ------------------------------------------------------------ 开始搬移

        private void OnKillAndContinue()
        {
            _forceKillLocks = true;
            OnStartMove();
        }

        private void OnScheduleReboot()
        {
            MoveRequest request = BuildRequestFromUi();
            if (string.IsNullOrEmpty(request.TargetDirectory))
            {
                ShowWarnBox("请先选择目标位置。");
                return;
            }
            string jobFile = null;
            string error = null;
            bool ok = false;
            try
            {
                ok = RebootMover.Schedule(Application.ExecutablePath, request, out jobFile, out error);
            }
            catch (NotImplementedException)
            {
                error = "重启任务模块暂不可用（后台模块尚未实现）";
            }
            catch (Exception ex)
            {
                Log.Warn("登记重启任务失败: " + ex.Message);
                error = "登记重启任务失败：" + ex.Message;
            }
            if (ok)
            {
                ShowInfoBox("已登记，重启电脑后会自动完成搬移。\r\n请尽快重启电脑，重启前不要再启动该软件。");
            }
            else
            {
                ShowErrorBox("登记重启任务失败", string.IsNullOrEmpty(error) ? "未知错误，请查看日志文件。" : error);
            }
        }

        private void OnStartMove()
        {
            if (_startingMove || _moveRunning || _selectedProgram == null) return;
            if (_currentStep != 2) return;

            string parent = _targetPanel.ParentDirectory;
            if (string.IsNullOrEmpty(parent))
            {
                ShowWarnBox("请先选择目标父文件夹。");
                return;
            }
            if (!Directory.Exists(parent))
            {
                ShowWarnBox("目标父文件夹不存在，请重新选择：\r\n" + parent);
                return;
            }
            bool writable;
            try { writable = FileUtil.HasWriteAccess(parent); }
            catch { writable = false; }
            if (!writable)
            {
                ShowWarnBox("没有写入权限，请换一个文件夹，或以管理员身份运行本程序。");
                return;
            }
            string leaf = _targetPanel.FolderName;
            if (string.IsNullOrEmpty(leaf))
            {
                ShowWarnBox("请填写新文件夹名。");
                _targetPanel.FocusFolderName();
                return;
            }
            if (!PathUtil.IsValidFolderName(leaf))
            {
                ShowWarnBox("文件夹名不合法：不能包含 \\ / : * ? \" < > | 等字符，也不能以空格或点结尾。");
                _targetPanel.FocusFolderName();
                return;
            }
            string target = PathUtil.CombineTarget(parent, leaf);
            if (string.IsNullOrEmpty(target))
            {
                ShowWarnBox("目标路径无效，请检查文件夹名。");
                return;
            }
            try
            {
                if (Directory.Exists(target) && !FileUtil.IsDirectoryEmpty(target))
                {
                    ShowWarnBox("目标文件夹已存在且不为空：\r\n" + target + "\r\n请换一个文件夹名。");
                    _targetPanel.FocusFolderName();
                    return;
                }
            }
            catch { }

            long free = -1;
            DriveInfoItem drive = GetDriveInfoForPath(parent);
            if (drive != null) free = drive.FreeBytes;
            long need = _selectedProgram.SizeBytes;
            if (need >= 0 && free >= 0 && free < need + 200L * 1024 * 1024)
            {
                ShowWarnBox("目标盘空间不足：\r\n本次需要 " + TextUtil.FormatBytes(need) +
                            "，目标盘可用 " + TextUtil.FormatBytes(free) + "。\r\n请更换目标位置。");
                return;
            }

            _startingMove = true;
            _startButton.Enabled = false;
            _startButton.Text = "正在预检…";
            MoveRequest request = BuildRequestFromUi();
            request.TargetDirectory = target;
            Thread worker = new Thread(() =>
            {
                PrecheckResult result = null;
                string error = null;
                try
                {
                    result = MoveEngine.Precheck(request);
                }
                catch (NotImplementedException)
                {
                    error = "预检模块暂不可用（后台模块尚未实现）";
                }
                catch (Exception ex)
                {
                    Log.Warn("开始前预检失败: " + ex.Message);
                    error = "预检失败：" + ex.Message;
                }
                SafeUi(delegate { OnPrecheckBeforeMove(request, result, error); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void OnPrecheckBeforeMove(MoveRequest request, PrecheckResult result, string error)
        {
            _startingMove = false;
            if (_startButton != null)
            {
                _startButton.Enabled = true;
                _startButton.Text = "开始搬移";
            }
            if (result == null)
            {
                if (_warningsPanel != null)
                {
                    _warningsPanel.Configure(Ctl.NoticeKind.Warning, "预检暂不可用",
                        (string.IsNullOrEmpty(error) ? "预检模块暂不可用。" : error + "。") + "将直接尝试搬移，请确保软件已完全退出。");
                    _warningsPanel.Visible = true;
                    LayoutPage2Notices();
                }
                BeginMove(request);
                return;
            }

            _precheck = result;
            ApplyPrecheckResult(result);
            if (!result.CanProceed || (result.Errors != null && result.Errors.Count > 0))
            {
                ShowErrorBox("无法开始搬移", JoinLines(result.Errors, "预检未通过，无法开始搬移。"));
                return;
            }
            if (result.IsProtectedSystemProgram)
            {
                ShowErrorBox("无法搬移系统关键程序",
                    string.IsNullOrEmpty(result.ProtectedReason)
                        ? "该程序属于系统关键组件，搬移后可能导致系统异常，已阻止搬移。"
                        : result.ProtectedReason);
                return;
            }
            if (!result.HasEnoughSpace)
            {
                ShowErrorBox("目标盘空间不足",
                    "目标盘可用 " + TextUtil.FormatBytes(result.TargetFreeBytes) +
                    "，本次需要 " + TextUtil.FormatBytes(result.SourceBytes) + "。\r\n请更换目标位置。");
                return;
            }
            bool locked = result.Locks != null && result.Locks.IsLocked;
            if (locked && !_forceKillLocks)
            {
                if (_locksPanel != null)
                {
                    try
                    {
                        _locksPanel.Visible = true;
                        _page2Scroll.ScrollControlIntoView(_locksPanel);
                    }
                    catch { }
                }
                ShowWarnBox("检测到正在使用该软件的程序。\r\n请先在下方选择「结束这些进程并继续」或「重启后自动完成」。");
                return;
            }

            BeginMove(request);
        }

        private static string JoinLines(List<string> lines, string fallback)
        {
            if (lines == null || lines.Count == 0) return fallback;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Count && i < 8; i++)
            {
                if (i > 0) sb.Append("\r\n");
                sb.Append("• ").Append(lines[i]);
            }
            return sb.ToString();
        }

        private void BeginMove(MoveRequest request)
        {
            if (request == null) return;
            request.Program = _selectedProgram;
            if (string.IsNullOrEmpty(request.TargetDirectory)) request.TargetDirectory = _targetPanel.TargetPath;
            if (string.IsNullOrEmpty(request.TargetDirectory))
            {
                ShowWarnBox("请先设置目标位置。");
                return;
            }
            _moveRequest = request;
            _moveResult = null;
            ShowStep(3);
            ResetProgressUi();
            StartMoveThread(request);
        }

        // ------------------------------------------------------------ 预览用

        private void PreparePage2ForPreview(bool showLocks, bool showWarning)
        {
            if (_selectedProgram == null) return;
            ShowStep(2);
            _forceKillLocks = false;
            ResetPage2Notices();
            _programSummary.SetProgram(_selectedProgram);
            if (_selectedProgram.SizeBytes < 0) _selectedProgram.SizeBytes = 3450L * 1024L * 1024L;
            _programSummary.SetSizeText(_selectedProgram.SizeBytes);
            _optionsPanel.ApplyDefaults();
            string parent = ResolveDefaultParent(_selectedProgram);
            _targetPanel.SetParentDirectory(parent);
            _targetPanel.SetFolderName(PathUtil.GetLeafName(_selectedProgram.InstallPath, _selectedProgram.DisplayName));
            UpdateTargetDerivedInfo();

            PrecheckResult demo = new PrecheckResult();
            demo.CanProceed = true;
            demo.SourceBytes = _selectedProgram.SizeBytes;
            DriveInfoItem drive = GetDriveInfoForPath(_targetPanel.ParentDirectory);
            demo.TargetFreeBytes = drive != null ? drive.FreeBytes : -1;
            if (showLocks)
            {
                demo.Locks = new LockInfo();
                LockingProcess process1 = new LockingProcess();
                process1.ProcessId = 14392;
                process1.ProcessName = "WeChat.exe";
                process1.MainWindowTitle = "微信";
                process1.HasMainWindow = true;
                demo.Locks.Processes.Add(process1);
                LockingProcess process2 = new LockingProcess();
                process2.ProcessId = 8920;
                process2.ProcessName = "WeChatAppEx.exe";
                demo.Locks.Processes.Add(process2);
                LockingService service = new LockingService();
                service.ServiceName = "WeChatService";
                service.DisplayName = "微信服务";
                service.State = "Running";
                service.CanStop = true;
                demo.Locks.Services.Add(service);
                demo.Warnings.Add("如需继续，请选择结束占用进程，或登记重启后自动搬移。");
            }
            else if (showWarning)
            {
                demo.Warnings.Add("目标文件夹已存在，将合并到现有空目录。");
                demo.Warnings.Add("检测到本程序曾创建桌面快捷方式，搬移后会自动更新指向。");
            }
            ApplyPrecheckResult(demo);
            LayoutPage2Notices();
        }
    }
}
