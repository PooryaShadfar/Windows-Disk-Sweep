// Disk Sweep - finders added in 1.1:
//   - leftovers of apps that are no longer installed
//   - large files (moved to the Recycle Bin, never deleted outright)
//   - project build folders (node_modules, .next, bin/obj, target, venv, ...)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace DiskSweep
{
    static partial class Scanner
    {
        public const string CatLeft = "Leftovers of removed apps";
        public const string CatBig = "Large files";
        public const string CatProj = "Project build folders";

        const long MB = 1L << 20;

        static string Norm(string s)
        {
            var c = s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
            return new string(c);
        }

        // Size of a tree plus the newest file time in it (never follows links).
        static long TreeSizeLatest(DirectoryInfo d, ref DateTime latest)
        {
            long t = 0;
            FileSystemInfo[] es;
            try { es = d.GetFileSystemInfos(); } catch { return 0; }
            foreach (var e in es)
            {
                try
                {
                    if (Fs.IsLink(e)) continue;
                    if (e.LastWriteTime > latest) latest = e.LastWriteTime;
                    var f = e as FileInfo;
                    if (f != null) t += f.Length;
                    else t += TreeSizeLatest((DirectoryInfo)e, ref latest);
                }
                catch { }
            }
            return t;
        }

        // ================================================================ leftovers of removed apps

        // Folders that belong to Windows, shared runtimes, or are handled by other items.
        static readonly HashSet<string> LeftoverSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Packages", "Temp", "Programs", "CrashDumps", "D3DSCache", "NVIDIA", "NVIDIA Corporation",
            "AMD", "Intel", "Comms", "ConnectedDevicesPlatform", "PeerDistRepub", "Publishers", "VirtualStore",
            "npm", "npm-cache", "pnpm", "pnpm-cache", "pnpm-state", "Yarn", "pip", "NuGet", "go-build", "ms-playwright",
            "Mozilla", "Google", "Apple", "Apple Computer", "Adobe", "Sun", "Oracle", "Package Cache", "PackageManagement",
            "IsolatedStorage", "Diagnostics", "Windows", "assembly", "fontconfig", "gtk-3.0", "History", "SquirrelTemp",
            "Microsoft Help", "Macromedia", "MicrosoftEdge", "DiskSweep", "Application Data", "History", "Temporary Internet Files",
            "Downloaded Installations", "ElevatedDiagnostics", "Deployment", "cache", "Caches", "Programs", "Backup",
            "Unity", "UnityHub", "electron", "electron-builder", "node-gyp", "Python", "pypa", "uv", "pipx", "deno",
            "CEF", "WebView2", "EBWebView", "Squirrel", "GitHub Desktop", "Code", "IdentityNexusIntegration"
        };

        static List<string> InstalledTokens()
        {
            var raw = new List<string>();
            Action<string> addPath = path =>
            {
                if (string.IsNullOrEmpty(path)) return;
                try
                {
                    path = path.Trim().Trim('"');
                    int comma = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                    if (comma > 0) path = Path.GetDirectoryName(path.Substring(0, comma + 4));
                    var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    // last two folder names, e.g. "JetBrains\WebStorm 2026.2"
                    foreach (var x in parts.Skip(Math.Max(0, parts.Length - 2))) raw.Add(x);
                }
                catch { }
            };

            // 1. Programs and Features (all users + current user, 64/32-bit)
            var hives = new[]
            {
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
            };
            foreach (var h in hives)
            {
                try
                {
                    using (var k = h.Item1.OpenSubKey(h.Item2))
                    {
                        if (k == null) continue;
                        foreach (var sub in k.GetSubKeyNames())
                        {
                            raw.Add(sub);
                            using (var s = k.OpenSubKey(sub))
                            {
                                if (s == null) continue;
                                raw.Add(s.GetValue("DisplayName") as string);
                                raw.Add(s.GetValue("Publisher") as string);
                                addPath(s.GetValue("InstallLocation") as string);
                                addPath(s.GetValue("DisplayIcon") as string);
                                addPath(s.GetValue("UninstallString") as string);
                            }
                        }
                    }
                }
                catch { }
            }

            // 2. Program folders (two levels: vendor\product)
            var progRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(L, "Programs")
            };
            foreach (var r in progRoots)
                foreach (var d in Fs.Dirs(r))
                {
                    raw.Add(d.Name);
                    foreach (var c in Fs.Dirs(d.FullName)) raw.Add(c.Name);
                }

            // 3. Start menu shortcuts
            foreach (var sm in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
            })
            {
                try
                {
                    foreach (var f in Directory.EnumerateFileSystemEntries(sm, "*", SearchOption.AllDirectories))
                        raw.Add(Path.GetFileNameWithoutExtension(f));
                }
                catch { }
            }

            // 4. Store apps (Packages\Publisher.App_hash)
            foreach (var d in Fs.Dirs(Path.Combine(L, "Packages")))
                foreach (var part in d.Name.Split('.', '_')) raw.Add(part);

            // 5. Running programs and command-line tools on PATH
            try { foreach (var p in Process.GetProcesses()) { raw.Add(p.ProcessName); p.Dispose(); } } catch { }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) addPath(dir);

            return raw.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Norm).Where(x => x.Length >= 3).Distinct().ToList();
        }

        static bool LooksInstalled(string name, List<string> tokens)
        {
            string n = Norm(name);
            if (n.Length < 3) return true;          // too short to judge: leave it alone
            foreach (var t in tokens)
            {
                if (t == n) return true;
                if (n.Length >= 4 && t.Contains(n)) return true;
                if (t.Length >= 5 && n.Contains(t)) return true;
            }
            return false;
        }

        public static void Leftovers(List<CleanItem> l, IProgress<string> p)
        {
            if (p != null) p.Report("Checking which apps are installed…");
            var tokens = InstalledTokens();
            var roots = new[] { Tuple.Create(R, "Roaming"), Tuple.Create(L, "Local"), Tuple.Create(LL, "LocalLow") };
            DateTime recent = DateTime.Now.AddDays(-30);
            foreach (var root in roots)
            {
                foreach (var d in Fs.Dirs(root.Item1))
                {
                    try
                    {
                        if (Fs.IsLink(d) || LeftoverSkip.Contains(d.Name) || d.Name.StartsWith(".")) continue;
                        if (LooksInstalled(d.Name, tokens)) continue;
                        // Vendor folders ("Company\Product"): keep if any product inside is installed.
                        var kids = Fs.Dirs(d.FullName);
                        if (kids.Any(k => LooksInstalled(k.Name, tokens))) continue;

                        if (p != null) p.Report("Checking: " + d.Name);
                        DateTime latest = DateTime.MinValue;
                        long size = TreeSizeLatest(d, ref latest);
                        if (size < 50 * MB || latest > recent) continue;

                        string path = d.FullName.StartsWith(@"\\?\") ? d.FullName.Substring(4) : d.FullName;
                        var it = Mk(CatLeft, d.Name + "  (" + root.Item2 + ")",
                            "No installed app matches this name. Last changed " + latest.ToString("yyyy-MM-dd") +
                            ". May hold that app's settings or saved data. Leave it if you still use the app.",
                            Safety.Review, false, path);
                        it.Mode = Mode.Whole;
                        it.Size = size;
                        l.Add(it);
                    }
                    catch { }
                }
            }
        }

        // ================================================================ large files

        static readonly HashSet<string> BigSkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "AppData", "Windows", "Program Files", "Program Files (x86)", "ProgramData",
            "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Recovery", "$SysReset", "$Windows.~BT",
            "$Windows.~WS", "Windows.old", "PerfLogs", "MSOCache", "OneDriveTemp", ".nuget", ".gradle", ".cargo", ".rustup",
            ".vscode", ".cursor", ".claude", ".codex", ".cache", "venv", ".venv",
            ".next", ".nuxt", ".turbo", ".parcel-cache", ".svelte-kit", ".angular", ".vite", "__pycache__",
            // installed games and store apps: their big files are not junk
            "SteamLibrary", "steamapps", "Steam", "Epic Games", "XboxGames", "GOG Games", "Riot Games", "EA Games",
            "Ubisoft", "Battle.net", "WindowsApps", "WpSystem", "Program Files (Arm)"
        };

        const FileAttributes CloudOnly = (FileAttributes)0x00400000 /*RecallOnDataAccess*/ | (FileAttributes)0x00040000 /*RecallOnOpen*/ | FileAttributes.Offline;

        public static List<CleanItem> LargeFiles(IProgress<string> p, string drive, long minSize = 100 * MB, int max = 40)
        {
            var found = new List<FileInfo>();
            bool sys = drive.Equals(Native.SysRoot, StringComparison.OrdinalIgnoreCase);
            var roots = new List<string>();
            if (sys) roots.Add(U);
            foreach (var d in Fs.Dirs(drive))
                if (!BigSkipDirs.Contains(d.Name) && !(sys && d.Name.Equals("Users", StringComparison.OrdinalIgnoreCase)) && !Fs.IsLink(d))
                    roots.Add(d.FullName.StartsWith(@"\\?\") ? d.FullName.Substring(4) : d.FullName);

            foreach (var r in roots) WalkBig(new DirectoryInfo(Fs.Long(r)), minSize, found, 0, p);

            var res = new List<CleanItem>();
            string prof = U.TrimEnd('\\');
            foreach (var f in found.OrderByDescending(x => x.Length).Take(max))
            {
                string full = f.FullName.StartsWith(@"\\?\") ? f.FullName.Substring(4) : f.FullName;
                string dir = Path.GetDirectoryName(full);
                if (dir.StartsWith(prof, StringComparison.OrdinalIgnoreCase)) dir = "Your folder" + dir.Substring(prof.Length);
                var it = Mk(CatBig, f.Name, "In " + dir + ".  Last changed " + f.LastWriteTime.ToString("yyyy-MM-dd") +
                    ".  Goes to the Recycle Bin, so you can restore it.", Safety.Review, false, full);
                it.Mode = Mode.Whole;
                it.ToRecycleBin = true;
                it.Size = f.Length;
                res.Add(it);
            }
            return res;
        }

        // File types that belong to installed games, apps and virtual machines.
        static readonly HashSet<string> AppDataExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pak", ".ucas", ".utoc", ".rpkg", ".minizip", ".bk2", ".bik", ".vpk", ".forge", ".big", ".bundle", ".assets",
            ".resource", ".resources", ".arc", ".wad", ".pck", ".cpk", ".rpf", ".archive", ".dat", ".bin", ".blob",
            ".dll", ".exe.config", ".sys", ".msp", ".cab", ".vhd", ".vhdx", ".avhdx", ".vmdk", ".vdi", ".qcow2", ".edb", ".db", ".sqlite"
        };

        static readonly HashSet<string> ProgramSubdirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Engine", "Binaries", "Content", "Data", "bin", "x64", "Win64", "Plugins", "runtime", "_CommonRedist", "Redist", "media" };

        // A folder with .dll files, or an .exe next to typical program folders, is an installed program.
        static bool LooksLikeProgram(FileSystemInfo[] es)
        {
            bool exe = false, dll = false, progDir = false;
            foreach (var e in es)
            {
                var f = e as FileInfo;
                if (f != null)
                {
                    if (f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) dll = true;
                    else if (f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) exe = true;
                }
                else if (ProgramSubdirs.Contains(e.Name)) progDir = true;
            }
            return dll || (exe && progDir);
        }

        static void WalkBig(DirectoryInfo d, long minSize, List<FileInfo> found, int depth, IProgress<string> p)
        {
            if (depth > 16) return;
            if (p != null && depth <= 1) p.Report("Looking for large files: " + d.FullName.Replace(@"\\?\", ""));
            FileSystemInfo[] es;
            try { es = d.GetFileSystemInfos(); } catch { return; }
            if (LooksLikeProgram(es)) return;   // installed game or app: its big files are not junk
            foreach (var e in es)
            {
                try
                {
                    if (Fs.IsLink(e)) continue;
                    var f = e as FileInfo;
                    if (f != null)
                    {
                        if (f.Length >= minSize && (f.Attributes & (CloudOnly | FileAttributes.System)) == 0
                            && !AppDataExt.Contains(f.Extension)) found.Add(f);
                    }
                    else if (!BigSkipDirs.Contains(e.Name)) WalkBig((DirectoryInfo)e, minSize, found, depth + 1, p);
                }
                catch { }
            }
        }

        // ================================================================ project build folders

        static readonly HashSet<string> JsCaches = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".next", ".nuxt", ".turbo", ".parcel-cache", ".svelte-kit", ".angular", ".vite" };

        class Junk { public DirectoryInfo Dir; public string Kind; }

        public static List<CleanItem> FindProjectJunk(string root, IProgress<string> p)
        {
            root = root.TrimEnd('\\');
            var found = new List<Junk>();
            var pycache = new List<DirectoryInfo>();
            WalkProjects(new DirectoryInfo(Fs.Long(root)), found, pycache, 0, p);

            var res = new List<CleanItem>();
            foreach (var j in found)
            {
                string full = j.Dir.FullName.StartsWith(@"\\?\") ? j.Dir.FullName.Substring(4) : j.Dir.FullName;
                string proj = Path.GetDirectoryName(full);
                string rel = proj.Length > root.Length ? proj.Substring(root.Length).TrimStart('\\') : Path.GetFileName(proj);
                DateTime changed = j.Dir.Parent.LastWriteTime;
                string hint; Safety s = Safety.Safe;
                switch (j.Kind)
                {
                    case "node_modules": hint = "Packages. Run npm install (or pnpm / yarn) to get them back."; s = Safety.Review; break;
                    case "js-cache": hint = "Build cache. Rebuilt on the next build or dev run."; break;
                    case "dotnet": hint = ".NET build output. Rebuilt on the next build."; break;
                    case "rust": hint = "Rust build output. Rebuilt by cargo build."; break;
                    case "gradle": hint = "Gradle build output. Rebuilt on the next build."; break;
                    case "venv": hint = "Python environment. Recreate with python -m venv and pip install."; s = Safety.Review; break;
                    default: hint = "Build output."; break;
                }
                var it = Mk(CatProj, rel + "  ›  " + j.Dir.Name,
                    hint + "  Project last changed " + changed.ToString("yyyy-MM-dd") + ".  Your code stays.", s, false, full);
                it.Mode = Mode.Whole;
                if (p != null) p.Report("Measuring: " + rel + "\\" + j.Dir.Name);
                it.Size = SizeOf(it);
                res.Add(it);
            }
            if (pycache.Count > 0)
            {
                var it = Mk(CatProj, Path.GetFileName(root) + "  ›  __pycache__ (" + pycache.Count + " folders)",
                    "Python bytecode caches. Rebuilt automatically.", Safety.Safe, false,
                    pycache.Select(d => d.FullName.StartsWith(@"\\?\") ? d.FullName.Substring(4) : d.FullName).ToArray());
                it.Mode = Mode.Whole;
                it.Size = SizeOf(it);
                res.Add(it);
            }
            return res;
        }

        static void WalkProjects(DirectoryInfo d, List<Junk> found, List<DirectoryInfo> pycache, int depth, IProgress<string> p)
        {
            if (depth > 12) return;
            if (p != null && depth <= 2) p.Report("Looking in: " + d.FullName.Replace(@"\\?\", ""));
            FileInfo[] files; DirectoryInfo[] subs;
            try { files = d.GetFiles(); subs = d.GetDirectories(); } catch { return; }
            var names = new HashSet<string>(files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            bool isNode = names.Contains("package.json");
            bool isDotnet = files.Any(f => f.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || f.Extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                || f.Extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase));
            bool isRust = names.Contains("Cargo.toml");
            bool isGradle = names.Contains("build.gradle") || names.Contains("build.gradle.kts");

            foreach (var s in subs)
            {
                try
                {
                    if (Fs.IsLink(s) || SkipDirs.Contains(s.Name)) continue;
                    string n = s.Name;
                    if (n.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) { found.Add(new Junk { Dir = s, Kind = "node_modules" }); continue; }
                    if (isNode && JsCaches.Contains(n)) { found.Add(new Junk { Dir = s, Kind = "js-cache" }); continue; }
                    if (isDotnet && (n.Equals("bin", StringComparison.OrdinalIgnoreCase) || n.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                    { found.Add(new Junk { Dir = s, Kind = "dotnet" }); continue; }
                    if (isRust && n.Equals("target", StringComparison.OrdinalIgnoreCase)) { found.Add(new Junk { Dir = s, Kind = "rust" }); continue; }
                    if (isGradle && n.Equals("build", StringComparison.OrdinalIgnoreCase)) { found.Add(new Junk { Dir = s, Kind = "gradle" }); continue; }
                    if (n.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)) { pycache.Add(s); continue; }
                    if (File.Exists(Path.Combine(s.FullName, "pyvenv.cfg"))) { found.Add(new Junk { Dir = s, Kind = "venv" }); continue; }
                    WalkProjects(s, found, pycache, depth + 1, p);
                }
                catch { }
            }
        }
    }
}
