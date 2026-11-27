using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Blick
{
    // Run in a disposable OS profile. Only this test executable supplies sample pages.
    static class WorkspaceChecks
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Field<T>(MainForm form, string name)
        {
            return (T)typeof(MainForm).GetField(name, Private).GetValue(form);
        }
        static void Call(MainForm form, string name, params object[] args)
        {
            typeof(MainForm).GetMethod(name, Private).Invoke(form, args);
        }
        static Bitmap Sample(int page)
        {
            Bitmap sheet = new Bitmap(760, 1075);
            using (Graphics g = Graphics.FromImage(sheet))
            using (Font heading = new Font("Arial", 34, FontStyle.Bold))
            using (Font body = new Font("Arial", 16))
            using (Font small = new Font("Arial", 12))
            {
                g.Clear(Color.White);
                g.DrawString("BLICK   /   PRINT LAB", body, Brushes.Black, 62, 65);
                g.DrawLine(Pens.Black, 62, 110, 698, 110);
                string[] titles = { "Контрольный лист", "Градации серого", "Тестовые линии" };
                g.DrawString(titles[page], heading, Brushes.Black, 62, 164);
                g.DrawString("Проверка передачи тона и детализации", body, Brushes.DimGray, 62, 239);
                for (int i = 0; i < 12; i++)
                {
                    int v = 18 + i * 20;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(v, v, v)))
                        g.FillRectangle(b, 62 + i * 53, 332, 53, 182);
                }
                g.DrawString("01     Тоновая шкала", body, Brushes.Black, 62, 538);
                g.DrawString("12 ступеней от глубокого чёрного до светло-серого.", small, Brushes.DimGray, 62, 584);
                for (int row = 0; row < 3; row++)
                {
                    int y = 688 + row * 63;
                    g.DrawLine(Pens.Silver, 62, y, 698, y);
                    g.DrawString(new string[] { "Формат оригинала", "Режим сканирования", "Разрешение" }[row], small, Brushes.Black, 62, y + 16);
                    g.DrawString(new string[] { "A4", "Оттенки серого", "300 dpi" }[row], small, Brushes.Black, 492, y + 16);
                }
                g.DrawLine(Pens.Black, 62, 960, 698, 960);
                g.DrawString("Образец документа", small, Brushes.DimGray, 62, 981);
                g.DrawString((page + 1) + " / 3", small, Brushes.Black, 650, 981);
            }
            return sheet;
        }
        // Render client surfaces only. WM_PRINT includes the native non-client frame
        // on some Windows runners and clips windows larger than the virtual desktop.
        static void PaintClient(Control control, Graphics g)
        {
            using (PaintEventArgs e = new PaintEventArgs(g, control.ClientRectangle))
                typeof(Control).GetMethod("OnPaint", Private).Invoke(control, new object[] { e });
            for (int i = control.Controls.Count - 1; i >= 0; i--)
            {
                Control child = control.Controls[i];
                if (!child.Visible || child.Width < 1 || child.Height < 1) continue;
                System.Drawing.Drawing2D.GraphicsState state = g.Save();
                g.TranslateTransform(child.Left, child.Top);
                g.SetClip(child.ClientRectangle, System.Drawing.Drawing2D.CombineMode.Intersect);
                PaintClient(child, g);
                g.Restore(state);
            }
        }
        static void Capture(Form form, string path)
        {
            Application.DoEvents();
            using (Bitmap b = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                using (Graphics g = Graphics.FromImage(b)) PaintClient(form, g);
                b.Save(path, ImageFormat.Png);
            }
        }
        [STAThread]
        static void Main(string[] args)
        {
            try { NativeMethods.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            FontLoader.Load();
            string folder = args[0];
            Directory.CreateDirectory(folder);
            using (MainForm form = new MainForm())
            {
                form.MaximumSize = new Size(2400, 1800);
                form.Show();
                form.ClientSize = new Size(1400, 880);
                Stopwatch wait = Stopwatch.StartNew();
                while (Field<bool>(form, "detecting") && wait.ElapsedMilliseconds < 6000)
                { Application.DoEvents(); System.Threading.Thread.Sleep(16); }
                Call(form, "SetCollapsed", false, false);
                foreach (ToolStripDropDownCloseReason reason in new ToolStripDropDownCloseReason[] {
                    ToolStripDropDownCloseReason.AppClicked, ToolStripDropDownCloseReason.Keyboard,
                    ToolStripDropDownCloseReason.ItemClicked })
                {
                    using (ContextMenuStrip menu = WorkspaceMenu.Create())
                    {
                        menu.Items.Add("PDF");
                        menu.Show(form, new Point(12, 12));
                        menu.Close(reason);
                        Application.DoEvents();
                        if (!menu.IsDisposed) throw new Exception("Closed dropdown was not disposed");
                    }
                }
                Settings settings = Field<Settings>(form, "st");
                settings.Tab = 0;
                settings.Lang = "ru"; L.En = false;
                List<Page> pages = Field<List<Page>>(form, "pages");
                foreach (int page in new int[] { 0, 1, 2 }) pages.Add(new Page(Sample(page), 300));
                Call(form, "SetStatus", L.T("Документ для проверки интерфейса", "Interface test document"), 0);
                Call(form, "SelectPage", 0);
                Capture(form, Path.Combine(folder, "basic.png"));
                TabBar tabs = Field<TabBar>(form, "tabs");
                tabs.SelectedIndex = 1;
                Call(form, "LayoutRight");
                Call(form, "SelectPage", 1);
                Capture(form, Path.Combine(folder, "more.png"));
                foreach (int scale in SettingsPopup.Scales)
                {
                    Theme.Ui = scale / 100f;
                    Call(form, "RebuildUi");
                    form.ClientSize = new Size(Theme.D(870), Theme.D(610));
                    Call(form, "SetCollapsed", false, false);
                    if (Field<PreviewPanel>(form, "preview").Width < Theme.D(160)) throw new Exception("Preview too narrow at " + scale);
                    if (!Field<IconButton>(form, "nextPage").Enabled) throw new Exception("Lost page navigation");
                    form.Refresh(); Application.DoEvents();
                }
                L.En = true;
                Call(form, "RebuildUi");
                if (Field<IconButton>(form, "navDevices").Text != "Devices") throw new Exception("Language rebuild failed");
                Call(form, "ShowHistory");
                Call(form, "ClosePopup");
                Call(form, "ShowSettings");
                Call(form, "ClosePopup");
                Console.WriteLine("Workspace rendered at five scales. Three pages, navigation, both languages and popups checked.");
            }
        }
    }
}
