// T3 拥有：配色、字体、绘制辅助（统一视觉风格）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace YijianNuodong.UI
{
    /// <summary>全局视觉主题：Windows 11 设置风格的浅色扁平化配色与字体。</summary>
    public static class Theme
    {
        // ---- 配色 ----
        public static readonly Color Background = Color.FromArgb(245, 247, 251);
        public static readonly Color Surface = Color.White;
        public static readonly Color Border = Color.FromArgb(226, 232, 240);
        public static readonly Color BorderStrong = Color.FromArgb(203, 213, 225);
        public static readonly Color Primary = Color.FromArgb(37, 99, 235);
        public static readonly Color PrimaryHover = Color.FromArgb(29, 78, 216);
        public static readonly Color PrimaryPressed = Color.FromArgb(30, 64, 175);
        public static readonly Color PrimaryLight = Color.FromArgb(239, 246, 255);
        public static readonly Color PrimaryLight2 = Color.FromArgb(219, 234, 254);
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
        public static readonly Color SuccessLight = Color.FromArgb(240, 253, 244);
        public static readonly Color Warning = Color.FromArgb(217, 119, 6);
        public static readonly Color WarningLight = Color.FromArgb(255, 251, 235);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);
        public static readonly Color DangerLight = Color.FromArgb(254, 242, 242);
        public static readonly Color TextPrimary = Color.FromArgb(15, 23, 42);
        public static readonly Color TextSecondary = Color.FromArgb(71, 85, 105);
        public static readonly Color TextMuted = Color.FromArgb(148, 163, 184);
        public static readonly Color HighlightRow = Color.FromArgb(239, 246, 255);
        public static readonly Color OptionBackground = Color.FromArgb(248, 250, 252);
        public static readonly Color Track = Color.FromArgb(226, 232, 240);
        public static readonly Color Slate = Color.FromArgb(100, 116, 139);

        // ---- 字体 ----
        public static readonly Font H1 = new Font("Microsoft YaHei UI", 24F, FontStyle.Bold);
        public static readonly Font H1Small = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
        public static readonly Font Display = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold);
        public static readonly Font H2 = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
        public static readonly Font DropTitle = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
        public static readonly Font H3 = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
        public static readonly Font Body = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
        public static readonly Font BodyBold = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        public static readonly Font Small = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular);
        public static readonly Font SmallBold = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 9F, FontStyle.Regular);

        /// <summary>双缓冲，减少闪烁</summary>
        public static void EnableDoubleBuffer(Control control)
        {
            if (control == null) return;
            try
            {
                PropertyInfo pi = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(control, true, null);

                // SetStyle 是 protected 方法，这里用反射调用，避免为每个控件写派生类
                MethodInfo mi = typeof(Control).GetMethod("SetStyle", BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi != null)
                {
                    mi.Invoke(control, new object[]
                    {
                        ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint,
                        true
                    });
                }
            }
            catch { }
        }

        /// <summary>圆角矩形路径</summary>
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            int d = radius * 2;
            if (d > bounds.Width) d = bounds.Width;
            if (d > bounds.Height) d = bounds.Height;
            Rectangle arc = new Rectangle(bounds.X, bounds.Y, d, d);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - d;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - d;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>绘制白底圆角卡片</summary>
        public static void PaintCard(Graphics g, Rectangle bounds, int radius, Color fill, Color border, float borderWidth)
        {
            if (g == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath path = RoundedRect(r, radius))
            {
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }
                if (border.A > 0)
                {
                    using (Pen pen = new Pen(border, borderWidth))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }
        }

        /// <summary>填充圆角矩形（无边框）</summary>
        public static void FillRoundRect(Graphics g, Rectangle bounds, int radius, Color fill)
        {
            if (g == null || fill.A == 0) return;
            Rectangle r = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (r.Width <= 0 || r.Height <= 0) return;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedRect(r, radius))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }
            g.SmoothingMode = old;
        }

        /// <summary>绘制椭圆</summary>
        public static void FillCircle(Graphics g, Rectangle bounds, Color fill)
        {
            if (g == null || fill.A == 0) return;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillEllipse(brush, bounds);
            }
            g.SmoothingMode = old;
        }

        /// <summary>按宽度省略绘制单行文字</summary>
        public static void DrawTextEllipsis(Graphics g, string text, Font font, Color color, Rectangle bounds, StringAlignment align)
        {
            if (g == null || string.IsNullOrEmpty(text)) return;
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Alignment = align;
                sf.LineAlignment = StringAlignment.Center;
                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.DrawString(text, font, brush, bounds, sf);
                }
            }
        }

        /// <summary>GDI 文字绘制（清晰、单行居中）</summary>
        public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle bounds,
            TextFormatFlags extra)
        {
            if (g == null || string.IsNullOrEmpty(text)) return;
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                                    TextFormatFlags.EndEllipsis | extra;
            TextRenderer.DrawText(g, text, font, bounds, color, flags);
        }

        /// <summary>GDI 文字绘制（左对齐、垂直居中）</summary>
        public static void DrawTextLeft(Graphics g, string text, Font font, Color color, Rectangle bounds)
        {
            DrawText(g, text, font, color, bounds, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        /// <summary>GDI 文字绘制（水平居中、垂直居中）</summary>
        public static void DrawTextCenter(Graphics g, string text, Font font, Color color, Rectangle bounds)
        {
            DrawText(g, text, font, color, bounds, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>简单明暗混合，用于生成悬停色</summary>
        public static Color Mix(Color a, Color b, double ratio)
        {
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            int r = (int)(a.R + (b.R - a.R) * ratio);
            int g = (int)(a.G + (b.G - a.G) * ratio);
            int b2 = (int)(a.B + (b.B - a.B) * ratio);
            return Color.FromArgb(a.A, r, g, b2);
        }

        /// <summary>半透明颜色</summary>
        public static Color Alpha(Color c, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        /// <summary>绘制一个小叉（失败图标）</summary>
        public static void DrawCross(Graphics g, Rectangle bounds, Color color, float width)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(color, width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int pad = bounds.Width / 4;
                g.DrawLine(pen, bounds.Left + pad, bounds.Top + pad, bounds.Right - pad, bounds.Bottom - pad);
                g.DrawLine(pen, bounds.Right - pad, bounds.Top + pad, bounds.Left + pad, bounds.Bottom - pad);
            }
            g.SmoothingMode = old;
        }

        /// <summary>绘制对勾</summary>
        public static void DrawCheck(Graphics g, Rectangle bounds, Color color, float width)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(color, width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                int w = bounds.Width;
                int h = bounds.Height;
                Point p1 = new Point(bounds.Left + w * 26 / 100, bounds.Top + h * 52 / 100);
                Point p2 = new Point(bounds.Left + w * 43 / 100, bounds.Top + h * 70 / 100);
                Point p3 = new Point(bounds.Left + w * 74 / 100, bounds.Top + h * 32 / 100);
                g.DrawLines(pen, new Point[] { p1, p2, p3 });
            }
            g.SmoothingMode = old;
        }

        /// <summary>绘制向下箭头（拖放提示用）</summary>
        public static void DrawArrowUpRight(Graphics g, Rectangle bounds, Color color, float width)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(color, width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                int w = bounds.Width;
                int h = bounds.Height;
                g.DrawLine(pen, bounds.Left + w * 25 / 100, bounds.Top + h * 75 / 100,
                                bounds.Left + w * 75 / 100, bounds.Top + h * 25 / 100);
                g.DrawLine(pen, bounds.Left + w * 46 / 100, bounds.Top + h * 25 / 100,
                                bounds.Left + w * 75 / 100, bounds.Top + h * 25 / 100);
                g.DrawLine(pen, bounds.Left + w * 75 / 100, bounds.Top + h * 25 / 100,
                                bounds.Left + w * 75 / 100, bounds.Top + h * 54 / 100);
            }
            g.SmoothingMode = old;
        }
    }
}
