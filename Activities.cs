// ============================================================================
//  Activities - live things the island can display: stopwatch, timer, alarm.
//
//  All state changes happen on the UI thread via Update(); companion apps push
//  new activities over the named pipe (see ActivityPipe) or by running the exe
//  with a --sw / --timer / --alarm argument.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Globalization;

namespace DynamicIsland
{
    internal enum ActivityKind { Stopwatch = 0, Timer = 1, Alarm = 2, Notification = 3, Device = 4,
                                  Language = 5, Download = 6, AppLaunch = 7, Radio = 8 }

    internal sealed class Activity
    {
        public ActivityKind Kind;
        public string Label = "";
        public string Detail = "";        // device id, notification body, ...
        public System.Drawing.Bitmap Icon;   // optional app icon (owned by the activity)
        public double Progress01 = -1;       // downloads: 0..1, or -1 when unknown
        public long GotBytes;
        public long TotalBytes = -1;

        // ---- timing ----
        public bool Running;
        public TimeSpan Elapsed;          // stopwatch: counted up
        public TimeSpan Duration;         // timer/alarm: total
        public DateTime LastTick = DateTime.Now;

        // ---- lifecycle ----
        public bool Finished;             // timer reached zero / alarm rang
        public bool Acknowledged;         // dismissed by the user
        public DateTime CreatedAt = DateTime.Now;
        public DateTime ExpiresAt = DateTime.MaxValue;

        /// <summary>Remaining time for a countdown; zero once finished.</summary>
        public TimeSpan Remaining
        {
            get
            {
                TimeSpan left = Duration - Elapsed;
                return left < TimeSpan.Zero ? TimeSpan.Zero : left;
            }
        }

        /// <summary>0..1 fill used by the progress ring.</summary>
        public double Progress
        {
            get
            {
                if (Kind == ActivityKind.Stopwatch)
                {
                    // a stopwatch has no natural total: show a full sweep every minute
                    double secs = Elapsed.TotalSeconds % 60.0;
                    return secs / 60.0;
                }
                if (Duration.TotalSeconds <= 0) return 0;
                double f = Elapsed.TotalSeconds / Duration.TotalSeconds;
                return f < 0 ? 0 : (f > 1 ? 1 : f);
            }
        }
    }

