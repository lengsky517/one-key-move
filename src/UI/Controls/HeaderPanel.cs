// T3 拥有：顶部品牌栏（Logo + 标题 + 副标题 + 权限状态胶囊 + 帮助链接）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using YijianNuodong.Core;

namespace YijianNuodong.UI.Controls
{
    public class HeaderPanel : Panel
    {
        private bool _elevated;
        private Rectangle _helpRect = Rectangle.Empty;
        private bool _helpHover;

        public event Action HelpClicked;

        public HeaderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.White;
            Height = 92;
        }

        public bool Elevated
        {
            get { return _elevated; }
            set { _elevated = value; Invalidate(); }
        }

        public void RefreshState()
        {
            bool elevated = false;
            try { elevated = ElevationUtil.IsElevated; }
            catch { elevated = false; }
            Elevated = elevated;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Color.White))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            // 底部 1px 分隔线
            using (Pen pen = new Pen(Theme.Border, 1f))
            {
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }

            // Logo：40x40 主色渐变圆角方块 + 白色“移动”箭头
            int logoSize = 42;
            Rectangle logo = new Rectangle(28, (Height - logoSize) / 2, logoSize, logoSize);
            using (GraphicsPath path = Theme.RoundedRect(logo, 11))
            using (LinearGradientBrush brush = new LinearGradientBrush(logo,
                Color.FromArgb(96, 165, 250), Theme.Primary, LinearGradientMode.ForwardDiagonal))
            {
                g.FillPath(brush, path);
            }
            // 内部图形：左下小方块 + 指向右上的箭头
            int cx = logo.X;
            int cy = logo.Y;
            Rectangle innerBox = new Rectangle(cx + 10, cy + 25, 11, 11);
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                g.FillRectangle(brush, innerBox);
            }
            Theme.DrawArrowUpRight(g, new Rectangle(cx + 15, cy + 8, 20, 20), Color.White, 2.2f);

            // 标题与副标题
            Rectangle titleRect = new Rectangle(84, 14, 520, 44);
            TextFormatFlags titleFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                         TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, "一键挪动", Theme.H1, titleRect, Theme.TextPrimary, titleFlags);

            Rectangle subRect = new Rectangle(86, 58, 640, 22);
            TextFormatFlags subFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                       TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, "把装在 C 盘的软件搬到其他盘 · 快捷方式照样能用",
                Theme.Small, subRect, Theme.TextSecondary, subFlags);

            // 右上：权限胶囊 + 帮助链接
            string helpText = "使用帮助";
            Size helpSize = TextRenderer.MeasureText(helpText, Theme.Small);
            int helpWidth = helpSize.Width + 8;
            int helpHeight = 22;
            int right = Width - 28;
            _helpRect = new Rectangle(right - helpWidth, (Height - helpHeight) / 2, helpWidth, helpHeight);

            string pillText = _elevated ? "管理员模式" : "普通模式";
            Size pillTextSize = TextRenderer.MeasureText(pillText, Theme.SmallBold);
            int pillWidth = pillTextSize.Width + 34;
            int pillHeight = 26;
            int pillRight = _helpRect.Left - 22;
            Rectangle pill = new Rectangle(pillRight - pillWidth, (Height - pillHeight) / 2, pillWidth, pillHeight);
            Color pillBack = _elevated ? Theme.SuccessLight : Color.FromArgb(255, 247, 237);
            Color pillFore = _elevated ? Theme.Success : Color.FromArgb(194, 65, 12);
            Theme.FillRoundRect(g, pill, pillHeight / 2, pillBack);
            Theme.FillCircle(g, new Rectangle(pill.X + 11, pill.Y + (pillHeight - 7) / 2, 7, 7), pillFore);
            Rectangle pillTextRect = new Rectangle(pill.X + 24, pill.Y, pill.Width - 30, pill.Height);
            TextFormatFlags pflags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                     TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, pillText, Theme.SmallBold, pillTextRect, pillFore, pflags);

            Color helpColor = _helpHover ? Theme.Primary : Theme.TextSecondary;
            TextFormatFlags hflags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                     TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, helpText, Theme.Small, _helpRect, helpColor, hflags);
            if (_helpHover)
            {
                int y = _helpRect.Bottom - 3;
                using (Pen pen = new Pen(Theme.Primary, 1f))
                {
                    g.DrawLine(pen, _helpRect.Left, y, _helpRect.Right - 4, y);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = _helpRect.Contains(e.Location);
            if (over != _helpHover)
            {
                _helpHover = over;
                Cursor = over ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_helpHover)
            {
                _helpHover = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && _helpRect.Contains(e.Location))
            {
                Action handler = HelpClicked;
                if (handler != null) handler();
            }
        }
    }
}
