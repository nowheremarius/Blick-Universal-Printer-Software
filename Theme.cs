using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Blick
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            try { NativeMethods.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { Report(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Report(e.ExceptionObject as Exception); };
            ImportPreviousSettings();
            FontLoader.Load();
            Application.Run(new MainForm());
        }

        static void ImportPreviousSettings()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string previous = Path.Combine(appData, "Blik");
            string current = Path.Combine(appData, "Blick");
            foreach (string name in new string[] { "settings.ini", "history.txt" })
            {
                try
                {
                    string source = Path.Combine(previous, name);
                    string target = Path.Combine(current, name);
                    if (File.Exists(source) && !File.Exists(target))
                    {
                        Directory.CreateDirectory(current);
                        File.Copy(source, target, false);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        static bool reporting;
        static void Report(Exception ex)
        {
            if (ex == null || reporting) return;
            reporting = true;
            string log = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blick"), "error.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log));
                File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
            try
            {
                MessageBox.Show(
                    L.T("Ошибка в Blick.\n\n", "Blick encountered an error.\n\n") + ex.Message +
                    L.T("\n\nЖурнал ошибок: ", "\n\nError log: ") + log,
                    "Blick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
            reporting = false;
        }
    }

    // язык интерфейса
    static class L
    {
        public static bool En;
        public static string T(string ru, string en) { return En ? en : ru; }
        public static CultureInfo Culture { get { return new CultureInfo(En ? "en-US" : "ru-RU"); } }
    }

    static class NativeMethods
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("gdi32.dll")] public static extern IntPtr AddFontMemResourceEx(IntPtr data, uint size, IntPtr reserved, ref uint count);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);


        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

    }

    interface ISurface { void PaintSurface(Graphics g); }
    static class Glass
    {
        // Нарисовать под элементом всё, что нарисовали бы его родители (стекло окна, карточки панелей)
        public static void PaintBehind(Graphics g, Control c)
        {
            List<Control> chain = new List<Control>();
            for (Control p = c.Parent; p != null; p = p.Parent) chain.Add(p);
            if (chain.Count == 0) return;
            Point screen = c.PointToScreen(Point.Empty);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                ISurface s = chain[i] as ISurface;
                if (s == null) continue;
                Point off = chain[i].PointToClient(screen);
                GraphicsState st = g.Save();
                g.TranslateTransform(-off.X, -off.Y);
                s.PaintSurface(g);
                g.Restore(st);
            }
        }

    }

    class GlassWindow : Form, ISurface
    {
        protected const int WS_CAPTION = 0xC00000, WS_THICKFRAME = 0x40000, WS_SYSMENU = 0x80000,
                            WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000, WS_EX_COMPOSITED = 0x02000000;
        protected bool FullFrame = true; // для главного окна — свернуть/развернуть

        public GlassWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            DoubleBuffered = true;
            BackColor = Theme.Base;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= WS_CAPTION | WS_THICKFRAME;
                // без WS_SYSMENU: иначе Windows рисует поверх стекла свои кнопки свернуть/закрыть
                if (FullFrame) cp.Style |= WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1, round = 2;
                NativeMethods.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                NativeMethods.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x83) // WM_NCCALCSIZE — всё окно клиентское
            {
                if (m.WParam != IntPtr.Zero && WindowState == FormWindowState.Maximized)
                {
                    // развёрнутое окно Windows выдвигает за край экрана на толщину рамки — подрезаем до рабочей области
                    NativeMethods.RECT r = (NativeMethods.RECT)Marshal.PtrToStructure(m.LParam, typeof(NativeMethods.RECT));
                    Rectangle wa = Screen.FromRectangle(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)).WorkingArea;
                    r.Left = Math.Max(r.Left, wa.Left); r.Top = Math.Max(r.Top, wa.Top);
                    r.Right = Math.Min(r.Right, wa.Right); r.Bottom = Math.Min(r.Bottom, wa.Bottom);
                    Marshal.StructureToPtr(r, m.LParam, false);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        public virtual void PaintSurface(Graphics g)
        {
            using (SolidBrush brush = new SolidBrush(this is MainForm ? Theme.Base : Theme.Panel))
                g.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { PaintSurface(e.Graphics); }
    }

    // шрифты
    static class FontLoader
    {
        static readonly PrivateFontCollection pfc = new PrivateFontCollection();
        static readonly Dictionary<string, FontFamily> families = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        public static string Regular = "Segoe UI", Semi = "Segoe UI Semibold", Heavy = "Segoe UI";
        public static bool SemiNeedsBold, HeavyNeedsBold = true;

        public static void Load()
        {
            try
            {
                foreach (string weight in new string[] { "Regular", "Bold" })
                {
                    using (Stream stream = typeof(FontLoader).Assembly.GetManifestResourceStream("Inter-" + weight))
                    {
                        if (stream == null) continue;
                        using (BinaryReader reader = new BinaryReader(stream))
                        {
                            byte[] data = reader.ReadBytes((int)stream.Length);
                            // GDI+ requires the font buffer for the lifetime of the private collection.
                            IntPtr mem = Marshal.AllocCoTaskMem(data.Length);
                            Marshal.Copy(data, 0, mem, data.Length);
                            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                            {
                                uint count = 0;
                                NativeMethods.AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, ref count);
                            }
                            pfc.AddMemoryFont(mem, data.Length);
                        }
                    }
                }
                foreach (FontFamily ff in pfc.Families) families[ff.Name] = ff;
            }
            catch { }

            HashSet<string> installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { using (InstalledFontCollection ic = new InstalledFontCollection()) foreach (FontFamily ff in ic.Families) installed.Add(ff.Name); }
            catch { }

            string[] prefs = new string[] { "Inter Variable Text", "Inter Variable", "Inter", "Segoe UI" };
            foreach (string name in prefs)
            {
                bool priv = families.ContainsKey(name);
                if (!priv && !installed.Contains(name)) continue;
                if (installed.Contains(name)) families.Remove(name);
                Regular = name;
                if (name == "Segoe UI") { Semi = "Segoe UI Semibold"; SemiNeedsBold = !installed.Contains("Segoe UI Semibold"); if (SemiNeedsBold) Semi = name; Heavy = name; HeavyNeedsBold = true; return; }
                if (Has(name + " SemiBold", installed)) { Semi = name + " SemiBold"; SemiNeedsBold = false; }
                else if (Has(name + " Semibold", installed)) { Semi = name + " Semibold"; SemiNeedsBold = false; }
                else if (Has(name + " Medium", installed)) { Semi = name + " Medium"; SemiNeedsBold = false; }
                else { Semi = name; SemiNeedsBold = true; }
                if (Has(name + " Bold", installed)) { Heavy = name + " Bold"; HeavyNeedsBold = false; }
                else { Heavy = name; HeavyNeedsBold = true; }
                return;
            }
        }

        static bool Has(string name, HashSet<string> installed) { return families.ContainsKey(name) || installed.Contains(name); }

        public static Font Make(string family, float size, FontStyle style)
        {
            try
            {
                FontFamily ff;
                if (families.TryGetValue(family, out ff))
                {
                    if (!ff.IsStyleAvailable(style)) style = ff.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
                    return new Font(ff, size, style);
                }
                return new Font(family, size, style);
            }
            catch { return new Font("Segoe UI", size, style); }
        }
    }

    // Цвета и размеры рабочего пространства.
    static class Theme
    {
        public static float Dpi = 1f, Ui = 1f;
        public static float Scale { get { return Dpi * Ui; } }
        public static int D(float v) { return (int)Math.Round(v * Scale); }

        static Color Gray(int value) { return Color.FromArgb(value, value, value); }

        public static readonly Color Base = Gray(17), Panel = Gray(25), Raised = Gray(34);
        public static readonly Color Inset = Gray(34), Track = Gray(25), Hover = Gray(44), Press = Gray(60);
        public static readonly Color Stroke = Gray(41), StrokeStrong = Gray(76);
        public static readonly Color EdgeTop = StrokeStrong, EdgeBottom = Stroke;
        public static readonly Color Text = Gray(242), Text2 = Gray(208), Muted = Gray(160), Dim = Gray(112);
        public static readonly Color Error = Text, Paper = Color.White, Accent = Text;
        public static readonly Color AccentHover = Gray(219), AccentInk = Base, Selection = Text;
        public static readonly Color Overlay = Color.FromArgb(150, 0, 0, 0);

        public static Font FontLabel, FontChip, FontBody, FontSmall, FontTitle, FontLogo, FontBtn, FontBig, FontHuge;

        static Theme() { MakeFonts(); }

        public static void MakeFonts()
        {
            float u = Ui;
            string r = FontLoader.Regular, s = FontLoader.Semi, h = FontLoader.Heavy;
            FontStyle sb = FontLoader.SemiNeedsBold ? FontStyle.Bold : FontStyle.Regular;
            FontStyle hb = FontLoader.HeavyNeedsBold ? FontStyle.Bold : FontStyle.Regular;
            FontLabel = FontLoader.Make(r, Math.Max(8f, 9f * u), FontStyle.Regular);
            FontChip = FontLoader.Make(s, Math.Max(8f, 9f * u), sb);
            FontBody = FontLoader.Make(r, Math.Max(8f, 9.5f * u), FontStyle.Regular);
            FontSmall = FontLoader.Make(r, Math.Max(8f, 9f * u), FontStyle.Regular);
            FontTitle = FontLoader.Make(h, 16f * u, hb);
            FontLogo = FontLoader.Make(s, 12f * u, sb);
            FontBtn = FontLoader.Make(s, Math.Max(8f, 9.5f * u), sb);
            FontBig = FontLoader.Make(s, 12f * u, sb);
            FontHuge = FontLoader.Make(h, 30f * u, hb);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Fade(Color c, float k)
        {
            k = Math.Max(0f, Math.Min(1f, k));
            return Color.FromArgb((int)(c.A * k), c.R, c.G, c.B);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            if (d < 1f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Hq(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.CompositingQuality = CompositingQuality.HighSpeed;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            if (c.A == 0) return;
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Color c, float width, RectangleF r, float rad)
        {
            if (c.A == 0) return;
            using (GraphicsPath p = Round(r, rad))
            using (Pen pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static void Edge(Graphics g, RectangleF r, float rad, Color top, Color bottom)
        {
            if (r.Width < 2 || r.Height < 2) return;
            StrokeRound(g, top, 1f, r, Math.Min(rad, D(12)));
        }

        public static void SelGlass(Graphics g, RectangleF r, float rad, float k)
        {
            FillRound(g, Fade(Hover, k), r, Math.Min(rad, D(10)));
            StrokeRound(g, Fade(StrokeStrong, k), 1f, r, Math.Min(rad, D(10)));
        }

        public static void GlassRect(Graphics g, RectangleF r, float rad, Color fill, Color stroke)
        {
            FillRound(g, fill, r, rad);
            StrokeRound(g, stroke, 1f, r, rad);
        }

        public static void Ring(Graphics g, Color c, float width, float cx, float cy, float dia)
        {
            using (Pen p = new Pen(c, width)) g.DrawEllipse(p, cx - dia / 2f, cy - dia / 2f, dia, dia);
        }

        public static void Dot(Graphics g, Color c, float cx, float cy, float dia)
        {
            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, cx - dia / 2f, cy - dia / 2f, dia, dia);
        }

        // Use the same spacing for measurement and painting, including at fractional DPI.
        static readonly StringFormat textFormat = StringFormat.GenericDefault;
        static StringFormat Fmt(int align, bool vcenter, bool wrap, bool ellipsis)
        {
            StringFormat sf = new StringFormat(textFormat);
            sf.Alignment = align == 1 ? StringAlignment.Center : (align == 2 ? StringAlignment.Far : StringAlignment.Near);
            sf.LineAlignment = vcenter ? StringAlignment.Center : StringAlignment.Near;
            sf.Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;
            sf.FormatFlags = wrap ? StringFormatFlags.LineLimit : (StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces);
            return sf;
        }

        // align: 0 — влево, 1 — по центру, 2 — вправо
        public static void Write(Graphics g, string t, Font f, RectangleF r, Color c, int align, bool vcenter)
        {
            if (string.IsNullOrEmpty(t) || c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
            using (StringFormat sf = Fmt(align, vcenter, false, true))
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(t, f, b, r, sf);
        }

        public static void TextWrap(Graphics g, string t, Font f, RectangleF r, Color c)
        {
            if (string.IsNullOrEmpty(t) || c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
            using (StringFormat sf = Fmt(0, false, true, true))
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(t, f, b, r, sf);
        }

        public static void TextC(Graphics g, string t, Font f, RectangleF r, Color c) { Write(g, t, f, r, c, 1, true); }

        public static void TextAt(Graphics g, string t, Font f, float x, float y, Color c)
        {
            if (string.IsNullOrEmpty(t) || c.A == 0) return;
            using (StringFormat sf = Fmt(0, false, false, false))
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(t, f, b, x, y, sf);
        }

        public static void TextAtBrush(Graphics g, string t, Font f, Brush b, float x, float y)
        {
            if (string.IsNullOrEmpty(t)) return;
            using (StringFormat sf = Fmt(0, false, false, false)) g.DrawString(t, f, b, x, y, sf);
        }

        public static SizeF MeasureF(Graphics g, string t, Font f)
        {
            if (string.IsNullOrEmpty(t)) return new SizeF(0, f.GetHeight(g));
            using (StringFormat sf = Fmt(0, false, false, false)) return g.MeasureString(t, f, PointF.Empty, sf);
        }

        public static Size Measure(Graphics g, string t, Font f)
        {
            SizeF s = MeasureF(g, t, f);
            return new Size((int)Math.Ceiling(s.Width) + 1, (int)Math.Ceiling(s.Height));
        }
    }

    // анимация
    interface IAnimated { bool Step(); }

    static class Anim
    {
        static readonly List<IAnimated> items = new List<IAnimated>();
        static System.Windows.Forms.Timer timer;

        public static void Kick(IAnimated a)
        {
            if (!items.Contains(a)) items.Add(a);
            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer();
                timer.Interval = 15;
                timer.Tick += Tick;
            }
            if (!timer.Enabled) timer.Start();
        }

        static void Tick(object sender, EventArgs e)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                bool more = false;
                try { more = items[i].Step(); } catch { }
                if (!more) items.RemoveAt(i);
            }
            if (items.Count == 0) timer.Stop();
        }

        public static float To(float cur, float target, float speed)
        {
            float d = target - cur;
            if (Math.Abs(d) < 0.01f) return target;
            return cur + d * speed;
        }
    }

    // иконки (квадрат 24×24)
    static class Icons
    {
        static readonly string[] Names = "house scan-line printer history printer-check settings circle-help file file-plus folder-open save file-output files trash-2 rotate-ccw rotate-cw crop arrow-up arrow-down layout-grid list file-text mouse-pointer-2 hand zoom-in zoom-out maximize scan chevron-left chevron-right refresh-cw layers image grip file-type scan-text copy sliders-horizontal sun contrast palette plus minus x check chevron-down ellipsis search info triangle-alert circle-x circle-check loader-circle moon monitor download wifi usb eye timer square".Split(' ');
        static readonly Bitmap Atlas = Load("Lucide");
        static readonly Bitmap Mark = Load("BlickMark");

        static Bitmap Load(string name)
        {
            using (Stream stream = typeof(Icons).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null) return null;
                using (Bitmap image = new Bitmap(stream)) return new Bitmap(image);
            }
        }

        public static void Logo(Graphics g, Color c, float x, float y, float size)
        {
            if (Mark != null) g.DrawImage(Mark, x, y, size, size);
        }

        public static void Draw(Graphics g, string kind, Color color, float x, float y, float size, float stroke)
        {
            switch (kind)
            {
                case "zoomin": kind = "zoom-in"; break;
                case "zoomout": kind = "zoom-out"; break;
                case "fit": kind = "maximize"; break;
                case "rotate": kind = "rotate-cw"; break;
                case "repeat": kind = "refresh-cw"; break;
                case "gear": kind = "settings"; break;
                case "trash": kind = "trash-2"; break;
                case "folder": kind = "folder-open"; break;
                case "scan": kind = "scan-line"; break;
                case "chevl": kind = "chevron-left"; break;
                case "chevr": kind = "chevron-right"; break;
                case "stop": case "max": kind = "square"; break;
                case "min": kind = "minus"; break;
                case "close": kind = "x"; break;
            }
            int index = Array.IndexOf(Names, kind);
            if (index < 0 || Atlas == null) return;
            ColorMatrix tint = new ColorMatrix();
            tint.Matrix00 = color.R / 255f;
            tint.Matrix11 = color.G / 255f;
            tint.Matrix22 = color.B / 255f;
            tint.Matrix33 = color.A / 255f;
            using (ImageAttributes attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(tint);
                g.DrawImage(Atlas, Rectangle.Round(new RectangleF(x, y, size, size)),
                    index % 8 * 96, index / 8 * 96, 96, 96, GraphicsUnit.Pixel, attributes);
            }
        }
    }
}
