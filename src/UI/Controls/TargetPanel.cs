// T3 拥有：第 2 步「移动到」区域（父文件夹 / 新文件夹名 / 路径预览 / 磁盘容量）
using System;
using System.Drawing;
using System.Windows.Forms;
using YijianNuodong.Core;

namespace YijianNuodong.UI.Controls
{
    public class TargetPanel : Panel
    {
        private readonly Label _parentTitle;
        private readonly BorderedPanel _parentBoxHost;
        private readonly TextBox _parentBox;
        private readonly FlatButton _browseButton;
        private readonly Label _nameTitle;
        private readonly BorderedPanel _nameBoxHost;
        private readonly TextBox _nameBox;
        private readonly Label _previewLabel;
        private readonly DiskBar _diskBar;
        private readonly Label _spaceLabel;

        public event Action BrowseClicked;
        public event Action TargetChanged;

        public TargetPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Surface;
            Height = 210;

            _parentTitle = new Label();
            _parentTitle.AutoSize = false;
            _parentTitle.Font = Theme.BodyBold;
            _parentTitle.ForeColor = Theme.TextPrimary;
            _parentTitle.BackColor = Theme.Surface;
            _parentTitle.TextAlign = ContentAlignment.MiddleLeft;
            _parentTitle.Text = "目标父文件夹";
            Controls.Add(_parentTitle);

            _parentBoxHost = new BorderedPanel();
            _parentBoxHost.CornerRadius = 8;
            _parentBoxHost.FillColor = Theme.Surface;
            _parentBoxHost.BackdropColor = Theme.Surface;
            _parentBoxHost.BorderColor = Theme.BorderStrong;
            Controls.Add(_parentBoxHost);

            _parentBox = new TextBox();
            _parentBox.BorderStyle = BorderStyle.None;
            _parentBox.Font = Theme.Body;
            _parentBox.ForeColor = Theme.TextPrimary;
            _parentBox.BackColor = Theme.Surface;
            _parentBox.ReadOnly = true;
            _parentBox.ShortcutsEnabled = true;
            _parentBoxHost.Controls.Add(_parentBox);

            _browseButton = new FlatButton();
            _browseButton.Text = "浏览…";
            _browseButton.Style = FlatButtonStyle.Secondary;
            _browseButton.Height = 38;
            _browseButton.Width = 88;
            _browseButton.Font = Theme.BodyBold;
            _browseButton.BackdropColor = Theme.Surface;
            _browseButton.Click += delegate
            {
                Action handler = BrowseClicked;
                if (handler != null) handler();
            };
            Controls.Add(_browseButton);

            _nameTitle = new Label();
            _nameTitle.AutoSize = false;
            _nameTitle.Font = Theme.BodyBold;
            _nameTitle.ForeColor = Theme.TextPrimary;
            _nameTitle.BackColor = Theme.Surface;
            _nameTitle.TextAlign = ContentAlignment.MiddleLeft;
            _nameTitle.Text = "新文件夹名";
            Controls.Add(_nameTitle);

            _nameBoxHost = new BorderedPanel();
            _nameBoxHost.CornerRadius = 8;
            _nameBoxHost.FillColor = Theme.Surface;
            _nameBoxHost.BackdropColor = Theme.Surface;
            _nameBoxHost.BorderColor = Theme.BorderStrong;
            Controls.Add(_nameBoxHost);

            _nameBox = new TextBox();
            _nameBox.BorderStyle = BorderStyle.None;
            _nameBox.Font = Theme.Body;
            _nameBox.ForeColor = Theme.TextPrimary;
            _nameBox.BackColor = Theme.Surface;
            _nameBox.MaxLength = 120;
            _nameBox.TextChanged += delegate
            {
                UpdatePreview();
                RaiseTargetChanged();
            };
            _nameBoxHost.Controls.Add(_nameBox);

            _previewLabel = new Label();
            _previewLabel.AutoSize = false;
            _previewLabel.Font = Theme.Small;
            _previewLabel.ForeColor = Theme.TextMuted;
            _previewLabel.BackColor = Theme.Surface;
            _previewLabel.TextAlign = ContentAlignment.MiddleLeft;
            _previewLabel.AutoEllipsis = true;
            Controls.Add(_previewLabel);

            _diskBar = new DiskBar();
            _diskBar.BackColor = Theme.Surface;
            Controls.Add(_diskBar);

            _spaceLabel = new Label();
            _spaceLabel.AutoSize = false;
            _spaceLabel.Font = Theme.Small;
            _spaceLabel.ForeColor = Theme.TextSecondary;
            _spaceLabel.BackColor = Theme.Surface;
            _spaceLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_spaceLabel);

