// UI 卸载对话框拥有：结果横幅（成功 / 部分完成 / 失败，三种配色）
// 与 ResultBanner 同款视觉，但多一个「部分完成」的橙色状态；
// 不复用 ResultBanner 是为了不改动已有三步向导的控件。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    /// <summary>结果横幅状态</summary>
    public enum BannerState
    {
        Success = 0,
        Partial = 1,
        Failed = 2
    }

    public class StateBanner : Panel
    {
        private BannerState _state = BannerState.Success;
        private string _title = string.Empty;
        private string _subTitle = string.Empty;

        public StateBanner()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Surface;
            Height = 88;
        }

        public BannerState State
        {
            get { return _state; }
        }

        public void SetState(BannerState state, string title, string subTitle)
        {
            _state = state;
            _title = title == null ? string.Empty : title;
            _subTitle = subTitle == null ? string.Empty : subTitle;
            Invalidate();
        }

        private Color Accent()
        {
            if (_state == BannerState.Failed) return Theme.Danger;
            if (_state == BannerState.Partial) return Theme.Warning;
            return Theme.Success;
        }

        private Color TitleColor()
        {
            if (_state == BannerState.Failed) return Theme.Danger;
            if (_state == BannerState.Partial) return Color.FromArgb(146, 64, 14);
            return Theme.TextPrimary;
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

            Color accent = Accent();
            int circleSize = 64;
            Rectangle circle = new Rectangle(0, (Height - circleSize) / 2, circleSize, circleSize);

            using (Pen halo = new Pen(Theme.Alpha(accent, 45), 4f))
            {
                Rectangle haloRect = new Rectangle(circle.X - 3, circle.Y - 3, circle.Width + 6, circle.Height + 6);
                g.DrawEllipse(halo, haloRect);
            }
            Theme.FillCircle(g, circle, accent);

            if (_state == BannerState.Success)
            {
                Rectangle inner = new Rectangle(circle.X + 16, circle.Y + 16, circle.Width - 32, circle.Height - 32);
                Theme.DrawCheck(g, inner, Color.White, 3.2f);
            }
            else if (_state == BannerState.Failed)
            {
                Rectangle inner = new Rectangle(circle.X + 16, circle.Y + 16, circle.Width - 32, circle.Height - 32);
                Theme.DrawCross(g, inner, Color.White, 3.2f);
            }
            else
            {
                // 部分完成：感叹号
                StringFormat format = new StringFormat();
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("!", Theme.Display, brush, circle, format);
                }
                format.Dispose();
            }

            int left = circleSize + 24;
            Rectangle titleRect = new Rectangle(left, 14, Width - left, 38);
            TextFormatFlags tflags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                     TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, _title, Theme.Display, titleRect, TitleColor(), tflags);

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
