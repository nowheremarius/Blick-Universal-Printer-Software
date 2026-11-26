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
    class MainForm : GlassWindow
    {
        static readonly int[] Dpis = new int[] { 150, 300, 600, 1200 };
        static readonly int[] TimerSecs = new int[] { 5, 10, 15 };

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
        string deviceName, requestedDevice;
        bool detecting;
        List<Scanner.Device> devices = new List<Scanner.Device>();
        int deviceState; // 0 — ищем, 1 — подключён, 2 — не найден

        TitleBar title;
        Surface rail, right, center, basicPage, extraPage;
        PreviewPanel preview;
        ThumbStrip thumbs;
        TabBar tabs;
        IconButton bCollapse, bExpand, cPreview;
        PillButton cScan;
        ChipGroup cPreset, cSource, cColor, cDpi, cArea, cFormat, cTimerSec;
        ChipGroup[] basicChips;
        GlassLabel[] basicLabels;
        SwitchRow sMerge, sEnhance, sOcr, sSplit, sDuplex, sTimer;
        SwitchRow[] switches;
        SaveCard card;
        PillButton bPreview, bScan;
        IconButton iZoomIn, iZoomOut, iFit, iRotate, iRepeat, iHistory, iTrash;
        ToolTip tips;
        Surface toolbar;
        IconButton navScan, navDevices, navSettings, previousPage, nextPage;
        GlassLabel pageCounter, parameterTitle;
        public int NavWidth { get { return rail == null ? D(172) : rail.Width; } }


        static int D(float v) { return Theme.D(v); }

        public MainForm()
        {
            if (st.Lang != "en") st.Lang = "ru";
            L.En = st.Lang == "en";
            using (Graphics g = CreateGraphics()) Theme.Dpi = g.DpiX / 96f;
            st.ScaleIdx = Clamp(st.ScaleIdx, 0, SettingsPopup.Scales.Length - 1);
            Theme.Ui = SettingsPopup.Scales[st.ScaleIdx] / 100f;
            Theme.MakeFonts();

            Text = "Blick";
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

        // окно
        // При открытии всё окно рисуется одним кадром (WS_EX_COMPOSITED),
        // поэтому боковые панели не появляются с задержкой. После показа режим снимается.
        bool composited = true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
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

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (right == null || navSettings == null || navSettings.IsDisposed) return;
            LayoutRail();
            if (!collapsed && ClientSize.Width < D(1080)) { autoCollapsed = true; SetCollapsed(true, false); }
            else if (collapsed && autoCollapsed && ClientSize.Width >= D(1140)) { autoCollapsed = false; SetCollapsed(false, false); }
        }

        protected override void WndProc(ref Message m)
        {
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

        string DeviceText()
        {
            if (deviceState == 1) return deviceName + L.T(" · подключён", " · connected");
            if (deviceState == 2) return L.T("Сканер не найден", "Scanner not found");
            return L.T("Ищу сканер…", "Looking for a scanner…");
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
            tips = Tips.Make();
            Padding = new Padding(D(5));
            Text = "Blick";

            center = new Surface(0);
            center.Dock = DockStyle.Fill;
            // отступы совпадают с карточкой справа: верх и низ блоков на одной линии
            center.Padding = new Padding(D(14), D(4), D(10), D(14));
            preview = new PreviewPanel(); preview.Dock = DockStyle.Fill;
            thumbs = new ThumbStrip(); thumbs.Dock = DockStyle.Left; thumbs.Width = D(128);
            Surface gap = new Surface(0); gap.Dock = DockStyle.Left; gap.Width = D(10);
            toolbar = new Surface(1); toolbar.Dock = DockStyle.Bottom; toolbar.Height = D(44);
            Surface canvas = new Surface(0); canvas.Dock = DockStyle.Fill;
            canvas.Controls.Add(preview);
            canvas.Controls.Add(toolbar);
            center.Controls.Add(canvas);
            center.Controls.Add(gap);
            center.Controls.Add(thumbs);

            rail = new Surface(2); rail.Dock = DockStyle.Left; rail.Width = D(172);
            iZoomIn = Tool("zoomin", L.T("Увеличить (колесо мыши)", "Zoom in (mouse wheel)"));
            iZoomOut = Tool("zoomout", L.T("Уменьшить", "Zoom out"));
            iFit = Tool("fit", L.T("Вместить страницу в окно", "Fit page to window"));
            iRotate = Tool("rotate", L.T("Повернуть страницу на 90°", "Rotate page 90°"));
            iRepeat = Tool("repeat", L.T("Заменить последний скан (Ctrl+R)", "Replace the last scan (Ctrl+R)"));
            iHistory = Tool("history", L.T("История сканов", "Scan history"));
            iTrash = Tool("trash", L.T("Начать новый документ. Файлы останутся в папке.", "Start a new document. Saved files stay in the folder."));
            navScan = Tool("scan", L.T("Сканирование", "Scanning")); navScan.Selected = true;
            navDevices = Tool("printer-check", L.T("Устройства", "Devices"));
            navSettings = Tool("settings", L.T("Настройки", "Settings"));
            navScan.Text = navScan.AccessibleName; navDevices.Text = navDevices.AccessibleName;
            navSettings.Text = navSettings.AccessibleName;
            iHistory.Text = L.T("История", "History");
            iTrash.Text = L.T("Новый документ", "New document");
            previousPage = Tool("chevl", L.T("Предыдущая страница", "Previous page"));
            nextPage = Tool("chevr", L.T("Следующая страница", "Next page"));
            pageCounter = new GlassLabel("", Theme.FontSmall, Theme.Text2); pageCounter.Align = 1;
            toolbar.Controls.Add(pageCounter);
            foreach (IconButton b in new IconButton[] { iZoomOut, iZoomIn, iFit, iRotate, iRepeat, previousPage, nextPage })
                toolbar.Controls.Add(b);
            toolbar.Resize += delegate { LayoutToolbar(); };
            navScan.Click += delegate { ClosePopup(); SetCollapsed(false, true); preview.Focus(); };
            navDevices.Click += delegate { OpenDevices(navDevices); };
            navSettings.Click += delegate { ShowSettings(); };
            previousPage.Click += delegate { SelectPage(sel - 1); };
            nextPage.Click += delegate { SelectPage(sel + 1); };
            rail.Resize += delegate { LayoutRail(); };

            BuildRight();

            title = new TitleBar(this); title.Dock = DockStyle.Top; title.Height = D(56);
            title.SetDevice(DeviceText(), deviceState);

            Controls.Add(center);
            Controls.Add(right);
            Controls.Add(rail);
            Controls.Add(title);

            // события
            title.DeviceClicked = delegate { OpenDevices(title); };
            preview.SelChanged = OnSelChanged;
            preview.CropApply = ApplyPageCrop;
            thumbs.PageDeleted = DeletePage;
            thumbs.PageMoved = MovePage;
            thumbs.AddClicked = delegate { OnScanButton(); };
            thumbs.PageClicked = SelectPage;
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
            IconButton b = new IconButton(kind, D(21), false);
            b.Size = new Size(D(44), D(44));
            b.AccessibleName = tip;
            tips.SetToolTip(b, tip);
            rail.Controls.Add(b);
            return b;
        }

        GlassLabel MakeLabel(Control parent, string text)
        {
            GlassLabel l = new GlassLabel(text, Theme.FontLabel, Theme.Muted);
            parent.Controls.Add(l);
            return l;
        }

        void BuildRight()
        {
            right = new Surface(1); right.Dock = DockStyle.Right;
            right.CardInset = new Padding(D(4), D(4), D(14), D(14));
            right.Width = collapsed ? D(84) : D(384);

            tabs = new TabBar(L.T("Основное", "Basic"), L.T("Дополнительно", "More"));
            tabs.SelectedIndex = Clamp(st.Tab, 0, 1);
            bCollapse = new IconButton("chevr", D(18), false);
            tips.SetToolTip(bCollapse, L.T("Свернуть панель", "Collapse panel"));

            basicPage = new Surface(0);
            extraPage = new Surface(0);

            string[] presetNames = new string[Preset.All.Length];
            for (int i = 0; i < presetNames.Length; i++) presetNames[i] = Preset.All[i].Name;
            cPreset = new ChipGroup(presetNames);
            cSource = new ChipGroup(L.T("Стекло", "Flatbed"), L.T("Автоподатчик", "Feeder"));
            cColor = new ChipGroup(L.T("Цветной", "Color"), L.T("Серый", "Gray"), L.T("Ч/Б", "B/W"));
            cDpi = new ChipGroup("150", "300", "600", "1200");
            cArea = new ChipGroup(L.T("Весь стол", "Full bed"), "A4", "A5", L.T("Рамка", "Custom"));
            cFormat = new ChipGroup("PDF", "JPG", "PNG");
            basicChips = new ChipGroup[] { cPreset, cSource, cColor, cDpi, cArea, cFormat };
            string[] names = L.En
                ? new string[] { "Preset", "Source", "Color", "Resolution, dpi", "Area", "Format" }
                : new string[] { "Пресет", "Источник", "Цвет", "Разрешение, dpi", "Область", "Формат" };
            string[] fieldIcons = { "sliders-horizontal", "layers", "palette", "grip", "file", "file-type" };
            parameterTitle = new GlassLabel(L.T("Параметры сканирования", "Scan settings"), Theme.FontBig, Theme.Text);
            basicPage.Controls.Add(parameterTitle);
            basicLabels = new GlassLabel[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                basicLabels[i] = MakeLabel(basicPage, names[i]);
                basicChips[i].FieldIcon = fieldIcons[i];
                basicChips[i].AccessibleName = names[i];
                basicPage.Controls.Add(basicChips[i]);
            }

            sMerge = new SwitchRow(L.T("Объединять в один файл", "Merge into one file"), L.T("Все страницы — в один PDF", "All pages go into one PDF"));
            sEnhance = new SwitchRow(L.T("Улучшать документ", "Enhance document"), L.T("Выровнять страницу и осветлить фон", "Straighten the page and lighten the background"));
            sOcr = new SwitchRow(L.T("Распознавать текст", "Recognize text"), L.T("В PDF можно искать и копировать текст", "Text in the PDF becomes searchable"));
            sSplit = new SwitchRow(L.T("Разделять фото и чеки", "Split into objects"), L.T("Каждый чек или снимок на своей странице", "Save each receipt or photo as a separate page"));
            sDuplex = new SwitchRow(L.T("Две стороны вручную", "Manual duplex"), L.T("Сначала лицевые, потом оборотные", "Fronts first, then backs"));
            sTimer = new SwitchRow(L.T("Сканировать по таймеру", "Scan on a timer"), L.T("Пауза между листами", "Pause between sheets"));
            sMerge.DisabledSub = L.T("Только для PDF", "PDF only");
            sOcr.DisabledSub = L.T("Только для PDF", "PDF only");
            switches = new SwitchRow[] { sMerge, sEnhance, sOcr, sSplit, sDuplex, sTimer };
            foreach (SwitchRow s in switches) extraPage.Controls.Add(s);
            cTimerSec = new ChipGroup(L.T("5 с", "5 s"), L.T("10 с", "10 s"), L.T("15 с", "15 s"));
            extraPage.Controls.Add(cTimerSec);

            card = new SaveCard();
            bPreview = new PillButton(L.T("Просмотр", "Preview"), false, null);
            bScan = new PillButton(L.T("Сканировать", "Scan"), true, "scan");
            bExpand = new IconButton("chevl", D(18), false);
            cPreview = new IconButton("eye", D(21), false);
            cScan = new PillButton("", true, "scan"); cScan.IconOnly = true;
            tips.SetToolTip(bExpand, L.T("Развернуть панель", "Expand panel"));
            tips.SetToolTip(cPreview, L.T("Просмотр", "Preview"));
            tips.SetToolTip(cScan, L.T("Сканировать (Enter)", "Scan (Enter)"));
            tips.SetToolTip(bPreview, L.T("Посмотреть, как лежит лист, и выбрать область сканирования", "Preview the sheet and select the area to scan"));
            tips.SetToolTip(card, L.T("Нажмите на строку состояния, чтобы найти файл в Проводнике", "Click the status line to locate the file in File Explorer"));

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
                    SetStatus(previewScan != null
                        ? L.T("Обведите мышкой на листе нужную область.", "Drag on the sheet to mark the area.")
                        : L.T("Нажмите «Просмотр» и обведите мышкой нужную область.", "Press “Preview” and drag to mark the area."), 0);
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
                if (st.Ocr) SetStatus(L.T("После каждого скана Blick добавит распознанный текст в PDF.",
                    "After each scan, Blick adds any recognized text to the PDF."), 0);
            };
            sSplit.Changed += delegate { st.Split = sSplit.Checked; SettingsChanged(); };
            sDuplex.Changed += delegate
            {
                if (!sDuplex.Checked) FinishDuplexIfAny();
                st.Duplex = sDuplex.Checked; SettingsChanged();
                if (st.Duplex) SetStatus(L.T("Сканируйте лицевые стороны, потом нажмите «К оборотным».", "Scan the fronts, then press “To backs”."), 0);
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
            if (navSettings == null || navSettings.IsDisposed || rail == null) return;
            bool compact = ClientSize.Width < D(1120);
            rail.Width = D(compact ? 58 : 174);
            thumbs.Width = D(compact ? 94 : 132);
            int y = D(14);
            foreach (IconButton b in new IconButton[] { navScan, iHistory, navDevices, iTrash })
            {
                b.SetBounds(D(6), y, rail.Width - D(12), D(42)); y += D(48);
            }
            navSettings.SetBounds(D(6), rail.Height - D(56), rail.Width - D(12), D(42));
            LayoutToolbar();
        }

        void LayoutToolbar()
        {
            if (pageCounter == null || pageCounter.IsDisposed || toolbar == null) return;
            int w = Math.Min(D(34), Math.Max(1, (toolbar.Width - D(52)) / 7));
            int x = (toolbar.Width - 7 * w - D(48)) / 2;
            foreach (Control control in new Control[] { iZoomOut, iZoomIn, iFit, previousPage, pageCounter, nextPage, iRotate, iRepeat })
            {
                int width = control == pageCounter ? D(48) : w;
                control.SetBounds(x, D(4), width, toolbar.Height - D(8));
                x += width;
            }
        }

        void SelectPage(int index)
        {
            if (busy || index < 0 || index >= pages.Count) return;
            sel = index;
            preview.ResetView();
            RefreshAll();
        }

        void LayoutRight()
        {
            if (cScan == null || right == null) return;
            int W = right.ClientSize.Width, H = right.ClientSize.Height;
            Padding ci = right.CardInset;
            bool c = collapsed;
            Control[] full = new Control[] { tabs, bCollapse, basicPage, extraPage, card, bPreview, bScan };
            foreach (Control ctl in full) ctl.Visible = !c;
            bExpand.Visible = c; cPreview.Visible = c; cScan.Visible = c;
            if (c)
            {
                int cw = W - ci.Horizontal;
                int s = D(44), x0 = ci.Left + (cw - s) / 2;
                bExpand.SetBounds(x0, ci.Top + D(14), s, s);
                int bs = D(48);
                cScan.SetBounds(ci.Left + (cw - bs) / 2, H - ci.Bottom - D(14) - bs, bs, bs);
                cPreview.SetBounds(x0, cScan.Top - D(10) - s, s, s);
                return;
            }
            int pad = D(18);
            int x = ci.Left + pad, w = W - ci.Horizontal - pad * 2;
            int top0 = ci.Top + pad;
            tabs.SetBounds(x, top0, w - D(48), D(38));
            bCollapse.SetBounds(x + w - D(40), top0, D(40), D(38));
            int top = top0 + D(38) + D(16);
            int by = H - ci.Bottom - pad - D(46);
            bScan.SetBounds(x, by, w, D(46));
            bPreview.SetBounds(x, by - D(44), w, D(36));
            card.SetBounds(x, bPreview.Top - D(8) - D(76), w, D(76));
            int pageH = Math.Max(D(40), card.Top - D(12) - top);
            basicPage.SetBounds(0, top, W, pageH);
            extraPage.SetBounds(0, top, W, pageH);
            basicPage.Visible = tabs.SelectedIndex == 0;
            extraPage.Visible = tabs.SelectedIndex == 1;

            int titleHeight = Math.Min(D(46), pageH / 6);
            parameterTitle.SetBounds(x, 0, w, titleHeight);
            int rowHeight = Math.Min(D(52), (pageH - titleHeight) / basicChips.Length);
            int labelWidth = w * 39 / 100;
            int y = titleHeight;
            for (int i = 0; i < basicChips.Length; i++)
            {
                basicLabels[i].SetBounds(x, y, labelWidth, rowHeight);
                basicChips[i].SetBounds(x + labelWidth, y + D(2), w - labelWidth, Math.Max(1, rowHeight - D(5)));
                y += rowHeight;
            }

            int quickTop = y + D(8);
            // «Дополнительно»
            bool timerChips = st.Timer;
            float needE = switches.Length * (D(52) + D(8)) + (timerChips ? D(40) : 0);
            float fe = Math.Min(1f, pageH / needE);
            int sh = Math.Max(D(20), (int)(D(52) * fe)), gap = Math.Max(1, (int)(D(8) * fe));
            bool quick = tabs.SelectedIndex == 0 && pageH - quickTop >= D(84);
            y = 0;
            foreach (SwitchRow s in switches)
            {
                if (quick && (s == sOcr || s == sDuplex))
                {
                    if (s.Parent != basicPage) basicPage.Controls.Add(s);
                    s.SetBounds(x, quickTop, w, D(40));
                    quickTop += D(42);
                }
                else
                {
                    if (s.Parent != extraPage) extraPage.Controls.Add(s);
                    s.SetBounds(x, y, w, sh);
                }
                y += sh + gap;
            }
            cTimerSec.Visible = timerChips;
            cTimerSec.SetBounds(x, y, w, Math.Max(D(26), (int)(D(32) * fe)));
        }

        void SetCollapsed(bool c, bool manual)
        {
            collapsed = c;
            if (manual) { st.Collapsed = c; autoCollapsed = false; st.Save(); }
            if (right == null) return;
            right.Width = c ? D(84) : D(384);
            LayoutRight();
        }

        // настройки
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
            SettingsPopup sp = new SettingsPopup(st.ScaleIdx);
            sp.LangChanged = delegate(int i)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (busy) { SetStatus(L.T("Язык можно сменить, когда закончится сканирование.", "Wait for the scan to finish before changing the language."), 0); return; }
                    st.Lang = i == 1 ? "en" : "ru"; st.Save();
                    L.En = i == 1;
                    statusText = null; statusKind = 0;
                    ClosePopup();
                    RebuildUi();
                    ShowSettings();
                });
            };
            sp.ScaleChanged = delegate(int i)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (busy) { SetStatus(L.T("Масштаб можно сменить, когда закончится сканирование.", "Wait for the scan to finish before changing the scale."), 0); return; }
                    st.ScaleIdx = i; st.Save();
                    ClosePopup();
                    Theme.Ui = SettingsPopup.Scales[i] / 100f;
                    RebuildUi();
                    ShowSettings();
                });
            };
            Point anchor = navSettings.PointToScreen(new Point(navSettings.Width + D(8), navSettings.Height - sp.Height));
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
            bits.Add(cColor.Items[st.ColorMode].ToLower(L.Culture));
            bits.Add(Dpis[st.DpiIdx] + " dpi");
            bits.Add(cArea.Items[st.Area]);
            bits.Add(cFormat.Items[st.Format]);
            if (st.Enhance) bits.Add(L.T("улучшение", "enhance"));
            if (st.Ocr) bits.Add(L.T("распознавание текста", "text recognition"));
            if (st.Split) bits.Add(L.T("деление на объекты", "split into objects"));
            SetStatus(L.T("Пресет «", "Preset “") + p.Name + L.T("»: ", "”: ") + string.Join(", ", bits.ToArray()) + ".", 0);
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
            if (string.Equals(folder.TrimEnd('\\'), desktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) fname = L.T("Рабочий стол", "Desktop");
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
                    card.Status = L.T("Следующая страница добавится в ", "The next page will be added to ") + Path.GetFileName(docPath);
                else card.Status = L.T("Следующий файл: ", "Next file: ") + Path.GetFileName(NextName(Ext()));
            }
            card.Openable = statusKind == 1 && statusText != null && lastSaved != null;
            card.Invalidate();
        }

        void UpdateButtons()
        {
            if (bScan == null) return;
            string scanText;
            if (busy && !busyPreview) scanText = L.T("Сканирую…", "Scanning…");
            else if (series) scanText = countdown > 0 ? L.T("Стоп · ", "Stop · ") + countdown : L.T("Стоп", "Stop");
            else if (st.Timer) scanText = L.T("Начать серию", "Start series");
            else scanText = L.T("Сканировать", "Scan");
            bScan.Text = scanText;
            bScan.IconKind = series ? "stop" : (st.Timer ? "timer" : "scan");
            cScan.IconKind = bScan.IconKind;
            string sec, secIcon = "eye";
            if (busy && busyPreview) sec = L.T("Готовлю просмотр…", "Previewing…");
            else if (duplexPhase == 1 && pages.Count > duplexStart) { sec = L.T("К оборотным", "To backs"); secIcon = "repeat"; }
            else if (duplexPhase == 2) { sec = L.T("Собрать", "Assemble"); secIcon = "check"; }
            else sec = L.T("Просмотр", "Preview");
            bPreview.Text = sec;
            cPreview.Kind = secIcon;
            tips.SetToolTip(cPreview, sec);
            bScan.Invalidate(); cScan.Invalidate(); bPreview.Invalidate(); cPreview.Invalidate();
            if (duplexPhase == 1) thumbs.Note = L.T("Лицевые: ", "Fronts: ") + (pages.Count - duplexStart);
            else if (duplexPhase == 2) thumbs.Note = L.T("Оборотные: ", "Backs: ") + (pages.Count - duplexStart - duplexFronts) + L.T(" из ", " of ") + duplexFronts;
            else thumbs.Note = null;
            thumbs.Invalidate();
        }

        void RefreshAll()
        {
            if (preview == null) return;
            thumbs.Pages = pages;
            thumbs.Selected = sel;
            pageCounter.Text = (sel + 1) + " / " + pages.Count;
            previousPage.Enabled = !busy && sel > 0;
            nextPage.Enabled = !busy && sel + 1 < pages.Count;
            thumbs.AllowReorder = st.Format == 0 && st.Merge && duplexPhase == 0;
            thumbs.Invalidate();
            Bitmap show = null;
            string a = "—", b = "—";
            if (sel >= 0 && sel < pages.Count)
            {
                Page p = pages[sel];
                show = p.Preview;
                a = p.Full.Width + " × " + p.Full.Height + " px";
                b = p.SavedPath == null ? L.T("не сохранено", "not saved") : SizeText(p.SavedPath);
            }
            else if (previewScan != null)
            {
                show = previewScan;
                a = L.T("Черновой просмотр", "Draft preview");
                b = "150 dpi";
            }
            if (!ReferenceEquals(show, shownRef))
            {
                shownRef = show;
                if (show != null && show == previewScan) preview.Sel = cropN;
                else preview.ClearSel();
            }
            if (sel >= 0 && sel < pages.Count) { preview.SelectMode = 2; preview.Hint = L.T("Выделите часть страницы, которую хотите оставить", "Drag to select the part of the page to keep"); }
            else if (previewScan != null) { preview.SelectMode = 1; preview.Hint = L.T("Выделите участок для сканирования", "Drag to select the area to scan"); }
            else { preview.SelectMode = 0; preview.Hint = null; }
            preview.Image = show;
            preview.Invalidate();
            preview.InfoA = a; preview.InfoB = b;
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
                    return (new FileInfo(path).Length / 1048576.0).ToString("0.0", L.Culture) + L.T(" МБ", " MB");
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
            SetStatus(L.T("Новый документ. Готовые сканы остались в папке.", "New document. Your scans are still in the folder."), 0);
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
            SetStatus(L.T("Новый документ. Сохранённые файлы остались в папке.", "New document. Saved files stay in the folder."), 0);
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
            if (series) { StopSeries(L.T("Серия остановлена.", "Series stopped.")); return; }
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
            SetBusy(true, isPreview, isPreview ? L.T("Готовлю предпросмотр…", "Preparing preview…") : L.T("Идёт сканирование…", "Scanning…"));
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
                            if (res.Count > 0) Post(L.T("Выравниваю и отбеливаю…", "Straightening and whitening…"));
                            for (int k = 0; k < res.Count; k++) res[k] = Enhance.Apply(res[k]);
                        }
                        if (sp.Ocr)
                        {
                            Post(L.T("Распознаю текст…", "Recognizing text…"));
                            words = new List<List<OcrWord>>();
                            foreach (Bitmap b in res)
                            {
                                try { words.Add(Ocr.Recognize(b)); }
                                catch (Exception ex) { words.Add(null); Ocr.LastInfo = L.T("Распознавание не сработало: ", "Text recognition failed: ") + ex.Message; }
                            }
                        }
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke((MethodInvoker)delegate { OnScanDone(res, words, err, sp, isPreview, forced != null); }); }
                catch { }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
        }

        void OnScanDone(List<Bitmap> res, List<List<OcrWord>> words, string err, ScanParams sp, bool isPreview, bool repeat)
        {
            SetBusy(false, false, null);
            if (err != null) { if (series) StopSeries(null); SetStatus(err, 2); return; }
            if (res == null || res.Count == 0) { if (series) StopSeries(null); SetStatus(L.T("Сканер не вернул изображение.", "The scanner returned no image."), 2); return; }

            if (isPreview)
            {
                if (previewScan != null) previewScan.Dispose();
                previewScan = Img.Scaled(res[0], 1400);
                foreach (Bitmap b in res) b.Dispose();
                sel = -1;
                preview.ResetView();
                RefreshAll();
                SetStatus(L.T("Выделите нужный участок или нажмите «Сканировать» для всего листа.", "Select an area or press “Scan” for the whole sheet."), 0);
                return;
            }

            if (previewScan != null) { previewScan.Dispose(); previewScan = null; }
            List<Page> added = new List<Page>();
            for (int i = 0; i < res.Count; i++)
            {
                Page p = new Page(res[i], sp.Dpi);
                if (words != null && i < words.Count) p.Words = words[i];
                added.Add(p);
            }
            if (repeat)
            {
                try { ReplaceLastScan(added); }
                catch (Exception ex)
                {
                    foreach (Page p in added) p.Dispose();
                    SetStatus(ex is InvalidDataException ? ex.Message : SaveError(ex), 2);
                }
                RefreshAll();
                return;
            }

            pages.AddRange(added);
            lastParams = sp.Clone();
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
            if (!any) return " · " + (Ocr.LastInfo ?? L.T("текст не распознан", "text not recognized"));
            return n > 0 ? L.T(" · текст распознан", " · text recognized") : L.T(" · текста не найдено", " · no text found");
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
                    SetStatus(L.T("Сохранено: ", "Saved: ") + Path.GetFileName(docPath) + L.T(" · страниц: ", " · pages: ") + pages.Count + OcrNote(added), 1);
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
                    string more = added.Count > 1 ? L.T(" и ещё ", " and ") + (added.Count - 1) + L.T("", " more") : "";
                    SetStatus(L.T("Сохранено: ", "Saved: ") + Path.GetFileName(lastSaved) + more + OcrNote(added), 1);
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
            if (ex is UnauthorizedAccessException) return L.T("Нет доступа к папке. Выберите другую через «Изменить».", "No access to the folder. Pick another one via “Change”.");
            if (ex is IOException) return L.T("Не удалось записать файл: возможно, он открыт в другой программе.", "Couldn’t write the file: it may be open in another program.");
            return L.T("Не удалось сохранить: ", "Couldn’t save: ") + ex.Message;
        }

        // двусторонний вручную
        bool HandleDuplex(List<Page> added, ScanParams sp)
        {
            if (duplexPhase == 0) { duplexPhase = 1; duplexStart = pages.Count - added.Count; }
            if (duplexPhase == 1)
            {
                SetStatus(L.T("Лицевые стороны: ", "Fronts: ") + (pages.Count - duplexStart) + L.T(". Закончите — нажмите «К оборотным».", ". When done, press “To backs”."), 0);
                return false;
            }
            duplexReverse = sp.Feeder;
            int backs = pages.Count - duplexStart - duplexFronts;
            if (backs >= duplexFronts) return AssembleDuplex();
            SetStatus(L.T("Оборотные стороны: ", "Backs: ") + backs + L.T(" из ", " of ") + duplexFronts + ".", 0);
            return false;
        }

        void GoBacks()
        {
            duplexFronts = pages.Count - duplexStart;
            duplexPhase = 2;
            SetStatus(L.T("Переверните листы и сканируйте оборотные стороны: 0 из ", "Flip the sheets and scan the backs: 0 of ") + duplexFronts + ".", 0);
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
            if (lastParams == null || lastAdded.Count == 0) { SetStatus(L.T("Сначала отсканируйте лист.", "Scan a sheet first."), 0); return; }
            StopSeries(null);
            DoScan(false, lastParams);
        }

        void ReplaceLastScan(List<Page> added)
        {
            bool pending = duplexPhase != 0;
            if (pending && (added.Count != lastAdded.Count || lastAdded.Exists(p => p.SavedPath != null)))
                throw new InvalidDataException(L.T("Для повтора загрузите столько же листов в текущем двустороннем скане.",
                    "Reload the same number of sheets for the current duplex scan."));

            List<Page> updated = new List<Page>();
            foreach (Page page in pages)
            {
                int index = lastAdded.IndexOf(page);
                if (index < 0) updated.Add(page);
                else
                {
                    if (index < added.Count) updated.Add(added[index]);
                    if (index == lastAdded.Count - 1 && added.Count > lastAdded.Count)
                        updated.AddRange(added.GetRange(lastAdded.Count, added.Count - lastAdded.Count));
                }
            }

            if (!pending && st.Format == 0 && st.Merge)
            {
                string path = docPath ?? NextName("pdf");
                Pdf.Write(path, updated);
                docPath = lastSaved = path;
                foreach (Page p in updated) p.SavedPath = path;
                History.Add(path, updated.Count);
            }
            else if (!pending)
            {
                SaveReplacementFiles(added);
                foreach (Page p in lastAdded) Recycle(p.SavedPath);
                lastSaved = added[added.Count - 1].SavedPath;
            }
            foreach (Page p in lastAdded) p.Dispose();
            pages.Clear();
            pages.AddRange(updated);
            lastAdded = added;
            sel = pages.IndexOf(added[added.Count - 1]);
            preview.ResetView();
            SetStatus(L.T("Последний скан заменён.", "Last scan replaced."), 1);
        }

        void SaveReplacementFiles(List<Page> added)
        {
            string ext = Ext();
            try
            {
                foreach (Page p in added)
                {
                    string path = NextName(ext);
                    string temp = Path.Combine(Path.GetDirectoryName(path), Guid.NewGuid().ToString("N") + "." + ext);
                    try
                    {
                        SavePage(p, temp);
                        File.Move(temp, path);
                        p.SavedPath = path;
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
            catch
            {
                foreach (Page p in added) if (p.SavedPath != null) Recycle(p.SavedPath);
                throw;
            }
            foreach (Page p in added) History.Add(p.SavedPath, 1);
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
                Resave(p, L.T("Повернул и пересохранил ", "Rotated and re-saved "));
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
            SetBusy(true, false, L.T("Распознаю текст заново…", "Recognizing text again…"));
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
                SetStatus(L.T("Отсканируется только выделенная область. Сбросить — клик по листу или «Весь стол».", "Only the marked area will be scanned. To reset, click the sheet or pick “Full bed”."), 0);
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
            Resave(p, L.T("Обрезал и пересохранил ", "Cropped and re-saved "));
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
                    if (savedPages.Count > 0) { Pdf.Write(docPath, savedPages); msg = L.T("Страница удалена, ", "Page deleted, ") + Path.GetFileName(docPath) + L.T(" пересохранён.", " re-saved."); }
                    else { Recycle(docPath); msg = L.T("Последняя страница удалена, файл отправлен в корзину.", "Last page deleted, the file was moved to the Recycle Bin."); docPath = null; }
                }
                else if (p.SavedPath != null)
                {
                    Recycle(p.SavedPath);
                    msg = L.T("Страница удалена, ", "Page deleted, ") + Path.GetFileName(p.SavedPath) + L.T(" в корзине.", " is in the Recycle Bin.");
                }
                else msg = L.T("Страница удалена.", "Page deleted.");
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
                try { Pdf.Write(docPath, pages); SetStatus(L.T("Порядок страниц сохранён в ", "Page order saved to ") + Path.GetFileName(docPath), 1); lastSaved = docPath; }
                catch (Exception ex) { SetStatus(SaveError(ex), 2); }
            }
            RefreshAll();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (series) { StopSeries(L.T("Серия остановлена.", "Series stopped.")); return true; }
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
                d.Description = L.T("Куда сохранять сканы", "Where to save scans");
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

        void OpenDevices(Control anchor)
        {
            if (busy || series || detecting) return;
            ContextMenuStrip menu = WorkspaceMenu.Create();
            foreach (Scanner.Device device in devices)
            {
                Scanner.Device choice = device;
                ToolStripMenuItem item = new ToolStripMenuItem(device.Name);
                item.Checked = deviceState == 1 && (requestedDevice == device.Id || (requestedDevice == null && deviceName == device.Name));
                item.Click += delegate { requestedDevice = choice.Id; DetectDevice(); };
                menu.Items.Add(item);
            }
            if (devices.Count > 0) menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("Обновить список", "Refresh devices"), null, delegate { DetectDevice(); });
            Point at = anchor == title ? new Point(NavWidth + D(10), title.Height) : new Point(anchor.Width, 0);
            menu.Show(anchor, at);
        }

        void DetectDevice()
        {
            if (detecting || busy || series) return;
            detecting = true;
            deviceState = 0;
            title.SetDevice(DeviceText(), deviceState);
            List<Scanner.Device> found = new List<Scanner.Device>();
            string id = requestedDevice;
            Thread th = new Thread(delegate()
            {
                string name = null, diag = null;
                try { name = Scanner.FindName(id, found, out diag); }
                catch (Exception ex) { diag = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        detecting = false;
                        devices = found;
                        if (name != null)
                        {
                            deviceName = name; deviceState = 1;
                            if (statusKind == 2) { statusText = null; statusKind = 0; UpdateCard(); }
                        }
                        else
                        {
                            deviceState = 2;
                            SetStatus(diag ?? L.T("Сканер не найден.", "Scanner not found."), 2);
                        }
                        title.SetDevice(DeviceText(), deviceState);
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
