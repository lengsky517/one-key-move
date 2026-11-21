// 第 1 步底部选中摘要：左侧图标 + 名称 + 安装位置，右侧软件说明（白话介绍 / 能不能搬 / 能不能卸 / 是不是系统软件）
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
        private SoftwareInfo _info;

        public SelectionSummary()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Surface;
            Height = 88;
        }

        public void Clear()
        {
            _name = string.Empty;
            _path = string.Empty;
            _info = null;
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
            SoftwareInfo info = null;
            try
            {
                info = SoftwareCatalog.Lookup(program);
            }
            catch
            {
                info = null;
            }
            _info = info;
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

            // 左侧：图标 + 名称 + 位置
            int leftWidth = Width * 38 / 100;
            if (leftWidth < 230) leftWidth = Math.Min(230, Width - 120);
            if (leftWidth > 340) leftWidth = 340;
            if (leftWidth < 120) leftWidth = 120;

            Rectangle iconRect = new Rectangle(0, 8, 32, 32);
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
            int textWidth = leftWidth - textLeft;
            if (textWidth < 40) textWidth = 40;

            Rectangle nameRect = new Rectangle(textLeft, 6, textWidth, 22);
            TextFormatFlags nflags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                     TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, _name, Theme.BodyBold, nameRect, Theme.TextPrimary, nflags);

            Rectangle pathRect = new Rectangle(textLeft, 28, textWidth, 20);
            TextFormatFlags pflags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                     TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, PathUtil.Shorten(_path, 72), Theme.Small, pathRect, Theme.TextMuted, pflags);

            // 分隔竖线
            int dividerX = leftWidth + 4;
            using (Pen pen = new Pen(Theme.Border))
            {
                g.DrawLine(pen, dividerX, 10, dividerX, Height - 12);
            }

            // 右侧：徽标 + 白话说明 + 提示
            int rightLeft = dividerX + 14;
            int rightWidth = Width - rightLeft - 4;
            if (rightWidth < 80) return;

            int x = rightLeft;
            SoftwareInfo info = _info;
            string kindText = info == null ? "未收录" : info.KindText;
            Color kindFg = Theme.Primary;
            Color kindBg = Theme.PrimaryLight;
            if (info != null && info.IsSystemComponent)
            {
                kindFg = Theme.Danger;
                kindBg = Theme.DangerLight;
            }
            x = DrawBadge(g, x, 6, kindText, kindFg, kindBg, rightLeft + rightWidth);

            if (info != null)
            {
                x = DrawBadge(g, x, 6, MoveText(info.Move), MoveColor(info.Move), MoveBg(info.Move), rightLeft + rightWidth);
                x = DrawBadge(g, x, 6, UninstallText(info.Uninstall), UninstallColor(info.Uninstall), UninstallBg(info.Uninstall), rightLeft + rightWidth);
            }

            string summary = info == null ? string.Empty : (info.Summary ?? string.Empty);
            if (summary.Length == 0)
                summary = "这个软件没有收录说明，但可以从安装位置判断它是什么。";
            Rectangle summaryRect = new Rectangle(rightLeft, 30, rightWidth, 20);
            TextRenderer.DrawText(g, summary, Theme.Small, summaryRect, Theme.TextSecondary, nflags);

            string tip = info == null ? string.Empty : (info.Tip ?? string.Empty);
            if (tip.Length > 0)
            {
                Rectangle tipRect = new Rectangle(rightLeft, 52, rightWidth, 20);
                TextRenderer.DrawText(g, "提示：" + tip, Theme.Small, tipRect, Theme.TextMuted, nflags);
            }
        }

        /// <summary>画一个小圆角徽标，返回下一个可用的 x 坐标。</summary>
        private static int DrawBadge(Graphics g, int x, int y, string text, Color fore, Color back, int limitRight)
        {
            if (string.IsNullOrEmpty(text)) return x;
            Size size = TextRenderer.MeasureText(g, text, Theme.SmallBold, new Size(int.MaxValue, 20),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int width = size.Width + 14;
            if (x + width > limitRight) return x;
            Rectangle rect = new Rectangle(x, y, width, 20);
            Theme.FillRoundRect(g, rect, 10, back);
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, text, Theme.SmallBold, rect, fore, flags);
            return x + width + 6;
        }

        private static string MoveText(MoveAdvice advice)
        {
            switch (advice)
            {
                case MoveAdvice.Safe: return "可以搬";
                case MoveAdvice.Caution: return "搬要小心";
                case MoveAdvice.KeepInPlace: return "别搬";
                default: return "搬移未知";
            }
        }

        private static Color MoveColor(MoveAdvice advice)
        {
            switch (advice)
            {
                case MoveAdvice.Safe: return Theme.Success;
                case MoveAdvice.Caution: return Theme.Warning;
                case MoveAdvice.KeepInPlace: return Theme.Danger;
                default: return Theme.Slate;
            }
        }

        private static Color MoveBg(MoveAdvice advice)
        {
            switch (advice)
            {
                case MoveAdvice.Safe: return Theme.SuccessLight;
                case MoveAdvice.Caution: return Theme.WarningLight;
                case MoveAdvice.KeepInPlace: return Theme.DangerLight;
                default: return Theme.OptionBackground;
            }
        }

        private static string UninstallText(UninstallAdvice advice)
        {
            switch (advice)
            {
                case UninstallAdvice.Safe: return "可以卸";
                case UninstallAdvice.Caution: return "卸载小心";
                case UninstallAdvice.DoNotRemove: return "别卸";
                default: return "卸载未知";
            }
        }

        private static Color UninstallColor(UninstallAdvice advice)
        {
            switch (advice)
            {
                case UninstallAdvice.Safe: return Theme.Success;
                case UninstallAdvice.Caution: return Theme.Warning;
                case UninstallAdvice.DoNotRemove: return Theme.Danger;
                default: return Theme.Slate;
            }
        }

        private static Color UninstallBg(UninstallAdvice advice)
        {
            switch (advice)
            {
                case UninstallAdvice.Safe: return Theme.SuccessLight;
                case UninstallAdvice.Caution: return Theme.WarningLight;
                case UninstallAdvice.DoNotRemove: return Theme.DangerLight;
                default: return Theme.OptionBackground;
            }
        }
    }
}
