// ============================================================================
//  Sensors - small system observers that feed the island:
//      LanguageWatcher   keyboard layout switches (rus / eng / ukr ...)
//      AppLaunchWatcher  a newly opened application window
//      RadioWatcher      Bluetooth / Wi-Fi turning on or off
//      DownloadWatcher   browser downloads, best effort (see the notes there)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DynamicIsland
{
    // ------------------------------------------------------------ language
    /// <summary>
    /// Reports keyboard-layout changes. The label is the culture's two/three letter
    /// code, so it reads "rus", "eng", "ukr" and so on.
    /// </summary>
    internal sealed class LanguageWatcher : IDisposable
    {
        public event Action<string> Changed;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);

        private IntPtr last;
        private System.Windows.Forms.Timer timer;

        /// <summary>
        /// Layout of the window the user is actually typing in. Reading the island
        /// window's own layout would be useless: that window is never activated, so
        /// its thread layout never changes.
        /// </summary>
        public static IntPtr ForegroundLayout()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    uint pid;
                    uint tid = GetWindowThreadProcessId(fg, out pid);
                    if (tid != 0)
                    {
                        IntPtr h = GetKeyboardLayout(tid);
                        if (h != IntPtr.Zero) return h;
                    }
                }
                // no foreground window (or it has no thread): fall back to our own thread
                return GetKeyboardLayout(0);
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// Layout handle -> short code. The low word of an HKL is a language id packed
        /// as (primary | sublanguage &lt;&lt; 10), e.g. Russian is 0x419, NOT 0x19, so the
        /// table is built straight from the OS locale list instead of hardcoded.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<int, string> Labels = BuildLabels();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetLocaleInfoW(int locale, int lcType, System.Text.StringBuilder data, int cch);

        private const int LOCALE_SISO639LANGNAME = 0x0059;

        private static System.Collections.Generic.Dictionary<int, string> BuildLabels()
        {
            var d = new System.Collections.Generic.Dictionary<int, string>();
            try
            {
                foreach (System.Globalization.CultureInfo c in System.Globalization.CultureInfo.GetCultures(
                             System.Globalization.CultureTypes.AllCultures))
                {
                    int lcid;
                    try { lcid = c.LCID; } catch { continue; }
                    if (lcid <= 0) continue;
                    try
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder(16);
                        if (GetLocaleInfoW(lcid, LOCALE_SISO639LANGNAME, sb, sb.Capacity) <= 0) continue;
                        string iso = sb.ToString().ToLowerInvariant();
                        if (iso.Length < 2) continue;

                        int primary = lcid & 0x3FF;
                        int sub = (lcid >> 10) & 0x3F;
                        int langId = primary | (sub << 10);
                        if (primary != 0 && !d.ContainsKey(langId)) d[langId] = ThreeLetter(iso);
                    }
                    catch { }
                }
            }
            catch { }
            // a few common ones in case the locale list is unavailable
            if (!d.ContainsKey(0x419)) d[0x419] = "rus";
            if (!d.ContainsKey(0x409)) d[0x409] = "eng";
            if (!d.ContainsKey(0x422)) d[0x422] = "ukr";
            return d;
        }
        /// <summary>Prefer the familiar three-letter form: ru -> rus, en -> eng.</summary>
        private static string ThreeLetter(string iso)
        {
            switch (iso)
            {
                case "ru": return "rus";
                case "en": return "eng";
                case "uk": return "ukr";
                case "be": return "bel";
                case "de": return "deu";
                case "fr": return "fra";
                case "es": return "spa";
                case "it": return "ita";
                case "pl": return "pol";
                case "tr": return "tur";
                case "zh": return "chi";
                case "ja": return "jpn";
                case "ko": return "kor";
                case "ar": return "ara";
                case "he": return "heb";
                case "cs": return "ces";
                case "pt": return "por";
                case "nl": return "nld";
                case "sv": return "swe";
                case "kk": return "kaz";
                case "uz": return "uzb";
                case "hy": return "hye";
                case "ka": return "kat";
                case "az": return "aze";
                case "bg": return "bul";
                case "sr": return "srp";
                case "ro": return "ron";
                case "hu": return "hun";
                case "el": return "ell";
                case "hi": return "hin";
                default: return iso;
            }
        }
        /// <summary>Short label from a keyboard layout handle: 0x4190419 -> "rus".</summary>
        public static string LabelForLayout(IntPtr layout)
        {
            try
            {
                if (layout == IntPtr.Zero) return "";
                int langId = (int)(layout.ToInt64() & 0xFFFFL);
                string label;
                if (Labels == null) return "";
                if (Labels.TryGetValue(langId, out label)) return label;
                return "";
            }
            catch { return ""; }
        }
        public static string CurrentLabel()
        {
            return LabelForLayout(ForegroundLayout());
        }

        public void Start()
        {
            last = ForegroundLayout();
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 250;
            timer.Tick += delegate
            {
                try
                {
                    IntPtr h = ForegroundLayout();
                    if (h == IntPtr.Zero || h == last) return;
                    last = h;
                    string label = LabelForLayout(h);
                    Action<string> c = Changed;
                    if (c != null && label.Length > 0) c(label);
                }
                catch { }
            };
            timer.Start();
        }

        public void Dispose()
        {
            try { if (timer != null) { timer.Stop(); timer.Dispose(); } } catch { }
        }
    }
    // ------------------------------------------------------------ app launches
    /// <summary>
    /// Notices a window appearing for the first time and reports the owning app with
    /// its icon. Polled rather than hooked: a shell hook would need elevation on some
    /// systems and would add a global callback into this process.
    /// </summary>
    internal sealed class AppLaunchWatcher : IDisposable
    {
        public event Action<string, Bitmap> Launched;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
        private delegate bool EnumProc(IntPtr h, IntPtr p);

        private readonly HashSet<IntPtr> known = new HashSet<IntPtr>();
        private readonly Dictionary<string, DateTime> announced = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Forms.Timer timer;

        public void Start()
        {
            Snapshot();                       // only NEW windows are announced
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 900;
            timer.Tick += delegate { Poll(); };
            timer.Start();
        }

        private void Snapshot()
        {
            try { EnumWindows(delegate(IntPtr h, IntPtr p) { known.Add(h); return true; }, IntPtr.Zero); }
            catch { }
        }

        private void Poll()
        {
            try
            {
                List<KeyValuePair<IntPtr, string>> fresh = new List<KeyValuePair<IntPtr, string>>();
                IntPtr shell = GetShellWindow();
                IntPtr desktop = GetDesktopWindow();

                EnumWindows(delegate(IntPtr h, IntPtr p)
                {
                    try
                    {
                        if (h == shell || h == desktop) return true;
                        if (known.Contains(h)) return true;
                        known.Add(h);
                        if (!IsWindowVisible(h)) return true;

                        int len = GetWindowTextLengthW(h);
                        if (len < 2) return true;
                        StringBuilder sb = new StringBuilder(len + 2);
                        GetWindowTextW(h, sb, sb.Capacity);
                        string title = sb.ToString().Trim();
                        if (title.Length < 2) return true;

                        // skip tool windows: they are mostly popups and tray helpers
                        int ex = GetWindowLong(h, -20);
                        bool tool = (ex & 0x00000080) != 0;
                        if (tool) return true;

                        fresh.Add(new KeyValuePair<IntPtr, string>(h, title));
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);

                foreach (KeyValuePair<IntPtr, string> kv in fresh)
                {
                    uint pid;
                    GetWindowThreadProcessId(kv.Key, out pid);
                    string exe = ExePath(pid);
                    string name = AppName(exe, kv.Value);
                    if (name.Length == 0) continue;

                    lock (announced)
                    {
                        DateTime last;
                        if (announced.TryGetValue(name, out last) && (DateTime.Now - last).TotalSeconds < 5) continue;
                        announced[name] = DateTime.Now;
                    }

                    Bitmap icon = IconFor(exe);
                    Action<string, Bitmap> c = Launched;
                    if (c != null) { try { c(name, icon); } catch { } }
                }
            }
            catch { }
        }

        private static string ExePath(uint pid)
        {
            try
            {
                IntPtr h = OpenProcess(0x1000, false, pid);
                if (h == IntPtr.Zero) return "";
                try
                {
                    StringBuilder sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
                    return sb.ToString();
                }
                finally { CloseHandle(h); }
            }
            catch { return ""; }
        }

        private static string AppName(string exe, string title)
        {
            if (exe.Length > 0)
            {
                string n = Path.GetFileNameWithoutExtension(exe);
                if (n.Length > 0) return n;
            }
            // fall back to the first words of the title
            int cut = title.IndexOf(" - ", StringComparison.Ordinal);
            return cut > 0 ? title.Substring(0, cut) : title;
        }

        /// <summary>Icon of the application executable, or null when unavailable.</summary>
        private static Bitmap IconFor(string exe)
        {
            if (exe.Length == 0) return null;
            IntPtr[] large = new IntPtr[1];
            IntPtr[] small = new IntPtr[1];
            try
            {
                uint n = ExtractIconEx(exe, 0, large, small, 1);
                IntPtr use = large[0] != IntPtr.Zero ? large[0] : small[0];
                if (n == 0 || use == IntPtr.Zero) return null;
                using (Icon ic = Icon.FromHandle(use))
                    return ic.ToBitmap();
            }
            catch { return null; }
            finally
            {
                try { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); } catch { }
                try { if (small[0] != IntPtr.Zero) DestroyIcon(small[0]); } catch { }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        public void Dispose()
        {
            try { if (timer != null) { timer.Stop(); timer.Dispose(); } } catch { }
        }
    }

    // ------------------------------------------------------------ radio state
    /// <summary>Reports Bluetooth and Wi-Fi being switched on or off.</summary>
    internal sealed class RadioWatcher : IDisposable
    {
        public event Action<string, bool> Changed;      // ("bluetooth"|"wi-fi", on)

        private bool? btOn;
        private bool? wifiOn;
        private System.Windows.Forms.Timer timer;

        public void Start()
        {
            // seed with the real state so only changes are announced
            RadioState s = Read();
            btOn = s.Bluetooth;
            wifiOn = s.Wifi;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1200;
            timer.Tick += delegate { Poll(); };
            timer.Start();
        }

        private void Poll()
        {
            try
            {
                RadioState s = Read();
                if (btOn.HasValue && s.Bluetooth.HasValue && s.Bluetooth.Value != btOn.Value)
                    Fire("bluetooth", s.Bluetooth.Value);
                if (s.Bluetooth.HasValue) btOn = s.Bluetooth;

                if (wifiOn.HasValue && s.Wifi.HasValue && s.Wifi.Value != wifiOn.Value)
                    Fire("wi-fi", s.Wifi.Value);
                if (s.Wifi.HasValue) wifiOn = s.Wifi;
            }
            catch { }
        }

        private void Fire(string what, bool on)
        {
            Action<string, bool> c = Changed;
            if (c != null) { try { c(what, on); } catch { } }
        }

        private struct RadioState
        {
            public bool? Bluetooth;
            public bool? Wifi;
        }

        /// <summary>
        /// Live radio state via WinRT Windows.Devices.Radios - the same source the
        /// Settings app uses. Registry keys only describe whether a service exists, so
        /// they never change when the user flips the switch.
        /// </summary>
        private static RadioState Read()
        {
            RadioState st = new RadioState();
            try
            {
                var ops = Windows.Devices.Radios.Radio.GetRadiosAsync().AsTask();
                if (!ops.Wait(1500)) return st;
                foreach (Windows.Devices.Radios.Radio r in ops.Result)
                {
                    bool on = r.State == Windows.Devices.Radios.RadioState.On;
                    switch (r.Kind)
                    {
                        case Windows.Devices.Radios.RadioKind.Bluetooth: st.Bluetooth = on; break;
                        case Windows.Devices.Radios.RadioKind.WiFi: st.Wifi = on; break;
                    }
                }
            }
            catch { }
            return st;
        }

        /// <summary>Fallback used by the diagnostic when WinRT is unavailable.</summary>
        internal static bool BluetoothOn()
        {
            RadioState s = Read();
            return s.Bluetooth.HasValue ? s.Bluetooth.Value : LegacyBluetooth();
        }

        internal static bool WifiOn()
        {
            RadioState s = Read();
            return s.Wifi.HasValue ? s.Wifi.Value : LegacyWifi();
        }

        private static bool LegacyBluetooth()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"System\CurrentControlSet\Services\BTHPORT", false))
                    return k != null;
            }
            catch { return false; }
        }

        private static bool LegacyWifi()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"System\CurrentControlSet\Services\WlanSvc", false))
                {
                    if (k == null) return false;
                    object start = k.GetValue("Start");
                    return start == null || Convert.ToInt32(start) != 4;
                }
            }
            catch { return false; }
        }

        public void Dispose()
        {
            try { if (timer != null) { timer.Stop(); timer.Dispose(); } } catch { }
        }
    }
    // ------------------------------------------------------------ downloads
    /// <summary>
    /// Best-effort download tracking.
    ///
    /// Windows exposes no public API for another application's download progress:
    /// Chrome/Edge keep transfer state in their own profile databases, and Firefox
    /// uses internal temp names. What CAN be observed is the partial file the browser
    /// leaves on disk (.crdownload / .part / .partial). Progress is therefore inferred
    /// from how the partial file grows, and completion from the partial vanishing
    /// while a finished file appears.
    ///
    /// Because the browser knows the total size and we do not, the percentage is an
    /// estimate and is reported as such. When the total is unknown the island shows an
    /// indeterminate bar instead of a made-up number.
    /// </summary>
    internal sealed class DownloadWatcher : IDisposable
    {
        public event Action<string, double, bool, long, long> Progress;  // name, 0..1, done, got, total

        private static readonly string[] PartialExt = {
            ".crdownload", ".part", ".partial", ".download", ".opdownload", ".!ut", ".tmp"
        };

        private sealed class Tracked
        {
            public string Path;
            public long Size;
            public long LastSize;
            public DateTime Started = DateTime.Now;
            public DateTime LastChange = DateTime.Now;
        }

        private readonly Dictionary<string, Tracked> growing =
            new Dictionary<string, Tracked>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> knownComplete =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Forms.Timer timer;

        private string DownloadsDir
        {
            get
            {
                try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); }
                catch { return ""; }
            }
        }

        public void Start()
        {
            // Files already present are history, not news.
            try
            {
                string dir = DownloadsDir;
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir))
                        knownComplete.Add(Path.GetFileName(f));
            }
            catch { }

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 700;
            timer.Tick += delegate { Scan(); };
            timer.Start();
        }

        private void Scan()
        {
            string dir = DownloadsDir;
            if (dir.Length == 0 || !Directory.Exists(dir)) return;

            try
            {
                // ---- 1. completed files: a file that is complete and not yet announced
                foreach (string f in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(f);
                    if (knownComplete.Contains(name)) continue;

                    long len = 0;
                    DateTime wrote = DateTime.Now;
                    try { FileInfo fi = new FileInfo(f); len = fi.Length; wrote = fi.LastWriteTime; } catch { }

                    // ignore tiny leftovers and files that have not been written yet
                    if (len < 32 * 1024) { knownComplete.Add(name); continue; }
                    if ((DateTime.Now - wrote).TotalSeconds > 90) { knownComplete.Add(name); continue; }

                    knownComplete.Add(name);
                    Action<string, double, bool, long, long> c = Progress;
                    if (c != null) { try { c(name, 1.0, true, len, len); } catch { } }
                }

                // ---- 2. partial files still being written: report progress
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string pat in PartialExt)
                {
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*" + pat); }
                    catch { continue; }

                    foreach (string path in files)
                    {
                        string key = path;
                        seen.Add(key);
                        long size = 0;
                        try { size = new FileInfo(path).Length; } catch { }

                        Tracked tr;
                        if (!growing.TryGetValue(key, out tr))
                        {
                            tr = new Tracked();
                            tr.Path = path;
                            tr.Size = size;
                            tr.LastSize = size;
                            growing[key] = tr;
                            continue;                       // wait one tick before reporting
                        }
                        if (size != tr.LastSize)
                        {
                            tr.LastSize = size;
                            tr.Size = size;
                            tr.LastChange = DateTime.Now;
                            Action<string, double, bool, long, long> c = Progress;
                            if (c != null) { try { c(DisplayName(path), -1.0, false, size, -1); } catch { } }
                        }
                    }
                }

                List<string> gone = new List<string>();
                foreach (KeyValuePair<string, Tracked> kv in growing)
                    if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                foreach (string key in gone) growing.Remove(key);
            }
            catch { }
        }

        private static string DisplayName(string path)
        {
            string n = Path.GetFileName(path);
            foreach (string e in PartialExt)
                if (n.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - e.Length);
            return n.Length > 0 ? n : "download";
        }

        public void Dispose()
        {
            try { if (timer != null) { timer.Stop(); timer.Dispose(); } } catch { }
        }
    }
}