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
        float success = -1f;              // -1 — нет анимации, иначе 0..1
        Bitmap ghost;
        bool dragging, selecting;
        Point dragFrom;
        PointF selAnchor;
        RectangleF imgRect = RectangleF.Empty;
        Rectangle applyRect = Rectangle.Empty, cancelRect = Rectangle.Empty;

        public PreviewPanel() { TrackHover = false; }

        public bool HasSel { get { return Sel.Width > 0 && Sel.Height > 0; } }
        public void SetBusy(bool b) { busy = b; sweep = 0f; Kick(); Invalidate(); }
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
            if (busy && Image != null) { sweep += 0.016f; if (sweep > 1f) sweep = 0f; more = true; }
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
                Theme.FillRound(g, Theme.Panel, rf, rad);
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
            int icon = Theme.D(48);
            Icons.Draw(g, busy ? "scan" : "file-text", Theme.Muted, cx - icon / 2f, cy - icon, icon, 1.65f);
            Theme.TextC(g, busy ? L.T("Сканирование…", "Scanning…") : L.T("Предпросмотр страницы", "Page preview"),
                Theme.FontBig, new RectangleF(0, cy + Theme.D(10), Width, Theme.D(28)), Theme.Text);
            Theme.TextC(g, busy ? L.T("Не поднимайте крышку", "Keep the lid closed") : L.T("Положите лист и нажмите «Просмотр»", "Place a sheet and choose Preview"),
                Theme.FontSmall, new RectangleF(0, cy + Theme.D(40), Width, Theme.D(22)), Theme.Muted);
        }

        protected override void Render(Graphics g)
        {
            applyRect = Rectangle.Empty; cancelRect = Rectangle.Empty;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float rad = Theme.D(10);
            Theme.GlassRect(g, outer, rad, Theme.Panel, Theme.Stroke);
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
                    using (Pen p = new Pen(Color.FromArgb(110, Theme.Selection), Theme.D(1.5f))) { p.DashStyle = DashStyle.Dash; g.DrawPath(p, op); }
                }

                if (hasSel) DrawSelection(g, page);

                if (busy && Image != null)
                {
                    float topY = Math.Max(page.Y, 0), bottom = Math.Min(page.Bottom, Height);
                    float y = topY + (bottom - topY) * sweep;
                    float lx = Math.Max(page.X - Theme.D(16), 0), lw = Math.Min(page.Width + Theme.D(32), Width);
                    float gh = Theme.D(28);
                    using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(lx, y - gh, lw, gh + 1),
                        Color.FromArgb(0, Theme.Selection), Color.FromArgb(90, Theme.Selection), LinearGradientMode.Vertical))
                        g.FillRectangle(lg, lx, y - gh, lw, gh);
                    using (SolidBrush b = new SolidBrush(Theme.Text)) g.FillRectangle(b, lx, y, lw, Theme.D(2));
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
            string[] chips = new string[] { InfoB, InfoA, Image == null ? "" : ((int)Math.Round(zoom * 100)) + "%" };
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
            using (Pen p = new Pen(Theme.Selection, Theme.D(1.5f))) g.DrawRectangle(p, s.X, s.Y, s.Width, s.Height);
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
            Theme.Dot(g, Theme.Raised, cx, cy, d + Theme.D(30));
            Theme.Ring(g, Theme.StrokeStrong, 1f, cx, cy, d + Theme.D(30));
            Theme.Ring(g, Theme.Stroke, Theme.D(5), cx, cy, d);
            float frac = (float)Countdown / Math.Max(1, CountdownTotal);
            using (Pen p = new Pen(Theme.Selection, Theme.D(5)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, cx - d / 2f, cy - d / 2f, d, d, -90f, 360f * frac);
            }
            Theme.TextC(g, Countdown.ToString(), Theme.FontHuge, new RectangleF(cx - d / 2f, cy - d / 2f - Theme.D(8), d, d), Theme.Text);
            Theme.TextC(g, L.T("до следующего скана", "until next scan"), Theme.FontSmall, new RectangleF(cx - d / 2f, cy + Theme.D(22), d, Theme.D(20)), Theme.Muted);
            Theme.TextC(g, L.T("Поменяйте лист · Esc — остановить", "Swap the sheet · Esc to stop"), Theme.FontBody, new RectangleF(0, cy + d / 2f + Theme.D(30), Width, Theme.D(24)), Theme.Text2);
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

        int first, lastSelected = -2, visibleCount;

        public ThumbStrip() { TrackHover = false; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            first = Math.Max(0, Math.Min(Math.Max(0, Pages.Count - visibleCount), first - Math.Sign(e.Delta)));
            Invalidate();
        }

        protected override void Render(Graphics g)
        {
            thumbs.Clear(); dels.Clear();
            int pad = Theme.D(10), gap = Theme.D(12);
            Theme.GlassRect(g, new RectangleF(.5f, .5f, Width - 1, Height - 1), Theme.D(10), Theme.Panel, Theme.Stroke);
            int btn = Theme.D(26);
            addRect = new Rectangle(Width - btn - pad, pad, btn, btn);
            if (hoverAdd && Enabled) Theme.FillRound(g, Theme.Hover, addRect, Theme.D(8));
            Icons.Draw(g, "plus", Enabled ? Theme.Text : Theme.Dim, addRect.X + Theme.D(4), addRect.Y + Theme.D(4), Theme.D(18), 1.65f);
            Theme.Write(g, L.T("Страницы", "Pages"), Theme.FontChip, new RectangleF(pad, pad, addRect.Left - pad, btn), Theme.Text, 0, true);
            int tw = Width - 2 * pad, th = Math.Max(1, tw * 13 / 10);
            int y = pad + btn + gap;
            if (!string.IsNullOrEmpty(Note))
            {
                Theme.TextWrap(g, Note, Theme.FontSmall, new RectangleF(pad, y, tw, Theme.D(40)), Theme.Text2);
                y += Theme.D(42);
            }
            visibleCount = Math.Max(1, (Height - y - pad + gap) / (th + gap));
            if (Selected != lastSelected)
            {
                lastSelected = Selected;
                if (Selected < first) first = Selected;
                if (Selected >= first + visibleCount) first = Selected - visibleCount + 1;
            }
            first = Math.Max(0, Math.Min(first, Pages.Count - visibleCount));
            int x = pad;
            for (int i = first; i < Math.Min(Pages.Count, first + visibleCount); i++)
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
                if (dragMove && i == downIdx) Theme.FillRound(g, Theme.Overlay, r, Theme.D(8));
                if (i == Selected)
                {
                    RectangleF o = r; o.Inflate(Theme.D(3), Theme.D(3));
                    Theme.StrokeRound(g, Theme.Selection, Theme.D(2), o, Theme.D(10));
                }
                if (i == hoverThumb && Enabled && !dragMove)
                {
                    float d = Theme.D(18);
                    float bcx = r.Right - Theme.D(10), bcy = r.Y + Theme.D(10);
                    Theme.Dot(g, hoverDel == i ? Theme.Press : Theme.Panel, bcx, bcy, d);
                    Theme.Ring(g, Theme.StrokeStrong, 1f, bcx, bcy, d);
                    float ip = Theme.D(12);
                    Icons.Draw(g, "close", hoverDel == i ? Color.White : Theme.Text2, bcx - ip / 2f, bcy - ip / 2f, ip, 3f);
                    dels.Add(new KeyValuePair<Rectangle, int>(Rectangle.Round(new RectangleF(bcx - d / 2f - 2, bcy - d / 2f - 2, d + 4, d + 4)), i));
                }
                thumbs.Add(new KeyValuePair<Rectangle, int>(r, i));
                Rectangle badge = new Rectangle(r.Right - Theme.D(27), r.Bottom - Theme.D(21), Theme.D(25), Theme.D(19));
                Theme.FillRound(g, Theme.Panel, badge, Theme.D(5));
                Theme.TextC(g, (i + 1).ToString(), Theme.FontSmall, badge, Theme.Text);
                y += th + gap;
            }
            if (dragMove && thumbs.Count > 0)
            {
                int line = thumbs[thumbs.Count - 1].Key.Bottom + gap / 2;
                foreach (KeyValuePair<Rectangle, int> row in thumbs)
                    if (row.Value == dropAt) line = row.Key.Top - gap / 2;
                using (Pen pen = new Pen(Theme.Text, Theme.D(2))) g.DrawLine(pen, pad, line, Width - pad, line);
            }
        }

        int HitThumb(Point p) { foreach (KeyValuePair<Rectangle, int> h in thumbs) if (h.Key.Contains(p)) return h.Value; return -1; }
        int HitDel(Point p) { foreach (KeyValuePair<Rectangle, int> h in dels) if (h.Key.Contains(p)) return h.Value; return -1; }
        int DropIndex(int py)
        {
            foreach (KeyValuePair<Rectangle, int> h in thumbs) if (py < h.Key.Y + h.Key.Height / 2) return h.Value;
            return Math.Min(Pages.Count, first + visibleCount);
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
            if (e.Button == MouseButtons.Left && downIdx >= 0 && AllowReorder && !dragMove && Math.Abs(e.Y - downPt.Y) > Theme.D(6))
                dragMove = true;
            if (dragMove) { dropAt = DropIndex(e.Y); Cursor = Cursors.SizeNS; Invalidate(); return; }
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
