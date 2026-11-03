// T3 拥有：带圆角边框的容器（用于搜索框、列表、只读路径框等）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    public class BorderedPanel : Panel
    {
        private int _cornerRadius = 10;
        private Color _fillColor = Theme.Surface;
        private Color _borderColor = Theme.Border;
        private Color _backdropColor = Theme.Surface;
        private bool _showSearchGlyph;

        public BorderedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public int CornerRadius
        {
            get { return _cornerRadius; }
            set { _cornerRadius = value; Invalidate(); }
        }

        public Color FillColor
        {
            get { return _fillColor; }
            set { _fillColor = value; Invalidate(); }
        }

        public Color BorderColor
        {
            get { return _borderColor; }
            set { _borderColor = value; Invalidate(); }
        }

        public Color BackdropColor
        {
            get { return _backdropColor; }
            set { _backdropColor = value; Invalidate(); }
        }

        /// <summary>左侧是否绘制一个放大镜图标（搜索框）</summary>
        public bool ShowSearchGlyph
        {
            get { return _showSearchGlyph; }
            set { _showSearchGlyph = value; Invalidate(); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(_backdropColor))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            Theme.PaintCard(g, ClientRectangle, _cornerRadius, _fillColor, Color.Transparent, 0f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width > 0 && r.Height > 0 && _borderColor.A > 0)
            {
                using (GraphicsPath path = Theme.RoundedRect(r, _cornerRadius))
                using (Pen pen = new Pen(_borderColor, 1f))
                {
                    g.DrawPath(pen, path);
                }
            }

            if (_showSearchGlyph)
            {
                int cy = Height / 2;
                int cx = 20;
                using (Pen pen = new Pen(Theme.TextMuted, 1.6f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawEllipse(pen, cx - 6, cy - 7, 12, 12);
                    g.DrawLine(pen, cx + 4, cy + 4, cx + 9, cy + 9);
                }
            }

            base.OnPaint(e);
        }
    }
}
