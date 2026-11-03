// T3 拥有：自绘圆角进度条（支持确定进度与不确定的流动条纹）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    public class ProgressBarEx : Control
    {
        private int _value;
        private bool _indeterminate;
        private Color _barColor = Theme.Primary;
        private Color _trackColor = Theme.Track;
        private int _offset;
        private Timer _timer;

        public ProgressBarEx()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.White;
            Height = 10;
            TabStop = false;
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v > 100) v = 100;
                if (_value == v) return;
                _value = v;
                Invalidate();
            }
        }

        public bool IsIndeterminate
        {
            get { return _indeterminate; }
            set
            {
                if (_indeterminate == value) return;
                _indeterminate = value;
                if (value) StartTimer();
                else StopTimer();
                Invalidate();
            }
        }

        public Color BarColor
        {
            get { return _barColor; }
            set { _barColor = value; Invalidate(); }
        }

        public Color TrackColor
        {
            get { return _trackColor; }
            set { _trackColor = value; Invalidate(); }
        }

        private void StartTimer()
        {
            if (_timer == null)
            {
                _timer = new Timer();
                _timer.Interval = 40;
                _timer.Tick += delegate
                {
                    _offset += 6;
                    if (_offset > 60) _offset = 0;
                    Invalidate();
                };
            }
            _timer.Start();
        }

        private void StopTimer()
        {
            if (_timer != null) _timer.Stop();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) StopTimer();
            else if (_indeterminate) StartTimer();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
            base.Dispose(disposing);
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

            int h = Height;
            if (h < 6) h = 6;
            Rectangle track = new Rectangle(0, (Height - h) / 2, Width, h);
            Theme.FillRoundRect(g, track, h / 2, _trackColor);

            if (_indeterminate)
            {
                int chunk = Math.Max(60, Width / 4);
                for (int i = -chunk; i < Width + chunk; i += chunk)
                {
                    int x = i + _offset * chunk / 60;
                    Rectangle piece = new Rectangle(x + 4, track.Y, chunk - 12, h);
                    if (piece.Right < 0 || piece.Left > Width) continue;
                    Rectangle clipped = Rectangle.Intersect(piece, track);
                    if (clipped.Width <= 0) continue;
                    using (GraphicsPath clipPath = Theme.RoundedRect(clipped, h / 2))
                    using (SolidBrush brush = new SolidBrush(Theme.Alpha(_barColor, 120)))
                    {
                        g.FillPath(brush, clipPath);
                    }
                }
            }
            else if (_value > 0)
            {
                int w = Width * _value / 100;
                if (w < h) w = h;
                if (w > Width) w = Width;
                Rectangle fill = new Rectangle(0, track.Y, w, h);
                using (GraphicsPath path = Theme.RoundedRect(fill, h / 2))
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, h)),
                    Theme.Mix(_barColor, Color.White, 0.15), _barColor, LinearGradientMode.Horizontal))
                {
                    g.FillPath(brush, path);
                }
            }
        }
    }
}
