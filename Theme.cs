// Блик — стекло, тема, шрифты, языки, анимация, иконки
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

namespace Blik
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
            FontLoader.Load();
            Application.Run(new MainForm());
        }

        static bool reporting;
        static void Report(Exception ex)
        {
            if (ex == null || reporting) return;
            reporting = true;
            string log = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blik"), "error.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log));
                File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
            try
            {
                MessageBox.Show(
                    L.T("Что-то пошло не так, но Blick продолжает работать.\n\n", "Something went wrong, but Blick keeps working.\n\n") + ex.Message +
                    L.T("\n\nПодробности записаны в ", "\n\nDetails were saved to ") + log + L.T(" — их можно отправить Claude.", " — you can send them to Claude."),
                    "Blick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
            reporting = false;
        }
    }

    // ---------------------------------------------------------------- язык интерфейса
    static class L
    {
        public static bool En;
        public static string T(string ru, string en) { return En ? en : ru; }
        public static CultureInfo Culture { get { return new CultureInfo(En ? "en-US" : "ru-RU"); } }
    }

    static class NativeMethods
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
        [DllImport("gdi32.dll")] public static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("ntdll.dll")] public static extern int RtlGetVersion(ref OSVERSIONINFO info);

        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct OSVERSIONINFO
        {
            public int Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Csd;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
    }

    // ---------------------------------------------------------------- стекло
    // Окно рисуется с настоящей прозрачностью: Windows 11 размывает всё, что под окном (акрил),
    // а мы поверх кладём полупрозрачные слои. Обычная GDI-отрисовка WinForms прозрачность не умеет,
    // поэтому каждый элемент рисует себя в собственный 32-битный буфер с альфой и копирует его в окно.
    interface ISurface { void PaintSurface(Graphics g); }
    interface ISnapshot { Bitmap Snapshot { get; } }

    static class Glass
    {
        public static bool Enabled;    // включилось ли настоящее размытие
        static int build = -1;

        public static int Build
        {
            get
            {
                if (build < 0)
                {
                    build = 0;
                    try
                    {
                        NativeMethods.OSVERSIONINFO v = new NativeMethods.OSVERSIONINFO();
                        v.Size = Marshal.SizeOf(typeof(NativeMethods.OSVERSIONINFO));
                        if (NativeMethods.RtlGetVersion(ref v) == 0 && v.Major >= 10) build = v.Build;
                    }
                    catch { }
                }
                return build;
            }
        }

        // Настроить окно: тёмный режим, скруглённые углы и (на Windows 11 22H2+) акриловое размытие
        public static bool Setup(IntPtr h)
        {
            try { int dark = 1; NativeMethods.DwmSetWindowAttribute(h, 20, ref dark, 4); } catch { }
            try { int round = 2; NativeMethods.DwmSetWindowAttribute(h, 33, ref round, 4); } catch { }
            if (Build < 22621) return false;
            try
            {
                NativeMethods.MARGINS m = new NativeMethods.MARGINS();
                m.Left = m.Right = m.Top = m.Bottom = -1;
                if (NativeMethods.DwmExtendFrameIntoClientArea(h, ref m) != 0) return false;
                int acrylic = 3; // DWMSBT_TRANSIENTWINDOW
                if (NativeMethods.DwmSetWindowAttribute(h, 38, ref acrylic, 4) == 0) return true;
                m.Left = m.Right = m.Top = m.Bottom = 0;
                NativeMethods.DwmExtendFrameIntoClientArea(h, ref m);
            }
            catch { }
            return false;
        }

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

        // Общая отрисовка через буфер с альфой
        public static void Paint(Control c, PaintEventArgs e, GlassBuffer buf, Action<Graphics> render)
        {
            int w = c.ClientSize.Width, h = c.ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            Rectangle clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(0, 0, w, h));
            if (clip.Width <= 0 || clip.Height <= 0) return;
            if (!buf.Ensure(w, h)) return;
            using (Graphics g = Graphics.FromImage(buf.Bmp))
            {
                g.SetClip(clip);
                g.Clear(Color.Transparent);
                Theme.Hq(g);
                PaintBehind(g, c);
                ISurface self = c as ISurface;
                if (self != null) self.PaintSurface(g);
                if (render != null) render(g);
            }
            IntPtr dst = e.Graphics.GetHdc();
            try
            {
                NativeMethods.GdiFlush();
                NativeMethods.BitBlt(dst, clip.X, clip.Y, clip.Width, clip.Height, buf.Hdc, clip.X, clip.Y, 0x00CC0020);
            }
            finally { e.Graphics.ReleaseHdc(dst); }
        }
    }

    // Снимок окна из буферов всех элементов (с альфой — стекло под ним продолжает просвечивать)
    static class WindowShot
    {
        public static Bitmap Capture(Control root)
        {
            int w = root.ClientSize.Width, h = root.ClientSize.Height;
            if (w <= 0 || h <= 0) return null;
            try { NativeMethods.RedrawWindow(root.Handle, IntPtr.Zero, IntPtr.Zero, 0x0100 | 0x0080); } catch { } // дорисовать всё, что ждёт отрисовки
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                Compose(g, root, Point.Empty);
            }
            return bmp;
        }

        static void Compose(Graphics g, Control c, Point at)
        {
            ISnapshot s = c as ISnapshot;
            Bitmap b = s != null ? s.Snapshot : null;
            if (b != null) g.DrawImage(b, new Rectangle(at.X, at.Y, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel);
            for (int i = c.Controls.Count - 1; i >= 0; i--)
            {
                Control ch = c.Controls[i];
                if (!ch.Visible) continue;
                Compose(g, ch, new Point(at.X + ch.Left, at.Y + ch.Top));
            }
        }

        // Мягкое размытие: уменьшить в factor раз и растянуть обратно
        public static Bitmap Blur(Bitmap src, int factor)
        {
            int w = Math.Max(1, src.Width / factor), h = Math.Max(1, src.Height / factor);
            using (Bitmap small = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
                Bitmap big = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(big))
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(small, new Rectangle(0, 0, big.Width, big.Height), 0, 0, w, h, GraphicsUnit.Pixel, ia);
                }
                return big;
            }
        }
    }

    // 32-битный буфер с альфой (DIB-секция, на которой рисует GDI+)
    sealed class GlassBuffer : IDisposable
    {
        public Bitmap Bmp;
        public IntPtr Hdc;
        IntPtr hbmp, oldObj;
        int w, h;

        public bool Ensure(int width, int height)
        {
            if (Bmp != null && width == w && height == h) return true;
            Dispose();
            NativeMethods.BITMAPINFOHEADER bi = new NativeMethods.BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
            bi.biWidth = width; bi.biHeight = -height; bi.biPlanes = 1; bi.biBitCount = 32; bi.biCompression = 0;
            IntPtr bits;
            hbmp = NativeMethods.CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (hbmp == IntPtr.Zero || bits == IntPtr.Zero) { hbmp = IntPtr.Zero; return false; }
            Hdc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            oldObj = NativeMethods.SelectObject(Hdc, hbmp);
            Bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
            w = width; h = height;
            return true;
        }

        public void Dispose()
        {
            if (Bmp != null) { Bmp.Dispose(); Bmp = null; }
            if (Hdc != IntPtr.Zero) { NativeMethods.SelectObject(Hdc, oldObj); NativeMethods.DeleteDC(Hdc); Hdc = IntPtr.Zero; }
            if (hbmp != IntPtr.Zero) { NativeMethods.DeleteObject(hbmp); hbmp = IntPtr.Zero; }
            w = h = 0;
        }
    }

    // Окно без рамки со стеклом. Стили обычного окна оставлены, чтобы Windows проигрывала
    // родные анимации и давала акрил, а рамку и заголовок срезаем в WM_NCCALCSIZE.
    class GlassWindow : Form, ISurface, ISnapshot
    {
        protected const int WS_CAPTION = 0xC00000, WS_THICKFRAME = 0x40000, WS_SYSMENU = 0x80000,
                            WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000, WS_EX_COMPOSITED = 0x02000000;
        readonly GlassBuffer buf = new GlassBuffer();
        protected bool FullFrame = true; // для главного окна — свернуть/развернуть
        public Bitmap Snapshot { get { return buf.Bmp; } }

        public GlassWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            DoubleBuffered = false;
            BackColor = Color.Black;
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
            bool ok = Glass.Setup(Handle);
            if (this is MainForm) Glass.Enabled = ok;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x86 && Glass.Enabled) // WM_NCACTIVATE: держим окно «активным» для Windows — иначе стекло гаснет в серый
            {
                m.WParam = (IntPtr)1;
                base.WndProc(ref m);
                m.Result = (IntPtr)1;
                return;
            }
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

        Bitmap backdrop;
        bool backdropGlass;

        // Подложка окна с мягким синим свечением снизу; рисуется один раз на размер окна
        Bitmap Backdrop()
        {
            int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
            if (backdrop != null && backdrop.Width == w && backdrop.Height == h && backdropGlass == Glass.Enabled) return backdrop;
            if (backdrop != null) backdrop.Dispose();
            backdropGlass = Glass.Enabled;
            backdrop = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(backdrop))
            {
                g.Clear(Theme.Base);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Glow(g, new RectangleF(w * 0.05f, h * 0.62f, w * 0.9f, h * 0.9f), Color.FromArgb(70, 70, 116, 255));
                Glow(g, new RectangleF(-w * 0.25f, -h * 0.35f, w * 0.7f, h * 0.8f), Color.FromArgb(26, 120, 100, 255));
            }
            return backdrop;
        }

        static void Glow(Graphics g, RectangleF r, Color c)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(r);
                using (PathGradientBrush pb = new PathGradientBrush(p))
                {
                    pb.CenterColor = c;
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, c) };
                    g.FillPath(pb, p);
                }
            }
        }

        public virtual void PaintSurface(Graphics g)
        {
            Bitmap bd = Backdrop();
            g.DrawImage(bd, new Rectangle(0, 0, bd.Width, bd.Height), 0, 0, bd.Width, bd.Height, GraphicsUnit.Pixel);
            if (!Glass.Enabled && Glass.Build < 22000)
                using (Pen p = new Pen(Theme.Stroke)) g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { Glass.Paint(this, e, buf, null); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { buf.Dispose(); if (backdrop != null) { backdrop.Dispose(); backdrop = null; } }
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------- шрифты
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
                string dir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "fonts");
                if (Directory.Exists(dir))
                {
                    List<string> files = new List<string>();
                    files.AddRange(Directory.GetFiles(dir, "*.ttf", SearchOption.AllDirectories));
                    files.AddRange(Directory.GetFiles(dir, "*.otf", SearchOption.AllDirectories));
                    bool hasStatic = false;
                    foreach (string f in files) if (f.IndexOf("Variable", StringComparison.OrdinalIgnoreCase) < 0) hasStatic = true;
                    foreach (string f in files)
                    {
                        if (hasStatic && f.IndexOf("Variable", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (f.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        try
                        {
                            byte[] data = File.ReadAllBytes(f);
                            IntPtr mem = Marshal.AllocCoTaskMem(data.Length);
                            Marshal.Copy(data, 0, mem, data.Length);
                            uint n = 0;
                            NativeMethods.AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, ref n);
                            pfc.AddMemoryFont(mem, data.Length); // память не освобождаем — шрифт живёт до выхода
                        }
                        catch { }
                    }
                    foreach (FontFamily ff in pfc.Families) families[ff.Name] = ff;
                }
            }
            catch { }

            HashSet<string> installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { using (InstalledFontCollection ic = new InstalledFontCollection()) foreach (FontFamily ff in ic.Families) installed.Add(ff.Name); }
            catch { }

            string[] prefs = new string[] { "Inter", "Montserrat", "Segoe UI" };
            foreach (string name in prefs)
            {
                bool priv = families.ContainsKey(name);
                if (!priv && !installed.Contains(name)) continue;
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

    // ---------------------------------------------------------------- тема (тёмное стекло, монохром)
    static class Theme
    {
        public static float Dpi = 1f, Ui = 1f;
        public static float Scale { get { return Dpi * Ui; } }
        public static int D(float v) { return (int)Math.Round(v * Scale); }

        static Color W(int a) { return Color.FromArgb(a, 255, 255, 255); }
        static Color B(int a) { return Color.FromArgb(a, 0, 0, 0); }

        static Color N(int a, int r, int g, int b) { return Color.FromArgb(a, r, g, b); }

        // Подложка окна — глубокий тёмно-синий: на стекле полупрозрачный, без стекла сплошной
        public static Color Base { get { return Glass.Enabled ? N(168, 6, 10, 22) : N(255, 9, 13, 26); } }

        public static readonly Color Raised = N(58, 26, 36, 62);       // стеклянные карточки
        public static readonly Color Inset = N(110, 3, 6, 16);         // поля внутри карточек
        public static readonly Color Track = N(60, 3, 6, 16);          // дорожка переключателей
        public static readonly Color Hover = N(34, 150, 176, 255);     // наведение — светлее, с синевой
        public static readonly Color Press = N(52, 150, 176, 255);
        public static readonly Color Stroke = N(30, 190, 205, 255);    // тонкие рамки
        public static readonly Color StrokeStrong = N(60, 200, 214, 255);
        public static readonly Color EdgeTop = N(64, 210, 222, 255);   // рамка карточки сверху (свет)
        public static readonly Color EdgeBottom = N(14, 210, 222, 255);// ...и снизу (почти нет)

        public static readonly Color Text = N(255, 238, 241, 248);
        public static readonly Color Text2 = N(255, 192, 199, 216);
        public static readonly Color Muted = N(255, 128, 138, 162);
        public static readonly Color Dim = N(255, 78, 86, 108);
        public static readonly Color Error = N(255, 255, 132, 132);
        public static readonly Color Paper = N(255, 246, 247, 250);
        public static readonly Color Accent = Color.White;                    // сплошные элементы (главная кнопка)
        public static readonly Color AccentHover = N(255, 222, 228, 242);
        public static readonly Color AccentInk = N(255, 8, 12, 24);
        public static readonly Color Blue = N(255, 120, 160, 255);            // мягкий синий акцент
        public static readonly Color Overlay = N(160, 2, 5, 14);

        public static Font FontLabel, FontChip, FontBody, FontSmall, FontTitle, FontLogo, FontBtn, FontBig, FontHuge;

        static Theme() { MakeFonts(); }

        public static void MakeFonts()
        {
            float u = Ui;
            string r = FontLoader.Regular, s = FontLoader.Semi, h = FontLoader.Heavy;
            FontStyle sb = FontLoader.SemiNeedsBold ? FontStyle.Bold : FontStyle.Regular;
            FontStyle hb = FontLoader.HeavyNeedsBold ? FontStyle.Bold : FontStyle.Regular;
            FontLabel = FontLoader.Make(r, 8.5f * u, FontStyle.Regular);
            FontChip = FontLoader.Make(s, 9f * u, sb);
            FontBody = FontLoader.Make(r, 9.5f * u, FontStyle.Regular);
            FontSmall = FontLoader.Make(r, 8.5f * u, FontStyle.Regular);
            FontTitle = FontLoader.Make(h, 16f * u, hb);
            FontLogo = FontLoader.Make(s, 12f * u, sb);
            FontBtn = FontLoader.Make(s, 9.5f * u, sb);
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

        // Тот же цвет с умноженной прозрачностью
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
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
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

        // Тонкая рамка, которая светлее сверху и тает книзу — главный приём стекла на референсах
        public static void Edge(Graphics g, RectangleF r, float rad, Color top, Color bottom)
        {
            if (r.Width < 2 || r.Height < 2) return;
            using (GraphicsPath p = Round(r, rad))
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), top, bottom, LinearGradientMode.Vertical))
            using (Pen pen = new Pen(lg, 1f))
                g.DrawPath(pen, p);
        }

        // Выбранный элемент: светлая синеватая плашка с тонкой рамкой (как подсвеченный пункт меню)
        public static void SelGlass(Graphics g, RectangleF r, float rad, float k)
        {
            if (k <= 0.01f || r.Width < 2 || r.Height < 2) return;
            FillRound(g, N((int)(44 * k), 150, 178, 255), r, rad);
            Edge(g, r, rad, N((int)(96 * k), 200, 216, 255), N((int)(26 * k), 200, 216, 255));
        }

        // Стеклянная карточка: еле заметная заливка + рамка, светлая сверху
        public static void GlassRect(Graphics g, RectangleF r, float rad, Color fill, Color stroke)
        {
            FillRound(g, fill, r, rad);
            Edge(g, r, rad, EdgeTop, EdgeBottom);
        }

        public static void Ring(Graphics g, Color c, float width, float cx, float cy, float dia)
        {
            using (Pen p = new Pen(c, width)) g.DrawEllipse(p, cx - dia / 2f, cy - dia / 2f, dia, dia);
        }

        public static void Dot(Graphics g, Color c, float cx, float cy, float dia)
        {
            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, cx - dia / 2f, cy - dia / 2f, dia, dia);
        }

        // ---- текст (только GDI+: обычный GDI-текст на стекле становится прозрачным)
        static readonly StringFormat typo = StringFormat.GenericTypographic;
        static StringFormat Fmt(int align, bool vcenter, bool wrap, bool ellipsis)
        {
            StringFormat sf = new StringFormat(typo);
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

    // ---------------------------------------------------------------- анимация
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

    // ---------------------------------------------------------------- иконки (квадрат 24×24)
    static class Icons
    {
        static PointF[] P(params float[] xy)
        {
            PointF[] r = new PointF[xy.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = new PointF(xy[i * 2], xy[i * 2 + 1]);
            return r;
        }

        // Знак Блика — четырёхлучевая искра (блик на стекле) и маленькая искра рядом
        public static GraphicsPath Spark(float cx, float cy, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float k = r * 0.11f, q = r * 0.3f;
            p.AddBezier(cx, cy - r, cx + k, cy - q, cx + q, cy - k, cx + r, cy);
            p.AddBezier(cx + r, cy, cx + q, cy + k, cx + k, cy + q, cx, cy + r);
            p.AddBezier(cx, cy + r, cx - k, cy + q, cx - q, cy + k, cx - r, cy);
            p.AddBezier(cx - r, cy, cx - q, cy - k, cx - k, cy - q, cx, cy - r);
            p.CloseFigure();
            return p;
        }

        public static void Logo(Graphics g, Color c, float x, float y, float size)
        {
            float s = size / 24f;
            using (SolidBrush b = new SolidBrush(c))
            {
                using (GraphicsPath p = Spark(x + 10.5f * s, y + 13f * s, 9.5f * s)) g.FillPath(b, p);
                using (GraphicsPath p = Spark(x + 19.5f * s, y + 4.5f * s, 3.6f * s)) g.FillPath(b, p);
            }
        }

        public static void Draw(Graphics g, string kind, Color color, float x, float y, float size, float stroke)
        {
            if (kind == "logo") { Logo(g, color, x, y, size); return; }
            GraphicsState state = g.Save();
            g.TranslateTransform(x, y);
            g.ScaleTransform(size / 24f, size / 24f);
            using (Pen p = new Pen(color, stroke))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                switch (kind)
                {
                    case "zoomin":
                        g.DrawEllipse(p, 4f, 4f, 14f, 14f); g.DrawLine(p, 16f, 16f, 20.5f, 20.5f);
                        g.DrawLine(p, 11f, 8f, 11f, 14f); g.DrawLine(p, 8f, 11f, 14f, 11f); break;
                    case "zoomout":
                        g.DrawEllipse(p, 4f, 4f, 14f, 14f); g.DrawLine(p, 16f, 16f, 20.5f, 20.5f);
                        g.DrawLine(p, 8f, 11f, 14f, 11f); break;
                    case "fit":
                        g.DrawLines(p, P(3, 8, 3, 3, 8, 3)); g.DrawLines(p, P(16, 3, 21, 3, 21, 8));
                        g.DrawLines(p, P(21, 16, 21, 21, 16, 21)); g.DrawLines(p, P(8, 21, 3, 21, 3, 16)); break;
                    case "rotate":
                        g.DrawArc(p, 3f, 3f, 18f, 18f, 30f, 290f);
                        g.DrawLines(p, P(20, 2.5f, 20, 7.5f, 15, 7.5f)); break;
                    case "repeat":
                        g.DrawArc(p, 4f, 4f, 16f, 16f, 190f, 150f);
                        g.DrawLines(p, P(17.5f, 2.5f, 18.6f, 7.2f, 14f, 8f));
                        g.DrawArc(p, 4f, 4f, 16f, 16f, 10f, 150f);
                        g.DrawLines(p, P(6.5f, 21.5f, 5.4f, 16.8f, 10f, 16f)); break;
                    case "history":
                        g.DrawEllipse(p, 4f, 4f, 16f, 16f); g.DrawLines(p, P(12, 8, 12, 12, 15, 14)); break;
                    case "timer":
                        g.DrawEllipse(p, 5f, 6.5f, 14f, 14f); g.DrawLine(p, 12f, 13.5f, 12f, 10f);
                        g.DrawLine(p, 10f, 3f, 14f, 3f); g.DrawLine(p, 12f, 3f, 12f, 6.5f); break;
                    case "gear":
                        g.DrawEllipse(p, 8.5f, 8.5f, 7f, 7f);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(p, (float)(12 + Math.Cos(a) * 6.3), (float)(12 + Math.Sin(a) * 6.3),
                                (float)(12 + Math.Cos(a) * 9.3), (float)(12 + Math.Sin(a) * 9.3));
                        }
                        g.DrawEllipse(p, 5.7f, 5.7f, 12.6f, 12.6f); break;
                    case "trash":
                        g.DrawLine(p, 4f, 7f, 20f, 7f); g.DrawLines(p, P(6, 7, 7, 20, 17, 20, 18, 7));
                        g.DrawLines(p, P(9, 7, 9, 4, 15, 4, 15, 7));
                        g.DrawLine(p, 10f, 11f, 10f, 16.5f); g.DrawLine(p, 14f, 11f, 14f, 16.5f); break;
                    case "plus":
                        g.DrawLine(p, 12f, 5f, 12f, 19f); g.DrawLine(p, 5f, 12f, 19f, 12f); break;
                    case "folder":
                        g.DrawPolygon(p, P(3, 5, 9, 5, 11, 7, 21, 7, 21, 19, 3, 19)); break;
                    case "file":
                        g.DrawPolygon(p, P(6, 3, 14, 3, 19, 8, 19, 21, 6, 21)); g.DrawLines(p, P(14, 3, 14, 8, 19, 8)); break;
                    case "image":
                        g.DrawRectangle(p, 3.5f, 5f, 17f, 14f); g.DrawLines(p, P(4, 17, 9.5f, 11.5f, 13, 15, 15.5f, 12.5f, 20, 17));
                        g.DrawEllipse(p, 14.5f, 7.5f, 2.5f, 2.5f); break;
                    case "scan":
                        g.DrawLine(p, 3f, 12f, 21f, 12f);
                        g.DrawLines(p, P(6, 8, 6, 4, 18, 4, 18, 8)); g.DrawLines(p, P(6, 16, 6, 20, 18, 20, 18, 16)); break;
                    case "eye":
                        g.DrawEllipse(p, 9f, 9f, 6f, 6f);
                        g.DrawBezier(p, 2f, 12f, 6f, 5f, 18f, 5f, 22f, 12f);
                        g.DrawBezier(p, 2f, 12f, 6f, 19f, 18f, 19f, 22f, 12f); break;
                    case "chevl":
                        g.DrawLines(p, P(15, 5, 8, 12, 15, 19)); break;
                    case "chevr":
                        g.DrawLines(p, P(9, 5, 16, 12, 9, 19)); break;
                    case "check":
                        g.DrawLines(p, P(5, 12.5f, 10, 17.5f, 19.5f, 7)); break;
                    case "stop":
                        g.DrawRectangle(p, 7f, 7f, 10f, 10f); break;
                    case "min":
                        g.DrawLine(p, 6f, 12f, 18f, 12f); break;
                    case "max":
                        g.DrawRectangle(p, 6f, 6f, 12f, 12f); break;
                    case "close":
                        g.DrawLine(p, 6f, 6f, 18f, 18f); g.DrawLine(p, 18f, 6f, 6f, 18f); break;
                }
            }
            g.Restore(state);
        }
    }
}
