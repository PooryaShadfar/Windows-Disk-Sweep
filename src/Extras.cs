// Disk Sweep 1.5 - duplicate files, leftovers after uninstalling, restore point before big changes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;

namespace DiskSweep
{
    // ================================================================ duplicate files
    class DupFile
    {
        public string Path;
        public long Size;
        public DateTime Created, Modified;
    }

    class DupGroup
    {
        public List<DupFile> Files = new List<DupFile>();
        public long Size;
        public long Wasted { get { return Size * (Files.Count - 1); } }
    }

    static class Duplicates
    {
        static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "AppData", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin",
            "System Volume Information", ".next", ".nuxt", "__pycache__", "venv", ".venv", "obj", ".gradle", ".nuget", ".cargo",
            "SteamLibrary", "steamapps", "WindowsApps"
        };

        const FileAttributes CloudOnly = (FileAttributes)0x00400000 | (FileAttributes)0x00040000 | FileAttributes.Offline;

        public static List<string> DefaultRoots()
        {
            string u = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var list = new[] { "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music" }
                .Select(n => Path.Combine(u, n)).Where(Directory.Exists).ToList();
            return list;
        }

        // Same size -> same first 64 KB -> same SHA-256 of the whole file. Only files of minSize or more.
        public static List<DupGroup> Find(IEnumerable<string> roots, long minSize, IProgress<string> p, CancellationToken ct)
        {
            var files = new List<FileInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roots)
                if (Directory.Exists(r)) Walk(new DirectoryInfo(Fs.Long(r)), minSize, files, seen, p, ct, 0);

            var groups = new List<DupGroup>();
            var bySize = files.GroupBy(f => f.Length).Where(g => g.Count() > 1).ToList();
            int done = 0;
            foreach (var g in bySize)
            {
                ct.ThrowIfCancellationRequested();
                if (p != null && ++done % 20 == 0) p.Report("Comparing files… " + done + " of " + bySize.Count + " size groups");
                foreach (var quick in g.GroupBy(f => Hash(f, 65536)).Where(x => x.Key != null && x.Count() > 1))
                {
                    var full = g.Key <= 65536 ? quick.GroupBy(f => quick.Key) : quick.GroupBy(f => Hash(f, -1));
                    foreach (var same in full.Where(x => x.Key != null && x.Count() > 1))
                    {
                        var dg = new DupGroup { Size = g.Key };
                        foreach (var f in same.OrderBy(x => x.CreationTime))
                        {
                            string path = f.FullName.StartsWith(@"\\?\") ? f.FullName.Substring(4) : f.FullName;
                            dg.Files.Add(new DupFile { Path = path, Size = f.Length, Created = f.CreationTime, Modified = f.LastWriteTime });
                        }
                        groups.Add(dg);
                    }
                }
            }
            return groups.OrderByDescending(x => x.Wasted).ToList();
        }

