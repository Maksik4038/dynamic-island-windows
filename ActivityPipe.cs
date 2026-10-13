// ============================================================================
//  ActivityPipe - lets any process drive the island's live activities.
//
//  A named pipe ("DynamicIsland.Activity") accepts one text command per line.
//  Two ways in:
//     1. a companion app / script connects and writes a line
//     2. run the exe again with an argument, e.g.
//            DynamicIsland.exe stopwatch start
//            DynamicIsland.exe timer 10m
//            DynamicIsland.exe alarm 07:30
//            DynamicIsland.exe notify "Build done" "3 tests failed"
//        The second instance forwards the line to the running island and exits.
//
//  Grammar (case-insensitive, extra spaces ignored):
//     stopwatch start | stop | pause | toggle | reset
//     timer <duration>        e.g. 10m, 90s, 1h30m, 5
//     timer pause | resume | reset | +10m | dismiss
//     alarm <HH:mm>           e.g. 07:30
//     alarm snooze | dismiss
//     notify "<title>" ["<body>"]
//     clear
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace DynamicIsland
{
    internal static class ActivityGrammar
    {
        /// <summary>Parses "90", "90s", "10m", "1h30m", "00:90"? -> a duration.</summary>
        public static bool TryDuration(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim().ToLowerInvariant();
            if (s.Length == 0) return false;

            // hh:mm[:ss]
            if (s.IndexOf(':') > 0)
            {
                string[] parts = s.Split(':');
                if (parts.Length < 2 || parts.Length > 3) return false;
                int h, m, sec = 0;
                if (!int.TryParse(parts[0], out h)) return false;
                if (!int.TryParse(parts[1], out m)) return false;
                if (parts.Length == 3 && !int.TryParse(parts[2], out sec)) return false;
                value = new TimeSpan(h, m, sec);
                return value > TimeSpan.Zero;
            }

            // sequences like 1h30m or 45s or 10
            double total = 0;
            int i = 0;
            bool any = false;
            while (i < s.Length)
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i == start) return false;
                double n;
                if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out n)) return false;
                char unit = i < s.Length ? s[i] : 's';
                if (char.IsLetter(unit)) i++;
                switch (unit)
                {
                    case 'h': total += n * 3600; break;
                    case 'm': total += n * 60; break;
                    case 's': total += n; break;
                    default: return false;
                }
                any = true;
            }
            if (!any || total <= 0) return false;
            value = TimeSpan.FromSeconds(total);
            return true;
        }

        /// <summary>Parses "7:30" or "07:30:15" into a time of day.</summary>
        public static bool TryTimeOfDay(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Trim().Split(':');
            if (parts.Length < 2 || parts.Length > 3) return false;
            int h, m, s = 0;
            if (!int.TryParse(parts[0], out h)) return false;
            if (!int.TryParse(parts[1], out m)) return false;
            if (parts.Length == 3 && !int.TryParse(parts[2], out s)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59 || s < 0 || s > 59) return false;
            value = new TimeSpan(h, m, s);
            return true;
        }

        /// <summary>Remaining time until the next occurrence of a time of day.</summary>
        public static TimeSpan UntilNext(TimeSpan timeOfDay)
        {
            DateTime now = DateTime.Now;
            DateTime target = now.Date.Add(timeOfDay);
            if (target <= now) target = target.AddDays(1);
            return target - now;
        }
    }

    internal sealed class ActivityPipe : IDisposable
    {
        public const string PipeName = "DynamicIsland.Activity";

        private readonly ActivityManager activities;
        private readonly Action<string> sink;        // called on the UI thread with a raw line
        private Thread thread;
        private volatile bool running;

        public ActivityPipe(ActivityManager manager, Action<string> uiSink)
        {
            activities = manager;
            sink = uiSink;
        }

        public void Start()
        {
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "ActivityPipe";
            thread.Start();
        }

        private void Loop()
        {
            while (running)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                        4, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();

                    string line;
                    using (StreamReader reader = new StreamReader(server, Encoding.UTF8, false, 1024, true))
                        line = reader.ReadLine();

                    if (!string.IsNullOrEmpty(line))
                    {
                        // hand off to the UI thread; reply immediately
                        Action<string> h = sink;
                        if (h != null) { try { h(line.Trim()); } catch { } }
                        try
                        {
                            byte[] ok = Encoding.UTF8.GetBytes("ok\n");
                            server.Write(ok, 0, ok.Length);
                            server.Flush();
                        }
                        catch { }
                    }
                }
                catch { Thread.Sleep(200); }
                finally
                {
                    try { if (server != null) server.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Sends one command to a running island. Returns false when nothing is
        /// listening, so the caller can fall back to starting normally.
        /// </summary>
        public static bool TrySend(string command, int timeoutMs)
        {
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    client.Connect(timeoutMs);
                    byte[] data = Encoding.UTF8.GetBytes(command + "\n");
                    client.Write(data, 0, data.Length);
                    client.Flush();
                    // read the acknowledgement so the server finishes the exchange
                    client.ReadTimeout = timeoutMs;
                    byte[] buf = new byte[16];
                    try { client.Read(buf, 0, buf.Length); } catch { }
                    return true;
                }
            }
            catch { return false; }
        }

        public void Dispose()
        {
            running = false;
            try { if (thread != null) thread.Join(300); } catch { }
        }
    }
}
