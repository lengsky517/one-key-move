// T3 拥有：第 1 步底部选中摘要（图标 + 名称 + 路径）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using YijianNuodong.Core;

namespace YijianNuodong.UI.Controls
{
    public class SelectionSummary : Panel
    {
        private string _name = string.Empty;
        private string _path = string.Empty;
        private Image _image;

        public SelectionSummary()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Surface;
            Height = 56;
        }

        public void Clear()
        {
            _name = string.Empty;
            _path = string.Empty;
            DisposeImage();
            Invalidate();
        }

        public void SetProgram(InstalledProgram program, Icon icon)
        {
            if (program == null)
            {
                Clear();
                return;
            }
            _name = program.DisplayName ?? string.Empty;
            _path = program.InstallPath ?? string.Empty;
            DisposeImage();
            try
            {
                if (icon != null) _image = icon.ToBitmap();
            }
            catch
            {
                _image = null;
            }
            Invalidate();
        }

        private void DisposeImage()
        {
            if (_image != null)
            {
                try { _image.Dispose(); } catch { }
                _image = null;
            }
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

            if (string.IsNullOrEmpty(_name))
            {
                Rectangle hintRect = new Rectangle(0, 0, Width, Height);
                TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, "尚未选择软件 · 请从上方列表选择，或直接拖入桌面快捷方式",
                    Theme.Small, hintRect, Theme.TextMuted, flags);
                return;
            }

            Rectangle iconRect = new Rectangle(0, (Height - 32) / 2, 32, 32);
            if (_image != null)
            {
                try
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_image, iconRect);
                }
                catch { }
            }
            else
            {
                Theme.FillRoundRect(g, iconRect, 8, Theme.PrimaryLight);
                TextFormatFlags f = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                string first = string.IsNullOrEmpty(_name) ? "程" : _name.Substring(0, 1);
                TextRenderer.DrawText(g, first, Theme.BodyBold, iconRect, Theme.Primary, f);
            }

            int textLeft = 44;
            Rectangle nameRect = new Rectangle(textLeft, 4, Width - textLeft, 24);
            TextFormatFlags nflags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                     TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, _name, Theme.BodyBold, nameRect, Theme.TextPrimary, nflags);

            Rectangle pathRect = new Rectangle(textLeft, 27, Width - textLeft, 20);
            TextFormatFlags pflags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                     TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, PathUtil.Shorten(_path, 88), Theme.Small, pathRect, Theme.TextMuted, pflags);
        }
    }
}
