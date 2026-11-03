// T3 拥有：扁平圆角按钮（主按钮 / 次级按钮 / 文字链接 / 危险按钮）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    public enum FlatButtonStyle
    {
        Primary = 0,
        Secondary = 1,
        Ghost = 2,
        Danger = 3,
        Link = 4
    }

    /// <summary>自绘扁平按钮，支持圆角、悬停/按下状态与禁用灰显。</summary>
    public class FlatButton : Button
    {
        private FlatButtonStyle _style = FlatButtonStyle.Primary;
        private int _cornerRadius = 8;
        private bool _hover;
        private bool _pressed;
        private Color _backdropColor = Color.Empty;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Theme.Surface;
            Cursor = Cursors.Hand;
            Font = Theme.BodyBold;
            Height = 36;
            TabStop = true;
        }

        public FlatButtonStyle Style
        {
            get { return _style; }
            set { _style = value; Invalidate(); }
        }

        public int CornerRadius
        {
            get { return _cornerRadius; }
            set { _cornerRadius = value; Invalidate(); }
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
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            _pressed = true;
            Invalidate();
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            Invalidate();
            base.OnTextChanged(e);
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

            Color fill;
            Color text;
            Color border;
            switch (_style)
            {
                case FlatButtonStyle.Primary:
                    if (!Enabled) { fill = Color.FromArgb(241, 245, 249); text = Theme.TextMuted; border = Color.FromArgb(226, 232, 240); }
                    else if (_pressed) { fill = Theme.PrimaryPressed; text = Color.White; border = Theme.PrimaryPressed; }
                    else if (_hover) { fill = Theme.PrimaryHover; text = Color.White; border = Theme.PrimaryHover; }
                    else { fill = Theme.Primary; text = Color.White; border = Theme.Primary; }
                    break;
                case FlatButtonStyle.Secondary:
                    if (!Enabled) { fill = Theme.OptionBackground; text = Theme.TextMuted; border = Theme.Border; }
                    else if (_pressed) { fill = Color.FromArgb(237, 242, 248); text = Theme.TextPrimary; border = Theme.BorderStrong; }
                    else if (_hover) { fill = Color.FromArgb(248, 250, 252); text = Theme.TextPrimary; border = Theme.BorderStrong; }
                    else { fill = Color.White; text = Theme.TextPrimary; border = Theme.BorderStrong; }
                    break;
                case FlatButtonStyle.Danger:
                    if (!Enabled) { fill = Color.FromArgb(241, 245, 249); text = Theme.TextMuted; border = Theme.Border; }
                    else if (_pressed) { fill = Color.FromArgb(185, 28, 28); text = Color.White; border = Color.FromArgb(185, 28, 28); }
                    else if (_hover) { fill = Color.FromArgb(239, 68, 68); text = Color.White; border = Color.FromArgb(239, 68, 68); }
                    else { fill = Color.White; text = Theme.Danger; border = Color.FromArgb(252, 165, 165); }
                    break;
                case FlatButtonStyle.Link:
                    fill = backdrop;
                    border = Color.Transparent;
                    text = Enabled ? (_hover ? Theme.PrimaryHover : Theme.Primary) : Theme.TextMuted;
                    break;
                default: // Ghost
                    if (!Enabled) { fill = backdrop; text = Theme.TextMuted; border = Color.Transparent; }
                    else if (_pressed) { fill = Theme.PrimaryLight2; text = Theme.PrimaryPressed; border = Color.Transparent; }
                    else if (_hover) { fill = Theme.PrimaryLight; text = Theme.Primary; border = Color.Transparent; }
                    else { fill = backdrop; text = Theme.Primary; border = Color.Transparent; }
                    break;
            }

            Rectangle r = new Rectangle(0, 0, Width, Height);
            using (GraphicsPath path = Theme.RoundedRect(r, _cornerRadius))
            {
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(brush, path);
                }
                if (border.A > 0)
                {
                    using (Pen pen = new Pen(border, 1f))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            Rectangle textRect = new Rectangle(8, 0, Width - 16, Height);
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, Text, Font, textRect, text, flags);

            if (Focused && ShowFocusCues && _style != FlatButtonStyle.Link)
            {
                Rectangle fr = new Rectangle(3, 3, Width - 7, Height - 7);
                using (Pen pen = new Pen(Theme.Alpha(text, 120), 1f))
                {
                    pen.DashStyle = DashStyle.Dot;
                    g.DrawRectangle(pen, fr);
                }
            }
        }
    }
}
