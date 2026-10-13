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
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern IntPtr GetWindowLongPtr32(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
        private delegate bool EnumProc(IntPtr h, IntPtr p);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        private const int GWL_EXSTYLE = -20;
        private const int GWL_STYLE = -16;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_CHILD = 0x40000000;
        private const int GCLP_HICON = -14;
        private const int GCLP_HICONSM = -34;
        private const uint WM_GETICON = 0x007F;
        private const uint GW_OWNER = 4;
        private static readonly IntPtr ICON_SMALL = IntPtr.Zero;
        private static readonly IntPtr ICON_BIG = new IntPtr(1);
        private static readonly IntPtr ICON_SMALL2 = new IntPtr(2);
        private const uint SHGFI_ICON = 0x00000100;
        private const uint SHGFI_LARGEICON = 0x00000000;

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

        private static IntPtr StyleOf(IntPtr h, int which)
        {
            try
            {
                // A 32-bit process must use the 32-bit entry point; picking by the running
                // bitness keeps this correct either way.
                return IntPtr.Size == 8 ? GetWindowLongPtr64(h, which) : GetWindowLongPtr32(h, which);
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>Window classes that are never an application the user "opened".</summary>
        private static bool IsNoiseClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            string c = cls.ToLowerInvariant();
            if (c == "shell_traywnd" || c == "shell_secondarytraywnd" || c == "progman") return true;
            if (c == "workerw" || c == "tooltips_class32" || c == "sysshadow") return true;
            if (c == "foregroundstaging" || c == "multitaskingviewframe") return true;
            if (c == "xamlexplorerhost" || c == "internet explorer_server" || c == "ieframe") return true;
            if (c.StartsWith("splwow") || c.StartsWith("nvidia") || c.StartsWith("amd")) return true;
            if (c.StartsWith("chrome_widgetwin_0")) return true;
            return false;
        }

        /// <summary>
        /// Would the user call this "an application I just opened"? These filters matter:
        /// without them the watcher fires for tool windows, dialogs, shell helpers and
        /// splash screens, which is exactly what produced a random letter or the Explorer
        /// icon for an unrelated program.
        /// </summary>
        private static bool IsRealAppWindow(IntPtr h, IntPtr shell, IntPtr desktop, uint self)
        {
            if (h == shell || h == desktop) return false;
            if (!IsWindowVisible(h)) return false;
            if (IsIconic(h)) return false;                                  // minimised, not opened

            int style = (int)StyleOf(h, GWL_STYLE).ToInt64();
            int ex = (int)StyleOf(h, GWL_EXSTYLE).ToInt64();
            if ((style & WS_CHILD) != 0) return false;                      // a control
            if ((ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;        // a dialog or popup

            RECT r;
            if (GetWindowRect(h, out r))
            {
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (w < 80 || ht < 60) return false;                        // splash or toast
                if (w >= 16000 || ht >= 16000) return false;
            }

            if (GetWindowTextLengthW(h) < 2) return false;

            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == 0 || pid == self) return false;                      // never announce ourselves

            // The input-experience window lives under a host process, so the name alone
            // cannot rule it out - the title can.
            try
            {
                int tl = GetWindowTextLengthW(h);
                StringBuilder tb = new StringBuilder(tl + 2);
                GetWindowTextW(h, tb, tb.Capacity);
                string t = tb.ToString().Trim().ToLowerInvariant();
                if (t.StartsWith("windows input experience") || t.StartsWith("интерфейс ввода windows"))
                    return false;
            }
            catch { }

            StringBuilder cls = new StringBuilder(256);
            GetClassNameW(h, cls, cls.Capacity);
            if (IsNoiseClass(cls.ToString())) return false;

            return true;
        }

        /// <summary>
        /// Diagnostic: what the watcher resolves for every candidate window right now.
        /// Tells "no icon was found" apart from "the wrong window was reported".
        /// </summary>
        public static void DescribeAll()
        {
            Console.WriteLine("  {0,-30} {1,-32} {2}", "window class", "resolved name", "icon");
            IntPtr shell = GetShellWindow();
            IntPtr desktop = GetDesktopWindow();
            uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                try
                {
                    if (!IsRealAppWindow(h, shell, desktop, self)) return true;

                    int len = GetWindowTextLengthW(h);
                    StringBuilder sb = new StringBuilder(len + 2);
                    GetWindowTextW(h, sb, sb.Capacity);
                    string title = sb.ToString().Trim();

                    StringBuilder cls = new StringBuilder(256);
                    GetClassNameW(h, cls, cls.Capacity);

                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    string exe = ExePath(pid);
                    string name = AppName(exe, title);
                    Bitmap bmp = IconFor(h, exe);
                    string icon = bmp == null ? "NONE (monogram)" : (bmp.Width + "x" + bmp.Height);
                    if (bmp != null) bmp.Dispose();

                    Console.WriteLine("  {0,-30} {1,-32} {2}", cls.ToString(), "[" + name + "]", icon);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }

        private void Poll()
        {
            try
            {
                List<KeyValuePair<IntPtr, string>> fresh = new List<KeyValuePair<IntPtr, string>>();
                IntPtr shell = GetShellWindow();
                IntPtr desktop = GetDesktopWindow();
                uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

                EnumWindows(delegate(IntPtr h, IntPtr p)
                {
                    try
                    {
                        // only a window seen for the first time can be "just opened"
                        if (known.Contains(h)) return true;
                        known.Add(h);

                        if (!IsRealAppWindow(h, shell, desktop, self)) return true;

                        int len = GetWindowTextLengthW(h);
                        StringBuilder sb = new StringBuilder(len + 2);
                        GetWindowTextW(h, sb, sb.Capacity);
                        string title = sb.ToString().Trim();
                        if (title.Length < 2) return true;

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

                    Bitmap icon = IconFor(kv.Key, exe);
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

        /// <summary>
        /// Processes that only HOST other windows. They are not applications the user
        /// opened, and naming them after the host is what produced nonsense such as
        /// "Application Frame Host" and a monogram letter "A" for a UWP app.
        /// </summary>
        private static readonly HashSet<string> HostProcesses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ApplicationFrameHost", "TextInputHost", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "SearchApp", "RuntimeBroker",
            "sihost", "ctfmon", "dwm", "LockApp", "WWAHost", "WindowsInternal.ComposableShell.Experiences.TextInput.InputApp"
        };

        /// <summary>Exe names that look nothing like the product they belong to.</summary>
        private static readonly Dictionary<string, string> Friendly =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome", "Chrome" }, { "msedge", "Edge" }, { "firefox", "Firefox" },
            { "opera", "Opera" }, { "brave", "Brave" }, { "vivaldi", "Vivaldi" },
            { "explorer", "Explorer" }, { "Code", "VS Code" }, { "devenv", "Visual Studio" },
            { "WINWORD", "Word" }, { "EXCEL", "Excel" }, { "POWERPNT", "PowerPoint" },
            { "OUTLOOK", "Outlook" }, { "olk", "Outlook" }, { "Teams", "Teams" },
            { "ms-teams", "Teams" }, { "Discord", "Discord" }, { "Spotify", "Spotify" },
            { "Telegram", "Telegram" }, { "WhatsApp", "WhatsApp" }, { "notepad", "Notepad" },
            { "mspaint", "Paint" }, { "calc", "Calculator" },
            { "ApplicationFrameHost", "Windows app" }, { "SystemSettings", "Settings" },
            { "steam", "Steam" }, { "steamwebhelper", "Steam" },
            { "EpicGamesLauncher", "Epic Games" }, { "blender", "Blender" },
            { "obs64", "OBS Studio" }, { "vlc", "VLC" },
            { "WindowsTerminal", "Terminal" }, { "wt", "Terminal" },
            { "powershell", "PowerShell" }, { "pwsh", "PowerShell" },
            { "cmd", "Command Prompt" }, { "mstsc", "Remote Desktop" },
            { "Everything", "Everything" }, { "7zFM", "7-Zip" }, { "WinRAR", "WinRAR" }
        };

        /// <summary>
        /// Display name. Prefers the executable's own description ("Google Chrome" rather
        /// than "chrome"), then a friendly name, then the file name, and only as a last
        /// resort makes something up from the window title.
        /// </summary>
        private static string AppName(string exe, string title)
        {
            if (exe.Length > 0)
            {
                string bare = Path.GetFileNameWithoutExtension(exe);

                // a host process tells us nothing; the title is the better source
                if (HostProcesses.Contains(bare))
                {
                    string fromTitleOnly = FromTitle(title);
                    if (fromTitleOnly.Length > 0) return fromTitleOnly;
                    return "";                      // nothing meaningful: do not announce
                }

                string desc = "";
                try
                {
                    System.Diagnostics.FileVersionInfo vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                    if (vi != null && !string.IsNullOrEmpty(vi.FileDescription))
                        desc = vi.FileDescription.Trim();
                }
                catch { }

                // "Google Chrome" is a better label than "chrome"; descriptions such as
                // "Microsoft Windows Operating System" are worse than the file name.
                if (desc.Length >= 3 && desc.Length <= 40 && !IsGenericDescription(desc))
                    return desc;

                // Settings and similar: the window title is localised and clearer than
                // the internal executable name ("SystemSettings").
                if (bare.StartsWith("SystemSettings", StringComparison.OrdinalIgnoreCase))
                {
                    string t2 = FromTitle(title);
                    if (t2.Length > 0) return t2;
                }

                string friendly;
                if (bare.Length > 0 && Friendly.TryGetValue(bare, out friendly)) return friendly;
                if (bare.Length > 0) return bare;
            }
            return FromTitle(title);
        }

        private static bool IsGenericDescription(string d)
        {
            string s = d.ToLowerInvariant();
            return s.StartsWith("microsoft windows")
                || s.StartsWith("windows ")
                || s.Contains("operating system")
                || s == "application" || s == "setup" || s == "installer"
                || s.EndsWith(".exe")
                || s.StartsWith("http");
        }

        /// <summary>
        /// Best effort name from a window title. Generic words are skipped so the monogram
        /// letter is the first letter of something meaningful rather than of "Untitled".
        /// </summary>
        private static string FromTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            string t = title.Trim();

            string[] generic = { "untitled", "new tab", "new document", "без имени",
                                 "новая вкладка", "loading", "please wait",
                                 "settings", "настройки", "program manager",
                                 "default ime", "start" };
            foreach (string g in generic)
                if (t.Equals(g, StringComparison.OrdinalIgnoreCase)) return "";

            // "Document1 - Word" -> Word ; "page - Chrome" -> Chrome
            string[] seps = { " - ", " – ", " — ", " | " };
            foreach (string sep in seps)
            {
                int idx = t.LastIndexOf(sep, StringComparison.Ordinal);
                if (idx > 0 && idx + sep.Length < t.Length)
                {
                    string tail = t.Substring(idx + sep.Length).Trim();
                    if (tail.Length >= 2 && tail.Length <= 40) return tail;
                }
            }
            return t.Length <= 40 ? t : "";
        }

        /// <summary>
        /// Icon for a window, trying every source Windows offers. `WM_GETICON` is the
        /// window's own icon and works for packaged apps; the window-class icon and the
        /// executable cover the rest. Returns null only when nothing is available, so the
        /// caller draws a monogram from the first letter of the name.
        /// </summary>
        private static Bitmap IconFor(IntPtr hwnd, string exe)
        {
            // 1. the window's own icon
            IntPtr src = IntPtr.Zero;
            try { src = SendMessageW(hwnd, WM_GETICON, ICON_BIG, IntPtr.Zero); } catch { }
            if (src == IntPtr.Zero) { try { src = SendMessageW(hwnd, WM_GETICON, ICON_SMALL2, IntPtr.Zero); } catch { } }
            if (src == IntPtr.Zero) { try { src = SendMessageW(hwnd, WM_GETICON, ICON_SMALL, IntPtr.Zero); } catch { } }
            Bitmap bmp = src != IntPtr.Zero ? FromHandle(src) : null;

            // 2. the window class icon
            if (bmp == null)
            {
                src = StyleOf(hwnd, GCLP_HICON);
                if (src == IntPtr.Zero) src = StyleOf(hwnd, GCLP_HICONSM);
                if (src != IntPtr.Zero) bmp = FromHandle(src);
            }

            // 3. the executable's icon
            if (bmp == null && exe.Length > 0)
            {
                IntPtr[] large = new IntPtr[1];
                IntPtr[] small = new IntPtr[1];
                try
                {
                    uint n = ExtractIconEx(exe, 0, large, small, 1);
                    IntPtr use = large[0] != IntPtr.Zero ? large[0] : small[0];
                    if (n > 0 && use != IntPtr.Zero) bmp = FromHandle(use);
                }
                catch { }
                finally
                {
                    if (large[0] != IntPtr.Zero) { try { DestroyIcon(large[0]); } catch { } }
                    if (small[0] != IntPtr.Zero) { try { DestroyIcon(small[0]); } catch { } }
                }
            }

            // 4. the shell's idea of the file's icon
            if (bmp == null && exe.Length > 0)
            {
                try
                {
                    SHFILEINFO fi = new SHFILEINFO();
                    IntPtr r = SHGetFileInfoW(exe, 0, ref fi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                        SHGFI_ICON | SHGFI_LARGEICON);
                    if (r != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
                    {
                        bmp = FromHandle(fi.hIcon);
                        DestroyIcon(fi.hIcon);          // SHGetFileInfo hands over ownership
                    }
                }
                catch { }
            }

            return bmp;
        }

        /// <summary>
        /// Copies an HICON into a Bitmap. Drawn through GDI+ rather than Icon.ToBitmap(),
        /// which turns 32-bit alpha icons into black boxes.
        /// </summary>
        private static Bitmap FromHandle(IntPtr hicon)
        {
            if (hicon == IntPtr.Zero) return null;
            try
            {
                using (Icon ic = Icon.FromHandle(hicon))
                {
                    Size sz = ic.Size;
                    if (sz.Width < 8 || sz.Height < 8) sz = new Size(32, 32);
                    Bitmap bmp = new Bitmap(sz.Width, sz.Height,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawIcon(ic, new Rectangle(0, 0, sz.Width, sz.Height));
                    }
                    return bmp;
                }
            }
            catch { return null; }
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

    // ------------------------------------------------------------ charging
    /// <summary>
    /// Reports the battery being charged. Polled through GetSystemPowerStatus, which is
    /// what the shell itself uses, so no battery driver and no admin rights are involved.
    /// </summary>
    internal sealed class ChargeWatcher : IDisposable
    {
        public event Action<int> Charging;          // percent, -1 when unknown

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        private const byte BATTERY_FLAG_NO_BATTERY = 128;
        private const byte BATTERY_PERCENT_UNKNOWN = 255;

        private bool wasCharging;
        private int lastPercent = -2;
        private System.Windows.Forms.Timer timer;

        public void Start()
        {
            int p;
            wasCharging = Read(out p);              // seed: only a CHANGE is announced
            lastPercent = p;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 4000;
            timer.Tick += delegate { Poll(); };
            timer.Start();
        }

        private void Poll()
        {
            try
            {
                int p;
                bool charging = Read(out p);

                if (charging != wasCharging)
                {
                    wasCharging = charging;
                    lastPercent = p;
                    Fire(charging ? p : -1);       // -1 means "stopped charging"
                    return;
                }
                // while charging, keep the percentage fresh
                if (charging && p != lastPercent)
                {
                    lastPercent = p;
                    Fire(p);
                }
            }
            catch { }
        }

        private void Fire(int percent)
        {
            Action<int> c = Charging;
            if (c != null) { try { c(percent); } catch { } }
        }

        /// <summary>
        /// True when the battery is actually taking charge: on AC power, a battery is
        /// present, and the flag either says "charging" or "high" (full but plugged in).
        /// </summary>
        private static bool Read(out int percent)
        {
            percent = -1;
            SYSTEM_POWER_STATUS s;
            if (!GetSystemPowerStatus(out s)) return false;
            if (s.BatteryFlag == BATTERY_FLAG_NO_BATTERY || s.BatteryFlag == 255) return false;

            if (s.BatteryLifePercent != BATTERY_PERCENT_UNKNOWN) percent = s.BatteryLifePercent;

            bool onAc = s.ACLineStatus == 1;
            bool chargingFlag = (s.BatteryFlag & 0x08) != 0;      // BATTERY_FLAG_CHARGING
            bool highFlag = (s.BatteryFlag & 0x04) != 0;          // BATTERY_FLAG_HIGH
            return onAc && (chargingFlag || highFlag);
        }

        /// <summary>Diagnostic: print the raw power state.</summary>
        public static string Describe()
        {
            SYSTEM_POWER_STATUS s;
            if (!GetSystemPowerStatus(out s)) return "GetSystemPowerStatus failed";
            int p;
            bool charging = Read(out p);
            return "ac=" + s.ACLineStatus + " flag=" + s.BatteryFlag
                + " percent=" + s.BatteryLifePercent + " charging=" + charging
                + " reported=" + (p < 0 ? "unknown" : p + "%");
        }

        public void Dispose()
        {
            try { if (timer != null) { timer.Stop(); timer.Dispose(); } } catch { }
        }
    }
}