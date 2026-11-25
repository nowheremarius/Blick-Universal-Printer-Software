using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;

namespace Blick
{
    class ScanException : Exception { public ScanException(string m) : base(m) { } }

    class ScanParams
    {
        public bool Feeder;
        public int ColorMode; // 0 цвет, 1 серый, 2 ч/б
        public int Dpi = 300;
        public int Area;      // 0 весь стол, 1 A4, 2 A5
        public RectangleF Crop = RectangleF.Empty; // своя рамка в долях стола
        public bool Enhance, Split, Ocr;
        public ScanParams Clone() { return (ScanParams)MemberwiseClone(); }
    }

    static class Scanner
    {
        const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";

        static object Get(object o, string name, params object[] args)
        {
            BindingFlags f = args.Length == 0 ? BindingFlags.GetProperty : (BindingFlags.GetProperty | BindingFlags.InvokeMethod);
            return o.GetType().InvokeMember(name, f, null, o, args);
        }
        static object Call(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
        }
        static void Put(object o, string name, object val)
        {
            o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new object[] { val });
        }
        static int Count(object coll) { return Convert.ToInt32(Get(coll, "Count")); }
        static object Item(object coll, int i) { return Get(coll, "Item", (object)i); }

        static Exception Root(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        static object CreateManager(out string diag)
        {
            diag = null;
            Type t = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (t == null) { diag = "В Windows нет компонента WIA (wiaaut.dll)."; return null; }
            try { return Activator.CreateInstance(t); }
            catch (Exception ex)
            {
                diag = "Не запускается служба «Служба загрузки изображений Windows (WIA)»: " + Root(ex).Message;
                return null;
            }
        }

        static string InfoName(object info)
        {
            try
            {
                object props = Get(info, "Properties");
                int n = Count(props);
                for (int i = 1; i <= n; i++)
                {
                    object p = Item(props, i);
                    if (Convert.ToString(Get(p, "Name")) == "Name") return Convert.ToString(Get(p, "Value"));
                }
            }
            catch { }
            return null;
        }

        static object FindInfo(out string diag)
        {
            object mgr = CreateManager(out diag);
            if (mgr == null) return null;
            object best = null;
            List<string> seen = new List<string>();
            try
            {
                object infos = Get(mgr, "DeviceInfos");
                int n = Count(infos);
                for (int i = 1; i <= n; i++)
                {
                    object info = Item(infos, i);
                    int type = 0;
                    try { type = Convert.ToInt32(Get(info, "Type")); } catch { }
                    string name = InfoName(info) ?? "без имени";
                    seen.Add(name + (type == 1 ? " (сканер)" : type == 2 ? " (камера)" : " (тип " + type + ")"));
                    if (type != 1) continue;
                    if (best == null) best = info;
                    if (name.IndexOf("pantum", StringComparison.OrdinalIgnoreCase) >= 0) { best = info; break; }
                }
            }
            catch (Exception ex)
            {
                diag = "Ошибка при поиске устройств WIA: " + Root(ex).Message;
                return null;
            }
            if (best == null)
                diag = seen.Count == 0
                    ? "Windows не видит ни одного сканера (WIA-устройств: 0). Добавьте сканер в Windows: Параметры → Принтеры и сканеры → Добавить устройство."
                    : "WIA видит только: " + string.Join(", ", seen.ToArray()) + ". Сканера среди них нет.";
            return best;
        }

        // Подключение WIA остаётся в одном STA-потоке между сканами.
        static readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        static Thread worker;
        static readonly object workerLock = new object();

        static void OnWorker(Action a, bool wait)
        {
            lock (workerLock)
            {
                if (worker == null)
                {
                    worker = new Thread(delegate()
                    {
                        foreach (Action x in queue.GetConsumingEnumerable()) { try { x(); } catch { } }
                    });
                    worker.Name = "Blick scanner";
                    worker.SetApartmentState(ApartmentState.STA);
                    worker.IsBackground = true;
                    worker.Start();
                }
            }
            if (!wait) { queue.Add(a); return; }
            if (Thread.CurrentThread == worker) { a(); return; }
            Exception err = null;
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                queue.Add(delegate
                {
                    try { a(); } catch (Exception ex) { err = ex; }
                    finally { done.Set(); }
                });
                done.WaitOne();
            }
            if (err != null) throw err;
        }

