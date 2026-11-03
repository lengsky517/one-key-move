// T3 拥有：目标文件夹选择器（支持新建文件夹、重命名、磁盘切换、路径输入、双击进入）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using YijianNuodong.Core;
using Ctl = YijianNuodong.UI.Controls;

namespace YijianNuodong.UI
{
    public class FolderPickerDialog : Form
    {
        private const string LoadingText = "加载中…";

        private Label _titleLabel;
        private Label _subtitleLabel;
        private ComboBox _driveCombo;
        private Ctl.BorderedPanel _pathHost;
        private TextBox _pathBox;
        private Ctl.FlatButton _goButton;
        private Ctl.BorderedPanel _treeHost;
        private TreeView _tree;
        private Ctl.BorderedPanel _listHost;
        private ListView _list;
        private Label _emptyLabel;
        private Label _spaceLabel;
        private Ctl.FlatButton _newButton;
        private Ctl.FlatButton _renameButton;
        private Ctl.FlatButton _refreshButton;
        private Ctl.FlatButton _cancelButton;
        private Ctl.FlatButton _selectButton;

        private readonly List<string> _driveRoots = new List<string>();
        private ImageList _images;
        private string _currentPath = string.Empty;
        private string _previewPath;
        private bool _loadingDrives;
        private bool _suppressTreeEvents;
        private bool _suppressDriveEvents;
        /// <summary>最近一次枚举文件夹列表是否失败（用于空列表时给出准确提示）</summary>
        private bool _lastEnumerationFailed;

        /// <summary>用户最终选中的父文件夹</summary>
        public string SelectedPath { get; private set; }

        public FolderPickerDialog() : this(null) { }

        public FolderPickerDialog(string initialDirectory)
        {
            SelectedPath = initialDirectory;
            BuildWindow();
            BuildUi();
            Load += FolderPickerDialog_Load;
        }

