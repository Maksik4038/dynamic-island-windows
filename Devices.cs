// ============================================================================
//  DeviceWatcher - notices USB and Bluetooth devices being connected.
//
//  WMI's Win32_DeviceChangeEvent fires on arrival/removal; on arrival the new
//  device is looked up in Win32_PnPEntity and reported with its class. Battery
//  level is included when the device exposes one (a paired BLE device usually
//  reports it through the property store rather than WMI, so it is best-effort).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;

namespace DynamicIsland
{
    internal sealed class DeviceEvent
    {
        public string Name = "";
        public string Kind = "";          // "USB", "Bluetooth", ...
        public string Detail = "";        // device instance id / manufacturer
        public int Battery = -1;          // -1 when unknown
        public bool Charging;
        public bool Removed;
    }

    internal sealed class DeviceWatcher : IDisposable
    {
        /// <summary>Raised on a background thread; marshal before touching the UI.</summary>
        public event Action<DeviceEvent> Arrived;

        private ManagementEventWatcher watcher;
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> reported = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        private DateTime startedAt = DateTime.Now;

        /// <summary>PnP classes worth announcing. Others are internal churn.</summary>
        private static readonly string[] Interesting = {
            "USB", "Bluetooth", "HIDClass", "Ports", "DiskDrive", "CDROM", "Camera",
            "Media", "MEDIA", "WPD", "Net", "AudioEndpoint", "SmartCardReader", "Image"
        };

        public void Start()
        {
            try
            {
                // seed with what is already attached so only NEW arrivals are announced
                Snapshot();

                WqlEventQuery q = new WqlEventQuery(
                    "SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3");
                watcher = new ManagementEventWatcher(q);
                watcher.EventArrived += OnWmiEvent;
                watcher.Start();
            }
            catch { }
        }

        private void OnWmiEvent(object sender, EventArrivedEventArgs e)
        {
            try
            {
                uint type = 0;
                try { type = Convert.ToUInt32(e.NewEvent["EventType"]); } catch { }
                // 2 = device arrival, 3 = device removal
                Check(type == 3);
            }
            catch { }
        }

        private void Snapshot()
        {
            try
            {
                ManagementObjectSearcher q = new ManagementObjectSearcher(
                    "SELECT DeviceID FROM Win32_PnPEntity");
                foreach (ManagementObject o in q.Get())
                {
                    string id = Convert.ToString(o["DeviceID"]);
                    if (!string.IsNullOrEmpty(id)) seen.Add(id);
                }
            }
            catch { }
        }

        private void Check(bool removal)
        {
            try
            {
                ManagementObjectSearcher q = new ManagementObjectSearcher(
                    "SELECT Name, PNPClass, DeviceID, Manufacturer, Service FROM Win32_PnPEntity");
                DateTime now = DateTime.Now;

                List<string> current = new List<string>();
                List<DeviceEvent> found = new List<DeviceEvent>();

                foreach (ManagementObject o in q.Get())
                {
                    string id = Convert.ToString(o["DeviceID"]);
                    if (string.IsNullOrEmpty(id)) continue;
                    current.Add(id);

                    bool isNew;
                    lock (gate) isNew = !seen.Contains(id);

                    if (!isNew || removal) continue;

                    string cls = Convert.ToString(o["PNPClass"]);
                    if (!IsInteresting(cls)) continue;

                    // do not announce the same device twice in a burst
                    lock (gate)
                    {
                        DateTime last;
                        if (reported.TryGetValue(id, out last) && (now - last).TotalSeconds < 20) continue;
                        reported[id] = now;
                    }

                    DeviceEvent d = new DeviceEvent();
                    d.Name = Convert.ToString(o["Name"]);
                    if (string.IsNullOrEmpty(d.Name)) d.Name = FriendlyFromId(id);
                    d.Kind = KindLabel(cls, Convert.ToString(o["Service"]));
                    d.Detail = Convert.ToString(o["Manufacturer"]);
                    d.Removed = removal;
                    ReadBattery(id, d);
                    found.Add(d);
                }

                lock (gate)
                {
                    if (removal)
                    {
                        // removal: forget ids that vanished
                        List<string> gone = new List<string>();
                        foreach (string id in seen) if (!current.Contains(id)) gone.Add(id);
                        foreach (string id in gone) seen.Remove(id);
                    }
                    else
                    {
                        foreach (string id in current) seen.Add(id);
                    }
                }

                foreach (DeviceEvent d in found)
                {
                    Action<DeviceEvent> h = Arrived;
                    if (h != null) { try { h(d); } catch { } }
                }
            }
            catch { }
        }

        private static bool IsInteresting(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            foreach (string c in Interesting)
                if (string.Equals(c, cls, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string KindLabel(string cls, string service)
        {
            if (string.Equals(cls, "Bluetooth", StringComparison.OrdinalIgnoreCase))
                return service != null && service.IndexOf("BTHLE", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Bluetooth LE" : "Bluetooth";
            if (string.Equals(cls, "USB", StringComparison.OrdinalIgnoreCase)) return "USB";
            if (string.Equals(cls, "DiskDrive", StringComparison.OrdinalIgnoreCase)) return "Drive";
            if (string.Equals(cls, "WPD", StringComparison.OrdinalIgnoreCase)) return "Phone / player";
            if (string.Equals(cls, "Camera", StringComparison.OrdinalIgnoreCase)) return "Camera";
            if (string.Equals(cls, "HIDClass", StringComparison.OrdinalIgnoreCase)) return "Input device";
            if (string.Equals(cls, "Net", StringComparison.OrdinalIgnoreCase)) return "Network";
            return string.IsNullOrEmpty(cls) ? "Device" : cls;
        }

        /// <summary>"USB\VID_1234&PID_5678\..." -> "USB device 1234:5678" as a fallback name.</summary>
        private static string FriendlyFromId(string id)
        {
            try
            {
                int v = id.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
                int p = id.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
                if (v >= 0 && p >= 0 && v + 8 <= id.Length && p + 8 <= id.Length)
                    return "USB " + id.Substring(v + 4, 4) + ":" + id.Substring(p + 4, 4);
            }
            catch { }
            return "Device";
        }

        /// <summary>
        /// Best-effort battery read. A device that reports through WMI (Win32_Battery)
        /// is matched by device id; otherwise the PnP property store is consulted.
        /// </summary>
        private static void ReadBattery(string deviceId, DeviceEvent d)
        {
            try
            {
                ManagementObjectSearcher q = new ManagementObjectSearcher(
                    "SELECT Name, EstimatedChargeRemaining, BatteryStatus, DeviceID FROM Win32_Battery");
                foreach (ManagementObject o in q.Get())
                {
                    string bname = Convert.ToString(o["Name"]);
                    string bid = Convert.ToString(o["DeviceID"]);
                    // WMI battery ids rarely match the PnP id, so fall back to a name match
                    bool match = (!string.IsNullOrEmpty(bid) && bid.IndexOf(deviceId, StringComparison.OrdinalIgnoreCase) >= 0)
                              || (!string.IsNullOrEmpty(bname) && d.Name.Length > 3 &&
                                  bname.IndexOf(d.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!match) continue;
                    object rem = o["EstimatedChargeRemaining"];
                    if (rem != null) d.Battery = Convert.ToInt32(rem);
                    object st = o["BatteryStatus"];
                    if (st != null) d.Charging = Convert.ToInt32(st) == 2;   // 2 = AC connected
                    return;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                if (watcher != null)
                {
                    watcher.EventArrived -= OnWmiEvent;
                    watcher.Stop();
                    watcher.Dispose();
                    watcher = null;
                }
            }
            catch { }
        }
    }
}
