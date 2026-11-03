// T3 拥有：提示面板（警告 / 错误 / 占用提示 / 重启提示，可带操作按钮）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YijianNuodong.UI.Controls
{
    public enum NoticeKind
    {
        Info = 0,
        Warning = 1,
        Danger = 2,
        Success = 3
    }

    public class NoticePanel : Panel
    {
        private NoticeKind _kind = NoticeKind.Warning;
        private string _title = string.Empty;
        private string _body = string.Empty;
        private readonly List<FlatButton> _buttons = new List<FlatButton>();
        private int _layoutWidth = 900;
        private Color _backdropColor = Theme.Surface;

        public NoticePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Visible = false;
        }

        public NoticeKind Kind
        {
            get { return _kind; }
        }

        public string BodyText
        {
            get { return _body; }
            set { _body = value == null ? string.Empty : value; ApplyLayout(); }
        }

        public Color BackdropColor
        {
            get { return _backdropColor; }
            set { _backdropColor = value; ApplyLayout(); }
        }

        private Color KindBack()
        {
            switch (_kind)
            {
                case NoticeKind.Danger: return Theme.DangerLight;
                case NoticeKind.Warning: return Theme.WarningLight;
                case NoticeKind.Success: return Theme.SuccessLight;
                default: return Theme.PrimaryLight;
            }
        }

        private Color KindBorder()
        {
            switch (_kind)
            {
                case NoticeKind.Danger: return Color.FromArgb(252, 165, 165);
                case NoticeKind.Warning: return Color.FromArgb(253, 230, 138);
                case NoticeKind.Success: return Color.FromArgb(187, 247, 208);
                default: return Color.FromArgb(191, 219, 254);
            }
        }

        private Color KindAccent()
        {
            switch (_kind)
            {
                case NoticeKind.Danger: return Theme.Danger;
                case NoticeKind.Warning: return Color.FromArgb(180, 83, 9);
                case NoticeKind.Success: return Theme.Success;
                default: return Theme.Primary;
            }
        }

        private Color KindTitle()
        {
            switch (_kind)
            {
                case NoticeKind.Danger: return Color.FromArgb(153, 27, 27);
                case NoticeKind.Warning: return Color.FromArgb(146, 64, 14);
                case NoticeKind.Success: return Color.FromArgb(22, 101, 52);
                default: return Color.FromArgb(30, 64, 175);
            }
        }

        public void Configure(NoticeKind kind, string title, string body)
        {
            _kind = kind;
            _title = title == null ? string.Empty : title;
            _body = body == null ? string.Empty : body;
            ApplyLayout();
        }

        public void ClearButtons()
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                Controls.Remove(_buttons[i]);
                _buttons[i].Dispose();
            }
            _buttons.Clear();
        }

        public FlatButton AddButton(string text, FlatButtonStyle style)
        {
            FlatButton button = new FlatButton();
            button.Text = text;
            button.Style = style;
            button.Height = 32;
            button.Width = 148;
            button.BackdropColor = KindBack();
            button.Font = Theme.SmallBold;
            button.Visible = true;
            Controls.Add(button);
            _buttons.Add(button);
            ApplyLayout();
            return button;
        }

        public void SetLayoutWidth(int width)
        {
            if (width < 260) width = 260;
            _layoutWidth = width;
            ApplyLayout();
        }

        private int MeasureBodyHeight(int width)
        {
            if (string.IsNullOrEmpty(_body)) return 0;
            int w = width - 52 - 16;
            if (w < 80) w = 80;
            Size size = TextRenderer.MeasureText(_body, Theme.Body, new Size(w, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            return size.Height + 4;
        }

        private void ApplyLayout()
        {
            int width = _layoutWidth;
            int bodyTop = string.IsNullOrEmpty(_title) ? 14 : 40;
            int bodyHeight = MeasureBodyHeight(width);
            int buttonsHeight = _buttons.Count > 0 ? 44 : 0;
            int height = bodyTop + bodyHeight + buttonsHeight + 12;
            if (height < 56) height = 56;
            if (height > 400) height = 400;
            if (Height != height) Height = height;

            int right = width - 16;
            int y = Height - 12 - 32;
            for (int i = _buttons.Count - 1; i >= 0; i--)
            {
                FlatButton button = _buttons[i];
                right -= button.Width;
                button.Location = new Point(right, y);
                right -= 8;
            }
            Invalidate();
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (_layoutWidth <= 0 && Width > 260) _layoutWidth = Width;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(_backdropColor))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            Theme.PaintCard(g, ClientRectangle, 10, KindBack(), KindBorder(), 1f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int iconSize = 22;
            Rectangle icon = new Rectangle(16, 14, iconSize, iconSize);
            Color accent = KindAccent();
            using (SolidBrush brush = new SolidBrush(accent))
            {
                g.FillEllipse(brush, icon);
            }
            string glyph = _kind == NoticeKind.Success ? "✓" : (_kind == NoticeKind.Info ? "i" : "!");
            if (glyph == "✓")
            {
                Theme.DrawCheck(g, new Rectangle(icon.X + 6, icon.Y + 5, icon.Width - 10, icon.Height - 10), Color.White, 1.8f);
            }
            else
            {
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    using (SolidBrush brush = new SolidBrush(Color.White))
                    {
                        g.DrawString(glyph, Theme.SmallBold, brush, icon, sf);
                    }
                }
            }

            Rectangle titleRect = new Rectangle(52, 12, Width - 68, 24);
            TextFormatFlags tflags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                     TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            if (!string.IsNullOrEmpty(_title))
            {
                TextRenderer.DrawText(g, _title, Theme.BodyBold, titleRect, KindTitle(), tflags);
            }

            if (!string.IsNullOrEmpty(_body))
            {
                int bodyTop = string.IsNullOrEmpty(_title) ? 18 : 38;
                Rectangle bodyRect = new Rectangle(52, bodyTop, Width - 68, Height - bodyTop - 8);
                TextFormatFlags bflags = TextFormatFlags.Left | TextFormatFlags.Top |
                                         TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, _body, Theme.Body, bodyRect, KindTitle(), bflags);
            }
        }
    }
}
