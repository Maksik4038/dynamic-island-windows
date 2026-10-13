// ============================================================================
//  MediaMonitor - "what track is playing right now?"
//
//  Primary source: the WinRT System Media Transport Controls session manager -
//  the same source the Windows media flyout uses. This needs the WinRT
//  projection, which became available once the Windows SDK was installed:
//      /reference:"...\Windows Kits\10\UnionMetadata\<ver>\Windows.winmd"
//      /reference:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll"
//      /reference:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll"
//
//  Everything WinRT lives inside SmtcSource and nowhere else, so that a machine
//  without those references still builds/runs the rest of the widget.
//
//  Fallback: window-title guessing. Disabled by default - it produced false
//  positives (a browser tab reported as playing while silent).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland
{
    internal sealed class MediaState
    {
        public bool Available;        // a media source responded at all
        public bool Playing;          // a track is actively playing
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string App = "";
        public string Source = "";
        public string Error = "";
        public long PositionMs = -1;
        public long DurationMs = -1;
        public Bitmap Artwork;        // owned by the caller; dispose when replacing

        public bool HasTrack { get { return Playing && Title.Length > 0; } }

        public void Clear()
        {
            Playing = false;
            Title = ""; Artist = ""; Album = "";
            PositionMs = -1; DurationMs = -1;
        }
    }

    internal interface IMediaSource
    {
        string Name { get; }
        bool TryRead(MediaState into);
    }

    // ============================================================ SMTC (WinRT)
    internal sealed class SmtcSource : IMediaSource
    {
        private GlobalSystemMediaTransportControlsSessionManager manager;
        private bool initTried;
        private bool initOk;
        private string initError = "";

        // Artwork is decoded at most once per track; re-decoding every poll would
        // waste CPU and allocate needlessly.
        private string cachedArtKey = "";
        private Bitmap cachedArt;

        public string Name { get { return "smtc"; } }

        public string InitError { get { return initError; } }

        // ------------------------------------------------- playback commands
        /// <summary>
        /// Fires a WinRT async command without blocking the UI: the call itself is
        /// started on the caller's thread, the result is observed on a pool thread.
        /// </summary>
        private void RunAsync(Func<Windows.Foundation.IAsyncOperation<bool>> op)
        {
            try
            {
                Windows.Foundation.IAsyncOperation<bool> o = op();
                if (o == null) return;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { o.AsTask().Wait(3000); } catch { }
                });
            }
            catch { }
        }

        /// <summary>Skips to the next track. No-op when nothing is playing.</summary>
        public void SkipNext()
        {
            if (!Initialize()) return;
            try
            {
                GlobalSystemMediaTransportControlsSession s = manager.GetCurrentSession();
                if (s == null) return;
                RunAsync(delegate { return s.TrySkipNextAsync(); });
            }
            catch { }
        }

        /// <summary>Skips to the previous track.</summary>
        public void SkipPrevious()
        {
            if (!Initialize()) return;
            try
            {
                GlobalSystemMediaTransportControlsSession s = manager.GetCurrentSession();
                if (s == null) return;
                RunAsync(delegate { return s.TrySkipPreviousAsync(); });
            }
            catch { }
        }

        /// <summary>Moves the playhead. Returns false when the player refuses.</summary>
        public bool Seek(long positionMs)
        {
            if (!Initialize()) return false;
            try
            {
                GlobalSystemMediaTransportControlsSession s = manager.GetCurrentSession();
                if (s == null) return false;
                long ticks = (positionMs < 0 ? 0 : positionMs) * TimeSpan.TicksPerMillisecond;
                RunAsync(delegate { return s.TryChangePlaybackPositionAsync(ticks); });
                return true;
            }
            catch { return false; }
        }

        public bool Initialize()
        {
            if (initTried) return initOk;
            initTried = true;
            try
            {
                manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                    .AsTask().Result;
                initOk = manager != null;
                if (!initOk) initError = "manager was null";
            }
            catch (Exception ex)
            {
                initOk = false;
                initError = ex.GetType().Name + ": " + ex.Message;
            }
            return initOk;
        }

        public bool TryRead(MediaState into)
        {
            if (!Initialize()) return false;
            try
            {
                into.Available = true;
                into.Source = Name;

                GlobalSystemMediaTransportControlsSession session = manager.GetCurrentSession();
                if (session == null)
                {
                    into.Clear();
                    return true;
                }

                try { into.App = session.SourceAppUserModelId; } catch { into.App = ""; }

                // playback status: 4 == Playing
                int status = -1;
                try
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
                    if (info != null) status = (int)info.PlaybackStatus;
                }
                catch { }

                // timeline gives position/duration for a progress display
                try
                {
                    GlobalSystemMediaTransportControlsSessionTimelineProperties tl = session.GetTimelineProperties();
                    if (tl != null)
                    {
                        into.PositionMs = (long)tl.Position.TotalMilliseconds;
                        into.DurationMs = (long)tl.EndTime.TotalMilliseconds;
                    }
                }
                catch { }

                GlobalSystemMediaTransportControlsSessionMediaProperties props = null;
                try
                {
                    props = session.TryGetMediaPropertiesAsync().AsTask().Result;
                }
                catch { }

                if (props != null)
                {
                    try { into.Title = props.Title ?? ""; } catch { }
                    try { into.Artist = props.Artist ?? ""; } catch { }
                    try { into.Album = props.AlbumTitle ?? ""; } catch { }
                    if (into.Artist.Length == 0)
                    {
                        try { into.Artist = props.AlbumArtist ?? ""; } catch { }
                    }
                    into.Artwork = ArtworkFor(props, into.Title, into.Artist);
                }

                into.Playing = status == 4;
                if (!into.Playing) into.Clear();
                if (into.Playing && into.Title.Length == 0) into.Playing = false;
                return true;
            }
            catch (Exception ex)
            {
                into.Error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------- artwork
        /// <summary>
        /// Returns the artwork for the given track key, decoding it only when the key
        /// changes. The returned Bitmap is shared: callers must not dispose it.
        /// </summary>
        private Bitmap ArtworkFor(GlobalSystemMediaTransportControlsSessionMediaProperties props, string title, string artist)
        {
            string key = (title ?? "") + "\u0001" + (artist ?? "");
            if (key == cachedArtKey) return cachedArt;

            Bitmap decoded = ReadArtwork(props);
            if (decoded == null) return null;      // MediaMonitor keeps the previous one
            cachedArtKey = key;
            cachedArt = decoded;
            return cachedArt;
        }

        [ComImport, Guid("905A0FEF-BC53-11DF-8C49-001E4FC686DA"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IBufferByteAccess
        {
            int Buffer(out IntPtr value);
        }

        /// <summary>
        /// Pulls the thumbnail stream and decodes it to a Bitmap. Returns null when
        /// the session has no artwork, which is normal for streams and radio.
        /// </summary>
        private static Bitmap ReadArtwork(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            try
            {
                IRandomAccessStreamReference reference = props.Thumbnail;
                if (reference == null) return null;

                using (IRandomAccessStreamWithContentType stream = reference.OpenReadAsync().AsTask().Result)
                {
                    if (stream == null) return null;
                    uint size = (uint)stream.Size;
                    if (size == 0 || size > 12 * 1024 * 1024) return null;   // sanity bound

                    byte[] bytes = new byte[size];
                    using (IInputStream input = stream.GetInputStreamAt(0))
                    {
                        IBuffer buffer = new Windows.Storage.Streams.Buffer(size);
                        buffer = input.ReadAsync(buffer, size, InputStreamOptions.None).AsTask().Result;
                        if (buffer.Length == 0) return null;

                        IntPtr raw;
                        if (((IBufferByteAccess)buffer).Buffer(out raw) != 0 || raw == IntPtr.Zero) return null;
                        Marshal.Copy(raw, bytes, 0, (int)buffer.Length);
                    }

                    using (MemoryStream ms = new MemoryStream(bytes))
                    using (Bitmap decoded = new Bitmap(ms))
                    {
                        // GDI+ keeps the stream alive lazily; clone so the stream can close
                        return new Bitmap(decoded);
                    }
                }
            }
            catch { return null; }
        }
    }

    // ================================================== window-title fallback
    /// <summary>
    /// Last-resort detection by inspecting window titles. Kept for machines where
    /// the WinRT projection is unavailable, but OFF by default: it cannot tell
    /// playing from paused and flags any media-shaped title.
    /// </summary>
    internal sealed class WindowTitleSource : IMediaSource
    {
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        private delegate bool EnumProc(IntPtr h, IntPtr param);

        private static readonly string[] Players = {
            "spotify", "groove", "musicbee", "foobar2000", "aimp", "winamp", "vlc",
            "wmplayer", "itunes", "deezer", "tidal", "audacity", "mpc-hc", "potplayer"
        };
        private static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };

        private string lastTitle = "", lastApp = "";
        private int stableTicks;

        public string Name { get { return "window"; } }

        public bool TryRead(MediaState into)
        {
            string app = "", title = "";
            try { Find(out app, out title); } catch { }

            into.Source = Name;
            into.App = app;
            into.Available = app.Length > 0;

            bool plausible = title.Length > 0 && IsPlausible(title, app);
            if (plausible && title == lastTitle && app == lastApp) stableTicks++;
            else stableTicks = 0;

            if (plausible && stableTicks >= 1)
            {
                into.Playing = true;
                into.Title = CleanTitle(title, app);
            }
            else
            {
                into.Playing = false;
                into.Title = "";
            }
            lastTitle = title;
            lastApp = app;
            return true;
        }

        private static bool IsPlausible(string title, string app)
        {
            if (title.Length < 3) return false;
            string lower = title.ToLowerInvariant();
            if (lower.IndexOf(" - ", StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf("now playing", StringComparison.Ordinal) >= 0) return true;
            foreach (string p in Players)
                if (app.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string CleanTitle(string title, string app)
        {
            string t = title;
            foreach (string suffix in new string[] { " - YouTube", " | Spotify", " - Spotify", " - VLC media player" })
                if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    t = t.Substring(0, t.Length - suffix.Length);
            return t.Trim();
        }

        private void Find(out string app, out string title)
        {
            app = ""; title = "";
            string bestTitle = "", bestApp = "";
            long bestScore = -1;
            try
            {
                EnumWindows(delegate(IntPtr h, IntPtr p)
                {
                    // never let an exception cross the native callback boundary
                    try
                    {
                        if (!IsWindowVisible(h)) return true;
                        int len = GetWindowTextLengthW(h);
                        if (len < 4) return true;
                        uint pid;
                        GetWindowThreadProcessId(h, out pid);
                        string proc = PrivacyMonitor.ProcessName(pid);
                        if (proc.Length == 0) return true;

                        bool isPlayer = false;
                        foreach (string s in Players) if (proc.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) { isPlayer = true; break; }
                        bool isBrowser = false;
                        if (!isPlayer) foreach (string s in Browsers) if (proc.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) { isBrowser = true; break; }
                        if (!isPlayer && !isBrowser) return true;

                        StringBuilder sb = new StringBuilder(len + 2);
                        GetWindowTextW(h, sb, sb.Capacity);
                        string t = sb.ToString();
                        bool shaped = t.IndexOf(" - ", StringComparison.Ordinal) >= 0;
                        long score = (shaped ? 1000 : 0) + (isPlayer ? 500 : 0) + t.Length;
                        if (score > bestScore) { bestScore = score; bestTitle = t; bestApp = proc; }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            app = bestApp;
            title = bestTitle;
        }
    }

    // ================================================================ monitor
    internal sealed class MediaMonitor
    {
        private readonly List<IMediaSource> sources = new List<IMediaSource>();
        private IMediaSource active;

        public MediaState State = new MediaState();
        public string Diagnostics = "";

        /// <param name="allowSmtc">use the real WinRT now-playing API</param>
        /// <param name="allowWindowFallback">use window-title guessing (off by default)</param>
        public MediaMonitor(bool allowSmtc, bool allowWindowFallback)
        {
            if (allowSmtc) sources.Add(new SmtcSource());
            if (allowWindowFallback) sources.Add(new WindowTitleSource());
        }

        public bool UsingSmtc { get { return active != null && active.Name == "smtc"; } }

        public void Poll()
        {
            if (active != null)
            {
                MediaState s = new MediaState();
                if (active.TryRead(s)) { Adopt(s); return; }
                active = null;
            }

            foreach (IMediaSource src in sources)
            {
                MediaState s = new MediaState();
                bool ok = false;
                try { ok = src.TryRead(s); }
                catch (Exception ex) { Diagnostics = src.Name + ": " + ex.GetType().Name; continue; }

                if (ok && s.Available)
                {
                    active = src;
                    Adopt(s);
                    return;
                }
                if (!ok) Diagnostics = src.Name + ": unavailable";
            }
        }

        /// <summary>Keeps the previous artwork when the new read has none.</summary>
        private void Adopt(MediaState s)
        {
            if (s.Artwork == null && State.Artwork != null && s.Playing && s.Title == State.Title)
                s.Artwork = State.Artwork;
            else if (s.Artwork != null && State.Artwork != null && !ReferenceEquals(s.Artwork, State.Artwork))
            {
                try { State.Artwork.Dispose(); } catch { }
            }
            State = s;
        }

        // ---- playback commands, forwarded to whichever source is active ----

        public void SkipNext()
        {
            SmtcSource smtc = active as SmtcSource;
            if (smtc == null) return;
            smtc.SkipNext();
        }

        public void SkipPrevious()
        {
            SmtcSource smtc = active as SmtcSource;
            if (smtc == null) return;
            smtc.SkipPrevious();
        }

        public bool Seek(long positionMs)
        {
            SmtcSource smtc = active as SmtcSource;
            if (smtc == null) return false;
            return smtc.Seek(positionMs);
        }
    }

    // ======================================================= capability probe
    /// <summary>
    /// Verifies the WinRT path in a CHILD process (--probe-media), so that a
    /// missing facade or a broken projection can never take the widget down.
    /// Prints "smtc=ok" or "smtc=fail".
    /// </summary>
    internal static class MediaProbe
    {
        public static int Run()
        {
            bool ok = false;
            try
            {
                SmtcSource src = new SmtcSource();
                if (src.Initialize())
                {
                    MediaState st = new MediaState();
                    ok = src.TryRead(st);       // working manager is enough, even with no session
                }
                Console.WriteLine(ok ? "smtc=ok" : ("smtc=fail " + src.InitError));
            }
            catch (Exception ex)
            {
                Console.WriteLine("smtc=fail " + ex.GetType().Name);
            }
            Console.Out.Flush();
            return 0;
        }
    }
}
