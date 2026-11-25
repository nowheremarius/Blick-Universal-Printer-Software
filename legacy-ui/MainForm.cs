using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Blick
{
    class MainForm : Form
    {
        static readonly int[] Dpis = new int[] { 150, 300, 600, 1200 };
        static readonly int[] TimerSecs = new int[] { 5, 10, 15 };
        static readonly CultureInfo Ru = new CultureInfo("ru-RU");

        readonly Settings st = Settings.Load();
        readonly List<Page> pages = new List<Page>();
        int sel = -1;
        Bitmap previewScan;
        string docPath, lastSaved;
        string statusText;
        int statusKind;
        bool busy, busyPreview;
        RectangleF cropN = RectangleF.Empty;
        object shownRef;
        ScanParams lastParams;
        List<Page> lastAdded = new List<Page>();
        int duplexPhase, duplexStart, duplexFronts;
        bool duplexReverse;
        bool series;
        int countdown;
        readonly System.Windows.Forms.Timer secTimer = new System.Windows.Forms.Timer();
        bool collapsed, autoCollapsed;
        PopupForm popup;
        string deviceText = "Ищу сканер…";
        bool deviceOk;

        TitleBar title;
        Panel rail, right, center, basicPage, extraPage;
        HeaderCtl header;
        PreviewPanel preview;
        ThumbStrip thumbs;
        TabBar tabs;
        IconButton bCollapse, bExpand, cPreview;
        PillButton cScan;
        ChipGroup cPreset, cSource, cColor, cDpi, cArea, cFormat, cTimerSec;
        ChipGroup[] basicChips;
        Label[] basicLabels;
        SwitchRow sMerge, sEnhance, sOcr, sSplit, sDuplex, sTimer;
        SwitchRow[] switches;
        SaveCard card;
        PillButton bPreview, bScan;
        IconButton iZoomIn, iZoomOut, iFit, iRotate, iRepeat, iHistory, iTrash;
        ToolTip tips;

        static int D(float v) { return Theme.D(v); }

        public MainForm()
        {
            using (Graphics g = CreateGraphics()) Theme.Dpi = g.DpiX / 96f;
            st.ScaleIdx = Clamp(st.ScaleIdx, 0, SettingsPopup.Scales.Length - 1);
            Theme.Ui = SettingsPopup.Scales[st.ScaleIdx] / 100f;
            Theme.SetPalette(st.ThemeKind);
            Theme.SetAccent(ParseHex(st.Accent, Theme.DefaultAccent));
            Theme.MakeFonts();

            Text = "Blick";
            FormBorderStyle = FormBorderStyle.None;
            DoubleBuffered = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(D(1240), wa.Width), Math.Min(D(800), wa.Height));
            ApplyMinSize();
            collapsed = st.Collapsed;
            secTimer.Interval = 1000;
            secTimer.Tick += delegate { OnSecond(); };

            BuildUi();
            ApplySettings();
            RefreshAll();
            Shown += delegate { DetectDevice(); EndComposited(); };
            FormClosing += delegate { series = false; secTimer.Stop(); st.Save(); };
        }

        static int Clamp(int v, int a, int b) { return v < a ? a : (v > b ? b : v); }

        void ApplyMinSize()
        {
            Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
            MinimumSize = new Size(Math.Min(D(860), wa.Width), Math.Min(D(600), wa.Height));
        }

        // окно без рамки
        // Окну оставлены стили обычного окна (заголовок + рамка), чтобы Windows
        // проигрывала родные анимации открытия/сворачивания/разворачивания и работал Snap.
        // Саму рамку и заголовок убираем в WM_NCCALCSIZE — вся площадь окна остаётся клиентской.
        const int WS_CAPTION = 0xC00000, WS_THICKFRAME = 0x40000, WS_SYSMENU = 0x80000,
                  WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000, WS_EX_COMPOSITED = 0x02000000;

        // При открытии всё окно рисуется одним кадром (WS_EX_COMPOSITED),
        // поэтому боковые панели не появляются с задержкой. После показа режим снимается.
        bool composited = true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                if (composited) cp.ExStyle |= WS_EX_COMPOSITED;
                return cp;
            }
        }

        void EndComposited()
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 600; // дожидаемся конца анимации открытия
            t.Tick += delegate
            {
                t.Stop(); t.Dispose();
                if (!composited || IsDisposed) return;
                composited = false;
                try
                {
                    int ex = NativeMethods.GetWindowLong(Handle, -20);
                    NativeMethods.SetWindowLong(Handle, -20, ex & ~WS_EX_COMPOSITED);
                    // NOSIZE | NOMOVE | NOZORDER | NOACTIVATE | FRAMECHANGED
                    NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
                }
                catch { }
            };
            t.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int pref = 2; NativeMethods.DwmSetWindowAttribute(Handle, 33, ref pref, 4); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.LineSoft)) e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
            if (right == null) return;
            if (!collapsed && ClientSize.Width < D(1080)) { autoCollapsed = true; SetCollapsed(true, false); }
            else if (collapsed && autoCollapsed && ClientSize.Width >= D(1140)) { autoCollapsed = false; SetCollapsed(false, false); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x83) // WM_NCCALCSIZE — без заголовка и рамки
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
            if (m.Msg == 0x84 && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1)
                {
                    long lp = m.LParam.ToInt64();
                    Point p = PointToClient(new Point((short)(lp & 0xffff), (short)((lp >> 16) & 0xffff)));
                    int gsz = D(6);
                    bool l = p.X < gsz, r = p.X >= ClientSize.Width - gsz, t = p.Y < gsz, b = p.Y >= ClientSize.Height - gsz;
                    if (t && l) m.Result = (IntPtr)13;
                    else if (t && r) m.Result = (IntPtr)14;
                    else if (b && l) m.Result = (IntPtr)16;
                    else if (b && r) m.Result = (IntPtr)17;
                    else if (l) m.Result = (IntPtr)10;
                    else if (r) m.Result = (IntPtr)11;
                    else if (t) m.Result = (IntPtr)12;
                    else if (b) m.Result = (IntPtr)15;
                }
                return;
            }
            base.WndProc(ref m);
        }

        public void ToggleMax()
        {
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            else WindowState = FormWindowState.Maximized;
        }

        // построение интерфейса
        void BuildUi()
        {
            SuspendLayout();
            List<Control> old = new List<Control>();
            foreach (Control c in Controls) old.Add(c);
            Controls.Clear();
            foreach (Control c in old) c.Dispose();
            if (tips != null) tips.Dispose();
            tips = new ToolTip();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Padding = new Padding(D(5));

            center = new Panel();
            center.Dock = DockStyle.Fill; center.BackColor = Theme.Bg;
            center.Padding = new Padding(D(32), D(22), D(32), D(18));
            preview = new PreviewPanel(); preview.Dock = DockStyle.Fill;
            Panel spB = new Panel(); spB.Dock = DockStyle.Bottom; spB.Height = D(14); spB.BackColor = Theme.Bg;
            thumbs = new ThumbStrip(); thumbs.Dock = DockStyle.Bottom; thumbs.Height = D(64);
            Panel spT = new Panel(); spT.Dock = DockStyle.Top; spT.Height = D(14); spT.BackColor = Theme.Bg;
            header = new HeaderCtl(); header.Dock = DockStyle.Top; header.Height = D(44);
            center.Controls.Add(preview);
            center.Controls.Add(spB);
            center.Controls.Add(thumbs);
            center.Controls.Add(spT);
            center.Controls.Add(header);

            rail = new Panel(); rail.Dock = DockStyle.Left; rail.Width = D(76); rail.BackColor = Theme.Bg;
            rail.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.LineSoft)) e.Graphics.DrawLine(p, rail.Width - 1, 0, rail.Width - 1, rail.Height);
            };
            iZoomIn = Tool("zoomin", "Увеличить (колесо мыши)");
            iZoomOut = Tool("zoomout", "Уменьшить");
            iFit = Tool("fit", "Вместить страницу в окно");
            iRotate = Tool("rotate", "Повернуть страницу на 90°");
            iRepeat = Tool("repeat", "Заменить последний скан (Ctrl+R)");
            iHistory = Tool("history", "История сканов");
            iTrash = Tool("trash", "Начать новый документ. Файлы останутся в папке.");
            rail.Resize += delegate { LayoutRail(); };

            BuildRight();

            title = new TitleBar(this); title.Dock = DockStyle.Top; title.Height = D(52);
            title.SetDevice(deviceText, deviceOk);
            tips.SetToolTip(title.Gear, "Настройки: цвет, тема, масштаб");

            Controls.Add(center);
            Controls.Add(right);
            Controls.Add(rail);
            Controls.Add(title);

            // события
            title.Gear.Click += delegate { ShowSettings(); };
            title.DeviceClicked = delegate { if (!busy) { deviceText = "Ищу сканер…"; deviceOk = false; title.SetDevice(deviceText, false); DetectDevice(); } };
            preview.SelChanged = OnSelChanged;
            preview.CropApply = ApplyPageCrop;
            thumbs.PageDeleted = DeletePage;
            thumbs.PageMoved = MovePage;
            thumbs.AddClicked = delegate { OnScanButton(); };
            thumbs.PageClicked = delegate(int i) { if (busy) return; sel = i; preview.ResetView(); RefreshAll(); };
            iZoomIn.Click += delegate { preview.ZoomBy(1.25f); };
            iZoomOut.Click += delegate { preview.ZoomBy(0.8f); };
            iFit.Click += delegate { preview.ResetView(); };
            iRotate.Click += delegate { RotateCurrent(); };
            iRepeat.Click += delegate { RepeatLast(); };
            iHistory.Click += delegate { ShowHistory(); };
            iTrash.Click += delegate { NewDocument(); };

            ResumeLayout(true);
            LayoutRail();
            LayoutRight();
        }

        IconButton Tool(string kind, string tip)
        {
            IconButton b = new IconButton(kind, D(22), false);
            b.Size = new Size(D(48), D(48));
            tips.SetToolTip(b, tip);
            rail.Controls.Add(b);
            return b;
        }

        Label MakeLabel(Panel parent, string text)
        {
            Label l = new Label();
            l.Text = text; l.ForeColor = Theme.Muted; l.BackColor = Theme.Panel; l.Font = Theme.FontLabel;
            l.AutoSize = false; l.TextAlign = ContentAlignment.MiddleLeft; l.Padding = Padding.Empty;
            parent.Controls.Add(l);
            return l;
        }

        void BuildRight()
        {
            right = new Panel(); right.Dock = DockStyle.Right; right.BackColor = Theme.Panel;
            right.Width = collapsed ? D(76) : D(380);
            right.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.LineSoft)) e.Graphics.DrawLine(p, 0, 0, 0, right.Height);
            };

            tabs = new TabBar("Основное", "Дополнительно");
            tabs.SelectedIndex = Clamp(st.Tab, 0, 1);
            bCollapse = new IconButton("chevr", D(18), false);
            tips.SetToolTip(bCollapse, "Свернуть панель");

            basicPage = new Panel(); basicPage.BackColor = Theme.Panel;
            extraPage = new Panel(); extraPage.BackColor = Theme.Panel;

            string[] presetNames = new string[Preset.All.Length];
            for (int i = 0; i < presetNames.Length; i++) presetNames[i] = Preset.All[i].Name;
            cPreset = new ChipGroup(presetNames);
            cSource = new ChipGroup("Стекло", "Автоподатчик");
            cColor = new ChipGroup("Цветной", "Серый", "Ч/Б");
            cDpi = new ChipGroup("150", "300", "600", "1200");
            cArea = new ChipGroup("Весь стол", "A4", "A5", "Рамка");
            cFormat = new ChipGroup("PDF", "JPG", "PNG");
            basicChips = new ChipGroup[] { cPreset, cSource, cColor, cDpi, cArea, cFormat };
            string[] names = new string[] { "Пресет", "Источник", "Цвет", "Разрешение, dpi", "Область", "Формат" };
            basicLabels = new Label[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                basicLabels[i] = MakeLabel(basicPage, names[i]);
                basicPage.Controls.Add(basicChips[i]);
            }

            sMerge = new SwitchRow("Объединять в один файл", "Все страницы — в один PDF");
            sEnhance = new SwitchRow("Улучшать документ", "Выровнять страницу и осветлить фон");
            sOcr = new SwitchRow("Распознавать текст", "В PDF можно искать и копировать текст");
            sSplit = new SwitchRow("Разделять фото и чеки", "Каждый чек или снимок на своей странице");
            sDuplex = new SwitchRow("Две стороны вручную", "Сначала лицевые, потом оборотные");
            sTimer = new SwitchRow("Сканировать по таймеру", "Пауза между листами");
            sMerge.DisabledSub = "Только для PDF";
            sOcr.DisabledSub = "Только для PDF";
            switches = new SwitchRow[] { sMerge, sEnhance, sOcr, sSplit, sDuplex, sTimer };
            foreach (SwitchRow s in switches) extraPage.Controls.Add(s);
            cTimerSec = new ChipGroup("5 с", "10 с", "15 с");
            extraPage.Controls.Add(cTimerSec);

            card = new SaveCard();
            bPreview = new PillButton("Просмотр", false, null);
            bScan = new PillButton("Сканировать", true, "scan");
            bExpand = new IconButton("chevl", D(18), false);
            cPreview = new IconButton("eye", D(22), false);
            cScan = new PillButton("", true, "scan"); cScan.IconOnly = true;
            tips.SetToolTip(bExpand, "Развернуть панель");
            tips.SetToolTip(cPreview, "Просмотр");
            tips.SetToolTip(cScan, "Сканировать (Enter)");
            tips.SetToolTip(bPreview, "Посмотреть, как лежит лист, и выбрать область сканирования");
            tips.SetToolTip(card, "Нажмите на строку состояния, чтобы найти файл в Проводнике");

            right.Controls.Add(tabs); right.Controls.Add(bCollapse);
            right.Controls.Add(basicPage); right.Controls.Add(extraPage);
            right.Controls.Add(card); right.Controls.Add(bPreview); right.Controls.Add(bScan);
            right.Controls.Add(bExpand); right.Controls.Add(cPreview); right.Controls.Add(cScan);
            right.Resize += delegate { LayoutRight(); };

            tabs.Changed += delegate { st.Tab = tabs.SelectedIndex; st.Save(); LayoutRight(); };
            bCollapse.Click += delegate { SetCollapsed(true, true); };
            bExpand.Click += delegate { SetCollapsed(false, true); };
            cPreset.Changed += delegate { ApplyPreset(cPreset.SelectedIndex); };
            cSource.Changed += delegate { st.Source = cSource.SelectedIndex; SettingsChanged(); };
            cColor.Changed += delegate { st.ColorMode = cColor.SelectedIndex; SettingsChanged(); };
            cDpi.Changed += delegate { st.DpiIdx = cDpi.SelectedIndex; SettingsChanged(); };
            cArea.Changed += delegate
            {
                st.Area = cArea.SelectedIndex;
                SettingsChanged();
                if (st.Area < 3) { cropN = RectangleF.Empty; if (preview.SelectMode == 1) preview.ClearSel(); }
                else if (cropN.IsEmpty)
                    SetStatus(previewScan != null ? "Обведите мышкой на листе нужную область." : "Нажмите «Просмотр» и обведите мышкой нужную область.", 0);
            };
            cFormat.Changed += delegate
            {
                FinishDuplexIfAny();
                st.Format = cFormat.SelectedIndex;
                NewDocIfAny();
                SettingsChanged();
            };
            sMerge.Changed += delegate { FinishDuplexIfAny(); st.Merge = sMerge.Checked; NewDocIfAny(); SettingsChanged(); };
            sEnhance.Changed += delegate { st.Enhance = sEnhance.Checked; SettingsChanged(); };
            sOcr.Changed += delegate
            {
                st.Ocr = sOcr.Checked; SettingsChanged();
                if (st.Ocr) SetStatus("После каждого скана Blick добавит распознанный текст в PDF.", 0);
            };
            sSplit.Changed += delegate { st.Split = sSplit.Checked; SettingsChanged(); };
            sDuplex.Changed += delegate
            {
                if (!sDuplex.Checked) FinishDuplexIfAny();
                st.Duplex = sDuplex.Checked; SettingsChanged();
                if (st.Duplex) SetStatus("Сканируйте лицевые стороны, потом нажмите «К оборотным».", 0);
            };
            sTimer.Changed += delegate { st.Timer = sTimer.Checked; if (!st.Timer) StopSeries(null); SettingsChanged(); LayoutRight(); };
            cTimerSec.Changed += delegate { st.TimerIdx = cTimerSec.SelectedIndex; SettingsChanged(); };
            bScan.Click += delegate { OnScanButton(); };
            cScan.Click += delegate { OnScanButton(); };
            bPreview.Click += delegate { OnSecondaryButton(); };
            cPreview.Click += delegate { OnSecondaryButton(); };
            card.ChangeFolder = ChooseFolder;
            card.OpenSaved = OpenSaved;
        }

        void LayoutRail()
        {
            if (iTrash == null || rail == null) return;
            int x = (rail.Width - D(48)) / 2, y = D(20);
            foreach (IconButton b in new IconButton[] { iZoomIn, iZoomOut, iFit, iRotate, iRepeat, iHistory })
            {
                b.SetBounds(x, y, D(48), D(48));
                y += D(56);
            }
            iTrash.SetBounds(x, rail.Height - D(20) - D(48), D(48), D(48));
        }

        void LayoutRight()
        {
            if (cScan == null || right == null) return;
            int W = right.ClientSize.Width, H = right.ClientSize.Height;
            bool c = collapsed;
            Control[] full = new Control[] { tabs, bCollapse, basicPage, extraPage, card, bPreview, bScan };
            foreach (Control ctl in full) ctl.Visible = !c;
            bExpand.Visible = c; cPreview.Visible = c; cScan.Visible = c;
            if (c)
            {
                int s = D(48), x0 = (W - s) / 2;
                bExpand.SetBounds(x0, D(20), s, s);
                cScan.SetBounds((W - D(52)) / 2, H - D(24) - D(52), D(52), D(52));
                cPreview.SetBounds(x0, cScan.Top - D(12) - s, s, s);
                return;
            }
            int pad = D(24), x = pad, w = W - pad * 2;
            tabs.SetBounds(x, pad, w - D(50), D(40));
            bCollapse.SetBounds(x + w - D(42), pad, D(42), D(40));
            int top = pad + D(40) + D(16);
            int by = H - pad - D(52);
            int pw = (w - D(10)) * 2 / 5;
            bPreview.SetBounds(x, by, pw, D(52));
            bScan.SetBounds(x + pw + D(10), by, w - pw - D(10), D(52));
            card.SetBounds(x, by - D(10) - D(76), w, D(76));
            int pageH = Math.Max(D(40), card.Top - D(12) - top);
            basicPage.SetBounds(0, top, W, pageH);
            extraPage.SetBounds(0, top, W, pageH);
            basicPage.Visible = tabs.SelectedIndex == 0;
            extraPage.Visible = tabs.SelectedIndex == 1;

            // «Основное»
            float need = basicChips.Length * (D(16) + D(4) + D(34) + D(12));
            float f = Math.Min(1f, pageH / need);
            int y = 0;
            for (int i = 0; i < basicChips.Length; i++)
            {
                basicLabels[i].SetBounds(x, y, w, D(16)); y += D(16) + (int)(D(4) * f);
                int ch = Math.Max(D(24), (int)(D(34) * f));
                basicChips[i].SetBounds(x, y, w, ch); y += ch + (int)(D(12) * f);
            }

            // «Дополнительно»
            bool timerChips = st.Timer;
            float needE = switches.Length * (D(52) + D(8)) + (timerChips ? D(40) : 0);
            float fe = Math.Min(1f, pageH / needE);
            int sh = Math.Max(D(38), (int)(D(52) * fe)), gap = Math.Max(D(4), (int)(D(8) * fe));
            y = 0;
            foreach (SwitchRow s in switches)
            {
                s.SetBounds(x, y, w, sh); y += sh + gap;
            }
            cTimerSec.Visible = timerChips;
            cTimerSec.SetBounds(x, y, w, Math.Max(D(26), (int)(D(32) * fe)));
        }

        void SetCollapsed(bool c, bool manual)
        {
            collapsed = c;
            if (manual) { st.Collapsed = c; autoCollapsed = false; st.Save(); }
            if (right == null) return;
            right.Width = c ? D(76) : D(380);
            LayoutRight();
        }

        // настройки
        static Color ParseHex(string hex, Color fallback)
        {
            int v;
            if (!string.IsNullOrEmpty(hex) && int.TryParse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                return Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
            return fallback;
        }

        void ApplyAccent(Color c)
        {
            Theme.SetAccent(c);
            st.Accent = Theme.Accent.R.ToString("X2") + Theme.Accent.G.ToString("X2") + Theme.Accent.B.ToString("X2");
            st.Save();
            Invalidate(true);
        }

        void RebuildUi()
        {
            Theme.MakeFonts();
            ApplyMinSize();
            shownRef = null;
            BuildUi();
            ApplySettings();
            RefreshAll();
            Invalidate(true);
        }

        void ClosePopup()
        {
            if (popup != null && !popup.IsDisposed) popup.Close();
            popup = null;
        }

        Point FitOnScreen(Point p, Size s)
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int x = Math.Max(wa.Left + 4, Math.Min(p.X, wa.Right - s.Width - 4));
            int y = Math.Max(wa.Top + 4, Math.Min(p.Y, wa.Bottom - s.Height - 4));
            return new Point(x, y);
        }

        void ShowSettings()
        {
            if (popup is SettingsPopup && !popup.IsDisposed) { ClosePopup(); return; }
            ClosePopup();
            SettingsPopup sp = new SettingsPopup(Theme.Kind, st.ScaleIdx);
            sp.AccentChanged = delegate(Color c) { ApplyAccent(c); };
            sp.ThemeChanged = delegate(int k)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (busy) { SetStatus("Тема сменится после сканирования — попробуйте ещё раз.", 0); return; }
                    st.ThemeKind = k; st.Save();
                    ClosePopup();
                    Theme.SetPalette(k);
                    RebuildUi();
                    ShowSettings();
                });
            };
            sp.ScaleChanged = delegate(int i)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (busy) { SetStatus("Масштаб можно сменить, когда закончится сканирование.", 0); return; }
                    st.ScaleIdx = i; st.Save();
                    ClosePopup();
                    Theme.Ui = SettingsPopup.Scales[i] / 100f;
                    RebuildUi();
                    ShowSettings();
                });
            };
            Point anchor = title.Gear.PointToScreen(new Point(title.Gear.Width - sp.Width, title.Gear.Height + D(6)));
            sp.Location = FitOnScreen(anchor, sp.Size);
            popup = sp;
            sp.Show(this);
        }

        void ShowHistory()
        {
            if (popup is HistoryPopup && !popup.IsDisposed) { ClosePopup(); return; }
            ClosePopup();
            HistoryPopup hp = new HistoryPopup();
            Point anchor = iHistory.PointToScreen(new Point(iHistory.Width + D(10), 0));
            hp.Location = FitOnScreen(anchor, hp.Size);
            popup = hp;
            hp.Show(this);
        }

        // состояние
        void ApplySettings()
        {
            if (st.Area == 3 && cropN.IsEmpty) st.Area = 0;
            cSource.SelectedIndex = st.Source;
            cColor.SelectedIndex = st.ColorMode;
            cDpi.SelectedIndex = st.DpiIdx;
            cArea.SelectedIndex = st.Area;
            cFormat.SelectedIndex = st.Format;
            st.Source = cSource.SelectedIndex; st.ColorMode = cColor.SelectedIndex; st.DpiIdx = cDpi.SelectedIndex;
            st.Area = cArea.SelectedIndex; st.Format = cFormat.SelectedIndex;
            sMerge.Checked = st.Merge;
            sEnhance.Checked = st.Enhance;
            sOcr.Checked = st.Ocr;
            sSplit.Checked = st.Split;
            sDuplex.Checked = st.Duplex;
            sTimer.Checked = st.Timer;
            cTimerSec.SelectedIndex = Clamp(st.TimerIdx, 0, TimerSecs.Length - 1);
            st.TimerIdx = cTimerSec.SelectedIndex;
            UpdatePresetChip();
            UpdateEnabled();
        }

        void UpdateEnabled()
        {
            bool pdf = st.Format == 0;
            foreach (ChipGroup c in basicChips) c.Enabled = !busy;
            foreach (SwitchRow s in switches) s.Enabled = !busy;
            cTimerSec.Enabled = !busy;
            sMerge.Enabled = !busy && pdf; sMerge.ShowDisabledSub = !pdf;
            sOcr.Enabled = !busy && pdf; sOcr.ShowDisabledSub = !pdf;
            bPreview.Enabled = !busy; cPreview.Enabled = !busy;
            bScan.Enabled = !busy; cScan.Enabled = !busy;
            thumbs.Enabled = !busy;
            tabs.Enabled = true;
        }

        void UpdatePresetChip()
        {
            int idx = -1;
            for (int i = 0; i < Preset.All.Length; i++)
            {
                Preset p = Preset.All[i];
                if (p.ColorMode == st.ColorMode && p.DpiIdx == st.DpiIdx && p.Area == st.Area && p.Format == st.Format &&
                    p.Enhance == st.Enhance && p.Ocr == st.Ocr && p.Split == st.Split) { idx = i; break; }
            }
            cPreset.SelectedIndex = idx;
        }

        void ApplyPreset(int i)
        {
            if (i < 0 || i >= Preset.All.Length) return;
            Preset p = Preset.All[i];
            bool formatChanged = p.Format != st.Format;
            if (formatChanged) FinishDuplexIfAny();
            st.ColorMode = p.ColorMode; st.DpiIdx = p.DpiIdx; st.Area = p.Area; st.Format = p.Format;
            st.Enhance = p.Enhance; st.Ocr = p.Ocr; st.Split = p.Split;
            if (st.Area < 3) { cropN = RectangleF.Empty; if (preview.SelectMode == 1) preview.ClearSel(); }
            if (formatChanged) NewDocIfAny();
            ApplySettings();
            st.Save();
            List<string> bits = new List<string>();
            bits.Add(cColor.Items[st.ColorMode].ToLower(Ru));
            bits.Add(Dpis[st.DpiIdx] + " dpi");
            bits.Add(cArea.Items[st.Area]);
            bits.Add(cFormat.Items[st.Format]);
            if (st.Enhance) bits.Add("улучшение");
            if (st.Ocr) bits.Add("распознавание текста");
            if (st.Split) bits.Add("деление на объекты");
            SetStatus("Пресет «" + p.Name + "»: " + string.Join(", ", bits.ToArray()) + ".", 0);
        }

        void SettingsChanged()
        {
            st.Save();
            if (statusKind != 2) statusText = null;
            UpdatePresetChip();
            UpdateEnabled();
            UpdateCard();
            UpdateButtons();
        }

        string Folder()
        {
            if (!string.IsNullOrEmpty(st.Folder) && Directory.Exists(st.Folder)) return st.Folder;
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        string NextName(string ext)
        {
            string folder = Folder();
            string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            for (int i = 1; i < 10000; i++)
            {
                string b = date + "_" + i.ToString("000", CultureInfo.InvariantCulture);
                if (!File.Exists(Path.Combine(folder, b + ".pdf")) && !File.Exists(Path.Combine(folder, b + ".jpg")) &&
                    !File.Exists(Path.Combine(folder, b + ".png")))
                    return Path.Combine(folder, b + "." + ext);
            }
            return Path.Combine(folder, date + "_" + DateTime.Now.Ticks + "." + ext);
        }

        string Ext() { return st.Format == 0 ? "pdf" : (st.Format == 1 ? "jpg" : "png"); }

        void SetStatus(string text, int kind) { statusText = text; statusKind = kind; UpdateCard(); }

        void UpdateCard()
        {
            if (card == null) return;
            string folder = Folder();
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string fname;
            if (string.Equals(folder.TrimEnd('\\'), desktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) fname = "Рабочий стол";
            else
            {
                fname = new DirectoryInfo(folder).Name;
                if (string.IsNullOrEmpty(fname)) fname = folder;
            }
            card.FolderName = fname;
            if (statusText != null) { card.Status = statusText; card.StatusKind = statusKind; }
            else
            {
                card.StatusKind = 0;
                if (st.Format == 0 && st.Merge && docPath != null)
                    card.Status = "Следующая страница добавится в " + Path.GetFileName(docPath);
                else card.Status = "Следующий файл: " + Path.GetFileName(NextName(Ext()));
            }
            card.Openable = statusKind == 1 && statusText != null && lastSaved != null;
            card.Invalidate();
        }

        void UpdateButtons()
        {
            if (bScan == null) return;
            string scanText;
            if (busy && !busyPreview) scanText = "Сканирую…";
            else if (series) scanText = countdown > 0 ? "Стоп · " + countdown : "Стоп";
            else if (st.Timer) scanText = "Начать серию";
            else scanText = "Сканировать";
            bScan.Text = scanText;
            bScan.IconKind = series ? "stop" : (st.Timer ? "timer" : "scan");
            cScan.IconKind = bScan.IconKind;
            string sec;
            if (busy && busyPreview) sec = "Готовлю просмотр…";
            else if (duplexPhase == 1 && pages.Count > duplexStart) sec = "К оборотным";
            else if (duplexPhase == 2) sec = "Собрать";
            else sec = "Просмотр";
            bPreview.Text = sec;
            cPreview.Kind = sec == "К оборотным" ? "repeat" : (sec == "Собрать" ? "check" : "eye");
            tips.SetToolTip(cPreview, sec);
            bScan.Invalidate(); cScan.Invalidate(); bPreview.Invalidate(); cPreview.Invalidate();
            if (duplexPhase == 1) thumbs.Note = "Лицевые: " + (pages.Count - duplexStart);
            else if (duplexPhase == 2) thumbs.Note = "Оборотные: " + (pages.Count - duplexStart - duplexFronts) + " из " + duplexFronts;
            else thumbs.Note = null;
            thumbs.Invalidate();
        }

        void RefreshAll()
        {
            if (preview == null) return;
            thumbs.Pages = pages;
            thumbs.Selected = sel;
            thumbs.AllowReorder = st.Format == 0 && st.Merge && duplexPhase == 0;
            thumbs.Invalidate();
            Bitmap show = null;
            string a = "—", b = "—";
            if (sel >= 0 && sel < pages.Count)
            {
                Page p = pages[sel];
                show = p.Preview;
                a = p.Full.Width + " × " + p.Full.Height + " px";
                b = p.SavedPath == null ? "не сохранено" : SizeText(p.SavedPath);
            }
            else if (previewScan != null)
            {
                show = previewScan;
                a = "Черновой просмотр";
                b = "150 dpi";
            }
            if (!ReferenceEquals(show, shownRef))
            {
                shownRef = show;
                if (show != null && show == previewScan) preview.Sel = cropN;
                else preview.ClearSel();
            }
            if (sel >= 0 && sel < pages.Count) { preview.SelectMode = 2; preview.Hint = "Выделите часть страницы, которую хотите оставить"; }
            else if (previewScan != null) { preview.SelectMode = 1; preview.Hint = "Выделите участок для сканирования"; }
            else { preview.SelectMode = 0; preview.Hint = null; }
            preview.Image = show;
            preview.Invalidate();
            header.InfoA = a; header.InfoB = b; header.Invalidate();
            bool hasImg = show != null && !busy;
            iRotate.Enabled = hasImg; iZoomIn.Enabled = hasImg; iZoomOut.Enabled = hasImg; iFit.Enabled = hasImg;
            iRepeat.Enabled = !busy && lastParams != null && lastAdded.Count > 0;
            iTrash.Enabled = !busy && (pages.Count > 0 || previewScan != null);
            UpdateButtons();
            UpdateCard();
        }

        static string SizeText(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                    return (new FileInfo(path).Length / 1048576.0).ToString("0.0", Ru) + " МБ";
            }
            catch { }
            return "—";
        }

        void ClearPages()
        {
            foreach (Page p in pages) p.Dispose();
            pages.Clear();
            sel = -1;
            docPath = null;
            duplexPhase = 0;
            lastAdded = new List<Page>();
        }

        void NewDocIfAny()
        {
            if (pages.Count == 0) return;
            ClearPages();
            RefreshAll();
            SetStatus("Новый документ. Готовые сканы остались в папке.", 0);
        }

        void NewDocument()
        {
            if (busy) return;
            FinishDuplexIfAny();
            StopSeries(null);
            ClearPages();
            if (previewScan != null) { previewScan.Dispose(); previewScan = null; }
            preview.ResetView();
            RefreshAll();
            SetStatus("Новый документ. Сохранённые файлы остались в папке.", 0);
        }

        void SetBusy(bool b, bool isPreview, string msg)
        {
            busy = b;
            busyPreview = b && isPreview;
            preview.SetBusy(b);
            UpdateEnabled();
            if (b && msg != null) SetStatus(msg, 0);
            RefreshAll();
        }

        // сканирование
        ScanParams BuildParams()
        {
            ScanParams sp = new ScanParams();
            sp.Feeder = st.Source == 1;
            sp.ColorMode = st.ColorMode;
            sp.Dpi = Dpis[Clamp(st.DpiIdx, 0, Dpis.Length - 1)];
            sp.Area = st.Area == 3 ? 0 : st.Area;
            if (st.Area == 3) sp.Crop = cropN;
            sp.Enhance = st.Enhance;
            sp.Split = st.Split;
            sp.Ocr = st.Ocr && st.Format == 0;
            return sp;
        }

        void OnScanButton()
        {
            if (series) { StopSeries("Серия остановлена."); return; }
            if (busy) return;
            if (st.Timer) series = true;
            DoScan(false, null);
        }

        void OnSecondaryButton()
        {
            if (busy) return;
            if (duplexPhase == 1 && pages.Count > duplexStart) GoBacks();
            else if (duplexPhase == 2) AssembleDuplex();
            else DoScan(true, null);
        }

        void Post(string text)
        {
            try { BeginInvoke((MethodInvoker)delegate { SetStatus(text, 0); }); } catch { }
        }

        void DoScan(bool isPreview, ScanParams forced)
        {
            if (busy) return;
            ClosePopup();
            ScanParams sp;
            if (forced != null) sp = forced.Clone();
            else if (isPreview) { sp = new ScanParams(); sp.Dpi = 150; }
            else sp = BuildParams();
            if (!isPreview) lastParams = sp.Clone();
            SetBusy(true, isPreview, isPreview ? "Готовлю предпросмотр…" : "Идёт сканирование…");
            Thread th = new Thread(delegate()
            {
                List<Bitmap> res = null;
                List<List<OcrWord>> words = null;
                string err = null;
                try
                {
                    res = Scanner.Scan(sp);
                    if (!isPreview)
                    {
                        if (sp.Split && sp.Crop.IsEmpty)
                        {
                            List<Bitmap> all = new List<Bitmap>();
                            foreach (Bitmap b in res)
                            {
                                List<Bitmap> parts = null;
                                try { parts = Splitter.Split(b); } catch { }
                                if (parts != null) { b.Dispose(); all.AddRange(parts); }
                                else all.Add(b);
                            }
                            res = all;
                        }
                        if (sp.Enhance)
                        {
                            if (res.Count > 0) Post("Выравниваю и отбеливаю…");
                            for (int k = 0; k < res.Count; k++) res[k] = Enhance.Apply(res[k]);
                        }
                        if (sp.Ocr)
                        {
                            Post("Распознаю текст…");
                            words = new List<List<OcrWord>>();
                            foreach (Bitmap b in res)
                            {
                                try { words.Add(Ocr.Recognize(b)); }
                                catch (Exception ex) { words.Add(null); Ocr.LastInfo = "Распознавание не сработало: " + ex.Message; }
                            }
                        }
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke((MethodInvoker)delegate { OnScanDone(res, words, err, sp, isPreview); }); }
                catch { }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
        }

        void OnScanDone(List<Bitmap> res, List<List<OcrWord>> words, string err, ScanParams sp, bool isPreview)
        {
            SetBusy(false, false, null);
            if (err != null) { if (series) StopSeries(null); SetStatus(err, 2); return; }
            if (res == null || res.Count == 0) { if (series) StopSeries(null); SetStatus("Сканер не вернул изображение.", 2); return; }

            if (isPreview)
            {
                if (previewScan != null) previewScan.Dispose();
                previewScan = Img.Scaled(res[0], 1400);
                foreach (Bitmap b in res) b.Dispose();
                sel = -1;
                preview.ResetView();
                RefreshAll();
                SetStatus("Выделите нужный участок или нажмите «Сканировать» для всего листа.", 0);
                return;
            }

            if (previewScan != null) { previewScan.Dispose(); previewScan = null; }
            List<Page> added = new List<Page>();
            for (int i = 0; i < res.Count; i++)
            {
                Page p = new Page(res[i], sp.Dpi);
                if (words != null && i < words.Count) p.Words = words[i];
                pages.Add(p); added.Add(p);
            }
            lastAdded = added;
            sel = pages.Count - 1;
            bool saved = false;
            if (st.Duplex) saved = HandleDuplex(added, sp);
            else saved = SaveAdded(added);
            preview.ResetView();
            RefreshAll();
            if (saved) { preview.PlaySuccess(); card.Flash(); }
            if (series) StartCountdown();
        }

        string OcrNote(List<Page> added)
        {
            if (!st.Ocr || st.Format != 0) return "";
            int n = 0; bool any = false;
            foreach (Page p in added) if (p.Words != null) { any = true; n += p.Words.Count; }
            if (!any) return " · " + (Ocr.LastInfo ?? "текст не распознан");
            return n > 0 ? " · текст распознан" : " · текста не найдено";
        }

        bool SaveAdded(List<Page> added)
        {
            try
            {
                if (st.Format == 0 && st.Merge)
                {
                    if (docPath == null) docPath = NextName("pdf");
                    Pdf.Write(docPath, pages);
                    foreach (Page p in pages) p.SavedPath = docPath;
                    lastSaved = docPath;
                    History.Add(docPath, pages.Count);
                    SetStatus("Сохранено: " + Path.GetFileName(docPath) + " · страниц: " + pages.Count + OcrNote(added), 1);
                }
                else
                {
                    foreach (Page p in added)
                    {
                        string path = NextName(Ext());
                        SavePage(p, path);
                        p.SavedPath = path;
                        lastSaved = path;
                        History.Add(path, 1);
                    }
                    string more = added.Count > 1 ? " и ещё " + (added.Count - 1) : "";
                    SetStatus("Сохранено: " + Path.GetFileName(lastSaved) + more + OcrNote(added), 1);
                }
                return true;
            }
            catch (Exception ex) { SetStatus(SaveError(ex), 2); return false; }
        }

        static void SavePage(Page p, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pdf") Pdf.Write(path, new List<Page> { p });
            else if (ext == ".jpg") Img.SaveJpeg(p.Full, path, 92, p.Dpi);
            else { p.Full.SetResolution(p.Dpi, p.Dpi); p.Full.Save(path, ImageFormat.Png); }
        }

        static string SaveError(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "Нет доступа к папке. Выберите другую через «Изменить».";
            if (ex is IOException) return "Не удалось записать файл: возможно, он открыт в другой программе.";
            return "Не удалось сохранить: " + ex.Message;
        }

        // двусторонний вручную
        bool HandleDuplex(List<Page> added, ScanParams sp)
        {
            if (duplexPhase == 0) { duplexPhase = 1; duplexStart = pages.Count - added.Count; }
            if (duplexPhase == 1)
            {
                SetStatus("Лицевые стороны: " + (pages.Count - duplexStart) + ". Закончите — нажмите «К оборотным».", 0);
                return false;
            }
            duplexReverse = sp.Feeder;
            int backs = pages.Count - duplexStart - duplexFronts;
            if (backs >= duplexFronts) return AssembleDuplex();
            SetStatus("Оборотные стороны: " + backs + " из " + duplexFronts + ".", 0);
            return false;
        }

        void GoBacks()
        {
            duplexFronts = pages.Count - duplexStart;
            duplexPhase = 2;
            SetStatus("Переверните листы и сканируйте оборотные стороны: 0 из " + duplexFronts + ".", 0);
            RefreshAll();
        }

        bool AssembleDuplex()
        {
            int total = pages.Count - duplexStart;
            int f = Math.Min(duplexFronts, total);
            List<Page> fronts = pages.GetRange(duplexStart, f);
            List<Page> backs = pages.GetRange(duplexStart + f, total - f);
            if (duplexReverse) backs.Reverse();
            List<Page> merged = new List<Page>();
            for (int i = 0; i < Math.Max(fronts.Count, backs.Count); i++)
            {
                if (i < fronts.Count) merged.Add(fronts[i]);
                if (i < backs.Count) merged.Add(backs[i]);
            }
            pages.RemoveRange(duplexStart, total);
            pages.AddRange(merged);
            duplexPhase = 0;
            bool ok = SaveAdded(merged);
            sel = pages.Count - 1;
            RefreshAll();
            if (ok) { preview.PlaySuccess(); card.Flash(); }
            return false; // анимацию уже показали
        }

        void FinishDuplexIfAny()
        {
            if (duplexPhase == 0) return;
            if (duplexPhase == 2) { AssembleDuplex(); return; }
            duplexPhase = 0;
            List<Page> unsaved = new List<Page>();
            foreach (Page p in pages) if (p.SavedPath == null) unsaved.Add(p);
            if (unsaved.Count > 0) SaveAdded(unsaved);
            RefreshAll();
        }

        // серия по таймеру
        void StartCountdown()
        {
            countdown = TimerSecs[Clamp(st.TimerIdx, 0, TimerSecs.Length - 1)];
            preview.CountdownTotal = countdown;
            preview.Countdown = countdown;
            preview.Invalidate();
            secTimer.Stop(); secTimer.Start();
            UpdateButtons();
        }

        void OnSecond()
        {
            if (!series) { secTimer.Stop(); return; }
            countdown--;
            preview.Countdown = Math.Max(0, countdown);
            preview.Invalidate();
            UpdateButtons();
            if (countdown <= 0)
            {
                secTimer.Stop();
                DoScan(false, null);
            }
        }

        void StopSeries(string msg)
        {
            bool was = series;
            series = false;
            secTimer.Stop();
            countdown = 0;
            if (preview != null) { preview.Countdown = 0; preview.Invalidate(); }
            UpdateButtons();
            if (was && msg != null) SetStatus(msg, 0);
        }

        // повтор последнего
        void RepeatLast()
        {
            if (busy) return;
            if (lastParams == null || lastAdded.Count == 0) { SetStatus("Сначала отсканируйте лист.", 0); return; }
            StopSeries(null);
            bool merged = docPath != null;
            foreach (Page p in lastAdded)
            {
                int idx = pages.IndexOf(p);
                if (idx < 0) continue;
                pages.RemoveAt(idx);
                if (!merged && p.SavedPath != null) Recycle(p.SavedPath);
                p.Dispose();
            }
            if (merged)
            {
                if (pages.Count > 0) { try { Pdf.Write(docPath, pages); } catch { } }
                else { Recycle(docPath); docPath = null; }
            }
            lastAdded = new List<Page>();
            sel = pages.Count - 1;
            RefreshAll();
            DoScan(false, lastParams);
        }

        // правка страниц
        void RotateCurrent()
        {
            if (busy) return;
            if (sel >= 0 && sel < pages.Count)
            {
                Page p = pages[sel];
                p.Rotate();
                preview.ClearSel();
                Resave(p, "Повернул и пересохранил ");
            }
            else if (previewScan != null)
            {
                previewScan.RotateFlip(RotateFlipType.Rotate90FlipNone);
                cropN = RectangleF.Empty; preview.ClearSel();
                if (st.Area == 3) { st.Area = 0; cArea.SelectedIndex = 0; st.Save(); }
            }
            preview.ResetView();
            RefreshAll();
        }

        void Resave(Page p, string verb)
        {
            if (p.SavedPath == null) return;
            bool needOcr = st.Ocr && p.Words == null && p.SavedPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            if (!needOcr) { DoResave(p, verb); return; }
            SetBusy(true, false, "Распознаю текст заново…");
            Thread th = new Thread(delegate()
            {
                List<OcrWord> w = null;
                try { w = Ocr.Recognize(p.Full); } catch { }
                try { BeginInvoke((MethodInvoker)delegate { p.Words = w; SetBusy(false, false, null); DoResave(p, verb); }); } catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        void DoResave(Page p, string verb)
        {
            try
            {
                if (docPath != null && p.SavedPath == docPath) Pdf.Write(docPath, pages);
                else SavePage(p, p.SavedPath);
                lastSaved = p.SavedPath;
                SetStatus(verb + Path.GetFileName(p.SavedPath), 1);
            }
            catch (Exception ex) { SetStatus(SaveError(ex), 2); }
            RefreshAll();
        }

        void OnSelChanged()
        {
            if (preview.SelectMode != 1) { preview.Invalidate(); return; }
            cropN = preview.HasSel ? preview.Sel : RectangleF.Empty;
            if (!cropN.IsEmpty)
            {
                st.Area = 3; cArea.SelectedIndex = 3; st.Save(); UpdatePresetChip();
                SetStatus("Отсканируется только выделенная область. Сбросить — клик по листу или «Весь стол».", 0);
            }
            else if (st.Area == 3)
            {
                st.Area = 0; cArea.SelectedIndex = 0; st.Save(); UpdatePresetChip();
                statusText = null; statusKind = 0; UpdateCard();
            }
        }

        void ApplyPageCrop()
        {
            if (busy || sel < 0 || sel >= pages.Count || !preview.HasSel) return;
            Page p = pages[sel];
            p.Crop(preview.Sel);
            preview.ClearSel();
            preview.ResetView();
            Resave(p, "Обрезал и пересохранил ");
            RefreshAll();
        }

        static void Recycle(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch { }
        }

        void DeletePage(int i)
        {
            if (busy || i < 0 || i >= pages.Count) return;
            Page p = pages[i];
            bool merged = docPath != null && p.SavedPath == docPath;
            if (duplexPhase > 0 && i >= duplexStart)
            {
                if (duplexPhase == 2 && i < duplexStart + duplexFronts) duplexFronts--;
            }
            else if (duplexPhase > 0 && i < duplexStart) duplexStart--;
            pages.RemoveAt(i);
            lastAdded.Remove(p);
            try
            {
                string msg;
                if (merged)
                {
                    List<Page> savedPages = new List<Page>();
                    foreach (Page q in pages) if (q.SavedPath == docPath) savedPages.Add(q);
                    if (savedPages.Count > 0) { Pdf.Write(docPath, savedPages); msg = "Страница удалена, " + Path.GetFileName(docPath) + " пересохранён."; }
                    else { Recycle(docPath); msg = "Последняя страница удалена, файл отправлен в корзину."; docPath = null; }
                }
                else if (p.SavedPath != null)
                {
                    Recycle(p.SavedPath);
                    msg = "Страница удалена, " + Path.GetFileName(p.SavedPath) + " в корзине.";
                }
                else msg = "Страница удалена.";
                SetStatus(msg, 0);
            }
            catch (Exception ex) { SetStatus(SaveError(ex), 2); }
            p.Dispose();
            if (sel >= pages.Count) sel = pages.Count - 1;
            else if (sel > i) sel--;
            preview.ResetView();
            RefreshAll();
        }

        void MovePage(int from, int to)
        {
            if (busy || duplexPhase != 0 || from < 0 || from >= pages.Count || to < 0 || to >= pages.Count) return;
            Page p = pages[from];
            pages.RemoveAt(from);
            pages.Insert(to, p);
            sel = to;
            if (docPath != null)
            {
                try { Pdf.Write(docPath, pages); SetStatus("Порядок страниц сохранён в " + Path.GetFileName(docPath), 1); lastSaved = docPath; }
                catch (Exception ex) { SetStatus(SaveError(ex), 2); }
            }
            RefreshAll();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (series) { StopSeries("Серия остановлена."); return true; }
                if (preview != null && preview.HasSel) { preview.ClearSel(); OnSelChanged(); return true; }
            }
            if (!busy)
            {
                if (keyData == Keys.Delete && sel >= 0 && sel < pages.Count) { DeletePage(sel); return true; }
                if (keyData == Keys.Enter)
                {
                    if (preview.SelectMode == 2 && preview.HasSel) ApplyPageCrop(); else OnScanButton();
                    return true;
                }
                if (keyData == (Keys.Control | Keys.R) || keyData == Keys.F5) { RepeatLast(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void ChooseFolder()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Куда сохранять сканы";
                d.SelectedPath = Folder();
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    st.Folder = d.SelectedPath;
                    st.Save();
                    statusText = null; statusKind = 0;
                    UpdateCard();
                }
            }
        }

        void OpenSaved()
        {
            try
            {
                if (lastSaved != null && File.Exists(lastSaved))
                    Process.Start("explorer.exe", "/select,\"" + lastSaved + "\"");
            }
            catch { }
        }

        void DetectDevice()
        {
            Thread th = new Thread(delegate()
            {
                string name = null, diag = null;
                try { name = Scanner.FindName(out diag); }
                catch (Exception ex) { diag = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (name != null)
                        {
                            deviceText = name + " · подключён"; deviceOk = true;
                            if (statusKind == 2) { statusText = null; statusKind = 0; UpdateCard(); }
                        }
                        else
                        {
                            deviceText = "Сканер не найден · проверить снова"; deviceOk = false;
                            SetStatus(diag ?? "Сканер не найден.", 2);
                        }
                        title.SetDevice(deviceText, deviceOk);
                    });
                }
                catch { }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClearPages();
                if (previewScan != null) previewScan.Dispose();
                if (tips != null) tips.Dispose();
                secTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