        // кэш подключения (живёт только в потоке сканера)
        static object cInfo, cDev, cItem;
        static string cName;

        static void ResetCache()
        {
            cInfo = null; cDev = null; cItem = null; cName = null;
        }

        // Открываем подключение при поиске устройства, до первого скана.
        static void EnsureConnected()
        {
            if (cItem != null) return;
            if (cInfo == null)
            {
                string diag;
                object info = FindInfo(out diag);
                if (info == null) throw new ScanException("Сканер не найден. " + diag);
                cInfo = info;
                cName = InfoName(info) ?? "Сканер";
            }
            object dev = Call(cInfo, "Connect");
            object items = Get(dev, "Items");
            if (Count(items) < 1) throw new ScanException("Драйвер сканера не сообщил, какие источники доступны.");
            cItem = Item(items, 1);
            cDev = dev;
        }

        // Ищем сканер при запуске и по щелчку на названии устройства.
        public static string FindName(out string diag)
        {
            string name = null, d = null;
            OnWorker(delegate
            {
                ResetCache();
                object info = FindInfo(out d);
                if (info == null) return;
                cInfo = info;
                cName = InfoName(info) ?? "Сканер";
                name = cName;
                try { EnsureConnected(); } catch { cDev = null; cItem = null; }
            }, true);
            diag = d;
            return name;
        }

        // Свойство по ID: сначала прямой запрос Properties("6147"), перебор — только как запасной путь
        static object FindProp(object props, int id)
        {
            try { return Get(props, "Item", (object)id.ToString(CultureInfo.InvariantCulture)); }
            catch { }
            try
            {
                int n = Count(props);
                for (int i = 1; i <= n; i++)
                {
                    object p = Item(props, i);
                    if (Convert.ToInt32(Get(p, "PropertyID")) == id) return p;
                }
            }
            catch { }
            return null;
        }

        static bool SetProp(object props, int id, object val)
        {
            object p = FindProp(props, id);
            if (p == null) return false;
            try { Put(p, "Value", val); return true; } catch { return false; }
        }

        static int GetMax(object props, int id)
        {
            object p = FindProp(props, id);
            if (p == null) return -1;
            try { return Convert.ToInt32(Get(p, "SubTypeMax")); } catch { return -1; }
        }

        public static List<Bitmap> Scan(ScanParams sp)
        {
            List<Bitmap> res = null;
            OnWorker(delegate
            {
                bool cached = cItem != null;
                try { res = ScanOnce(sp); }
                catch (ScanException) { throw; }
                catch (Exception ex)
                {
                    // После сна сканера сохранённое подключение иногда обрывается.
                    // Повтор возможен, пока податчик не протянул первый лист.
                    if (!Retryable(ex)) throw Wrap(ex);
                    ResetCache();
                    if (!cached || gotPages) throw Wrap(ex);
                    try { res = ScanOnce(sp); }
                    catch (Exception ex2) { ResetCache(); throw Wrap(ex2); }
                }
            }, true);
            return res;
        }

        static bool Retryable(Exception ex)
        {
            COMException ce = Root(ex) as COMException;
            if (ce == null) return true;
            switch ((uint)ce.ErrorCode)
            {
                case 0x80210002: case 0x80210003: case 0x80210004: case 0x80210006:
                case 0x80210007: case 0x80210008: case 0x80210016: case 0x80210017: case 0x80210064:
                    return false;
            }
            return true;
        }

