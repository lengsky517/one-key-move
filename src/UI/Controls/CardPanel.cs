// T3 拥有：圆角卡片容器（白色/浅灰底 + 细边框 + 圆角）
using System;
using System.Drawing;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    /// <summary>
    /// 圆角卡片容器。OnPaintBackground 负责填充“外部底色 + 圆角卡片底色”，
    /// 这样子控件即使使用透明背景也不会出现方形灰角。
    /// </summary>
    public class CardPanel : Panel
    {
        private int _cornerRadius = 12;
        private Color _fillColor = Theme.Surface;
        private Color _borderColor = Theme.Border;
        private Color _backdropColor = Theme.Background;
        private bool _drawBorder = true;

        public CardPanel()
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

        /// <summary>圆角外侧露出的底色（应与父容器底色一致）</summary>
        public Color BackdropColor
        {
            get { return _backdropColor; }
            set { _backdropColor = value; Invalidate(); }
        }

        public bool DrawBorder
        {
            get { return _drawBorder; }
            set { _drawBorder = value; Invalidate(); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // 先用父容器底色铺满，再画圆角卡片
            Color backdrop = _backdropColor;
            if (backdrop.A == 0)
            {
                if (Parent != null && Parent.BackColor.A == 255) backdrop = Parent.BackColor;
                else backdrop = Theme.Background;
            }
            using (SolidBrush bg = new SolidBrush(backdrop))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            Theme.PaintCard(g, ClientRectangle, _cornerRadius, _fillColor, Color.Transparent, 0f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_drawBorder && _borderColor.A > 0)
            {
                Rectangle r = new Rectangle(ClientRectangle.X, ClientRectangle.Y,
                    ClientRectangle.Width - 1, ClientRectangle.Height - 1);
                if (r.Width > 0 && r.Height > 0)
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (System.Drawing.Drawing2D.GraphicsPath path = Theme.RoundedRect(r, _cornerRadius))
                    using (Pen pen = new Pen(_borderColor, 1f))
                    {
                        e.Graphics.DrawPath(pen, path);
                    }
                }
            }
            base.OnPaint(e);
        }
    }
}
