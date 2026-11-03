// T3 拥有：圆角标签胶囊（状态标签：运行中 / 未运行 / C 盘等）
using System;
using System.Drawing;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    public class PillLabel : Control
    {
        private Color _pillBack = Theme.SuccessLight;
        private Color _pillFore = Theme.Success;
        private int _dotColor = 0;

        public PillLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.SmallBold;
            Height = 24;
            Width = 80;
            TabStop = false;
        }

        public Color PillBack
        {
            get { return _pillBack; }
            set { _pillBack = value; Invalidate(); }
        }

        public Color PillFore
        {
            get { return _pillFore; }
            set { _pillFore = value; Invalidate(); }
        }

        /// <summary>是否在左侧绘制一个小圆点</summary>
        public bool ShowDot
        {
            get { return _dotColor != 0; }
            set { _dotColor = value ? 1 : 0; Invalidate(); }
        }

        public void SetState(string text, Color back, Color fore)
        {
            Text = text;
            _pillBack = back;
            _pillFore = fore;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color backdrop = Color.Empty;
            Control p = Parent;
            while (p != null)
            {
                if (p.BackColor.A == 255) { backdrop = p.BackColor; break; }
                p = p.Parent;
            }
            if (backdrop == Color.Empty) backdrop = Theme.Surface;
            using (SolidBrush bg = new SolidBrush(backdrop))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRoundRect(g, r, Height / 2, _pillBack);

            int textLeft = 10;
            if (ShowDot)
            {
                int dot = 6;
                Rectangle dotRect = new Rectangle(10, (Height - dot) / 2, dot, dot);
                Theme.FillCircle(g, dotRect, _pillFore);
                textLeft = 10 + dot + 6;
            }
            Rectangle textRect = new Rectangle(textLeft, 0, Width - textLeft - 8, Height);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, Text, Font, textRect, _pillFore, flags);
        }
    }
}