        static Exception Wrap(Exception ex)
        {
            Exception r = Root(ex);
            if (r is ScanException) return r;
            COMException ce = r as COMException;
            if (ce != null) return new ScanException(Explain(ce));
            return new ScanException("Не удалось отсканировать: " + r.Message);
        }

        static bool gotPages; // хоть одна страница уже прошла — повторять нельзя (в автоподатчике листы уже ушли)

        static List<Bitmap> ScanOnce(ScanParams sp)
        {
            gotPages = false;
            List<Bitmap> result = new List<Bitmap>();
            try
            {
                EnsureConnected();
                object devProps = Get(cDev, "Properties");
                SetProp(devProps, 3088, sp.Feeder ? 1 : 2);
                if (sp.Feeder) SetProp(devProps, 3096, 1);

                object item = cItem;
                object props = Get(item, "Properties");

                SetProp(props, 6146, sp.ColorMode == 0 ? 1 : (sp.ColorMode == 1 ? 2 : 4));
                SetProp(props, 4103, sp.ColorMode == 0 ? 3 : (sp.ColorMode == 1 ? 2 : 0));
                SetProp(props, 6147, sp.Dpi);
                SetProp(props, 6148, sp.Dpi);
                SetProp(props, 6149, 0);
                SetProp(props, 6150, 0);
                int maxW = GetMax(props, 6151), maxH = GetMax(props, 6152);
                if (sp.Crop.Width > 0 && sp.Crop.Height > 0 && maxW > 0 && maxH > 0)
                {
                    int cx = (int)(sp.Crop.X * maxW), cy = (int)(sp.Crop.Y * maxH);
                    int cw = Math.Max(16, (int)(sp.Crop.Width * maxW)), ch = Math.Max(16, (int)(sp.Crop.Height * maxH));
                    if (cx + cw > maxW) cw = maxW - cx;
                    if (cy + ch > maxH) ch = maxH - cy;
                    SetProp(props, 6151, cw);
                    SetProp(props, 6152, ch);
                    SetProp(props, 6149, cx);
                    SetProp(props, 6150, cy);
                }
                else
                {
                    int w = maxW, h = maxH;
                    if (sp.Area == 1) { w = (int)(8.27 * sp.Dpi); h = (int)(11.69 * sp.Dpi); }
                    else if (sp.Area == 2) { w = (int)(5.83 * sp.Dpi); h = (int)(8.27 * sp.Dpi); }
                    if (maxW > 0 && w > maxW) w = maxW;
                    if (maxH > 0 && h > maxH) h = maxH;
                    if (w > 0) SetProp(props, 6151, w);
                    if (h > 0) SetProp(props, 6152, h);
                }

                int guard = 0;
                while (true)
                {
                    object img;
                    try { img = Call(item, "Transfer", FormatBmp); }
                    catch (Exception ex)
                    {
                        COMException ce = Root(ex) as COMException;
                        if (sp.Feeder && result.Count > 0 && ce != null && (uint)ce.ErrorCode == 0x80210003) break;
                        throw;
                    }
                    byte[] data = (byte[])Get(Get(img, "FileData"), "BinaryData");
                    gotPages = true;
                    result.Add(Img.FromBmpBytes(data));
                    guard++;
                    if (!sp.Feeder || guard > 200) break;
                }
            }
            catch (Exception ex)
            {
                foreach (Bitmap b in result) b.Dispose();
                Exception r = Root(ex);
                if (r is ScanException) throw r;
                throw r is COMException ? r : ex;   // разбор кода — в Scan(), после возможного повтора
            }
            return result;
        }

