// T3 拥有：搬移阶段列表（两列布局，已完成为绿色对勾、进行中为蓝色、失败为红色叉）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    public class StageList : Control
    {
        public const int StatePending = 0;
        public const int StateActive = 1;
        public const int StateDone = 2;
        public const int StateFailed = 3;

        private string[] _titles = new string[0];
        private int[] _states = new int[0];
        private int _columns = 2;

        public StageList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.White;
            Height = 150;
            TabStop = false;
        }

        public void SetStages(string[] titles)
        {
            if (titles == null) titles = new string[0];
            _titles = titles;
            _states = new int[titles.Length];
            Invalidate();
        }

        public void ResetStates()
        {
            _states = new int[_titles.Length];
            Invalidate();
        }

        public void SetStageState(int index, int state)
        {
            if (_states == null || index < 0 || index >= _states.Length) return;
            if (_states[index] == state) return;
            _states[index] = state;
            Invalidate();
        }

        public void MarkAllDone()
        {
            for (int i = 0; i < _states.Length; i++) _states[i] = StateDone;
            Invalidate();
        }

        /// <summary>把 index 之前的阶段标记为完成，index 本身标记为进行中</summary>
        public void SetCurrent(int index)
        {
            for (int i = 0; i < _states.Length; i++)
            {
                if (i < index) _states[i] = StateDone;
                else if (i == index) _states[i] = StateActive;
                else _states[i] = StatePending;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color backdrop = Color.White;
            if (Parent != null && Parent.BackColor.A == 255) backdrop = Parent.BackColor;
            using (SolidBrush bg = new SolidBrush(backdrop))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            int n = _titles.Length;
            if (n == 0) return;
            int rows = (n + _columns - 1) / _columns;
            int rowHeight = rows > 0 ? Height / rows : Height;
            if (rowHeight > 34) rowHeight = 34;
            int colWidth = Width / _columns;

            for (int i = 0; i < n; i++)
            {
                int col = i % _columns;
                int row = i / _columns;
                int x = col * colWidth;
                int y = row * rowHeight;
                DrawStage(g, i, new Rectangle(x, y, colWidth, rowHeight));
            }
        }

        private void DrawStage(Graphics g, int index, Rectangle cell)
        {
            int state = index < _states.Length ? _states[index] : StatePending;
            int circleSize = 20;
            int cy = cell.Y + cell.Height / 2;
            Rectangle circle = new Rectangle(cell.X + 2, cy - circleSize / 2, circleSize, circleSize);

            Color fill;
            switch (state)
            {
                case StateDone: fill = Theme.Success; break;
                case StateActive: fill = Theme.Primary; break;
                case StateFailed: fill = Theme.Danger; break;
                default: fill = Color.FromArgb(232, 236, 243); break;
            }
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillEllipse(brush, circle);
            }

            if (state == StateDone)
            {
                Rectangle check = new Rectangle(circle.X + 5, circle.Y + 5, circle.Width - 10, circle.Height - 10);
                Theme.DrawCheck(g, check, Color.White, 1.8f);
            }
            else if (state == StateFailed)
            {
                Rectangle cross = new Rectangle(circle.X + 5, circle.Y + 5, circle.Width - 10, circle.Height - 10);
                Theme.DrawCross(g, cross, Color.White, 1.8f);
            }
            else if (state == StateActive)
            {
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    Rectangle dot = new Rectangle(circle.X + 7, circle.Y + 7, 6, 6);
                    g.FillEllipse(brush, dot);
                }
            }

            Color textColor;
            Font font;
            if (state == StateDone) { textColor = Theme.TextSecondary; font = Theme.Body; }
            else if (state == StateActive) { textColor = Theme.TextPrimary; font = Theme.BodyBold; }
            else if (state == StateFailed) { textColor = Theme.Danger; font = Theme.BodyBold; }
            else { textColor = Theme.TextMuted; font = Theme.Body; }

            Rectangle textRect = new Rectangle(circle.Right + 10, cell.Y, cell.Width - circle.Width - 14, cell.Height);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, _titles[index], font, textRect, textColor, flags);
        }
    }
}