        private void BuildWindow()
        {
            Text = "选择目标文件夹";
            ClientSize = new Size(800, 560);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Background;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;
            KeyPreview = true;
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 7;
            root.BackColor = Theme.Background;
            root.Padding = new Padding(24, 16, 24, 16);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            // ---- 标题区 ----
            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Theme.Background;
            _titleLabel = new Label();
            _titleLabel.AutoSize = false;
            _titleLabel.Dock = DockStyle.Top;
            _titleLabel.Height = 40;
            _titleLabel.Font = Theme.H1Small;
            _titleLabel.ForeColor = Theme.TextPrimary;
            _titleLabel.BackColor = Theme.Background;
            _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
            _titleLabel.Text = "选择目标文件夹";

            _subtitleLabel = new Label();
            _subtitleLabel.AutoSize = false;
            _subtitleLabel.Dock = DockStyle.Top;
            _subtitleLabel.Height = 20;
            _subtitleLabel.Font = Theme.Small;
            _subtitleLabel.ForeColor = Theme.TextSecondary;
            _subtitleLabel.BackColor = Theme.Background;
            _subtitleLabel.TextAlign = ContentAlignment.MiddleLeft;
            _subtitleLabel.Text = "可以在这里新建文件夹、重命名文件夹；双击文件夹进入";
            header.Controls.Add(_subtitleLabel);
            header.Controls.Add(_titleLabel);
            root.Controls.Add(header, 0, 0);

            // ---- 工具栏：磁盘 + 路径 + 转到 ----
            TableLayoutPanel toolbar = new TableLayoutPanel();
            toolbar.Dock = DockStyle.Fill;
            toolbar.ColumnCount = 3;
            toolbar.RowCount = 1;
            toolbar.BackColor = Theme.Background;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _driveCombo = new ComboBox();
            _driveCombo.Dock = DockStyle.Fill;
            _driveCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _driveCombo.Font = Theme.Body;
            _driveCombo.BackColor = Theme.Surface;
            _driveCombo.Margin = new Padding(0, 2, 10, 2);
            _driveCombo.SelectedIndexChanged += DriveCombo_SelectedIndexChanged;
            toolbar.Controls.Add(_driveCombo, 0, 0);

            _pathHost = new Ctl.BorderedPanel();
            _pathHost.Dock = DockStyle.Fill;
            _pathHost.CornerRadius = 8;
            _pathHost.FillColor = Theme.Surface;
            _pathHost.BackdropColor = Theme.Background;
            _pathHost.BorderColor = Theme.BorderStrong;
            _pathHost.Margin = new Padding(0, 2, 10, 2);
            toolbar.Controls.Add(_pathHost, 1, 0);

            _pathBox = new TextBox();
            _pathBox.BorderStyle = BorderStyle.None;
            _pathBox.Font = Theme.Body;
            _pathBox.ForeColor = Theme.TextPrimary;
            _pathBox.BackColor = Theme.Surface;
            _pathBox.KeyDown += PathBox_KeyDown;
            _pathHost.Controls.Add(_pathBox);
            _pathHost.Resize += delegate { LayoutPathBox(); };

            _goButton = new Ctl.FlatButton();
            _goButton.Text = "转到";
            _goButton.Style = Ctl.FlatButtonStyle.Secondary;
            _goButton.Font = Theme.BodyBold;
            _goButton.Dock = DockStyle.Fill;
            _goButton.Margin = new Padding(0, 2, 0, 2);
            _goButton.BackdropColor = Theme.Background;
            _goButton.Click += delegate { NavigateFromPathBox(); };
            toolbar.Controls.Add(_goButton, 2, 0);
            root.Controls.Add(toolbar, 0, 2);

            // ---- 左侧目录树 + 右侧文件夹列表 ----
            TableLayoutPanel content = new TableLayoutPanel();
            content.Dock = DockStyle.Fill;
            content.ColumnCount = 2;
            content.RowCount = 1;
            content.BackColor = Theme.Background;
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _treeHost = new Ctl.BorderedPanel();
            _treeHost.Dock = DockStyle.Fill;
            _treeHost.CornerRadius = 10;
            _treeHost.FillColor = Theme.Surface;
            _treeHost.BackdropColor = Theme.Background;
            _treeHost.BorderColor = Theme.Border;
            _treeHost.Padding = new Padding(1);
            _treeHost.Margin = new Padding(0, 0, 10, 0);
            content.Controls.Add(_treeHost, 0, 0);

            _tree = new TreeView();
            _tree.Dock = DockStyle.Fill;
            _tree.BorderStyle = BorderStyle.None;
            _tree.Font = Theme.Body;
            _tree.BackColor = Theme.Surface;
            _tree.ForeColor = Theme.TextPrimary;
            _tree.HideSelection = false;
            _tree.FullRowSelect = true;
            _tree.HotTracking = true;
            _tree.ShowLines = false;
            _tree.ShowRootLines = true;
            _tree.ShowPlusMinus = true;
            _tree.ItemHeight = 24;
            _tree.BeforeExpand += Tree_BeforeExpand;
            _tree.AfterSelect += Tree_AfterSelect;
            Theme.EnableDoubleBuffer(_tree);
            _treeHost.Controls.Add(_tree);

            _listHost = new Ctl.BorderedPanel();
            _listHost.Dock = DockStyle.Fill;
            _listHost.CornerRadius = 10;
            _listHost.FillColor = Theme.Surface;
            _listHost.BackdropColor = Theme.Background;
            _listHost.BorderColor = Theme.Border;
            _listHost.Padding = new Padding(1);
            _listHost.Margin = new Padding(0);
            content.Controls.Add(_listHost, 1, 0);

            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.BorderStyle = BorderStyle.None;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.GridLines = false;
            _list.Font = Theme.Body;
            _list.BackColor = Theme.Surface;
            _list.Columns.Add("名称", 300, HorizontalAlignment.Left);
            _list.Columns.Add("修改日期", 150, HorizontalAlignment.Left);
            _list.DoubleClick += delegate { EnterSelectedFolder(); };
            _list.KeyDown += List_KeyDown;
            Theme.EnableDoubleBuffer(_list);
            _listHost.Controls.Add(_list);

            _emptyLabel = new Label();
            _emptyLabel.Dock = DockStyle.Fill;
            _emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            _emptyLabel.Font = Theme.Body;
            _emptyLabel.ForeColor = Theme.TextMuted;
            _emptyLabel.BackColor = Theme.Surface;
            _emptyLabel.Text = "这个文件夹里没有子文件夹";
            _emptyLabel.Visible = false;
            _listHost.Controls.Add(_emptyLabel);
            _emptyLabel.BringToFront();
            _listHost.Resize += delegate { AdjustListColumns(); };
            root.Controls.Add(content, 0, 4);

            // ---- 底部按钮区 ----
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.ColumnCount = 6;
            bottom.RowCount = 1;
            bottom.BackColor = Theme.Background;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _newButton = CreateBottomButton("新建文件夹", Ctl.FlatButtonStyle.Secondary);
            _newButton.Width = 112;
            _newButton.Click += delegate { CreateNewFolder(); };
            bottom.Controls.Add(_newButton, 0, 0);

            _renameButton = CreateBottomButton("重命名", Ctl.FlatButtonStyle.Secondary);
            _renameButton.Width = 92;
            _renameButton.Click += delegate { RenameSelectedFolder(); };
            bottom.Controls.Add(_renameButton, 1, 0);

            _refreshButton = CreateBottomButton("刷新", Ctl.FlatButtonStyle.Ghost);
            _refreshButton.Width = 68;
            _refreshButton.Click += delegate { RefreshTree(); Navigate(_currentPath); };
            bottom.Controls.Add(_refreshButton, 2, 0);

            _spaceLabel = new Label();
            _spaceLabel.AutoSize = false;
            _spaceLabel.Dock = DockStyle.Fill;
            _spaceLabel.Font = Theme.Small;
            _spaceLabel.ForeColor = Theme.TextSecondary;
            _spaceLabel.BackColor = Theme.Background;
            _spaceLabel.TextAlign = ContentAlignment.MiddleRight;
            _spaceLabel.Padding = new Padding(0, 0, 12, 0);
            _spaceLabel.AutoEllipsis = true;
            _spaceLabel.Text = "可用空间 计算中…";
            bottom.Controls.Add(_spaceLabel, 3, 0);

            _cancelButton = CreateBottomButton("取消", Ctl.FlatButtonStyle.Secondary);
            _cancelButton.Width = 88;
            _cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            bottom.Controls.Add(_cancelButton, 4, 0);

            _selectButton = CreateBottomButton("选择此文件夹", Ctl.FlatButtonStyle.Primary);
            _selectButton.Width = 132;
            _selectButton.Click += delegate { ConfirmSelection(); };
            bottom.Controls.Add(_selectButton, 5, 0);
            root.Controls.Add(bottom, 0, 6);

            Controls.Add(root);
            AcceptButton = _selectButton;
            CancelButton = _cancelButton;

            LoadImages();
        }