        static string Explain(COMException ex)
        {
            uint code = (uint)ex.ErrorCode;
            switch (code)
            {
                case 0x80210002: return "В сканере застряла бумага.";
                case 0x80210003: return "В автоподатчике нет бумаги. Положите листы или выберите «Стекло».";
                case 0x80210004: return "Проблема с бумагой. Проверьте автоподатчик.";
                case 0x80210005: return "Сканер не в сети. Проверьте кабель и питание.";
                case 0x80210006: return "Сканер занят. Подождите немного и попробуйте снова.";
                case 0x80210007: return "Сканер прогревается. Попробуйте через минуту.";
                case 0x80210008: return "Проверьте сообщение на экране сканера или его индикаторы.";
                case 0x8021000A: return "Нет связи со сканером. Переподключите его и попробуйте снова.";
                case 0x8021000D: return "Сканер заблокирован другой программой. Закройте её и попробуйте снова.";
                case 0x80210015: return "Сканер не найден. Проверьте питание и подключение.";
                case 0x80210016: return "Открыта крышка сканера.";
                case 0x80210017: return "Лампа сканера выключена.";
                case 0x80210064: return "Сканирование отменено.";
            }
            return "Ошибка сканера (код 0x" + code.ToString("X8") + "): " + ex.Message;
        }
    }


    // распознавание текста (встроенный OCR Windows через PowerShell)
    static class Ocr
    {
        public static string LastInfo;

        const string Script = @"param([string]$path)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bmp = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$engine = $null
$ru = [Windows.Globalization.Language]::new('ru')
if ([Windows.Media.Ocr.OcrEngine]::IsLanguageSupported($ru)) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($ru) }
if ($engine -eq $null) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
if ($engine -eq $null) { Write-Output ""ERR`tnolang""; exit 2 }
$res = Await ($engine.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
Write-Output (""LANG`t"" + $engine.RecognizerLanguage.LanguageTag)
foreach ($line in $res.Lines) { foreach ($w in $line.Words) { $r = $w.BoundingRect; Write-Output ([string]::Format([Globalization.CultureInfo]::InvariantCulture, ""W`t{0}`t{1}`t{2}`t{3}`t{4}"", $r.X, $r.Y, $r.Width, $r.Height, $w.Text)) } }
";

        public static List<OcrWord> Recognize(Bitmap full)
        {
            string dir = Path.Combine(Path.GetTempPath(), "Blick");
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, "ocr.ps1");
            File.WriteAllText(script, Script, new UTF8Encoding(true));
            string png = Path.Combine(dir, "ocr_" + Guid.NewGuid().ToString("N") + ".png");
            float k;
            using (Bitmap s = Img.Scaled(full, 2400))
            {
                k = (float)s.Width / full.Width;
                s.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            }
            List<OcrWord> words = new List<OcrWord>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" \"" + png + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    string err = null;
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null && err == null) err = e.Data; };
                    p.BeginErrorReadLine();
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        line = line.TrimStart('\uFEFF');
                        string[] parts = line.Split('\t');
                        if (parts.Length >= 6 && parts[0] == "W")
                        {
                            float x, y, w, h;
                            if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                                float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out w) &&
                                float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out h))
                            {
                                OcrWord ow = new OcrWord();
                                ow.Box = new RectangleF(x / k, y / k, w / k, h / k);
                                ow.Text = string.Join("\t", parts, 5, parts.Length - 5);
                                words.Add(ow);
                            }
                        }
                        else if (parts.Length >= 2 && parts[0] == "LANG") LastInfo = "Распознавание: " + parts[1];
                        else if (parts.Length >= 2 && parts[0] == "ERR") LastInfo = "В Windows не установлен язык распознавания текста.";
                    }
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } }
                    if (words.Count == 0 && err != null) LastInfo = "Распознавание не сработало: " + err;
                }
            }
            finally { try { File.Delete(png); } catch { } }
            return words;
        }
    }

    // история
    static class History
    {
        public class Entry
        {
            public string Path;
            public DateTime Time;
            public int Pages;
        }

        static string FilePath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blick", "history.txt"); }
        }

        public static List<Entry> Load()
        {
            List<Entry> list = new List<Entry>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string[] p = line.Split(new char[] { '|' }, 3);
                    if (p.Length < 3) continue;
                    long ticks; int pages;
                    if (!long.TryParse(p[0], out ticks) || !int.TryParse(p[1], out pages)) continue;
                    Entry e = new Entry();
                    e.Time = new DateTime(ticks); e.Pages = pages; e.Path = p[2];
                    list.Add(e);
                }
            }
            catch { }
            return list;
        }

        static void Save(List<Entry> list)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                List<string> lines = new List<string>();
                foreach (Entry e in list) lines.Add(e.Time.Ticks + "|" + e.Pages + "|" + e.Path);
                File.WriteAllLines(FilePath, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        public static void Add(string path, int pages)
        {
            List<Entry> list = Load();
            list.RemoveAll(delegate(Entry e) { return string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase); });
            Entry n = new Entry();
            n.Path = path; n.Time = DateTime.Now; n.Pages = pages;
            list.Insert(0, n);
            if (list.Count > 40) list.RemoveRange(40, list.Count - 40);
            Save(list);
        }

        public static void Clear() { Save(new List<Entry>()); }
    }

    // пресеты
    class Preset
    {
        public string Name;
        public int ColorMode, DpiIdx, Area, Format;
        public bool Enhance, Ocr, Split;
        public Preset(string name, int color, int dpi, int area, int format, bool enhance, bool ocr, bool split)
        {
            Name = name; ColorMode = color; DpiIdx = dpi; Area = area; Format = format; Enhance = enhance; Ocr = ocr; Split = split;
        }
        public static readonly Preset[] All = new Preset[] {
            new Preset("Документ", 1, 1, 1, 0, true, true, false),
            new Preset("Чек", 1, 1, 0, 0, true, false, true),
            new Preset("Фото", 0, 2, 0, 1, false, false, true),
            new Preset("Паспорт", 0, 1, 2, 0, false, false, false) };
    }

    // настройки
    class Settings
    {
        public int Source, ColorMode, DpiIdx = 1, Area, Format;
        public bool Merge = true, Enhance = true, Ocr, Split, Duplex, Timer, Collapsed;
        public int TimerIdx = 1, ThemeKind, ScaleIdx = 2, Tab;
        public string Folder = "";
        public string Accent = "5835F3";

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blick", "settings.ini"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                bool sawAccent2 = false;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    int n; int.TryParse(v, out n);
                    bool b = v == "1";
                    switch (k)
                    {
                        case "source": s.Source = n; break;
                        case "color": s.ColorMode = n; break;
                        case "dpi": s.DpiIdx = n; break;
                        case "area": s.Area = n; break;
                        case "format": s.Format = n; break;
                        case "merge": s.Merge = b; break;
                        case "enhance": s.Enhance = b; break;
                        case "ocr": s.Ocr = b; break;
                        case "split": s.Split = b; break;
                        case "duplex": s.Duplex = b; break;
                        case "timer": s.Timer = b; break;
                        case "timeridx": s.TimerIdx = n; break;
                        case "theme": s.ThemeKind = n; break;
                        case "scale": s.ScaleIdx = n; break;
                        case "collapsed": s.Collapsed = b; break;
                        case "tab": s.Tab = n; break;
                        case "folder": s.Folder = v; break;
                        case "accent2": s.Accent = v; sawAccent2 = true; break;
                    }
                }
                if (!sawAccent2) s.Accent = "5835F3";
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new string[] {
                    "source=" + Source, "color=" + ColorMode, "dpi=" + DpiIdx, "area=" + Area, "format=" + Format,
                    "merge=" + B(Merge), "enhance=" + B(Enhance), "ocr=" + B(Ocr), "split=" + B(Split), "duplex=" + B(Duplex),
                    "timer=" + B(Timer), "timeridx=" + TimerIdx, "theme=" + ThemeKind, "scale=" + ScaleIdx,
                    "collapsed=" + B(Collapsed), "tab=" + Tab, "folder=" + Folder, "accent2=" + Accent }, Encoding.UTF8);
            }
            catch { }
        }
        static string B(bool v) { return v ? "1" : "0"; }
    }
}