            LayoutPositions();
        }

        public string ParentDirectory
        {
            get { return _parentBox.Text.Trim(); }
        }

        public string FolderName
        {
            get { return _nameBox.Text.Trim(); }
        }

        public string TargetPath
        {
            get
            {
                if (string.IsNullOrEmpty(ParentDirectory)) return string.Empty;
                return PathUtil.CombineTarget(ParentDirectory, FolderName);
            }
        }

        public TextBox FolderNameTextBox
        {
            get { return _nameBox; }
        }

        public void SetParentDirectory(string directory)
        {
            string dir = PathUtil.Normalize(directory);
            if (_parentBox.Text == dir) return;
            _parentBox.Text = dir;
            // 光标回到开头，长路径优先显示盘符
            try { _parentBox.SelectionStart = 0; } catch { }
            UpdatePreview();
            RaiseTargetChanged();
        }

        public void SetFolderName(string name)
        {
            if (_nameBox.Text == name) return;
            _nameBox.Text = name;
            UpdatePreview();
        }

        public void FocusFolderName()
        {
            try
            {
                _nameBox.Focus();
                _nameBox.SelectionStart = 0;
                _nameBox.SelectionLength = _nameBox.Text.Length;
            }
            catch { }
        }

        public void SetDrive(DriveInfoItem drive)
        {
            _diskBar.SetDrive(drive);
        }

        public void SetSpaceInfo(long requiredBytes, long freeBytes)
        {
            if (requiredBytes < 0)
            {
                _spaceLabel.Text = "本次需要 未知 · 可用 " + (freeBytes < 0 ? "未知" : TextUtil.FormatBytes(freeBytes));
                _spaceLabel.ForeColor = Theme.TextSecondary;
                _diskBar.Insufficient = false;
                return;
            }
            if (freeBytes < 0)
            {
                _spaceLabel.Text = "本次需要 " + TextUtil.FormatBytes(requiredBytes) + " · 可用 未知";
                _spaceLabel.ForeColor = Theme.TextSecondary;
                _diskBar.Insufficient = false;
                return;
            }
            bool enough = freeBytes > requiredBytes + 200L * 1024 * 1024;
            _spaceLabel.Text = "本次需要 " + TextUtil.FormatBytes(requiredBytes) + " · 可用 " +
                               TextUtil.FormatBytes(freeBytes) + (enough ? string.Empty : " · 目标盘空间不足，请更换位置");
            _spaceLabel.ForeColor = enough ? Theme.TextSecondary : Theme.Danger;
            _diskBar.Insufficient = !enough;
        }

        private void UpdatePreview()
        {
            if (string.IsNullOrEmpty(ParentDirectory))
            {
                _previewLabel.Text = "最终路径：请先选择目标父文件夹";
                _previewLabel.ForeColor = Theme.TextMuted;
                return;
            }
            string leaf = FolderName;
            if (string.IsNullOrEmpty(leaf))
            {
                _previewLabel.Text = "最终路径：请填写文件夹名";
                _previewLabel.ForeColor = Theme.Danger;
                return;
            }
            if (!PathUtil.IsValidFolderName(leaf))
            {
                _previewLabel.Text = "文件夹名含非法字符，请修改";
                _previewLabel.ForeColor = Theme.Danger;
                return;
            }
            string target = PathUtil.CombineTarget(ParentDirectory, leaf);
            _previewLabel.Text = "最终路径：" + target;
            _previewLabel.ForeColor = Theme.TextMuted;
            try
            {
                if (System.IO.Directory.Exists(target) && !FileUtil.IsDirectoryEmpty(target))
                {
                    _previewLabel.Text = "目标文件夹已存在且不为空，请换个名字：" + target;
                    _previewLabel.ForeColor = Theme.Warning;
                }
            }
            catch { }
        }

        private void RaiseTargetChanged()
        {
            Action handler = TargetChanged;
            if (handler != null) handler();
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutPositions();
        }

        private void LayoutPositions()
        {
            if (_parentTitle == null) return;
            int w = Width;
            _parentTitle.Location = new Point(0, 0);
            _parentTitle.Size = new Size(240, 22);

            int browseWidth = 88;
            _browseButton.Location = new Point(w - browseWidth, 24);
            _browseButton.Size = new Size(browseWidth, 38);
            _parentBoxHost.Location = new Point(0, 24);
            _parentBoxHost.Size = new Size(w - browseWidth - 10, 38);
            _parentBox.Location = new Point(12, 8);
            _parentBox.Size = new Size(Math.Max(40, w - browseWidth - 10 - 24), 22);

            _nameTitle.Location = new Point(0, 74);
            _nameTitle.Size = new Size(140, 22);
            _nameBoxHost.Location = new Point(0, 96);
            _nameBoxHost.Size = new Size(340, 38);
            _nameBox.Location = new Point(12, 8);
            _nameBox.Size = new Size(316, 22);

            _previewLabel.Location = new Point(352, 96);
            _previewLabel.Size = new Size(Math.Max(60, w - 352), 38);

            _diskBar.Location = new Point(0, 144);
            _diskBar.Size = new Size(w, 46);
            _spaceLabel.Location = new Point(0, 190);
            _spaceLabel.Size = new Size(w, 20);
        }
    }
}
