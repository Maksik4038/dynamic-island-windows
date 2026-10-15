// ============================================================================
//  Dynamic Island for Windows 11
//  ---------------------------------------------------------------------------
//  A per-pixel-alpha layered window (WS_EX_LAYERED + UpdateLayeredWindow) that
//  imitates the iPhone Dynamic Island: a seamless black pill fused to the top
//  edge of the screen that springs open when the pointer approaches.
//
//  Collapsed : time + battery, 127 x 37 dp
//  Hover     : springs to 371 x 196 dp, bottom edge unfurls downward
//  Content   : decorative waveform tied to the real volume level, date/time,
//              battery, live volume row and screen-brightness row
//  Sliders   : drag to set. Volume via CoreAudio (IAudioEndpointVolume),
//              brightness via Dxva2 Get/SetMonitorBrightness.
//
//  Why not WinRT: this machine has no System.Runtime.WindowsRuntime facade and
//  no Windows SDK, so the SMTC now-playing projection cannot be compiled here.
//  Everything below is plain Win32/GDI+/COM, which is why it builds and runs.
//
//  Build: csc.exe (in-box .NET Framework compiler, C# 5 language level)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DynamicIsland
{
    // ================================================================= interop
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int CX, CY; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }
        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONDATA
        {
            public int cbSize; public IntPtr hWnd; public int uID; public int uFlags;
            public int uCallbackMessage; public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
        }
        [StructLayout(LayoutKind.Sequential)] public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags;
        }

        // window styles
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080,
                         WS_EX_TOPMOST = 0x00000008, WS_EX_NOACTIVATE = 0x08000000;
        public const int ULW_ALPHA = 2, AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
        public const int DIB_RGB_COLORS = 0, BI_RGB = 0;

        // tray / menu
        public const int WM_APP = 0x8000, WM_TRAYICON = WM_APP + 1;
        public const int WM_RBUTTONUP = 0x0205, WM_LBUTTONUP = 0x0202;
        public const int NIM_ADD = 0, NIM_DELETE = 2;
        public const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
        public const int MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8, MF_POPUP = 0x10;
        public const int TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;

        // cursors
        public const int IDC_ARROW = 32512, IDC_HAND = 32649, IDC_SIZEWE = 32644;

        // misc
        public const int MDT_EFFECTIVE_DPI = 0, MONITOR_DEFAULTTONEAREST = 2;
        public const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
        public const int MB_ICONINFORMATION = 0x40;

        [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string win);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenInputDesktop(int flags, bool inherit, int access);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseDesktop(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SwitchDesktop(IntPtr h);
        public const int DESKTOP_SWITCHDESKTOP = 0x0100;
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetCursor();
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr h, int name);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst,
            ref SIZE size, IntPtr srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr m);
        [DllImport("user32.dll")] public static extern bool AppendMenu(IntPtr m, int flags, int id, string text);
        [DllImport("user32.dll")] public static extern int TrackPopupMenu(IntPtr m, int flags, int x, int y, int res, IntPtr h, IntPtr rect);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern int MessageBox(IntPtr h, string text, string caption, int type);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, int usage,
            out IntPtr bits, IntPtr section, int offset);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

        public static void EnablePerMonitorDpi()
        {
            // -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
            try { SetProcessDpiAwareness(2); } catch { }
        }
    }

    // =========================================================== CoreAudio volume
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr props,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IntPtr store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    /// <summary>Vtable order matters: it must match IAudioEndpointVolume exactly.</summary>
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr p);
        int UnregisterControlChangeNotify(IntPtr p);
        int GetChannelCount(out int count);
        int SetMasterVolumeLevel(float level, ref Guid ctx);
        int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(int ch, float level, ref Guid ctx);
        int SetChannelVolumeLevelScalar(int ch, float level, ref Guid ctx);
        int GetChannelVolumeLevel(int ch, out float level);
        int GetChannelVolumeLevelScalar(int ch, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        int GetVolumeStepInfo(out int step, out int count);
        int VolumeStepUp(ref Guid ctx);
        int VolumeStepDown(ref Guid ctx);
        int QueryHardwareSupport(out int mask);
        int GetVolumeRange(out float min, out float max, out float inc);
    }

    internal static class Audio
    {
        private static IAudioEndpointVolume endpoint;

        /// <summary>EDataFlow.eCapture - recording endpoints.</summary>
        internal const int Capture = 1;
        /// <summary>ERole.eMultimedia.</summary>
        internal const int Multimedia = 1;
        /// <summary>CLSCTX_INPROC_SERVER.</summary>
        internal const int InProcServer = 1;

        /// <summary>
        /// Opens the default recording device. Used by PrivacyMonitor to enumerate
        /// capture sessions (who is holding the microphone).
        /// </summary>
        internal static object OpenCaptureEndpoint()
        {
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
            IMMDevice dev;
            if (en.GetDefaultAudioEndpoint(Capture, Multimedia, out dev) != 0 || dev == null) return null;
            return dev;
        }

        /// <summary>Activates a CoreAudio interface on the default recording device.</summary>
        internal static object ActivateCapture(Type iface)
        {
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
            IMMDevice dev;
            if (en.GetDefaultAudioEndpoint(Capture, Multimedia, out dev) != 0 || dev == null) return null;
            Guid iid = iface.GUID;
            object o;
            if (dev.Activate(ref iid, InProcServer, IntPtr.Zero, out o) != 0) return null;
            return o;
        }

        private static IAudioEndpointVolume Endpoint()
        {
            if (endpoint != null) return endpoint;
            try
            {
                IMMDeviceEnumerator en = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out dev) != 0 || dev == null)
                    return null;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                if (dev.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out o) != 0 || o == null)
                    return null;
                endpoint = (IAudioEndpointVolume)o;
            }
            catch { endpoint = null; }
            return endpoint;
        }

        public static float GetVolume()
        {
            try
            {
                IAudioEndpointVolume v = Endpoint();
                if (v == null) return -1f;
                float f;
                if (v.GetMasterVolumeLevelScalar(out f) == 0) return f;
            }
            catch { endpoint = null; }
            return -1f;
        }

        public static bool GetMute()
        {
            try
            {
                IAudioEndpointVolume v = Endpoint();
                if (v == null) return false;
                bool m;
                if (v.GetMute(out m) == 0) return m;
            }
            catch { endpoint = null; }
            return false;
        }

        public static bool SetVolume(float level)
        {
            try
            {
                IAudioEndpointVolume v = Endpoint();
                if (v == null) return false;
                Guid g = Guid.Empty;
                if (level > 0.001f) { bool m; if (v.GetMute(out m) == 0 && m) v.SetMute(false, ref g); }
                return v.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, level)), ref g) == 0;
            }
            catch { endpoint = null; return false; }
        }

        public static bool SetMute(bool mute)
        {
            try
            {
                IAudioEndpointVolume v = Endpoint();
                if (v == null) return false;
                Guid g = Guid.Empty;
                return v.SetMute(mute, ref g) == 0;
            }
            catch { endpoint = null; return false; }
        }
    }

    // ============================================================== brightness
    [StructLayout(LayoutKind.Sequential)]
    internal struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    internal static class Brightness
    {
        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint count, [Out] PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr h, uint value);
        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] arr);

        private static IntPtr handle = IntPtr.Zero;
        private static uint bMin, bMax = 100;
        private static bool available, probed;
        private static DateTime lastProbe = DateTime.MinValue;

        public static int Min { get { return (int)bMin; } }
        public static int Max { get { return (int)bMax; } }

        public static bool Available
        {
            get
            {
                if (!probed || (DateTime.Now - lastProbe).TotalSeconds > 20) { Probe(); }
                return available;
            }
        }

        public static void Probe()
        {
            probed = true;
            lastProbe = DateTime.Now;
            Release();
            available = false;
            try
            {
                IntPtr mon = Native.MonitorFromWindow(IntPtr.Zero, Native.MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return;
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(mon, out count) || count == 0) return;
                if (count > 16) count = 16;
                PHYSICAL_MONITOR[] all = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(mon, count, all)) return;

                // keep the first monitor, release the rest immediately
                for (uint i = 1; i < count; i++)
                {
                    PHYSICAL_MONITOR[] one = new PHYSICAL_MONITOR[1];
                    one[0] = all[i];
                    DestroyPhysicalMonitors(1, one);
                }
                handle = all[0].hPhysicalMonitor;

                uint cur;
                if (!GetMonitorBrightness(handle, out bMin, out cur, out bMax) || bMax <= bMin)
                {
                    Release();
                    return;
                }
                available = true;
            }
            catch { available = false; }
        }

        public static void Release()
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    PHYSICAL_MONITOR[] one = new PHYSICAL_MONITOR[1];
                    one[0].hPhysicalMonitor = handle;
                    DestroyPhysicalMonitors(1, one);
                }
                catch { }
                handle = IntPtr.Zero;
            }
        }

        public static int Get()
        {
            if (!available) return -1;
            try
            {
                uint min, cur, max;
                if (GetMonitorBrightness(handle, out min, out cur, out max)) return (int)cur;
            }
            catch { available = false; }
            return -1;
        }

        public static bool Set(int value)
        {
            if (!available) return false;
            try
            {
                uint v = (uint)Math.Max((int)bMin, Math.Min((int)bMax, value));
                if (SetMonitorBrightness(handle, v)) return true;
                available = false;
                return false;
            }
            catch { available = false; return false; }
        }
    }

    // ============================================================== animation
    internal static class Anim
    {
        /// <summary>Critically damped spring: frame-rate independent, no overshoot.</summary>
        public static void Spring(ref double value, ref double velocity, double target, double dt, double freq)
        {
            double w = 2 * Math.PI * freq;
            double steps = Math.Ceiling(dt / 0.002);
            if (steps < 1) steps = 1;
            if (steps > 64) steps = 64;
            double h = dt / steps;
            for (int i = 0; i < (int)steps; i++)
            {
                double a = -w * w * (value - target) - 2 * w * velocity;
                velocity += a * h;
                value += velocity * h;
            }
            if (Math.Abs(value - target) < 0.0005 && Math.Abs(velocity) < 0.005) { value = target; velocity = 0; }
        }

        public static double Clamp01(double t) { return t < 0 ? 0 : (t > 1 ? 1 : t); }
        public static double Smooth(double t) { t = Clamp01(t); return t * t * (3 - 2 * t); }
        public static double Lerp(double a, double b, double t) { return a + (b - a) * Clamp01(t); }

        /// <summary>Cubic ease-out for short timed pops, given elapsed seconds.</summary>
        public static double EaseOut(double elapsed, double duration)
        {
            double t = Clamp01(elapsed / Math.Max(0.001, duration));
            double u = 1 - t;
            return 1 - u * u * u;
        }
    }

    internal static class Fonts
    {
        private static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();
        private static readonly Dictionary<string, string> resolved = new Dictionary<string, string>();

        public static Font Get(string family, float pixelSize, FontStyle style)
        {
            string key = family + "|" + pixelSize.ToString("0.##") + "|" + (int)style;
            Font f;
            if (cache.TryGetValue(key, out f)) return f;

            string use;
            if (!resolved.TryGetValue(family, out use))
            {
                use = family;
                try { using (FontFamily probe = new FontFamily(family)) { } }
                catch { use = "Segoe UI"; }
                resolved[family] = use;
            }
            try { f = new Font(use, pixelSize, style, GraphicsUnit.Pixel); }
            catch { f = new Font("Segoe UI", pixelSize, style, GraphicsUnit.Pixel); }
            cache[key] = f;
            return f;
        }
    }

    // ================================================================== island
    internal sealed class Island
    {
        // ---- geometry in design pixels (96 dpi) ----
        public const int CanvasW = 470, CanvasH = 268;
        public const int TopMarginPx = 7;
        private const int IdleH = 37;                 // collapsed height; width comes from settings
        private const int OpenW = 371, OpenH = 202;
        private const int HudW = 276;                 // compact volume capsule
        private const int GapW = 7;                   // gap between the little mic island and the pill
        private const int MediaW = 300;               // mini-player with artwork
        private const int MicIslandW = 30;            // the little mic island, same height as the pill
        private const int RunningW = 62;              // capsule showing just a glyph while a timer runs
        private const int FinishedW = 250;            // "time is up" card
        private const int CardBigH = 104;             // height of the unfurled card
        private const int NotifyW = 250;              // device / notification card width
        private const int CardSmallH = 92;            // its height
        private const int DownloadW = 230;            // download bar with the file name
        private const int AppLaunchW = 120;           // app launch: just a big icon
        private const int CardAppH = 120;
        private const int ChargePillW = 92;           // bolt + percentage
        private const int ChargeBadgeW = 30;          // the detached badge
        private const int FlatH = 15;                 // flat capsule while an app is fullscreen
        private const int BandH = 4;                  // coloured strip along the top edge

        // ---- status colours: the island tints itself instead of drawing an icon ----
        private static readonly Color BandCharge = Color.FromArgb(255, 48, 209, 88);    // green
        private static readonly Color BandMic = Color.FromArgb(255, 255, 159, 10);      // orange
        private static readonly Color BandMusic = Color.FromArgb(255, 10, 132, 255);    // blue
        private static readonly Color BandLanguage = Color.FromArgb(255, 245, 245, 247); // white
        private static readonly Color BandRadio = Color.FromArgb(255, 255, 69, 58);     // red
        private static readonly Color BandDownload = Color.FromArgb(255, 90, 200, 250); // cyan
        private static readonly Color BandEvent = Color.FromArgb(255, 191, 90, 242);    // purple

        // ---- the microphone accent, matching the reference look ----
        private static readonly Color MicInk = Color.FromArgb(255, 242, 158, 24);       // icon glyph
        private static readonly Color MicRim = Color.FromArgb(190, 150, 78, 10);       // border
        private static readonly Color MicBody = Color.FromArgb(255, 8, 6, 4);          // fill

        private const double HudDuration = 0.52;      // seconds for the capsule pop

        // ---- animated state ----
        private double animW = 62, velW;
        private double animH = IdleH, velH;
        private double open = 0, velOpen;          // 0 collapsed -> 1 expanded
        private bool expanded;
        private bool hudMode;                      // volume HUD instead of full panel
        private double hud = 0, velHud;
        private DateTime hudShownAt = DateTime.MinValue;

        // ---- cached system state ----
        private float volume = -1f;
        private bool muted;
        private int brightness = -1;
        private int battery = -1;
        private bool charging;
        private string chargeText = "";      // "85%" while charging, empty when unknown
        private double chgX, chgW;           // detached charging badge, on the right
        private string dateText = "";
        private DateTime lastSlowPoll = DateTime.MinValue;
        private DateTime lastVolumePush = DateTime.MinValue;
        private DateTime lastPrivacyPoll = DateTime.MinValue;
        private volatile bool privacyPolling;
        private readonly object privacyLock = new object();
        private PrivacyState privacyPending;
        private bool privacyDirty;
        private DateTime lastMediaPoll = DateTime.MinValue;
        private double pulseT;
        private Settings settings = new Settings();
        private int idleW = 62;                       // collapsed width, from settings
        private double dotX, dotW;                    // detached microphone dot geometry
        private bool dotShown;
        private long seekPreviewMs = -1;              // while dragging the progress bar
        private DateTime lastSeekPush = DateTime.MinValue;
        private int drag;                             // 0 none, 1 volume, 2 brightness, 3 seek

        // ---- live activities (stopwatch / timer / alarm / notices) ----
        private ActivityManager activities;
        private Activity shown;                       // what Current() returned last tick
        private double digitsT;                       // drives the ringing blink
        private bool locked;                          // workstation locked
        private DateTime lastLockPoll = DateTime.MinValue;
        private bool fullscreen;                       // an app covers the whole screen
        private DateTime lastFullscreenPoll = DateTime.MinValue;

        // ---- new: privacy + media ----
        private PrivacyState privacy = new PrivacyState();
        private MediaState media = new MediaState();
        private MediaMonitor monitor;
        private Bitmap artwork;                       // null unless SMTC supplied one
        private long lastPrivacyStart;                // keeps the notification up briefly
        private DateTime privacySeenAt = DateTime.MinValue;
        private DateTime mediaSeenAt = DateTime.MinValue;
        private int pulse;                            // 0..1 breathing indicator

        // ---- interaction ----
        private readonly IslandWindow host;
        private readonly Stopwatch since = Stopwatch.StartNew();
        private IntPtr curArrow, curSizeWe;

        public Island(IslandWindow window)
        {
            host = window;
            curArrow = Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW);
            curSizeWe = Native.LoadCursor(IntPtr.Zero, Native.IDC_SIZEWE);
            PollSlow();
            PollVolume(true);
        }

        public bool Expanded { get { return expanded; } }
        public bool Dragging { get { return drag != 0; } }
        public double Width { get { return animW; } }
        public double Height { get { return animH; } }

        /// <summary>Used by the snapshot renderer to pose the island for an image.</summary>
        public void Pose(bool open2, bool hud2, double seconds)
        {            expanded = open2;
            hudMode = hud2;
            if (hud2) hudShownAt = DateTime.Now.AddSeconds(-(seconds > 0 ? seconds : 1));
            if (open2 && !hud2) { animW = OpenW; animH = OpenH; }
            else if (hud2) { animW = HudW; animH = IdleH; }
            else if (fullscreen) { animW = idleW; animH = FlatH; }
            else { animW = idleW; animH = IdleH; }
            // a posed island must not be re-animated by the first Tick it receives
            velW = velH = velOpen = velHud = 0;
            open = (open2 && !hud2) ? 1 : 0;
            hud = hud2 ? 1 : 0;
            if (seconds > 0) since.Restart();
        }

        public void SetSystemState(float vol, bool isMuted, int bright, int battPct, bool isCharging)
        {
            volume = vol; muted = isMuted; brightness = bright;
            battery = battPct; charging = isCharging;
            dateText = DateTime.Now.ToString("dddd d MMMM");
        }

        /// <summary>Snapshot helper: stage a camera/microphone notification.</summary>
        public void PosePrivacy(bool camera, bool mic, string app)
        {
            privacy = new PrivacyState();
            if (camera)
            {
                CaptureUser u = new CaptureUser();
                u.Name = app; u.Kind = CaptureKind.Camera; u.StartedAt = DateTime.Now.Ticks;
                privacy.Camera.Add(u);
            }
            if (mic)
            {
                CaptureUser u = new CaptureUser();
                u.Name = app; u.Kind = CaptureKind.Microphone; u.StartedAt = DateTime.Now.Ticks;
                privacy.Mic.Add(u);
            }
            pulse = 205;
            dotShown = privacy.Any;      // Tick normally sets this; a posed island has no Tick
        }

        /// <summary>Snapshot helper: stage a now-playing track, optionally with artwork.</summary>
        public void PoseMedia(string title, string artist)
        {
            PoseMedia(title, artist, null);
        }

        /// <summary>
        /// Snapshot helper. artworkPath, when given, is decoded exactly the way the
        /// real thumbnail bytes are (bytes -> MemoryStream -> Bitmap), so the
        /// rendering path under test matches production.
        /// </summary>
        public void PoseMedia(string title, string artist, string artworkPath)
        {
            media = new MediaState();
            media.Available = true;
            media.Playing = true;
            media.Title = title;
            media.Artist = artist;
            media.App = "Spotify";
            media.Source = "smtc";
            if (artworkPath != null)
            {
                try
                {
                    byte[] bytes = System.IO.File.ReadAllBytes(artworkPath);
                    using (System.IO.MemoryStream ms = new System.IO.MemoryStream(bytes))
                    using (Bitmap decoded = new Bitmap(ms))
                        AttachArtwork(new Bitmap(decoded));
                }
                catch { }
            }
        }

        /// <summary>Snapshot helper: forces which activity the island renders.</summary>
        public void PoseActivityOnly(Activity a)
        {
            shown = a;
            animW = TargetWidth(a);
            animH = TargetHeight(a);
            velW = velH = 0;
            expanded = false; open = 0; hud = 0; hudMode = false;
        }

        /// <summary>Snapshot helper: pretend an application is fullscreen.</summary>
        public void PoseFullscreen(bool on) { fullscreen = on; }

        /// <summary>Snapshot helper: pretend the battery is charging.</summary>
        public void PoseChargeOnly(bool on, string text)
        {
            charging = on;
            chargeText = text;
        }

        /// <summary>Snapshot helper: pretend the workstation is locked.</summary>
        public void PoseLocked(bool value) { locked = value; }

        /// <summary>Snapshot helper: force the flat fullscreen capsule regardless of content.</summary>
        public void PoseFlat()
        {
            fullscreen = true;
            animW = idleW;          // same length as always, only flatter
            animH = FlatH;
            velW = velH = 0;
            expanded = false; open = 0; hud = 0; hudMode = false;
        }

        public void PoseGeometry(double width, double height)
        {
            animW = width;
            // microphone-only keeps the normal height, so a chosen narrow width still
            // becomes a round island; otherwise shrink to a circle for tiny widths
            if (privacy.Any && !media.HasTrack) animH = IdleH;
            else animH = width < IdleH ? Math.Max(10.0, width) : height;
            velW = velH = 0;
            expanded = false; open = 0; hud = 0; hudMode = false;
        }

        // ------------------------------------------------------------- polling
        public void PollVolume(bool force)
        {
            float v = Audio.GetVolume();
            if (v < 0) return;
            bool m = Audio.GetMute();
            if (force) { volume = v; muted = m; return; }
            if (drag == 0 && (Math.Abs(v - volume) > 0.005f || m != muted))
            {
                // the keyboard or another app changed it: mirror silently
                volume = v;
                muted = m;
                if (host != null) host.NeedFrame();
            }
        }

        public void PollSlow()
        {
            try
            {
                Native.SYSTEM_POWER_STATUS ps;
                if (Native.GetSystemPowerStatus(out ps))
                {
                    battery = (ps.BatteryLifePercent >= 0 && ps.BatteryLifePercent <= 100) ? ps.BatteryLifePercent : -1;
                    charging = ps.ACLineStatus == 1;
                }
            }
            catch { }
            brightness = Brightness.Available ? Brightness.Get() : -1;
            dateText = DateTime.Now.ToString("dddd d MMMM");
        }

        /// <summary>
        /// Applies a freshly read privacy state. Runs on the UI thread; the read
        /// itself happens on a pool thread (see Tick).
        /// </summary>
        private void ApplyPrivacy(PrivacyState next)
        {
            try
            {
                if (next == null) return;
                if (next.Any)
                {
                    // remember the newest start so the pill can linger a moment
                    CaptureUser p = next.Primary;
                    if (p != null && p.StartedAt > lastPrivacyStart) lastPrivacyStart = p.StartedAt;
                    privacySeenAt = DateTime.Now;
                    privacy = next;
                }
                else if (privacy.Any)
                {
                    // a released capture session can linger in CoreAudio's list, so give
                    // it a moment before clearing the notice
                    if (privacySeenAt != DateTime.MinValue &&
                        (DateTime.Now - privacySeenAt).TotalSeconds > 6)
                        privacy = next;
                }
                else privacy = next;
            }
            catch { }
        }

        /// <summary>Now-playing state, only meaningful while a track is actually playing.</summary>
        public void PollMedia()
        {
            if (monitor == null) return;
            try
            {
                monitor.Poll();
                media = monitor.State;
                if (media.HasTrack) mediaSeenAt = DateTime.Now;

                // hand artwork to the renderer; it owns disposal from here
                if (media.Artwork != null && !ReferenceEquals(media.Artwork, artwork))
                    AttachArtwork(media.Artwork);
                else if (media.Artwork == null && !media.HasTrack && artwork != null)
                    AttachArtwork(null);
            }
            catch { }
        }

        public void AttachMedia(MediaMonitor m) { monitor = m; }

        public void ApplySettings(Settings s)
        {
            settings = s;
            idleW = Settings.Clamp(s.IdleWidth);
        }

        public int IdleWidth { get { return idleW; } }

        public void AttachActivities(ActivityManager m) { activities = m; }

        /// <summary>Width a live activity needs, growing with the number of digits.</summary>
        private static int ActivityWidth(Activity a)
        {
            if (a == null) return 190;
            switch (a.Kind)
            {
                case ActivityKind.Stopwatch: return 196;   // 0:00:00 + two buttons
                case ActivityKind.Timer: return 208;       // 0:00:00 + label + two buttons
                case ActivityKind.Alarm: return 232;       // bell + time + snooze/dismiss
                case ActivityKind.Device: return 250;      // icon + name + battery
                default: return 250;                       // notification: icon + title/body
            }
        }

        /// <summary>
        /// Collapsed height for a given width. Narrow settings shrink the height too,
        /// so the smallest size is a real circle rather than a thin oval.
        /// </summary>
        private static double IdleHeightFor(double width)
        {
            return width < IdleH ? Math.Max(10.0, width) : IdleH;
        }
        public void AttachArtwork(Bitmap b)
        {
            if (artwork != null && !ReferenceEquals(artwork, b)) { try { artwork.Dispose(); } catch { } }
            artwork = b;
        }

        private void PushVolume()
        {
            if ((DateTime.Now - lastVolumePush).TotalMilliseconds < 45) return;
            lastVolumePush = DateTime.Now;
            Audio.SetVolume(volume);
        }

        private void PushBrightness()
        {
            if ((DateTime.Now - lastVolumePush).TotalMilliseconds < 45) return;
            lastVolumePush = DateTime.Now;
            Brightness.Set(brightness);
        }

        /// <summary>
        /// True when the lock screen owns the desktop. OpenInputDesktop fails while the
        /// secure desktop is up, which is the cheapest reliable signal.
        /// </summary>
        private static bool IsWorkstationLocked()
        {
            IntPtr h = Native.OpenInputDesktop(0, false, Native.DESKTOP_SWITCHDESKTOP);
            if (h == IntPtr.Zero) return true;
            bool ok = Native.SwitchDesktop(h);
            Native.CloseDesktop(h);
            return !ok;
        }

        /// <summary>
        /// Collapsed height. Cards for a finished/ringing event unfurl downward; a
        /// smaller notice card is used for device arrivals and notifications.
        /// </summary>
        /// <summary>
        /// True when the foreground window really fills the screen - a fullscreen video,
        /// game or a browser in F11 - as opposed to a merely maximised window.
        ///
        /// The two are told apart by what Windows does to the frame. A maximised window
        /// keeps a resizable frame, and Windows then places it a few pixels OUTSIDE the
        /// monitor (measured here: -8..1688 on a monitor of 0..1680). A real fullscreen
        /// window has no such frame and matches the monitor exactly. So a window that
        /// spills past the screen edges is maximised, not fullscreen.
        /// </summary>
        private static bool IsFullscreenApp()
        {
            try
            {
                IntPtr h = Native.GetAncestor(Native.GetForegroundWindow(), 2 /* GA_ROOT */);
                if (h == IntPtr.Zero) return false;
                if (h == Native.GetShellWindow() || h == Native.GetDesktopWindow()) return false;
                if (Native.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return false;

                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return false;

                IntPtr mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return false;
                Native.MONITORINFO mi = new Native.MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
                if (!Native.GetMonitorInfo(mon, ref mi)) return false;

                // Filling the work area means the window is using the whole screen: a
                // browser in F11, a game, a video player, or a maximised window. A window
                // that merely happens to be large does not reach the far edges.
                //
                // Checked against the work area rather than the monitor on purpose: a real
                // fullscreen window and a maximised one both cover the work area, and both
                // are the cases where the island gets in the way. The taskbar staying
                // visible does not change that.
                int tol = 2;
                return r.Left <= mi.rcWork.Left + tol
                    && r.Top <= mi.rcWork.Top + tol
                    && r.Right >= mi.rcWork.Right - tol
                    && r.Bottom >= mi.rcWork.Bottom - tol;
            }
            catch { return false; }
        }
        /// <summary>
        /// Diagnostic text describing what the fullscreen test sees right now. Used by
        /// --fullscreen so the decision can be checked against a real window.
        /// </summary>
        public static string DescribeFullscreen()
        {
            try
            {
                IntPtr h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero) return "no foreground window";
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return "GetWindowRect failed";
                IntPtr mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
                Native.MONITORINFO mi = new Native.MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
                if (!Native.GetMonitorInfo(mon, ref mi)) return "GetMonitorInfo failed";

                StringBuilder title = new StringBuilder(300);
                Native.GetWindowTextW(h, title, title.Capacity);

                bool owner = Native.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero;
                bool spills = r.Left < mi.rcMonitor.Left - 1 || r.Top < mi.rcMonitor.Top - 1
                           || r.Right > mi.rcMonitor.Right + 1 || r.Bottom > mi.rcMonitor.Bottom + 1;

                return "window   : " + title + "\r\n"
                     + "rect     : L" + r.Left + " T" + r.Top + " R" + r.Right + " B" + r.Bottom + "\r\n"
                     + "monitor  : L" + mi.rcMonitor.Left + " T" + mi.rcMonitor.Top
                     + " R" + mi.rcMonitor.Right + " B" + mi.rcMonitor.Bottom + "\r\n"
                     + "work area: L" + mi.rcWork.Left + " T" + mi.rcWork.Top
                     + " R" + mi.rcWork.Right + " B" + mi.rcWork.Bottom + "\r\n"
                     + "has owner: " + owner + "\r\n"
                     + "spills past the screen edges (means maximised): " + spills + "\r\n"
                     + "=> island goes flat: " + IsFullscreenApp();
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        /// <summary>
        /// Corner radius. Capsules are fully round; once the island grows past capsule
        /// height the radius eases toward a rounded rectangle instead of snapping.
        /// </summary>
        private static double CardRadius(double ph, double scale)
        {
            double capsule = ph / 2.0;
            double card = 26 * scale;
            if (capsule <= card) return capsule;
            // blend as the height grows beyond a capsule
            double grow = Anim.Clamp01((ph - IdleH * scale) / (34 * scale));
            return capsule + (card - capsule) * grow;
        }
        private double TargetHeight(Activity a)
        {
            // fullscreen: a thin flat bar, whatever else wants to show
            if (fullscreen) return FlatH;
            if (a == null) return IdleH;
            switch (a.Kind)
            {
                // these are always one-liners, regardless of any flag
                case ActivityKind.Language:
                case ActivityKind.Radio:
                    return IdleH;
                case ActivityKind.Device:
                case ActivityKind.Notification:
                    return CardSmallH;
                case ActivityKind.Download:
                    return a.Finished ? CardBigH : IdleH;      // bar while downloading
                case ActivityKind.AppLaunch:
                    return CardAppH;                            // a big icon needs room
                default:
                    return a.Finished ? CardBigH : IdleH;
            }
        }

        /// <summary>Width for a card: a touch wider than the compact glyph capsule.</summary>
        private double TargetWidth(Activity a)
        {
            if (fullscreen) return idleW;      // same length as always, just flat
            if (a == null) return idleW;
            switch (a.Kind)
            {
                case ActivityKind.Device:
                case ActivityKind.Notification:
                    return NotifyW;
                case ActivityKind.Language:
                    return 74;                                  // just the layout code
                case ActivityKind.Radio:
                    return a.Label != null && a.Label.Length > 5 ? 118 : 96;
                case ActivityKind.Download:
                    return a.Finished ? FinishedW : DownloadW;  // the bar needs room for the name
                case ActivityKind.AppLaunch:
                    return AppLaunchW;
                default:
                    return a.Finished ? FinishedW : RunningW;
            }
        }
        /// <summary>
        /// Small padlock that appears beside the island while the workstation is locked.
        /// Drawn on the RIGHT so it never collides with the detached microphone island.
        /// </summary>
        private void DrawLockGlyph(Graphics g, RectangleF pill, double s)
        {
            double d = 17 * s;
            RectangleF box = new RectangleF((float)(pill.Right + 8 * s), (float)(pill.Y + (pill.Height - d) / 2),
                (float)d, (float)d);

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 8, 8, 10)))
                g.FillEllipse(bg, box);
            using (Pen rim = new Pen(Color.FromArgb(70, 255, 255, 255), (float)(1 * s)))
                g.DrawEllipse(rim, box);

            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            Color c = Color.FromArgb(235, 240, 240, 245);
            using (Pen p = new Pen(c, Math.Max(1.1f, (float)(1.5 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, cx - 2.6f * u, cy - 5.4f * u, 5.2f * u, 5.2f * u, 180, 180);
            }
            using (SolidBrush br = new SolidBrush(c))
                FillRound(g, br, new RectangleF(cx - 3.6f * u, cy - 0.4f * u, 7.2f * u, 5.6f * u), 1.0f * u);
            using (SolidBrush dark = new SolidBrush(Color.FromArgb(220, 12, 12, 16)))
                g.FillEllipse(dark, cx - 0.9f * u, cy + 1.4f * u, 1.8f * u, 1.8f * u);
        }
        // --------------------------------------------------------------- input

        /// <summary>Screen rects of the two activity control glyphs (left, right).</summary>
        private bool ActivityButtonRects(out Rectangle left, out Rectangle right)
        {
            left = Rectangle.Empty; right = Rectangle.Empty;
            if (host == null || shown == null) return false;
            Rectangle b = host.Bounds;
            double s = host.ScaleForIsland;
            double pw = animW * s, ph = animH * s;
            double pillX = b.Left + (b.Width - pw) / 2 + (dotShown && media.HasTrack ? (GapW + MicIslandW) * s : 0);
            double pillY = b.Top + TopMarginPx * s;
            double pad = 10 * s, d = 22 * s;
            double cy = pillY + ph / 2;
            right = new Rectangle((int)(pillX + pw - pad - d), (int)(cy - d / 2), (int)d, (int)d);
            left = new Rectangle((int)(right.Left - d - 6 * s), (int)(cy - d / 2), (int)d, (int)d);
            return true;
        }

        /// <summary>Rects for the mini-player controls, in absolute screen pixels.</summary>
        public Rectangle SeekBarScreenRect()
        {
            if (host == null || !media.HasTrack || media.DurationMs <= 1000) return Rectangle.Empty;
            Rectangle b = host.Bounds;
            double s = host.ScaleForIsland;
            double pw = animW * s, ph = animH * s;
            double pillX = b.Left + (b.Width - pw) / 2 + (dotShown && media.HasTrack ? (GapW + 16) * s : 0);
            double pad = 10 * s;
            float barH = (float)(2.6 * s);
            float barY = (float)(b.Top + TopMarginPx * s + ph) - (float)pad * 0.75f;
            // a generous vertical grab band around a thin bar
            return new Rectangle((int)(pillX + pad), (int)(barY - 7 * s),
                (int)(pw - pad * 2), (int)(barH + 14 * s));
        }

        /// <summary>Where tapping skips to the next track (the right side of the pill).</summary>
        public Rectangle NextZoneScreenRect()
        {
            if (host == null || !media.HasTrack) return Rectangle.Empty;
            Rectangle b = host.Bounds;
            double s = host.ScaleForIsland;
            double pw = animW * s, ph = animH * s;
            double pillX = b.Left + (b.Width - pw) / 2 + (dotShown && media.HasTrack ? (GapW + 16) * s : 0);
            return new Rectangle((int)(pillX + pw * 0.6), (int)(b.Top + TopMarginPx * s),
                (int)(pw * 0.4), (int)ph);
        }

        public void OnMouseMove(Point screen)
        {
            if (drag == 3 && monitor != null)
            {
                Rectangle bar = SeekBarScreenRect();
                if (bar.Width > 4 && media.DurationMs > 1000)
                {
                    double f = Anim.Clamp01((screen.X - bar.Left) / (double)bar.Width);
                    seekPreviewMs = (long)(f * media.DurationMs);
                    if ((DateTime.Now - lastSeekPush).TotalMilliseconds > 180)
                    {
                        lastSeekPush = DateTime.Now;
                        monitor.Seek(seekPreviewMs);
                    }
                    host.NeedFrame();
                }
                return;
            }
            if (drag != 0)
            {
                Rectangle box = drag == 1 ? host.BoxVolume() : host.BoxBrightness();
                if (box.Width > 4)
                {
                    double f = Anim.Clamp01((screen.X - box.Left) / (double)box.Width);
                    if (drag == 1) { volume = (float)f; muted = false; PushVolume(); }
                    else
                    {
                        int span = Math.Max(1, Brightness.Max - Brightness.Min);
                        brightness = Brightness.Min + (int)Math.Round(f * span);
                        PushBrightness();
                    }
                    host.NeedFrame();
                }
                return;
            }

            if (host.HitTest(screen) && !fullscreen)
            {
                expanded = true;
                if (volume < 0) PollVolume(true);
                host.ApplyCursor(curArrow);
            }
            else
            {
                expanded = false;
            }
        }

        public void OnMouseLeave()
        {
            if (drag == 0) expanded = false;
        }

        public void OnMouseDown(Point screen)
        {
            if (!host.HitTest(screen)) return;

            // ---- live activity controls ----
            if (shown != null && activities != null)
            {
                Rectangle left, right;
                if (ActivityButtonRects(out left, out right))
                {
                    bool onLeft = left.Width > 4 && left.Contains(screen);
                    bool onRight = right.Width > 4 && right.Contains(screen);
                    if (onLeft || onRight)
                    {
                        HandleActivityButton(shown, onLeft);
                        host.NeedFrame();
                        return;
                    }
                }
            }

            // mini-player controls come first: they live in the same collapsed pill
            if (media.HasTrack)
            {
                Rectangle bar = SeekBarScreenRect();
                if (bar.Width > 4 && bar.Contains(screen))
                {
                    drag = 3;
                    Rectangle full = new Rectangle(bar.Left, bar.Top, bar.Width, bar.Height);
                    double f = Anim.Clamp01((screen.X - full.Left) / (double)full.Width);
                    seekPreviewMs = (long)(f * media.DurationMs);
                    host.NeedFrame();
                    return;
                }
                Rectangle nextZone = NextZoneScreenRect();
                if (nextZone.Width > 4 && nextZone.Contains(screen))
                {
                    if (monitor != null) monitor.SkipNext();
                    host.NeedFrame();
                    return;
                }
            }

            Rectangle vb = host.BoxVolume(), bb = host.BoxBrightness();
            if (vb.Width > 4 && vb.Contains(screen)) { drag = 1; return; }
            if (bb.Width > 4 && bb.Contains(screen)) { drag = 2; return; }
        }

        /// <summary>
        /// Left / right control taps for the visible activity.
        ///   stopwatch : left = pause-resume, right = reset
        ///   timer     : left = pause-resume (or reset when ringing), right = reset / stop
        ///   alarm     : left = snooze 10 minutes, right = dismiss
        ///   notice    : either = dismiss
        /// </summary>
        private void HandleActivityButton(Activity a, bool isLeft)
        {
            if (a == null || activities == null) return;

            switch (a.Kind)
            {
                case ActivityKind.Stopwatch:
                    if (isLeft) activities.ToggleStopwatch(); else activities.ResetStopwatch();
                    break;

                case ActivityKind.Timer:
                    if (isLeft) activities.ToggleTimer(); else activities.ResetTimer();
                    break;

                case ActivityKind.Alarm:
                    if (isLeft) activities.SnoozeAlarm(TimeSpan.FromMinutes(10));
                    else activities.DismissAlarm();
                    break;

                case ActivityKind.Device:
                    activities.Dismiss(ActivityKind.Device);
                    break;

                case ActivityKind.Download:
                    activities.Dismiss(ActivityKind.Download);
                    break;

                case ActivityKind.Language:
                    activities.Dismiss(ActivityKind.Language);
                    break;

                case ActivityKind.Radio:
                    activities.Dismiss(ActivityKind.Radio);
                    break;

                case ActivityKind.AppLaunch:
                    activities.Dismiss(ActivityKind.AppLaunch);
                    break;

                default:
                    activities.Dismiss(ActivityKind.Notification);
                    break;
            }
        }

        public void OnMouseUp(Point screen)
        {
            if (drag == 3)
            {
                if (monitor != null && seekPreviewMs >= 0) monitor.Seek(seekPreviewMs);
                seekPreviewMs = -1;
                drag = 0;
                host.NeedFrame();
                return;
            }
            if (drag == 1) Audio.SetVolume(volume);
            else if (drag == 2) Brightness.Set(brightness);
            else if (host.HitTest(screen))
            {
                if (host.BoxGlyph().Contains(screen))
                {
                    muted = !muted;
                    Audio.SetMute(muted);
                    hudShownAt = DateTime.Now;
                    hudMode = true;
                }
                else if (host.BoxToggle().Contains(screen))
                {
                    hudMode = !hudMode;
                    if (hudMode) hudShownAt = DateTime.Now;
                }
            }
            drag = 0;
            host.NeedFrame();
        }

        public void OnWheel(bool up)
        {
            if (volume < 0) return;
            volume = (float)Anim.Clamp01(volume + (up ? 0.02 : -0.02));
            muted = false;
            Audio.SetVolume(volume);
            hudShownAt = DateTime.Now;
            hudMode = true;
            host.NeedFrame();
        }

        // ---------------------------------------------------------------- tick
        public void Tick(double dt)
        {
            if (hudMode && drag == 0 && (DateTime.Now - hudShownAt).TotalSeconds > 2.6) hudMode = false;

            // live activities advance here, on the UI thread
            digitsT += dt;
            if (activities != null)
            {
                activities.Update(dt);
                shown = activities.Current();
            }
            else shown = null;

            // Privacy polling runs on a pool thread: it walks CoreAudio sessions and
            // the registry, which must never stall the animation.
            if ((DateTime.Now - lastPrivacyPoll).TotalMilliseconds > 600 && !privacyPolling)
            {
                lastPrivacyPoll = DateTime.Now;
                privacyPolling = true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        PrivacyState next = PrivacyMonitor.Read();
                        lock (privacyLock) { privacyPending = next; privacyDirty = true; }
                    }
                    catch { }
                    finally { privacyPolling = false; }
                });
            }
            lock (privacyLock)
            {
                if (privacyDirty) { privacyDirty = false; ApplyPrivacy(privacyPending); }
            }
            // lock screen: a small padlock appears beside the island
            // fullscreen: collapse to a flat bar so no tab strip is covered
            if ((DateTime.Now - lastFullscreenPoll).TotalMilliseconds > 400)
            {
                lastFullscreenPoll = DateTime.Now;
                bool wasFull = fullscreen;
                try { fullscreen = IsFullscreenApp(); } catch { }
                if (fullscreen != wasFull)
                {
                    if (fullscreen) expanded = false;      // never cover a fullscreen app
                    if (host != null) host.NeedFrame();
                }
            }

            if ((DateTime.Now - lastLockPoll).TotalMilliseconds > 900)
            {
                lastLockPoll = DateTime.Now;
                bool was = locked;
                try { locked = IsWorkstationLocked(); } catch { }
                if (locked != was && host != null) host.NeedFrame();
            }

            // charging badge: the text feeds the pill, the flag decides whether the badge
            // rides inside the island or gets its own small island on the right
            charging = false;
            chargeText = "";
            Activity ch = activities == null ? null : activities.Charging;
            if (ch != null)
            {
                charging = true;
                chargeText = ch.GotBytes < 0 ? "" : ch.GotBytes + "%";
            }

            double mediaInterval = media.Playing ? 1200 : 3000;
            if ((DateTime.Now - lastMediaPoll).TotalMilliseconds > mediaInterval)
            { lastMediaPoll = DateTime.Now; PollMedia(); }

            // breathing indicator for the privacy pill
            pulseT += dt;
            pulse = (int)Math.Round((Math.Sin(pulseT * 2.4) * 0.5 + 0.5) * 255);

            // A microphone in use shows as a small orange dot, NOT text. When a track is
            // also playing the dot detaches and floats to the left of the pill.
            // A live activity (stopwatch / timer / alarm / notice) outranks both.
            bool activityMode = !expanded && shown != null;
            bool mediaMode = !expanded && !activityMode && media.HasTrack;
            bool micOnly = !expanded && !activityMode && !mediaMode && privacy.Any;
            dotShown = privacy.Any;

            if (expanded)
            {
                if (hudMode && drag == 0)
                {
                    // explicit ease-out: more predictable than a spring for this small pop
                    open = 0; velOpen = 0;
                    double hudT = Anim.EaseOut((DateTime.Now - hudShownAt).TotalSeconds, HudDuration);
                    animW = Anim.Lerp(idleW, HudW, hudT);
                    animH = IdleH;
                }
                else
                {
                    Anim.Spring(ref animW, ref velW, OpenW, dt, 4.4);
                    Anim.Spring(ref animH, ref velH, OpenH, dt, 4.4);
                }
            }
            else if (fullscreen)
            {
                // Fullscreen overrides every other size rule: the capsule must stay flat.
                // These branches each used to hardcode IdleH, and they were quietly winning
                // over the flat target - the island ended up pinned to the top but tall.
                Anim.Spring(ref animW, ref velW, idleW, dt, 4.4);
                Anim.Spring(ref animH, ref velH, FlatH, dt, 4.4);
            }
            else if (activityMode)
            {
                // a running timer is just a glyph; a finished one unfurls downward
                Anim.Spring(ref animW, ref velW, TargetWidth(shown), dt, 4.7);
                Anim.Spring(ref animH, ref velH, TargetHeight(shown), dt, 4.7);
            }
            else if (micOnly)
            {
                // the island KEEPS its resting width - only the icon and rim change
                Anim.Spring(ref animW, ref velW, idleW, dt, 4.6);
                Anim.Spring(ref animH, ref velH, IdleH, dt, 4.6);
            }
            else if (mediaMode)
            {
                Anim.Spring(ref animW, ref velW, MediaW, dt, 4.6);
                Anim.Spring(ref animH, ref velH, IdleH, dt, 4.6);
            }
            else
            {
                Anim.Spring(ref animW, ref velW, idleW, dt, 4.4);
                Anim.Spring(ref animH, ref velH, IdleHeightFor(idleW), dt, 4.4);
            }
            Anim.Spring(ref open, ref velOpen, (expanded && !(hudMode && drag == 0)) ? 1 : 0, dt, 5.0);
            Anim.Spring(ref hud, ref velHud, (hudMode && !expanded) || (hudMode && drag != 0) ? 1 : 0, dt, 5.2);

            if ((DateTime.Now - lastSlowPoll).TotalSeconds > 4) { lastSlowPoll = DateTime.Now; PollSlow(); }
            PollVolume(false);
        }

        // -------------------------------------------------------------- drawing
        public void Draw(Graphics g, double scale)
        {
            int cw = (int)Math.Round(CanvasW * scale);
            int ch = (int)Math.Round(CanvasH * scale);
            g.Clear(Color.Transparent);

            // In fullscreen the island hugs the very top edge, so it covers as little of
            // the application as possible; otherwise it keeps its normal inset.
            double top = (fullscreen ? 0 : TopMarginPx) * scale;
            double pw = animW * scale;
            double ph = animH * scale;
            // when the microphone dot is detached it sits to the LEFT of the pill, so the
            // pill shifts right and the whole pair stays centred on the screen
            // room on the left for the detached microphone island, whatever is showing
            // room on the left for the detached microphone and on the right for the
            // charging badge, so neither overlaps whatever is showing
            double groupPad = 0;
            if (dotShown && (media.HasTrack || shown != null)) groupPad += (GapW + MicIslandW + 4) * scale;
            if (charging && (media.HasTrack || shown != null)) groupPad += (GapW + ChargeBadgeW + 4) * scale;
            double px = (cw - pw - groupPad) / 2.0 + groupPad;
            // cards keep a rounded-rect corner; capsules stay fully round
            double rad = CardRadius(ph, scale);

            RectangleF pill = new RectangleF((float)px, (float)top, (float)pw, (float)ph);

            Guard("Shadow", delegate
            {
                // --- soft contact shadow: a few faint bands, brightest just below the pill
                for (int i = 4; i >= 1; i--)
                {
                    double grow = i * 1.5 * scale;
                    RectangleF sr = new RectangleF((float)(px - grow + 1.5), (float)(top - grow * 0.25 + 1.0),
                        (float)(pw + grow * 2 - 3), (float)(ph + grow * 1.35));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(5, 0, 0, 0)))
                        FillRound(g, b, sr, (float)(rad + grow));
                }
            });

            Guard("Pill", delegate
            {
                // while the microphone is in use the island takes on the orange accent
                // from the reference: orange rim, faintly warm body
                bool micAccent = dotShown && !media.HasTrack;

                using (GraphicsPath path = Round(pill, (float)rad))
                {
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                        new RectangleF(pill.X, pill.Y, pill.Width, Math.Max(2, pill.Height)),
                        micAccent ? Color.FromArgb(255, 26, 15, 5) : Color.FromArgb(255, 16, 16, 18),
                        micAccent ? Color.FromArgb(255, 8, 6, 4) : Color.FromArgb(255, 3, 3, 5), 90f))
                        g.FillPath(lg, path);

                    int rimIn = micAccent ? 70 : 24;
                    using (Pen pen = new Pen(Color.FromArgb(rimIn, 255, 255, 255), (float)(1.0 * scale)))
                        g.DrawPath(pen, Round(new RectangleF(pill.X + 0.6f, pill.Y + 1.1f,
                            pill.Width - 1.2f, pill.Height - 1.6f), (float)Math.Max(1, rad - 1)));

                    using (Pen pen = micAccent
                        ? new Pen(MicRim, (float)(1.5 * scale))
                        : new Pen(Color.FromArgb(46, 255, 255, 255), (float)(1.0 * scale)))
                        g.DrawPath(pen, path);
                }
            });

            // The microphone island lives OUTSIDE the pill on the LEFT, the charging badge
            // on the RIGHT. Both are drawn before the clip is installed, so a tall event
            // card can never cut them off.
            GraphicsState state = g.Save();
            using (GraphicsPath clip = Round(pill, (float)rad)) g.SetClip(clip);

            double op = Anim.Smooth(open);
            double hp = Anim.Smooth(hud);
            bool activityMode = shown != null && op < 0.5;
            bool mediaMode = !activityMode && media.HasTrack && op < 0.5;
            bool detached = mediaMode && dotShown;

            // The charge pill takes the island when nothing louder is happening. The
            // microphone does not count as "louder": it lives beside the island, not in it.
            // When both are idle there is only room for one, and the percentage is the more
            // useful of the two, so the microphone yields for those few seconds.
            bool chargePill = charging && !activityMode && !mediaMode && op < 0.5;
            bool micPill = !charging && !activityMode && !mediaMode && dotShown && op < 0.5;

            // A card or the player takes the island, so charging steps aside to the right.
            bool chargeBadge = charging && (activityMode || mediaMode || op >= 0.5);
            bool micBadge = dotShown && animW > MicIslandW + 8 && !chargePill;

            g.Restore(state);

            if (micBadge) Guard("MicIslandLeft", delegate
            {
                dotW = MicIslandW * scale;
                dotX = pill.X - (GapW * scale) - dotW;
                double mcy = pill.Y + Math.Min(pill.Height, IdleH * scale) / 2.0;
                RectangleF dr = new RectangleF((float)dotX, (float)(mcy - dotW / 2), (float)dotW, (float)dotW);
                DrawMicIslandShape(g, dr, scale);
            });
            else { dotX = 0; dotW = 0; }

            if (chargeBadge) Guard("ChargeIslandRight", delegate
            {
                chgW = ChargeBadgeW * scale;
                chgX = pill.Right + (GapW * scale);
                double ccy = pill.Y + Math.Min(pill.Height, IdleH * scale) / 2.0;
                RectangleF cr = new RectangleF((float)chgX, (float)(ccy - chgW / 2), (float)chgW, (float)chgW);
                DrawChargeIslandShape(g, cr, scale);
            });
            else { chgX = 0; chgW = 0; }

            state = g.Save();
            using (GraphicsPath clip2 = Round(pill, (float)rad)) g.SetClip(clip2);

            // collapsed: a plain black pill. Nothing is drawn unless something happens.
            // In fullscreen the flat capsule carries colours only, never content.
            if (fullscreen)
            {
                /* the bands below are all it shows */
            }
            else if (activityMode) Guard("Activity", delegate { DrawActivity(g, pill, scale, shown); });
            else if (mediaMode) Guard("Media", delegate { DrawMiniPlayer(g, pill, scale, detached); });
            else if (chargePill) Guard("ChargePill", delegate { DrawChargePill(g, pill, scale); });
            else if (micPill) Guard("MicIsland", delegate { DrawMicIsland(g, pill, scale); });

            if (!fullscreen && hp > 0.02 && op < 0.6 && !mediaMode && !micPill && !activityMode && !chargePill)
                Guard("Hud", delegate { DrawHud(g, pill, scale, Anim.Clamp01(hp) * (1 - op)); });
            if (!fullscreen && op > 0.02 && !(hudMode && drag == 0))
                Guard("Panel", delegate { DrawPanel(g, pill, scale, Anim.Clamp01(op)); });

            g.Restore(state);

            // status colours along the top edge, over whatever the pill is showing
            Guard("Bands", delegate { DrawBands(g, pill, scale, BandColors()); });

            // lock screen: a small padlock beside the island
            if (locked && !expanded) Guard("Lock", delegate { DrawLockGlyph(g, pill, scale); });
        }

        /// <summary>
        /// The microphone island: a compact capsule with the orange rim and the
        /// microphone glyph, matching the reference screenshot.
        /// </summary>
        private void DrawMicIsland(Graphics g, RectangleF pill, double s)
        {
            // the island keeps its full width; the icon sits on the left, inset like
            // the music player's disc so the layout stays consistent
            double pad = 10 * s;
            double side = Math.Min(pill.Height * 0.58, pill.Width - pad * 2);   // compact glyph
            if (side < 6 * s) return;
            RectangleF icon = new RectangleF((float)(pill.X + pad), (float)(pill.Y + (pill.Height - side) / 2),
                (float)side, (float)side);
            DrawMicGlyph(g, icon, s, MicInk, 255);
        }

        /// <summary>The separate little microphone island: capsule, orange rim, icon.</summary>
        private void DrawMicIslandShape(Graphics g, RectangleF box, double s)
        {
            float rad = (float)(box.Height / 2.0);
            using (GraphicsPath path = Round(box, rad))
            {
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new RectangleF(box.X, box.Y, box.Width, Math.Max(2, box.Height)),
                    Color.FromArgb(255, 22, 13, 4), MicBody, 90f))
                    g.FillPath(lg, path);
                using (Pen pen = new Pen(MicRim, (float)(1.4 * s)))
                    g.DrawPath(pen, path);
            }

            // glyph, centred inside this small island
            double side = Math.Min(box.Width, box.Height) * 0.72;
            RectangleF icon = new RectangleF(box.X + (float)((box.Width - side) / 2),
                box.Y + (float)((box.Height - side) / 2), (float)side, (float)side);
            DrawMicGlyph(g, icon, s, MicInk, 255);
        }

        /// <summary>
        /// Minimalist microphone: round head, cradle arc, stem and base.
        /// Stroked rather than filled, matching the reference icon.
        /// </summary>
        private void DrawMicGlyph(Graphics g, RectangleF box, double s, Color ink, int alpha)
        {
            float cx = box.X + box.Width / 2f;
            float top = box.Y;
            float h = box.Height;
            float headW = h * 0.36f;
            float headH = h * 0.60f;
            Color c = Color.FromArgb(alpha, ink);

            using (Pen p = new Pen(c, Math.Max(1.15f, (float)(h * 0.085))))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                // head (rounded capsule)
                using (GraphicsPath hp = Round(new RectangleF(cx - headW / 2, top, headW, headH), headW / 2))
                    g.DrawPath(p, hp);
                // cradle
                float cw = h * 0.74f;
                g.DrawArc(p, cx - cw / 2, top + h * 0.24f, cw, h * 0.52f, 0, 180);
                // stem + base
                g.DrawLine(p, cx, top + h * 0.76f, cx, top + h * 0.93f);
                g.DrawLine(p, cx - h * 0.24f, top + h * 0.93f, cx + h * 0.24f, top + h * 0.93f);
            }
        }

        /// <summary>Circle rect used for the microphone inside a pill (unused layouts).</summary>
        private RectangleF MicDotRect(RectangleF pill, double s)
        {
            double side = pill.Width * 0.82;
            return new RectangleF(pill.X + (float)((pill.Width - side) / 2),
                pill.Y + (float)((pill.Height - side) / 2), (float)side, (float)side);
        }

        /// <summary>
        /// Live activity capsule: a progress ring, the digits, and up to two control
        /// glyphs on the right. The ring fills in the activity's accent colour.
        /// </summary>
        private void DrawActivity(Graphics g, RectangleF pill, double s, Activity a)
        {
            if (a == null) return;

            // simple one-line states: a short word, nothing else
            if (a.Kind == ActivityKind.Language || a.Kind == ActivityKind.Radio)
            {
                DrawSimpleWord(g, pill, s, a);
                return;
            }
            // a freshly opened app: one big icon, nothing else
            if (a.Kind == ActivityKind.AppLaunch)
            {
                DrawAppLaunch(g, pill, s, a);
                return;
            }
            // a download in progress: name above, thin bar below
            if (a.Kind == ActivityKind.Download && !a.Finished)
            {
                DrawDownloadBar(g, pill, s, a);
                return;
            }

            bool card = a.Kind == ActivityKind.Device || a.Kind == ActivityKind.Notification || a.Finished;

            // ---- compact: a running stopwatch/timer shows just its glyph
            if (!card)
            {
                DrawCompactGlyph(g, pill, s, a);
                return;
            }

            // ---- card: big icon centred, text under it, transport in the corner
            double pad = 10 * s;
            double w = pill.Width, h = pill.Height;
            Color accent = ActivityAccent(a);

            // icon in the upper middle
            double iconD = Math.Min(w * 0.24, h * 0.34);
            RectangleF icon = new RectangleF(pill.X + (float)((w - iconD) / 2), pill.Y + (float)(h * 0.13),
                (float)iconD, (float)iconD);

            bool blink = a.Finished;
            int alpha = 255;
            if (blink) alpha = 130 + (int)(Math.Abs(Math.Sin(digitsT * 3.2)) * 125);

            switch (a.Kind)
            {
                case ActivityKind.Device: DrawDeviceIcon(g, icon, s, accent, a.Label); break;
                case ActivityKind.Alarm: DrawBellGlyph(g, icon, s, Color.FromArgb(alpha, accent)); break;
                case ActivityKind.Timer: DrawRing(g, icon, 1.0, s, Color.FromArgb(alpha, accent)); break;
                case ActivityKind.Download: DrawGreenTick(g, icon, s); break;
                default: DrawInfoGlyph(g, icon, s, accent); break;
            }

            // headline
            string head = a.Kind == ActivityKind.Timer && a.Finished ? "Time is up"
                        : a.Kind == ActivityKind.Alarm ? "Alarm"
                        : a.Kind == ActivityKind.Download ? "Downloaded"
                        : a.Label;
            string sub = a.Detail;
            if (a.Kind == ActivityKind.Timer && a.Finished)
                sub = a.Label + "  " + Clock.Format(a.Duration);
            else if (a.Kind == ActivityKind.Alarm && a.Finished)
                sub = string.Format("{0}:{1:00}", (int)a.Duration.TotalHours, a.Duration.Minutes);

            Font fHead = Fonts.Get("Segoe UI Variable Display", 16.5f * (float)s, FontStyle.Bold);
            Font fSub = Fonts.Get("Segoe UI Variable Text", 9.8f * (float)s, FontStyle.Regular);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(blink ? alpha : 255, blink ? accent : Color.FromArgb(255, 255, 255))))
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(170, 255, 255, 255)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                float headY = (float)(pill.Y + h * 0.44);
                g.DrawString(head, fHead, ink,
                    new RectangleF(pill.X + (float)pad, headY, (float)(w - pad * 2), (float)(h * 0.26)), sf);
                if (!string.IsNullOrEmpty(sub))
                    g.DrawString(sub, fSub, dim,
                        new RectangleF(pill.X + (float)pad, (float)(headY + h * 0.25), (float)(w - pad * 2), (float)(h * 0.16)), sf);
            }

            // ---- bottom row: transport badge in the corner, controls on the right
            double badgeD = 17 * s;
            RectangleF badge = new RectangleF(pill.X + (float)pad, pill.Bottom - (float)pad - (float)badgeD,
                (float)badgeD, (float)badgeD);
            if (a.Kind == ActivityKind.Device)
            {
                DrawTransportBadge(g, badge, s, CardTransport(a));
                // battery text next to the badge
                if (a.Duration.TotalSeconds >= 0 && a.Duration.TotalSeconds <= 100)
                {
                    Font fB = Fonts.Get("Segoe UI Variable Text", 10.2f * (float)s, FontStyle.Bold);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.LineAlignment = StringAlignment.Center;
                        sf.Alignment = StringAlignment.Near;
                        g.DrawString((int)a.Duration.TotalSeconds + "%" + (a.Running ? " charging" : ""), fB, b,
                            new RectangleF(badge.Right + (float)(6 * s), badge.Y - (float)(4 * s),
                                (float)(w * 0.5), (float)(badgeD + 8 * s)), sf);
                    }
                }
            }

            // controls: one glyph per bottom corner, so they never overlap the headline
            DrawCardButtons(g, pill, s, a, accent);
        }

        /// <summary>Control glyphs for a card: one per bottom corner.</summary>
        private void DrawCardButtons(Graphics g, RectangleF pill, double s, Activity a, Color accent)
        {
            double pad = 12 * s;
            double d = 20 * s;
            double cy = pill.Bottom - pad - d / 2;

            RectangleF right = new RectangleF((float)(pill.Right - pad - d), (float)(cy - d / 2), (float)d, (float)d);
            RectangleF left = new RectangleF((float)(pill.X + pad), (float)(cy - d / 2), (float)d, (float)d);

            if (a.Kind == ActivityKind.Device || a.Kind == ActivityKind.Notification
                || a.Kind == ActivityKind.Download)
            {
                DrawCross(g, right, s, Color.FromArgb(205, 255, 255, 255));
                return;
            }
            if (a.Kind == ActivityKind.Alarm)
            {
                DrawGlyphPlus(g, left, s, Color.FromArgb(240, 255, 255, 255));
                DrawCross(g, right, s, Color.FromArgb(240, 255, 255, 255));
                return;
            }
            // finished timer: reset on the left, stop on the right
            DrawGlyphReset(g, left, s, Color.FromArgb(240, 255, 255, 255));
            DrawCross(g, right, s, Color.FromArgb(240, 255, 255, 255));
        }

        /// <summary>
        /// One-line states: the keyboard layout code or a Bluetooth / Wi-Fi toggle.
        /// Nothing else is drawn.
        /// </summary>
        private void DrawSimpleWord(Graphics g, RectangleF pill, double s, Activity a)
        {
            bool radio = a.Kind == ActivityKind.Radio;
            string word = a.Label ?? "";

            if (!radio)
            {
                // keyboard layout: just the code, e.g. "rus"
                Font f = Fonts.Get("Segoe UI Variable Text", 15f * (float)s, FontStyle.Bold);
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(255, 240, 240, 245)))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(word, f, ink, pill, sf);
                }
                return;
            }

            // radio toggle: the mark on the left, the name after it
            Color accent = a.Running
                ? Color.FromArgb(255, 130, 200, 255)
                : Color.FromArgb(200, 150, 150, 160);

            double d = Math.Min(pill.Height * 0.56, 22 * s);
            RectangleF ico = new RectangleF((float)(pill.X + 9 * s),
                (float)(pill.Y + (pill.Height - d) / 2), (float)d, (float)d);
            DrawRadioGlyph(g, ico, s, accent, word);

            Font fr = Fonts.Get("Segoe UI Variable Text", 12f * (float)s, FontStyle.Bold);
            using (SolidBrush ink = new SolidBrush(accent))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Alignment = StringAlignment.Near;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.None;
                double tx = ico.Right + 7 * s;
                g.DrawString(word, fr, ink,
                    new RectangleF((float)tx, pill.Y, (float)(pill.Right - tx - 6 * s), pill.Height), sf);
            }
            if (!a.Running) DrawStrike(g, ico, s, accent);
        }
        /// <summary>A newly opened application: one big icon, centred, nothing else.</summary>
        private void DrawAppLaunch(Graphics g, RectangleF pill, double s, Activity a)
        {
            double d = Math.Min(pill.Width * 0.52, pill.Height * 0.62);
            RectangleF box = new RectangleF(pill.X + (float)((pill.Width - d) / 2),
                pill.Y + (float)((pill.Height - d) / 2), (float)d, (float)d);

            if (a.Icon != null)
            {
                try
                {
                    GraphicsState st = g.Save();
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(a.Icon, box);
                    g.Restore(st);
                    return;
                }
                catch { }
            }
            // no icon available: a rounded monogram in a tinted square
            int hash = 0;
            string key = a.Label ?? "";
            for (int i = 0; i < key.Length; i++) hash = (hash * 31 + key[i]) & 0x7fffffff;
            Color c1 = ColorFromHash(hash);
            using (GraphicsPath p = Round(box, (float)(d * 0.24)))
            {
                using (LinearGradientBrush lg = new LinearGradientBrush(box, c1, Color.FromArgb(255, 30, 30, 38), 60f))
                    g.FillPath(lg, p);
                using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255), (float)(1 * s)))
                    g.DrawPath(pen, p);
            }
            string letter = key.Length > 0 ? key.Substring(0, 1).ToUpperInvariant() : "?";
            Font f = Fonts.Get("Segoe UI Variable Display", (float)(d * 0.52), FontStyle.Bold);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(letter, f, ink, box, sf);
            }
        }

        /// <summary>Download in progress: the file name above, a thin bar below.</summary>
        private void DrawDownloadBar(Graphics g, RectangleF pill, double s, Activity a)
        {
            double pad = 12 * s;
            double cy = pill.Y + pill.Height / 2.0;
            bool known = a.Progress01 >= 0;

            // name on top
            Font f = Fonts.Get("Segoe UI Variable Text", 10.6f * (float)s, FontStyle.Bold);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                double right = pill.Right - pad - (known ? 44 * s : 0);
                g.DrawString(a.Label, f, ink,
                    new RectangleF((float)(pill.X + pad), (float)(pill.Y - 5 * s),
                        (float)(right - pill.X - pad), pill.Height), sf);
            }

            // percentage on the right when the total is known
            if (known)
            {
                Font fp = Fonts.Get("Segoe UI Variable Text", 10.6f * (float)s, FontStyle.Bold);
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(210, 255, 255, 255)))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Far;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(Math.Round(a.Progress01 * 100) + "%", fp, ink,
                        new RectangleF((float)(pill.Right - pad - 42 * s), (float)(pill.Y - 5 * s),
                            (float)(42 * s), pill.Height), sf);
                }
            }

            // the bar
            float h = (float)(3.4 * s);
            RectangleF track = new RectangleF((float)(pill.X + pad), (float)(cy + 6 * s),
                (float)(pill.Width - pad * 2), h);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
                FillRound(g, b, track, h / 2);

            Color accent = Color.FromArgb(255, 130, 200, 255);
            if (known)
            {
                using (SolidBrush b = new SolidBrush(accent))
                    FillRound(g, b, new RectangleF(track.X, track.Y,
                        (float)Math.Max(h, track.Width * Anim.Clamp01(a.Progress01)), h), h / 2);
            }
            else
            {
                // total unknown: a moving indeterminate segment instead of a fake percentage
                double t = since.Elapsed.TotalSeconds;
                double seg = track.Width * 0.28;
                double pos = (t * 0.45 % 1.0) * (track.Width + seg) - seg;
                float x0 = (float)Math.Max(track.X, track.X + pos);
                float x1 = (float)Math.Min(track.Right, track.X + pos + seg);
                if (x1 > x0)
                {
                    using (SolidBrush b = new SolidBrush(accent))
                        FillRound(g, b, new RectangleF(x0, track.Y, x1 - x0, h), h / 2);
                }
            }
        }

        /// <summary>Green tick inside a filled green circle - the download finished.</summary>
        private void DrawGreenTick(Graphics g, RectangleF box, double s)
        {
            Color green = Color.FromArgb(255, 52, 199, 89);
            using (SolidBrush b = new SolidBrush(green))
                g.FillEllipse(b, box);
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(60, 52, 199, 89)))
                g.FillEllipse(glow, box.X - box.Width * 0.12f, box.Y - box.Height * 0.12f,
                    box.Width * 1.24f, box.Height * 1.24f);

            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            using (Pen p = new Pen(Color.FromArgb(255, 255, 255, 255), Math.Max(1.6f, (float)(2.6 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[]
                {
                    new PointF(cx - box.Width * 0.22f, cy + box.Height * 0.02f),
                    new PointF(cx - box.Width * 0.06f, cy + box.Height * 0.18f),
                    new PointF(cx + box.Width * 0.24f, cy - box.Height * 0.17f)
                });
            }
        }

        /// <summary>Bluetooth or Wi-Fi mark for the toggle notice.</summary>
        private void DrawRadioGlyph(Graphics g, RectangleF box, double s, Color c, string what)
        {
            bool wifi = what != null && what.IndexOf("wi", StringComparison.OrdinalIgnoreCase) >= 0;
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (Pen p = new Pen(c, Math.Max(1.15f, (float)(1.5 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                if (wifi)
                {
                    g.DrawArc(p, cx - 7 * u, cy - 2 * u, 14 * u, 14 * u, 200, 140);
                    g.DrawArc(p, cx - 4.6f * u, cy + 0.6f * u, 9.2f * u, 9.2f * u, 200, 140);
                    using (SolidBrush b = new SolidBrush(c))
                        g.FillEllipse(b, cx - 1.2f * u, cy + 5 * u, 2.4f * u, 2.4f * u);
                }
                else
                {
                    g.DrawLine(p, cx, cy - 6 * u, cx, cy + 6 * u);
                    g.DrawLine(p, cx, cy - 6 * u, cx + 3.8f * u, cy - 2.6f * u);
                    g.DrawLine(p, cx + 3.8f * u, cy - 2.6f * u, cx - 3.4f * u, cy + 2.6f * u);
                    g.DrawLine(p, cx, cy + 6 * u, cx + 3.8f * u, cy + 2.6f * u);
                    g.DrawLine(p, cx + 3.8f * u, cy + 2.6f * u, cx - 3.4f * u, cy - 2.6f * u);
                }
            }
        }

        /// <summary>Small globe used for the keyboard-layout notice.</summary>
        private void DrawGlobeGlyph(Graphics g, RectangleF box, double s, Color c)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (Pen p = new Pen(c, Math.Max(1.1f, (float)(1.5 * s))))
            {
                g.DrawEllipse(p, cx - 6 * u, cy - 6 * u, 12 * u, 12 * u);
                g.DrawEllipse(p, cx - 2.6f * u, cy - 6 * u, 5.2f * u, 12 * u);
                g.DrawLine(p, cx - 6 * u, cy, cx + 6 * u, cy);
            }
        }

        /// <summary>Diagonal strike showing a radio was switched off.</summary>
        private void DrawStrike(Graphics g, RectangleF box, double s, Color c)
        {
            using (Pen p = new Pen(Color.FromArgb(230, 255, 99, 99), Math.Max(1.4f, (float)(1.9 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, box.Left, box.Bottom, box.Right, box.Top);
            }
        }
        /// <summary>Which transport carried the device: "bluetooth", "usb" or "other".</summary>
        private static string CardTransport(Activity a)
        {
            string k = a.Label == null ? "" : a.Label.ToLowerInvariant();
            if (k.IndexOf("bluetooth") >= 0 || k.IndexOf("bt ") >= 0) return "bluetooth";
            if (k.IndexOf("usb") >= 0 || k.IndexOf("drive") >= 0 || k.IndexOf("phone") >= 0
                || k.IndexOf("camera") >= 0 || k.IndexOf("input") >= 0) return "usb";
            if (k.IndexOf("network") >= 0) return "other";
            return "usb";
        }

        /// <summary>
        /// Charging pill shown ON its own: green bolt and the percentage. Used when nothing
        /// else is on screen; when something is, the badge moves beside the island instead.
        /// </summary>
        private void DrawChargePill(Graphics g, RectangleF pill, double s)
        {
            string pct = chargeText;
            Font f = Fonts.Get("Segoe UI Variable Text", 13.5f * (float)s, FontStyle.Bold);
            SizeF ts = SizeF.Empty;
            using (Graphics gr = Graphics.FromHwnd(IntPtr.Zero)) { try { ts = g.MeasureString(pct, f); } catch { } }

            double bolt = 17 * s;
            double gap = 5 * s;
            double pad = 12 * s;
            double totalW = bolt + (pct.Length > 0 ? gap + ts.Width : 0);
            double cx = pill.X + (pill.Width - totalW) / 2.0;
            double cy = pill.Y + pill.Height / 2.0;

            RectangleF box = new RectangleF((float)cx, (float)(cy - bolt / 2), (float)bolt, (float)bolt);
            if (pct.Length > 0)
            {
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(255, 90, 220, 130)))
                using (StringFormat sf = new StringFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Alignment = StringAlignment.Near;
                    g.DrawString(pct, f, ink,
                        new RectangleF((float)(cx + bolt + gap), pill.Y, (float)(ts.Width + 4), pill.Height), sf);
                }
            }
            DrawBolt(g, box, s);
        }

        /// <summary>The detached charge badge: a small island on the right with the green bolt.</summary>
        private void DrawChargeIslandShape(Graphics g, RectangleF box, double s)
        {
            using (GraphicsPath path = Round(box, (float)(box.Height / 2)))
            {
                using (SolidBrush body = new SolidBrush(Color.FromArgb(255, 6, 8, 6)))
                    g.FillPath(body, path);
                using (Pen rim = new Pen(Color.FromArgb(200, 40, 170, 80), Math.Max(1f, (float)(1.6 * s))))
                    g.DrawPath(rim, path);
            }
            DrawBolt(g, new RectangleF(box.X + box.Width * 0.22f, box.Y + box.Height * 0.17f,
                box.Width * 0.56f, box.Height * 0.66f), s);
        }

        /// <summary>
        /// The status colours to show as strips along the top edge. Several at once are
        /// drawn side by side, so the island reads as "part green, part orange".
        /// </summary>
        private List<Color> BandColors()
        {
            List<Color> list = new List<Color>();
            if (charging) list.Add(BandCharge);
            if (dotShown) list.Add(BandMic);
            if (media.HasTrack && media.Playing) list.Add(BandMusic);

            Activity a = shown;
            if (a != null)
            {
                switch (a.Kind)
                {
                    case ActivityKind.Language: list.Add(BandLanguage); break;
                    case ActivityKind.Radio: list.Add(BandRadio); break;
                    case ActivityKind.Download: list.Add(BandDownload); break;
                    case ActivityKind.Device:
                    case ActivityKind.Notification:
                    case ActivityKind.AppLaunch:
                    case ActivityKind.Timer:
                    case ActivityKind.Stopwatch:
                    case ActivityKind.Alarm:
                        list.Add(BandEvent); break;
                }
            }
            return list;
        }

        /// <summary>
        /// Paints the colour strip. Clipped to the capsule so it follows the rounded shape,
        /// with a soft falloff downward so it reads as a glow rather than a hard edge.
        /// </summary>
        private void DrawBands(Graphics g, RectangleF pill, double s, List<Color> colors)
        {
            if (colors == null || colors.Count == 0) return;

            // Proportional to the capsule, not a fixed number of pixels: the band has to
            // look right on a 37 dp pill AND on the 15 dp flat one. Kept deliberately thin
            // so it reads as an edge light rather than as a fill.
            float h = (float)Math.Max(1.2 * s, Math.Min(BandH * s * 0.38, pill.Height * 0.15));
            float falloff = h * 0.8f;
            float bandW = pill.Width / colors.Count;

            GraphicsState st = g.Save();
            using (GraphicsPath clip = Round(pill, (float)Math.Min(pill.Height / 2.0, 26 * s)))
                g.SetClip(clip);

            for (int i = 0; i < colors.Count; i++)
            {
                RectangleF r = new RectangleF((float)(pill.X + bandW * i), pill.Y, bandW + 1f, h);
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new RectangleF(r.X, pill.Y, r.Width, h + falloff),
                    Color.FromArgb(255, colors[i]), Color.FromArgb(0, colors[i]),
                    LinearGradientMode.Vertical))
                {
                    g.FillRectangle(br, r);
                }
            }
            g.Restore(st);
        }
        /// <summary>Green lightning bolt.</summary>
        private void DrawBolt(Graphics g, RectangleF box, double s)
        {
            float x = box.X, y = box.Y, w = box.Width, h = box.Height;
            PointF[] pts = new PointF[]
            {
                new PointF(x + w * 0.58f, y + h * 0.00f),
                new PointF(x + w * 0.22f, y + h * 0.56f),
                new PointF(x + w * 0.47f, y + h * 0.56f),
                new PointF(x + w * 0.38f, y + h * 1.00f),
                new PointF(x + w * 0.79f, y + h * 0.42f),
                new PointF(x + w * 0.52f, y + h * 0.42f)
            };
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(255, 60, 210, 110)))
                g.FillPolygon(fill, pts);
            using (Pen edge = new Pen(Color.FromArgb(120, 210, 255, 220), Math.Max(0.8f, (float)(0.9 * s))))
                g.DrawPolygon(edge, pts);
        }
        /// <summary>Small capsule with just the glyph, used while a stopwatch/timer runs.</summary>
        private void DrawCompactGlyph(Graphics g, RectangleF pill, double s, Activity a)
        {
            double pad = 10 * s;
            double d = Math.Min(pill.Height * 0.58, pill.Width - pad * 2);
            if (d < 6 * s) return;
            RectangleF icon = new RectangleF((float)(pill.X + (pill.Width - d) / 2),
                (float)(pill.Y + (pill.Height - d) / 2), (float)d, (float)d);

            Color accent = ActivityAccent(a);
            if (a.Kind == ActivityKind.Timer) DrawRing(g, icon, a.Progress, s, accent);
            else DrawStopwatchGlyph(g, icon, s, accent);
        }

        /// <summary>Stopwatch outline: a dial with a crown and one hand.</summary>
        private void DrawStopwatchGlyph(Graphics g, RectangleF box, double s, Color c)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (Pen p = new Pen(c, Math.Max(1.2f, (float)(1.7 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawEllipse(p, cx - 5.4f * u, cy - 4.4f * u, 10.8f * u, 10.8f * u);
                g.DrawLine(p, cx, cy - 4.4f * u, cx, cy - 7.6f * u);          // crown stem
                g.DrawLine(p, cx - 2.2f * u, cy - 7.6f * u, cx + 2.2f * u, cy - 7.6f * u);
                g.DrawLine(p, cx, cy + 1.0f * u, cx, cy - 2.6f * u);          // hand
                g.DrawLine(p, cx, cy + 1.0f * u, cx + 2.6f * u, cy + 2.4f * u);
            }
        }

        /// <summary>USB or Bluetooth mark, drawn small in the card's corner.</summary>
        private void DrawTransportBadge(Graphics g, RectangleF box, double s, string transport)
        {
            Color c = transport == "bluetooth"
                ? Color.FromArgb(235, 140, 190, 255)
                : Color.FromArgb(235, 200, 200, 210);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(38, 255, 255, 255)))
            using (GraphicsPath p = Round(box, (float)(box.Height * 0.3)))
                g.FillPath(bg, p);

            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (Pen p = new Pen(c, Math.Max(1.05f, (float)(1.35 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                if (transport == "bluetooth")
                {
                    g.DrawLine(p, cx, cy - 5.6f * u, cx, cy + 5.6f * u);
                    g.DrawLine(p, cx, cy - 5.6f * u, cx + 3.6f * u, cy - 2.4f * u);
                    g.DrawLine(p, cx + 3.6f * u, cy - 2.4f * u, cx - 3.2f * u, cy + 2.4f * u);
                    g.DrawLine(p, cx, cy + 5.6f * u, cx + 3.6f * u, cy + 2.4f * u);
                    g.DrawLine(p, cx + 3.6f * u, cy + 2.4f * u, cx - 3.2f * u, cy - 2.4f * u);
                }
                else
                {
                    g.DrawLine(p, cx, cy + 5.6f * u, cx, cy - 5.0f * u);
                    using (SolidBrush br = new SolidBrush(c))
                    {
                        g.FillEllipse(br, cx - 1.7f * u, cy + 4.4f * u, 3.4f * u, 3.4f * u);
                        using (GraphicsPath a = new GraphicsPath())
                        {
                            a.AddPolygon(new PointF[]
                            {
                                new PointF(cx, cy - 6.8f * u),
                                new PointF(cx - 2.2f * u, cy - 3.6f * u),
                                new PointF(cx + 2.2f * u, cy - 3.6f * u)
                            });
                            g.FillPath(br, a);
                        }
                    }
                    g.DrawLine(p, cx, cy - 0.8f * u, cx - 3.6f * u, cy - 3.2f * u);
                    g.DrawLine(p, cx, cy + 1.2f * u, cx + 3.6f * u, cy - 1.2f * u);
                }
            }
        }

        /// <summary>Generic information glyph for plain notifications.</summary>
        private void DrawInfoGlyph(Graphics g, RectangleF box, double s, Color c)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (Pen p = new Pen(c, Math.Max(1.3f, (float)(1.9 * s))))
                g.DrawEllipse(p, cx - 6.4f * u, cy - 6.4f * u, 12.8f * u, 12.8f * u);
            using (SolidBrush br = new SolidBrush(c))
            {
                g.FillEllipse(br, cx - 1.2f * u, cy - 4.4f * u, 2.4f * u, 2.4f * u);
                using (GraphicsPath p = Round(new RectangleF(cx - 1.2f * u, cy - 0.9f * u, 2.4f * u, 5.4f * u), 1.1f * u))
                    g.FillPath(br, p);
            }
        }

        /// <summary>Big device icon chosen from the device's name.</summary>
        private void DrawDeviceIcon(Graphics g, RectangleF box, double s, Color accent, string label)
        {
            string k = label == null ? "" : label.ToLowerInvariant();
            if (k.IndexOf("head") >= 0 || k.IndexOf("audio") >= 0 || k.IndexOf("buds") >= 0
                || k.IndexOf("speaker") >= 0 || k.IndexOf("sound") >= 0)
                DrawHeadphoneGlyph(g, box, s, accent);
            else if (k.IndexOf("gamepad") >= 0 || k.IndexOf("controller") >= 0 || k.IndexOf("xbox") >= 0
                || k.IndexOf("joy") >= 0 || k.IndexOf("dualsense") >= 0 || k.IndexOf("dualshock") >= 0)
                DrawGamepadGlyph(g, box, s, accent);
            else if (k.IndexOf("phone") >= 0 || k.IndexOf("android") >= 0 || k.IndexOf("iphone") >= 0)
                DrawPhoneGlyph(g, box, s, accent);
            else
                DrawUsbStickGlyph(g, box, s, accent);       // default: a flash drive
        }

        private void DrawUsbStickGlyph(Graphics g, RectangleF box, double s, Color c)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (SolidBrush br = new SolidBrush(Color.FromArgb(225, c)))
            {
                using (GraphicsPath p = Round(new RectangleF(cx - 5.2f * u, cy - 2.6f * u, 8.6f * u, 6.6f * u), 1.3f * u))
                    g.FillPath(br, p);
                using (GraphicsPath p = Round(new RectangleF(cx + 3.0f * u, cy - 1.1f * u, 4.2f * u, 3.6f * u), 0.7f * u))
                    g.FillPath(br, p);
                g.FillRectangle(br, cx + 6.6f * u, cy - 0.4f * u, 1.4f * u, 2.2f * u);
            }
            using (SolidBrush dark = new SolidBrush(Color.FromArgb(150, 12, 12, 16)))
                g.FillRectangle(dark, cx - 3.6f * u, cy - 1.2f * u, 5.0f * u, 3.8f * u);
        }

        private void DrawHeadphoneGlyph(Graphics g, RectangleF box, double s, Color c)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (Pen p = new Pen(c, Math.Max(1.5f, (float)(2.1 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, cx - 6.4f * u, cy - 6.4f * u, 12.8f * u, 12.8f * u, 180, 180);
            }
            using (SolidBrush br = new SolidBrush(c))
            {
                using (GraphicsPath p = Round(new RectangleF(cx - 7.6f * u, cy - 0.6f * u, 3.6f * u, 6.4f * u), 1.6f * u))
                    g.FillPath(br, p);
                using (GraphicsPath p = Round(new RectangleF(cx + 4.0f * u, cy - 0.6f * u, 3.6f * u, 6.4f * u), 1.6f * u))
                    g.FillPath(br, p);
            }
        }

        private void DrawGamepadGlyph(Graphics g, RectangleF box, double s, Color c)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (SolidBrush br = new SolidBrush(c))
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddBezier(cx - 7.2f * u, cy - 1.0f * u, cx - 6.4f * u, cy - 5.4f * u,
                            cx - 2.0f * u, cy - 4.4f * u, cx, cy - 4.4f * u);
                p.AddBezier(cx, cy - 4.4f * u, cx + 2.0f * u, cy - 4.4f * u,
                            cx + 6.4f * u, cy - 5.4f * u, cx + 7.2f * u, cy - 1.0f * u);
                p.AddBezier(cx + 7.2f * u, cy - 1.0f * u, cx + 7.8f * u, cy + 5.6f * u,
                            cx + 4.0f * u, cy + 5.6f * u, cx + 2.6f * u, cy + 3.0f * u);
                p.AddLine(cx + 2.6f * u, cy + 3.0f * u, cx - 2.6f * u, cy + 3.0f * u);
                p.AddBezier(cx - 2.6f * u, cy + 3.0f * u, cx - 4.0f * u, cy + 5.6f * u,
                            cx - 7.8f * u, cy + 5.6f * u, cx - 7.2f * u, cy - 1.0f * u);
                p.CloseFigure();
                g.FillPath(br, p);
            }
            using (SolidBrush dark = new SolidBrush(Color.FromArgb(190, 12, 12, 16)))
            {
                g.FillRectangle(dark, cx - 5.4f * u, cy - 1.2f * u, 3.6f * u, 1.5f * u);
                g.FillRectangle(dark, cx - 4.3f * u, cy - 2.3f * u, 1.5f * u, 3.7f * u);
                g.FillEllipse(dark, cx + 2.6f * u, cy - 1.8f * u, 2.2f * u, 2.2f * u);
                g.FillEllipse(dark, cx + 4.6f * u, cy + 0.2f * u, 2.2f * u, 2.2f * u);
            }
        }

        private void DrawPhoneGlyph(Graphics g, RectangleF box, double s, Color c)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (SolidBrush br = new SolidBrush(c))
                FillRound(g, br, new RectangleF(cx - 3.6f * u, cy - 7.0f * u, 7.2f * u, 14.0f * u), 1.4f * u);
            using (SolidBrush dark = new SolidBrush(Color.FromArgb(200, 12, 12, 16)))
                g.FillRectangle(dark, cx - 2.6f * u, cy - 5.4f * u, 5.2f * u, 9.6f * u);
            using (Pen p = new Pen(Color.FromArgb(200, 12, 12, 16), Math.Max(1f, (float)(1.1 * s))))
                g.DrawEllipse(p, cx - 0.8f * u, cy + 4.9f * u, 1.6f * u, 1.6f * u);
        }
        private static Color ActivityAccent(Activity a)
        {
            switch (a.Kind)
            {
                case ActivityKind.Stopwatch: return Color.FromArgb(255, 120, 200, 255);
                case ActivityKind.Timer: return Color.FromArgb(255, 255, 190, 80);
                case ActivityKind.Alarm: return Color.FromArgb(255, 255, 120, 110);
                case ActivityKind.Device: return Color.FromArgb(255, 160, 230, 170);
                default: return Color.FromArgb(255, 200, 200, 210);
            }
        }

        /// <summary>Space reserved on the right for the two control glyphs.</summary>
        private static double ActivityButtonsWidth(Activity a, double s)
        {
            switch (a.Kind)
            {
                case ActivityKind.Stopwatch: return 62 * s;
                case ActivityKind.Timer: return 62 * s;
                case ActivityKind.Alarm: return 68 * s;
                default: return 24 * s;              // notices just get a close cross
            }
        }

        private void DrawActivityButtons(Graphics g, RectangleF pill, double s, Activity a, Color accent)
        {
            double pad = 10 * s;
            double cy = pill.Y + pill.Height / 2.0;
            double d = 22 * s;

            if (a.Kind == ActivityKind.Notification || a.Kind == ActivityKind.Device)
            {
                RectangleF close = new RectangleF((float)(pill.Right - pad - d), (float)(cy - d / 2), (float)d, (float)d);
                DrawCross(g, close, s, Color.FromArgb(170, 255, 255, 255));
                return;
            }

            RectangleF second = new RectangleF((float)(pill.Right - pad - d), (float)(cy - d / 2), (float)d, (float)d);
            RectangleF first = new RectangleF((float)(second.X - d - 6 * s), (float)(cy - d / 2), (float)d, (float)d);

            bool paused = !a.Running && !a.Finished;

            if (a.Kind == ActivityKind.Alarm)
            {
                // left: snooze 10 minutes, right: dismiss
                DrawGlyphPlus(g, first, s, Color.FromArgb(235, 255, 255, 255));
                DrawCross(g, second, s, Color.FromArgb(235, 255, 255, 255));
            }
            else
            {
                if (a.Finished)
                {
                    // ringing timer: left resets, right stops
                    DrawGlyphReset(g, first, s, Color.FromArgb(235, 255, 255, 255));
                    DrawCross(g, second, s, Color.FromArgb(235, 255, 255, 255));
                }
                else
                {
                    if (paused) DrawGlyphPlay(g, first, s, Color.FromArgb(235, 255, 255, 255));
                    else DrawGlyphPause(g, first, s, Color.FromArgb(235, 255, 255, 255));
                    DrawGlyphReset(g, second, s, Color.FromArgb(235, 255, 255, 255));
                }
            }
        }

        // ---- small control glyphs ----
        private void DrawGlyphPause(Graphics g, RectangleF b, double s, Color c)
        {
            float w = b.Width * 0.20f, h = b.Height * 0.52f;
            float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
            using (SolidBrush br = new SolidBrush(c))
            {
                FillRound(g, br, new RectangleF(cx - w * 1.5f, cy - h / 2, w, h), w * 0.35f);
                FillRound(g, br, new RectangleF(cx + w * 0.5f, cy - h / 2, w, h), w * 0.35f);
            }
        }

        private void DrawGlyphPlay(Graphics g, RectangleF b, double s, Color c)
        {
            float h = b.Height * 0.52f;
            float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
            using (SolidBrush br = new SolidBrush(c))
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddPolygon(new PointF[]
                {
                    new PointF(cx - h * 0.34f, cy - h / 2),
                    new PointF(cx - h * 0.34f, cy + h / 2),
                    new PointF(cx + h * 0.50f, cy)
                });
                g.FillPath(br, p);
            }
        }

        private void DrawGlyphReset(Graphics g, RectangleF b, double s, Color c)
        {
            float r = b.Width * 0.26f;
            float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
            using (Pen p = new Pen(c, Math.Max(1.2f, (float)(1.7 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, cx - r, cy - r, r * 2, r * 2, -60, 300);
            }
            using (SolidBrush br = new SolidBrush(c))
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddPolygon(new PointF[]
                {
                    new PointF(cx + r * 0.05f, cy - r * 1.45f),
                    new PointF(cx + r * 0.95f, cy - r * 0.75f),
                    new PointF(cx - r * 0.30f, cy - r * 0.45f)
                });
                g.FillPath(br, path);
            }
        }

        /// <summary>A plus sign with a tiny "10" hint - the alarm snooze control.</summary>
        private void DrawGlyphPlus(Graphics g, RectangleF b, double s, Color c)
        {
            float arm = (float)(b.Width * 0.24);
            float cx = b.X + b.Width / 2f, cy = (float)(b.Y + b.Height / 2f - 3.2 * s);
            using (Pen p = new Pen(c, Math.Max(1.3f, (float)(1.8 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, cx - arm, cy, cx + arm, cy);
                g.DrawLine(p, cx, cy - arm, cx, cy + arm);
            }
            Font f = Fonts.Get("Segoe UI Variable Text", 7.2f * (float)s, FontStyle.Bold);
            using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(c.A * 0.85), c)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString("10", f, br, new RectangleF(b.X, cy + arm * 0.9f, b.Width, b.Height * 0.36f), sf);
            }
        }

        private void DrawCross(Graphics g, RectangleF b, double s, Color c)
        {
            float arm = b.Width * 0.20f;
            float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
            using (Pen p = new Pen(c, Math.Max(1.3f, (float)(1.8 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, cx - arm, cy - arm, cx + arm, cy + arm);
                g.DrawLine(p, cx + arm, cy - arm, cx - arm, cy + arm);
            }
        }

        /// <summary>Circular progress ring: a dim track plus a bright accent arc.</summary>
        private void DrawRing(Graphics g, RectangleF box, double frac, double s, Color accent)
        {
            float w = Math.Max(1.6f, (float)(2.6 * s));
            using (Pen track = new Pen(Color.FromArgb(60, 255, 255, 255), w))
                g.DrawEllipse(track, box);
            float sweep = (float)(Anim.Clamp01(frac) * 360.0);
            if (sweep < 1) return;
            using (Pen arc = new Pen(accent, w))
            {
                arc.StartCap = LineCap.Round; arc.EndCap = LineCap.Round;
                g.DrawArc(arc, box, -90, sweep);
            }
        }

        private void DrawBell(Graphics g, RectangleF box, double s, Color accent, bool ringing)
        {
            DrawBellGlyph(g, box, s, ringing ? accent : Color.FromArgb(235, 235, 240));
        }

        private void DrawBellGlyph(Graphics g, RectangleF box, double s, Color c)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (SolidBrush br = new SolidBrush(c))
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddBezier(cx - 5.5f * u, cy + 3.4f * u, cx - 5.5f * u, cy - 6.6f * u,
                            cx + 5.5f * u, cy - 6.6f * u, cx + 5.5f * u, cy + 3.4f * u);
                p.AddLine(cx + 5.5f * u, cy + 3.4f * u, cx + 7.0f * u, cy + 5.0f * u);
                p.AddLine(cx + 7.0f * u, cy + 5.0f * u, cx - 7.0f * u, cy + 5.0f * u);
                p.CloseFigure();
                g.FillPath(br, p);
                g.FillEllipse(br, cx - 1.4f * u, cy - 8.0f * u, 2.8f * u, 2.8f * u);
                g.FillEllipse(br, cx - 1.8f * u, cy + 5.6f * u, 3.6f * u, 3.0f * u);
            }
        }

        /// <summary>USB or Bluetooth glyph depending on the device class.</summary>
        private void DrawDeviceGlyph(Graphics g, RectangleF box, double s, Color accent, string kind)
        {
            bool bt = kind != null && kind.IndexOf("lue", StringComparison.OrdinalIgnoreCase) >= 0;
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (Pen p = new Pen(accent, Math.Max(1.2f, (float)(1.8 * s))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                if (bt)
                {
                    // Bluetooth rune
                    g.DrawLine(p, cx, cy - 7 * u, cx, cy + 7 * u);
                    g.DrawLine(p, cx, cy - 7 * u, cx + 4.5f * u, cy - 3 * u);
                    g.DrawLine(p, cx + 4.5f * u, cy - 3 * u, cx - 4 * u, cy + 3 * u);
                    g.DrawLine(p, cx, cy + 7 * u, cx + 4.5f * u, cy + 3 * u);
                    g.DrawLine(p, cx + 4.5f * u, cy + 3 * u, cx - 4 * u, cy - 3 * u);
                }
                else
                {
                    // USB trident
                    g.DrawLine(p, cx, cy + 7 * u, cx, cy - 6 * u);
                    using (SolidBrush br = new SolidBrush(accent))
                    {
                        g.FillEllipse(br, cx - 2 * u, cy + 5.5f * u, 4 * u, 4 * u);
                        using (GraphicsPath a = new GraphicsPath())
                        {
                            a.AddPolygon(new PointF[]
                            {
                                new PointF(cx, cy - 8 * u),
                                new PointF(cx - 2.6f * u, cy - 4.4f * u),
                                new PointF(cx + 2.6f * u, cy - 4.4f * u)
                            });
                            g.FillPath(br, a);
                        }
                    }
                    g.DrawLine(p, cx, cy - 1 * u, cx - 4.5f * u, cy - 4 * u);
                    g.DrawLine(p, cx, cy + 1.5f * u, cx + 4.5f * u, cy - 1.5f * u);
                }
            }
        }

        /// <summary>
        /// Mini-player: a disc (real artwork when available), the track title, and a
        /// progress bar you can drag to seek. Tapping the disc skips to the next track.
        /// </summary>
        private void DrawMiniPlayer(Graphics g, RectangleF pill, double s, bool detached)
        {
            double pad = 10 * s;
            double cy = pill.Y + pill.Height / 2.0;
            double tile = pill.Height - pad * 1.4;

            // the microphone lives in its own little island to the left, so the music
            // pill keeps its normal padding either way
            RectangleF art = new RectangleF((float)(pill.X + pad), (float)(cy - tile / 2),
                (float)tile, (float)tile);

            // NOTE: the microphone is signalled by the little island OUTSIDE the pill,
            // so nothing orange is drawn inside it here.

            using (GraphicsPath p = Round(art, (float)(tile * 0.34)))
            {
                if (artwork != null)
                {
                    GraphicsState st = g.Save();
                    g.SetClip(p);
                    g.DrawImage(artwork, art);
                    g.Restore(st);
                    using (Pen pen = new Pen(Color.FromArgb(60, 255, 255, 255), (float)(1 * s)))
                        g.DrawPath(pen, p);
                }
                else
                {
                    using (LinearGradientBrush lg = new LinearGradientBrush(art,
                        Color.FromArgb(255, 46, 46, 54), Color.FromArgb(255, 18, 18, 24), 60f))
                        g.FillPath(lg, p);
                    using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255), (float)(1 * s)))
                        g.DrawPath(pen, p);
                }
                // the disc sits on top of the artwork, so it reads as a record
                DrawDisc(g, art, s, artwork == null ? 235 : 200);
            }

            // ---- title only (no artist clutter), vertically centred
            double tx = art.Right + 10 * s;
            double textW = pill.Right - pad - tx;
            string title = media.Title.Length > 0 ? media.Title : "Playing";
            Font fTitle = Fonts.Get("Segoe UI Variable Text", 11.2f * (float)s, FontStyle.Bold);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Alignment = StringAlignment.Near;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                if (textW > 24 * s)
                    g.DrawString(title, fTitle, ink,
                        new RectangleF((float)tx, (float)(pill.Y - 1.5 * s), (float)textW, pill.Height), sf);
            }

            // ---- progress bar along the bottom, draggable to seek
            long dur = media.DurationMs;
            if (dur > 1000)
            {
                long pos = seekPreviewMs >= 0 ? seekPreviewMs : media.PositionMs;
                double frac = Anim.Clamp01(pos / (double)dur);
                float barH = (float)(2.6 * s);
                float barY = pill.Bottom - (float)pad * 0.75f;
                RectangleF track = new RectangleF((float)(pill.X + pad), barY, (float)(pill.Width - pad * 2), barH);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
                    FillRound(g, b, track, barH / 2);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                    FillRound(g, b, new RectangleF(track.X, track.Y,
                        (float)Math.Max(barH, track.Width * frac), barH), barH / 2);
            }
        }

        /// <summary>Vinyl disc: dark platter, grooves, light label.</summary>
        private void DrawDisc(Graphics g, RectangleF tile, double s, int alpha)
        {
            float cx = tile.X + tile.Width / 2f, cy = tile.Y + tile.Height / 2f;
            float R = tile.Width * 0.27f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(alpha * 0.72), 12, 12, 14)))
                g.FillEllipse(b, cx - R, cy - R, R * 2, R * 2);
            using (Pen pen = new Pen(Color.FromArgb((int)(alpha * 0.55), 255, 255, 255), Math.Max(0.7f, (float)(0.8 * s))))
                g.DrawEllipse(pen, cx - R, cy - R, R * 2, R * 2);
            using (Pen pen = new Pen(Color.FromArgb((int)(alpha * 0.30), 255, 255, 255), Math.Max(0.6f, (float)(0.7 * s))))
            {
                g.DrawEllipse(pen, cx - R * 0.70f, cy - R * 0.70f, R * 1.40f, R * 1.40f);
                g.DrawEllipse(pen, cx - R * 0.50f, cy - R * 0.50f, R * 1.00f, R * 1.00f);
            }
            float lr = R * 0.30f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 235, 235, 240)))
                g.FillEllipse(b, cx - lr, cy - lr, lr * 2, lr * 2);
            float hr = R * 0.09f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 20, 20, 22)))
                g.FillEllipse(b, cx - hr, cy - hr, hr * 2, hr * 2);
        }

        private static Color ColorFromHash(int hash)
        {
            double h = (hash % 360) / 360.0;
            return FromHsl(h, 0.62, 0.56);
        }

        private static Color FromHsl(double h, double sat, double light)
        {
            double r, g, b;
            if (sat == 0) { r = g = b = light; }
            else
            {
                double q = light < 0.5 ? light * (1 + sat) : light + sat - light * sat;
                double p = 2 * light - q;
                r = Hue(p, q, h + 1.0 / 3);
                g = Hue(p, q, h);
                b = Hue(p, q, h - 1.0 / 3);
            }
            return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        private static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        /// <summary>Set to true to have Guard print the failing draw step and its stack.</summary>
        public static bool Trace = false;
        private static void Guard(string what, Action body)
        {
            if (!Trace) { body(); return; }
            try { body(); }
            catch (Exception ex)
            {
                Console.WriteLine("    [" + what + "] " + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine("    " + ex.StackTrace.Replace("\n", "\n    "));
                throw;
            }
        }

        /// <summary>Full expanded panel: waveform, clock, battery, then volume + brightness rows.</summary>
        private void DrawPanel(Graphics g, RectangleF pill, double s, double opacity)
        {
            int a = (int)(255 * opacity);
            if (a <= 2) return;
            double pad = 16 * s;
            double top = pill.Y + 11 * s;

            // ---- battery readout owns the top row, so it never collides with the waveform
            DrawBattery(g, new RectangleF((float)(pill.Right - pad - 52 * s),
                (float)(top + 3 * s), (float)(18 * s), (float)(10 * s)), s, (int)(a * 0.85));

            double level = volume < 0 ? 0.5 : (muted ? 0.04 : 0.16 + volume * 0.84);
            DrawWave(g, new RectangleF((float)(pill.X + pad), (float)(top + 24 * s),
                (float)(pill.Width - pad * 2), (float)(26 * s)), s, level, a);

            Font fDate = Fonts.Get("Segoe UI Variable Text", 9.6f * (float)s, FontStyle.Regular);
            Font fTime = Fonts.Get("Segoe UI Variable Display", 19.5f * (float)s, FontStyle.Regular);
            Font fSmall = Fonts.Get("Segoe UI Variable Text", 10.4f * (float)s, FontStyle.Regular);
            using (SolidBrush dim = new SolidBrush(Color.FromArgb((int)(a * 0.55), 255, 255, 255)))
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
            using (SolidBrush soft = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
            using (StringFormat sf = new StringFormat())
            using (StringFormat right = new StringFormat())
            {
                right.LineAlignment = StringAlignment.Center;
                right.Alignment = StringAlignment.Far;

                string battText = battery >= 0 ? battery + "%" + (charging ? " +" : "") : "AC";
                g.DrawString(battText, fSmall, soft,
                    new RectangleF((float)(pill.Right - pad - 32 * s), (float)(top - 1 * s), (float)(32 * s), (float)(18 * s)), right);

                float y = (float)(top + 62 * s);
                g.DrawString(dateText, fDate, dim,
                    new RectangleF((float)(pill.X + pad), y, (float)(pill.Width - pad * 2), (float)(15 * s)), sf);
                g.DrawString(DateTime.Now.ToString("HH:mm"), fTime, ink,
                    new RectangleF((float)(pill.X + pad), (float)(y + 12 * s), (float)(pill.Width - pad * 2), (float)(32 * s)), sf);

                // separator
                using (Pen pen = new Pen(Color.FromArgb((int)(a * 0.12), 255, 255, 255), (float)(1 * s)))
                    g.DrawLine(pen, (float)(pill.X + pad), (float)(pill.Y + 120 * s), (float)(pill.Right - pad), (float)(pill.Y + 120 * s));

                // ---- volume row
                double row1 = pill.Y + 127 * s;
                DrawSpeaker(g, new RectangleF((float)(pill.X + pad), (float)(row1 - 1 * s),
                    (float)(19 * s), (float)(19 * s)), s, a, muted);
                double tx = pill.X + pad + 28 * s;
                double tw = pill.Width - pad * 2 - 28 * s - 44 * s;
                DrawSlider(g, (float)tx, (float)(row1 + 8.5 * s), (float)tw, s,
                    volume < 0 ? 0 : (muted ? 0 : volume), a, null);
                string volText = muted ? "muted" : (volume < 0 ? "--" : Math.Round(volume * 100) + "%");
                g.DrawString(volText, fSmall, soft,
                    new RectangleF((float)(pill.Right - pad - 40 * s), (float)(row1 + 1 * s), (float)(40 * s), (float)(16 * s)), right);

                // ---- brightness row
                double row2 = pill.Y + 152 * s;
                DrawSun(g, new RectangleF((float)(pill.X + pad), (float)(row2 - 1 * s),
                    (float)(19 * s), (float)(19 * s)), s, a, brightness);
                bool bav = brightness >= 0;
                double bfrac = bav ? Anim.Clamp01((brightness - Brightness.Min) / (double)Math.Max(1, Brightness.Max - Brightness.Min)) : 0;
                DrawSlider(g, (float)tx, (float)(row2 + 8.5 * s), (float)tw, s, bfrac, a, null);
                g.DrawString(bav ? Math.Round(bfrac * 100) + "%" : "n/a", fSmall, soft,
                    new RectangleF((float)(pill.Right - pad - 40 * s), (float)(row2 + 1 * s), (float)(40 * s), (float)(16 * s)), right);
            }
        }

        /// <summary>
        /// Compact volume bezel: speaker glyph, percentage, then a filled bar.
        /// IMPORTANT: the pill arrives already in physical pixels, so offsets inside it are
        /// scaled by (1 + scale) terms only where a design-pixel margin is intended.
        /// </summary>
        private void DrawHud(Graphics g, RectangleF pill, double s, double opacity)
        {
            int a = (int)(255 * Anim.Clamp01(opacity));
            if (a <= 2) return;

            double pad = 15 * s;                       // design margin -> physical
            double cy = pill.Y + pill.Height / 2.0;
            double frac = volume < 0 ? 0 : (muted ? 0 : volume);

            DrawSpeaker(g, new RectangleF((float)(pill.X + pad), (float)(cy - 8 * s),
                (float)(16 * s), (float)(16 * s)), s, a, muted);

            string txt = muted ? "mute" : (volume < 0 ? "--" : Math.Round(volume * 100) + "%");
            double textX = pill.X + pad + 21 * s;
            double textW = 34 * s;
            Font f = Fonts.Get("Segoe UI Variable Text", 10.4f * (float)s, FontStyle.Regular);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Alignment = StringAlignment.Near;
                g.DrawString(txt, f, ink,
                    new RectangleF((float)textX, pill.Y, (float)textW, pill.Height), sf);
            }

            // the bar takes whatever is left, but only if that leaves a usable width
            double bx = textX + textW + 7 * s;
            double bw = pill.Right - pad - bx;
            if (bw < 12 * s) return;

            RectangleF track = new RectangleF((float)bx, (float)(cy - 3.5 * s), (float)bw, (float)(7 * s));
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(a * 0.22), 255, 255, 255)))
                FillRound(g, b, track, (float)(3.5 * s));
            if (frac > 0.005)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                    FillRound(g, b, new RectangleF(track.X, track.Y,
                        (float)Math.Max(track.Height, track.Width * frac), track.Height), (float)(3.5 * s));
        }

        // ------------------------------------------------------------ primitives
        private void DrawWave(Graphics g, RectangleF box, double s, double level, int alpha)
        {
            DrawWave(g, box, s, level, alpha, 42);
        }

        private void DrawWave(Graphics g, RectangleF box, double s, double level, int alpha, int bars)
        {
            double mid = box.Y + box.Height / 2.0;
            double gapX = 2.3 * s;
            if (bars > 20) gapX = 2.3 * s; else gapX = 1.6 * s;
            double bw = Math.Max(1.3, (box.Width - gapX * (bars - 1)) / bars);
            double t = since.Elapsed.TotalSeconds;

            for (int i = 0; i < bars; i++)
            {
                double p = bars > 1 ? i / (double)(bars - 1) : 0;
                // compact waveforms want a flatter envelope, wide ones taper more
                double env = bars > 20 ? Math.Sin(Math.PI * p) : 0.55 + 0.45 * Math.Sin(Math.PI * p);
                double wob = 0.5 + 0.5 * Math.Sin(t * 1.7 + i * 0.55) * Math.Sin(t * 0.86 + i * 0.21);
                double amp = box.Height * 0.46 * env * (0.25 + wob * 0.75) * (0.14 + level * 1.05);
                double hgt = Math.Max(1.1 * s, Math.Abs(amp));
                double x = box.X + i * (bw + gapX);
                int al = (int)(alpha * (0.5 + 0.5 * env));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(al, 255, 255, 255)))
                    FillRound(g, b, new RectangleF((float)x, (float)(mid - hgt), (float)bw, (float)(hgt * 2)), (float)(bw / 2));
            }
        }

        /// <summary>Camera body with a lens, used by the privacy indicator.</summary>
        private void DrawCamera(Graphics g, RectangleF box, double s, int alpha)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
            {
                using (GraphicsPath p = Round(new RectangleF(cx - 7 * u, cy - 4.4f * u, 11 * u, 8.8f * u), 1.8f * (float)u))
                    g.FillPath(b, p);
                // lens housing
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddPolygon(new PointF[]
                    {
                        new PointF(cx + 4 * u, cy - 1.6f * u),
                        new PointF(cx + 7.2f * u, cy - 4.2f * u),
                        new PointF(cx + 7.2f * u, cy + 4.2f * u),
                        new PointF(cx + 4 * u, cy + 1.6f * u)
                    });
                    g.FillPath(b, p);
                }
            }
            // dark lens dot
            using (SolidBrush dark = new SolidBrush(Color.FromArgb(alpha, 14, 14, 16)))
                g.FillEllipse(dark, cx - 4.4f * u, cy - 2 * u, 4 * u, 4 * u);
        }

        /// <summary>Microphone capsule on a stand.</summary>
        private void DrawMic(Graphics g, RectangleF box, double s, int alpha)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
            {
                using (GraphicsPath p = Round(new RectangleF(cx - 2.3f * u, cy - 7 * u, 4.6f * u, 8.6f * u), 2.3f * (float)u))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 1.5f * (float)s))
                {
                    g.DrawArc(pen, cx - 4.6f * u, cy - 4.4f * u, 9.2f * u, 8.4f * u, 0, 180);
                    g.DrawLine(pen, cx, cy + 4 * u, cx, cy + 6.6f * u);
                    g.DrawLine(pen, cx - 2.6f * u, cy + 6.6f * u, cx + 2.6f * u, cy + 6.6f * u);
                }
            }
        }

        /// <summary>Music note, used when no artwork is available.</summary>
        private void DrawNote(Graphics g, RectangleF tile, double s, int alpha)
        {
            float cx = tile.X + tile.Width / 2f, cy = tile.Y + tile.Height / 2f;
            float u = tile.Width / 16f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
            {
                g.FillEllipse(b, cx - 5.4f * u, cy + 2.2f * u, 4.4f * u, 3.4f * u);
                g.FillEllipse(b, cx + 1.6f * u, cy + 1.0f * u, 4.4f * u, 3.4f * u);
                using (Pen pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 1.7f * (float)s))
                {
                    g.DrawLine(pen, cx - 1.2f * u, cy + 3.6f * u, cx - 1.2f * u, cy - 5.4f * u);
                    g.DrawLine(pen, cx + 5.8f * u, cy + 2.4f * u, cx + 5.8f * u, cy - 6.2f * u);
                    g.DrawLine(pen, cx - 1.2f * u, cy - 5.4f * u, cx + 5.8f * u, cy - 6.2f * u);
                }
            }
        }

        private void DrawSlider(Graphics g, float x, float y, float width, double s, double frac, int alpha, double[] ticks)
        {
            float hgt = (float)(5 * s);
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(alpha * 0.20), 255, 255, 255)))
                FillRound(g, b, new RectangleF(x, y - hgt / 2, width, hgt), hgt / 2);

            float fw = (float)(width * Anim.Clamp01(frac));
            if (fw > 0.6f)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                    FillRound(g, b, new RectangleF(x, y - hgt / 2, fw, hgt), hgt / 2);
                float kr = (float)(6.2 * s);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                    g.FillEllipse(b, x + fw - kr, y - kr, kr * 2, kr * 2);
            }
            if (ticks != null)
            {
                foreach (double tk in ticks)
                {
                    float tx = x + (float)(width * tk);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(alpha * 0.30), 255, 255, 255)))
                        g.FillEllipse(b, tx - (float)(0.95 * s), y + (float)(6.4 * s), (float)(1.9 * s), (float)(1.9 * s));
                }
            }
        }

        private void DrawBattery(Graphics g, RectangleF box, double s, int alpha)
        {
            float rad = (float)(box.Height * 0.32);
            using (Pen pen = new Pen(Color.FromArgb((int)(alpha * 0.5), 255, 255, 255), (float)(1.1 * s)))
            using (GraphicsPath p = Round(new RectangleF(box.X, box.Y, box.Width, box.Height), rad))
                g.DrawPath(pen, p);
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(alpha * 0.45), 255, 255, 255)))
                g.FillRectangle(b, box.Right + (float)(0.9 * s), box.Y + box.Height * 0.34f, (float)(1.7 * s), box.Height * 0.32f);

            if (battery < 0) return;
            float f = battery / 100f;
            float iw = (float)Math.Max(0.6, (box.Width - box.Height * 0.5f) * f);
            RectangleF inner = new RectangleF(box.X + box.Height * 0.25f, box.Y + box.Height * 0.25f, iw, box.Height * 0.5f);
            int aa = (int)(alpha * (battery <= 20 ? 0.95 : 0.78));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(aa, 255, 255, 255)))
                FillRound(g, b, inner, (float)(box.Height * 0.2));

            if (charging)
            {
                float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 255, 232, 140)))
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddPolygon(new PointF[]
                    {
                        new PointF(cx + 1.1f*(float)s, cy - 3.0f*(float)s),
                        new PointF(cx - 1.5f*(float)s, cy + 0.4f*(float)s),
                        new PointF(cx - 0.2f*(float)s, cy + 0.4f*(float)s),
                        new PointF(cx - 1.0f*(float)s, cy + 3.0f*(float)s),
                        new PointF(cx + 1.6f*(float)s, cy - 0.5f*(float)s),
                        new PointF(cx + 0.3f*(float)s, cy - 0.5f*(float)s)
                    });
                    g.FillPath(b, p);
                }
            }
        }

        private void DrawSpeaker(Graphics g, RectangleF box, double s, int alpha, bool mute)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddPolygon(new PointF[]
                {
                    new PointF(cx - 6.2f*u, cy - 2.2f*u), new PointF(cx - 3.4f*u, cy - 2.2f*u),
                    new PointF(cx - 0.7f*u, cy - 5.0f*u), new PointF(cx - 0.7f*u, cy + 5.0f*u),
                    new PointF(cx - 3.4f*u, cy + 2.2f*u), new PointF(cx - 6.2f*u, cy + 2.2f*u)
                });
                g.FillPath(b, p);
            }
            using (Pen pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 1.45f * (float)s))
            {
                if (mute)
                {
                    g.DrawLine(pen, cx + 1.6f*u, cy - 3.0f*u, cx + 6.0f*u, cy + 3.0f*u);
                    g.DrawLine(pen, cx + 6.0f*u, cy - 3.0f*u, cx + 1.6f*u, cy + 3.0f*u);
                }
                else
                {
                    g.DrawArc(pen, cx - 0.3f*u, cy - 3.4f*u, 5.0f*u, 6.8f*u, -58, 116);
                    g.DrawArc(pen, cx - 0.3f*u, cy - 6.2f*u, 9.0f*u, 12.4f*u, -54, 108);
                }
            }
        }

        private void DrawSun(Graphics g, RectangleF box, double s, int alpha, int value)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float u = box.Width / 16f;
            int dim = (value >= 0 && value < 40) ? (int)(alpha * 0.55) : alpha;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(dim, 255, 255, 255)))
                g.FillEllipse(b, cx - 3.0f*u, cy - 3.0f*u, 6.0f*u, 6.0f*u);
            using (Pen pen = new Pen(Color.FromArgb(dim, 255, 255, 255), 1.3f * (float)s))
            {
                for (int i = 0; i < 8; i++)
                {
                    double ang = i * Math.PI / 4;
                    g.DrawLine(pen,
                        cx + (float)(Math.Cos(ang) * 4.6 * u), cy + (float)(Math.Sin(ang) * 4.6 * u),
                        cx + (float)(Math.Cos(ang) * 6.4 * u), cy + (float)(Math.Sin(ang) * 6.4 * u));
                }
            }
        }

        // ------------------------------------------------------------ path utils
        internal static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            rad = Math.Max(0, Math.Min(rad, Math.Min(r.Width, r.Height) / 2f));
            if (rad < 0.5f) { p.AddRectangle(r); return p; }
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        internal static void FillRound(Graphics g, Brush b, RectangleF r, float rad)
        {
            using (GraphicsPath p = Round(r, rad)) g.FillPath(b, p);
        }
    }

    // ================================================================== window
    internal sealed class IslandWindow : Form
    {
        private const int IdExit = 1, IdCenter = 2, IdReset = 3, IdAutoStart = 4, IdAbout = 5;
        private const int IdWidthBase = 100, IdEditSettings = 6;

        /// <summary>Resting-width presets offered in the tray menu: {width, label}.</summary>
        private static readonly int[][] WidthPresets = new int[][]
        {
            new int[] { 16, 0 },     // dot
            new int[] { 40, 1 },
            new int[] { 62, 2 },     // default
            new int[] { 90, 3 },
            new int[] { 127, 4 },    // original
            new int[] { 170, 5 }
        };

        private static string WidthPresetLabel(int w, int kind)
        {
            switch (kind)
            {
                case 0: return "Dot (" + w + " px)";
                case 1: return "Tiny (" + w + " px)";
                case 2: return "Small (" + w + " px)";
                case 3: return "Medium (" + w + " px)";
                case 4: return "Wide (" + w + " px)";
                default: return "Extra wide (" + w + " px)";
            }
        }

        private Settings settings = new Settings();

        private void OpenSettingsFile()
        {
            try
            {
                settings.Save();                       // make sure the file exists
                Process.Start(new ProcessStartInfo(Settings.FilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Native.MessageBox(hwnd, "Could not open " + Settings.FilePath + "\n\n" + ex.Message,
                    "Settings", Native.MB_ICONINFORMATION);
            }
        }
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "DynamicIsland";

        private readonly Island island;
        private readonly System.Windows.Forms.Timer timer;
        private IntPtr hwnd;
        private double scale = 1.0;
        private int canvasW = Island.CanvasW, canvasH = Island.CanvasH;
        private Point lastCursor = new Point(int.MinValue, int.MinValue);
        private bool lastInside, lastDown;
        private int trayAdded;
        private IntPtr trayIcon;
        private bool dirty = true;
        private DateTime lastFrame = DateTime.Now;
        private DateTime lastIdlePaint = DateTime.MinValue;
        private Rectangle boxVolume, boxBrightness, boxGlyph, boxToggle;
        private IntPtr curArrow;
        private MediaMonitor mediaMonitor;
        private ActivityManager activities;
        private ActivityPipe pipe;
        private DeviceWatcher devices;
        private LanguageWatcher languages;
        private AppLaunchWatcher appLaunches;
        private RadioWatcher radios;
        private DownloadWatcher downloads;
        private ChargeWatcher charges;
        private string lastLanguage = "";

        // ------------------------------------------------------- activity intake

        /// <summary>
        /// Parses one command line (from the pipe or the command line) and applies it.
        /// Runs on the UI thread.
        /// </summary>
        internal void RunActivityCommand(string raw)
        {
            if (activities == null || string.IsNullOrEmpty(raw)) return;
            string line = raw.Trim();
            if (line.Length == 0) return;

            string[] parts = SplitArgs(line);
            if (parts.Length == 0) return;
            string verb = parts[0].ToLowerInvariant();

            switch (verb)
            {
                case "stopwatch":
                case "sw":
                    {
                        string what = parts.Length > 1 ? parts[1].ToLowerInvariant() : "start";
                        if (what == "stop") activities.StopStopwatch();
                        else if (what == "reset") activities.ResetStopwatch();
                        else if (what == "pause" || what == "resume" || what == "toggle") activities.ToggleStopwatch();
                        else activities.StartStopwatch();
                        break;
                    }

                case "timer":
                    {
                        if (parts.Length < 2) { activities.StartTimer(TimeSpan.FromMinutes(5), "Timer"); break; }
                        string what = parts[1].ToLowerInvariant();
                        if (what == "pause" || what == "resume" || what == "toggle") activities.ToggleTimer();
                        else if (what == "reset") activities.ResetTimer();
                        else if (what == "dismiss") activities.DismissTimer();
                        else if (what.StartsWith("+"))
                        {
                            TimeSpan extra;
                            if (ActivityGrammar.TryDuration(what.Substring(1), out extra)) activities.ExtendTimer(extra);
                        }
                        else
                        {
                            TimeSpan d;
                            if (ActivityGrammar.TryDuration(what, out d))
                                activities.StartTimer(d, parts.Length > 2 ? parts[2] : "Timer");
                        }
                        break;
                    }

                case "alarm":
                    {
                        if (parts.Length < 2) break;
                        string what = parts[1].ToLowerInvariant();
                        if (what == "snooze") activities.SnoozeAlarm(TimeSpan.FromMinutes(10));
                        else if (what == "dismiss") activities.DismissAlarm();
                        else
                        {
                            TimeSpan at;
                            if (ActivityGrammar.TryTimeOfDay(what, out at))
                                activities.RingAlarm(at, "Alarm " + what);
                        }
                        break;
                    }

                case "notify":
                case "notification":
                    {
                        string title = parts.Length > 1 ? parts[1] : "Notification";
                        string body = parts.Length > 2 ? parts[2] : "";
                        activities.Notify(title, body, 9);
                        break;
                    }

                case "lang":
                    if (parts.Length > 1) activities.ShowLanguage(parts[1].ToLowerInvariant(), 3);
                    break;

                case "radio":
                    if (parts.Length > 1)
                        activities.ShowRadio(parts[1].ToLowerInvariant(),
                            parts.Length > 2 ? parts[2].ToLowerInvariant() != "off" : true, 3);
                    break;

                case "download":
                    if (parts.Length > 1)
                    {
                        double frac = -1;
                        if (parts.Length > 2)
                        {
                            double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out frac);
                        }
                        activities.DownloadProgress(parts[1], frac, 0, -1);
                    }
                    break;

                case "downloaded":
                    if (parts.Length > 1) activities.DownloadDone(parts[1], 6);
                    break;

                case "charging":
                    if (parts.Length > 1)
                    {
                        int pc = -1;
                        int.TryParse(parts[1].TrimEnd('%'), out pc);
                        if (pc < 0) activities.ClearCharging(); else activities.ShowCharging(pc);
                    }
                    break;

                case "app":
                    if (parts.Length > 1) activities.ShowAppLaunch(parts[1], null, 3);
                    break;

                case "clear":
                    activities.DismissAll();
                    break;
            }

            dirty = true;
        }

        /// <summary>Splits on spaces but keeps "quoted phrases" together.</summary>
        internal static string[] SplitArgs(string line)
        {
            List<string> outp = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (c == ' ' && !inQuotes)
                {
                    if (cur.Length > 0) { outp.Add(cur.ToString()); cur.Length = 0; }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0) outp.Add(cur.ToString());
            return outp.ToArray();
        }

        private void OnLanguageChanged(string label)
        {
            try
            {
                if (label == lastLanguage) return;
                lastLanguage = label;
                activities.ShowLanguage(label, 2);
                dirty = true;
            }
            catch { }
        }

        private void OnAppLaunched(string name, System.Drawing.Bitmap icon)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    activities.ShowAppLaunch(name, icon, 3);
                    dirty = true;
                });
            }
            catch { }
        }

        private void OnRadioChanged(string what, bool on)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    activities.ShowRadio(what, on, 3);
                    dirty = true;
                });
            }
            catch { }
        }

        private void OnCharging(int percent)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    // a negative value is how the watcher reports "no longer charging"
                    if (percent < 0)
                        activities.ClearCharging();
                    else
                        activities.ShowCharging(percent);
                    dirty = true;
                });
            }
            catch { }
        }

        private void OnDownloadProgress(string name, double fraction, bool done, long got, long total)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (done) activities.DownloadDone(name, 6);
                    else activities.DownloadProgress(name, fraction, got, total);
                    dirty = true;
                });
            }
            catch { }
        }

        private void OnDeviceEvent(DeviceEvent e)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    string detail = e.Detail;
                    if (e.Battery >= 0)
                        detail = (detail.Length > 0 ? detail + "  " : "") + e.Battery + "%" + (e.Charging ? " charging" : "");
                    activities.NotifyDevice(e.Name, detail, e.Battery, e.Charging, 9);
                    dirty = true;
                });
            }
            catch { }
        }

        /// <summary>
        /// Runs the media probe in a child process. A bad WinRT vtable slot can only
        /// kill that child, so the widget itself can never be taken down by it.
        /// </summary>
        private static bool ProbeMediaSupport()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--probe-media";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(4000))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }
                    return line != null && line.IndexOf("smtc=ok", StringComparison.Ordinal) >= 0;
                }
            }
            catch { return false; }
        }

        // persistent 32bpp DIB back buffer
        private IntPtr dib, dibOld, dibDc;
        private Graphics dibG;

        public IslandWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Dynamic Island";
            island = new Island(this);
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 8;                 // ~120 fps target for smooth morphs
            timer.Tick += OnTick;
        }

        /// <summary>An activity command supplied on the command line, applied once shown.</summary>
        public string PendingCommand;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style = Native.WS_POPUP;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW
                            | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hwnd = Handle;
            curArrow = Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW);
            ResolveGeometry(true);
            BuildTrayIcon();

            // resting width etc. come from settings.ini next to the exe
            settings = Settings.Load();
            island.ApplySettings(settings);

            // media detection sources: SMTC only if the child-process probe proved it safe
            bool smtc = ProbeMediaSupport();
            mediaMonitor = new MediaMonitor(smtc, false);   // window-title fallback off: false positives
            island.AttachMedia(mediaMonitor);

            // live activities: driven by the pipe, watched by the device monitor
            activities = new ActivityManager();
            try
            {
                ActivityManager.StatusPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Application.ExecutablePath), "island-status.txt");
            }
            catch { }
            activities.Changed += delegate { dirty = true; };
            island.AttachActivities(activities);

            pipe = new ActivityPipe(activities, RunActivityCommand);
            pipe.Start();

            devices = new DeviceWatcher();
            devices.Arrived += OnDeviceEvent;
            devices.Start();

            // keyboard layout switches (rus / eng / ukr ...)
            languages = new LanguageWatcher();
            lastLanguage = LanguageWatcher.CurrentLabel();
            languages.Changed += OnLanguageChanged;
            languages.Start();

            // newly opened application windows
            appLaunches = new AppLaunchWatcher();
            appLaunches.Launched += OnAppLaunched;
            appLaunches.Start();

            // bluetooth / wi-fi toggles
            radios = new RadioWatcher();
            radios.Changed += OnRadioChanged;
            radios.Start();

            // downloads (best effort - see the notes on DownloadWatcher)
            downloads = new DownloadWatcher();
            downloads.Progress += OnDownloadProgress;
            downloads.Start();

            // the battery being charged
            charges = new ChargeWatcher();
            charges.Charging += OnCharging;
            charges.Start();
        }

        // ----------------------------------------------------------- geometry
        private void ResolveGeometry(bool force)
        {
            uint dx = 96, dy = 96;
            try
            {
                IntPtr mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero) Native.GetDpiForMonitor(mon, Native.MDT_EFFECTIVE_DPI, out dx, out dy);
            }
            catch { }
            double ns = Math.Max(1.0, dx / 96.0);
            if (!force && Math.Abs(ns - scale) < 0.01) return;
            scale = ns;
            canvasW = (int)Math.Round(Island.CanvasW * scale);
            canvasH = (int)Math.Round(Island.CanvasH * scale);
            ClientSize = new Size(canvasW, canvasH);
            RebuildBackBuffer();
            dirty = true;
        }

        private void RebuildBackBuffer()
        {
            ReleaseBackBuffer();
            Native.BITMAPINFO bmi = new Native.BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = canvasW;
            bmi.bmiHeader.biHeight = -canvasH;          // negative => top-down rows
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = Native.BI_RGB;
            IntPtr bits;
            dib = Native.CreateDIBSection(IntPtr.Zero, ref bmi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return;
            dibDc = Native.CreateCompatibleDC(IntPtr.Zero);
            dibOld = Native.SelectObject(dibDc, dib);
            dibG = Graphics.FromHdc(dibDc);
            dibG.SmoothingMode = SmoothingMode.AntiAlias;
            dibG.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            dibG.InterpolationMode = InterpolationMode.HighQualityBicubic;
            dibG.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        private void ReleaseBackBuffer()
        {
            if (dibG != null) { dibG.Dispose(); dibG = null; }
            if (dibDc != IntPtr.Zero)
            {
                if (dibOld != IntPtr.Zero) Native.SelectObject(dibDc, dibOld);
                Native.DeleteDC(dibDc);
                dibDc = IntPtr.Zero; dibOld = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }
        }

        /// <summary>Distance from the top of the primary work area, honouring a top taskbar.</summary>
        private int AnchorTop()
        {
            try
            {
                IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
                if (tb != IntPtr.Zero)
                {
                    Native.RECT r;
                    if (Native.GetWindowRect(tb, out r) && r.Top <= 2)
                    {
                        int gap = r.Bottom;
                        if (gap >= 0 && gap < 400) return gap;
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// True when a genuinely full-screen app owns the screen. SHQueryUserNotificationState
        /// alone is too eager: it reports QUNS_BUSY for ordinary foreground windows, so the
        /// notification state is only trusted together with a full-screen foreground window.
        /// </summary>
        private bool FullScreenAppActive()
        {
            int qs;
            if (Native.SHQueryUserNotificationState(out qs) != 0) return false;
            if (qs != Native.QUNS_RUNNING_D3D_FULL_SCREEN) return false;

            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                Native.RECT r;
                if (!Native.GetWindowRect(fg, out r)) return false;
                IntPtr mon = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return false;
                Native.MONITORINFO mi = new Native.MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
                if (!Native.GetMonitorInfo(mon, ref mi)) return false;
                // within a couple of pixels of the whole monitor in both axes
                return r.Left <= mi.rcMonitor.Left + 2 && r.Top <= mi.rcMonitor.Top + 2
                    && r.Right >= mi.rcMonitor.Right - 2 && r.Bottom >= mi.rcMonitor.Bottom - 2;
            }
            catch { return false; }
        }

        private void Place()
        {
            try
            {
                Rectangle b = Screen.PrimaryScreen.Bounds;
                int x = b.Left + (b.Width - (int)Math.Round(Island.CanvasW * scale)) / 2;
                Location = new Point(x, AnchorTop());
            }
            catch { }
        }

        // --------------------------------------------------------------- tray
        private void BuildTrayIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 16, 16, 20)))
                        using (GraphicsPath p = Island.Round(new RectangleF(1, 9, 30, 14), 7))
                            g.FillPath(b, p);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                        using (GraphicsPath p = Island.Round(new RectangleF(6, 14, 9, 4), 2))
                            g.FillPath(b, p);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 120, 205, 255)))
                            g.FillEllipse(b, 19, 13, 6, 6);
                    }
                    trayIcon = bmp.GetHicon();
                }
                Native.NOTIFYICONDATA nid = new Native.NOTIFYICONDATA();
                nid.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA));
                nid.hWnd = hwnd;
                nid.uID = 1;
                nid.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
                nid.uCallbackMessage = Native.WM_TRAYICON;
                nid.hIcon = trayIcon;
                nid.szTip = "Dynamic Island";
                if (Native.Shell_NotifyIcon(Native.NIM_ADD, ref nid)) trayAdded = 1;
            }
            catch { }
        }

        private void RemoveTrayIcon()
        {
            if (trayAdded == 0) return;
            try
            {
                Native.NOTIFYICONDATA nid = new Native.NOTIFYICONDATA();
                nid.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA));
                nid.hWnd = hwnd; nid.uID = 1;
                Native.Shell_NotifyIcon(Native.NIM_DELETE, ref nid);
            }
            catch { }
            trayAdded = 0;
        }

        private bool AutoStartEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        private void ToggleAutoStart()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (k.GetValue(AppName) != null) k.DeleteValue(AppName, false);
                    else k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_TRAYICON)
            {
                int evt = m.LParam.ToInt32() & 0xFFFF;
                if (evt == Native.WM_RBUTTONUP) { ShowTrayMenu(); return; }
                if (evt == Native.WM_LBUTTONUP) { Place(); dirty = true; return; }
            }
            base.WndProc(ref m);
        }

        private void ShowTrayMenu()
        {
            IntPtr menu = Native.CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                Native.AppendMenu(menu, Native.MF_STRING, IdCenter, "Center island");
                Native.AppendMenu(menu, Native.MF_STRING, IdReset, "Move to default spot");
                Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);

                // ---- collapsed width presets ----
                IntPtr sizeMenu = Native.CreatePopupMenu();
                foreach (int[] preset in WidthPresets)
                {
                    int w = preset[0];
                    string label = WidthPresetLabel(preset[0], preset[1]);
                    int flags = Native.MF_STRING | (island.IdleWidth == w ? Native.MF_CHECKED : 0);
                    Native.AppendMenu(sizeMenu, flags, IdWidthBase + w, label);
                }
                Native.AppendMenu(menu, Native.MF_POPUP, (int)sizeMenu, "Resting width");
                Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);

                Native.AppendMenu(menu, Native.MF_STRING | (AutoStartEnabled() ? Native.MF_CHECKED : 0),
                    IdAutoStart, "Start with Windows");
                Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
                Native.AppendMenu(menu, Native.MF_STRING, IdAbout, "About");
                Native.AppendMenu(menu, Native.MF_STRING, IdEditSettings, "Edit settings file...");
                Native.AppendMenu(menu, Native.MF_STRING, IdExit, "Quit");

                Native.POINT p;
                Native.GetCursorPos(out p);
                Native.SetForegroundWindow(hwnd);
                int cmd = Native.TrackPopupMenu(menu,
                    Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY, p.X, p.Y, 0, hwnd, IntPtr.Zero);
                switch (cmd)
                {
                    case IdExit:
                        timer.Stop();
                        Close();
                        Application.Exit();
                        break;
                    case IdCenter:
                    case IdReset:
                        Place(); dirty = true; break;
                    case IdAutoStart:
                        ToggleAutoStart(); break;
                    case IdEditSettings:
                        OpenSettingsFile(); break;
                    default:
                        // width preset ids
                        if (cmd >= IdWidthBase && cmd <= IdWidthBase + Settings.MaxIdleWidth)
                        {
                            int w = cmd - IdWidthBase;
                            settings.IdleWidth = w;
                            settings.Save();
                            island.ApplySettings(settings);
                            dirty = true;
                        }
                        break;
                    case IdAbout:
                        Native.MessageBox(hwnd,
                            "Dynamic Island for Windows 11\n\n" +
                            "HOVER the pill to expand it, then drag the two bars to set\n" +
                            "volume and screen brightness. Click the speaker to mute.\n\n" +
                            "RESTING WIDTH - tray menu > Resting width. From a dot (16 px)\n" +
                            "up to 220 px. The size is stored in settings.ini next to the exe.\n\n" +
                            "MUSIC - while a track plays the pill shows a disc and the title.\n" +
                            "  drag the thin bar along the bottom  = seek\n" +
                            "  click the right third of the pill  = next track\n\n" +
                            "MICROPHONE - an orange dot appears in the left corner while the\n" +
                            "microphone is in use. If music is playing too, the dot detaches\n" +
                            "and floats just to the left of the pill.\n\n" +
                            "Right-click the tray icon for options.\n" +
                            "Diagnostics: --selftest, --privacy, --probe-media",
                            "About Dynamic Island", Native.MB_ICONINFORMATION);
                        break;
                }
            }
            finally { Native.DestroyMenu(menu); }
        }

        // --------------------------------------------------------- hit testing
        public bool HitTest(Point screen)
        {
            if (!Visible && !IsHandleCreated) return false;
            Rectangle b = Bounds;
            if (b.Width <= 0) return false;
            int pad = (int)Math.Round(4 * scale);
            int pw = (int)Math.Round(island.Width * scale);
            int ph = (int)Math.Round(island.Height * scale);
            Rectangle pill = new Rectangle(b.Left + (b.Width - pw) / 2 - pad,
                                           b.Top + (int)Math.Round(Island.TopMarginPx * scale) - pad,
                                           pw + pad * 2, ph + pad * 2);
            return pill.Contains(screen);
        }

        public Rectangle BoxVolume() { return boxVolume; }
        public Rectangle BoxBrightness() { return boxBrightness; }
        public Rectangle BoxGlyph() { return boxGlyph; }
        public Rectangle BoxToggle() { return boxToggle; }

        private void ComputeBoxes()
        {
            if (!island.Expanded || island.Height < 120)
            {
                boxVolume = boxBrightness = boxGlyph = boxToggle = Rectangle.Empty;
                return;
            }
            Rectangle b = Bounds;
            double s = scale;
            int pw = (int)Math.Round(island.Width * s);
            int px = b.Left + (b.Width - pw) / 2;
            int py = b.Top + (int)Math.Round(Island.TopMarginPx * s);
            double pad = 16 * s;
            int trackX = (int)(px + pad + 28 * s);
            int trackW = (int)(pw - pad * 2 - 28 * s - 44 * s);
            boxVolume = new Rectangle(trackX, (int)(py + 127 * s), trackW, (int)(14 * s));
            boxBrightness = new Rectangle(trackX, (int)(py + 152 * s), trackW, (int)(14 * s));
            boxGlyph = new Rectangle((int)(px + pad - 3 * s), (int)(py + 123 * s), (int)(26 * s), (int)(26 * s));
            boxToggle = new Rectangle(px, py, pw, (int)(44 * s));
        }

        public void ApplyCursor(IntPtr cursor)
        {
            try { if (cursor != IntPtr.Zero && Native.GetCursor() != cursor) Native.SetCursor(cursor); }
            catch { }
        }

        public void NeedFrame() { dirty = true; }

        /// <summary>Current DPI scale factor, so Island can lay out screen-space hit rects.</summary>
        public double ScaleForIsland { get { return scale; } }

        // ------------------------------------------------------------- the loop
        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                double dt = (DateTime.Now - lastFrame).TotalSeconds;
                lastFrame = DateTime.Now;
                if (dt <= 0) dt = 0.016;
                if (dt > 0.25) dt = 0.25;                 // ignore time spent suspended

                ResolveGeometry(false);

                // yield while a full-screen app or a presentation owns the screen
                int qs;
                bool suppress = false;
                if (Native.SHQueryUserNotificationState(out qs) == 0)
                    suppress = qs == Native.QUNS_PRESENTATION_MODE || FullScreenAppActive();

                Native.POINT cp;
                Native.GetCursorPos(out cp);
                Point cur = new Point(cp.X, cp.Y);

                if (suppress)
                {
                    island.OnMouseLeave();
                }
                else
                {
                    if (cur != lastCursor) { lastCursor = cur; island.OnMouseMove(cur); }
                    bool down = (Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left;
                    if (down && !lastDown) island.OnMouseDown(cur);
                    if (!down && lastDown) island.OnMouseUp(cur);
                    lastDown = down;
                }

                bool inside = !suppress && HitTest(cur);
                if (inside != lastInside)
                {
                    lastInside = inside;
                    if (!inside) island.OnMouseLeave();
                    dirty = true;
                }
                if (inside && !island.Dragging) ApplyCursor(curArrow);

                double measure = island.Width + island.Height * 1000.0;
                island.Tick(dt);
                if (Math.Abs(island.Width + island.Height * 1000.0 - measure) > 0.01) dirty = true;

                ComputeBoxes();

                if (suppress)
                {
                    if (Visible) Visible = false;
                    return;
                }
                if (!Visible) { Visible = true; Place(); dirty = true; }

                bool repaint = dirty || island.Expanded || island.Dragging
                    || (DateTime.Now - lastIdlePaint).TotalMilliseconds > 900;
                if (repaint)
                {
                    Render();
                    dirty = false;
                    lastIdlePaint = DateTime.Now;
                }
            }
            catch { }
        }

        private void Render()
        {
            if (dibG == null || canvasW <= 0) return;
            island.Draw(dibG, scale);

            Native.POINT dst = new Native.POINT { X = Left, Y = Top };
            Native.POINT src = new Native.POINT { X = 0, Y = 0 };
            Native.SIZE size = new Native.SIZE { CX = canvasW, CY = canvasH };
            Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
            bf.BlendOp = Native.AC_SRC_OVER;
            bf.BlendFlags = 0;
            bf.SourceConstantAlpha = 255;
            bf.AlphaFormat = Native.AC_SRC_ALPHA;

            Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, dibDc, ref src, 0, ref bf, Native.ULW_ALPHA);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ResolveGeometry(true);
            Place();
            dirty = true;
            Render();
            timer.Start();

            if (!string.IsNullOrEmpty(PendingCommand))
            {
                RunActivityCommand(PendingCommand);
                PendingCommand = null;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { timer.Stop(); } catch { }
            RemoveTrayIcon();
            Brightness.Release();
            ReleaseBackBuffer();
            try { if (pipe != null) pipe.Dispose(); } catch { }
            try { if (devices != null) devices.Dispose(); } catch { }
            try { if (languages != null) languages.Dispose(); } catch { }
            try { if (appLaunches != null) appLaunches.Dispose(); } catch { }
            try { if (radios != null) radios.Dispose(); } catch { }
            try { if (downloads != null) downloads.Dispose(); } catch { }
            try { if (charges != null) charges.Dispose(); } catch { }
            base.OnFormClosing(e);
        }
    }

    // ================================================================= program
    internal static class Program
    {
        [STAThread]
        /// <summary>
        /// Last-resort error reporting. Without this, ANY exception during startup kills
        /// the process silently: no window, no console (this is a GUI subsystem build),
        /// no clue at all. That is the worst failure mode on an unfamiliar machine, so the
        /// error is reported twice - a message box that cannot be missed and a log file
        /// next to the exe.
        /// </summary>
        internal static void ReportFatal(Exception ex)
        {
            string text = ex.GetType().Name + ": " + ex.Message;
            string where = " startup";
            try
            {
                if (ex.StackTrace != null)
                {
                    string[] lines = ex.StackTrace.Split('\n');
                    if (lines.Length > 0) where = lines[0].Trim();
                }
            }
            catch { }

            string log = "";
            try
            {
                log = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Application.ExecutablePath), "island-error.log");
                System.IO.File.AppendAllText(log,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n" + where + "\r\n\r\n");
            }
            catch { }

            try
            {
                Native.MessageBox(IntPtr.Zero,
                    "Dynamic Island could not start.\r\n\r\n" + text + "\r\n\r\n" + where
                    + (log.Length > 0 ? "\r\n\r\nFull details: " + log : ""),
                    "Dynamic Island - error", 0x10 /* MB_ICONERROR */);
            }
            catch { }
        }

        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception ex) { ReportFatal(ex); return 70; }
        }

        private static int Run(string[] args)
        {
            // DPI awareness must be set before any GDI+ surface is created
            Native.EnablePerMonitorDpi();
            if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
            if (args.Length > 0 && args[0] == "--privacy") return PrivacyTest();
            if (args.Length > 0 && args[0] == "--watch") return WatchTest();
            if (args.Length > 0 && args[0] == "--apps")
            {
                Console.WriteLine("== visible application windows ==");
                AppLaunchWatcher.DescribeAll();
                Console.WriteLine("APPS DONE");
                return 0;
            }
            if (args.Length > 0 && args[0] == "--probe-media") return MediaProbe.Run();
            if (args.Length > 0 && args[0] == "--fullscreen")
            {
                Console.WriteLine("== fullscreen test ==");
                Console.WriteLine(Island.DescribeFullscreen());
                Console.WriteLine("FULLSCREEN DONE");
                return 0;
            }
            if (args.Length > 0 && args[0] == "--charge")
            {
                Console.WriteLine("power: " + ChargeWatcher.Describe());
                Console.WriteLine("CHARGE DONE");
                return 0;
            }
            if (args.Length > 1 && args[0] == "--snapshot") return Snapshot(args[1]);

            // Any other argument is an activity command. If an island is already
            // running, forward the line over the named pipe and exit; otherwise start
            // the island and apply the command once it is up.
            string command = args.Length > 0 ? string.Join(" ", args) : null;
            if (!string.IsNullOrEmpty(command))
            {
                if (ActivityPipe.TrySend(command, 700))
                {
                    Console.WriteLine("sent: " + command);
                    return 0;
                }
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "DynamicIsland_SingleInstance_Mutex", out created))
            {
                if (!created)
                {
                    // an instance exists but the pipe did not answer
                    if (!string.IsNullOrEmpty(command)) ActivityPipe.TrySend(command, 1500);
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                IslandWindow window = new IslandWindow();
                if (!string.IsNullOrEmpty(command)) window.PendingCommand = command;
                Application.Run(window);
            }
            return 0;
        }

        /// <summary>
        /// Diagnostic: runs the language / radio / window watchers and prints what they
        /// see every few seconds. Used to tell "the watcher never fired" apart from
        /// "the island did not draw it".
        /// </summary>
        private static int WatchTest()
        {
            Console.WriteLine("== watchers ==");

            Console.WriteLine("layout now: " + LanguageWatcher.CurrentLabel()
                + "   (switch with Alt+Shift or Win+Space and watch this line)");

            LanguageWatcher lang = new LanguageWatcher();
            lang.Changed += delegate(string s) { Console.WriteLine("  [LANG] -> " + s); };

            RadioWatcher radio = new RadioWatcher();
            radio.Changed += delegate(string what, bool on)
            { Console.WriteLine("  [RADIO] " + what + " -> " + (on ? "ON" : "OFF")); };

            AppLaunchWatcher apps = new AppLaunchWatcher();
            apps.Launched += delegate(string n, Bitmap b)
            { Console.WriteLine("  [APP] " + n + "  icon=" + (b != null)); };

            DownloadWatcher dl = new DownloadWatcher();
            dl.Progress += delegate(string n, double fr, bool done, long got, long total)
            { Console.WriteLine("  [DL] " + n + " frac=" + fr + " done=" + done); };

            lang.Start();
            radio.Start();
            apps.Start();
            dl.Start();

            Console.WriteLine("  bluetooth state: " + (RadioWatcher.BluetoothOn() ? "ON" : "OFF"));
            Console.WriteLine("  wi-fi state    : " + (RadioWatcher.WifiOn() ? "ON" : "OFF"));

            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(1000);
                if (i % 3 == 2)
                    Console.WriteLine("  ... layout=" + LanguageWatcher.CurrentLabel()
                        + " bt=" + (RadioWatcher.BluetoothOn() ? "ON" : "OFF")
                        + " wifi=" + (RadioWatcher.WifiOn() ? "ON" : "OFF"));
            }
            lang.Dispose(); radio.Dispose(); apps.Dispose(); dl.Dispose();
            Console.WriteLine("WATCH DONE");
            return 0;
        }

        /// <summary>Reports camera/microphone capture state, and optionally watches it.</summary>
        private static int PrivacyTest()
        {
            Console.WriteLine("== camera / microphone capture state ==");
            int ticks = 0;
            while (true)
            {
                PrivacyState p = PrivacyMonitor.Read();
                string cam = p.CameraActive ? Describe(p.Camera) : "-";
                string mic = p.MicActive ? Describe(p.Mic) : "-";
                Console.WriteLine(string.Format("[{0:HH:mm:ss}] camera: {1,-28} mic: {2}", DateTime.Now, cam, mic));
                foreach (CaptureUser u in p.Camera) Console.WriteLine("      cam user: " + u.Name + "  " + u.Detail);
                foreach (CaptureUser u in p.Mic) Console.WriteLine("      mic user: " + u.Name + "  " + u.Detail);
                ticks++;
                if (ticks >= 40) break;                 // ~20 seconds when watching
                System.Threading.Thread.Sleep(500);
            }
            Console.WriteLine("PRIVACY TEST DONE");
            return 0;
        }

        private static string Describe(System.Collections.Generic.List<CaptureUser> list)
        {
            string s = "";
            foreach (CaptureUser u in list) { if (s.Length > 0) s += ", "; s += u.Name; }
            return s;
        }

        /// <summary>Exercises the fragile COM/P-Invoke paths and reports, then exits.</summary>
        private static int SelfTest()
        {
            Console.WriteLine("== Dynamic Island self-test ==");
            // Only things that are genuinely broken count as failures. A missing audio
            // endpoint, no battery or no DDC/CI brightness is a normal configuration
            // (servers, VMs, some desktops) - those are reported as notes, not errors,
            // otherwise the test fails on machines where the app works fine.
            int failures = 0;
            int notes = 0;

            float v = Audio.GetVolume();
            Console.WriteLine("volume        : " + (v < 0 ? "UNAVAILABLE (no audio endpoint - volume HUD will be idle)" : Math.Round(v * 100) + "%"));
            if (v < 0) notes++;

            bool m = Audio.GetMute();
            Console.WriteLine("mute state    : " + m);

            Brightness.Probe();
            Console.WriteLine("brightness    : " + (Brightness.Available
                ? Brightness.Get() + " (range " + Brightness.Min + "-" + Brightness.Max + ")"
                : "UNAVAILABLE (no DDC/CI - shows n/a)"));

            Native.SYSTEM_POWER_STATUS ps;
            if (Native.GetSystemPowerStatus(out ps))
            {
                // 255 is the "unknown / no battery" sentinel on a desktop
                Console.WriteLine("battery       : " + (ps.BatteryLifePercent > 100
                    ? "none present (desktop)"
                    : ps.BatteryLifePercent + "%  ac=" + ps.ACLineStatus));
            }
            else { Console.WriteLine("battery       : UNAVAILABLE"); notes++; }

            int qs;
            Console.WriteLine("notif state   : " + (Native.SHQueryUserNotificationState(out qs) == 0 ? qs.ToString() : "n/a"));

            int dpiX, dpiY;
            // a real display surface is mandatory: without it nothing can be drawn
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                { dpiX = (int)g.DpiX; dpiY = (int)g.DpiY; }
                Console.WriteLine("screen dpi    : " + dpiX + "x" + dpiY);
                Console.WriteLine("primary screen: " + Screen.PrimaryScreen.Bounds);
                Console.WriteLine("work area     : " + Screen.PrimaryScreen.WorkingArea);
                if (Screen.PrimaryScreen.Bounds.Width <= 0) { Console.WriteLine("ERROR: no usable display"); failures++; }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: no usable display - " + ex.Message);
                failures++;
            }

            IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
            Native.RECT r;
            if (tb != IntPtr.Zero && Native.GetWindowRect(tb, out r))
                Console.WriteLine("taskbar rect  : L" + r.Left + " T" + r.Top + " R" + r.Right + " B" + r.Bottom);
            else Console.WriteLine("taskbar rect  : not found (explorer not running?)");

            // one round of layout math, to be sure the geometry does not collapse
            Island probe = new Island(null);
            for (int i = 0; i < 240; i++) probe.Tick(1.0 / 60.0);
            Console.WriteLine("collapsed size: " + Math.Round(probe.Width) + " x " + Math.Round(probe.Height) + " dp");
            if (probe.Width <= 0 || probe.Height <= 0)
            {
                Console.WriteLine("ERROR: layout collapsed to zero");
                failures++;
            }

            if (notes > 0)
                Console.WriteLine("notes         : " + notes + " optional feature(s) unavailable on this machine");

            Console.WriteLine(failures == 0 ? "SELFTEST PASS" : "SELFTEST FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Renders the three island states to PNGs on a checkerboard so the visual design
        /// can be inspected without a human watching the screen.
        /// </summary>
        /// <summary>Snapshot helper: shows the given activity, with the clock advanced.</summary>
        private static void PoseActivity(Island island, ActivityManager mgr, TimeSpan advance)
        {
            Activity a = mgr.Current();
            if (a == null) return;
            if (advance > TimeSpan.Zero)
            {
                a.Elapsed = advance;
                if (a.Kind == ActivityKind.Stopwatch) a.Running = true;
            }
            island.PoseActivityOnly(a);
        }

        private static int Snapshot(string folder)
        {
            try
            {
                if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);
                double scale = 2.0;
                string[] names = { "idle-black", "idle-dot", "expanded", "volume-hud",
                                   "mic-dot", "mini-player", "mini-player-art", "mini-plus-mic",
                                   "act-stopwatch", "act-timer", "act-alarm", "act-notify", "act-device",
                                   "act-timer-running", "act-timer-done", "act-alarm-ring", "act-locked",
                                   "act-device-bt", "act-device-gamepad", "act-alarm-mic",
                                   "act-lang-rus", "act-lang-eng", "act-radio-bt-on", "act-radio-wifi-off",
                                   "act-download", "act-download-unknown", "act-download-done", "act-app-open",
                                   "act-charge", "act-charge-badge", "act-charge-mic", "act-charge-device-mic",
                                   "band-charge", "band-mic", "band-charge-lang", "band-wifi",
                                   "band-download", "band-three", "flat-charge-mic", "flat-lang",
                                   "flat-idle" };

                for (int mode = 0; mode < 41; mode++)
                {
                    Island island = new Island(null);
                    island.ApplySettings(new Settings());     // default 62 px resting width
                    island.SetSystemState(0.62f, false, 78, 84, false);
                    if (mode == 0) island.PoseGeometry(62, 37);                   // plain black pill
                    if (mode == 1) island.PoseGeometry(16, 37);                   // dot
                    if (mode == 2) island.Pose(true, false, 1.7);                 // expanded panel
                    if (mode == 3) island.Pose(false, true, 1.7);                 // volume hud
                    if (mode == 4) { island.PosePrivacy(false, true, "Discord"); island.PoseGeometry(127, 37); }
                    if (mode == 5) { island.PoseMedia("Midnight City", "M83"); island.PoseGeometry(300, 37); }
                    if (mode == 7)
                    {
                        island.PoseMedia("Midnight City", "M83");
                        island.PosePrivacy(false, true, "Discord");
                        island.PoseGeometry(300, 37);
                    }
                    if (mode >= 8)
                    {
                        // live activities, posed through the real manager
                        ActivityManager mgr = new ActivityManager();
                        island.AttachActivities(mgr);
                        if (mode == 8) { mgr.StartStopwatch(); mgr.Update(0); PoseActivity(island, mgr, new TimeSpan(0, 4, 12)); }
                        if (mode == 9) { mgr.StartTimer(TimeSpan.FromMinutes(25), "Focus"); PoseActivity(island, mgr, new TimeSpan(0, 6, 30)); }
                        if (mode == 10) { mgr.RingAlarm(new TimeSpan(7, 30, 0), "Alarm 07:30"); mgr.Update(0); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 11) { mgr.Notify("Build finished", "3 tests failed", 9); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 12) { mgr.NotifyDevice("WH-1000XM4", "Sony  82%", 82, false, 9); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 13) { mgr.StartTimer(TimeSpan.FromMinutes(25), "Focus"); PoseActivity(island, mgr, new TimeSpan(0, 6, 30)); }
                        if (mode == 14)
                        {
                            mgr.StartTimer(TimeSpan.FromMinutes(1), "Focus");
                            PoseActivity(island, mgr, TimeSpan.FromMinutes(1));
                            Activity t = mgr.Current(); if (t != null) { t.Finished = true; t.Running = false; }
                            PoseActivity(island, mgr, TimeSpan.FromMinutes(1));
                        }
                        if (mode == 15) { mgr.RingAlarm(new TimeSpan(7, 30, 0), "Alarm 07:30"); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 16) { island.PoseLocked(true); island.PoseGeometry(127, 37); }
                        if (mode == 17)
                        {
                            mgr.NotifyDevice("Bluetooth", "Jabra Elite 85h  64%", 64, false, 9);
                            PoseActivity(island, mgr, TimeSpan.Zero);
                        }
                        if (mode == 20) { mgr.ShowLanguage("rus", 30); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 21) { mgr.ShowLanguage("eng", 30); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 22) { mgr.ShowRadio("bluetooth", true, 30); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 23) { mgr.ShowRadio("wi-fi", false, 30); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 24) { mgr.DownloadProgress("setup_installer_x64.exe", 0.42, 4200000, 10000000); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 25) { mgr.DownloadProgress("ubuntu-24.04.iso", -1, 900000, -1); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 26) { mgr.DownloadDone("setup_installer_x64.exe", 30); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 27) { mgr.ShowAppLaunch("Photoshop", null, 30); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 28) { mgr.ShowCharging(85); PoseActivity(island, mgr, TimeSpan.Zero); island.PoseChargeOnly(true, "85%"); }
                        if (mode == 29)
                        {
                            mgr.ShowCharging(64);
                            mgr.NotifyDevice("Jabra Elite 85h", "Jabra", 64, false, 30);
                            PoseActivity(island, mgr, TimeSpan.Zero);
                            island.PoseChargeOnly(true, "64%");
                        }
                        if (mode == 30)
                        {
                            mgr.ShowCharging(41);
                            island.PosePrivacy(false, true, "Discord");
                            PoseActivity(island, mgr, TimeSpan.Zero);
                            island.PoseChargeOnly(true, "41%");
                        }
                        if (mode == 31)
                        {
                            mgr.ShowCharging(72);
                            island.PosePrivacy(false, true, "Discord");
                            island.PoseChargeOnly(true, "72%");
                            mgr.NotifyDevice("Jabra Elite 85h", "Jabra", 72, false, 30);
                            PoseActivity(island, mgr, TimeSpan.Zero);
                        }
                        if (mode == 32) { island.PoseChargeOnly(true, ""); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 33) { island.PosePrivacy(false, true, "Discord"); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 34) { mgr.ShowLanguage("rus", 30); island.PoseChargeOnly(true, ""); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 35) { mgr.ShowRadio("wi-fi", false, 30); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 36) { mgr.DownloadProgress("ubuntu-24.04.iso", -1, 900000, -1); PoseActivity(island, mgr, TimeSpan.Zero); }
                        if (mode == 37)
                        {
                            mgr.ShowCharging(70);
                            island.PoseChargeOnly(true, "");
                            island.PosePrivacy(false, true, "Discord");
                            mgr.NotifyDevice("Jabra Elite 85h", "Jabra", 70, false, 30);
                            PoseActivity(island, mgr, TimeSpan.Zero);
                        }
                        if (mode == 38)
                        {
                            island.PoseFullscreen(true);
                            island.PoseChargeOnly(true, "");
                            island.PosePrivacy(false, true, "Discord");
                            mgr.NotifyDevice("Jabra Elite 85h", "Jabra", 70, false, 30);
                            PoseActivity(island, mgr, TimeSpan.Zero);
                            island.PoseFlat();
                        }
                        if (mode == 39)
                        {
                            island.PoseFullscreen(true);
                            mgr.ShowLanguage("eng", 30);
                            PoseActivity(island, mgr, TimeSpan.Zero);
                            island.PoseFlat();
                        }
                        if (mode == 40)
                        {
                            island.PoseFullscreen(true);
                            island.PosePrivacy(false, true, "Discord");
                            PoseActivity(island, mgr, TimeSpan.Zero);
                            island.PoseFlat();
                        }
                        if (mode == 19)
                        {
                            mgr.RingAlarm(new TimeSpan(7, 30, 0), "Alarm 07:30");
                            island.PosePrivacy(false, true, "Discord");
                            PoseActivity(island, mgr, TimeSpan.Zero);
                        }
                        if (mode == 18)
                        {
                            mgr.NotifyDevice("Xbox Wireless Controller", "Microsoft", -1, false, 9);
                            PoseActivity(island, mgr, TimeSpan.Zero);
                        }
                    }
                    if (mode == 6)
                    {
                        // An unmistakable test image, written as PNG bytes and then read
                        // back through the same decode path a WinRT thumbnail uses.
                        string art = System.IO.Path.Combine(folder, "_art_source.png");
                        using (Bitmap probe = new Bitmap(256, 256))
                        {
                            using (Graphics pg = Graphics.FromImage(probe))
                            {
                                pg.SmoothingMode = SmoothingMode.AntiAlias;
                                pg.Clear(Color.FromArgb(255, 20, 140, 90));
                                using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 250, 230, 40)))
                                    pg.FillEllipse(br, 28, 28, 200, 200);
                                using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 200, 20, 60)))
                                    pg.FillRectangle(br, 96, 96, 64, 64);
                            }
                            probe.Save(art, ImageFormat.Png);
                        }
                        island.PoseMedia("Midnight City", "M83", art);
                        island.PoseGeometry(300, 37);
                    }

                    int w = (int)Math.Round(Island.CanvasW * scale);
                    int h = (int)Math.Round(Island.CanvasH * scale);
                    using (Bitmap canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(canvas))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                            // checkerboard so the alpha silhouette is visible
                            int cell = 16;
                            for (int y = 0; y < h; y += cell)
                                for (int x = 0; x < w; x += cell)
                                {
                                    bool alt = ((x / cell) + (y / cell)) % 2 == 0;
                                    using (SolidBrush b = new SolidBrush(alt
                                        ? Color.FromArgb(255, 44, 46, 54) : Color.FromArgb(255, 34, 36, 43)))
                                        g.FillRectangle(b, x, y, cell, cell);
                                }

                            // draw the island through an alpha-preserving layer
                            using (Bitmap layer = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                            {
                                using (Graphics lg = Graphics.FromImage(layer))
                                {
                                    lg.SmoothingMode = SmoothingMode.AntiAlias;
                                    lg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                                    try { island.Draw(lg, scale); }
                                    catch (Exception dex)
                                    {
                                        Console.WriteLine("  draw error in " + names[mode] + ": "
                                            + dex.GetType().Name + ": " + dex.Message);
                                        Console.WriteLine("  state: w=" + island.Width + " h=" + island.Height);
                                        throw;
                                    }
                                }
                                g.DrawImageUnscaled(layer, 0, 0);
                            }
                        }
                        string path = System.IO.Path.Combine(folder, names[mode] + ".png");
                        canvas.Save(path, ImageFormat.Png);
                        Console.WriteLine("wrote " + path + "  (" + island.Width + " x " + island.Height + " dp)");
                    }
                }
                Console.WriteLine("SNAPSHOT OK");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("SNAPSHOT FAIL: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
    }
}
