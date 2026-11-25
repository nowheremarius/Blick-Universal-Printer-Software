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
        public string InfoA = "—", InfoB = "—";
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

        public PreviewPanel() { TrackHover = false; }

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

        static void Pill(Graphics g, Rectangle r, string text, bool primary)
        {
            RectangleF rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            float rad = r.Height / 2f;
            if (primary) Theme.FillRound(g, Theme.Accent, rf, rad);
            else
            {
                Theme.FillRound(g, Color.FromArgb(210, 10, 16, 32), rf, rad);
                Theme.Edge(g, rf, rad, Theme.StrokeStrong, Theme.Stroke);
            }
            Theme.TextC(g, text, Theme.FontChip, rf, primary ? Theme.AccentInk : Theme.Text);
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

        void DrawEmpty(Graphics g, float cx, float cy, float aw, float ah)
        {
            float bf = busyFade;
            float w = Math.Min(aw * 0.56f, Theme.D(300));
            float h = w * 0.42f;
            float top = cy - h / 2f - Theme.D(40);
            RectangleF body = new RectangleF(cx - w / 2f, top, w, h);

            float pw = w * 0.5f, ph = w * 0.42f;
            RectangleF sheet = new RectangleF(cx - pw / 2f, body.Y - ph * 0.62f, pw, ph);
            // лист уходит под крышку: ниже верхнего края корпуса его не видно (корпус полупрозрачный)
            GraphicsState gsSheet = g.Save();
            g.SetClip(new RectangleF(sheet.X - 1, sheet.Y - 1, sheet.Width + 2, body.Y - sheet.Y + 1), CombineMode.Intersect);
            Theme.FillRound(g, Theme.Paper, sheet, Theme.D(5));
            g.Restore(gsSheet);

            float visBottom = body.Y;
            float beamY = visBottom, linesA = 1f, beamA = 0f;
            if (bf > 0f)
            {
                float t = Math.Min(1f, phase / 0.72f);
                t = t * t * (3f - 2f * t);
                beamY = sheet.Y + (visBottom - sheet.Y) * t;
                beamA = phase < 0.72f ? 1f : Math.Max(0f, 1f - (phase - 0.72f) / 0.12f);
                linesA = phase < 0.86f ? 1f : Math.Max(0f, 1f - (phase - 0.86f) / 0.14f);
                beamA *= bf;
                beamY = visBottom + (beamY - visBottom) * bf;
                linesA = 1f + (linesA - 1f) * bf;
            }
            GraphicsState gsLines = g.Save();
            g.SetClip(new RectangleF(sheet.X, sheet.Y, sheet.Width, Math.Max(0f, beamY - sheet.Y)), CombineMode.Intersect);
            using (SolidBrush lb = new SolidBrush(Color.FromArgb((int)(255 * linesA), 0xd4, 0xd4, 0xda)))
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
                    Color.FromArgb(0, Theme.Blue), Color.FromArgb((int)(110 * beamA), Theme.Blue), LinearGradientMode.Vertical))
                    g.FillRectangle(lg, tail);
                Theme.FillRound(g, Color.FromArgb((int)(255 * beamA), 70, 110, 240),
                    new RectangleF(sheet.X - Theme.D(6), beamY - Theme.D(1.5f), sheet.Width + Theme.D(12), Theme.D(3)), Theme.D(1.5f));
                g.Restore(gsBeam);
            }

            float br = Theme.D(16);
            Theme.FillRound(g, Color.FromArgb(225, 10, 15, 30), body, br);
            Theme.Edge(g, body, br, Color.FromArgb(120, 210, 222, 255), Theme.EdgeBottom);
            RectangleF lid = new RectangleF(body.X, body.Y, body.Width, h * 0.38f);
            using (Pen lp = new Pen(Theme.Stroke)) g.DrawLine(lp, lid.X + br * 0.5f, lid.Bottom, lid.Right - br * 0.5f, lid.Bottom);

            float gy = body.Y + h * 0.64f;
            RectangleF glow = new RectangleF(body.X + w * 0.1f, gy - Theme.D(10), w * 0.8f, Theme.D(20));
            float pulse = 0.5f + 0.5f * (float)Math.Sin(phase * Math.PI * 4);
            int ga = (int)(70 + 70 * pulse * bf);
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(glow.X, glow.Y - 1, glow.Width, glow.Height + 2),
                Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
            {
                ColorBlend cb = new ColorBlend(3);
                cb.Colors = new Color[] { Color.FromArgb(0, Theme.Blue), Color.FromArgb(ga, Theme.Blue), Color.FromArgb(0, Theme.Blue) };
                cb.Positions = new float[] { 0f, 0.5f, 1f };
                lg.InterpolationColors = cb;
                g.FillRectangle(lg, glow);
            }
            Theme.FillRound(g, Color.FromArgb(255, 200, 216, 255), new RectangleF(glow.X, gy - Theme.D(1.5f), glow.Width, Theme.D(3)), Theme.D(1.5f));
            Theme.Dot(g, Theme.Blue, body.Right - w * 0.08f, body.Bottom - h * 0.17f, Theme.D(6));
            Theme.Ring(g, Theme.StrokeStrong, 1.5f, body.Right - w * 0.16f, body.Bottom - h * 0.17f, Theme.D(9));

            float ty = body.Bottom + Theme.D(24);
            // подписи меняются через затухание: первая половина перехода гасит старую, вторая проявляет новую
            if (bf >= 0.5f)
            {
                float ta = (bf - 0.5f) * 2f;
                Theme.TextC(g, L.T("Сканирую", "Scanning"), Theme.FontBig, new RectangleF(0, ty, Width, Theme.D(28)), Theme.Fade(Theme.Text, ta));
                float dy = ty + Theme.D(46), ds = Theme.D(6), dg = Theme.D(13);
                for (int i = 0; i < 3; i++)
                {
                    float k = 0.5f + 0.5f * (float)Math.Sin((phase * 3f - i * 0.18f) * Math.PI * 2);
                    Theme.Dot(g, Color.FromArgb((int)((60 + 195 * k) * ta), Theme.Blue), cx + (i - 1) * dg, dy, ds);
                }
                Theme.TextC(g, L.T("Не поднимайте крышку", "Keep the lid closed"), Theme.FontSmall,
                    new RectangleF(0, dy + Theme.D(14), Width, Theme.D(22)), Theme.Fade(Theme.Muted, ta));
                return;
            }
            float ra = 1f - bf * 2f;
            Theme.TextC(g, L.T("Готов к сканированию", "Ready to scan"), Theme.FontBig, new RectangleF(0, ty, Width, Theme.D(28)), Theme.Fade(Theme.Text, ra));
            ty += Theme.D(42);
            string[] steps = L.En
                ? new string[] { "Place the sheet", "Preview", "Scan" }
                : new string[] { "Положите лист", "Просмотр", "Сканировать" };
            float[] widths = new float[3];
            float total = 0, gap = Theme.D(8), nd = Theme.D(18);
            for (int i = 0; i < 3; i++) { widths[i] = Theme.MeasureF(g, steps[i], Theme.FontSmall).Width + nd + Theme.D(26); total += widths[i]; }
            total += gap * 2;
            float x = cx - total / 2f;
            for (int i = 0; i < 3; i++)
            {
                RectangleF chip = new RectangleF(x + 0.5f, ty + 0.5f, widths[i] - 1, Theme.D(28) - 1);
                Theme.Edge(g, chip, chip.Height / 2f, Theme.Fade(Theme.StrokeStrong, ra), Theme.Fade(Theme.Stroke, ra * 0.5f));
                float ncx = chip.X + Theme.D(5) + nd / 2f, ncy = chip.Y + chip.Height / 2f;
                Theme.Dot(g, Theme.Fade(Theme.Hover, ra), ncx, ncy, nd);
                Theme.TextC(g, (i + 1).ToString(), Theme.FontSmall, new RectangleF(ncx - nd / 2f, ncy - nd / 2f, nd, nd), Theme.Fade(Theme.Text, ra));
                Theme.Write(g, steps[i], Theme.FontSmall, new RectangleF(chip.X + nd + Theme.D(11), chip.Y, widths[i] - nd - Theme.D(13), chip.Height),
                    Theme.Fade(Theme.Text2, ra), 0, true);
                x += widths[i] + gap;
            }
        }

        protected override void Render(Graphics g)
        {
            applyRect = Rectangle.Empty; cancelRect = Rectangle.Empty;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float rad = Theme.D(20);
            Theme.GlassRect(g, outer, rad, Theme.Raised, Theme.Stroke);
            using (GraphicsPath clip = Theme.Round(outer, rad))
            {
                GraphicsState gs = g.Save();
                g.SetClip(clip, CombineMode.Intersect);

                DrawHeader(g);
                float top = Theme.D(58), side = Theme.D(36), bottomPad = Theme.D(34);
                float aw = Width - side * 2, ah = Height - top - bottomPad;
                float cx = Width / 2f, cy = top + ah / 2f;
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
                    Theme.TextC(g, Hint, Theme.FontSmall, new RectangleF(0, Height - Theme.D(34), Width, Theme.D(22)), Theme.Muted);

                if (Image != null && !busy && zoom <= 1.001f && !hasSel && success < 0f)
                {
                    RectangleF o = page; o.Inflate(Theme.D(7), Theme.D(7));
                    using (GraphicsPath op = Theme.Round(o, Theme.D(6)))
                    using (Pen p = new Pen(Color.FromArgb(110, Theme.Blue), Theme.D(1.5f))) { p.DashStyle = DashStyle.Dash; g.DrawPath(p, op); }
                }

                if (hasSel) DrawSelection(g, page);

                if (busy && Image != null)
                {
                    float topY = Math.Max(page.Y, 0), bottom = Math.Min(page.Bottom, Height);
                    float y = topY + (bottom - topY) * sweep;
                    float lx = Math.Max(page.X - Theme.D(16), 0), lw = Math.Min(page.Width + Theme.D(32), Width);
                    float gh = Theme.D(28);
                    using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(lx, y - gh, lw, gh + 1),
                        Color.FromArgb(0, Theme.Blue), Color.FromArgb(90, Theme.Blue), LinearGradientMode.Vertical))
                        g.FillRectangle(lg, lx, y - gh, lw, gh);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 200, 216, 255))) g.FillRectangle(b, lx, y, lw, Theme.D(2));
                }

                if (success >= 0f && ghost != null) DrawSuccess(g, page);
                if (Countdown > 0 && !busy) DrawCountdown(g, cx, cy);

                g.Restore(gs);
            }
        }

        void DrawHeader(Graphics g)
        {
            float y = Theme.D(14), h = Theme.D(30);
            float x = Width - Theme.D(14);
            string[] chips = new string[] { InfoB, InfoA };
            foreach (string t in chips)
            {
                if (string.IsNullOrEmpty(t) || t == "—") continue;
                SizeF s = Theme.MeasureF(g, t, Theme.FontSmall);
                float w = s.Width + Theme.D(24);
                x -= w;
                RectangleF r = new RectangleF(x + 0.5f, y + Theme.D(2) + 0.5f, w - 1, h - Theme.D(4) - 1);
                Theme.Edge(g, r, r.Height / 2f, Theme.StrokeStrong, Theme.Stroke);
                Theme.TextC(g, t, Theme.FontSmall, r, Theme.Text2);
                x -= Theme.D(8);
            }
            float tx = Theme.D(22);
            Theme.Write(g, L.T("Предпросмотр", "Preview"), Theme.FontBig, new RectangleF(tx, y, Math.Max(0, x - tx - Theme.D(8)), h), Theme.Text, 0, true);
        }

        void DrawSelection(Graphics g, RectangleF page)
        {
            RectangleF s = SelScreen();
            using (Region rg = new Region(page))
            {
                rg.Exclude(s);
                using (SolidBrush b = new SolidBrush(Theme.Overlay)) g.FillRegion(b, rg);
            }
            using (Pen p = new Pen(Theme.Blue, Theme.D(1.5f))) g.DrawRectangle(p, s.X, s.Y, s.Width, s.Height);
            float hs = Theme.D(9);
            PointF[] corners = new PointF[] { new PointF(s.Left, s.Top), new PointF(s.Right, s.Top), new PointF(s.Left, s.Bottom), new PointF(s.Right, s.Bottom) };
            foreach (PointF c in corners)
            {
                Theme.Dot(g, Theme.Accent, c.X, c.Y, hs);
                Theme.Ring(g, Color.FromArgb(120, 0, 0, 0), 1f, c.X, c.Y, hs);
            }

            if (SelectMode == 1)
            {
                string t = L.T("Область сканирования", "Scan area");
                SizeF ts = Theme.MeasureF(g, t, Theme.FontSmall);
                RectangleF lr = new RectangleF(s.X, s.Y - Theme.D(28), ts.Width + Theme.D(22), Theme.D(22));
                if (lr.Y < Theme.D(6)) lr.Y = s.Y + Theme.D(6);
                Theme.FillRound(g, Theme.Accent, lr, lr.Height / 2f);
                Theme.TextC(g, t, Theme.FontSmall, lr, Theme.AccentInk);
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
                Pill(g, cancelRect, L.T("Отмена", "Cancel"), false);
                Pill(g, applyRect, L.T("Обрезать", "Crop"), true);
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
                float d = Theme.D(80) * pop;
                float ccx = Width / 2f, ccy = Height / 2f;
                Theme.Dot(g, Color.FromArgb((int)(50 * fade), 255, 255, 255), ccx, ccy, d * 1.6f);
                Theme.Dot(g, Color.FromArgb((int)(255 * fade), 255, 255, 255), ccx, ccy, d);
                Icons.Draw(g, "check", Color.FromArgb((int)(255 * fade), Theme.AccentInk), ccx - d * 0.3f, ccy - d * 0.3f, d * 0.6f, 2.6f);
            }
        }

        void DrawCountdown(Graphics g, float cx, float cy)
        {
            float d = Theme.D(150);
            Theme.Dot(g, Color.FromArgb(200, 8, 13, 28), cx, cy, d + Theme.D(30));
            Theme.Ring(g, Theme.StrokeStrong, 1f, cx, cy, d + Theme.D(30));
            Theme.Ring(g, Theme.Stroke, Theme.D(5), cx, cy, d);
            float frac = (float)Countdown / Math.Max(1, CountdownTotal);
            using (Pen p = new Pen(Theme.Blue, Theme.D(5)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, cx - d / 2f, cy - d / 2f, d, d, -90f, 360f * frac);
            }
            Theme.TextC(g, Countdown.ToString(), Theme.FontHuge, new RectangleF(cx - d / 2f, cy - d / 2f - Theme.D(8), d, d), Theme.Text);
            Theme.TextC(g, L.T("до следующего скана", "until next scan"), Theme.FontSmall, new RectangleF(cx - d / 2f, cy + Theme.D(22), d, Theme.D(20)), Theme.Muted);
            Theme.TextC(g, L.T("Поменяйте лист · Esc — остановить", "Swap the sheet · Esc to stop"), Theme.FontBody, new RectangleF(0, cy + d / 2f + Theme.D(30), Width, Theme.D(24)), Theme.Text2);
        }

        static void Shadow(Graphics g, RectangleF r)
        {
            for (int k = 1; k <= 6; k++)
            {
                RectangleF s = r; s.Inflate(k * 2.5f, k * 2.5f); s.Offset(0, k * 3f);
                Theme.FillRound(g, Color.FromArgb(16, 0, 0, 0), s, 8 + k * 2);
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

        public ThumbStrip() { TrackHover = false; }

        protected override void Render(Graphics g)
        {
            thumbs.Clear(); dels.Clear();
            Theme.GlassRect(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.D(18), Theme.Raised, Theme.Stroke);
            int pad = Theme.D(22);
            string label = Note ?? (Pages.Count == 0 ? L.T("Страниц пока нет", "No pages yet") : L.T("Страниц в документе: ", "Pages in document: ") + Pages.Count);
            SizeF ls = Theme.MeasureF(g, label, Theme.FontBody);
            Theme.TextAt(g, label, Theme.FontBody, pad, (Height - ls.Height) / 2f, Note != null ? Theme.Text : Theme.Muted);
            int tw = Theme.D(40), th = Theme.D(52), gap = Theme.D(12);
            int y = (Height - th) / 2;
            int x = pad + (int)Math.Ceiling(ls.Width) + Theme.D(20);
            int avail = Width - pad - x - (tw + gap);
            int maxVis = Math.Max(1, avail / (tw + gap));
            int start = Math.Max(0, Pages.Count - maxVis);
            if (start > 0)
            {
                start = Math.Max(0, Pages.Count - (maxVis - 1));
                RectangleF mr = new RectangleF(x + 0.5f, y + 0.5f, tw - 1, th - 1);
                Theme.FillRound(g, Theme.Inset, mr, Theme.D(8));
                Theme.Edge(g, mr, Theme.D(8), Theme.StrokeStrong, Theme.Stroke);
                Theme.TextC(g, "+" + start, Theme.FontChip, mr, Theme.Muted);
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
                        GraphicsState gs = g.Save();
                        g.SetClip(cp, CombineMode.Intersect);
                        float s = Math.Min((float)tw / t.Width, (float)th / t.Height);
                        float w = t.Width * s, h = t.Height * s;
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(t, r.X + (tw - w) / 2f, r.Y + (th - h) / 2f, w, h);
                        g.Restore(gs);
                    }
                }
                if (Pages[i].SavedPath == null) Theme.StrokeRound(g, Theme.Muted, 1f, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.D(8));
                if (dragMove && i == downIdx) Theme.FillRound(g, Color.FromArgb(160, 8, 13, 28), r, Theme.D(8));
                if (i == Selected)
                {
                    RectangleF o = r; o.Inflate(Theme.D(3), Theme.D(3));
                    Theme.StrokeRound(g, Theme.Blue, Theme.D(2), o, Theme.D(10));
                }
                if (i == hoverThumb && Enabled && !dragMove)
                {
                    float d = Theme.D(18);
                    float bcx = r.Right - Theme.D(10), bcy = r.Y + Theme.D(10);
                    Theme.Dot(g, hoverDel == i ? Color.FromArgb(255, 0xc4, 0x2b, 0x3a) : Color.FromArgb(235, 10, 16, 32), bcx, bcy, d);
                    Theme.Ring(g, Theme.StrokeStrong, 1f, bcx, bcy, d);
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
                using (SolidBrush b = new SolidBrush(Theme.Blue)) g.FillRectangle(b, lx - Theme.D(1.5f), y - Theme.D(2), Theme.D(3), th + Theme.D(4));
            }
            addRect = new Rectangle(x, y, tw, th);
            RectangleF ar = new RectangleF(addRect.X + 0.5f, addRect.Y + 0.5f, tw - 1, th - 1);
            if (hoverAdd && Enabled) Theme.FillRound(g, Theme.Hover, ar, Theme.D(8));
            using (GraphicsPath pp = Theme.Round(ar, Theme.D(8)))
            using (Pen pen = new Pen(hoverAdd && Enabled ? Color.FromArgb(170, Theme.Blue) : Theme.StrokeStrong, 1f))
            {
                pen.DashStyle = DashStyle.Dash; g.DrawPath(pen, pp);
            }
            int ipx = Theme.D(18);
            Icons.Draw(g, "plus", !Enabled ? Theme.Dim : (hoverAdd ? Theme.Text : Theme.Muted),
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
            if (!dragMove) { hoverThumb = -1; hoverDel = -1; hoverAdd = false; Invalidate(); }
            base.OnMouseLeave(e);
        }
    }
}
