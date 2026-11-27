using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace Blick
{
    internal static class ReleaseChecks
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        static void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
        }

        static Page NewPage() { return new Page(new Bitmap(32, 32), 300); }

        static void PdfChecks(string folder)
        {
            string path = Path.Combine(folder, "test.pdf");
            using (Page first = NewPage())
            using (Page second = NewPage())
            {
                Pdf.Write(path, new Page[] { first });
                byte[] original = File.ReadAllBytes(path);
                bool failed = false;
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { Pdf.Write(path, new Page[] { first, second }); }
                    catch (IOException) { failed = true; }
                }
                Check(failed && Convert.ToBase64String(original) == Convert.ToBase64String(File.ReadAllBytes(path)),
                    "A blocked PDF replacement changed the original.");
                Pdf.Write(path, new Page[] { first, second });
                Check(File.ReadAllText(path).Contains("/Count 2"), "Two-page PDF was not committed.");
                Check(Directory.GetFiles(folder, "*.tmp").Length == 0, "PDF temporary files left behind.");
            }
        }

        static void RepeatChecks(string folder)
        {
            using (MainForm form = new MainForm())
            using (Page old = NewPage())
            {
                Type t = typeof(MainForm);
                Settings st = (Settings)t.GetField("st", Private).GetValue(form);
                st.Folder = folder;
                st.Format = 1;
                old.SavedPath = Path.Combine(folder, "original.jpg");
                Img.SaveJpeg(old.Full, old.SavedPath, 92, 300);
                string original = Convert.ToBase64String(File.ReadAllBytes(old.SavedPath));
                List<Page> pages = (List<Page>)t.GetField("pages", Private).GetValue(form);
                pages.Add(old);
                var last = new List<Page> { old };
                t.GetField("lastAdded", Private).SetValue(form, last);
                MethodInfo done = t.GetMethod("OnScanDone", Private);
                done.Invoke(form, new object[] { null, null, "Device offline", new ScanParams(), false, true });
                Check(pages.Count == 1 && pages[0] == old && File.Exists(old.SavedPath), "Failed rescan removed the original.");
                using (Page first = NewPage())
                using (Page broken = NewPage())
                {
                    broken.Full.Dispose();
                    bool failed = false;
                    try { t.GetMethod("ReplaceLastScan", Private).Invoke(form, new object[] { new List<Page> { first, broken } }); }
                    catch (TargetInvocationException) { failed = true; }
                    Check(failed && pages.Count == 1 && pages[0] == old && Directory.GetFiles(folder, "*.jpg").Length == 1 &&
                        original == Convert.ToBase64String(File.ReadAllBytes(old.SavedPath)), "Partial batch damaged the previous scan.");
                }
                done.Invoke(form, new object[] { new List<Bitmap> { new Bitmap(32, 32) }, null, null, new ScanParams(), false, true });
                Check(pages.Count == 1 && pages[0] != old && File.Exists(pages[0].SavedPath) && !File.Exists(old.SavedPath),
                    "Successful rescan did not replace the original.");
                pages[0].Dispose();
            }
        }

        static ProcessStartInfo Command(string command)
        {
            return new ProcessStartInfo("cmd.exe", "/d /c " + command) {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
        }

        [STAThread]
        static void Main()
        {
            string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                PdfChecks(folder);
                RepeatChecks(folder);
                string errors;
                Check(Ocr.ReadOutput(Command("echo ready"), 10000, out errors).Trim() == "ready", "Missing OCR output.");
                bool expired = false;
                var hung = new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 30") {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                var elapsed = Stopwatch.StartNew();
                try { Ocr.ReadOutput(hung, 700, out errors); }
                catch (TimeoutException) { expired = true; }
                Check(expired && elapsed.ElapsedMilliseconds < 15000, "Silent OCR process outlived timeout.");
                Console.WriteLine("Passed PDF replacement, rescan rollback, successful rescan and OCR timeout checks.");
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}
