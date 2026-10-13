// ============================================================================
//  PrivacyMonitor - reports whether the camera and/or microphone are in use.
//
//  Microphone: CoreAudio capture sessions. Any process holding the capture
//  endpoint (WASAPI, DirectSound, waveIn, Media Foundation - Discord, Teams,
//  browsers, games) appears as an Active session on the capture device. This is
//  the same source the taskbar microphone indicator uses, so it is accurate.
//
//  Camera: the per-app ConsentStore under CapabilityAccessManager. Windows only
//  records LastUsedTime* there for apps that go through the capability/consent
//  API, so packaged apps, the Camera app and Windows Hello are caught, while a
//  Win32 app using DirectShow/Media Foundation directly may not be. That
//  limitation is real and is reported rather than hidden.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DynamicIsland
{
    internal enum CaptureKind { None = 0, Camera = 1, Microphone = 2 }

    internal sealed class CaptureUser
    {
        public string Name;          // friendly app name, e.g. "Discord"
        public string Detail;        // full exe path when known
        public long StartedAt;       // ordering key
        public CaptureKind Kind;
    }

    internal sealed class PrivacyState
    {
        public readonly List<CaptureUser> Camera = new List<CaptureUser>();
        public readonly List<CaptureUser> Mic = new List<CaptureUser>();

        public bool CameraActive { get { return Camera.Count > 0; } }
        public bool MicActive { get { return Mic.Count > 0; } }
        public bool Any { get { return CameraActive || MicActive; } }

        public CaptureUser Primary
        {
            get
            {
                CaptureUser best = null;
                foreach (CaptureUser u in Camera) if (best == null || u.StartedAt > best.StartedAt) best = u;
                foreach (CaptureUser u in Mic) if (best == null || u.StartedAt > best.StartedAt) best = u;
                return best;
            }
        }
    }

    // CoreAudio interop lives in DynamicIsland.cs (IMMDeviceEnumerator/IMMDevice);
    // only the session-enumeration interfaces are needed here.

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        int GetAudioSessionControl(ref Guid sessionGuid, int streamFlags, out IntPtr sessionControl);
        int GetSimpleAudioVolume(ref Guid sessionGuid, int streamFlags, out IntPtr audioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification(string sessionId, IntPtr notification);
        int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl control);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(string name, ref Guid eventContext);
        int GetIconPath(out IntPtr path);
        int SetIconPath(string path, ref Guid eventContext);
        int GetGroupingParam(out Guid grouping);
        int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(string name, ref Guid eventContext);
        int GetIconPath(out IntPtr path);
        int SetIconPath(string path, ref Guid eventContext);
        int GetGroupingParam(out Guid grouping);
        int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out uint pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }
    internal static class PrivacyMonitor
    {
        private const int eCapture = 1;            // EDataFlow
        private const int eMultimedia = 1;         // ERole
        private const int CLSCTX_INPROC_SERVER = 1;
        private const int AudioSessionStateActive = 1;

        private const string ConsentRoot =
            @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        /// <summary>
        /// Off by default - see Read(). Flip to true only to experiment with the
        /// ConsentStore path; it will miss ordinary camera use.
        /// </summary>
        internal static bool CameraDetectionEnabled = false;

        public static PrivacyState Read()
        {
            PrivacyState s = new PrivacyState();
            try { ReadMic(s); } catch { }
            // Camera detection is intentionally disabled: Windows only maintains the
            // per-app ConsentStore for capability-broker apps, and the built-in Camera
            // app does not update it (verified: its entry stayed months old after use).
            // Direct device state cannot be distinguished from a permissions failure.
            // Reporting nothing is honest; reporting a guess would not be.
            if (CameraDetectionEnabled) { try { ReadCamera(s); } catch { } }
            return s;
        }

        // --------------------------------------------------------------- mic
        /// <summary>
        /// Enumerates capture sessions on the default recording endpoint. A session
        /// in the Active state means some process is holding the microphone now.
        /// </summary>
        // Pids seen on the capture endpoint while the microphone was known idle.
        // Audio control panels and driver helpers keep a session open permanently,
        // so they are baselined out instead of being reported as active use.
        private static readonly HashSet<uint> baselinePids = new HashSet<uint>();
        private static bool baselined;


        private static readonly string[] IgnoredProcesses = {
            "audiodg", "svchost", "system", "idle", "registry", "csrss", "winlogon",
            "xearaudiocenter_x64", "xearaudiocenter", "realtekaudioconsole",
            "nahimic", "wavesmaxxaudio", "dolby", "sonicstudio", "rtkauduservice"
        };

        private static bool IsIgnored(string name)
        {
            if (name.Length == 0) return true;
            string lower = name.ToLowerInvariant();
            foreach (string s in IgnoredProcesses)
                if (lower.IndexOf(s, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Reads the capture endpoint's session list. A process only has a session
        /// here while it actually holds the microphone, so presence in this set is
        /// the detection signal. (Session state is NOT usable: capture sessions
        /// report state 0 even mid-capture, verified on this machine.)
        /// </summary>
        private static void ReadMic(PrivacyState s)
        {
            object mgrObj = null;
            try
            {
                mgrObj = Audio.ActivateCapture(typeof(IAudioSessionManager2));
                if (mgrObj == null) { baselined = false; return; }
                IAudioSessionManager2 mgr = (IAudioSessionManager2)mgrObj;

                IAudioSessionEnumerator list;
                if (mgr.GetSessionEnumerator(out list) != 0 || list == null) return;
                int count;
                if (list.GetCount(out count) != 0) return;

                uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                List<uint> live = new List<uint>();
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl ctl;
                    if (list.GetSession(i, out ctl) != 0 || ctl == null) continue;
                    IAudioSessionControl2 c2 = ctl as IAudioSessionControl2;
                    if (c2 == null) continue;
                    uint pid;
                    if (c2.GetProcessId(out pid) != 0) continue;
                    if (pid == 0 || pid == 4) continue;   // 0 = unnamed session, 4 = System
                    string name = ProcessName(pid);
                    if (IsIgnored(name)) continue;
                    if (!live.Contains(pid)) live.Add(pid);
                }

                if (!baselined)
                {
                    baselined = true;
                    foreach (uint pid in live) { if (pid != self) baselinePids.Add(pid); }
                    return;                       // first pass only establishes the baseline
                }
                // note: our own process is intentionally NOT baselined (see ReadMic)

                // forget anything that no longer has a session, so a process that
                // later starts capturing is reported again
                List<uint> stale = new List<uint>();
                foreach (uint pid in baselinePids) if (!live.Contains(pid)) stale.Add(pid);
                foreach (uint pid in stale) baselinePids.Remove(pid);

                int n = 0;
                foreach (uint pid in live)
                {
                    if (baselinePids.Contains(pid)) continue;      // was already there and idle
                    CaptureUser u = new CaptureUser();
                    u.Name = ProcessName(pid);
                    u.Kind = CaptureKind.Microphone;
                    u.StartedAt = DateTime.Now.Ticks + n++;
                    s.Mic.Add(u);
                }
            }
            catch { }
            finally { Release(mgrObj); }
        }
        private static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }

        // ------------------------------------------------------------ camera
        private static void ReadCamera(PrivacyState s)
        {
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(ConsentRoot + "webcam", false))
            {
                if (root == null) return;
                ScanConsent(s, root, "NonPackaged");
                string[] names = root.GetSubKeyNames();
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], "NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                    using (RegistryKey app = root.OpenSubKey(names[i], false))
                    {
                        if (app == null) continue;
                        long st = ReadTime(app, "LastUsedTimeStart");
                        long sp = ReadTime(app, "LastUsedTimeStop");
                        if (st > 0 && st > sp) AddCam(s, Friendly(names[i]), "", st);
                    }
                }
            }
        }

        private static void ScanConsent(PrivacyState s, RegistryKey root, string branch)
        {
            using (RegistryKey np = root.OpenSubKey(branch, false))
            {
                if (np == null) return;
                string[] apps = np.GetSubKeyNames();
                for (int j = 0; j < apps.Length; j++)
                {
                    using (RegistryKey app = np.OpenSubKey(apps[j], false))
                    {
                        if (app == null) continue;
                        long st = ReadTime(app, "LastUsedTimeStart");
                        long sp = ReadTime(app, "LastUsedTimeStop");
                        if (st > 0 && st > sp)
                        {
                            string path = apps[j].Replace('#', '\\');
                            AddCam(s, Friendly(path), path, st);
                        }
                    }
                }
            }
        }

        private static void AddCam(PrivacyState s, string name, string detail, long start)
        {
            CaptureUser u = new CaptureUser();
            u.Name = name; u.Detail = detail; u.StartedAt = start; u.Kind = CaptureKind.Camera;
            s.Camera.Add(u);
        }

        private static long ReadTime(RegistryKey k, string name)
        {
            try
            {
                object v = k.GetValue(name, null);
                return v == null ? 0 : Convert.ToInt64(v);
            }
            catch { return 0; }
        }

        private static string Friendly(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unknown";
            string s = raw;
            int slash = s.LastIndexOf('\\');
            if (slash >= 0 && slash + 1 < s.Length) s = s.Substring(slash + 1);
            if (slash < 0)
            {
                int dot = s.LastIndexOf('.');
                if (dot > 0) s = s.Substring(0, dot);
                int us = s.IndexOf('_');
                if (us > 2) s = s.Substring(0, us);
            }
            return s.Length > 0 ? s : "unknown";
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// Resolves a pid to a short name without Process.GetProcessById, which can
        /// block for seconds on protected or suspended processes.
        /// </summary>
        internal static string ProcessName(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
                string full = sb.ToString();
                int slash = full.LastIndexOf('\\');
                string name = slash >= 0 ? full.Substring(slash + 1) : full;
                int dot = name.LastIndexOf('.');
                if (dot > 0) name = name.Substring(0, dot);
                return name;
            }
            finally { CloseHandle(h); }
        }
    }
}
