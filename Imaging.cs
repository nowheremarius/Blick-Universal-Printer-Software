using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Blick
{
    class OcrWord
    {
        public RectangleF Box; // в пикселях полного изображения
        public string Text;
    }

    class Page : IDisposable
    {
        public Bitmap Full, Preview, Thumb;
        public int Dpi;
        public string SavedPath;
        public List<OcrWord> Words;
        public Page(Bitmap full, int dpi) { Full = full; Dpi = dpi; Rebuild(); }
        public void Rebuild()
        {
            if (Preview != null) Preview.Dispose();
            if (Thumb != null) Thumb.Dispose();
            Preview = Img.Scaled(Full, 1400);
            Thumb = Img.Scaled(Full, 160);
        }
        public void Rotate() { Full.RotateFlip(RotateFlipType.Rotate90FlipNone); Words = null; Rebuild(); }
        public void Crop(RectangleF n)
        {
            Rectangle r = Rectangle.Round(new RectangleF(n.X * Full.Width, n.Y * Full.Height, n.Width * Full.Width, n.Height * Full.Height));
            r.Intersect(new Rectangle(0, 0, Full.Width, Full.Height));
            if (r.Width < 4 || r.Height < 4) return;
            Bitmap c = Full.Clone(r, PixelFormat.Format24bppRgb);
            Full.Dispose();
            Full = c;
            Words = null;
            Rebuild();
        }
        public void Dispose()
        {
            if (Full != null) Full.Dispose();
            if (Preview != null) Preview.Dispose();
            if (Thumb != null) Thumb.Dispose();
        }
    }

    static class Img
    {
        public static Bitmap Scaled(Bitmap src, int maxSide)
        {
            float k = Math.Min(1f, (float)maxSide / Math.Max(src.Width, src.Height));
            int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
            }
            return b;
        }

        public static Bitmap FromBmpBytes(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream(data))
            using (Bitmap raw = new Bitmap(ms))
            {
                Bitmap copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(copy))
                {
                    g.Clear(Color.White);
                    g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                }
                return copy;
            }
        }

        static ImageCodecInfo JpegCodec()
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.MimeType == "image/jpeg") return c;
            return null;
        }

        public static byte[] Jpeg(Bitmap b, long quality)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                EncoderParameters ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                b.Save(ms, JpegCodec(), ep);
                return ms.ToArray();
            }
        }

        public static void SaveJpeg(Bitmap b, string path, long quality, int dpi)
        {
            b.SetResolution(dpi, dpi);
            File.WriteAllBytes(path, Jpeg(b, quality));
        }

        public static byte[] Pixels(Bitmap b, out int stride)
        {
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                stride = d.Stride;
                byte[] px = new byte[stride * b.Height];
                Marshal.Copy(d.Scan0, px, 0, px.Length);
                return px;
            }
            finally { b.UnlockBits(d); }
        }

        public static int Pct(int[] hist, int n, double frac)
        {
            long target = (long)(n * frac), acc = 0;
            for (int i = 0; i < hist.Length; i++) { acc += hist[i]; if (acc >= target) return i; }
            return hist.Length - 1;
        }
    }

    // PDF (с невидимым текстовым слоем)
    static class Pdf
    {
        static void W(Stream s, string text)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            s.Write(b, 0, b.Length);
        }
        static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        static string ToUnicodeCMap()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
            sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
            sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
            sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
            List<string> ranges = new List<string>();
            for (int hi = 0; hi < 256; hi++)
            {
                if (hi >= 0xD8 && hi <= 0xDF) continue;
                string h = hi.ToString("X2");
                ranges.Add("<" + h + "00> <" + h + "FF> <" + h + "00>");
            }
            for (int i = 0; i < ranges.Count; i += 100)
            {
                int n = Math.Min(100, ranges.Count - i);
                sb.Append(n).Append(" beginbfrange\n");
                for (int k = 0; k < n; k++) sb.Append(ranges[i + k]).Append('\n');
                sb.Append("endbfrange\n");
            }
            sb.Append("endcmap\nCMapName currentdict /CIDInit /ProcSet findresource exch defineresource pop\nend\nend\n");
            return sb.ToString();
        }

        static string TextLayer(Page p, double wpt, double hpt, int dpi)
        {
            if (p.Words == null || p.Words.Count == 0) return "";
            double k = 72.0 / dpi;
            StringBuilder sb = new StringBuilder();
            sb.Append("BT 3 Tr\n");
            foreach (OcrWord w in p.Words)
            {
                if (string.IsNullOrEmpty(w.Text)) continue;
                double bw = w.Box.Width * k, bh = w.Box.Height * k;
                if (bw <= 0.1 || bh <= 0.1) continue;
                double fs = bh;
                double natural = w.Text.Length * 0.5 * fs;
                double tz = natural > 0 ? bw / natural * 100.0 : 100.0;
                double x = w.Box.X * k, y = hpt - (w.Box.Bottom * k) + bh * 0.2;
                sb.Append("/F1 ").Append(N(fs)).Append(" Tf ").Append(N(tz)).Append(" Tz 1 0 0 1 ")
                  .Append(N(x)).Append(' ').Append(N(y)).Append(" Tm <");
                foreach (char c in w.Text) sb.Append(((int)c).ToString("X4"));
                sb.Append("> Tj\n");
            }
            sb.Append("ET\n");
            return sb.ToString();
        }

        public static void Write(string path, IList<Page> pages)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteTemporary(tmp, pages);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        static void WriteTemporary(string tmp, IList<Page> pages)
        {
            int n = pages.Count;
            bool anyText = false;
            foreach (Page p in pages) if (p.Words != null && p.Words.Count > 0) anyText = true;
            // 1 каталог, 2 страницы, 3..6 шрифт (если есть текст), далее по 3 объекта на страницу
            int fontBase = 3;
            int pageBase = anyText ? 7 : 3;
            int total = pageBase - 1 + 3 * n;
            long[] offs = new long[total];
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                W(fs, "%PDF-1.4\n");
                fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);
                offs[0] = fs.Position;
                W(fs, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                StringBuilder kids = new StringBuilder();
                for (int i = 0; i < n; i++) kids.Append((pageBase + 3 * i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
                offs[1] = fs.Position;
                W(fs, "2 0 obj\n<< /Type /Pages /Kids [" + kids + "] /Count " + n + " >>\nendobj\n");

                if (anyText)
                {
                    int f0 = fontBase, f1 = fontBase + 1, f2 = fontBase + 2, f3 = fontBase + 3;
                    offs[f0 - 1] = fs.Position;
                    W(fs, f0 + " 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /BlickOCR /Encoding /Identity-H /DescendantFonts [" + f1 + " 0 R] /ToUnicode " + f3 + " 0 R >>\nendobj\n");
                    offs[f1 - 1] = fs.Position;
                    W(fs, f1 + " 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /BlickOCR /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor " + f2 + " 0 R /DW 500 /CIDToGIDMap /Identity >>\nendobj\n");
                    offs[f2 - 1] = fs.Position;
                    W(fs, f2 + " 0 obj\n<< /Type /FontDescriptor /FontName /BlickOCR /Flags 4 /FontBBox [0 -200 1000 800] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>\nendobj\n");
                    string cmap = ToUnicodeCMap();
                    offs[f3 - 1] = fs.Position;
                    W(fs, f3 + " 0 obj\n<< /Length " + cmap.Length + " >>\nstream\n" + cmap + "endstream\nendobj\n");
                }

                for (int i = 0; i < n; i++)
                {
                    Page p = pages[i];
                    int pn = pageBase + 3 * i, cn = pn + 1, im = pn + 2;
                    int dpi = p.Dpi > 0 ? p.Dpi : 300;
                    double wpt = p.Full.Width * 72.0 / dpi, hpt = p.Full.Height * 72.0 / dpi;
                    string ws = N(wpt), hs = N(hpt);
                    bool hasText = p.Words != null && p.Words.Count > 0;
                    offs[pn - 1] = fs.Position;
                    W(fs, pn + " 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + ws + " " + hs +
                        "] /Resources << /XObject << /Im0 " + im + " 0 R >>" + (hasText ? " /Font << /F1 " + fontBase + " 0 R >>" : "") +
                        " /ProcSet [/PDF /ImageC /Text] >> /Contents " + cn + " 0 R >>\nendobj\n");
                    string content = "q " + ws + " 0 0 " + hs + " 0 0 cm /Im0 Do Q\n" + (hasText ? TextLayer(p, wpt, hpt, dpi) : "");
                    offs[cn - 1] = fs.Position;
                    W(fs, cn + " 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "endstream\nendobj\n");
                    byte[] jpg = Img.Jpeg(p.Full, 85);
                    offs[im - 1] = fs.Position;
                    W(fs, im + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + p.Full.Width + " /Height " + p.Full.Height +
                        " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpg.Length + " >>\nstream\n");
                    fs.Write(jpg, 0, jpg.Length);
                    W(fs, "\nendstream\nendobj\n");
                }
                long xref = fs.Position;
                W(fs, "xref\n0 " + (total + 1) + "\n0000000000 65535 f \n");
                foreach (long o in offs) W(fs, o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
                W(fs, "trailer\n<< /Size " + (total + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            }
        }
    }

    // улучшение документа
    static class Enhance
    {
        public static Bitmap Apply(Bitmap src)
        {
            Bitmap cur = src;
            try
            {
                float a = DetectSkew(cur);
                if (Math.Abs(a) >= 0.3f && Math.Abs(a) <= 5.8f)
                {
                    Bitmap r = Rotate(cur, -a);
                    cur.Dispose();
                    cur = r;
                }
            }
            catch { }
            try { Levels(cur); } catch { }
            return cur;
        }

        static void Levels(Bitmap b)
        {
            Rectangle rect = new Rectangle(0, 0, b.Width, b.Height);
            BitmapData d = b.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int stride = d.Stride;
                int len = stride * b.Height;
                byte[] px = new byte[len];
                Marshal.Copy(d.Scan0, px, 0, len);
                int[] hist = new int[256];
                int n = 0;
                for (int y = 0; y < b.Height; y += 3)
                {
                    int row = y * stride;
                    for (int x = 0; x < b.Width; x += 3)
                    {
                        int i = row + x * 3;
                        hist[(px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8]++;
                        n++;
                    }
                }
                if (n == 0) return;
                int p1 = Img.Pct(hist, n, 0.01), p75 = Img.Pct(hist, n, 0.75);
                if (p75 < 120) return;
                int wp = Math.Max(p75 - 6, 140), bp = Math.Min(p1, 60);
                if (wp - bp < 60) return;
                byte[] lut = new byte[256];
                for (int v = 0; v < 256; v++)
                {
                    int o = (int)Math.Round((v - bp) * 255.0 / (wp - bp));
                    lut[v] = (byte)(o < 0 ? 0 : (o > 255 ? 255 : o));
                }
                int rowBytes = b.Width * 3;
                for (int y = 0; y < b.Height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < rowBytes; x++) px[row + x] = lut[px[row + x]];
                }
                Marshal.Copy(px, 0, d.Scan0, len);
            }
            finally { b.UnlockBits(d); }
        }

        static float DetectSkew(Bitmap full)
        {
            using (Bitmap s = Img.Scaled(full, 1000))
            {
                int w = s.Width, h = s.Height, stride;
                byte[] px = Img.Pixels(s, out stride);
                long sum = 0; int cnt = 0;
                for (int y = 0; y < h; y += 2)
                    for (int x = 0; x < w; x += 2)
                    {
                        int i = y * stride + x * 3;
                        sum += (px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8; cnt++;
                    }
                if (cnt == 0) return 0f;
                int thr = (int)(sum / cnt * 0.6);
                int mx = w / 40, my = h / 40;
                List<int> xs = new List<int>(), ys = new List<int>();
                for (int y = my; y < h - my; y++)
                    for (int x = mx; x < w - mx; x++)
                    {
                        int i = y * stride + x * 3;
                        if (((px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8) < thr) { xs.Add(x); ys.Add(y); }
                    }
                if (xs.Count < 300) return 0f;
                int step = xs.Count / 60000 + 1;
                int n = xs.Count / step;
                int[] ax = new int[n], ay = new int[n];
                for (int i = 0; i < n; i++) { ax[i] = xs[i * step]; ay[i] = ys[i * step]; }

                float best = 0f; double bestScore = -1;
                for (float a = -6f; a <= 6.001f; a += 0.25f)
                {
                    double sc = Score(ax, ay, n, a, w, h);
                    if (sc > bestScore) { bestScore = sc; best = a; }
                }
                float center = best;
                for (float a = center - 0.25f; a <= center + 0.2501f; a += 0.05f)
                {
                    double sc = Score(ax, ay, n, a, w, h);
                    if (sc > bestScore) { bestScore = sc; best = a; }
                }
                return best;
            }
        }

        static double Score(int[] xs, int[] ys, int n, float deg, int w, int h)
        {
            double rad = deg * Math.PI / 180.0, sn = Math.Sin(rad), cs = Math.Cos(rad);
            int off = w + 2;
            int[] bins = new int[h + 2 * w + 8];
            for (int i = 0; i < n; i++)
            {
                int r = (int)(ys[i] * cs - xs[i] * sn) + off;
                if (r >= 0 && r < bins.Length) bins[r]++;
            }
            double s = 0;
            for (int i = 0; i < bins.Length; i++) s += (double)bins[i] * bins[i];
            return s;
        }

        static Bitmap Rotate(Bitmap src, float deg)
        {
            Bitmap dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TranslateTransform(src.Width / 2f, src.Height / 2f);
                g.RotateTransform(deg);
                g.TranslateTransform(-src.Width / 2f, -src.Height / 2f);
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
            return dst;
        }
    }

    // деление стола на отдельные объекты
    static class Splitter
    {
        // Возвращает null, если найден один объект (или ничего)
        public static List<Bitmap> Split(Bitmap full)
        {
            using (Bitmap s = Img.Scaled(full, 600))
            {
                int w = s.Width, h = s.Height, stride;
                byte[] px = Img.Pixels(s, out stride);
                byte[] lum = new byte[w * h];
                int[] hist = new int[256];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * stride + x * 3;
                        int l = (px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8;
                        lum[y * w + x] = (byte)l; hist[l]++;
                    }
                int bg = Img.Pct(hist, w * h, 0.9);
                int thr = bg - 28;
                int bx = Math.Max(2, w / 60), by = Math.Max(2, h / 60);
                bool[] mask = new bool[w * h];
                for (int y = by; y < h - by; y++)
                    for (int x = bx; x < w - bx; x++)
                        if (lum[y * w + x] < thr) mask[y * w + x] = true;

                int r = Math.Max(3, Math.Max(w, h) / 45);
                bool[] dil = Dilate(mask, w, h, r);

                int[] label = new int[w * h];
                List<Rectangle> boxes = new List<Rectangle>();
                Stack<int> st = new Stack<int>();
                int next = 0;
                for (int i = 0; i < w * h; i++)
                {
                    if (!dil[i] || label[i] != 0) continue;
                    next++;
                    int minX = w, minY = h, maxX = 0, maxY = 0;
                    st.Push(i); label[i] = next;
                    while (st.Count > 0)
                    {
                        int c = st.Pop();
                        int cx = c % w, cy = c / w;
                        if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
                        if (cy < minY) minY = cy; if (cy > maxY) maxY = cy;
                        if (cx > 0 && dil[c - 1] && label[c - 1] == 0) { label[c - 1] = next; st.Push(c - 1); }
                        if (cx < w - 1 && dil[c + 1] && label[c + 1] == 0) { label[c + 1] = next; st.Push(c + 1); }
                        if (cy > 0 && dil[c - w] && label[c - w] == 0) { label[c - w] = next; st.Push(c - w); }
                        if (cy < h - 1 && dil[c + w] && label[c + w] == 0) { label[c + w] = next; st.Push(c + w); }
                    }
                    Rectangle box = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
                    if (box.Width > w * 0.05 && box.Height > h * 0.04 && (double)box.Width * box.Height > w * h * 0.012)
                        boxes.Add(box);
                }
                if (boxes.Count < 2) return null;

                float k = (float)full.Width / w;
                int rowBand = Math.Max(1, h / 7);
                boxes.Sort(delegate(Rectangle a, Rectangle b)
                {
                    int ra = a.Y / rowBand, rb = b.Y / rowBand;
                    if (ra != rb) return ra.CompareTo(rb);
                    return a.X.CompareTo(b.X);
                });
                List<Bitmap> result = new List<Bitmap>();
                foreach (Rectangle box in boxes)
                {
                    Rectangle inner = Rectangle.FromLTRB(box.Left + r / 2, box.Top + r / 2, box.Right - r / 2, box.Bottom - r / 2);
                    Rectangle fr = Rectangle.Round(new RectangleF(inner.X * k, inner.Y * k, inner.Width * k, inner.Height * k));
                    fr.Intersect(new Rectangle(0, 0, full.Width, full.Height));
                    if (fr.Width < 16 || fr.Height < 16) continue;
                    result.Add(full.Clone(fr, PixelFormat.Format24bppRgb));
                }
                if (result.Count < 2) { foreach (Bitmap b in result) b.Dispose(); return null; }
                return result;
            }
        }

        static bool[] Dilate(bool[] m, int w, int h, int r)
        {
            bool[] tmp = new bool[w * h], outp = new bool[w * h];
            int[] pre = new int[Math.Max(w, h) + 1];
            for (int y = 0; y < h; y++)
            {
                pre[0] = 0;
                for (int x = 0; x < w; x++) pre[x + 1] = pre[x] + (m[y * w + x] ? 1 : 0);
                for (int x = 0; x < w; x++)
                {
                    int a = Math.Max(0, x - r), b = Math.Min(w, x + r + 1);
                    tmp[y * w + x] = pre[b] - pre[a] > 0;
                }
            }
            for (int x = 0; x < w; x++)
            {
                pre[0] = 0;
                for (int y = 0; y < h; y++) pre[y + 1] = pre[y] + (tmp[y * w + x] ? 1 : 0);
                for (int y = 0; y < h; y++)
                {
                    int a = Math.Max(0, y - r), b = Math.Min(h, y + r + 1);
                    outp[y * w + x] = pre[b] - pre[a] > 0;
                }
            }
            return outp;
        }
    }
}