    internal static class Clock
    {
        /// <summary>mm:ss under an hour, otherwise h:mm:ss.</summary>
        public static string Format(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            int totalHours = (int)t.TotalHours;
            if (totalHours > 0)
                return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", totalHours, t.Minutes, t.Seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        /// <summary>Always h:mm:ss - used by the stopwatch so the width stays stable.</summary>
        public static string FormatLong(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}",
                (int)t.TotalHours, t.Minutes, t.Seconds);
        }
    }

    internal sealed class ActivityManager
    {
        private readonly List<Activity> items = new List<Activity>();
        private readonly object gate = new object();

        /// <summary>
        /// Optional status sink. The running island writes a small text file here so
        /// external tools can confirm what the island is currently showing.
        /// </summary>
        public static string StatusPath;

        private void WriteStatus()
        {
            if (string.IsNullOrEmpty(StatusPath)) return;
            try
            {
                Activity a = Current();
                StringBuilder sb = new StringBuilder();
                if (a == null)
                {
                    sb.AppendLine("activity=none");
                }
                else
                {
                    sb.AppendLine("activity=" + a.Kind);
                    sb.AppendLine("label=" + a.Label);
                    sb.AppendLine("running=" + (a.Running ? "1" : "0"));
                    sb.AppendLine("finished=" + (a.Finished ? "1" : "0"));
                    sb.AppendLine("remaining=" + Clock.FormatLong(a.Remaining));
                    sb.AppendLine("elapsed=" + Clock.FormatLong(a.Elapsed));
                }
                File.WriteAllText(StatusPath, sb.ToString());
            }
            catch { }
        }

        /// <summary>Raised (on the pushing thread) whenever something changed.</summary>
        public event Action Changed;

        private void Notify()
        {
            WriteStatus();
            Action h = Changed;
            if (h != null) { try { h(); } catch { } }
        }

        // ------------------------------------------------------------ stopwatch
        public Activity Stopwatch { get { lock (gate) return Find(ActivityKind.Stopwatch); } }

        public void StartStopwatch()
        {
            lock (gate)
            {
                Activity a = Find(ActivityKind.Stopwatch);
                if (a == null)
                {
                    a = new Activity();
                    a.Kind = ActivityKind.Stopwatch;
                    a.Label = "Stopwatch";
                    a.Elapsed = TimeSpan.Zero;
                    items.Add(a);
                }
                a.Running = true;
                a.LastTick = DateTime.Now;
                a.Acknowledged = false;
            }
            Notify();
        }

        public void ToggleStopwatch()
        {
            lock (gate)
            {
                Activity a = Find(ActivityKind.Stopwatch);
                if (a == null) { StartStopwatch(); return; }
                a.Running = !a.Running;
                a.LastTick = DateTime.Now;
            }
            Notify();
        }

        public void ResetStopwatch()
        {
            lock (gate)
            {
                Activity a = Find(ActivityKind.Stopwatch);
                if (a == null) return;
                a.Elapsed = TimeSpan.Zero;
                a.Running = false;
                a.LastTick = DateTime.Now;
            }
            Notify();
        }

        public void StopStopwatch()
        {
            lock (gate) Remove(ActivityKind.Stopwatch);
            Notify();
        }

        // ---------------------------------------------------------------- timer
        public Activity Timer { get { lock (gate) return Find(ActivityKind.Timer); } }

        public void StartTimer(TimeSpan duration, string label)
        {
            if (duration <= TimeSpan.Zero) duration = TimeSpan.FromMinutes(1);
            lock (gate)
            {
                Remove(ActivityKind.Timer);
                Activity a = new Activity();
                a.Kind = ActivityKind.Timer;
                a.Label = label == null || label.Length == 0 ? "Timer" : label;
                a.Duration = duration;
                a.Elapsed = TimeSpan.Zero;
                a.Running = true;
                a.LastTick = DateTime.Now;
                items.Add(a);
            }
            Notify();
        }

        public void ToggleTimer()
        {
            lock (gate)
            {
                Activity a = Find(ActivityKind.Timer);
                if (a == null) return;
                if (a.Finished) { Remove(ActivityKind.Timer); }
                else { a.Running = !a.Running; a.LastTick = DateTime.Now; }
            }
            Notify();
        }

        public void ResetTimer()
        {
            lock (gate)
            {
                Activity a = Find(ActivityKind.Timer);
                if (a == null) return;
                a.Elapsed = TimeSpan.Zero;
                a.Finished = false;
                a.Running = false;
                a.LastTick = DateTime.Now;
            }
            Notify();
        }

        /// <summary>Adds time to the running timer, or snoozes a ringing one.</summary>
        public void ExtendTimer(TimeSpan extra)
        {
            lock (gate)
            {
                Activity a = Find(ActivityKind.Timer);
                if (a == null) return;
                if (a.Finished)
                {
                    a.Finished = false;
                    a.Duration = extra;
                    a.Elapsed = TimeSpan.Zero;
                    a.Running = true;
                    a.Label = "Snoozed";
                }
                else
                {
                    a.Duration = a.Duration + extra;
                }
                a.LastTick = DateTime.Now;
            }
            Notify();
        }

        public void DismissTimer()
        {
            lock (gate) Remove(ActivityKind.Timer);
            Notify();
        }

        // ---------------------------------------------------------------- alarm
        public Activity Alarm { get { lock (gate) return Find(ActivityKind.Alarm); } }

        public void RingAlarm(TimeSpan at, string label)
        {
            lock (gate)
            {
                Remove(ActivityKind.Alarm);
                Activity a = new Activity();
                a.Kind = ActivityKind.Alarm;
                a.Label = label == null || label.Length == 0 ? "Alarm" : label;
                a.Duration = at;
                a.Elapsed = at;
                a.Finished = true;
                a.Running = false;
                items.Add(a);
            }
            Notify();
        }

        /// <summary>Alarm: "delay 10 minutes" - re-arms the alarm ten minutes out.</summary>
        public void SnoozeAlarm(TimeSpan extra)
        {
            lock (gate)
            {
                Activity a = Find(ActivityKind.Alarm);
                if (a == null) return;
                a.Finished = false;
                a.Duration = extra;
                a.Elapsed = TimeSpan.Zero;
                a.Running = true;
                a.Label = "Snoozed";
                a.LastTick = DateTime.Now;
            }
            Notify();
        }

        public void DismissAlarm()
        {
            lock (gate) Remove(ActivityKind.Alarm);
            Notify();
        }

        // ------------------------------------------------- language / radio / apps
        /// <summary>Keyboard layout switched: shows the short code for a couple of seconds.</summary>
        public void ShowLanguage(string label, int seconds)
        {
            lock (gate)
            {
                Remove(ActivityKind.Language);
                Activity a = new Activity();
                a.Kind = ActivityKind.Language;
                a.Label = label;
                a.ExpiresAt = DateTime.Now.AddSeconds(seconds <= 0 ? 2.5 : seconds);
                items.Add(a);
            }
            Notify();
        }

        /// <summary>Bluetooth or Wi-Fi switched on/off.</summary>
        public void ShowRadio(string what, bool on, int seconds)
        {
            lock (gate)
            {
                Remove(ActivityKind.Radio);
                Activity a = new Activity();
                a.Kind = ActivityKind.Radio;
                a.Label = what;
                a.Running = on;
                a.Finished = !on;                      // off is drawn with a strike
                a.ExpiresAt = DateTime.Now.AddSeconds(seconds <= 0 ? 3 : seconds);
                items.Add(a);
            }
            Notify();
        }

        /// <summary>A download in progress. fraction &lt; 0 means "total unknown".</summary>
        public void DownloadProgress(string name, double fraction, long got, long total)
        {
            lock (gate)
            {
                Activity a = Find(ActivityKind.Download);
                if (a == null || a.Finished)
                {
                    if (a != null) Remove(ActivityKind.Download);
                    a = new Activity();
                    a.Kind = ActivityKind.Download;
                    a.CreatedAt = DateTime.Now;
                    items.Add(a);
                }
                a.Label = name;
                a.Progress01 = fraction;
                a.GotBytes = got;
                a.TotalBytes = total;
                a.Finished = false;
                a.ExpiresAt = DateTime.MaxValue;
            }
            Notify();
        }

        /// <summary>The download finished: the island unfurls and shows a green tick.</summary>
        public void DownloadDone(string name, int seconds)
        {
            lock (gate)
            {
                Remove(ActivityKind.Download);
                Activity a = new Activity();
                a.Kind = ActivityKind.Download;
                a.Label = name;
                a.Finished = true;
                a.Progress01 = 1.0;
                a.ExpiresAt = DateTime.Now.AddSeconds(seconds <= 0 ? 6 : seconds);
                items.Add(a);
            }
            Notify();
        }

        /// <summary>An application window just appeared: big icon, nothing else.</summary>
        public void ShowAppLaunch(string name, System.Drawing.Bitmap icon, int seconds)
        {
            lock (gate)
            {
                Activity old = Find(ActivityKind.AppLaunch);
                if (old != null)
                {
                    if (old.Icon != null && !ReferenceEquals(old.Icon, icon)) { try { old.Icon.Dispose(); } catch { } }
                    Remove(ActivityKind.AppLaunch);
                }
                Activity a = new Activity();
                a.Kind = ActivityKind.AppLaunch;
                a.Label = name;
                a.Icon = icon;
                a.ExpiresAt = DateTime.Now.AddSeconds(seconds <= 0 ? 3 : seconds);
                items.Add(a);
            }
            Notify();
        }

        // --------------------------------------------------------- notification
        public void Notify(string title, string body, int seconds)
        {
            lock (gate)
            {
                Activity a = new Activity();
                a.Kind = ActivityKind.Notification;
                a.Label = title == null || title.Length == 0 ? "Notification" : title;
                a.Detail = body == null ? "" : body;
                a.ExpiresAt = DateTime.Now.AddSeconds(seconds <= 0 ? 8 : seconds);
                items.Add(a);
                // keep the list from growing without bound
                while (CountKind(ActivityKind.Notification) > 4) RemoveFirst(ActivityKind.Notification);
            }
            Notify();
        }

        /// <summary>Pushes a device-connection notice (USB / Bluetooth).</summary>
        public void NotifyDevice(string title, string detail, int batteryPercent, bool charging, int seconds)
        {
            lock (gate)
            {
                Activity a = new Activity();
                a.Kind = ActivityKind.Device;
                a.Label = title == null ? "Device" : title;
                a.Detail = detail == null ? "" : detail;
                a.Duration = TimeSpan.FromSeconds(batteryPercent);
                a.Running = charging;
                a.ExpiresAt = DateTime.Now.AddSeconds(seconds <= 0 ? 9 : seconds);
                items.Add(a);
                while (CountKind(ActivityKind.Device) > 2) RemoveFirst(ActivityKind.Device);
            }
            Notify();
        }

        public void Dismiss(ActivityKind kind)
        {
            lock (gate) Remove(kind);
            Notify();
        }

        public void DismissAll()
        {
            lock (gate) items.Clear();
            Notify();
        }

        /// <summary>
        /// Picks what the island should show. Alarms outrank everything, then a
        /// ringing timer, then a running stopwatch, then device notices and finally
        /// plain notifications.
        /// </summary>
        public Activity Current()
        {
            lock (gate)
            {
                // an armed alarm stays invisible until it actually rings
                Activity a = Find(ActivityKind.Alarm);
                if (a != null && a.Finished) return a;

                a = Find(ActivityKind.Timer);
                if (a != null && (a.Running || a.Finished)) return a;

                a = Find(ActivityKind.Stopwatch);
                if (a != null && (a.Running || a.Elapsed > TimeSpan.Zero)) return a;

                // a freshly opened application outranks the passive notices
                a = FindNewest(ActivityKind.AppLaunch);
                if (a != null) return a;

                a = Find(ActivityKind.Download);
                if (a != null) return a;

                a = FindNewest(ActivityKind.Device);
                if (a != null) return a;

                a = FindNewest(ActivityKind.Radio);
                if (a != null) return a;

                a = FindNewest(ActivityKind.Language);
                if (a != null) return a;

                a = FindNewest(ActivityKind.Notification);
                if (a != null) return a;

                return null;
            }
        }

        public bool Any
        {
            get { lock (gate) return items.Count > 0; }
        }

        /// <summary>Advances all timers by the given delta. Call from the UI thread.</summary>
        public void Update(double dt)
        {
            bool changed = false;
            lock (gate)
            {
                DateTime now = DateTime.Now;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    Activity a = items[i];

                    if (a.ExpiresAt != DateTime.MaxValue && now >= a.ExpiresAt)
                    {
                        items.RemoveAt(i);
                        changed = true;
                        continue;
                    }

                    if (!a.Running) { a.LastTick = now; continue; }

                    TimeSpan step = now - a.LastTick;
                    a.LastTick = now;
                    if (step < TimeSpan.Zero) step = TimeSpan.Zero;
                    if (step > TimeSpan.FromSeconds(5)) step = TimeSpan.FromSeconds(5);  // ignore sleep

                    if (a.Kind == ActivityKind.Stopwatch)
                    {
                        a.Elapsed = a.Elapsed + step;
                        changed = true;
                    }
                    else if (a.Kind == ActivityKind.Timer || a.Kind == ActivityKind.Alarm)
                    {
                        a.Elapsed = a.Elapsed + step;
                        if (a.Elapsed >= a.Duration)
                        {
                            a.Elapsed = a.Duration;
                            a.Finished = true;
                            a.Running = false;
                        }
                        changed = true;
                    }
                }
            }
            if (changed) Notify();
        }

        // ------------------------------------------------------------- internals
        private Activity Find(ActivityKind kind)
        {
            foreach (Activity a in items) if (a.Kind == kind) return a;
            return null;
        }

        private Activity FindNewest(ActivityKind kind)
        {
            Activity best = null;
            foreach (Activity a in items)
                if (a.Kind == kind && (best == null || a.CreatedAt > best.CreatedAt)) best = a;
            return best;
        }

        private int CountKind(ActivityKind kind)
        {
            int n = 0;
            foreach (Activity a in items) if (a.Kind == kind) n++;
            return n;
        }

        private void Remove(ActivityKind kind)
        {
            for (int i = items.Count - 1; i >= 0; i--) if (items[i].Kind == kind) items.RemoveAt(i);
        }

        private void RemoveFirst(ActivityKind kind)
        {
            for (int i = 0; i < items.Count; i++) if (items[i].Kind == kind) { items.RemoveAt(i); return; }
        }
    }
}