        private Ctl.FlatButton CreateBottomButton(string text, Ctl.FlatButtonStyle style)
        {
            Ctl.FlatButton button = new Ctl.FlatButton();
            button.Text = text;
            button.Style = style;
            button.Font = Theme.BodyBold;
            button.Height = 36;
            button.Width = 100;
            button.Margin = new Padding(0, 4, 8, 4);
            button.BackdropColor = Theme.Background;
            return button;
        }

        private void LoadImages()
        {
            _images = new ImageList();
            _images.ImageSize = new Size(16, 16);
            _images.ColorDepth = ColorDepth.Depth32Bit;
            try
            {
                Icon folderIcon = IconUtil.GetIconFor(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", true);
                if (folderIcon != null)
                {
                    using (folderIcon)
                    {
                        _images.Images.Add(folderIcon.ToBitmap());
                    }
                }
            }
            catch { }
            if (_images.Images.Count == 0)
            {
                using (Bitmap bmp = new Bitmap(16, 16))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        Theme.FillRoundRect(g, new Rectangle(1, 3, 14, 11), 2, Theme.PrimaryLight2);
                    }
                    _images.Images.Add(bmp);
                }
            }
            _tree.ImageList = _images;
            _list.SmallImageList = _images;
        }

        private void LayoutPathBox()
        {
            try
            {
                _pathBox.Location = new Point(10, (_pathHost.Height - 22) / 2);
                _pathBox.Size = new Size(Math.Max(40, _pathHost.Width - 22), 22);
            }
            catch { }
        }

