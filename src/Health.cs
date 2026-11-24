// Disk Sweep - drive health (SSD and HDD).
// Basic status works for everyone. Wear, temperature, power-on hours, error counts and
// HDD SMART attributes come from Windows' storage reliability counters and need admin rights.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text;

namespace DiskSweep
{
    enum HealthLevel { Good, Warning, Bad, Unknown }

    class DiskHealth
    {
        public string Number;            // disk number, e.g. "0"
        public string Model;
        public string Kind;              // "SSD", "HDD" or "Drive"
        public string Bus;               // "NVMe", "SATA", "USB", ...
        public long Size;
        public List<string> Letters = new List<string>();
        public string WindowsStatus;     // Healthy / Warning / Unhealthy / Unknown
        public HealthLevel Level = HealthLevel.Unknown;
        public List<string> Problems = new List<string>();

        // admin-only details (null = not available)
        public int? WearPercent;         // SSD life used
        public int? Temperature, TemperatureMax;
        public long? PowerOnHours;
        public long? ReadErrors, WriteErrors;
        public long? StartStops;
        public bool? PredictFailure;     // SMART "failure predicted" (SATA)
        public long? Reallocated, Pending, Uncorrectable;   // HDD SMART 5 / 197 / 198
        public bool DetailsAvailable;
    }

    static class Health
    {
        static string Norm(string s) { return new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()); }

        static string BusName(int b)
        {
            switch (b)
            {
                case 17: return "NVMe";
                case 11: return "SATA";
                case 7: return "USB";
                case 10: return "SAS";
                case 8: return "RAID";
                case 3: return "ATA";
                case 1: return "SCSI";
                case 12: return "SD card";
                case 15: return "Virtual";
                default: return "Other";
            }
        }