        static void Walk(DirectoryInfo d, long minSize, List<FileInfo> files, HashSet<string> seen, IProgress<string> p, CancellationToken ct, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 30 || !seen.Add(d.FullName)) return;
            if (p != null && depth <= 2) p.Report("Looking in " + d.FullName.Replace(@"\\?\", "") + "…");
            FileSystemInfo[] es;
            try { es = d.GetFileSystemInfos(); } catch { return; }
            foreach (var e in es)
            {
                try
                {
                    if (Fs.IsLink(e)) continue;
                    var f = e as FileInfo;
                    if (f != null)
                    {
                        if (f.Length >= minSize && (f.Attributes & (CloudOnly | FileAttributes.System)) == 0) files.Add(f);
                    }
                    else if (!Skip.Contains(e.Name)) Walk((DirectoryInfo)e, minSize, files, seen, p, ct, depth + 1);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        // SHA-256 of the first `limit` bytes (or the whole file when limit < 0). Null if the file can't be read.
        static string Hash(FileInfo f, int limit)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var s = new FileStream(Fs.Long(f.FullName.StartsWith(@"\\?\") ? f.FullName.Substring(4) : f.FullName),
                                              FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                {
                    if (limit < 0) return Convert.ToBase64String(sha.ComputeHash(s));
                    var buf = new byte[limit];
                    int n = s.Read(buf, 0, limit);
                    return Convert.ToBase64String(sha.ComputeHash(buf, 0, n));
                }
            }
            catch { return null; }
        }
    }

    // ================================================================ leftovers after uninstalling
    static class AppLeftovers
    {
        public static bool StillInstalled(AppEntry a)
        {
            try
            {
                int cut = a.KeyPath.IndexOf('\\');
                string hive = a.KeyPath.Substring(0, cut), rest = a.KeyPath.Substring(cut + 1);
                var root = hive.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ? Registry.CurrentUser : Registry.LocalMachine;
                using (var k = root.OpenSubKey(rest)) return k != null;
            }
            catch { return true; }
        }

        static readonly HashSet<string> Shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Windows", "Google", "Adobe", "Apple", "Mozilla", "Intel", "AMD", "NVIDIA", "NVIDIA Corporation", "Packages",
            "Temp", "Programs", "Common Files", "Package Cache", "JetBrains", "Oracle", "Sun", "Steam", "Microsoft Office", "Windows Defender", "DiskSweep"
        };

        static string Norm(string s) { return new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()); }

        // Folders the removed app probably left: its install folder, and folders named after it in AppData,
        // ProgramData and Program Files. Shared vendor folders and folders of apps that are still installed are skipped.
        public static List<Tuple<string, long>> Find(AppEntry a, List<AppEntry> stillInstalled)
        {
            var res = new List<Tuple<string, long>>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (a.Location.Length > 3 && Directory.Exists(a.Location)) paths.Add(a.Location.TrimEnd('\\'));

            string name = Norm(a.Name);
            // "Foo Bar 2.3 (64-bit)" -> also try "foobar"
            string core = Norm(System.Text.RegularExpressions.Regex.Replace(a.Name, @"[\d\.\(\)]|x64|x86|64-bit|32-bit", " "));
            var keys = new[] { name, core }.Where(k => k.Length >= 4).Distinct().ToList();
            var others = stillInstalled.Where(x => x != a).Select(x => Norm(x.Name)).ToList();

            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
            };
            string pub = Norm(a.Publisher);
            foreach (var r in roots.Where(Directory.Exists))
            {
                foreach (var d in Fs.Dirs(r))
                {
                    if (Fs.IsLink(d) || Shared.Contains(d.Name)) continue;
                    string n = Norm(d.Name);
                    string full = d.FullName.StartsWith(@"\\?\") ? d.FullName.Substring(4) : d.FullName;
                    if (n.Length >= 4 && keys.Any(k => k == n || n.StartsWith(k) || k.StartsWith(n) && n.Length >= 6 && n.Length >= k.Length * 0.6))
                    {
                        if (others.Any(o => o.Contains(n) || n.Contains(o) && o.Length >= 4)) continue;   // another app uses this name
                        paths.Add(full);
                    }
                    // Vendor folder ("Publisher\App"): look one level down
                    else if (pub.Length >= 4 && (n == pub || pub.StartsWith(n) && n.Length >= 5))
                        foreach (var c in Fs.Dirs(full))
                        {
                            string cn = Norm(c.Name);
                            if (cn.Length >= 4 && keys.Any(k => k == cn || k.StartsWith(cn) || cn.StartsWith(k))
                                && !others.Any(o => o.Contains(cn)))
                                paths.Add(c.FullName.StartsWith(@"\\?\") ? c.FullName.Substring(4) : c.FullName);
                        }
                }
            }
            foreach (var pth in paths)
            {
                long size = Fs.PathSize(pth, null, 0, true);
                if (size > 0) res.Add(Tuple.Create(pth, size));
            }
            return res.OrderByDescending(x => x.Item2).ToList();
        }
    }

    // ================================================================ restore point before big changes (admin)
    static class RestorePoint
    {
        // Returns null when a restore point was made, otherwise why not.
        // Windows only allows one every 24 hours by default; a recent one counts as protection too.
        public static string Create(string description)
        {
            try
            {
                int before = Count();
                using (var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null))
                {
                    var args = cls.GetMethodParameters("CreateRestorePoint");
                    args["Description"] = description;
                    args["RestorePointType"] = 12;     // MODIFY_SETTINGS
                    args["EventType"] = 100;           // BEGIN_SYSTEM_CHANGE
                    var outp = cls.InvokeMethod("CreateRestorePoint", args, null);
                    uint rc = Convert.ToUInt32(outp["ReturnValue"]);
                    if (rc != 0) return rc == 1058 ? "System Restore is turned off for this PC." : "Windows didn't create it (code " + rc + ").";
                }
                return Count() > before ? null : "skipped: Windows made a restore point in the last 24 hours, which already covers this.";
            }
            catch (Exception ex) { return "Couldn't create a restore point: " + ex.Message; }
        }

        static int Count()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"), new ObjectQuery("SELECT SequenceNumber FROM SystemRestore")))
                    return s.Get().Count;
            }
            catch { return -1; }
        }

        // Changes that are hard to undo by hand.
        public static bool IsBigChange(CleanItem it)
        {
            if (it.Mode != Mode.Command) return false;
            return it.Cmd == "sagerun" || (it.Cmd ?? "").EndsWith("Dism.exe", StringComparison.OrdinalIgnoreCase)
                   || (it.Cmd ?? "").EndsWith("powercfg.exe", StringComparison.OrdinalIgnoreCase);
        }
    }
}
