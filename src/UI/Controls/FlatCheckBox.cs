// T3 拥有：扁平复选框（自绘圆角方框 + 白色对勾）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    public class FlatCheckBox : CheckBox
    {
        private bool _hover;
        private Color _boxBack = Color.White;
        private Color _backdropColor = Color.Empty;

        public FlatCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Surface;
            Font = Theme.Body;
            ForeColor = Theme.TextPrimary;
            AutoSize = false;
            Cursor = Cursors.Hand;
            Height = 24;
        }

        /// <summary>圆角外侧的底色（默认自动取父控件底色）</summary>
        public Color BackdropColor
        {
            get { return _backdropColor; }
            set { _backdropColor = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            Invalidate();
            base.OnCheckedChanged(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        private Color ResolveBackdrop()
        {
            if (_backdropColor.A > 0) return _backdropColor;
            Control p = Parent;
            while (p != null)
            {
                if (p.BackColor.A == 255) return p.BackColor;
                p = p.Parent;
            }
            return Theme.Surface;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            Color backdrop = ResolveBackdrop();
            using (SolidBrush bg = new SolidBrush(backdrop))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            int boxSize = 18;
            int top = (Height - boxSize) / 2;
            Rectangle box = new Rectangle(0, top, boxSize, boxSize);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.RoundedRect(box, 4))
            {
                Color fill;
                Color border;
                if (!Enabled)
                {
                    fill = Theme.OptionBackground;
                    border = Theme.Border;
                }
                else if (Checked)
                {
                    fill = _hover ? Theme.PrimaryHover : Theme.Primary;
                    border = fill;
                }
                else
                {
                    fill = Theme.Surface;
                    border = _hover ? Theme.Primary : Theme.BorderStrong;
                }
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(border, 1.5f))
                {
                    g.DrawPath(pen, path);
                }
            }

            if (Checked)
            {
                Rectangle checkRect = new Rectangle(box.Left + 2, box.Top + 2, box.Width - 4, box.Height - 4);
                Theme.DrawCheck(g, checkRect, Color.White, 2.0f);
            }

            Color textColor = Enabled ? ForeColor : Theme.TextMuted;
            Rectangle textRect = new Rectangle(boxSize + 10, 0, Width - boxSize - 10, Height);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, Text, Font, textRect, textColor, flags);
        }
    }
}
