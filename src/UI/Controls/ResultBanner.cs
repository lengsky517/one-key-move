// T3 拥有：结果页顶部大图标横幅（成功对勾 / 失败红叉 / 已撤销）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    public enum ResultBannerKind
    {
        Success = 0,
        Failure = 1,
        Undo = 2
    }

    public class ResultBanner : Panel
    {
        private ResultBannerKind _kind = ResultBannerKind.Success;
        private string _title = "搬移完成！";
        private string _subTitle = string.Empty;

        public ResultBanner()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Surface;
            Height = 88;
        }

        public void SetResult(ResultBannerKind kind, string title, string subTitle)
        {
            _kind = kind;
            _title = title == null ? string.Empty : title;
            _subTitle = subTitle == null ? string.Empty : subTitle;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color backdrop = Theme.Surface;
            if (Parent != null && Parent.BackColor.A == 255) backdrop = Parent.BackColor;
            using (SolidBrush bg = new SolidBrush(backdrop))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            Color accent = _kind == ResultBannerKind.Success ? Theme.Success :
                (_kind == ResultBannerKind.Failure ? Theme.Danger : Theme.Primary);

            int circleSize = 64;
            Rectangle circle = new Rectangle(0, (Height - circleSize) / 2, circleSize, circleSize);
            // 灰色/淡色外圈 + 实心彩色圆 + 白色对勾（失败为白色叉）
            using (Pen halo = new Pen(Theme.Alpha(accent, 45), 4f))
            {
                Rectangle haloRect = new Rectangle(circle.X - 3, circle.Y - 3, circle.Width + 6, circle.Height + 6);
                g.DrawEllipse(halo, haloRect);
            }
            Theme.FillCircle(g, circle, accent);
            Rectangle inner = new Rectangle(circle.X + 16, circle.Y + 16, circle.Width - 32, circle.Height - 32);
            if (_kind == ResultBannerKind.Failure)
            {
                Theme.DrawCross(g, inner, Color.White, 3.2f);
            }
            else
            {
                Theme.DrawCheck(g, inner, Color.White, 3.2f);
            }

            int left = circleSize + 24;
            Rectangle titleRect = new Rectangle(left, 14, Width - left, 38);
            TextFormatFlags tflags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                     TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, _title, Theme.Display, titleRect,
                _kind == ResultBannerKind.Failure ? Theme.Danger : Theme.TextPrimary, tflags);

            if (!string.IsNullOrEmpty(_subTitle))
            {
                Rectangle subRect = new Rectangle(left + 2, 50, Width - left, 22);
                TextFormatFlags sflags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                         TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, _subTitle, Theme.Body, subRect, Theme.TextSecondary, sflags);
            }
        }
    }
}
