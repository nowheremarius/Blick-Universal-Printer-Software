using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace Blick
{
    class BaseCtl : Control, IAnimated
    {
        protected bool hover;
        protected float hv;
        public BaseCtl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
        protected Color ParentBg { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }
        protected void Kick() { Anim.Kick(this); }

        // бегущая строка для текста, который не влезает
        protected float marq;
        int hoverTicks;
        bool marqNeeded;
        protected void ResetMarqueeFlag() { marqNeeded = false; }
        protected void DrawMarquee(Graphics g, string text, Font f, Rectangle r, Color c, Color bg, bool animate)
        {
            TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
            Size sz = Theme.Measure(g, text, f);
            if (sz.Width <= r.Width)
            {
                TextRenderer.DrawText(g, text, f, r, c, fl);
                return;
            }
            // Длинный текст всегда рисуется одним способом — и в покое, и в движении, поэтому старт без рывка
            if (animate) { marqNeeded = true; Kick(); }
            GraphicsState gs = g.Save();
            try
            {
                g.SetClip(r, CombineMode.Intersect);
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                StringFormat sf = StringFormat.GenericTypographic;
                SizeF ms = g.MeasureString(text, f, PointF.Empty, sf);
                float period = ms.Width + Theme.D(40);
                float off = animate ? marq % period : 0f;
                float y = r.Y + (r.Height - ms.Height) / 2f;
                using (SolidBrush br = new SolidBrush(c))
                {
                    g.DrawString(text, f, br, r.X - off, y, sf);
                    if (off > 0f) g.DrawString(text, f, br, r.X - off + period, y, sf);
                }
                // виньетка по краям вместо многоточия
                float fw = Math.Min(Theme.D(30), r.Width / 3f);
                Fade(g, new RectangleF(r.Right - fw, r.Y, fw, r.Height), bg, 1f, false);
                // левая виньетка проявляется плавно один раз и дальше не исчезает — без вспышек при новом круге
                float lstr = animate ? Math.Min(1f, marq / fw) : 0f;
                if (lstr > 0f) Fade(g, new RectangleF(r.X, r.Y, fw, r.Height), bg, lstr, true);
            }
            finally { g.Restore(gs); }
        }

        static void Fade(Graphics g, RectangleF rf, Color bg, float strength, bool fromLeft)
        {
            Color solid = Color.FromArgb((int)(255 * strength), bg);
            Color clear = Color.FromArgb(0, bg);
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(rf.X - 1, rf.Y, rf.Width + 2, rf.Height),
                clear, solid, LinearGradientMode.Horizontal))
            {
                ColorBlend cb = new ColorBlend(3);
                cb.Colors = fromLeft ? new Color[] { solid, Color.FromArgb((int)(200 * strength), bg), clear }
                                     : new Color[] { clear, Color.FromArgb((int)(200 * strength), bg), solid };
                cb.Positions = new float[] { 0f, 0.55f, 1f };
                lg.InterpolationColors = cb;
                g.FillRectangle(lg, rf);
            }
        }

        public bool Step()
        {
            if (IsDisposed) return false;
            float target = hover && Enabled ? 1f : 0f;
            float n = Anim.To(hv, target, 0.22f);
            bool changed = n != hv;
            hv = n;
            bool more = ExtraStep();
            if (hover && marqNeeded)
            {
                hoverTicks++;
                if (hoverTicks > 25)
                {
                    float ramp = Math.Min(1f, (hoverTicks - 25) / 30f);
                    marq += 0.8f * Theme.Scale * ramp * ramp;
                    changed = true;
                }
                more = true;
            }
            else if (marq != 0f || hoverTicks != 0) { marq = 0f; hoverTicks = 0; changed = true; }
            if (changed || more) Invalidate();
            return hv != target || more;
        }
        protected virtual bool ExtraStep() { return false; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Kick(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Kick(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); Kick(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    }

    class IconButton : BaseCtl
    {
        public string Kind;
        public int IconPx;
        public bool WindowButton;
        public IconButton(string kind, int iconPx, bool windowButton)
        {
            Kind = kind; IconPx = iconPx; WindowButton = windowButton; Cursor = Cursors.Hand;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float t = hv;
            if (t > 0.01f && Enabled)
            {
                bool close = WindowButton && Kind == "close";
                Color fill = close ? Color.FromArgb(0xc4, 0x2b, 0x4a) : Theme.Mix(Theme.HoverBg, Theme.Accent, 0.08f);
                Theme.FillRound(g, Theme.Mix(ParentBg, fill, t), r, Theme.D(WindowButton ? 8 : 14));
                if (!WindowButton) Theme.StrokeRound(g, Theme.Mix(ParentBg, Theme.Border2, t), 1f, r, Theme.D(14));
            }
            Color c;
            if (!Enabled) c = Theme.Dim;
            else if (WindowButton) c = Kind == "close" ? Theme.Mix(Theme.Muted, Color.White, hv) : Theme.Mix(Theme.Muted, Theme.Text, hv);
            else c = Theme.Mix(Theme.Muted, Theme.Accent, t);
            Icons.Draw(g, Kind, c, (Width - IconPx) / 2f, (Height - IconPx) / 2f, IconPx, WindowButton ? 2.6f : 1.8f);
        }
    }

    class ChipGroup : BaseCtl
    {
        public string[] Items;
        int sel;
        int hoverIdx = -1;
        float[] hvs, selT;
        public event EventHandler Changed;
        public ChipGroup(params string[] items)
        {
            Items = items; Cursor = Cursors.Hand;
            hvs = new float[items.Length]; selT = new float[items.Length];
        }
        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                sel = Math.Max(-1, Math.Min(Items.Length - 1, value));
                if (!IsHandleCreated) for (int i = 0; i < selT.Length; i++) selT[i] = i == sel ? 1f : 0f;
                Kick(); Invalidate();
            }
        }
        protected override bool ExtraStep()
        {
            bool more = false;
            for (int i = 0; i < Items.Length; i++)
            {
                float th = i == hoverIdx && hover && Enabled ? 1f : 0f, ts = i == sel ? 1f : 0f;
                hvs[i] = Anim.To(hvs[i], th, 0.25f);
                selT[i] = Anim.To(selT[i], ts, 0.22f);
                if (hvs[i] != th || selT[i] != ts) more = true;
            }
            return more;
        }
        RectangleF Cell(int i)
        {
            float gap = Theme.D(6);
            float w = (Width - 1 - gap * (Items.Length - 1)) / Items.Length;
            return new RectangleF(i * (w + gap) + 0.5f, 0.5f, w, Height - 1);
        }
        int HitIndex(Point p)
        {
            for (int i = 0; i < Items.Length; i++) if (Cell(i).Contains(p)) return i;
            return -1;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            ResetMarqueeFlag();
            for (int i = 0; i < Items.Length; i++)
            {
                RectangleF r = Cell(i);
                float s = selT[i];
                Color fill = Theme.Mix(Theme.Mix(Theme.Card, Theme.HoverBg, hvs[i]), Theme.SelBg, s);
                Color border = Enabled ? Theme.Mix(Theme.Line, Theme.Accent, s) : Theme.Line;
                Theme.FillRound(g, fill, r, Theme.D(11));
                Theme.StrokeRound(g, border, s > 0.5f ? 1.5f : 1f, r, Theme.D(11));
                Color tc = !Enabled ? Theme.Dim : Theme.Mix(Theme.Text2, Theme.Text, s);
                Rectangle tr = Rectangle.Round(r);
                tr.Inflate(-Theme.D(8), 0);
                Size tsz = Theme.Measure(g, Items[i], Theme.FontChip);
                if (tsz.Width <= tr.Width) Theme.TextC(g, Items[i], Theme.FontChip, Rectangle.Round(r), tc);
                else DrawMarquee(g, Items[i], Theme.FontChip, tr, tc, fill, hover && i == hoverIdx);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = HitIndex(e.Location);
            if (i != hoverIdx) { hoverIdx = i; marq = 0f; Kick(); Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIdx = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = HitIndex(e.Location);
            if (i >= 0 && i != sel && Enabled)
            {
                sel = i; Kick();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
            base.OnMouseClick(e);
        }
    }

    class SwitchRow : BaseCtl
    {
        bool isOn;
        float knob;
        public string Title, Sub, DisabledSub;
        public bool ShowDisabledSub;
        public event EventHandler Changed;
        public SwitchRow(string title, string sub) { Title = title; Sub = sub; Cursor = Cursors.Hand; }
        public bool Checked
        {
            get { return isOn; }
            set { isOn = value; if (!IsHandleCreated) knob = value ? 1f : 0f; Kick(); Invalidate(); }
        }
        protected override bool ExtraStep()
        {
            float t = isOn && Enabled ? 1f : 0f;
            knob = Anim.To(knob, t, 0.25f);
            return knob != t;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color cardBg = Theme.Mix(Theme.Card, Theme.HoverBg, hv);
            Theme.FillRound(g, cardBg, r, Theme.D(14));
            Theme.StrokeRound(g, Theme.CardLine, 1f, r, Theme.D(14));
            int sw = Theme.D(42), sh = Theme.D(24);
            int textW = Width - Theme.D(14) * 3 - sw;
            ResetMarqueeFlag();
            int ty = (Height - Theme.D(38)) / 2;
            DrawMarquee(g, Title, Theme.FontChip, new Rectangle(Theme.D(14), ty, textW, Theme.D(20)), Enabled ? Theme.Text : Theme.Dim, cardBg, hover);
            string sub = ShowDisabledSub && DisabledSub != null ? DisabledSub : Sub;
            DrawMarquee(g, sub, Theme.FontSmall, new Rectangle(Theme.D(14), ty + Theme.D(20), textW, Theme.D(18)), Enabled ? Theme.Muted : Theme.Dim, cardBg, hover);
            RectangleF s = new RectangleF(Width - Theme.D(14) - sw, (Height - sh) / 2f, sw, sh);
            Theme.FillRound(g, Enabled ? Theme.Mix(Theme.SwitchOff, Theme.Accent, knob) : Theme.SwitchOff, s, sh / 2f);
            float k = sh - Theme.D(6);
            float kx = s.X + Theme.D(3) + (s.Width - Theme.D(6) - k) * knob;
            Color kc = Theme.IsLight ? Color.White : Theme.Mix(Theme.Muted, Theme.AccentInk, knob);
            Theme.Dot(g, kc, kx + k / 2f, s.Y + sh / 2f, k);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (Enabled)
            {
                isOn = !isOn; Kick();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
            base.OnMouseClick(e);
        }
    }

    class PillButton : BaseCtl
    {
        public bool Primary;
        public string IconKind;
        public bool IconOnly;
        public PillButton(string text, bool primary, string icon)
        {
            Text = text; Primary = primary; IconKind = icon; Font = Theme.FontBtn; Cursor = Cursors.Hand;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color tc;
            if (Primary)
            {
                Color fill = !Enabled ? Theme.AccentDisabled : Theme.Mix(Theme.Accent, Theme.AccentHover, hv);
                Theme.FillRound(g, fill, r, Height / 2f);
                tc = Theme.AccentInk;
            }
            else
            {
                Theme.FillRound(g, Theme.Mix(ParentBg, Theme.HoverBg, hv), r, Height / 2f);
                Theme.StrokeRound(g, Theme.Border2, 1f, r, Height / 2f);
                tc = Enabled ? Theme.Text : Theme.Muted;
            }
            int iconPx = IconKind != null ? Theme.D(18) : 0;
            if (IconOnly)
            {
                Icons.Draw(g, IconKind, tc, (Width - iconPx) / 2f, (Height - iconPx) / 2f, iconPx, 2.2f);
                return;
            }
            Size ts = Theme.Measure(g, Text, Font);
            int gap = IconKind != null ? Theme.D(10) : 0;
            int total = ts.Width + iconPx + gap;
            int x0 = Math.Max(Theme.D(8), (Width - total) / 2);
            if (IconKind != null) Icons.Draw(g, IconKind, tc, x0, (Height - iconPx) / 2f, iconPx, 2.2f);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x0 + iconPx + gap, 0, Width - x0 - iconPx - gap - Theme.D(6), Height), tc,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    // вкладки
    class TabBar : BaseCtl
    {
        public string[] Items;
        int sel;
        float pos;
        int hoverIdx = -1;
        public event EventHandler Changed;
        public TabBar(params string[] items) { Items = items; Cursor = Cursors.Hand; }
        public int SelectedIndex { get { return sel; } set { sel = value; if (!IsHandleCreated) pos = value; Kick(); Invalidate(); } }
        protected override bool ExtraStep() { pos = Anim.To(pos, sel, 0.25f); return pos != sel; }
        RectangleF Cell(float i)
        {
            float pad = Theme.D(4);
            float w = (Width - 1 - pad * 2) / Items.Length;
            return new RectangleF(pad + i * w + 0.5f, pad + 0.5f, w, Height - 1 - pad * 2);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, Theme.Card, r, Height / 2f);
            Theme.StrokeRound(g, Theme.CardLine, 1f, r, Height / 2f);
            RectangleF ind = Cell(pos);
            Theme.FillRound(g, Theme.Accent, ind, ind.Height / 2f);
            for (int i = 0; i < Items.Length; i++)
            {
                float d = Math.Abs(pos - i);
                Color c = Theme.Mix(Theme.AccentInk, i == hoverIdx ? Theme.Text : Theme.Text2, Math.Min(1f, d));
                Theme.TextC(g, Items[i], Theme.FontChip, Rectangle.Round(Cell(i)), c);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = -1;
            for (int k = 0; k < Items.Length; k++) if (Cell(k).Contains(e.Location)) i = k;
            if (i != hoverIdx) { hoverIdx = i; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIdx = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            for (int k = 0; k < Items.Length; k++)
                if (Cell(k).Contains(e.Location) && k != sel)
                {
                    sel = k; Kick();
                    if (Changed != null) Changed(this, EventArgs.Empty);
                }
            base.OnMouseClick(e);
        }
    }

    // заголовок окна
    class TitleBar : BaseCtl
    {
        readonly MainForm form;
        string device = "Ищу сканер…";
        bool connected;
        public readonly IconButton Gear;
        readonly IconButton bMin, bMax, bClose;
        public Action DeviceClicked;
        RectangleF chipRect;

        public TitleBar(MainForm f)
        {
            form = f; BackColor = Theme.Bg;
            int ip = Theme.D(14);
            bMin = new IconButton("min", ip, true);
            bMax = new IconButton("max", ip, true);
            bClose = new IconButton("close", ip, true);
            Gear = new IconButton("gear", Theme.D(19), true);
            bMin.Click += delegate { form.WindowState = FormWindowState.Minimized; };
            bMax.Click += delegate { form.ToggleMax(); };
            bClose.Click += delegate { form.Close(); };
            Controls.Add(Gear); Controls.Add(bMin); Controls.Add(bMax); Controls.Add(bClose);
        }

        public void SetDevice(string text, bool ok) { device = text; connected = ok; Invalidate(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (bClose == null) return;
            int w = Theme.D(44), h = Theme.D(36), y = (Height - h) / 2;
            int x = Width - Theme.D(10) - w;
            bClose.SetBounds(x, y, w, h); x -= w + Theme.D(2);
            bMax.SetBounds(x, y, w, h); x -= w + Theme.D(2);
            bMin.SetBounds(x, y, w, h); x -= w + Theme.D(14);
            Gear.SetBounds(x, y, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(Theme.Bg); Theme.Hq(g);
            using (Pen p = new Pen(Theme.LineSoft)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            using (Pen p = new Pen(Theme.LineSoft)) g.DrawLine(p, Gear.Right + Theme.D(7), Theme.D(14), Gear.Right + Theme.D(7), Height - Theme.D(14));
            float cy = Height / 2f;
            float x = Theme.D(20);
            float ring = Theme.D(26), stroke = Theme.D(3);
            Theme.Ring(g, Theme.Accent, stroke, x + ring / 2f, cy, ring - stroke);
            Theme.Dot(g, Theme.Accent, x + ring / 2f, cy, Theme.D(8));
            x += ring + Theme.D(12);
            TextFormatFlags fl = TextFormatFlags.NoPadding;
            Size s1 = Theme.Measure(g, "Blick", Theme.FontLogo);
            TextRenderer.DrawText(g, "Blick", Theme.FontLogo, new Point((int)x, (int)(cy - s1.Height / 2f)), Theme.Text, fl);
            x += s1.Width;
            Size s2 = Theme.Measure(g, ".", Theme.FontLogo);
            TextRenderer.DrawText(g, ".", Theme.FontLogo, new Point((int)x, (int)(cy - s1.Height / 2f)), Theme.Accent, fl);
            x += s2.Width + Theme.D(16);

            Size ds = Theme.Measure(g, device, Theme.FontSmall);
            float ch = Theme.D(26);
            float maxW = Gear.Left - Theme.D(16) - x;
            RectangleF chip = new RectangleF(x, cy - ch / 2f, Math.Min(ds.Width + Theme.D(36), Math.Max(Theme.D(60), maxW)), ch);
            Theme.FillRound(g, Theme.Mix(Theme.Bg, Theme.HoverBg, hover && chipRect.Contains(PointToClient(MousePosition)) ? 1f : 0f), chip, ch / 2f);
            Theme.StrokeRound(g, Theme.Line, 1f, chip, ch / 2f);
            Theme.Dot(g, connected ? Theme.Accent : Theme.Dim, chip.X + Theme.D(13), cy, Theme.D(6));
            TextRenderer.DrawText(g, device, Theme.FontSmall, new Rectangle((int)(chip.X + Theme.D(24)), (int)chip.Y, (int)chip.Width - Theme.D(30), (int)ch),
                connected ? Theme.Text2 : Theme.Muted, fl | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            chipRect = chip;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = chipRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (chipRect.Contains(e.Location)) { if (DeviceClicked != null) DeviceClicked(); return; }
            if (e.Clicks == 2) { form.ToggleMax(); return; }
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(form.Handle, 0xA1, 2, 0);
        }
    }

    // шапка над предпросмотром
    class HeaderCtl : BaseCtl
    {
        public string InfoA = "—", InfoB = "—";
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            float x = Width - 1;
            string[] chips = new string[] { InfoB, InfoA };
            foreach (string t in chips)
            {
                Size s = Theme.Measure(g, t, Theme.FontBody);
                float w = s.Width + Theme.D(28), h = Theme.D(32);
                x -= w;
                RectangleF r = new RectangleF(x, Height - h - Theme.D(6), w, h);
                Theme.FillRound(g, Theme.Card, r, h / 2f);
                Theme.StrokeRound(g, Theme.Line, 1f, r, h / 2f);
                Theme.TextC(g, t, Theme.FontBody, Rectangle.Round(r), Theme.Text2);
                x -= Theme.D(8);
            }
            int tw = (int)x - Theme.D(12);
            TextRenderer.DrawText(g, "Предпросмотр", Theme.FontBig, new Rectangle(0, Height - Theme.D(38), tw, Theme.D(32)), Theme.Text, fl | TextFormatFlags.VerticalCenter);
        }
    }

    // карточка «куда сохранять»
    class SaveCard : BaseCtl
    {
        public string FolderName = "";
        public string Status = "";
        public int StatusKind; // 0 обычный, 1 сохранено, 2 ошибка
        public bool Openable;
        public Action ChangeFolder;
        public Action OpenSaved;
        Rectangle linkRect, statusRect;
        float flash;

        public void Flash() { flash = 1f; Kick(); }
        protected override bool ExtraStep()
        {
            if (flash <= 0f) return false;
            flash = Math.Max(0f, flash - 0.012f);
            return flash > 0f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, Theme.Mix(Theme.Card, Theme.SelBg, flash), r, Theme.D(16));
            Theme.StrokeRound(g, Theme.Mix(Theme.CardLine, Theme.Accent, flash), 1f, r, Theme.D(16));
            TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            int pad = Theme.D(14), ip = Theme.D(18);
            Icons.Draw(g, "folder", Theme.Text, pad, Theme.D(12), ip, 1.8f);
            string link = "Изменить";
            Size lsz = Theme.Measure(g, link, Theme.FontChip);
            linkRect = new Rectangle(Width - pad - lsz.Width, Theme.D(12), lsz.Width, Theme.D(20));
            TextRenderer.DrawText(g, link, Theme.FontChip, linkRect.Location, Theme.Accent, TextFormatFlags.NoPadding);
            int nx = pad + ip + Theme.D(8);
            TextRenderer.DrawText(g, FolderName, Theme.FontChip, new Rectangle(nx, Theme.D(12), linkRect.X - nx - Theme.D(8), Theme.D(22)), Theme.Text, fl);
            int sx = pad;
            if (flash > 0f && StatusKind == 1)
            {
                float pop = flash > 0.85f ? (1f - flash) / 0.15f : 1f;
                float d = Theme.D(18) * (0.6f + 0.4f * pop);
                float ccx = pad + Theme.D(9), ccy = Theme.D(38) + Theme.D(9);
                Theme.Dot(g, Theme.Mix(Theme.Card, Theme.Accent, Math.Min(1f, flash * 2f)), ccx, ccy, d);
                Icons.Draw(g, "check", Theme.AccentInk, ccx - d * 0.35f, ccy - d * 0.35f, d * 0.7f, 3f);
                sx = pad + Theme.D(26);
            }
            statusRect = new Rectangle(sx, Theme.D(38), Width - sx - pad, Height - Theme.D(38) - Theme.D(8));
            Color sc = StatusKind == 2 ? Theme.Error : (StatusKind == 1 ? Theme.Text2 : Theme.Muted);
            TextRenderer.DrawText(g, Status, Theme.FontSmall, statusRect, sc, fl | TextFormatFlags.WordBreak);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hand = linkRect.Contains(e.Location) || (Openable && statusRect.Contains(e.Location));
            Cursor = hand ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (linkRect.Contains(e.Location)) { if (ChangeFolder != null) ChangeFolder(); }
            else if (Openable && statusRect.Contains(e.Location)) { if (OpenSaved != null) OpenSaved(); }
            base.OnMouseClick(e);
        }
    }

    // всплывающие окна
    class PopupForm : Form
    {
        public bool SuppressClose;
        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Panel;
            DoubleBuffered = true;
            KeyPreview = true;
        }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // тень
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int pref = 2; NativeMethods.DwmSetWindowAttribute(Handle, 33, ref pref, 4); } catch { }
        }
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!SuppressClose) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) Close(); });
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
        protected Label MakeLabel(string text, Font f, Color c)
        {
            Label l = new Label();
            l.Text = text; l.Font = f; l.ForeColor = c; l.BackColor = Theme.Panel; l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(l);
            return l;
        }
    }

    class Swatches : BaseCtl
    {
        public Action<Color> Picked;
        public Action CustomRequested;
        readonly List<KeyValuePair<RectangleF, int>> hits = new List<KeyValuePair<RectangleF, int>>();
        public Swatches() { Cursor = Cursors.Hand; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            hits.Clear();
            int count = Theme.Presets.Length + 1;
            float dia = Theme.D(26), gap = (Width - count * dia) / Math.Max(1, count - 1);
            float cy = Height / 2f;
            bool custom = true;
            for (int i = 0; i < count; i++)
            {
                float cx = i * (dia + gap) + dia / 2f;
                if (i < Theme.Presets.Length)
                {
                    Color c = Theme.Presets[i];
                    Theme.Dot(g, c, cx, cy, dia - Theme.D(4));
                    if (c.ToArgb() == Theme.Accent.ToArgb()) { custom = false; Theme.Ring(g, Theme.Text, Theme.D(2), cx, cy, dia + Theme.D(2)); }
                }
                else
                {
                    Theme.Ring(g, Theme.Muted, Theme.D(1.5f), cx, cy, dia - Theme.D(5));
                    float ip = Theme.D(14);
                    if (custom)
                    {
                        Theme.Dot(g, Theme.Accent, cx, cy, dia - Theme.D(4));
                        Theme.Ring(g, Theme.Text, Theme.D(2), cx, cy, dia + Theme.D(2));
                        Icons.Draw(g, "plus", Theme.AccentInk, cx - ip / 2f, cy - ip / 2f, ip, 2.4f);
                    }
                    else Icons.Draw(g, "plus", Theme.Muted, cx - ip / 2f, cy - ip / 2f, ip, 2.4f);
                }
                hits.Add(new KeyValuePair<RectangleF, int>(new RectangleF(cx - dia / 2f, 0, dia, Height), i));
            }
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int hit = -1;
            foreach (KeyValuePair<RectangleF, int> h in hits)
                if (h.Key.Contains(e.Location)) { hit = h.Value; break; }
            base.OnMouseClick(e);
            if (hit < 0) return;
            if (hit < Theme.Presets.Length) { if (Picked != null) Picked(Theme.Presets[hit]); }
            else if (CustomRequested != null) CustomRequested();
            if (!IsDisposed) Invalidate();
        }
    }

    class SettingsPopup : PopupForm
    {
        public static readonly int[] Scales = new int[] { 75, 90, 100, 110, 120 };
        public Action<Color> AccentChanged;
        public Action<int> ThemeChanged;
        public Action<int> ScaleChanged;
        readonly Swatches sw;

        public SettingsPopup(int themeKind, int scaleIdx)
        {
            int pad = Theme.D(20), w = Theme.D(460);
            int y = pad;
            Label title = MakeLabel("Настройки", Theme.FontBig, Theme.Text);
            title.SetBounds(pad, y, w - pad * 2, Theme.D(28)); y += Theme.D(40);

            Label l1 = MakeLabel("Цвет акцента", Theme.FontLabel, Theme.Muted);
            l1.SetBounds(pad, y, w - pad * 2, Theme.D(18)); y += Theme.D(24);
            sw = new Swatches();
            sw.SetBounds(pad, y, w - pad * 2, Theme.D(34)); y += Theme.D(34) + Theme.D(18);
            Controls.Add(sw);
            sw.Picked = delegate(Color c) { if (AccentChanged != null) AccentChanged(c); Invalidate(true); };
            sw.CustomRequested = delegate
            {
                SuppressClose = true;
                using (ColorDialog d = new ColorDialog())
                {
                    d.FullOpen = true; d.Color = Theme.Accent;
                    if (d.ShowDialog(this) == DialogResult.OK && AccentChanged != null) AccentChanged(d.Color);
                }
                SuppressClose = false;
                if (!IsDisposed) { Activate(); Invalidate(true); }
            };

            Label l2 = MakeLabel("Тема", Theme.FontLabel, Theme.Muted);
            l2.SetBounds(pad, y, w - pad * 2, Theme.D(18)); y += Theme.D(24);
            ChipGroup themes = new ChipGroup("Глубокий чёрный", "Графит", "Светлая");
            themes.SelectedIndex = themeKind;
            themes.SetBounds(pad, y, w - pad * 2, Theme.D(36)); y += Theme.D(36) + Theme.D(18);
            Controls.Add(themes);
            themes.Changed += delegate { if (ThemeChanged != null) ThemeChanged(themes.SelectedIndex); };

            Label l3 = MakeLabel("Масштаб интерфейса", Theme.FontLabel, Theme.Muted);
            l3.SetBounds(pad, y, w - pad * 2, Theme.D(18)); y += Theme.D(24);
            string[] names = new string[Scales.Length];
            for (int i = 0; i < Scales.Length; i++) names[i] = Scales[i] + "%";
            ChipGroup scales = new ChipGroup(names);
            scales.SelectedIndex = scaleIdx;
            scales.SetBounds(pad, y, w - pad * 2, Theme.D(36)); y += Theme.D(36) + Theme.D(18);
            Controls.Add(scales);
            scales.Changed += delegate { if (ScaleChanged != null) ScaleChanged(scales.SelectedIndex); };

            Label l4 = MakeLabel("Шрифт: " + FontLoader.Source, Theme.FontSmall, Theme.Muted);
            l4.SetBounds(pad, y, w - pad * 2, Theme.D(18)); y += Theme.D(18) + pad;

            ClientSize = new Size(w, y);
        }
    }

    // история
    class HistoryList : BaseCtl
    {
        public List<History.Entry> Items = new List<History.Entry>();
        int hoverRow = -1;
        bool hoverFolder;
        public Action Done;
        public int RowH { get { return Theme.D(54); } }

        Rectangle RowRect(int i) { return new Rectangle(0, i * RowH, Width, RowH); }
        Rectangle FolderRect(int i) { Rectangle r = RowRect(i); int s = Theme.D(34); return new Rectangle(r.Right - s - Theme.D(8), r.Y + (RowH - s) / 2, s, s); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(ParentBg); Theme.Hq(g);
            if (Items.Count == 0)
            {
                Theme.TextC(g, "Здесь появятся сохранённые сканы", Theme.FontBody, new Rectangle(0, 0, Width, Height), Theme.Muted);
                return;
            }
            TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            for (int i = 0; i < Items.Count; i++)
            {
                History.Entry it = Items[i];
                Rectangle r = RowRect(i);
                bool exists = File.Exists(it.Path);
                if (i == hoverRow) Theme.FillRound(g, Theme.HoverBg, new RectangleF(r.X + 0.5f, r.Y + 2.5f, r.Width - 1, r.Height - 5), Theme.D(12));
                int ic = Theme.D(36);
                RectangleF ib = new RectangleF(r.X + Theme.D(8), r.Y + (RowH - ic) / 2f, ic, ic);
                Theme.FillRound(g, Theme.SelBg, ib, Theme.D(10));
                string ext = Path.GetExtension(it.Path).ToLowerInvariant();
                Icons.Draw(g, ext == ".pdf" ? "file" : "image", exists ? Theme.Accent : Theme.Dim, ib.X + Theme.D(8), ib.Y + Theme.D(8), Theme.D(20), 1.8f);
                int tx = (int)ib.Right + Theme.D(12);
                int tw = FolderRect(i).X - tx - Theme.D(8);
                TextRenderer.DrawText(g, Path.GetFileName(it.Path), Theme.FontChip, new Rectangle(tx, r.Y + Theme.D(9), tw, Theme.D(20)), exists ? Theme.Text : Theme.Dim, fl);
                string sub = exists
                    ? it.Time.ToString("d MMMM, HH:mm", new System.Globalization.CultureInfo("ru-RU")) + (it.Pages > 1 ? " · стр.: " + it.Pages : "") + " · " + Path.GetFileName(Path.GetDirectoryName(it.Path))
                    : "Файл перемещён или удалён";
                TextRenderer.DrawText(g, sub, Theme.FontSmall, new Rectangle(tx, r.Y + Theme.D(30), tw, Theme.D(18)), Theme.Muted, fl);
                if (i == hoverRow && exists)
                {
                    Rectangle fr = FolderRect(i);
                    if (hoverFolder) Theme.FillRound(g, Theme.Card, new RectangleF(fr.X + 0.5f, fr.Y + 0.5f, fr.Width - 1, fr.Height - 1), Theme.D(10));
                    Icons.Draw(g, "folder", hoverFolder ? Theme.Accent : Theme.Muted, fr.X + Theme.D(8), fr.Y + Theme.D(8), Theme.D(18), 1.8f);
                }
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int row = e.Y / RowH;
            if (row < 0 || row >= Items.Count) row = -1;
            bool hf = row >= 0 && FolderRect(row).Contains(e.Location);
            if (row != hoverRow || hf != hoverFolder) { hoverRow = row; hoverFolder = hf; Invalidate(); }
            Cursor = row >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverRow = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int row = e.Y / RowH;
            if (row >= 0 && row < Items.Count && File.Exists(Items[row].Path))
            {
                try
                {
                    if (FolderRect(row).Contains(e.Location)) Process.Start("explorer.exe", "/select,\"" + Items[row].Path + "\"");
                    else Process.Start(Items[row].Path);
                }
                catch { }
                if (Done != null) Done();
            }
            base.OnMouseClick(e);
        }
    }

    class HistoryPopup : PopupForm
    {
        public HistoryPopup()
        {
            int pad = Theme.D(16), w = Theme.D(440);
            List<History.Entry> items = History.Load();
            if (items.Count > 10) items = items.GetRange(0, 10);
            Label title = MakeLabel("История сканов", Theme.FontBig, Theme.Text);
            title.SetBounds(pad + Theme.D(4), pad, w - pad * 2 - Theme.D(110), Theme.D(28));
            LinkLabel clear = new LinkLabel();
            clear.Text = "Очистить"; clear.Font = Theme.FontChip; clear.BackColor = Theme.Panel;
            clear.LinkColor = Theme.Accent; clear.ActiveLinkColor = Theme.AccentHover; clear.LinkBehavior = LinkBehavior.HoverUnderline;
            clear.TextAlign = ContentAlignment.MiddleRight; clear.AutoSize = false;
            clear.SetBounds(w - pad - Theme.D(110), pad, Theme.D(110), Theme.D(28));
            Controls.Add(clear);
            HistoryList list = new HistoryList();
            list.Items = items;
            int lh = Math.Max(1, items.Count) * list.RowH;
            if (items.Count == 0) lh = Theme.D(90);
            list.SetBounds(pad, pad + Theme.D(40), w - pad * 2, lh);
            Controls.Add(list);
            list.Done = delegate { Close(); };
            clear.LinkClicked += delegate { History.Clear(); list.Items = new List<History.Entry>(); list.Height = Theme.D(90); ClientSize = new Size(w, list.Bottom + pad); list.Invalidate(); };
            ClientSize = new Size(w, list.Bottom + pad);
        }
    }
}
