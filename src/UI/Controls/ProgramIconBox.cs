// T3 拥有：程序图标框（图标缺失时绘制浅蓝方块 + 首字母，避免空白）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    public class ProgramIconBox : Control
    {
        private Image _image;
        private string _fallbackText = "程";

        public ProgramIconBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(48, 48);
        }

        public Image Image
        {
            get { return _image; }
            set
            {
                _image = value;
                Invalidate();
            }
        }

        public string FallbackText
        {
            get { return _fallbackText; }
            set { _fallbackText = value; Invalidate(); }
        }

        public void SetIcon(Icon icon)
        {
            Image old = _image;
            _image = null;
            try
            {
                if (icon != null) _image = icon.ToBitmap();
            }
            catch
            {
                _image = null;
            }
            if (old != null)
            {
                try { old.Dispose(); } catch { }
            }
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

            if (_image != null)
            {
                Rectangle target = new Rectangle(0, 0, Width, Height);
                try
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawImage(_image, target);
                    return;
                }
                catch
                {
                    // 图标绘制失败时退化为占位方块
                }
            }

            Rectangle box = new Rectangle(0, 0, Width, Height);
            Theme.FillRoundRect(g, box, 10, Theme.PrimaryLight);
            string text = string.IsNullOrEmpty(_fallbackText) ? "程" : _fallbackText;
            if (text.Length > 1) text = text.Substring(0, 1);
            Rectangle textRect = new Rectangle(0, 0, Width, Height);
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, text, Theme.H2, textRect, Theme.Primary, flags);
        }
    }
}
