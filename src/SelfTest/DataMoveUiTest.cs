using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using YijianNuodong.UI;
namespace YijianNuodong.SelfTest
{
    internal static class DataMoveUiTest
    {
        internal static int Run(string output)
        {
            StringBuilder log = new StringBuilder(); int failures = 0;
            Action<bool, string> check = delegate(bool ok, string name) { log.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name); if (!ok) failures++; };
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (DataMoveDialog dialog = new DataMoveDialog())
                {
                    dialog.PreviewFillDemoData(); dialog.Show(); Application.DoEvents();
                    Type t = typeof(DataMoveDialog); BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    TabControl tabs = (TabControl)t.GetField("tabs", flags).GetValue(dialog);
                    ListView data = (ListView)t.GetField("data", flags).GetValue(dialog);
                    check(dialog.Visible && dialog.IsHandleCreated, "窗口及原生消息循环");
                    check(tabs.TabCount == 4 && data.Items.Count == 2, "四个功能页及演示条目");
                    check(t.GetField("scan", flags).GetValue(dialog) == null, "演示模式无可执行扫描");
                    foreach (string name in new string[] { "scanButton", "moveButton", "archiveButton", "pipButton", "pipUndoButton", "undoButton" })
                        check(!((Button)t.GetField(name, flags).GetValue(dialog)).Enabled, "演示禁写：" + name);
                    tabs.SelectedIndex = 1; Application.DoEvents();
                    data.Items[1].Checked = true; Application.DoEvents(); check(!data.Items[1].Checked, "无效演示项无法勾选执行");
                    foreach (int width in new int[] { 1000, 1160 })
                    {
                        dialog.Size = new Size(width, 810); Application.DoEvents();
                        for (int tab = 0; tab < tabs.TabCount; tab++)
                        {
                            tabs.SelectedIndex = tab; Application.DoEvents();
                            check(tabs.SelectedTab.ClientSize.Height > 150 && tabs.SelectedTab.ClientSize.Width > 800, "可见布局 " + width + " tab" + tab);
                            if (width == 1160)
                                using (Bitmap image = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(image, new Rectangle(Point.Empty, dialog.Size)); image.Save(output + ".tab" + tab + ".png"); }
                        }
                    }
                    dialog.Close();
                }
                Exception threadError = null;
                System.Threading.ThreadExceptionEventHandler onError = delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { threadError = e.Exception; };
                Application.ThreadException += onError;
                try
                {
                    using (DataMoveDialog live = new DataMoveDialog())
                    {
                        live.Show();
                        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                        while (clock.ElapsedMilliseconds < 700) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                        FieldInfo workerField = typeof(DataMoveDialog).GetField("worker", BindingFlags.Instance | BindingFlags.NonPublic);
                        System.ComponentModel.BackgroundWorker scanning = (System.ComponentModel.BackgroundWorker)workerField.GetValue(live);
                        live.Close();
                        clock.Restart();
                        while (scanning.IsBusy && clock.ElapsedMilliseconds < 15000) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                        Application.DoEvents();
                        check(!scanning.IsBusy, "真实只读扫描可安全取消并完成关闭");
                        check(threadError == null, "真实扫描及列表异步通知无窗口异常");
                        live.Close();
                    }
                }
                finally { Application.ThreadException -= onError; }
            }
            catch (Exception ex) { check(false, ex.ToString()); }
            log.AppendLine("UI failures: " + failures); File.WriteAllText(output, log.ToString(), new UTF8Encoding(true)); return failures == 0 ? 0 : 1;
        }
    }
}