        private void AdjustListColumns()
        {
            try
            {
                int width = _list.ClientSize.Width;
                if (width <= 0) return;
                int fixedWidth = 150;
                int nameWidth = width - fixedWidth - 4;
                if (nameWidth < 120) nameWidth = 120;
                _list.Columns[0].Width = nameWidth;
                _list.Columns[1].Width = fixedWidth;
            }
            catch { }
        }

        // ---------------------------------------------------------------- 数据加载

        private void FolderPickerDialog_Load(object sender, EventArgs e)
        {
            try
            {
                LoadDrives();
                string start = ResolveStartDirectory();
                if (!string.IsNullOrEmpty(_previewPath) && Directory.Exists(_previewPath)) start = _previewPath;
                PopulateTree(start);
                Navigate(start);
            }
            catch (Exception ex)
            {
                Log.Warn("文件夹选择器初始化失败: " + ex.Message);
                ShowError("读取磁盘信息失败：" + ex.Message);
            }
        }

        /// <summary>供界面预览使用：直接打开到指定目录</summary>
        public void PreviewNavigate(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _previewPath = path;
            if (IsHandleCreated) Navigate(path);
        }

        private string ResolveStartDirectory()
        {
            if (!string.IsNullOrEmpty(SelectedPath) && Directory.Exists(SelectedPath)) return SelectedPath;
            try
            {
                string last = AppSettings.LastTargetDirectory;
                if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;
            }
            catch { }
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                string systemRoot = PathUtil.GetDriveRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                for (int i = 0; i < drives.Length; i++)
                {
                    try
                    {
                        if (drives[i].DriveType != DriveType.Fixed || !drives[i].IsReady) continue;
                        if (PathUtil.SamePath(drives[i].RootDirectory.FullName, systemRoot)) continue;
                        return drives[i].RootDirectory.FullName;
                    }
                    catch { }
                }
                for (int i = 0; i < drives.Length; i++)
                {
                    try
                    {
                        if (drives[i].DriveType != DriveType.Fixed || !drives[i].IsReady) continue;
                        return drives[i].RootDirectory.FullName;
                    }
                    catch { }
                }
            }
            catch { }
            return @"C:\";
        }

        private void LoadDrives()
        {
            _loadingDrives = true;
            try
            {
                _driveCombo.Items.Clear();
                _driveRoots.Clear();
                List<DriveInfoItem> list = null;
                try { list = DriveAnalyzer.GetFixedDrives(); }
                catch (Exception ex) { Log.Warn("读取磁盘列表失败: " + ex.Message); list = null; }

                if (list != null && list.Count > 0)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        DriveInfoItem item = list[i];
                        if (item == null || string.IsNullOrEmpty(item.RootPath)) continue;
                        _driveCombo.Items.Add(item.DisplayText);
                        _driveRoots.Add(item.RootPath);
                    }
                }
                else
                {
                    TryAddDrivesFromDotNet();
                }
                if (_driveCombo.Items.Count > 0 && _driveCombo.SelectedIndex < 0) _driveCombo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                Log.Warn("加载磁盘列表异常: " + ex.Message);
            }
            finally
            {
                _loadingDrives = false;
            }
        }

        private void TryAddDrivesFromDotNet()
        {
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    try
                    {
                        DriveInfo d = drives[i];
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        string label = string.IsNullOrEmpty(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel;
                        string text = d.RootDirectory.FullName + "  " + label + "  可用 " +
                                      TextUtil.FormatBytes(d.AvailableFreeSpace) + " / " + TextUtil.FormatBytes(d.TotalSize);
                        _driveCombo.Items.Add(text);
                        _driveRoots.Add(d.RootDirectory.FullName);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("枚举磁盘失败: " + ex.Message);
            }
        }

        private void PopulateTree(string selectPath)
        {
            _suppressTreeEvents = true;
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                for (int i = 0; i < _driveRoots.Count; i++)
                {
                    string root = _driveRoots[i];
                    string label = root;
                    try
                    {
                        DriveInfo d = new DriveInfo(root);
                        string volume = string.IsNullOrEmpty(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel;
                        label = root.TrimEnd('\\') + "  " + volume;
                    }
                    catch { }
                    TreeNode node = new TreeNode(label);
                    node.Tag = root;
                    if (_images.Images.Count > 0) node.ImageIndex = 0;
                    node.SelectedImageIndex = 0;
                    AddPlaceholder(node);
                    _tree.Nodes.Add(node);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("构建目录树失败: " + ex.Message);
            }
            finally
            {
                _tree.EndUpdate();
                _suppressTreeEvents = false;
            }
            TrySelectNodeForPath(selectPath);
        }

        private void AddPlaceholder(TreeNode node)
        {
            TreeNode placeholder = new TreeNode(LoadingText);
            placeholder.ForeColor = Theme.TextMuted;
            node.Nodes.Add(placeholder);
        }

        private static bool IsPlaceholder(TreeNode node)
        {
            return node != null && node.Nodes.Count == 1 && node.Nodes[0].Tag == null &&
                   node.Nodes[0].Text == LoadingText;
        }

        private void FillChildNodes(TreeNode parent, string path)
        {
            try
            {
                string[] dirs = Directory.GetDirectories(path);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                int count = 0;
                for (int i = 0; i < dirs.Length; i++)
                {
                    if (count >= 400) break;
                    string dir = dirs[i];
                    try
                    {
                        FileAttributes attr = File.GetAttributes(dir);
                        if ((attr & FileAttributes.System) != 0) continue;
                        if ((attr & FileAttributes.Hidden) != 0) continue;
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        TreeNode child = new TreeNode(Path.GetFileName(dir));
                        child.Tag = dir;
                        child.ImageIndex = 0;
                        child.SelectedImageIndex = 0;
                        try
                        {
                            if (Directory.GetDirectories(dir).Length > 0) AddPlaceholder(child);
                        }
                        catch { }
                        parent.Nodes.Add(child);
                        count++;
                    }
                    catch { }
                }
                if (count == 0)
                {
                    TreeNode empty = new TreeNode("（没有子文件夹）");
                    empty.ForeColor = Theme.TextMuted;
                    parent.Nodes.Add(empty);
                }
            }
            catch (Exception ex)
            {
                TreeNode error = new TreeNode("无法读取（" + ex.Message + "）");
                error.ForeColor = Theme.Danger;
                parent.Nodes.Add(error);
            }
        }

        private void Tree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node == null) return;
            if (!IsPlaceholder(e.Node)) return;
            string path = e.Node.Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            _suppressTreeEvents = true;
            try
            {
                e.Node.Nodes.Clear();
                FillChildNodes(e.Node, path);
            }
            finally
            {
                _suppressTreeEvents = false;
            }
        }

        private void Tree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (_suppressTreeEvents || e.Node == null) return;
            string path = e.Node.Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            if (PathUtil.SamePath(path, _currentPath)) return;
            Navigate(path);
        }

        private bool TrySelectNodeForPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            _suppressTreeEvents = true;
            try
            {
                return TrySelectNode(_tree.Nodes, path);
            }
            finally
            {
                _suppressTreeEvents = false;
            }
        }

        private bool TrySelectNode(TreeNodeCollection nodes, string path)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                TreeNode node = nodes[i];
                string nodePath = node.Tag as string;
                if (string.IsNullOrEmpty(nodePath)) continue;
                if (PathUtil.SamePath(nodePath, path))
                {
                    _tree.SelectedNode = node;
                    node.EnsureVisible();
                    return true;
                }
                if (PathUtil.IsSubPathOf(path, nodePath))
                {
                    try { node.Expand(); } catch { }
                    if (TrySelectNode(node.Nodes, path)) return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- 导航

        private void Navigate(string path)
        {
            try
            {
                string normalized = PathUtil.Normalize(path);
                if (string.IsNullOrEmpty(normalized))
                {
                    ShowError("路径不能为空。");
                    return;
                }
                if (!Directory.Exists(normalized))
                {
                    ShowError("文件夹不存在或无法访问：\r\n" + normalized);
                    return;
                }
                _currentPath = normalized;
                _pathBox.Text = normalized;
                Log.Info("文件夹选择器打开: " + normalized);
                try { _pathBox.SelectionStart = 0; } catch { }
                UpdateDriveSelection();
                FillFolderList();
                UpdateSpaceInfo();
            }
            catch (Exception ex)
            {
                Log.Warn("导航失败: " + ex.Message);
                ShowError("打开文件夹失败：" + ex.Message);
            }
        }

        private void UpdateDriveSelection()
        {
            string root = PathUtil.GetDriveRoot(_currentPath);
            _suppressDriveEvents = true;
            try
            {
                for (int i = 0; i < _driveRoots.Count; i++)
                {
                    if (PathUtil.SamePath(_driveRoots[i], root))
                    {
                        if (_driveCombo.SelectedIndex != i) _driveCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            catch { }
            finally
            {
                _suppressDriveEvents = false;
            }
        }

        private void FillFolderList()
        {
            _lastEnumerationFailed = false;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                string path = PathUtil.Normalize(_currentPath);
                string[] dirs = null;
                if (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        dirs = Directory.GetDirectories(PathUtil.ToLongPath(path));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("读取目录失败: " + path + " -> " + ex.GetType().Name + ": " + ex.Message);
                        // 兜底：个别系统/安全软件对 \\?\ 长路径前缀支持不一致，退回普通路径再试一次
                        try
                        {
                            dirs = Directory.GetDirectories(path);
                        }
                        catch (Exception ex2)
                        {
                            Log.Warn("读取目录再次失败: " + path + " -> " + ex2.GetType().Name + ": " + ex2.Message);
                            _lastEnumerationFailed = true;
                            dirs = null;
                        }
                    }
                }
                else
                {
                    _lastEnumerationFailed = true;
                }
                if (dirs == null) dirs = new string[0];
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string dir = dirs[i];
                    try
                    {
                        FileAttributes attr = File.GetAttributes(dir);
                        if ((attr & FileAttributes.System) != 0) continue;
                        string name = Path.GetFileName(dir);
                        if (string.IsNullOrEmpty(name)) continue;
                        string dateText = string.Empty;
                        try { dateText = Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd  HH:mm"); }
                        catch { }
                        ListViewItem item = new ListViewItem(name, 0);
                        item.SubItems.Add(dateText);
                        item.Tag = dir;
                        _list.Items.Add(item);
                    }
                    catch { }
                }
            }
            finally
            {
                _list.EndUpdate();
            }
            AdjustListColumns();
            bool empty = _list.Items.Count == 0;
            _emptyLabel.Text = _lastEnumerationFailed
                ? "无法读取这个文件夹（可能被系统或安全软件限制）\r\n请点「刷新」重试，或换一个文件夹"
                : "这个文件夹里没有子文件夹";
            _emptyLabel.Visible = empty;
            _list.Visible = !empty;
            if (empty) _emptyLabel.BringToFront();
        }

        private void UpdateSpaceInfo()
        {
            long free = -1;
            long total = -1;
            try
            {
                DriveInfoItem item = null;
                try { item = DriveAnalyzer.GetDriveForPath(_currentPath); }
                catch { item = null; }
                if (item != null)
                {
                    free = item.FreeBytes;
                    total = item.TotalBytes;
                }
                else
                {
                    try
                    {
                        DriveInfo d = new DriveInfo(PathUtil.GetDriveRoot(_currentPath));
                        free = d.AvailableFreeSpace;
                        total = d.TotalSize;
                    }
                    catch { }
                }
            }
            catch { }
            if (free >= 0 && total > 0)
                _spaceLabel.Text = "可用空间 " + TextUtil.FormatBytes(free) + " / " + TextUtil.FormatBytes(total);
            else if (free >= 0)
                _spaceLabel.Text = "可用空间 " + TextUtil.FormatBytes(free);
            else
                _spaceLabel.Text = "可用空间 未知";
        }

        private void NavigateFromPathBox()
        {
            Navigate(_pathBox.Text.Trim());
        }

        private void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateFromPathBox();
            }
        }

        private void DriveCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_loadingDrives || _suppressDriveEvents) return;
            int index = _driveCombo.SelectedIndex;
            if (index < 0 || index >= _driveRoots.Count) return;
            Navigate(_driveRoots[index]);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (_list.SelectedItems.Count > 0) EnterSelectedFolder();
                else ConfirmSelection();
            }
            else if (e.KeyCode == Keys.Back)
            {
                e.SuppressKeyPress = true;
                GoParent();
            }
        }

        private void EnterSelectedFolder()
        {
            if (_list.SelectedItems.Count == 0) return;
            string path = _list.SelectedItems[0].Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            Navigate(path);
            TrySelectNodeForPath(path);
        }

        private void GoParent()
        {
            try
            {
                DirectoryInfo parent = Directory.GetParent(_currentPath);
                if (parent == null) return;
                Navigate(parent.FullName);
                TrySelectNodeForPath(parent.FullName);
            }
            catch { }
        }

        // ---------------------------------------------------------------- 新建 / 重命名 / 选择

        private void CreateNewFolder()
        {
            if (string.IsNullOrEmpty(_currentPath) || !Directory.Exists(_currentPath))
            {
                ShowError("请先选择一个有效的文件夹。");
                return;
            }
            if (!FileUtil.HasWriteAccess(_currentPath))
            {
                ShowError("当前文件夹没有写入权限，无法新建文件夹。\r\n请换一个文件夹，或以管理员身份运行本程序。");
                return;
            }
            string folderName = null;
            string createdPath = null;
            Func<string, string> validator = delegate(string name)
            {
                if (!PathUtil.IsValidFolderName(name)) return "文件夹名不合法：不能包含 \\ / : * ? \" < > | 等字符，且不能以空格或点结尾。";
                string target = Path.Combine(_currentPath, name);
                if (Directory.Exists(target)) return "同名文件夹已存在，请换一个名字。";
                if (File.Exists(target)) return "同名文件已存在，请换一个名字。";
                return null;
            };
            if (!InputDialog.Prompt(this, "新建文件夹", "位置：" + _currentPath, "新建文件夹", validator, out folderName)) return;
            try
            {
                createdPath = Path.Combine(_currentPath, folderName);
                Directory.CreateDirectory(createdPath);
            }
            catch (Exception ex)
            {
                Log.Error("新建文件夹失败: " + createdPath + " -> " + ex.Message, ex);
                ShowError("新建文件夹失败：" + ex.Message);
                return;
            }
            Log.Info("新建文件夹成功: " + createdPath);
            // 关键修复：新建后立即刷新右侧列表 + 左侧目录树，并自动进入新文件夹，
            // 让用户马上看到结果，且点「选择此文件夹」就能选中这个新文件夹。
            FillFolderList();
            SelectListItem(createdPath);
            RefreshTree();
            Navigate(createdPath);
            TrySelectNodeForPath(createdPath);
            if (_list.Items.Count == 0 && !_lastEnumerationFailed)
            {
                _emptyLabel.Text = "已新建文件夹：" + folderName + "\r\n点「选择此文件夹」即可把它作为搬移目标";
            }
        }

        private void RenameSelectedFolder()
        {
            if (_list.SelectedItems.Count == 0)
            {
                ShowError("请先在右侧列表中选中要重命名的文件夹。");
                return;
            }
            string oldPath = _list.SelectedItems[0].Tag as string;
            if (string.IsNullOrEmpty(oldPath) || !Directory.Exists(oldPath))
            {
                ShowError("选中的文件夹已不存在，请刷新后重试。");
                return;
            }
            string oldName = Path.GetFileName(oldPath);
            string newName = null;
            Func<string, string> validator = delegate(string name)
            {
                if (!PathUtil.IsValidFolderName(name)) return "文件夹名不合法：不能包含 \\ / : * ? \" < > | 等字符，且不能以空格或点结尾。";
                if (string.Equals(name, oldName, StringComparison.Ordinal)) return "新名称与原名称相同。";
                string target = Path.Combine(_currentPath, name);
                if (Directory.Exists(target)) return "同名文件夹已存在，请换一个名字。";
                if (File.Exists(target)) return "同名文件已存在，请换一个名字。";
                return null;
            };
            if (!InputDialog.Prompt(this, "重命名文件夹", "位置：" + _currentPath, oldName, validator, out newName)) return;
            string newPath = Path.Combine(_currentPath, newName);
            try
            {
                Directory.Move(oldPath, newPath);
            }
            catch (Exception ex)
            {
                Log.Error("重命名文件夹失败", ex);
                ShowError("重命名失败：" + ex.Message + "\r\n（文件夹可能正被其他程序占用）");
                return;
            }
            FillFolderList();
            SelectListItem(newPath);
            Log.Info("重命名文件夹成功: " + oldPath + " -> " + newPath);
            RefreshTree();
            TrySelectNodeForPath(_currentPath);
        }

        /// <summary>重建左侧目录树并定位到当前路径（新建/重命名后保证左右同步，避免"看不到新文件夹"）</summary>
        private void RefreshTree()
        {
            try
            {
                _suppressTreeEvents = true;
                PopulateTree(_currentPath);
            }
            catch (Exception ex)
            {
                Log.Warn("刷新目录树失败: " + ex.Message);
            }
            finally
            {
                _suppressTreeEvents = false;
            }
        }

        private void SelectListItem(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    string itemPath = _list.Items[i].Tag as string;
                    if (itemPath != null && PathUtil.SamePath(itemPath, path))
                    {
                        _list.Items[i].Selected = true;
                        _list.Items[i].Focused = true;
                        _list.Items[i].EnsureVisible();
                        return;
                    }
                }
            }
            catch { }
        }

        private void ConfirmSelection()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentPath) || !Directory.Exists(_currentPath))
                {
                    ShowError("请先选择一个有效的文件夹。");
                    return;
                }
                if (!FileUtil.HasWriteAccess(_currentPath))
                {
                    ShowError("当前文件夹不可写入，无法作为搬移目标。\r\n请换一个文件夹，或以管理员身份运行本程序。");
                    return;
                }
                SelectedPath = _currentPath;
                try
                {
                    AppSettings.LastTargetDirectory = _currentPath;
                    AppSettings.Save();
                }
                catch { }
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                ShowError("确认文件夹失败：" + ex.Message);
            }
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, "选择目标文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
