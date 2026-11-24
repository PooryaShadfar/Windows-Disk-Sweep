// Disk Sweep - user settings, hidden items, cleanup history, and the low-space check.
// Everything is stored as plain text in %LOCALAPPDATA%\DiskSweep.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DiskSweep
{
    class HistoryEntry
    {
        public DateTime When;
        public long Bytes;
        public int Items;
    }

    static class Settings
    {
        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSweep"); } }
        static string IniFile { get { return Path.Combine(Dir, "settings.ini"); } }
        static string HiddenFile { get { return Path.Combine(Dir, "hidden.txt"); } }
        static string HistoryFile { get { return Path.Combine(Dir, "history.txt"); } }
        public static string FoldersFile { get { return Path.Combine(Dir, "code-folders.txt"); } }

        public static string Theme = "System";          // System | Light | Dark
        public static bool RecycleReview = true;        // "Check first" items go to the Recycle Bin
        public static bool AlertOn;
        public static int AlertGB = 10;
        public static int CacheDays = 30;           // package caches: keep files used in the last N days
        public static int ProjectDays = 14;         // build folders: skip projects changed in the last N days
        public static DateTime LastAlert = DateTime.MinValue;
        public static Dictionary<string, string> Hidden = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // key -> label
        public static List<HistoryEntry> History = new List<HistoryEntry>();

        public static void Load()
        {
            try
            {
                if (File.Exists(IniFile))
                    foreach (var line in File.ReadAllLines(IniFile))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                        switch (k)
                        {
                            case "Theme": if (v == "Light" || v == "Dark" || v == "System") Theme = v; break;
                            case "RecycleReview": RecycleReview = v == "1"; break;
                            case "AlertOn": AlertOn = v == "1"; break;
                            case "AlertGB": int g; if (int.TryParse(v, out g) && g > 0) AlertGB = g; break;
                            case "CacheDays": int cd; if (int.TryParse(v, out cd) && cd >= 0) CacheDays = cd; break;
                            case "ProjectDays": int pd; if (int.TryParse(v, out pd) && pd >= 0) ProjectDays = pd; break;
                            case "LastAlert":
                                DateTime d;
                                if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) LastAlert = d;
                                break;
                        }
                    }
            }
            catch { }
            try
            {
                Hidden.Clear();
                if (File.Exists(HiddenFile))
                    foreach (var line in File.ReadAllLines(HiddenFile))
                    {
                        var p = line.Split('\t');
                        if (p[0].Length > 0) Hidden[p[0]] = p.Length > 1 ? p[1] : p[0];
                    }
            }
            catch { }
            try
            {
                History.Clear();
                if (File.Exists(HistoryFile))
                    foreach (var line in File.ReadAllLines(HistoryFile))
                    {
                        var p = line.Split('\t');
                        DateTime d; long b; int n;
                        if (p.Length >= 3 && DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                            && long.TryParse(p[1], out b) && int.TryParse(p[2], out n))
                            History.Add(new HistoryEntry { When = d, Bytes = b, Items = n });
                    }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(IniFile, new[]
                {
                    "Theme=" + Theme,
                    "RecycleReview=" + (RecycleReview ? "1" : "0"),
                    "AlertOn=" + (AlertOn ? "1" : "0"),
                    "AlertGB=" + AlertGB,
                    "CacheDays=" + CacheDays,
                    "ProjectDays=" + ProjectDays,
                    "LastAlert=" + LastAlert.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                });
                File.WriteAllLines(HiddenFile, Hidden.Select(kv => kv.Key + "\t" + kv.Value.Replace("\t", " ")));
                File.WriteAllLines(HistoryFile, History.Select(h =>
                    h.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\t" + h.Bytes + "\t" + h.Items));
            }
            catch { }
        }

        public static long TotalCleaned { get { return History.Sum(h => h.Bytes); } }

        public static List<string> LoadFolders()
        {
            try
            {
                if (!File.Exists(FoldersFile)) return new List<string>();
                return File.ReadAllLines(FoldersFile).Select(l => l.Trim()).Where(l => l.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new List<string>(); }
        }

        public static void SaveFolders(List<string> folders)
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllLines(FoldersFile, folders); } catch { }
        }

        // ------------------------------------------------------------ low-space alert (Task Scheduler)
        const string TaskName = "Disk Sweep low space check";

        static int Schtasks(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        public static bool AlertTaskExists() { return Schtasks("/Query /TN \"" + TaskName + "\"") == 0; }

        // Checks every 3 hours while you're signed in. Runs DiskSweep.exe --check-space (no window).
        public static bool SetAlertTask(bool on)
        {
            if (!on) { Schtasks("/Delete /F /TN \"" + TaskName + "\""); return true; }
            string exe = Assembly.GetExecutingAssembly().Location;
            return Schtasks("/Create /F /TN \"" + TaskName + "\" /SC HOURLY /MO 3 /TR \"\\\"" + exe + "\\\" --check-space\"") == 0;
        }

        public static List<DriveInfo> FixedDrives()
        {
            try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList(); }
            catch { return new List<DriveInfo>(); }
        }

        // Shows a Windows notification when a drive is low on space or reports a health problem.
        // At most once every 20 hours.
        public static int CheckSpace()
        {
            Load();
            if (!AlertOn || (DateTime.Now - LastAlert).TotalHours < 20) return 0;
            var lines = FixedDrives().Where(d => d.AvailableFreeSpace < (long)AlertGB << 30)
                .Select(d => "Drive " + d.Name.TrimEnd('\\') + " has only " + Util.Fmt(d.AvailableFreeSpace) + " free.").ToList();
            bool health = false;
            try
            {
                foreach (var d in Health.Read().Where(x => x.Level == HealthLevel.Warning || x.Level == HealthLevel.Bad))
                {
                    health = true;
                    lines.Add(d.Model + (d.Letters.Count > 0 ? " (" + string.Join(" ", d.Letters) + ")" : "") + ": "
                              + (d.Problems.Count > 0 ? d.Problems[0] : Health.LevelText(d.Level)));
                }
            }
            catch { }
            if (lines.Count == 0) return 0;
            LastAlert = DateTime.Now;
            Save();

            string exe = Assembly.GetExecutingAssembly().Location;
            string text = string.Join("\n", lines) + "\nClick to open Disk Sweep.";
            string title = health ? "Drive health warning" : "Low disk space";
            using (var ni = new System.Windows.Forms.NotifyIcon())
            {
                try { ni.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe); } catch { ni.Icon = System.Drawing.SystemIcons.Warning; }
                ni.Text = "Disk Sweep";
                ni.Visible = true;
                ni.BalloonTipTitle = title;
                ni.BalloonTipText = text;
                ni.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Warning;
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
            return 0;
        }
    }
}
