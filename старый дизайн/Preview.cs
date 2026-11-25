using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Blick
{
    class PreviewPanel : BaseCtl
    {
        public Bitmap Image;
        public int SelectMode;            // 0 — нет, 1 — область сканирования, 2 — обрезка страницы
        public RectangleF Sel = RectangleF.Empty; // в долях изображения (0..1)
        public string Hint;
        public Action SelChanged;
        public Action CropApply;
        public int Countdown;             // >0 — идёт отсчёт до следующего скана
        public int CountdownTotal = 1;
        float zoom = 1f;
        PointF pan = PointF.Empty;
        bool busy;
        float sweep;
        float phase;      // фаза анимации скана на пустом экране, 0..1 по кругу
        float busyFade;   // плавный переход «готов» ⇄ «сканирую», 0..1
        float success = -1f;              // -1 — нет анимации, иначе 0..1
        Bitmap ghost;
        bool dragging, selecting;
        Point dragFrom;
        PointF selAnchor;
        RectangleF imgRect = RectangleF.Empty;
        Rectangle applyRect = Rectangle.Empty, cancelRect = Rectangle.Empty;

        static readonly float[] nodes = new float[] {
            0.08f, 0.15f, 0.055f,  0.26f, 0.06f, 0.025f,  0.20f, 0.42f, 0.035f,  0.06f, 0.72f, 0.07f,  0.30f, 0.93f, 0.03f,
            0.78f, 0.10f, 0.04f,   0.93f, 0.33f, 0.06f,   0.84f, 0.66f, 0.03f,   0.95f, 0.90f, 0.05f,  0.64f, 0.96f, 0.025f };
        static readonly int[] links = new int[] { 0, 1, 0, 2, 2, 3, 3, 4, 5, 6, 6, 7, 7, 8, 8, 9 };

        public bool HasSel { get { return Sel.Width > 0 && Sel.Height > 0; } }
        public void SetBusy(bool b) { busy = b; sweep = 0f; if (b) phase = 0f; Kick(); Invalidate(); }
        public void ResetView() { zoom = 1f; pan = PointF.Empty; Invalidate(); }
        public void ClearSel() { Sel = RectangleF.Empty; selecting = false; Invalidate(); }
        public void ZoomBy(float k)
        {
            zoom = Math.Max(1f, Math.Min(8f, zoom * k));
            if (zoom <= 1.001f) { zoom = 1f; pan = PointF.Empty; }
            Invalidate();
        }
        public void PlaySuccess()
        {
            if (Image == null) return;
            if (ghost != null) ghost.Dispose();
            ghost = new Bitmap(Image);
            success = 0f;
            Kick();
        }
        bool SelAllowed { get { return SelectMode != 0 && Image != null && !busy && zoom <= 1.001f && success < 0f; } }

        protected override bool ExtraStep()
        {
            bool more = false;
            if (busy) { sweep += 0.016f; if (sweep > 1f) sweep = 0f; phase += 0.0075f; if (phase > 1f) phase -= 1f; more = true; }
            float bf = Anim.To(busyFade, busy ? 1f : 0f, 0.12f);
            if (Math.Abs(bf - busyFade) > 0.001f) { busyFade = bf; more = true; }
            else busyFade = busy ? 1f : 0f;
            if (success >= 0f)
            {
                success += 0.022f;
                if (success >= 1f) { success = -1f; if (ghost != null) { ghost.Dispose(); ghost = null; } }
                more = true;
            }
            return more;
        }

        PointF ToNorm(Point p)
        {
            float x = (p.X - imgRect.X) / imgRect.Width, y = (p.Y - imgRect.Y) / imgRect.Height;
            return new PointF(Math.Max(0f, Math.Min(1f, x)), Math.Max(0f, Math.Min(1f, y)));
        }
        RectangleF SelScreen()
        {
            return new RectangleF(imgRect.X + Sel.X * imgRect.Width, imgRect.Y + Sel.Y * imgRect.Height,
                Sel.Width * imgRect.Width, Sel.Height * imgRect.Height);
        }

        void Pill(Graphics g, Rectangle r, string text, bool primary)
        {
            RectangleF rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            if (primary) Theme.FillRound(g, Theme.Accent, rf, r.Height / 2f);
            else { Theme.FillRound(g, Theme.Card, rf, r.Height / 2f); Theme.StrokeRound(g, Theme.Line, 1f, rf, r.Height / 2f); }
            Theme.TextC(g, text, Theme.FontChip, r, primary ? Theme.AccentInk : Theme.Text);
        }

        static void DrawFaded(Graphics g, Bitmap img, RectangleF dst, float alpha)
        {
            if (alpha >= 0.99f) { g.DrawImage(img, dst); return; }
            if (alpha <= 0.01f) return;
            ColorMatrix cm = new ColorMatrix();
            cm.Matrix33 = alpha;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(img, new PointF[] { dst.Location, new PointF(dst.Right, dst.Top), new PointF(dst.Left, dst.Bottom) },
                    new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
            }
        }

        void DrawDecor(Graphics g)
        {
            float m = Math.Min(Width, Height);
            using (Pen dash = new Pen(Theme.Decor, Math.Max(1f, Theme.D(1.5f))))
            {
                dash.DashStyle = DashStyle.Custom;
                dash.DashPattern = new float[] { 2f, 3f };
                for (int i = 0; i < links.Length; i += 2)
                {
                    int a = links[i] * 3, b = links[i + 1] * 3;
                    PointF pa = new PointF(nodes[a] * Width, nodes[a + 1] * Height), pb = new PointF(nodes[b] * Width, nodes[b + 1] * Height);
                    PointF c1 = new PointF(pa.X + (pb.X - pa.X) * 0.5f, pa.Y), c2 = new PointF(pa.X + (pb.X - pa.X) * 0.5f, pb.Y);
                    g.DrawBezier(dash, pa, c1, c2, pb);
                }
            }
            for (int i = 0; i < nodes.Length; i += 3)
            {
                float cx = nodes[i] * Width, cy = nodes[i + 1] * Height, d = nodes[i + 2] * m * 2f;
                Theme.Dot(g, Theme.Card2, cx, cy, d);
                Theme.Ring(g, Theme.Decor, Math.Max(1f, Theme.D(1.5f)), cx, cy, d);
                if (i == 6 || i == 21) Theme.Dot(g, Color.FromArgb(150, Theme.Accent), cx, cy, d * 0.35f);
            }
        }

        void DrawEmpty(Graphics g, float cx, float cy, float aw, float ah)
        {
            float w = Math.Min(aw * 0.62f, Theme.D(330));
            float h = w * 0.42f;
            float top = cy - h / 2f - Theme.D(40);
            RectangleF body = new RectangleF(cx - w / 2f, top, w, h);
            float pw = w * 0.5f, ph = w * 0.42f;
            RectangleF sheet = new RectangleF(cx - pw / 2f, body.Y - ph * 0.62f, pw, ph);
            Theme.FillRound(g, Theme.Paper, sheet, Theme.D(5));

            float bf = busyFade;
            float visBottom = body.Y;                      // ниже лист закрыт крышкой
            float beamY = visBottom, linesA = 1f, beamA = 0f;
            if (bf > 0f)
            {
                float t = Math.Min(1f, phase / 0.72f);
                t = t * t * (3f - 2f * t);                 // плавный разгон и торможение
                beamY = sheet.Y + (visBottom - sheet.Y) * t;
                beamA = phase < 0.72f ? 1f : Math.Max(0f, 1f - (phase - 0.72f) / 0.12f);
                linesA = phase < 0.86f ? 1f : Math.Max(0f, 1f - (phase - 0.86f) / 0.14f);
                beamA *= bf;
                // пока переходим из «готов» в «сканирую», строки не пропадают рывком
                beamY = visBottom + (beamY - visBottom) * bf;
                linesA = 1f + (linesA - 1f) * bf;
            }
            GraphicsState gsLines = g.Save();
            g.SetClip(new RectangleF(sheet.X, sheet.Y, sheet.Width, Math.Max(0f, beamY - sheet.Y)), CombineMode.Intersect);
            using (SolidBrush lb = new SolidBrush(Color.FromArgb((int)(255 * linesA), 0xd6, 0xd6, 0xe2)))
            {
                g.FillRectangle(lb, sheet.X + pw * 0.12f, sheet.Y + ph * 0.14f, pw * 0.5f, Theme.D(5));
                g.FillRectangle(lb, sheet.X + pw * 0.12f, sheet.Y + ph * 0.28f, pw * 0.76f, Theme.D(4));
                g.FillRectangle(lb, sheet.X + pw * 0.12f, sheet.Y + ph * 0.38f, pw * 0.66f, Theme.D(4));
            }
            g.Restore(gsLines);
            if (beamA > 0.01f)
            {
                GraphicsState gsBeam = g.Save();
                g.SetClip(new RectangleF(sheet.X - Theme.D(8), sheet.Y, sheet.Width + Theme.D(16), visBottom - sheet.Y), CombineMode.Intersect);
                float gh = Theme.D(22);
                RectangleF tail = new RectangleF(sheet.X, beamY - gh, sheet.Width, gh);
                using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(tail.X, tail.Y - 1, tail.Width, tail.Height + 2),
                    Color.FromArgb(0, Theme.Accent), Color.FromArgb((int)(110 * beamA), Theme.Accent), LinearGradientMode.Vertical))
                    g.FillRectangle(lg, tail);
                Theme.FillRound(g, Color.FromArgb((int)(255 * beamA), Theme.Accent),
                    new RectangleF(sheet.X - Theme.D(6), beamY - Theme.D(1.5f), sheet.Width + Theme.D(12), Theme.D(3)), Theme.D(1.5f));
                g.Restore(gsBeam);
            }
            Theme.FillRound(g, Theme.Card, body, Theme.D(18));
            Theme.StrokeRound(g, Theme.CardLine, 1.5f, body, Theme.D(18));
            RectangleF lid = new RectangleF(body.X, body.Y, body.Width, h * 0.38f);
            Theme.FillRound(g, Theme.Mix(Theme.Card, Theme.HoverBg, 0.8f), lid, Theme.D(18));
            Theme.StrokeRound(g, Theme.CardLine, 1.5f, lid, Theme.D(18));
            float gy = body.Y + h * 0.62f;
            RectangleF glow = new RectangleF(body.X + w * 0.1f, gy - Theme.D(10), w * 0.8f, Theme.D(20));
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(glow.X, glow.Y - 1, glow.Width, glow.Height + 2),
                Color.FromArgb(0, Theme.Accent), Color.FromArgb(0, Theme.Accent), LinearGradientMode.Vertical))
            {
                ColorBlend cb = new ColorBlend(3);
                float pulse = 0.5f + 0.5f * (float)Math.Sin(phase * Math.PI * 4);
                int ga = (int)(90 + 80 * pulse * bf);
                cb.Colors = new Color[] { Color.FromArgb(0, Theme.Accent), Color.FromArgb(ga, Theme.Accent), Color.FromArgb(0, Theme.Accent) };
                cb.Positions = new float[] { 0f, 0.5f, 1f };
                lg.InterpolationColors = cb;
                g.FillRectangle(lg, glow);
            }
            Theme.FillRound(g, Theme.Accent, new RectangleF(glow.X, gy - Theme.D(1.5f), glow.Width, Theme.D(3)), Theme.D(1.5f));
            Theme.Dot(g, Theme.Accent, body.Right - w * 0.08f, body.Bottom - h * 0.18f, Theme.D(7));
            Theme.Ring(g, Theme.Line, 1.5f, body.Right - w * 0.17f, body.Bottom - h * 0.18f, Theme.D(10));

            int ty = (int)body.Bottom + Theme.D(22);
            // подписи меняются через затухание: первая половина перехода гасит старую, вторая проявляет новую
            if (bf >= 0.5f)
            {
                float ta = (bf - 0.5f) * 2f;
                Theme.TextC(g, "Сканирую", Theme.FontBig, new Rectangle(0, ty, Width, Theme.D(28)), Theme.Mix(Theme.Card2, Theme.Text, ta));
                float dy = ty + Theme.D(40) + Theme.D(15), ds = Theme.D(7), dg = Theme.D(14);
                for (int i = 0; i < 3; i++)
                {
                    float k = 0.5f + 0.5f * (float)Math.Sin((phase * 3f - i * 0.18f) * Math.PI * 2);
                    Theme.Dot(g, Color.FromArgb((int)((70 + 185 * k) * ta), Theme.Accent), cx + (i - 1) * dg, dy, ds);
                }
                TextRenderer.DrawText(g, "Не поднимайте крышку", Theme.FontSmall, new Rectangle(0, (int)(dy + Theme.D(14)), Width, Theme.D(22)),
                    Theme.Mix(Theme.Card2, Theme.Muted, ta), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            float ra = 1f - bf * 2f;
            Theme.TextC(g, "Готов к сканированию", Theme.FontBig, new Rectangle(0, ty, Width, Theme.D(28)), Theme.Mix(Theme.Card2, Theme.Text, ra));
            ty += Theme.D(40);
            string[] steps = new string[] { "Положите лист", "Просмотр", "Сканировать" };
            int[] widths = new int[3];
            int total = 0, gap = Theme.D(10), nd = Theme.D(20);
            for (int i = 0; i < 3; i++) { widths[i] = Theme.Measure(g, steps[i], Theme.FontSmall).Width + nd + Theme.D(26); total += widths[i]; }
            total += gap * 2;
            float x = cx - total / 2f;
            for (int i = 0; i < 3; i++)
            {
                RectangleF chip = new RectangleF(x, ty, widths[i], Theme.D(30));
                Theme.FillRound(g, Theme.Mix(Theme.Card2, Theme.Card, ra), chip, chip.Height / 2f);
                Theme.StrokeRound(g, Theme.Mix(Theme.Card2, Theme.Line, ra), 1f, chip, chip.Height / 2f);
                float ncx = chip.X + Theme.D(6) + nd / 2f, ncy = chip.Y + chip.Height / 2f;
                Theme.Dot(g, Theme.Mix(Theme.Card2, Theme.Accent, ra), ncx, ncy, nd);
                Theme.TextC(g, (i + 1).ToString(), Theme.FontSmall, Rectangle.Round(new RectangleF(ncx - nd / 2f, ncy - nd / 2f, nd, nd)), Theme.Mix(Theme.Card2, Theme.AccentInk, ra));
                TextRenderer.DrawText(g, steps[i], Theme.FontSmall, new Rectangle((int)(chip.X + nd + Theme.D(12)), (int)chip.Y, widths[i] - nd - Theme.D(14), (int)chip.Height),
                    Theme.Mix(Theme.Card2, Theme.Text2, ra), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += widths[i] + gap;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            applyRect = Rectangle.Empty; cancelRect = Rectangle.Empty;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (GraphicsPath clip = Theme.Round(outer, Theme.D(22)))
            {
                using (SolidBrush b = new SolidBrush(Theme.Card2)) g.FillPath(b, clip);
                g.SetClip(clip);

                float cx = Width / 2f, cy = Height / 2f, m = Math.Min(Width, Height);
                Theme.Dot(g, Color.FromArgb(Theme.IsLight ? 22 : 30, Theme.Accent), cx, cy, m * 0.62f);
                DrawDecor(g);

                float margin = Theme.D(48);
                float aw = Width - margin * 2, ah = Height - margin * 2;
                RectangleF page = RectangleF.Empty;
                bool hasSel = false;
                if (Image != null && aw > 10 && ah > 10)
                {
                    float fit = Math.Min(aw / Image.Width, ah / Image.Height);
                    float s = fit * zoom;
                    float w = Image.Width * s, h = Image.Height * s;
                    page = new RectangleF(cx - w / 2f + pan.X, cy - h / 2f + pan.Y, w, h);
                    imgRect = page;
                    float alpha = success < 0f ? 1f : Math.Max(0f, Math.Min(1f, (success - 0.55f) / 0.35f));
                    if (alpha > 0.5f) Shadow(g, page);
                    g.InterpolationMode = busy || dragging || selecting || success >= 0f ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
                    DrawFaded(g, Image, page, alpha);
                    hasSel = HasSel && zoom <= 1.001f && success < 0f;
                }
                else
                {
                    imgRect = RectangleF.Empty;
                    DrawEmpty(g, cx, cy, aw, ah);
                }

                if (Image != null && Hint != null && !hasSel && !busy && Countdown <= 0 && success < 0f)
                    TextRenderer.DrawText(g, Hint, Theme.FontBody, new Rectangle(0, Height - Theme.D(38), Width, Theme.D(24)),
                        Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                if (Image != null && !busy && zoom <= 1.001f && !hasSel && success < 0f)
                {
                    RectangleF o = page; o.Inflate(Theme.D(8), Theme.D(8));
                    using (Pen p = new Pen(Theme.Accent, Theme.D(2))) { p.DashStyle = DashStyle.Dash; g.DrawRectangle(p, o.X, o.Y, o.Width, o.Height); }
                }

                if (hasSel) DrawSelection(g, page);

                if (busy && Image != null)
                {
                    float topY = Math.Max(page.Y, 0), bottom = Math.Min(page.Bottom, Height);
                    float y = topY + (bottom - topY) * sweep;
                    float lx = Math.Max(page.X - Theme.D(20), 0), lw = Math.Min(page.Width + Theme.D(40), Width);
                    float gh = Theme.D(28);
                    using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(lx, y - gh, lw, gh + 1),
                        Color.FromArgb(0, Theme.Accent), Color.FromArgb(90, Theme.Accent), LinearGradientMode.Vertical))
                        g.FillRectangle(lg, lx, y - gh, lw, gh);
                    using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, lx, y, lw, Theme.D(3));
                }

                if (success >= 0f && ghost != null) DrawSuccess(g, page);
                if (Countdown > 0 && !busy) DrawCountdown(g, cx, cy);

                g.ResetClip();
                using (Pen p = new Pen(Theme.CardLine)) g.DrawPath(p, clip);
            }
        }

        void DrawSelection(Graphics g, RectangleF page)
        {
            RectangleF s = SelScreen();
            using (Region rg = new Region(page))
            {
                rg.Exclude(s);
                using (SolidBrush b = new SolidBrush(Theme.Overlay)) g.FillRegion(b, rg);
            }
            using (Pen p = new Pen(Theme.Accent, Theme.D(2))) g.DrawRectangle(p, s.X, s.Y, s.Width, s.Height);
            float hs = Theme.D(8);
            PointF[] corners = new PointF[] { new PointF(s.Left, s.Top), new PointF(s.Right, s.Top), new PointF(s.Left, s.Bottom), new PointF(s.Right, s.Bottom) };
            using (SolidBrush b = new SolidBrush(Theme.Accent))
                foreach (PointF c in corners) g.FillRectangle(b, c.X - hs / 2f, c.Y - hs / 2f, hs, hs);

            if (SelectMode == 1)
            {
                string t = "Область сканирования";
                Size ts = Theme.Measure(g, t, Theme.FontSmall);
                RectangleF lr = new RectangleF(s.X, s.Y - Theme.D(28), ts.Width + Theme.D(20), Theme.D(22));
                if (lr.Y < Theme.D(6)) lr.Y = s.Y + Theme.D(6);
                Theme.FillRound(g, Theme.Accent, lr, lr.Height / 2f);
                Theme.TextC(g, t, Theme.FontSmall, Rectangle.Round(lr), Theme.AccentInk);
            }
            else if (SelectMode == 2 && !selecting)
            {
                int bh = Theme.D(34), bw1 = Theme.D(108), bw2 = Theme.D(92), gap = Theme.D(8);
                int by = (int)s.Bottom + Theme.D(10);
                if (by + bh > Height - Theme.D(10)) by = (int)s.Bottom - bh - Theme.D(10);
                int bx = (int)s.Right - bw1;
                if (bx - gap - bw2 < Theme.D(10)) bx = Theme.D(10) + bw2 + gap;
                if (bx + bw1 > Width - Theme.D(10)) bx = Width - Theme.D(10) - bw1;
                applyRect = new Rectangle(bx, by, bw1, bh);
                cancelRect = new Rectangle(bx - gap - bw2, by, bw2, bh);
                Pill(g, cancelRect, "Отмена", false);
                Pill(g, applyRect, "Обрезать", true);
            }
        }

        void DrawSuccess(Graphics g, RectangleF page)
        {
            float t = success;
            float a = Math.Min(1f, t / 0.55f);
            float ease = 1f - (1f - a) * (1f - a) * (1f - a);
            RectangleF from = page.IsEmpty ? new RectangleF(Width * 0.35f, Height * 0.2f, Width * 0.3f, Height * 0.6f) : page;
            PointF target = new PointF(Width * 0.18f, Height + Theme.D(30));
            float sc = 1f - 0.85f * ease;
            float w = from.Width * sc, h = from.Height * sc;
            float fx = from.X + from.Width / 2f, fy = from.Y + from.Height / 2f;
            float x = fx + (target.X - fx) * ease - w / 2f, y = fy + (target.Y - fy) * ease - h / 2f;
            if (a < 1f) DrawFaded(g, ghost, new RectangleF(x, y, w, h), 1f - a * 0.6f);
            if (t > 0.35f)
            {
                float b = Math.Min(1f, (t - 0.35f) / 0.3f);
                float fade = t > 0.75f ? 1f - (t - 0.75f) / 0.25f : 1f;
                float pop = b < 1f ? b * (1.15f - 0.15f * b) : 1f;
                float d = Theme.D(86) * pop;
                float ccx = Width / 2f, ccy = Height / 2f;
                Theme.Dot(g, Color.FromArgb((int)(60 * fade), Theme.Accent), ccx, ccy, d * 1.6f);
                Theme.Dot(g, Color.FromArgb((int)(255 * fade), Theme.Accent), ccx, ccy, d);
                Icons.Draw(g, "check", Color.FromArgb((int)(255 * fade), Theme.AccentInk), ccx - d * 0.3f, ccy - d * 0.3f, d * 0.6f, 2.6f);
            }
        }

        void DrawCountdown(Graphics g, float cx, float cy)
        {
            float d = Theme.D(150);
            Theme.Dot(g, Color.FromArgb(230, Theme.Card), cx, cy, d + Theme.D(30));
            Theme.Ring(g, Theme.Line, Theme.D(6), cx, cy, d);
            float frac = (float)Countdown / Math.Max(1, CountdownTotal);
            using (Pen p = new Pen(Theme.Accent, Theme.D(6)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, cx - d / 2f, cy - d / 2f, d, d, -90f, 360f * frac);
            }
            Theme.TextC(g, Countdown.ToString(), Theme.FontHuge, Rectangle.Round(new RectangleF(cx - d / 2f, cy - d / 2f - Theme.D(8), d, d)), Theme.Text);
            Theme.TextC(g, "до следующего скана", Theme.FontSmall, Rectangle.Round(new RectangleF(cx - d / 2f, cy + Theme.D(22), d, Theme.D(20))), Theme.Muted);
            Theme.TextC(g, "Поменяйте лист · Esc — остановить", Theme.FontBody, new Rectangle(0, (int)(cy + d / 2f + Theme.D(30)), Width, Theme.D(24)), Theme.Text2);
        }

        static void Shadow(Graphics g, RectangleF r)
        {
            for (int k = 1; k <= 6; k++)
            {
                RectangleF s = r; s.Inflate(k * 2.5f, k * 2.5f); s.Offset(0, k * 3f);
                Theme.FillRound(g, Color.FromArgb(Theme.IsLight ? 8 : 14, 0, 0, 0), s, 8 + k * 2);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            if (e.Button != MouseButtons.Left) return;
            if (applyRect.Contains(e.Location)) { if (CropApply != null) CropApply(); return; }
            if (cancelRect.Contains(e.Location)) { ClearSel(); if (SelChanged != null) SelChanged(); return; }
            if (zoom > 1.001f) { dragging = true; dragFrom = e.Location; Cursor = Cursors.SizeAll; return; }
            if (SelAllowed && imgRect.Contains(e.Location))
            {
                selecting = true;
                selAnchor = ToNorm(e.Location);
                Sel = new RectangleF(selAnchor, SizeF.Empty);
                Invalidate();
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                pan = new PointF(pan.X + e.X - dragFrom.X, pan.Y + e.Y - dragFrom.Y);
                dragFrom = e.Location; Invalidate();
            }
            else if (selecting)
            {
                PointF q = ToNorm(e.Location);
                Sel = RectangleF.FromLTRB(Math.Min(q.X, selAnchor.X), Math.Min(q.Y, selAnchor.Y), Math.Max(q.X, selAnchor.X), Math.Max(q.Y, selAnchor.Y));
                Invalidate();
            }
            else if (applyRect.Contains(e.Location) || cancelRect.Contains(e.Location)) Cursor = Cursors.Hand;
            else if (zoom > 1.001f) Cursor = Cursors.Hand;
            else if (SelAllowed && imgRect.Contains(e.Location)) Cursor = Cursors.Cross;
            else Cursor = Cursors.Default;
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            if (selecting)
            {
                selecting = false;
                if (Sel.Width < 0.02f || Sel.Height < 0.02f) Sel = RectangleF.Empty;
                if (SelChanged != null) SelChanged();
            }
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ZoomBy(e.Delta > 0 ? 1.2f : 1f / 1.2f); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && ghost != null) { ghost.Dispose(); ghost = null; }
            base.Dispose(disposing);
        }
    }

    // полоса страниц
    class ThumbStrip : BaseCtl
    {
        public List<Page> Pages = new List<Page>();
        public int Selected = -1;
        public bool AllowReorder;
        public string Note;
        public Action<int> PageClicked;
        public Action<int> PageDeleted;
        public Action<int, int> PageMoved;
        public Action AddClicked;
        readonly List<KeyValuePair<Rectangle, int>> thumbs = new List<KeyValuePair<Rectangle, int>>();
        readonly List<KeyValuePair<Rectangle, int>> dels = new List<KeyValuePair<Rectangle, int>>();
        Rectangle addRect;
        int hoverThumb = -1, hoverDel = -1;
        bool hoverAdd;
        int downIdx = -1, downDel = -1, dropAt = -1;
        bool downAdd, dragMove;
        Point downPt;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            thumbs.Clear(); dels.Clear();
            string label = Note ?? (Pages.Count == 0 ? "Страниц пока нет" : "Страниц в документе: " + Pages.Count);
            Size ls = Theme.Measure(g, label, Theme.FontBody);
            TextRenderer.DrawText(g, label, Theme.FontBody, new Point(0, (Height - ls.Height) / 2), Note != null ? Theme.Accent : Theme.Muted, TextFormatFlags.NoPadding);
            int tw = Theme.D(44), th = Theme.D(56), gap = Theme.D(12);
            int y = (Height - th) / 2;
            int x = ls.Width + gap + Theme.D(4);
            int avail = Width - x - (tw + gap);
            int maxVis = Math.Max(1, avail / (tw + gap));
            int start = Math.Max(0, Pages.Count - maxVis);
            if (start > 0)
            {
                start = Math.Max(0, Pages.Count - (maxVis - 1));
                RectangleF mr = new RectangleF(x + 0.5f, y + 0.5f, tw, th);
                Theme.FillRound(g, Theme.Card, mr, Theme.D(8));
                Theme.TextC(g, "+" + start, Theme.FontChip, Rectangle.Round(mr), Theme.Muted);
                x += tw + gap;
            }
            for (int i = start; i < Pages.Count; i++)
            {
                Rectangle r = new Rectangle(x, y, tw, th);
                Theme.FillRound(g, Theme.Paper, r, Theme.D(8));
                Bitmap t = Pages[i].Thumb;
                if (t != null)
                {
                    using (GraphicsPath cp = Theme.Round(r, Theme.D(8)))
                    {
                        Region old = g.Clip;
                        g.SetClip(cp);
                        float s = Math.Min((float)tw / t.Width, (float)th / t.Height);
                        float w = t.Width * s, h = t.Height * s;
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(t, r.X + (tw - w) / 2f, r.Y + (th - h) / 2f, w, h);
                        g.Clip = old;
                    }
                }
                if (Pages[i].SavedPath == null) Theme.StrokeRound(g, Theme.Muted, 1f, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.D(8));
                if (dragMove && i == downIdx) Theme.FillRound(g, Color.FromArgb(160, ParentBg), r, Theme.D(8));
                if (i == Selected)
                {
                    RectangleF o = r; o.Inflate(Theme.D(3), Theme.D(3));
                    Theme.StrokeRound(g, Theme.Accent, Theme.D(2), o, Theme.D(10));
                }
                if (i == hoverThumb && Enabled && !dragMove)
                {
                    float d = Theme.D(18);
                    float bcx = r.Right - Theme.D(10), bcy = r.Y + Theme.D(10);
                    Theme.Dot(g, hoverDel == i ? Theme.Error : Theme.Card, bcx, bcy, d);
                    Theme.Ring(g, Theme.Line, 1f, bcx, bcy, d);
                    float ip = Theme.D(12);
                    Icons.Draw(g, "close", hoverDel == i ? Color.White : Theme.Text2, bcx - ip / 2f, bcy - ip / 2f, ip, 3f);
                    dels.Add(new KeyValuePair<Rectangle, int>(Rectangle.Round(new RectangleF(bcx - d / 2f - 2, bcy - d / 2f - 2, d + 4, d + 4)), i));
                }
                thumbs.Add(new KeyValuePair<Rectangle, int>(r, i));
                x += tw + gap;
            }
            if (dragMove && dropAt >= 0 && thumbs.Count > 0)
            {
                int lx = -1;
                foreach (KeyValuePair<Rectangle, int> tk in thumbs) if (tk.Value == dropAt) { lx = tk.Key.X - gap / 2; break; }
                if (lx < 0) { Rectangle last = thumbs[thumbs.Count - 1].Key; lx = last.Right + gap / 2; }
                using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, lx - Theme.D(1.5f), y - Theme.D(2), Theme.D(3), th + Theme.D(4));
            }
            addRect = new Rectangle(x, y, tw, th);
            using (GraphicsPath pp = Theme.Round(new RectangleF(addRect.X + 0.5f, addRect.Y + 0.5f, tw - 1, th - 1), Theme.D(8)))
            using (Pen pen = new Pen(hoverAdd && Enabled ? Theme.Accent : Theme.Border2, 1f))
            {
                pen.DashStyle = DashStyle.Dash; g.DrawPath(pen, pp);
            }
            int ipx = Theme.D(18);
            Icons.Draw(g, "plus", !Enabled ? Theme.Dim : (hoverAdd ? Theme.Accent : Theme.Muted),
                addRect.X + (tw - ipx) / 2f, addRect.Y + (th - ipx) / 2f, ipx, 2f);
        }

        int HitThumb(Point p) { foreach (KeyValuePair<Rectangle, int> h in thumbs) if (h.Key.Contains(p)) return h.Value; return -1; }
        int HitDel(Point p) { foreach (KeyValuePair<Rectangle, int> h in dels) if (h.Key.Contains(p)) return h.Value; return -1; }
        int DropIndex(int px)
        {
            foreach (KeyValuePair<Rectangle, int> h in thumbs) if (px < h.Key.X + h.Key.Width / 2) return h.Value;
            return Pages.Count;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            downPt = e.Location;
            downDel = HitDel(e.Location);
            downIdx = downDel >= 0 ? -1 : HitThumb(e.Location);
            downAdd = addRect.Contains(e.Location);
            dragMove = false; dropAt = -1;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.Left && downIdx >= 0 && AllowReorder && !dragMove && Math.Abs(e.X - downPt.X) > Theme.D(6))
                dragMove = true;
            if (dragMove) { dropAt = DropIndex(e.X); Cursor = Cursors.SizeWE; Invalidate(); return; }
            int ht = HitThumb(e.Location), hd = HitDel(e.Location);
            bool ha = addRect.Contains(e.Location);
            Cursor = ht >= 0 || hd >= 0 || ha ? Cursors.Hand : Cursors.Default;
            if (ht != hoverThumb || hd != hoverDel || ha != hoverAdd) { hoverThumb = ht; hoverDel = hd; hoverAdd = ha; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (dragMove)
            {
                int to = dropAt;
                if (to > downIdx) to--;
                if (to >= 0 && to != downIdx && PageMoved != null) PageMoved(downIdx, to);
            }
            else if (downDel >= 0 && HitDel(e.Location) == downDel) { if (PageDeleted != null) PageDeleted(downDel); }
            else if (downIdx >= 0 && HitThumb(e.Location) == downIdx) { if (PageClicked != null) PageClicked(downIdx); }
            else if (downAdd && addRect.Contains(e.Location)) { if (AddClicked != null) AddClicked(); }
            dragMove = false; downIdx = -1; downDel = -1; downAdd = false; dropAt = -1;
            Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            if (!dragMove) { hoverThumb = -1; hoverDel = -1; hoverAdd = false; }
            base.OnMouseLeave(e);
        }
    }
}
