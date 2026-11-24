// Disk Sweep 1.4 - "What grew?" snapshots, System Restore space, reserved storage, and auto-clean.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DiskSweep
{
    // ================================================================ what grew
    class GrowthRow
    {
        public string Path;
        public long Before, After;
        public long Change { get { return After - Before; } }
    }

    static class Growth
    {
        static string Dir { get { return System.IO.Path.Combine(Settings.Dir, "snapshots"); } }
        const long MinSize = 20L << 20;   // folders under 20 MB aren't stored
        const int MaxDepth = 5;

        static string FileFor(string drive, DateTime when)
        {
            return System.IO.Path.Combine(Dir, drive.Substring(0, 1).ToUpperInvariant() + "_" + when.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".tsv");
        }

        // Saves folder sizes (5 levels deep, 20 MB+) from a space-map tree.
        public static void Save(string drive, MapNode root)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("#total\t" + root.Size + "\t" + Native.FreeBytes(drive));
                Flatten(root, 0, sb);
                File.WriteAllText(FileFor(drive, DateTime.Now), sb.ToString(), Encoding.UTF8);
                // keep the last 60 snapshots per drive
                foreach (var old in List(drive).Skip(60)) File.Delete(old.Item2);
            }
            catch { }
        }

        static void Flatten(MapNode n, int depth, StringBuilder sb)
        {
            if (n.Children == null || depth >= MaxDepth) return;
            foreach (var c in n.Children)
            {
                if (c.Size < MinSize) continue;
                sb.Append(c.IsFiles ? c.Path.TrimEnd('\\') + "\\[files]" : c.Path).Append('\t').Append(c.Size).Append('\n');
                if (!c.IsFiles) Flatten(c, depth + 1, sb);
            }
        }

        // Snapshots for a drive, newest first: (time, file)
        public static List<Tuple<DateTime, string>> List(string drive)
        {
            var res = new List<Tuple<DateTime, string>>();
            try
            {
                if (!Directory.Exists(Dir)) return res;
                string prefix = drive.Substring(0, 1).ToUpperInvariant() + "_";
                foreach (var f in Directory.GetFiles(Dir, prefix + "*.tsv"))
                {
                    DateTime t;
                    if (DateTime.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(f).Substring(2), "yyyyMMdd-HHmm",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                        res.Add(Tuple.Create(t, f));
                }
            }
            catch { }
            return res.OrderByDescending(x => x.Item1).ToList();
        }

        public static DateTime? Latest(string drive)
        {
            var l = List(drive);
            return l.Count == 0 ? (DateTime?)null : l[0].Item1;
        }

        static Dictionary<string, long> Load(string file, out long total)
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            total = 0;
            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
            {
                var c = line.Split('\t');
                if (c.Length < 2) continue;
                long v; if (!long.TryParse(c[1], out v)) continue;
                if (c[0] == "#total") { total = v; continue; }
                d[c[0]] = v;
            }
            return d;
        }

        // Compares the newest snapshot with the newest one that is at least `days` old (or the oldest there is).
        // Returns the folders that grew most, most specific first, plus the totals.
        public static List<GrowthRow> Compare(string drive, int days, out DateTime? since, out long totalChange)
        {
            since = null; totalChange = 0;
            var snaps = List(drive);
            if (snaps.Count < 2) return new List<GrowthRow>();
            var newest = snaps[0];
            var older = snaps.Skip(1).FirstOrDefault(s => s.Item1 <= newest.Item1.AddDays(-days)) ?? snaps.Last();
            since = older.Item1;
            long tNew, tOld;
            var a = Load(newest.Item2, out tNew);
            var b = Load(older.Item2, out tOld);
            totalChange = tNew - tOld;

            var rows = new List<GrowthRow>();
            foreach (var kv in a)
            {
                long before; b.TryGetValue(kv.Key, out before);
                if (kv.Value - before >= (100L << 20)) rows.Add(new GrowthRow { Path = kv.Key, Before = before, After = kv.Value });
            }
            // Keep the most specific folder: drop a parent when one child explains most of its growth.
            var keep = new List<GrowthRow>();
            foreach (var r in rows.OrderByDescending(x => x.Path.Length))
            {
                bool explained = keep.Any(k => k.Path.StartsWith(r.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                                               && k.Change >= r.Change * 0.7);
                if (!explained) keep.Add(r);
            }
            return keep.OrderByDescending(x => x.Change).Take(25).ToList();
        }
    }

    // ================================================================ System Restore and reserved storage (admin)
    class RestoreInfo
    {
        public int Count;
        public long Used, Max;
        public bool Available;      // false without admin rights
    }

    static class SystemSpace
    {
        static string VolumeId(string drive)
        {
            using (var s = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Volume WHERE DriveLetter='" + drive.TrimEnd('\\') + "'"))
                foreach (ManagementObject o in s.Get()) return Convert.ToString(o["DeviceID"]);
            return null;
        }

        public static RestoreInfo Restore(string drive)
        {
            var r = new RestoreInfo();
            try
            {
                string vol = VolumeId(drive);
                if (vol == null) return r;
                using (var s = new ManagementObjectSearcher("SELECT VolumeName FROM Win32_ShadowCopy"))
                    foreach (ManagementObject o in s.Get())
                        if (string.Equals(Convert.ToString(o["VolumeName"]), vol, StringComparison.OrdinalIgnoreCase)) r.Count++;
                using (var s = new ManagementObjectSearcher("SELECT UsedSpace, MaxSpace, Volume FROM Win32_ShadowStorage"))
                    foreach (ManagementObject o in s.Get())
                        if (Convert.ToString(o["Volume"]).IndexOf(vol.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase) >= 0
                            || Convert.ToString(o["Volume"]).IndexOf(vol, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            r.Used += Convert.ToInt64(o["UsedSpace"]);
                            r.Max += Convert.ToInt64(o["MaxSpace"]);
                        }
                r.Available = true;
            }
            catch { }
            return r;
        }

        // Deletes every restore point / shadow copy on the drive except the newest one.
        public static int DeleteOld(string drive)
        {
            int n = 0;
            string vol = VolumeId(drive);
            if (vol == null) return 0;
            var copies = new List<Tuple<DateTime, ManagementObject>>();
            using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_ShadowCopy"))
                foreach (ManagementObject o in s.Get())
                {
                    if (!string.Equals(Convert.ToString(o["VolumeName"]), vol, StringComparison.OrdinalIgnoreCase)) continue;
                    DateTime t = DateTime.MinValue;
                    try { t = ManagementDateTimeConverter.ToDateTime(Convert.ToString(o["InstallDate"])); } catch { }
                    copies.Add(Tuple.Create(t, o));
                }
            foreach (var c in copies.OrderByDescending(x => x.Item1).Skip(1))
            {
                try { c.Item2.Delete(); n++; } catch { }
            }
            return n;
        }

        public static string Run(string exe, string args, out int code)
        {
            var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(); code = p.ExitCode;
                return o;
            }
        }

        // Sets the most space restore points may use, as a percentage of the drive.
        public static bool SetLimit(string drive, int percent)
        {
            int c;
            string d = drive.TrimEnd('\\');
            Run(Path.Combine(Environment.SystemDirectory, "vssadmin.exe"), "resize shadowstorage /for=" + d + " /on=" + d + " /maxsize=" + percent + "%", out c);
            return c == 0;
        }

        // "on", "off" or "unknown". DISM's text is translated, so only English is parsed.
        public static string ReservedState()
        {
            try
            {
                int c;
                string o = Run(Path.Combine(Environment.SystemDirectory, "Dism.exe"), "/Online /Get-ReservedStorageState", out c);
                if (Regex.IsMatch(o, @"Reserved storage is enabled", RegexOptions.IgnoreCase)) return "on";
                if (Regex.IsMatch(o, @"Reserved storage is disabled", RegexOptions.IgnoreCase)) return "off";
            }
            catch { }
            return "unknown";
        }

        // Returns null on success, otherwise DISM's message.
        public static string SetReserved(bool on)
        {
            int c;
            string o = Run(Path.Combine(Environment.SystemDirectory, "Dism.exe"), "/Online /Set-ReservedStorageState /State:" + (on ? "Enabled" : "Disabled"), out c);
            if (c == 0) return null;
            var lines = o.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return lines.Count > 0 ? lines[lines.Count - 1] : "DISM exit code " + c;
        }
    }

    // ================================================================ auto-clean (Task Scheduler, opt-in)
    static class AutoClean
    {
        const string TaskName = "Disk Sweep auto-clean";

        static int Schtasks(string args)
        {
            int c;
            SystemSpace.Run(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args, out c);
            return c;
        }

        public static bool TaskExists() { return Schtasks("/Query /TN \"" + TaskName + "\"") == 0; }

        // Weekly, Sunday 12:00 (runs at the next sign-in if the PC was off is not guaranteed; Windows runs it when it can).
        public static bool SetTask(bool on)
        {
            if (!on) { Schtasks("/Delete /F /TN \"" + TaskName + "\""); return true; }
            string exe = Assembly.GetExecutingAssembly().Location;
            return Schtasks("/Create /F /TN \"" + TaskName + "\" /SC WEEKLY /D SUN /ST 12:00 /TR \"\\\"" + exe + "\\\" --auto-clean\"") == 0;
        }

        // What auto-clean may touch: only "Safe" items that delete files directly. Never the Recycle Bin,
        // commands, large files, leftovers, project folders or anything marked "Check first", and never hidden items.
        public static bool Eligible(CleanItem it)
        {
            return it.Safety == Safety.Safe && (it.Mode == Mode.Contents || it.Mode == Mode.Whole)
                   && !it.NeedsAdmin && it.Size > 0
                   && it.Category != Scanner.CatBig && it.Category != Scanner.CatLeft && it.Category != Scanner.CatProj
                   && !Settings.Hidden.ContainsKey(MainUi.HideKey(it));
        }

        public static int Run()
        {
            Settings.Load();
            if (!Settings.AutoClean) return 0;
            var items = Scanner.ScanAll(null).Where(Eligible).ToList();
            long total = 0;
            foreach (var it in items)
            {
                long freed = 0;
                try { freed = Cleaner.Clean(it); } catch { }
                if (freed > 0) { total += freed; Activity.Add("auto", it.Name, freed); }
            }
            if (total > 0) { Settings.History.Add(new HistoryEntry { When = DateTime.Now, Bytes = total, Items = items.Count }); Settings.Save(); }
            if (total >= (50L << 20)) Notify.Show("Disk Sweep cleaned " + Util.Fmt(total), "Removed caches and temporary files that rebuild by themselves. Click to open Disk Sweep.");
            return 0;
        }
    }

    // A Windows notification from a tray icon (used by the alert and auto-clean). Stays clickable for 45 seconds.
    static class Notify
    {
        public static void Show(string title, string text)
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            using (var ni = new System.Windows.Forms.NotifyIcon())
            {
                try { ni.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe); } catch { ni.Icon = System.Drawing.SystemIcons.Information; }
                ni.Text = "Disk Sweep";
                ni.Visible = true;
                ni.BalloonTipTitle = title;
                ni.BalloonTipText = text;
                Action open = () => { try { Process.Start(exe); } catch { } System.Windows.Forms.Application.ExitThread(); };
                ni.BalloonTipClicked += (s, e) => open();
                ni.Click += (s, e) => open();
                var timer = new System.Windows.Forms.Timer { Interval = 45000 };
                timer.Tick += (s, e) => System.Windows.Forms.Application.ExitThread();
                timer.Start();
                ni.ShowBalloonTip(20000);
                System.Windows.Forms.Application.Run();
                ni.Visible = false;
            }
        }
    }
}