        static long? L(object o) { try { return o == null ? (long?)null : Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { return null; } }

        public static List<DiskHealth> Read()
        {
            var disks = new List<DiskHealth>();
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            try
            {
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                    "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk")))
                    foreach (ManagementObject o in s.Get())
                    {
                        int media = (int)(L(o["MediaType"]) ?? 0), bus = (int)(L(o["BusType"]) ?? 0), hs = (int)(L(o["HealthStatus"]) ?? 5);
                        var d = new DiskHealth
                        {
                            Number = Convert.ToString(o["DeviceId"]),
                            Model = (Convert.ToString(o["FriendlyName"]) ?? "Disk").Trim(),
                            Kind = media == 4 ? "SSD" : media == 3 ? "HDD" : "Drive",
                            Bus = BusName(bus),
                            Size = L(o["Size"]) ?? 0,
                            WindowsStatus = hs == 0 ? "Healthy" : hs == 1 ? "Warning" : hs == 2 ? "Unhealthy" : "Unknown"
                        };
                        disks.Add(d);
                    }
            }
            catch { return disks; }

            // Drive letters per disk
            try
            {
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
                    foreach (ManagementObject o in s.Get())
                    {
                        var letter = o["DriveLetter"];
                        if (letter == null) continue;
                        char c = letter is char ? (char)letter : Convert.ToChar(Convert.ToInt32(letter));
                        if (!char.IsLetter(c)) continue;
                        var d = disks.FirstOrDefault(x => x.Number == Convert.ToString(o["DiskNumber"]));
                        if (d != null) d.Letters.Add(char.ToUpperInvariant(c) + ":");
                    }
            }
            catch { }

            // Reliability counters (admin)
            try
            {
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_StorageReliabilityCounter")))
                    foreach (ManagementObject o in s.Get())
                    {
                        var d = disks.FirstOrDefault(x => x.Number == Convert.ToString(o["DeviceId"]));
                        if (d == null) continue;
                        d.DetailsAvailable = true;
                        long? wear = L(o["Wear"]);
                        if (wear.HasValue && d.Kind != "HDD") d.WearPercent = (int)wear.Value;
                        long? t = L(o["Temperature"]), tm = L(o["TemperatureMax"]);
                        if (t.HasValue && t.Value > 0 && t.Value < 150) d.Temperature = (int)t.Value;
                        if (tm.HasValue && tm.Value > 0 && tm.Value < 150) d.TemperatureMax = (int)tm.Value;
                        long? poh = L(o["PowerOnHours"]);
                        if (poh.HasValue && poh.Value > 0) d.PowerOnHours = poh;
                        d.ReadErrors = L(o["ReadErrorsUncorrected"]);
                        d.WriteErrors = L(o["WriteErrorsUncorrected"]);
                        long? ss = L(o["StartStopCycleCount"]);
                        if (ss.HasValue && ss.Value > 0) d.StartStops = ss;
                    }
            }
            catch { }

            // SATA SMART (admin): failure prediction + key HDD attributes
            try
            {
                var wmi = new ManagementScope(@"\\.\root\wmi");
                var predict = new Dictionary<string, bool>();
                using (var s = new ManagementObjectSearcher(wmi, new ObjectQuery("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus")))
                    foreach (ManagementObject o in s.Get()) predict[Convert.ToString(o["InstanceName"])] = Convert.ToBoolean(o["PredictFailure"]);
                var smart = new Dictionary<string, byte[]>();
                using (var s = new ManagementObjectSearcher(wmi, new ObjectQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData")))
                    foreach (ManagementObject o in s.Get()) smart[Convert.ToString(o["InstanceName"])] = o["VendorSpecific"] as byte[];

                foreach (var d in disks)
                {
                    // Match "WDC WD10EZRX-00L4HB0" to "SCSI\Disk&Ven_WDC&Prod_WD10EZRX-00L4HB0\..."
                    string key = Norm(d.Model.Split(' ').OrderByDescending(x => x.Length).First());
                    if (key.Length < 5) continue;
                    foreach (var kv in predict) if (Norm(kv.Key).Contains(key)) { d.PredictFailure = kv.Value; d.DetailsAvailable = true; }
                    foreach (var kv in smart)
                        if (Norm(kv.Key).Contains(key) && kv.Value != null) { ParseSmart(d, kv.Value); d.DetailsAvailable = true; }
                }
            }
            catch { }

            foreach (var d in disks) Judge(d);
            return disks.OrderBy(d => d.Letters.Count == 0 ? "~" : d.Letters.Min()).ToList();
        }

        // ATA SMART table: 2-byte version, then 30 entries of 12 bytes: id, flags(2), value, worst, raw(6), reserved.
        static void ParseSmart(DiskHealth d, byte[] v)
        {
            for (int i = 2; i + 12 <= v.Length && i < 2 + 30 * 12; i += 12)
            {
                int id = v[i];
                if (id == 0) continue;
                long raw = 0;
                for (int b = 5; b >= 0; b--) raw = (raw << 8) | v[i + 5 + b];
                switch (id)
                {
                    case 5: d.Reallocated = raw; break;                               // reallocated sectors
                    case 197: d.Pending = raw; break;                                 // sectors waiting to be remapped
                    case 198: d.Uncorrectable = raw; break;                           // sectors that couldn't be read
                    case 9: if (!d.PowerOnHours.HasValue) d.PowerOnHours = raw & 0xFFFFFFFF; break;
                    case 194: if (!d.Temperature.HasValue && (raw & 0xFF) > 0 && (raw & 0xFF) < 150) d.Temperature = (int)(raw & 0xFF); break;
                }
            }
        }

        // Turns the numbers into one verdict plus plain-language reasons.
        static void Judge(DiskHealth d)
        {
            var bad = new List<string>(); var warn = new List<string>();
            if (d.WindowsStatus == "Unhealthy") bad.Add("Windows reports this drive as unhealthy.");
            else if (d.WindowsStatus == "Warning") warn.Add("Windows reports a warning for this drive.");
            if (d.PredictFailure == true) bad.Add("The drive itself predicts it may fail soon (SMART).");
            if (d.WearPercent >= 100) bad.Add("The SSD has used up its rated life.");
            else if (d.WearPercent >= 80) warn.Add("The SSD has used most of its rated life.");
            if (d.Reallocated > 0) (d.Reallocated > 50 ? bad : warn).Add(d.Reallocated + " damaged sectors were replaced.");
            if (d.Pending > 0) warn.Add(d.Pending + " sectors are unreadable and waiting to be replaced.");
            if (d.Uncorrectable > 0) warn.Add(d.Uncorrectable + " sectors could not be read.");
            if ((d.ReadErrors ?? 0) + (d.WriteErrors ?? 0) > 0) warn.Add("The drive reported uncorrected read or write errors.");
            int hot = d.Kind == "HDD" ? 55 : 70;
            if (d.Temperature >= hot) warn.Add("The drive is running hot (" + d.Temperature + " °C).");

            d.Problems = bad.Concat(warn).ToList();
            if (bad.Count > 0) d.Level = HealthLevel.Bad;
            else if (warn.Count > 0) d.Level = HealthLevel.Warning;
            else if (d.WindowsStatus == "Healthy") d.Level = HealthLevel.Good;
            else d.Level = HealthLevel.Unknown;
        }

        public static string LevelText(HealthLevel l)
        {
            return l == HealthLevel.Good ? "Healthy" : l == HealthLevel.Warning ? "Needs attention" : l == HealthLevel.Bad ? "At risk" : "Unknown";
        }

        public static string Hours(long h)
        {
            double years = h / (24.0 * 365);
            string n = h.ToString("N0", CultureInfo.CurrentCulture) + " h";
            return years >= 0.1 ? n + "  (" + years.ToString("0.0", CultureInfo.CurrentCulture) + " years)" : n;
        }

        // Self-test for the SMART decoder and the verdict rules, using made-up data.
        public static bool SelfTest(StringBuilder log)
        {
            var v = new byte[512];
            Action<int, int, long> put = (slot, id, raw) =>
            {
                int i = 2 + slot * 12;
                v[i] = (byte)id;
                for (int b = 0; b < 6; b++) v[i + 5 + b] = (byte)(raw >> (8 * b));
            };
            put(0, 5, 3); put(1, 197, 0); put(2, 9, 12345); put(3, 194, 38); put(4, 198, 0);
            var hdd = new DiskHealth { Kind = "HDD", WindowsStatus = "Healthy" };
            ParseSmart(hdd, v);
            Judge(hdd);
            bool a = hdd.Reallocated == 3 && hdd.Pending == 0 && hdd.PowerOnHours == 12345 && hdd.Temperature == 38
                     && hdd.Level == HealthLevel.Warning && hdd.Problems.Any(p => p.Contains("3 damaged sectors"));
            log.AppendLine("smart decode: realloc=" + hdd.Reallocated + " poh=" + hdd.PowerOnHours + " temp=" + hdd.Temperature
                           + " verdict=" + LevelText(hdd.Level) + " -> " + (a ? "ok" : "FAIL"));

            var ssd = new DiskHealth { Kind = "SSD", WindowsStatus = "Healthy", WearPercent = 12, Temperature = 41 };
            Judge(ssd);
            var worn = new DiskHealth { Kind = "SSD", WindowsStatus = "Healthy", WearPercent = 85 };
            Judge(worn);
            var dying = new DiskHealth { Kind = "HDD", WindowsStatus = "Healthy", PredictFailure = true };
            Judge(dying);
            bool b2 = ssd.Level == HealthLevel.Good && worn.Level == HealthLevel.Warning && dying.Level == HealthLevel.Bad;
            log.AppendLine("verdicts: ssd12%=" + LevelText(ssd.Level) + " ssd85%=" + LevelText(worn.Level) + " predicted=" + LevelText(dying.Level)
                           + " -> " + (b2 ? "ok" : "FAIL"));
            return a && b2;
        }

        // For --health-report
        public static string Report()
        {
            var sb = new StringBuilder();
            foreach (var d in Read())
            {
                sb.AppendLine(d.Model + " | " + d.Bus + " " + d.Kind + " | " + Util.Fmt(d.Size) + " | " + string.Join(",", d.Letters)
                    + " | windows=" + d.WindowsStatus + " | verdict=" + LevelText(d.Level) + " | details=" + d.DetailsAvailable);
                sb.AppendLine("   wear=" + d.WearPercent + " temp=" + d.Temperature + " tmax=" + d.TemperatureMax + " poh=" + d.PowerOnHours
                    + " rerr=" + d.ReadErrors + " werr=" + d.WriteErrors + " predict=" + d.PredictFailure
                    + " realloc=" + d.Reallocated + " pending=" + d.Pending + " uncorr=" + d.Uncorrectable);
                foreach (var p in d.Problems) sb.AppendLine("   ! " + p);
            }
            return sb.ToString();
        }
    }
}
