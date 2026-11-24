// Disk Sweep - tools (1.3): space map, apps by size, move a folder to another drive, compression.
// None of these run on their own; each one is started by a button and asks before changing anything.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace DiskSweep
{
    // ================================================================ space map
    class MapNode
    {
        public string Name;
        public string Path;
        public long Size;
        public bool IsFiles;              // "files in this folder" bucket
        public List<MapNode> Children;
        public MapNode Parent;
    }

    static class SpaceMap
    {
        // Measures every folder under root. Never follows links. Reports progress every ~2000 folders.
        public static MapNode Build(string root, IProgress<string> p, CancellationToken ct)
        {
            long count = 0;
            var node = new MapNode { Name = root, Path = root };
            Walk(new DirectoryInfo(Fs.Long(root)), node, p, ct, ref count);
            return node;
        }

        static void Walk(DirectoryInfo d, MapNode node, IProgress<string> p, CancellationToken ct, ref long count)
        {
            ct.ThrowIfCancellationRequested();
            if (++count % 2000 == 0 && p != null) p.Report(count.ToString("N0") + " folders measured…  " + node.Path);
            node.Children = new List<MapNode>();
            long files = 0;
            IEnumerable<FileSystemInfo> es;
            try { es = d.EnumerateFileSystemInfos(); } catch { return; }
            try
            {
                foreach (var e in es)
                {
                    try
                    {
                        if (Fs.IsLink(e)) continue;
                        var f = e as FileInfo;
                        if (f != null) { files += f.Length; continue; }
                        string path = e.FullName.StartsWith(@"\\?\") ? e.FullName.Substring(4) : e.FullName;
                        var c = new MapNode { Name = e.Name, Path = path, Parent = node };
                        Walk((DirectoryInfo)e, c, p, ct, ref count);
                        if (c.Size > 0) node.Children.Add(c);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            if (files > 0) node.Children.Add(new MapNode { Name = node.Path.TrimEnd('\\').Length <= 2 ? "Files here (page file, hibernation…)" : "Files in this folder", Path = node.Path, Size = files, IsFiles = true, Parent = node });
            node.Size = node.Children.Sum(c => c.Size);
            node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            // Keep memory small: tiny folders deep down don't need their own children.
            if (node.Size < (1L << 20)) node.Children = node.Children.Where(c => c.IsFiles).ToList();
        }

        public struct Rect { public double X, Y, W, H; public MapNode Node; }

        // Squarified treemap layout.
        public static List<Rect> Layout(List<MapNode> items, double x, double y, double w, double h)
        {
            var result = new List<Rect>();
            var nodes = items.Where(n => n.Size > 0).ToList();
            double total = nodes.Sum(n => (double)n.Size);
            if (total <= 0 || w <= 0 || h <= 0) return result;
            double scale = w * h / total;
            var areas = nodes.Select(n => n.Size * scale).ToList();
            int i = 0;
            while (i < nodes.Count)
            {
                double side = Math.Min(w, h);
                var row = new List<int> { i };
                double rowSum = areas[i];
                double worst = Worst(areas, row, rowSum, side);
                int j = i + 1;
                while (j < nodes.Count)
                {
                    row.Add(j);
                    double s2 = rowSum + areas[j];
                    double w2 = Worst(areas, row, s2, side);
                    if (w2 > worst) { row.RemoveAt(row.Count - 1); break; }
                    worst = w2; rowSum = s2; j++;
                }
                // place the row along the short side
                if (w >= h)
                {
                    double colW = rowSum / h, cy = y;
                    foreach (int k in row) { double rh = areas[k] / colW; result.Add(new Rect { X = x, Y = cy, W = colW, H = rh, Node = nodes[k] }); cy += rh; }
                    x += colW; w -= colW;
                }
                else
                {
                    double rowH = rowSum / w, cx = x;
                    foreach (int k in row) { double rw = areas[k] / rowH; result.Add(new Rect { X = cx, Y = y, W = rw, H = rowH, Node = nodes[k] }); cx += rw; }
                    y += rowH; h -= rowH;
                }
                i += row.Count;
            }
            return result;
        }

        static double Worst(List<double> areas, List<int> row, double sum, double side)
        {
            double mx = row.Max(k => areas[k]), mn = row.Min(k => areas[k]);
            double s2 = side * side, sum2 = sum * sum;
            return Math.Max(s2 * mx / sum2, sum2 / (s2 * mn));
        }
    }

    // ================================================================ apps by size
    class AppEntry
    {
        public string Name, Publisher, Version, Uninstall, Location, KeyPath;
        public long Size;                 // bytes (estimate from the installer, or measured)
        public DateTime? Installed;
    }

    static class Apps
    {
        public static List<AppEntry> List()
        {
            var apps = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            var roots = new[]
            {
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
            };
            foreach (var r in roots)
            {
                try
                {
                    using (var k = r.Item1.OpenSubKey(r.Item2))
                    {
                        if (k == null) continue;
                        foreach (var sub in k.GetSubKeyNames())
                            using (var s = k.OpenSubKey(sub))
                            {
                                if (s == null) continue;
                                string name = s.GetValue("DisplayName") as string;
                                string un = s.GetValue("UninstallString") as string;
                                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(un)) continue;
                                if (Convert.ToInt32(s.GetValue("SystemComponent", 0)) == 1) continue;
                                if (s.GetValue("ParentKeyName") != null) continue;
                                string rt = s.GetValue("ReleaseType") as string ?? "";
                                if (rt.Contains("Update") || rt.Contains("Hotfix")) continue;
                                var a = new AppEntry
                                {
                                    Name = name.Trim(),
                                    Publisher = (s.GetValue("Publisher") as string ?? "").Trim(),
                                    Version = (s.GetValue("DisplayVersion") as string ?? "").Trim(),
                                    Uninstall = un.Trim(),
                                    Location = (s.GetValue("InstallLocation") as string ?? "").Trim().Trim('"'),
                                    KeyPath = r.Item1.Name + "\\" + r.Item2 + "\\" + sub
                                };
                                try { a.Size = Convert.ToInt64(s.GetValue("EstimatedSize", 0)) * 1024; } catch { }
                                string d = s.GetValue("InstallDate") as string;
                                DateTime dt;
                                if (d != null && DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out dt)) a.Installed = dt;
                                AppEntry existing;
                                if (!apps.TryGetValue(a.Name, out existing) || existing.Size < a.Size) apps[a.Name] = a;
                            }
                    }
                }
                catch { }
            }
            return apps.Values.ToList();
        }

        // Measures apps whose installer didn't report a size (only when the folder is known).
        public static void MeasureMissing(List<AppEntry> apps)
        {
            foreach (var a in apps.Where(x => x.Size == 0 && x.Location.Length > 3 && Directory.Exists(x.Location)))
            {
                // never measure a whole drive or a shared parent folder
                string full = a.Location.TrimEnd('\\');
                if (full.Length <= 3 || full.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)) continue;
                a.Size = Fs.PathSize(full, null, 0, true);
            }
        }

        // Starts the app's own uninstaller. Windows or the app may ask for permission.
        public static void Uninstall(AppEntry a)
        {
            string cmd = a.Uninstall, file, args;
            var msi = Regex.Match(cmd, @"msiexec(\.exe)?\s+/[IX]\s*(\{[0-9A-Fa-f\-]+\})", RegexOptions.IgnoreCase);
            if (msi.Success) { file = "msiexec.exe"; args = "/X" + msi.Groups[2].Value; }
            else if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                file = cmd.Substring(1, end - 1);
                args = cmd.Substring(end + 1).Trim();
            }
            else
            {
                int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe > 0) { file = cmd.Substring(0, exe + 4); args = cmd.Substring(exe + 4).Trim(); }
                else { file = cmd; args = ""; }
            }
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
    }

    // ================================================================ move a folder to another drive
    static class Mover
    {
        [DllImport("shell32.dll")]
        static extern int SHSetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] string path);

        static readonly Guid Downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        public static string DownloadsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); }
        }

        // Returns null when the folder can be moved, otherwise the reason it can't.
        public static string Check(string src, string targetRoot)
        {
            if (string.IsNullOrEmpty(src) || !Directory.Exists(src)) return "Pick a folder first.";
            var di = new DirectoryInfo(src);
            if (Fs.IsLink(di)) return "This folder is already a link to somewhere else.";
            string full = di.FullName.TrimEnd('\\');
            if (full.Length <= 3) return "A whole drive can't be moved.";
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] blocked =
            {
                win, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Path.GetPathRoot(win), "Users")
            };
            foreach (var b in blocked)
                if (full.Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "This system folder can't be moved safely.";
            if (full.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return "Folders inside Windows can't be moved.";
            if (full.IndexOf(@"\steamapps", StringComparison.OrdinalIgnoreCase) >= 0 || full.EndsWith(@"\Steam", StringComparison.OrdinalIgnoreCase))
                return "Move Steam games with Steam itself: Steam > Settings > Storage > Move.";
            if (string.Equals(Path.GetPathRoot(full), targetRoot, StringComparison.OrdinalIgnoreCase)) return "Pick a different drive than the one the folder is on.";
            return null;
        }

        public static string TargetFor(string src, string targetRoot)
        {
            return Path.Combine(targetRoot, "Moved by Disk Sweep", new DirectoryInfo(src).Name);
        }

        // 1. rename the folder (fails if something is using it), 2. copy, 3. check, 4. link, 5. delete the old copy.
        public static string Move(string src, string targetRoot, IProgress<string> p)
        {
            src = src.TrimEnd('\\');
            string target = TargetFor(src, targetRoot);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) return "The target folder already exists and isn't empty: " + target;
            long size = Fs.PathSize(src, null, 0, true);
            var drive = new DriveInfo(targetRoot);
            if (drive.AvailableFreeSpace < size + (1L << 30)) return "Not enough free space on " + targetRoot.TrimEnd('\\') + ".";

            bool isDownloads = src.Equals(DownloadsPath, StringComparison.OrdinalIgnoreCase);
            string old = src + ".disksweep-moving";
            if (p != null) p.Report("Checking nothing is using the folder…");
            try { Directory.Move(src, old); }
            catch { return "Something is using this folder. Close the apps that use it and try again."; }

            if (p != null) p.Report("Copying " + Util.Fmt(size) + " to " + target + "…");
            Directory.CreateDirectory(target);
            int rc = RunRobocopy(old, target);
            long copied = Fs.PathSize(target, null, 0, true);
            if (rc >= 8 || copied < size)
            {
                try { Directory.Move(old, src); } catch { }
                return "Copying failed (" + Util.Fmt(copied) + " of " + Util.Fmt(size) + "). Your folder is back where it was. The partial copy is in " + target + ".";
            }

            if (p != null) p.Report("Linking the old place to the new one…");
            if (isDownloads)
            {
                if (SHSetKnownFolderPath(Downloads, 0, IntPtr.Zero, target) != 0)
                {
                    try { Directory.Move(old, src); } catch { }
                    return "Windows didn't accept the new Downloads location. Your folder is back where it was.";
                }
            }
            else if (!MakeJunction(src, target))
            {
                try { Directory.Move(old, src); } catch { }
                return "Couldn't create the link. Your folder is back where it was. The copy is in " + target + ".";
            }

            if (p != null) p.Report("Removing the old copy…");
            Fs.DeletePath(old, null, 0, true);
            if (Directory.Exists(old)) return "Moved, but some old files were in use and are still in " + old + ". You can delete that folder later.";
            return null;
        }

        static int RunRobocopy(string from, string to)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "robocopy.exe"),
                "\"" + from + "\" \"" + to + "\" /E /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /MT:8 /NFL /NDL /NP /NJH /NJS")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using (var pr = Process.Start(psi)) { pr.StandardOutput.ReadToEnd(); pr.WaitForExit(); return pr.ExitCode; }
        }

        static bool MakeJunction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var pr = Process.Start(psi)) { pr.StandardOutput.ReadToEnd(); pr.StandardError.ReadToEnd(); pr.WaitForExit(); }
            return Directory.Exists(link) && Fs.IsLink(new DirectoryInfo(link));
        }
    }

    // ================================================================ compression
    static class Compressor
    {
        static string Compact { get { return Path.Combine(Environment.SystemDirectory, "compact.exe"); } }

        static string Run(string args, out int code)
        {
            var psi = new ProcessStartInfo(Compact, args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit(); code = p.ExitCode;
                return o;
            }
        }

        // "on", "off" or "unknown". Windows' message text is translated, so only the English wording is parsed;
        // other languages show "unknown" and the button still works.
        public static string CompactOsState()
        {
            try
            {
                int c;
                string o = Run("/CompactOS:query", out c);
                if (o.IndexOf("is in the Compact state", StringComparison.OrdinalIgnoreCase) >= 0) return "on";
                if (o.IndexOf("not in the Compact state", StringComparison.OrdinalIgnoreCase) >= 0) return "off";
            }
            catch { }
            return "unknown";
        }

        public static long EnableCompactOs()
        {
            long before = Native.FreeBytes();
            int c; Run("/CompactOS:always", out c);
            return Math.Max(0, Native.FreeBytes() - before);
        }

        public static string CheckFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return "Pick a folder first.";
            string full = folder.TrimEnd('\\');
            if (full.Length <= 3) return "Pick a folder, not a whole drive.";
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (full.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return "Use \"Compress Windows\" for system files instead.";
            return null;
        }

        // Windows' file compression (the same as compact /EXE). Files stay usable; Windows unpacks them on the fly.
        public static long CompressFolder(string folder)
        {
            string drive = Path.GetPathRoot(folder);
            long before = Native.FreeBytes(drive);
            int c; Run("/C /S:\"" + folder.TrimEnd('\\') + "\" /I /Q /EXE:XPRESS8K", out c);
            return Math.Max(0, Native.FreeBytes(drive) - before);
        }
    }
}
