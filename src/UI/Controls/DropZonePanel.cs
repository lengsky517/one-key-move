// T3 拥有：拖放区域（拖入快捷方式/文件夹/exe）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using YijianNuodong.Core;

namespace YijianNuodong.UI.Controls
{
    /// <summary>浅蓝虚线圆角拖放区：支持拖入 .lnk / 文件夹 / exe，右侧内置“选择快捷方式…”按钮。</summary>
    public class DropZonePanel : Panel
    {
        private bool _dragActive;
        private string _hint = "把桌面上的软件快捷方式拖到这里";
        private string _subHint = "支持 .lnk 快捷方式、程序文件夹或 exe 文件";
        private string _errorText = string.Empty;
        private bool _browseHover;
        private Rectangle _browseRect = Rectangle.Empty;

        /// <summary>拖入路径后触发</summary>
        public event Action<string> PathDropped;

        /// <summary>点击“选择快捷方式…”后触发</summary>
        public event Action BrowseRequested;

        public DropZonePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            AllowDrop = true;
            BackColor = Theme.Background;
            Font = Theme.Body;
            Height = 112;
        }

        public bool DragActive
        {
            get { return _dragActive; }
            set { _dragActive = value; Invalidate(); }
        }

        public string ErrorText
        {
            get { return _errorText; }
            set { _errorText = value == null ? string.Empty : value; Invalidate(); }
        }

        public void SetHint(string hint)
        {
            if (string.IsNullOrEmpty(hint)) return;
            _hint = hint;
            Invalidate();
        }

        public void SetError(string error)
        {
            ErrorText = error;
        }

        /// <summary>拖放内容是否为可接受的类型</summary>
        public static bool IsAcceptablePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (Directory.Exists(path)) return true;
                string ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext)) return false;
                ext = ext.ToLowerInvariant();
                return ext == ".lnk" || ext == ".exe";
            }
            catch
            {
                return false;
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            int bw = 132;
            int bh = 36;
            _browseRect = new Rectangle(Width - bw - 24, (Height - bh) / 2, bw, bh);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color backdrop = Theme.Background;
            if (Parent != null && Parent.BackColor.A == 255) backdrop = Parent.BackColor;
            using (SolidBrush bg = new SolidBrush(backdrop))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = _dragActive ? Theme.PrimaryLight2 : Theme.PrimaryLight;
            Color border = _dragActive ? Theme.Primary : Theme.Alpha(Theme.Primary, 150);
            Color textColor = Theme.TextPrimary;
            if (_errorText.Length > 0) border = Theme.Alpha(Theme.Danger, 170);

            using (GraphicsPath path = Theme.RoundedRect(rect, 12))
            {
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }
                // 虚线边框（拖拽时变为实线并加深）
                using (Pen pen = new Pen(border, 2f))
                {
                    if (_dragActive)
                    {
                        pen.DashStyle = DashStyle.Solid;
                    }
                    else
                    {
                        pen.DashStyle = DashStyle.Dash;
                        pen.DashPattern = new float[] { 5f, 4f };
                    }
                    g.DrawPath(pen, path);
                }
            }

            // 文本区域（避开右侧按钮）
            int textWidth = Width - 24 - 132 - 24 - 24;
            if (textWidth < 200) textWidth = Math.Max(120, Width - 48);
            Rectangle mainRect = new Rectangle(24, 22, textWidth, 32);
            TextFormatFlags mainFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, _hint, Theme.DropTitle, mainRect, textColor, mainFlags);

            Rectangle subRect = new Rectangle(24, 54, textWidth, 22);
            TextFormatFlags subFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                       TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            if (_errorText.Length > 0)
            {
                TextRenderer.DrawText(g, _subHint, Theme.Small, subRect, Theme.TextSecondary, subFlags);
                Rectangle errRect = new Rectangle(24, 78, textWidth, 22);
                TextRenderer.DrawText(g, _errorText, Theme.Small, errRect, Theme.Danger, subFlags);
            }
            else
            {
                TextRenderer.DrawText(g, _dragActive ? "松开鼠标即可读取该软件" : _subHint,
                    Theme.Small, subRect, Theme.TextSecondary, subFlags);
            }

            // “选择快捷方式…”按钮
            if (_browseRect.Width > 0)
            {
                Color btnFill = _browseHover ? Theme.PrimaryLight2 : Color.White;
                using (GraphicsPath btnPath = Theme.RoundedRect(_browseRect, 8))
                {
                    using (SolidBrush brush = new SolidBrush(btnFill))
                    {
                        g.FillPath(brush, btnPath);
                    }
                    using (Pen pen = new Pen(_browseHover ? Theme.Primary : Theme.BorderStrong, 1f))
                    {
                        g.DrawPath(pen, btnPath);
                    }
                }
                TextFormatFlags btnFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                           TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, "选择快捷方式…", Theme.SmallBold, _browseRect,
                    _browseHover ? Theme.Primary : Theme.TextPrimary, btnFlags);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = _browseRect.Contains(e.Location);
            if (over != _browseHover)
            {
                _browseHover = over;
                Cursor = over ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_browseHover)
            {
                _browseHover = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && _browseRect.Contains(e.Location))
            {
                Action handler = BrowseRequested;
                if (handler != null) handler();
            }
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                DragActive = true;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            e.Effect = (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            DragActive = false;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            DragActive = false;
            string chosen = null;
            try
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        if (IsAcceptablePath(files[i]))
                        {
                            chosen = files[i];
                            break;
                        }
                    }
                    if (chosen == null && files.Length > 0) chosen = files[0];
                }
            }
            catch (Exception ex)
            {
                Log.Warn("处理拖放失败: " + ex.Message);
            }
            if (string.IsNullOrEmpty(chosen)) return;
            Action<string> handler = PathDropped;
            if (handler != null) handler(chosen);
        }
    }
}
