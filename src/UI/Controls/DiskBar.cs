// T3 拥有：磁盘容量条（已用/可用，空间不足时变红）
using System;
using System.Drawing;
using System.Windows.Forms;
using YijianNuodong.Core;

namespace YijianNuodong.UI.Controls
{
    public class DiskBar : Control
    {
        private long _totalBytes = -1;
        private long _freeBytes = -1;
        private string _title = string.Empty;
        private bool _insufficient;

        public DiskBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.White;
            Height = 46;
            TabStop = false;
        }

        public bool Insufficient
        {
            get { return _insufficient; }
            set { _insufficient = value; Invalidate(); }
        }

        public long FreeBytes
        {
            get { return _freeBytes; }
        }

        public long TotalBytes
        {
            get { return _totalBytes; }
        }

        public void SetData(long totalBytes, long freeBytes, string title)
        {
            _totalBytes = totalBytes;
            _freeBytes = freeBytes;
            _title = title == null ? string.Empty : title;
            Invalidate();
        }

        public void SetDrive(DriveInfoItem drive)
        {
            if (drive == null)
            {
                SetData(-1, -1, string.Empty);
                return;
            }
            string label = string.IsNullOrEmpty(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;
            SetData(drive.TotalBytes, drive.FreeBytes, drive.RootPath + " " + label);
        }

        public static string FormatLine(long total, long free, string title)
        {
            if (total <= 0)
            {
                return string.IsNullOrEmpty(title) ? string.Format("已用 未知 · 可用 未知") :
                    string.Format("{0} · 已用 未知 · 可用 未知", title);
            }
            long used = total - free;
            if (used < 0) used = 0;
            return string.Format("{0} · 已用 {1} · 可用 {2}", title,
                TextUtil.FormatBytes(used), TextUtil.FormatBytes(free));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color backdrop = Color.White;
            if (Parent != null && Parent.BackColor.A == 255) backdrop = Parent.BackColor;
            using (SolidBrush bg = new SolidBrush(backdrop))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            string left = FormatLine(_totalBytes, _freeBytes, _title);
            Rectangle leftRect = new Rectangle(0, 0, Width, 22);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, left, Theme.Small, leftRect, Theme.TextSecondary, flags);

            Rectangle track = new Rectangle(0, 30, Width, 10);
            Theme.FillRoundRect(g, track, 5, Theme.Track);

            if (_totalBytes > 0)
            {
                long used = _totalBytes - _freeBytes;
                if (used < 0) used = 0;
                int percent = (int)(used * 100 / _totalBytes);
                if (percent < 1) percent = 1;
                if (percent > 100) percent = 100;
                int w = Width * percent / 100;
                if (w < 10) w = 10;
                if (w > Width) w = Width;
                Rectangle fill = new Rectangle(0, 30, w, 10);
                Color usedColor = _insufficient ? Theme.Danger : Theme.Slate;
                Theme.FillRoundRect(g, fill, 5, usedColor);
            }
        }
    }
}
