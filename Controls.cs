// Блик — элементы интерфейса (жидкое стекло)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace Blik
{
    // Базовый элемент: рисует себя в буфер с альфой поверх того, что под ним (см. Glass.Paint)
    class BaseCtl : Control, IAnimated, ISnapshot
    {
        protected bool hover;
        protected float hv;
        protected bool TrackHover = true;
        readonly GlassBuffer buf = new GlassBuffer();

        public BaseCtl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        }
        protected void Kick() { Anim.Kick(this); }
        public Bitmap Snapshot { get { return buf.Bmp; } }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { Glass.Paint(this, e, buf, Render); }
        protected virtual void Render(Graphics g) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) buf.Dispose();
            base.Dispose(disposing);
        }

        // бегущая строка для текста, который не влезает: края текста плавно растворяются
        protected float marq;
        int hoverTicks;
        bool marqNeeded;
        protected void ResetMarqueeFlag() { marqNeeded = false; }
        protected void DrawMarquee(Graphics g, string text, Font f, RectangleF r, Color c, bool animate, bool center)
        {
            if (string.IsNullOrEmpty(text) || r.Width <= 4) return;
            SizeF ms = Theme.MeasureF(g, text, f);
            if (ms.Width <= r.Width)
            {
                Theme.Write(g, text, f, r, c, center ? 1 : 0, true);
                return;
            }
            if (animate) { marqNeeded = true; Kick(); }
            GraphicsState gs = g.Save();
            try
            {
                g.SetClip(r, CombineMode.Intersect);
                float period = ms.Width + Theme.D(40);
                float off = animate ? marq % period : 0f;
                float y = r.Y + (r.Height - ms.Height) / 2f;
                float fw = Math.Min(Theme.D(28), r.Width / 3f);
                float fwN = fw / (r.Width + 2f);
                float lstr = animate ? Math.Min(1f, marq / fw) : 0f;
                using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), c, c, LinearGradientMode.Horizontal))
                {
                    ColorBlend cb = new ColorBlend(4);
                    cb.Colors = new Color[] { Theme.Fade(c, 1f - lstr), c, c, Theme.Fade(c, 0f) };
                    cb.Positions = new float[] { 0f, fwN, 1f - fwN, 1f };
                    lg.InterpolationColors = cb;
                    Theme.TextAtBrush(g, text, f, lg, r.X - off, y);
                    if (off > 0f) Theme.TextAtBrush(g, text, f, lg, r.X - off + period, y);
                }
            }
            finally { g.Restore(gs); }
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
        protected override void OnMouseEnter(EventArgs e) { if (TrackHover) { hover = true; Kick(); } base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { if (TrackHover) { hover = false; Kick(); } base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); Kick(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    }

    // Контейнер на стекле. Style: 0 — прозрачный, 1 — стеклянная карточка, 2 — разделитель справа
    class Surface : BaseCtl, ISurface
    {
        public int Style;
        public Padding CardInset = Padding.Empty;
        public float Radius = 20f;
        public Surface(int style) { Style = style; TrackHover = false; }

        public RectangleF CardRect
        {
            get
            {
                return new RectangleF(CardInset.Left + 0.5f, CardInset.Top + 0.5f,
                    Math.Max(1, Width - CardInset.Horizontal - 1), Math.Max(1, Height - CardInset.Vertical - 1));
            }
        }

        public void PaintSurface(Graphics g)
        {
            if (Style == 1) Theme.GlassRect(g, CardRect, Theme.D(Radius), Theme.Raised, Theme.Stroke);
            else if (Style == 2)
                using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(0, 0, 2, Math.Max(2, Height)), Theme.Stroke, Color.FromArgb(0, Theme.Stroke), LinearGradientMode.Vertical))
                using (Pen p = new Pen(lg, 1f)) g.DrawLine(p, Width - 1, Theme.D(6), Width - 1, Height - Theme.D(18));
        }

        // обычный Control не учитывает Padding при раскладке Dock — учитываем сами
        public override Rectangle DisplayRectangle
        {
            get
            {
                Rectangle r = ClientRectangle;
                Padding p = Padding;
                return new Rectangle(r.X + p.Left, r.Y + p.Top, Math.Max(0, r.Width - p.Horizontal), Math.Max(0, r.Height - p.Vertical));
            }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(true); }
    }

    // Размытый снимок окна под всплывающими окнами (настройки, история)
    class BlurOverlay : BaseCtl
    {
        Bitmap sharp, blurred;
        float t, target = 1f;
        bool removing;

        public BlurOverlay(Bitmap snap)
        {
            TrackHover = false;
            sharp = snap;
            blurred = WindowShot.Blur(snap, 5);
            Kick();
        }

        public void Show(bool on) { target = on ? 1f : 0f; removing = false; Kick(); }

        protected override bool ExtraStep()
        {
            float n = Anim.To(t, target, 0.3f);
            bool changed = n != t;
            t = n;
            if (changed) Invalidate();
            if (t <= 0f && target <= 0f && !removing)
            {
                removing = true;
                Control p = Parent;
                if (p != null && p.IsHandleCreated)
                    p.BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed || !removing) return;
                        if (Parent != null) Parent.Controls.Remove(this);
                        Dispose();
                    });
                return false;
            }
            return t != target;
        }

        static void DrawAlpha(Graphics g, Bitmap b, Rectangle r, float a)
        {
            ColorMatrix cm = new ColorMatrix();
            cm.Matrix33 = a;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(b, r, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
            }
        }

        protected override void Render(Graphics g)
        {
            if (sharp == null) return;
            Rectangle r = new Rectangle(0, 0, sharp.Width, sharp.Height);
            g.CompositingMode = CompositingMode.SourceCopy;
            if (t >= 0.99f) g.DrawImage(blurred, r, 0, 0, blurred.Width, blurred.Height, GraphicsUnit.Pixel);
            else
            {
                DrawAlpha(g, sharp, r, 1f - t);
                g.CompositingMode = CompositingMode.SourceOver;
                DrawAlpha(g, blurred, r, t);
            }
            g.CompositingMode = CompositingMode.SourceOver;
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(40 * t), 0, 0, 0))) g.FillRectangle(b, 0, 0, Width, Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (sharp != null) { sharp.Dispose(); sharp = null; }
                if (blurred != null) { blurred.Dispose(); blurred = null; }
            }
            base.Dispose(disposing);
        }
    }

    // Надпись на стекле (обычный Label рисует через GDI и на стекле становится прозрачным)
    class GlassLabel : BaseCtl
    {
        public Color Color;
        public int Align;
        public bool Wrap;
        public GlassLabel(string text, Font f, Color c) { Text = text; Font = f; Color = c; TrackHover = false; }
        protected override void Render(Graphics g)
        {
            RectangleF r = new RectangleF(0, 0, Width, Height);
            if (Wrap) Theme.TextWrap(g, Text, Font, r, Color);
            else Theme.Write(g, Text, Font, r, Color, Align, true);
        }
    }

    // Текстовая ссылка
    class GlassLink : BaseCtl
    {
        public int Align = 2;
        public GlassLink(string text, Font f) { Text = text; Font = f; Cursor = Cursors.Hand; }
        protected override void Render(Graphics g)
        {
            Color c = Theme.Mix(Theme.Text2, Theme.Text, hv);
            RectangleF r = new RectangleF(0, 0, Width, Height);
            Theme.Write(g, Text, Font, r, c, Align, true);
            if (hv > 0.02f)
            {
                SizeF s = Theme.MeasureF(g, Text, Font);
                float x = Align == 2 ? Width - s.Width : (Align == 1 ? (Width - s.Width) / 2f : 0f);
                float y = Height / 2f + s.Height / 2f;
                using (Pen p = new Pen(Theme.Fade(c, hv * 0.7f), 1f)) g.DrawLine(p, x, y, x + s.Width, y);
            }
        }
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
        protected override void Render(Graphics g)
        {
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float t = hv;
            bool close = WindowButton && Kind == "close";
            if (t > 0.01f && Enabled)
            {
                float rad = Theme.D(WindowButton ? 8 : 12);
                if (close) Theme.FillRound(g, Color.FromArgb((int)(220 * t), 0xc4, 0x2b, 0x3a), r, rad);
                else
                {
                    Theme.FillRound(g, Theme.Fade(Theme.Hover, t), r, rad);
                    if (!WindowButton) Theme.Edge(g, r, rad, Theme.Fade(Theme.StrokeStrong, t), Theme.Fade(Theme.Stroke, t * 0.4f));
                }
            }
            Color c;
            if (!Enabled) c = Theme.Dim;
            else if (close) c = Theme.Mix(Theme.Muted, Color.White, t);
            else c = Theme.Mix(Theme.Muted, Theme.Text, t);
            Icons.Draw(g, Kind, c, (Width - IconPx) / 2f, (Height - IconPx) / 2f, IconPx, WindowButton ? 2.2f : 1.8f);
        }
    }

    // Сегментированный переключатель (как «Board / List» на референсе)
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
            float pad = Theme.D(3);
            float w = (Width - 1 - pad * 2) / Items.Length;
            return new RectangleF(pad + i * w + 0.5f, pad + 0.5f, w, Height - 1 - pad * 2);
        }
        int HitIndex(Point p)
        {
            for (int i = 0; i < Items.Length; i++) if (Cell(i).Contains(p)) return i;
            return -1;
        }
        protected override void Render(Graphics g)
        {
            RectangleF tr = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float rad = Math.Min(tr.Height / 2f, Theme.D(14));
            Theme.FillRound(g, Theme.Track, tr, rad);
            Theme.StrokeRound(g, Color.FromArgb(18, 190, 205, 255), 1f, tr, rad);
            ResetMarqueeFlag();
            for (int i = 0; i < Items.Length; i++)
            {
                RectangleF r = Cell(i);
                float cr = Math.Max(2f, rad - Theme.D(3));
                float s = selT[i];
                if (hvs[i] > 0.01f && s < 0.99f) Theme.FillRound(g, Theme.Fade(Theme.Hover, hvs[i] * (1f - s)), r, cr);
                if (s > 0.01f)
                {
                    Theme.SelGlass(g, r, cr, s);
                }
                Color tc = !Enabled ? Theme.Dim : Theme.Mix(Theme.Mix(Theme.Muted, Theme.Text2, hvs[i]), Theme.Text, s);
                RectangleF txt = r; txt.Inflate(-Theme.D(8), 0);
                DrawMarquee(g, Items[i], Theme.FontChip, txt, tc, hover && i == hoverIdx, true);
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
        protected override void Render(Graphics g)
        {
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float rad = Theme.D(14);
            if (hv > 0.01f)
            {
                Theme.FillRound(g, Theme.Fade(Theme.Hover, hv), r, rad);
                Theme.Edge(g, r, rad, Theme.Fade(Theme.StrokeStrong, hv), Theme.Fade(Theme.Stroke, hv * 0.3f));
            }
            int sw = Theme.D(40), sh = Theme.D(22);
            int pad = Theme.D(14);
            int textW = Width - pad * 3 - sw;
            ResetMarqueeFlag();
            int ty = (Height - Theme.D(36)) / 2;
            DrawMarquee(g, Title, Theme.FontChip, new RectangleF(pad, ty, textW, Theme.D(19)), Enabled ? Theme.Text : Theme.Dim, hover, false);
            string sub = ShowDisabledSub && DisabledSub != null ? DisabledSub : Sub;
            DrawMarquee(g, sub, Theme.FontSmall, new RectangleF(pad, ty + Theme.D(19), textW, Theme.D(17)), Enabled ? Theme.Muted : Theme.Dim, hover, false);

            RectangleF s = new RectangleF(Width - pad - sw, (Height - sh) / 2f, sw, sh);
            Color track = Enabled ? Theme.Mix(Theme.Inset, Theme.Blue, knob) : Theme.Track;
            Theme.FillRound(g, track, s, sh / 2f);
            Theme.StrokeRound(g, Theme.Fade(Theme.StrokeStrong, 1f - knob * 0.6f), 1f, s, sh / 2f);
            float k = sh - Theme.D(6);
            float kx = s.X + Theme.D(3) + (s.Width - Theme.D(6) - k) * knob;
            Color kc = !Enabled ? Theme.Dim : Theme.Mix(Color.FromArgb(255, 150, 160, 184), Color.White, knob);
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

    // Кнопка-пилюля: главная — белая (как «New loan»), обычная — стеклянная
    class PillButton : BaseCtl
    {
        public bool Primary;
        public string IconKind;
        public bool IconOnly;
        public PillButton(string text, bool primary, string icon)
        {
            Text = text; Primary = primary; IconKind = icon; Font = Theme.FontBtn; Cursor = Cursors.Hand;
        }
        protected override void Render(Graphics g)
        {
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float rad = Height / 2f;
            Color tc;
            if (Primary)
            {
                Color fill = !Enabled ? Color.FromArgb(70, 255, 255, 255) : Theme.Mix(Theme.Accent, Theme.AccentHover, hv);
                Theme.FillRound(g, fill, r, rad);
                tc = Enabled ? Theme.AccentInk : Color.FromArgb(255, 60, 60, 64);
            }
            else
            {
                if (hv > 0.01f) Theme.FillRound(g, Theme.Fade(Theme.Hover, hv), r, rad);
                Theme.Edge(g, r, rad, Theme.Mix(Theme.StrokeStrong, Color.FromArgb(110, 210, 222, 255), hv), Theme.Stroke);
                tc = Enabled ? Theme.Text : Theme.Dim;
            }
            int iconPx = IconKind != null ? Theme.D(17) : 0;
            if (IconOnly)
            {
                Icons.Draw(g, IconKind, tc, (Width - iconPx) / 2f, (Height - iconPx) / 2f, iconPx, 2f);
                return;
            }
            SizeF ts = Theme.MeasureF(g, Text, Font);
            int gap = IconKind != null ? Theme.D(9) : 0;
            float total = ts.Width + iconPx + gap;
            float x0 = Math.Max(Theme.D(10), (Width - total) / 2f);
            if (IconKind != null) Icons.Draw(g, IconKind, tc, x0, (Height - iconPx) / 2f, iconPx, 2f);
            Theme.Write(g, Text, Font, new RectangleF(x0 + iconPx + gap, 0, Width - x0 - iconPx - gap - Theme.D(6), Height), tc, 0, true);
        }
    }

    // ---------------------------------------------------------------- вкладки
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
            float pad = Theme.D(3);
            float w = (Width - 1 - pad * 2) / Items.Length;
            return new RectangleF(pad + i * w + 0.5f, pad + 0.5f, w, Height - 1 - pad * 2);
        }
        protected override void Render(Graphics g)
        {
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, Theme.Track, r, r.Height / 2f);
            Theme.StrokeRound(g, Color.FromArgb(18, 190, 205, 255), 1f, r, r.Height / 2f);
            RectangleF ind = Cell(pos);
            Theme.SelGlass(g, ind, ind.Height / 2f, 1f);
            for (int i = 0; i < Items.Length; i++)
            {
                float d = Math.Min(1f, Math.Abs(pos - i));
                Color c = Theme.Mix(Theme.Text, i == hoverIdx ? Theme.Text2 : Theme.Muted, d);
                Theme.TextC(g, Items[i], Theme.FontChip, Cell(i), c);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = -1;
            for (int k = 0; k < Items.Length; k++) if (Cell(k).Contains(e.Location)) i = k;
            if (i != hoverIdx) { hoverIdx = i; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIdx = -1; Invalidate(); base.OnMouseLeave(e); }
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

    // ---------------------------------------------------------------- заголовок окна
    class TitleBar : BaseCtl
    {
        readonly MainForm form;
        string device = "";
        int state; // 0 — ищем, 1 — подключён, 2 — не найден
        public readonly IconButton Gear;
        readonly IconButton bMin, bMax, bClose;
        public Action DeviceClicked;
        RectangleF chipRect;
        bool chipHover;

        public TitleBar(MainForm f)
        {
            form = f; TrackHover = false;
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

        public void SetDevice(string text, int st) { device = text; state = st; Invalidate(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (bClose == null) return;
            int w = Theme.D(42), h = Theme.D(34), y = (Height - h) / 2;
            int x = Width - Theme.D(10) - w;
            bClose.SetBounds(x, y, w, h); x -= w + Theme.D(2);
            bMax.SetBounds(x, y, w, h); x -= w + Theme.D(2);
            bMin.SetBounds(x, y, w, h); x -= w + Theme.D(12);
            Gear.SetBounds(x, y, w, h);
        }

        protected override void Render(Graphics g)
        {
            float cy = Height / 2f;
            float x = Theme.D(16);
            // плитка со знаком — как логотип в углу референса
            float tile = Theme.D(32);
            RectangleF tr = new RectangleF(x + 0.5f, cy - tile / 2f + 0.5f, tile - 1, tile - 1);
            Theme.FillRound(g, Color.FromArgb(150, 4, 8, 20), tr, Theme.D(10));
            Theme.Edge(g, tr, Theme.D(10), Color.FromArgb(110, 210, 222, 255), Theme.EdgeBottom);
            float lp = Theme.D(18);
            Icons.Logo(g, Theme.Text, tr.X + (tr.Width - lp) / 2f, tr.Y + (tr.Height - lp) / 2f, lp);
            x += tile + Theme.D(12);
            string word = "Blick";
            SizeF ws = Theme.MeasureF(g, word, Theme.FontLogo);
            Theme.TextAt(g, word, Theme.FontLogo, x, cy - ws.Height / 2f, Theme.Text);
            x += ws.Width + Theme.D(18);

            // строка устройства
            SizeF ds = Theme.MeasureF(g, device, Theme.FontSmall);
            float ch = Theme.D(28);
            float maxW = Gear.Left - Theme.D(16) - x;
            RectangleF chip = new RectangleF(x + 0.5f, cy - ch / 2f + 0.5f, Math.Min(ds.Width + Theme.D(38), Math.Max(Theme.D(60), maxW)), ch - 1);
            if (chipHover) Theme.FillRound(g, Theme.Hover, chip, ch / 2f);
            Theme.Edge(g, chip, ch / 2f, chipHover ? Theme.StrokeStrong : Theme.Stroke, Theme.EdgeBottom);
            Color dot = state == 1 ? Theme.Blue : (state == 2 ? Theme.Error : Theme.Muted);
            if (state == 1) Theme.Dot(g, Color.FromArgb(70, Theme.Blue), chip.X + Theme.D(14), cy, Theme.D(13));
            Theme.Dot(g, dot, chip.X + Theme.D(14), cy, Theme.D(6));
            Theme.Write(g, device, Theme.FontSmall, new RectangleF(chip.X + Theme.D(26), chip.Y, chip.Width - Theme.D(34), ch), state == 1 ? Theme.Text2 : Theme.Muted, 0, true);
            chipRect = chip;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = chipRect.Contains(e.Location);
            Cursor = h ? Cursors.Hand : Cursors.Default;
            if (h != chipHover) { chipHover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (chipHover) { chipHover = false; Invalidate(); }
            base.OnMouseLeave(e);
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

    // ---------------------------------------------------------------- шапка над предпросмотром
    class HeaderCtl : BaseCtl
    {
        public string InfoA = "—", InfoB = "—";
        public HeaderCtl() { TrackHover = false; }
        protected override void Render(Graphics g)
        {
            float x = Width - 1;
            string[] chips = new string[] { InfoB, InfoA };
            float h = Theme.D(28), y = Height - h - Theme.D(6);
            foreach (string t in chips)
            {
                SizeF s = Theme.MeasureF(g, t, Theme.FontSmall);
                float w = s.Width + Theme.D(26);
                x -= w;
                RectangleF r = new RectangleF(x + 0.5f, y + 0.5f, w - 1, h - 1);
                Theme.FillRound(g, Color.FromArgb(10, 255, 255, 255), r, h / 2f);
                Theme.StrokeRound(g, Theme.Stroke, 1f, r, h / 2f);
                Theme.TextC(g, t, Theme.FontSmall, r, Theme.Text2);
                x -= Theme.D(8);
            }
            float tw = x - Theme.D(12);
            Theme.Write(g, L.T("Предпросмотр", "Preview"), Theme.FontBig, new RectangleF(0, y - Theme.D(2), tw, h + Theme.D(4)), Theme.Text, 0, true);
        }
    }

    // ---------------------------------------------------------------- карточка «куда сохранять»
    class SaveCard : BaseCtl
    {
        public string FolderName = "";
        public string Status = "";
        public int StatusKind; // 0 обычный, 1 сохранено, 2 ошибка
        public bool Openable;
        public Action ChangeFolder;
        public Action OpenSaved;
        RectangleF linkRect, statusRect;
        bool linkHover;
        float flash;

        public SaveCard() { TrackHover = false; }
        public void Flash() { flash = 1f; Kick(); }
        protected override bool ExtraStep()
        {
            if (flash <= 0f) return false;
            flash = Math.Max(0f, flash - 0.012f);
            return flash > 0f;
        }

        protected override void Render(Graphics g)
        {
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float rad = Theme.D(14);
            Theme.FillRound(g, Theme.Inset, r, rad);
            if (flash > 0f) Theme.FillRound(g, Color.FromArgb((int)(40 * flash), Theme.Blue), r, rad);
            Theme.Edge(g, r, rad, Theme.Mix(Theme.Stroke, Theme.StrokeStrong, flash), Theme.EdgeBottom);
            int pad = Theme.D(14), ip = Theme.D(17);
            float row = Theme.D(12);
            Icons.Draw(g, "folder", Theme.Text2, pad, row + Theme.D(1), ip, 1.7f);
            string link = L.T("Изменить", "Change");
            SizeF lsz = Theme.MeasureF(g, link, Theme.FontChip);
            linkRect = new RectangleF(Width - pad - lsz.Width, row, lsz.Width, Theme.D(20));
            Color lc = linkHover ? Theme.Text : Theme.Text2;
            Theme.Write(g, link, Theme.FontChip, linkRect, lc, 0, true);
            if (linkHover)
                using (Pen p = new Pen(Theme.Fade(lc, 0.7f))) g.DrawLine(p, linkRect.X, linkRect.Bottom - Theme.D(1), linkRect.Right, linkRect.Bottom - Theme.D(1));
            float nx = pad + ip + Theme.D(8);
            Theme.Write(g, FolderName, Theme.FontChip, new RectangleF(nx, row, linkRect.X - nx - Theme.D(8), Theme.D(20)), Theme.Text, 0, true);
            float sx = pad;
            float sy = Theme.D(38);
            if (flash > 0f && StatusKind == 1)
            {
                float pop = flash > 0.85f ? (1f - flash) / 0.15f : 1f;
                float d = Theme.D(16) * (0.6f + 0.4f * pop);
                float ccx = pad + Theme.D(8), ccy = sy + Theme.D(8);
                Theme.Dot(g, Theme.Fade(Theme.Accent, Math.Min(1f, flash * 2f)), ccx, ccy, d);
                Icons.Draw(g, "check", Theme.AccentInk, ccx - d * 0.35f, ccy - d * 0.35f, d * 0.7f, 3f);
                sx = pad + Theme.D(24);
            }
            statusRect = new RectangleF(sx, sy, Width - sx - pad, Height - sy - Theme.D(8));
            Color sc = StatusKind == 2 ? Theme.Error : (StatusKind == 1 ? Theme.Text2 : Theme.Muted);
            Theme.TextWrap(g, Status, Theme.FontSmall, statusRect, sc);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool lh = linkRect.Contains(e.Location);
            bool hand = lh || (Openable && statusRect.Contains(e.Location));
            Cursor = hand ? Cursors.Hand : Cursors.Default;
            if (lh != linkHover) { linkHover = lh; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            if (linkHover) { linkHover = false; Invalidate(); }
            base.OnMouseLeave(e);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (linkRect.Contains(e.Location)) { if (ChangeFolder != null) ChangeFolder(); }
            else if (Openable && statusRect.Contains(e.Location)) { if (OpenSaved != null) OpenSaved(); }
            base.OnMouseClick(e);
        }
    }

    // ---------------------------------------------------------------- подсказки в тёмном стиле
    static class Tips
    {
        public static ToolTip Make()
        {
            ToolTip t = new ToolTip();
            t.OwnerDraw = true;
            t.Popup += delegate(object s, PopupEventArgs e)
            {
                string text = t.GetToolTip(e.AssociatedControl);
                Size sz = TextRenderer.MeasureText(text, Theme.FontSmall);
                e.ToolTipSize = new Size(sz.Width + Theme.D(20), sz.Height + Theme.D(12));
            };
            t.Draw += delegate(object s, DrawToolTipEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 14, 20, 36))) e.Graphics.FillRectangle(b, e.Bounds);
                using (Pen p = new Pen(Color.FromArgb(255, 44, 56, 84))) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, Theme.FontSmall, e.Bounds, Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            return t;
        }
    }

    // ---------------------------------------------------------------- всплывающие окна (тоже стекло)
    class PopupForm : GlassWindow
    {
        public bool SuppressClose;
        public PopupForm()
        {
            FullFrame = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
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
        protected GlassLabel MakeLabel(string text, Font f, Color c)
        {
            GlassLabel l = new GlassLabel(text, f, c);
            Controls.Add(l);
            return l;
        }
    }

    class SettingsPopup : PopupForm
    {
        public static readonly int[] Scales = new int[] { 75, 90, 100, 110, 120 };
        public Action<int> LangChanged;
        public Action<int> ScaleChanged;

        public SettingsPopup(int scaleIdx)
        {
            int pad = Theme.D(20), w = Theme.D(420);
            int y = pad;
            GlassLabel title = MakeLabel(L.T("Настройки", "Settings"), Theme.FontBig, Theme.Text);
            title.SetBounds(pad, y, w - pad * 2, Theme.D(28)); y += Theme.D(42);

            GlassLabel l1 = MakeLabel(L.T("Язык интерфейса", "Language"), Theme.FontLabel, Theme.Muted);
            l1.SetBounds(pad, y, w - pad * 2, Theme.D(18)); y += Theme.D(24);
            ChipGroup langs = new ChipGroup("Русский", "English");
            langs.SelectedIndex = L.En ? 1 : 0;
            langs.SetBounds(pad, y, w - pad * 2, Theme.D(36)); y += Theme.D(36) + Theme.D(20);
            Controls.Add(langs);
            langs.Changed += delegate { if (LangChanged != null) LangChanged(langs.SelectedIndex); };

            GlassLabel l2 = MakeLabel(L.T("Масштаб интерфейса", "Interface scale"), Theme.FontLabel, Theme.Muted);
            l2.SetBounds(pad, y, w - pad * 2, Theme.D(18)); y += Theme.D(24);
            string[] names = new string[Scales.Length];
            for (int i = 0; i < Scales.Length; i++) names[i] = Scales[i] + "%";
            ChipGroup scales = new ChipGroup(names);
            scales.SelectedIndex = scaleIdx;
            scales.SetBounds(pad, y, w - pad * 2, Theme.D(36)); y += Theme.D(36) + pad;
            Controls.Add(scales);
            scales.Changed += delegate { if (ScaleChanged != null) ScaleChanged(scales.SelectedIndex); };

            Size = new Size(w, y);
        }
    }

    // ---------------------------------------------------------------- история
    class HistoryList : BaseCtl
    {
        public List<History.Entry> Items = new List<History.Entry>();
        int hoverRow = -1;
        bool hoverFolder;
        public Action Done;
        public int RowH { get { return Theme.D(54); } }

        public HistoryList() { TrackHover = false; }
        Rectangle RowRect(int i) { return new Rectangle(0, i * RowH, Width, RowH); }
        Rectangle FolderRect(int i) { Rectangle r = RowRect(i); int s = Theme.D(34); return new Rectangle(r.Right - s - Theme.D(8), r.Y + (RowH - s) / 2, s, s); }

        protected override void Render(Graphics g)
        {
            if (Items.Count == 0)
            {
                Theme.TextC(g, L.T("Здесь появятся сохранённые сканы", "Saved scans will appear here"), Theme.FontBody, new RectangleF(0, 0, Width, Height), Theme.Muted);
                return;
            }
            System.Globalization.CultureInfo ci = L.Culture;
            for (int i = 0; i < Items.Count; i++)
            {
                History.Entry it = Items[i];
                Rectangle r = RowRect(i);
                bool exists = File.Exists(it.Path);
                if (i == hoverRow) Theme.FillRound(g, Theme.Hover, new RectangleF(r.X + 0.5f, r.Y + 2.5f, r.Width - 1, r.Height - 5), Theme.D(12));
                int ic = Theme.D(36);
                RectangleF ib = new RectangleF(r.X + Theme.D(8), r.Y + (RowH - ic) / 2f, ic, ic);
                Theme.FillRound(g, Theme.Inset, ib, Theme.D(10));
                Theme.StrokeRound(g, Theme.Stroke, 1f, ib, Theme.D(10));
                string ext = Path.GetExtension(it.Path).ToLowerInvariant();
                Icons.Draw(g, ext == ".pdf" ? "file" : "image", exists ? Theme.Text : Theme.Dim, ib.X + Theme.D(8), ib.Y + Theme.D(8), Theme.D(20), 1.7f);
                float tx = ib.Right + Theme.D(12);
                float tw = FolderRect(i).X - tx - Theme.D(8);
                Theme.Write(g, Path.GetFileName(it.Path), Theme.FontChip, new RectangleF(tx, r.Y + Theme.D(8), tw, Theme.D(20)), exists ? Theme.Text : Theme.Dim, 0, true);
                string sub = exists
                    ? it.Time.ToString(L.En ? "MMMM d, HH:mm" : "d MMMM, HH:mm", ci) + (it.Pages > 1 ? L.T(" · стр.: ", " · pages: ") + it.Pages : "") + " · " + Path.GetFileName(Path.GetDirectoryName(it.Path))
                    : L.T("Файл перемещён или удалён", "File was moved or deleted");
                Theme.Write(g, sub, Theme.FontSmall, new RectangleF(tx, r.Y + Theme.D(29), tw, Theme.D(18)), Theme.Muted, 0, true);
                if (i == hoverRow && exists)
                {
                    Rectangle fr = FolderRect(i);
                    if (hoverFolder) Theme.FillRound(g, Theme.Hover, new RectangleF(fr.X + 0.5f, fr.Y + 0.5f, fr.Width - 1, fr.Height - 1), Theme.D(10));
                    Icons.Draw(g, "folder", hoverFolder ? Theme.Text : Theme.Muted, fr.X + Theme.D(8), fr.Y + Theme.D(8), Theme.D(18), 1.7f);
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
            GlassLabel title = MakeLabel(L.T("История сканов", "Scan history"), Theme.FontBig, Theme.Text);
            title.SetBounds(pad + Theme.D(4), pad, w - pad * 2 - Theme.D(120), Theme.D(28));
            GlassLink clear = new GlassLink(L.T("Очистить", "Clear"), Theme.FontChip);
            clear.SetBounds(w - pad - Theme.D(110), pad, Theme.D(110), Theme.D(28));
            Controls.Add(clear);
            HistoryList list = new HistoryList();
            list.Items = items;
            int lh = Math.Max(1, items.Count) * list.RowH;
            if (items.Count == 0) lh = Theme.D(90);
            list.SetBounds(pad, pad + Theme.D(40), w - pad * 2, lh);
            Controls.Add(list);
            list.Done = delegate { Close(); };
            clear.Click += delegate
            {
                History.Clear(); list.Items = new List<History.Entry>(); list.Height = Theme.D(90);
                Size = new Size(w, list.Bottom + pad); list.Invalidate();
            };
            Size = new Size(w, list.Bottom + pad);
        }
    }
}
