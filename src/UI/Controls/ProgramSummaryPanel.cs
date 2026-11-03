// T3 拥有：第 2 步顶部「即将搬移」程序信息卡片
using System;
using System.Drawing;
using System.Windows.Forms;
using YijianNuodong.Core;

namespace YijianNuodong.UI.Controls
{
    public class ProgramSummaryPanel : CardPanel
    {
        private readonly ProgramIconBox _icon;
        private readonly Label _nameLabel;
        private readonly Label _metaLabel;
        private readonly Label _pathLabel;
        private readonly PillLabel _runningTag;
        private readonly Label _sizeLabel;

        public ProgramSummaryPanel()
        {
            CornerRadius = 10;
            FillColor = Theme.Surface;
            BorderColor = Theme.Border;
            BackdropColor = Theme.Surface;
            Height = 96;
            Padding = new Padding(0);

            _icon = new ProgramIconBox();
            _icon.Size = new Size(48, 48);
            _icon.Location = new Point(20, 24);
            _icon.BackColor = Theme.Surface;
            Controls.Add(_icon);

            _nameLabel = new Label();
            _nameLabel.AutoSize = false;
            _nameLabel.AutoEllipsis = true;
            _nameLabel.Font = Theme.H2;
            _nameLabel.ForeColor = Theme.TextPrimary;
            _nameLabel.BackColor = Theme.Surface;
            _nameLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_nameLabel);

            _metaLabel = new Label();
            _metaLabel.AutoSize = false;
            _metaLabel.AutoEllipsis = true;
            _metaLabel.Font = Theme.Small;
            _metaLabel.ForeColor = Theme.TextSecondary;
            _metaLabel.BackColor = Theme.Surface;
            _metaLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_metaLabel);

            _pathLabel = new Label();
            _pathLabel.AutoSize = false;
            _pathLabel.AutoEllipsis = true;
            _pathLabel.Font = Theme.Small;
            _pathLabel.ForeColor = Theme.TextMuted;
            _pathLabel.BackColor = Theme.Surface;
            _pathLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_pathLabel);

            _runningTag = new PillLabel();
            _runningTag.Size = new Size(88, 24);
            _runningTag.ShowDot = true;
            _runningTag.BackColor = Theme.Surface;
            Controls.Add(_runningTag);

            _sizeLabel = new Label();
            _sizeLabel.AutoSize = false;
            _sizeLabel.Font = Theme.Small;
            _sizeLabel.ForeColor = Theme.TextSecondary;
            _sizeLabel.BackColor = Theme.Surface;
            _sizeLabel.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_sizeLabel);
        }

        public void SetProgram(InstalledProgram program)
        {
            if (program == null)
            {
                _nameLabel.Text = "未选择软件";
                _metaLabel.Text = string.Empty;
                _pathLabel.Text = string.Empty;
                return;
            }
            _nameLabel.Text = program.DisplayName;
            string version = string.IsNullOrEmpty(program.Version) ? "版本未知" : "版本 " + program.Version;
            string publisher = string.IsNullOrEmpty(program.Publisher) ? "发布者未知" : program.Publisher;
            _metaLabel.Text = version + "  ·  " + publisher;
            string drive = string.IsNullOrEmpty(program.DriveRoot) ? PathUtil.GetDriveRoot(program.InstallPath) : program.DriveRoot;
            string driveText = string.IsNullOrEmpty(drive) ? string.Empty : "（" + drive.TrimEnd('\\') + " 盘）";
            _pathLabel.Text = "原安装位置" + driveText + "：" + (program.InstallPath ?? string.Empty);
            _icon.SetIcon(program.Icon);
            _icon.FallbackText = string.IsNullOrEmpty(program.DisplayName) ? "程" : program.DisplayName.Substring(0, 1);
            SetRunning(program.IsRunning);
            SetSizeText(program.SizeBytes);
        }

        public void SetIcon(Icon icon)
        {
            _icon.SetIcon(icon);
        }

        public void SetRunning(bool running)
        {
            if (running)
            {
                _runningTag.SetState("运行中", Color.FromArgb(255, 247, 237), Color.FromArgb(194, 65, 12));
            }
            else
            {
                _runningTag.SetState("未运行", Theme.SuccessLight, Theme.Success);
            }
        }

        public void SetSizeText(long sizeBytes)
        {
            if (sizeBytes < 0) _sizeLabel.Text = "占用空间 计算中…";
            else _sizeLabel.Text = "占用空间 " + TextUtil.FormatBytes(sizeBytes);
            LayoutPositions();
        }

        public void SetPathText(string text)
        {
            _pathLabel.Text = text;
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutPositions();
        }

        private void LayoutPositions()
        {
            if (_nameLabel == null) return;
            int rightArea = 236;
            int textWidth = Width - 84 - rightArea;
            if (textWidth < 80) textWidth = 80;
            _nameLabel.Location = new Point(84, 16);
            _nameLabel.Size = new Size(textWidth, 28);
            _metaLabel.Location = new Point(84, 43);
            _metaLabel.Size = new Size(textWidth, 20);
            _pathLabel.Location = new Point(84, 64);
            _pathLabel.Size = new Size(textWidth, 20);

            int rightX = Width - 20 - 200;
            if (rightX < 84 + textWidth + 8) rightX = 84 + textWidth + 8;
            _sizeLabel.Location = new Point(rightX, 50);
            _sizeLabel.Size = new Size(200, 20);
            _runningTag.Location = new Point(Width - 20 - 88, 18);
            _runningTag.BackColor = Theme.Surface;
        }
    }
}
