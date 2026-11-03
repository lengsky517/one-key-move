// T3 拥有：第 2 步选项卡片（保留目录联接 / 创建还原点 / 更新快捷方式与注册表）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using YijianNuodong.Core;

namespace YijianNuodong.UI.Controls
{
    public class OptionsPanel : Panel
    {
        private readonly FlatCheckBox _junctionCheck;
        private readonly Label _junctionDesc;
        private readonly FlatCheckBox _restoreCheck;
        private readonly Label _restoreDesc;
        private readonly FlatCheckBox _updateCheck;
        private readonly Label _updateDesc;

        public OptionsPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Surface;
            Height = 128;

            _junctionCheck = CreateCheck("在原位置保留目录联接（推荐）", 6);
            Controls.Add(_junctionCheck);
            _junctionDesc = CreateDesc("搬移后原路径仍然可用，绝大多数软件无需重装即可正常使用；不勾选则原路径失效，部分软件可能无法启动", 27);
            Controls.Add(_junctionDesc);

            _restoreCheck = CreateCheck("搬移前创建系统还原点（尽力而为）", 44);
            Controls.Add(_restoreCheck);
            _restoreDesc = CreateDesc("需要系统保护功能已开启；创建失败不会影响搬移流程", 65);
            Controls.Add(_restoreDesc);

            _updateCheck = CreateCheck("自动更新快捷方式和注册表路径", 82);
            Controls.Add(_updateCheck);
            _updateDesc = CreateDesc("桌面与开始菜单快捷方式、卸载信息等会同步指向新位置，避免“找不到程序”", 103);
            Controls.Add(_updateDesc);

            LayoutPositions();
        }

        private FlatCheckBox CreateCheck(string text, int top)
        {
            FlatCheckBox check = new FlatCheckBox();
            check.Text = text;
            check.Font = Theme.BodyBold;
            check.ForeColor = Theme.TextPrimary;
            check.BackdropColor = Theme.OptionBackground;
            check.Location = new Point(16, top);
            check.Height = 24;
            return check;
        }

        private Label CreateDesc(string text, int top)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = Theme.Small;
            label.ForeColor = Theme.TextMuted;
            label.BackColor = Theme.OptionBackground;
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Location = new Point(44, top);
            label.Height = 16;
            return label;
        }

        public bool CreateJunction
        {
            get { return _junctionCheck.Checked; }
            set { _junctionCheck.Checked = value; }
        }

        public bool CreateRestorePoint
        {
            get { return _restoreCheck.Checked; }
            set { _restoreCheck.Checked = value; }
        }

        public bool UpdateReferences
        {
            get { return _updateCheck.Checked; }
            set { _updateCheck.Checked = value; }
        }

        public void ApplyDefaults()
        {
            try
            {
                _junctionCheck.Checked = AppSettings.CreateJunctionByDefault;
                _restoreCheck.Checked = AppSettings.CreateRestorePointByDefault;
                _updateCheck.Checked = AppSettings.UpdateShortcutsByDefault && AppSettings.UpdateRegistryByDefault;
            }
            catch
            {
                _junctionCheck.Checked = true;
                _restoreCheck.Checked = true;
                _updateCheck.Checked = true;
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutPositions();
        }

        private void LayoutPositions()
        {
            if (_junctionCheck == null) return;
            int w = Width;
            _junctionCheck.Width = w - 32;
            _junctionDesc.Width = Math.Max(80, w - 32 - 28);
            _restoreCheck.Width = w - 32;
            _restoreDesc.Width = Math.Max(80, w - 32 - 28);
            _updateCheck.Width = w - 32;
            _updateDesc.Width = Math.Max(80, w - 32 - 28);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(Theme.Surface))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            Theme.PaintCard(g, ClientRectangle, 10, Theme.OptionBackground, Theme.Border, 1f);
        }
    }
}
