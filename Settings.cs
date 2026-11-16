// ============================================================================
//  Settings - a tiny key=value file next to the executable.
//  Kept deliberately simple so it can be edited by hand.
// ============================================================================
using System;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace DynamicIsland
{
    internal sealed class Settings
    {
        /// <summary>Collapsed pill width in design pixels (96 dpi). 16 = a fat dot.</summary>
        public int IdleWidth = 62;

        /// <summary>Expanded panel width in design pixels. Up to the full screen width.</summary>
        public int OpenWidth = 371;

        /// <summary>Horizontal offset of the island along the top edge: negative = left, positive = right.</summary>
        public int OffsetX = 0;

        // ---- allowed range for the collapsed pill ----
        public const int MinIdleWidth = 16;      // a round dot
        public const int MaxIdleWidth = 220;

        // ---- allowed range for the expanded panel and the horizontal offset ----
        public const int MinOpenWidth = 300;     // smaller makes the sliders unusable
        public const int MaxOpenWidth = 4000;    // clamped to the screen width at run time
        public const int MaxOffset = 2000;       // clamped to the screen at run time

        public static string FilePath
        {
            get
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                return Path.Combine(dir, "settings.ini");
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) { s.Save(); return s; }
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    if (key == "idlewidth")
                    {
                        int w;
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out w))
                            s.IdleWidth = Clamp(w);
                    }
                    else if (key == "openwidth")
                    {
                        int w;
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out w))
                            s.OpenWidth = ClampOpen(w);
                    }
                    else if (key == "offsetx")
                    {
                        int w;
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out w))
                            s.OffsetX = ClampOffset(w);
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                using (StreamWriter w = new StreamWriter(FilePath, false))
                {
                    w.WriteLine("# Dynamic Island settings");
                    w.WriteLine("# idlewidth = collapsed pill width in pixels (16 = dot, 62 = small, 127 = original)");
                    w.WriteLine("# openwidth = expanded panel width in pixels (up to the full screen width)");
                    w.WriteLine("# offsetx   = horizontal offset along the top edge (negative = left, positive = right)");
                    w.WriteLine("idlewidth=" + IdleWidth.ToString(CultureInfo.InvariantCulture));
                    w.WriteLine("openwidth=" + OpenWidth.ToString(CultureInfo.InvariantCulture));
                    w.WriteLine("offsetx=" + OffsetX.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch { }
        }

        public static int Clamp(int w)
        {
            if (w < MinIdleWidth) return MinIdleWidth;
            if (w > MaxIdleWidth) return MaxIdleWidth;
            return w;
        }

        public static int ClampOpen(int w)
        {
            if (w < MinOpenWidth) return MinOpenWidth;
            if (w > MaxOpenWidth) return MaxOpenWidth;
            return w;
        }

        public static int ClampOffset(int w)
        {
            if (w < -MaxOffset) return -MaxOffset;
            if (w > MaxOffset) return MaxOffset;
            return w;
        }
    }
}
