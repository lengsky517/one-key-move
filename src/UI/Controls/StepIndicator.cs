// T3 拥有：顶部步骤指示器（① 选择软件 -> ② 选择位置 -> ③ 开始搬移）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    public class StepIndicator : Control
    {
        private int _activeStep = 0;
        private string[] _steps = new string[] { "选择软件", "选择位置", "开始搬移" };

        public StepIndicator()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Height = 64;
            BackColor = Color.Transparent;
            ForeColor = Theme.TextSecondary;
            Font = Theme.Body;
            TabStop = false;
        }

        public int ActiveStep
        {
            get { return _activeStep; }
            set { _activeStep = value; Invalidate(); }
        }

        public string[] Steps
        {
            get { return _steps; }
            set { if (value != null) _steps = value; Invalidate(); }
        }

        // 计算每个步骤圆心的水平位置
        private int[] ComputeCenters(out int circleSize)
        {
            circleSize = 30;
            int n = _steps == null ? 0 : _steps.Length;
            int[] centers = new int[n];
            if (n == 0) return centers;
            int lineLength = 96;
            int gap = 10;
            int[] labelWidths = new int[n];
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                labelWidths[i] = TextRenderer.MeasureText(_steps[i], Theme.BodyBold).Width;
                total += circleSize + gap + labelWidths[i];
                if (i < n - 1) total += lineLength;
            }
            int x = (Width - total) / 2;
            if (x < 16) x = 16;
            for (int i = 0; i < n; i++)
            {
                centers[i] = x + circleSize / 2;
                x += circleSize + gap + labelWidths[i] + (i < n - 1 ? lineLength : 0);
            }
            return centers;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 背景透明：由父容器（灰色）自行绘制；这里补一层父色，避免透明控件伪影
            Color backdrop = Theme.Background;
            if (Parent != null && Parent.BackColor.A == 255) backdrop = Parent.BackColor;
            using (SolidBrush bg = new SolidBrush(backdrop))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            int circleSize = 30;
            int[] centers = ComputeCenters(out circleSize);
            int cy = Height / 2 - 1;
            int n = centers.Length;

            // 连接线
            for (int i = 0; i < n - 1; i++)
            {
                int labelWidth = TextRenderer.MeasureText(_steps[i], Theme.BodyBold).Width;
                int x1 = centers[i] + circleSize / 2 + 10 + labelWidth + 10;
                int x2 = centers[i + 1] - circleSize / 2 - 10;
                if (x2 <= x1) continue;
                Color lineColor = i < _activeStep ? Theme.Success : Theme.Border;
                using (Pen pen = new Pen(lineColor, 1.5f))
                {
                    pen.DashStyle = i < _activeStep ? DashStyle.Solid : DashStyle.Dash;
                    g.DrawLine(pen, x1, cy, x2, cy);
                }
            }

            for (int i = 0; i < n; i++)
            {
                Rectangle circle = new Rectangle(centers[i] - circleSize / 2, cy - circleSize / 2, circleSize, circleSize);
                bool done = i < _activeStep;
                bool active = i == _activeStep;

                Color fill = done ? Theme.Success : (active ? Theme.Primary : Color.FromArgb(232, 236, 243));
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillEllipse(brush, circle);
                }
                if (active)
                {
                    using (Pen pen = new Pen(Theme.Alpha(Theme.Primary, 60), 5f))
                    {
                        Rectangle halo = new Rectangle(circle.X - 4, circle.Y - 4, circle.Width + 8, circle.Height + 8);
                        g.DrawEllipse(pen, halo);
                    }
                }

                if (done)
                {
                    Rectangle checkRect = new Rectangle(circle.X + 7, circle.Y + 7, circle.Width - 14, circle.Height - 14);
                    Theme.DrawCheck(g, checkRect, Color.White, 2.2f);
                }
                else
                {
                    string num = (i + 1).ToString();
                    Rectangle textRect = new Rectangle(circle.X, circle.Y, circle.Width, circle.Height);
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        using (SolidBrush brush = new SolidBrush(active ? Color.White : Theme.TextSecondary))
                        {
                            g.DrawString(num, Theme.SmallBold, brush, textRect, sf);
                        }
                    }
                }

                int labelWidth2 = TextRenderer.MeasureText(_steps[i], Theme.BodyBold).Width;
                Rectangle labelRect = new Rectangle(circle.Right + 10, circle.Top - 2, labelWidth2 + 6, circle.Height + 4);
                Color labelColor = done ? Theme.Success : (active ? Theme.TextPrimary : Theme.TextMuted);
                TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, _steps[i], active ? Theme.BodyBold : Theme.Body, labelRect, labelColor, flags);
            }
        }
    }
}
