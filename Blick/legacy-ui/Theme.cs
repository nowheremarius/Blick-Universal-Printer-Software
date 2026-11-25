using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
                MessageBox.Show("Ошибка в Blick.\n\n" + ex.Message +
                    "\n\nЖурнал ошибок: " + log, "Blick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
            reporting = false;
        }
    }

    static class NativeMethods
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("gdi32.dll")] public static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
    }

    // шрифты
    static class FontLoader
    {
        static readonly PrivateFontCollection pfc = new PrivateFontCollection();
        static readonly Dictionary<string, FontFamily> families = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        public static string Regular = "Segoe UI", Semi = "Segoe UI Semibold", Heavy = "Segoe UI";
        public static bool SemiNeedsBold, HeavyNeedsBold = true;
        public static string Source = "системный Segoe UI";

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

            string[] prefs = new string[] { "Montserrat", "Century Gothic" };
            foreach (string name in prefs)
            {
                bool priv = families.ContainsKey(name);
                if (!priv && !installed.Contains(name)) continue;
                Regular = name;
                Source = priv ? name + " (из папки fonts)" : name;
                if (Has(name + " SemiBold", installed)) { Semi = name + " SemiBold"; SemiNeedsBold = false; }
                else if (Has(name + " Medium", installed)) { Semi = name + " Medium"; SemiNeedsBold = false; }
                else { Semi = name; SemiNeedsBold = true; }
                if (Has(name + " ExtraBold", installed)) { Heavy = name + " ExtraBold"; HeavyNeedsBold = false; }
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

    // тема
    static class Theme
    {
        public static float Dpi = 1f, Ui = 1f;
        public static float Scale { get { return Dpi * Ui; } }
        public static int D(float v) { return (int)Math.Round(v * Scale); }

        public static int Kind; // 0 глубокий чёрный, 1 графит, 2 светлая
        public static bool IsLight { get { return Kind == 2; } }

        public static Color Bg, Panel, Card, Card2, Line, LineSoft, CardLine, Text, Text2, Muted, Dim, HoverBg, Error, Decor, Border2, SwitchOff, Overlay;
        public static readonly Color Paper = Color.FromArgb(0xf6, 0xf6, 0xf9);
        public static Color Accent, AccentHover, AccentInk, SelBg, AccentDisabled;

        public static readonly Color DefaultAccent = Color.FromArgb(0x58, 0x35, 0xf3);
        public static readonly Color[] Presets = new Color[] {
            Color.FromArgb(0x58, 0x35, 0xf3), Color.FromArgb(0x8b, 0x5c, 0xf6), Color.FromArgb(0xff, 0x5c, 0xa8),
            Color.FromArgb(0x3a, 0xd6, 0xc4), Color.FromArgb(0xff, 0xb5, 0x47), Color.FromArgb(0x3b, 0x82, 0xf6) };

        public static Font FontLabel, FontChip, FontBody, FontSmall, FontTitle, FontLogo, FontBtn, FontBig, FontHuge;

        static Theme() { SetPalette(0); SetAccent(DefaultAccent); MakeFonts(); }

        static Color H(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static void SetPalette(int kind)
        {
            Kind = kind < 0 || kind > 2 ? 0 : kind;
            if (Kind == 0)
            {
                Bg = H(0x0e0e12); Panel = H(0x111116); Card = H(0x17171e); Card2 = H(0x131318);
                Line = H(0x2a2a36); LineSoft = H(0x1e1e27); CardLine = H(0x262632);
                Text = H(0xf4f4f8); Text2 = H(0xc8c8d4); Muted = H(0x9a9aac); Dim = H(0x55556a);
                HoverBg = H(0x1e1e28); Error = H(0xff8a8a); Decor = H(0x262634); Border2 = H(0x34344a);
                SwitchOff = H(0x2c2c3a); Overlay = Color.FromArgb(160, 6, 6, 10);
            }
            else if (Kind == 1)
            {
                Bg = H(0x1b1c21); Panel = H(0x202127); Card = H(0x282931); Card2 = H(0x22232a);
                Line = H(0x3a3b47); LineSoft = H(0x2c2d36); CardLine = H(0x363744);
                Text = H(0xf2f2f6); Text2 = H(0xd0d1db); Muted = H(0xa4a5b4); Dim = H(0x64657a);
                HoverBg = H(0x30313b); Error = H(0xff9090); Decor = H(0x34353f); Border2 = H(0x484959);
                SwitchOff = H(0x3c3d4a); Overlay = Color.FromArgb(150, 12, 12, 16);
            }
            else
            {
                Bg = H(0xf3f3f8); Panel = H(0xfbfbfe); Card = H(0xffffff); Card2 = H(0xededf4);
                Line = H(0xd9d9e4); LineSoft = H(0xe4e4ed); CardLine = H(0xdcdce7);
                Text = H(0x15151c); Text2 = H(0x383846); Muted = H(0x666678); Dim = H(0xb2b2c2);
                HoverBg = H(0xeeeef6); Error = H(0xd23c3c); Decor = H(0xd6d6e3); Border2 = H(0xc9c9d8);
                SwitchOff = H(0xd2d2df); Overlay = Color.FromArgb(120, 20, 20, 30);
            }
            if (Accent.A != 0) SetAccent(Accent);
        }

        public static void SetAccent(Color c)
        {
            Accent = Color.FromArgb(255, c.R, c.G, c.B);
            AccentHover = Mix(Accent, Color.White, 0.16f);
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            AccentInk = lum > 0.6 ? Color.FromArgb(0x16, 0x10, 0x20) : Color.White;
            SelBg = Mix(Card, Accent, IsLight ? 0.10f : 0.16f);
            AccentDisabled = Mix(Panel, Accent, 0.4f);
        }

        public static void MakeFonts()
        {
            float u = Ui;
            string r = FontLoader.Regular, s = FontLoader.Semi, h = FontLoader.Heavy;
            FontStyle sb = FontLoader.SemiNeedsBold ? FontStyle.Bold : FontStyle.Regular;
            FontStyle hb = FontLoader.HeavyNeedsBold ? FontStyle.Bold : FontStyle.Regular;
            FontLabel = FontLoader.Make(s, 8.5f * u, sb);
            FontChip = FontLoader.Make(s, 9.5f * u, sb);
            FontBody = FontLoader.Make(r, 9.5f * u, FontStyle.Regular);
            FontSmall = FontLoader.Make(r, 8.5f * u, FontStyle.Regular);
            FontTitle = FontLoader.Make(h, 18f * u, hb);
            FontLogo = FontLoader.Make(h, 13f * u, hb);
            FontBtn = FontLoader.Make(s, 10.5f * u, FontStyle.Bold);
            FontBig = FontLoader.Make(h, 12.5f * u, hb);
            FontHuge = FontLoader.Make(h, 34f * u, hb);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
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
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Color c, float width, RectangleF r, float rad)
        {
            using (GraphicsPath p = Round(r, rad))
            using (Pen pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static void Ring(Graphics g, Color c, float width, float cx, float cy, float dia)
        {
            using (Pen p = new Pen(c, width)) g.DrawEllipse(p, cx - dia / 2f, cy - dia / 2f, dia, dia);
        }

        public static void Dot(Graphics g, Color c, float cx, float cy, float dia)
        {
            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, cx - dia / 2f, cy - dia / 2f, dia, dia);
        }

        public static void TextC(Graphics g, string t, Font f, Rectangle r, Color c)
        {
            TextRenderer.DrawText(g, t, f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        public static Size Measure(Graphics g, string t, Font f)
        {
            return TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
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
        static PointF[] P(params float[] xy)
        {
            PointF[] r = new PointF[xy.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = new PointF(xy[i * 2], xy[i * 2 + 1]);
            return r;
        }

        public static void Draw(Graphics g, string kind, Color color, float x, float y, float size, float stroke)
        {
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
