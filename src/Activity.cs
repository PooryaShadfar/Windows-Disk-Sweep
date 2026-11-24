// Disk Sweep - activity log (what was removed, moved or compressed) and restoring from the Recycle Bin.
// Log: %LOCALAPPDATA%\DiskSweep\activity.log, one tab-separated line per action.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace DiskSweep
{
    class ActivityEntry
    {
        public DateTime When;
        public string Kind;          // deleted | recycled | moved | compressed | system | auto
        public string Name;
        public long Bytes;
        public List<string> Paths = new List<string>();   // for recycled items: what went to the Recycle Bin
    }

    static class Activity
    {
        static string LogFile { get { return Path.Combine(Settings.Dir, "activity.log"); } }
        const int MaxPaths = 500, MaxEntries = 400;

        public static bool Muted;   // set by the self-test so it never writes to the real log

        public static void Add(string kind, string name, long bytes, IEnumerable<string> paths = null)
        {
            if (Muted) return;
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var p = (paths ?? new string[0]).Take(MaxPaths).Select(x => x.Replace("\t", " ").Replace("|", " "));
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + kind + "\t"
                              + (name ?? "").Replace("\t", " ") + "\t" + bytes + "\t" + string.Join("|", p);
                File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
                Trim();
            }
            catch { }
        }

        static void Trim()
        {
            var lines = File.ReadAllLines(LogFile);
            if (lines.Length > MaxEntries) File.WriteAllLines(LogFile, lines.Skip(lines.Length - MaxEntries), Encoding.UTF8);
        }

        public static List<ActivityEntry> Read()
        {
            var list = new List<ActivityEntry>();
            try
            {
                if (!File.Exists(LogFile)) return list;
                foreach (var line in File.ReadAllLines(LogFile, Encoding.UTF8))
                {
                    var c = line.Split('\t');
                    if (c.Length < 4) continue;
                    DateTime w; long b;
                    if (!DateTime.TryParseExact(c[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out w)) continue;
                    long.TryParse(c[3], out b);
                    var e = new ActivityEntry { When = w, Kind = c[1], Name = c[2], Bytes = b };
                    if (c.Length > 4 && c[4].Length > 0) e.Paths.AddRange(c[4].Split('|'));
                    list.Add(e);
                }
            }
            catch { }
            list.Reverse();   // newest first
            return list;
        }

        public static void Clear() { try { File.Delete(LogFile); } catch { } }

        // ------------------------------------------------------------ Recycle Bin
        class BinItem { public string Original, DataPath, InfoPath; }

        // Reads this user's Recycle Bin on every drive. Each deleted item is a pair: $I... (original path) + $R... (the data).
        static List<BinItem> BinItems()
        {
            var res = new List<BinItem>();
            string sid;
            try { sid = WindowsIdentity.GetCurrent().User.Value; } catch { return res; }
            foreach (var d in Settings.FixedDrives())
            {
                string bin = Path.Combine(d.Name, "$Recycle.Bin", sid);
                string[] infos;
                try { infos = Directory.GetFiles(bin, "$I*"); } catch { continue; }
                foreach (var info in infos)
                {
                    try
                    {
                        string original = ReadInfo(File.ReadAllBytes(info));
                        string data = Path.Combine(bin, "$R" + Path.GetFileName(info).Substring(2));
                        if (original != null && (File.Exists(data) || Directory.Exists(data)))
                            res.Add(new BinItem { Original = original, DataPath = data, InfoPath = info });
                    }
                    catch { }
                }
            }
            return res;
        }

        // $I file: version (8 bytes), size (8), deletion time (8), then the original path:
        // version 1 = fixed 260 UTF-16 chars, version 2 = 4-byte length + UTF-16 chars.
        static string ReadInfo(byte[] b)
        {
            if (b.Length < 24) return null;
            long version = BitConverter.ToInt64(b, 0);
            if (version == 2 && b.Length >= 28)
            {
                int len = BitConverter.ToInt32(b, 24);
                if (len <= 0 || 28 + len * 2 > b.Length) return null;
                return Encoding.Unicode.GetString(b, 28, len * 2).TrimEnd('\0');
            }
            if (version == 1) return Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24)).Split('\0')[0];
            return null;
        }

        // How many of this entry's items are still in the Recycle Bin and can go back.
        public static int Restorable(ActivityEntry e)
        {
            if (e.Kind != "recycled" || e.Paths.Count == 0) return 0;
            var set = new HashSet<string>(e.Paths, StringComparer.OrdinalIgnoreCase);
            return BinItems().Count(i => set.Contains(i.Original) && !File.Exists(i.Original) && !Directory.Exists(i.Original));
        }

        // Puts items back where they were. Returns how many were restored.
        public static int Restore(ActivityEntry e)
        {
            var set = new HashSet<string>(e.Paths, StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var i in BinItems().Where(x => set.Contains(x.Original)))
            {
                try
                {
                    if (File.Exists(i.Original) || Directory.Exists(i.Original)) continue;   // never overwrite
                    Directory.CreateDirectory(Path.GetDirectoryName(i.Original));
                    if (Directory.Exists(i.DataPath)) Directory.Move(i.DataPath, i.Original);
                    else File.Move(i.DataPath, i.Original);
                    File.Delete(i.InfoPath);
                    n++;
                }
                catch { }
            }
            if (n > 0) Add("restored", e.Name, 0, e.Paths);
            return n;
        }
    }
}
