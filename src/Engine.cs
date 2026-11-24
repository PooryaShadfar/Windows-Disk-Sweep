// Disk Sweep - engine: what to look for, how to measure it, how to delete it.
// Nothing here runs on its own: the UI only deletes items the user ticked and confirmed.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("Disk Sweep")]
[assembly: System.Reflection.AssemblyDescription("Find and remove junk files on Windows. You choose what goes.")]
[assembly: System.Reflection.AssemblyProduct("Disk Sweep")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 Poorya Shadfar. MIT License.")]
[assembly: System.Reflection.AssemblyVersion("1.5.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.5.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.5.0")]

namespace DiskSweep
{
    static class Util
    {
        public static string Fmt(long b)
        {
            if (b < 0) return "?";
            if (b >= 1L << 30) return (b / 1073741824.0).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / 1048576.0).ToString("0") + " MB";
            if (b >= 1024) return (b / 1024.0).ToString("0") + " KB";
            return b + " B";
        }
    }

    enum Safety { Safe, Review }
    enum Mode { Contents, Whole, RecycleBin, Command }

    class CleanItem
    {
        public string Category;
        public string Name;
        public string Info = "";
        public List<string> Paths = new List<string>();
        public Mode Mode = Mode.Contents;
        public Safety Safety = Safety.Safe;
        public bool NeedsAdmin;
        public long Size = -1;          // -1 = unknown / varies
        public string Pattern;          // top-level file pattern (Contents mode), null = everything
        public int MinAgeHours;         // skip files newer than this
        public string Cmd;
        public string CmdArgs;
        public string[] Processes = new string[0];
        public bool Checked;
        public string RunningText;      // apps that should be closed first
        public long CleanedBytes;       // filled in after cleaning
        public bool AlwaysRecycle;      // set by finders for personal files: always goes to the Recycle Bin
        public bool ToRecycleBin;
        public List<string> RecycledPaths;  // filled in after moving to the Recycle Bin (for the activity log)       // move to Recycle Bin instead of deleting (personal files)
        public string Key { get { return Category + "|" + Name; } }
    }

    // ------------------------------------------------------------------ native helpers
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint flags);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        // root = "D:\" for one drive's Recycle Bin, null for all drives
        public static long RecycleSize(string root = null)
        {
            var i = new SHQUERYRBINFO();
            i.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
            return SHQueryRecycleBin(root, ref i) == 0 ? i.i64Size : 0;
        }

        public static void EmptyRecycle(string root = null) { SHEmptyRecycleBin(IntPtr.Zero, root, 1 | 2 | 4); }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        // Moves files/folders to the Recycle Bin. Windows asks first if something is too big for the bin
        // (it would otherwise be deleted for good). True if the operation ran.
        public static bool Recycle(IEnumerable<string> paths)
        {
            var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (list.Count == 0) return true;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = 3,                                            // FO_DELETE
                pFrom = string.Join("\0", list) + "\0",               // list ends with a double null
                fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400 | 0x4000   // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI | WANTNUKEWARNING
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }

        public static string SysRoot { get { return Path.GetPathRoot(Environment.SystemDirectory); } }

        public static long FreeBytes(string root = null) { try { return new DriveInfo(root ?? SysRoot).AvailableFreeSpace; } catch { return 0; } }
        public static long TotalBytes(string root = null) { try { return new DriveInfo(root ?? SysRoot).TotalSize; } catch { return 0; } }

        public static bool IsRunning(string name)
        {
            try
            {
                var p = Process.GetProcessesByName(name);
                bool r = p.Length > 0;
                foreach (var x in p) x.Dispose();
                return r;
            }
            catch { return false; }
        }

        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ file system
    static class Fs
    {
        // \\?\ prefix: allows paths longer than 260 chars (deep node_modules).
        public static string Long(string p)
        {
            if (p.StartsWith(@"\\?\")) return p;
            if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }

        public static bool IsLink(FileSystemInfo i) { return (i.Attributes & FileAttributes.ReparsePoint) != 0; }

        // Last time a file was written or (when Windows tracks it) opened.
        public static DateTime Recent(FileInfo f) { return f.LastAccessTime > f.LastWriteTime ? f.LastAccessTime : f.LastWriteTime; }

        public static bool Exists(string p)
        {
            try { var lp = Long(p); return Directory.Exists(lp) || File.Exists(lp); } catch { return false; }
        }

        static FileSystemInfo[] Entries(DirectoryInfo d)
        {
            try { return d.GetFileSystemInfos(); } catch { return new FileSystemInfo[0]; }
        }

        static FileInfo[] Files(DirectoryInfo d, string pattern)
        {
            try { return d.GetFiles(pattern); } catch { return new FileInfo[0]; }
        }

        public static DirectoryInfo[] Dirs(string path)
        {
            try { return new DirectoryInfo(Long(path)).GetDirectories(); } catch { return new DirectoryInfo[0]; }
        }

        // Size of a tree. Never follows junctions/symlinks (pnpm, worktrees...).
        public static long TreeSize(DirectoryInfo d, DateTime cutoff)
        {
            long t = 0;
            foreach (var e in Entries(d))
            {
                try
                {
                    if (IsLink(e)) continue;
                    var f = e as FileInfo;
                    if (f != null) { if (Recent(f) < cutoff) t += f.Length; }
                    else t += TreeSize((DirectoryInfo)e, cutoff);
                }
                catch { }
            }
            return t;
        }

        // Deletes a tree. Junctions/symlinks are unlinked, their targets are never touched.
        // Files in use are skipped. Returns bytes actually deleted.
        public static long DeleteTree(DirectoryInfo d, DateTime cutoff, bool removeSelf)
        {
            long freed = 0;
            foreach (var e in Entries(d))
            {
                try
                {
                    if (IsLink(e))
                    {
                        if (e is DirectoryInfo) Directory.Delete(e.FullName, false); else e.Delete();
                        continue;
                    }
                    var f = e as FileInfo;
                    if (f != null)
                    {
                        if (Recent(f) >= cutoff) continue;
                        long len = f.Length;
                        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes = FileAttributes.Normal;
                        f.Delete();
                        freed += len;
                    }
                    else freed += DeleteTree((DirectoryInfo)e, cutoff, true);
                }
                catch { }
            }
            if (removeSelf)
            {
                try { d.Attributes = FileAttributes.Directory; d.Delete(false); } catch { }
            }
            return freed;
        }

        static DateTime Cutoff(int minAgeHours)
        {
            return minAgeHours > 0 ? DateTime.Now.AddHours(-minAgeHours) : DateTime.MaxValue;
        }

        public static long PathSize(string path, string pattern, int minAgeHours, bool whole)
        {
            try
            {
                string lp = Long(path);
                if (File.Exists(lp)) return new FileInfo(lp).Length;
                if (!Directory.Exists(lp)) return 0;
                var d = new DirectoryInfo(lp);
                if (whole && IsLink(d)) return 0;
                if (pattern != null)
                {
                    long t = 0;
                    foreach (var f in Files(d, pattern)) { try { t += f.Length; } catch { } }
                    return t;
                }
                return TreeSize(d, Cutoff(minAgeHours));
            }
            catch { return 0; }
        }

        public static long DeletePath(string path, string pattern, int minAgeHours, bool whole)
        {
            try
            {
                string lp = Long(path);
                if (File.Exists(lp))
                {
                    var fi = new FileInfo(lp);
                    long len = fi.Length;
                    fi.Attributes = FileAttributes.Normal;
                    fi.Delete();
                    return len;
                }
                if (!Directory.Exists(lp)) return 0;
                var d = new DirectoryInfo(lp);
                if (whole && IsLink(d)) { Directory.Delete(lp, false); return 0; }
                if (pattern != null)
                {
                    long t = 0;
                    foreach (var f in Files(d, pattern))
                    {
                        try { long len = f.Length; f.Delete(); t += len; } catch { }
                    }
                    return t;
                }
                return DeleteTree(d, Cutoff(minAgeHours), whole);
            }
            catch { return 0; }
        }
    }

    // ------------------------------------------------------------------ what to look for
    static partial class Scanner
    {
        public static bool IsAdmin;

        static string L { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string R { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        static string U { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        static string W { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
        static string PD { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }
        static string LL { get { return Path.Combine(U, "AppData", "LocalLow"); } }

        static string P(params string[] parts) { return Path.Combine(parts); }

        static CleanItem Mk(string cat, string name, string info, Safety s, bool admin, params string[] paths)
        {
            var it = new CleanItem { Category = cat, Name = name, Info = info, Safety = s, NeedsAdmin = admin };
            it.Paths.AddRange(paths.Where(p => !string.IsNullOrEmpty(p)).Select(p => p.StartsWith(@"\\?\") ? p.Substring(4) : p));
            return it;
        }

        static void Add(List<CleanItem> l, CleanItem it, params string[] procs)
        {
            it.Processes = procs;
            if (it.Paths.Any(Fs.Exists)) l.Add(it);
        }

        public const string CatWin = "Windows & temp files";
        public const string CatGpu = "Graphics caches";
        public const string CatDev = "Developer caches";
        public const string CatWeb = "Browser caches";
        public const string CatApp = "App caches";
        public const string CatOld = "Old program versions";

        public static List<CleanItem> Build()
        {
            var l = new List<CleanItem>();
            string root = Native.SysRoot;

            // --- Windows
            l.Add(RecycleItem(root));

            var tmp = Mk(CatWin, "Your temporary files", "Leftovers from installers and apps. Files changed in the last 24 h are kept.",
                Safety.Safe, false, Path.GetTempPath().TrimEnd('\\'));
            tmp.MinAgeHours = 24; Add(l, tmp);

            var wtmp = Mk(CatWin, "Windows temporary files", "System temp folder. Files changed in the last 24 h are kept.",
                Safety.Safe, true, P(W, "Temp"));
            wtmp.MinAgeHours = 24; Add(l, wtmp);

            Add(l, Mk(CatWin, "Windows Update downloads", "Already-installed update packages.", Safety.Safe, true,
                P(W, "SoftwareDistribution", "Download")));
            Add(l, Mk(CatWin, "Delivery Optimization cache", "Update pieces Windows shares with other PCs.", Safety.Safe, true,
                P(W, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")));
            Add(l, Mk(CatWin, "Crash dumps (yours)", "Memory dumps from crashed apps.", Safety.Safe, false, P(L, "CrashDumps")));
            Add(l, Mk(CatWin, "Crash dumps (system)", "Memory dumps from Windows crashes (blue screens).", Safety.Safe, true,
                P(W, "Minidump"), P(W, "MEMORY.DMP"), P(W, "LiveKernelReports")));
            Add(l, Mk(CatWin, "Error reports", "Windows Error Reporting files.", Safety.Safe, false, P(L, "Microsoft", "Windows", "WER")));
            Add(l, Mk(CatWin, "Error reports (system)", "Windows Error Reporting files for all users.", Safety.Safe, true,
                P(PD, "Microsoft", "Windows", "WER", "ReportArchive"), P(PD, "Microsoft", "Windows", "WER", "ReportQueue")));
            var thumbs = Mk(CatWin, "Thumbnail cache", "Picture previews in Explorer. Rebuilt when you open folders. Some files may be in use.",
                Safety.Safe, false, P(L, "Microsoft", "Windows", "Explorer"));
            thumbs.Pattern = "thumbcache_*.db"; Add(l, thumbs);

            string hib = P(root, "hiberfil.sys");
            if (Fs.Exists(hib))
            {
                var h = Mk(CatWin, "Hibernation file", "Turns off hibernation and Fast Startup. Boot may be a bit slower. Undo any time: powercfg /h on",
                    Safety.Review, true, hib);
                h.Mode = Mode.Command; h.Cmd = P(Environment.SystemDirectory, "powercfg.exe"); h.CmdArgs = "/hibernate off";
                l.Add(h);
            }
            var dism = Mk(CatWin, "Old Windows component versions (WinSxS)", "Runs Windows' own cleanup (DISM). Takes a few minutes. Size is only known afterwards.",
                Safety.Safe, true);
            dism.Mode = Mode.Command; dism.Cmd = P(Environment.SystemDirectory, "Dism.exe"); dism.CmdArgs = "/Online /Cleanup-Image /StartComponentCleanup";
            l.Add(dism);

            // --- driver installers that unpack themselves and never clean up
            Add(l, Mk(CatGpu, "AMD installer files", "Unpacked AMD driver and chipset installers. AMD Software keeps them after installing. Not needed any more.",
                Safety.Safe, false, P(root, "AMD")));
            Add(l, Mk(CatGpu, "NVIDIA installer files", "Unpacked NVIDIA driver installers left on the drive.", Safety.Safe, false, P(root, "NVIDIA")));
            Add(l, Mk(CatGpu, "Intel installer files", "Unpacked Intel driver installers and their logs.", Safety.Review, false, P(root, "Intel")));

            // --- GPU
            Add(l, Mk(CatGpu, "NVIDIA shader cache", "Saved game graphics data. Games may stutter briefly the first time after cleaning.", Safety.Safe, false,
                P(L, "NVIDIA", "DXCache"), P(L, "NVIDIA", "GLCache"), P(LL, "NVIDIA", "PerDriverVersion", "DXCache"), P(LL, "NVIDIA", "PerDriverVersion", "GLCache")));
            Add(l, Mk(CatGpu, "AMD shader cache", "Saved game graphics data. Games may stutter briefly the first time after cleaning.", Safety.Safe, false,
                P(L, "AMD", "DxCache"), P(L, "AMD", "DxcCache"), P(L, "AMD", "GLCache"), P(L, "AMD", "VkCache")));
            Add(l, Mk(CatGpu, "Intel shader cache", "Saved graphics data.", Safety.Safe, false, P(L, "Intel", "ShaderCache"), P(LL, "Intel", "ShaderCache")));
            Add(l, Mk(CatGpu, "DirectX shader cache", "Saved graphics data. Rebuilds automatically.", Safety.Safe, false, P(L, "D3DSCache")));
            Add(l, Mk(CatGpu, "Old NVIDIA driver downloads", "Driver installers the NVIDIA App already used.", Safety.Review, true,
                P(PD, "NVIDIA Corporation", "NVIDIA App", "UpdateFramework", "ota-artifacts"), P(PD, "NVIDIA Corporation", "Downloader")));

            // --- developer
            // Package caches: keep what was downloaded or used in the last N days (Settings).
            int keepDays = Settings.CacheDays;
            string keepNote = keepDays > 0 ? "  Keeps packages used in the last " + keepDays + " days." : "";
            var pkg = new[]
            {
                Mk(CatDev, "npm cache", "Downloaded npm packages. Next install downloads them again." + keepNote, Safety.Safe, false,
                    P(L, "npm-cache", "_cacache"), P(L, "npm-cache", "_npx")),
                Mk(CatDev, "pnpm store", "Shared pnpm package store. Existing projects keep working." + keepNote, Safety.Safe, false,
                    P(L, "pnpm", "store"), P(L, "pnpm-cache")),
                Mk(CatDev, "Yarn cache", "Downloaded Yarn packages." + keepNote, Safety.Safe, false, P(L, "Yarn", "Cache")),
                Mk(CatDev, "Bun cache", "Downloaded Bun packages." + keepNote, Safety.Safe, false, P(U, ".bun", "install", "cache"))
            };
            foreach (var it in pkg)
            {
                it.MinAgeHours = keepDays * 24;
                Add(l, it, "node");
            }
            Add(l, Mk(CatDev, "pip cache", "Downloaded Python packages.", Safety.Safe, false, P(L, "pip", "Cache")));
            Add(l, Mk(CatDev, "NuGet HTTP cache", "Downloaded .NET package data.", Safety.Safe, false,
                P(L, "NuGet", "v3-cache"), P(L, "NuGet", "plugins-cache"), P(L, "NuGet", "Cache")));
            Add(l, Mk(CatDev, "NuGet packages folder", ".NET packages. Restored again on next build (download needed).", Safety.Review, false,
                P(U, ".nuget", "packages")));
            Add(l, Mk(CatDev, "Gradle caches", "Android/Java build caches. Next build downloads again.", Safety.Review, false, P(U, ".gradle", "caches")));
            Add(l, Mk(CatDev, "Go build cache", "Compiled Go build outputs.", Safety.Safe, false, P(L, "go-build")));
            Add(l, Mk(CatDev, "Cargo download cache", "Downloaded Rust crates.", Safety.Safe, false, P(U, ".cargo", "registry", "cache")));
            Add(l, Mk(CatDev, "Playwright browsers", "Test browsers. Re-download with: npx playwright install", Safety.Review, false, P(L, "ms-playwright")));

            // --- browsers
            Chromium(l, "Google Chrome", P(L, "Google", "Chrome", "User Data"), "chrome");
            Chromium(l, "Microsoft Edge", P(L, "Microsoft", "Edge", "User Data"), "msedge");
            Chromium(l, "Brave", P(L, "BraveSoftware", "Brave-Browser", "User Data"), "brave");
            Chromium(l, "Vivaldi", P(L, "Vivaldi", "User Data"), "vivaldi");
            var opera = new List<string>();
            foreach (var d in Fs.Dirs(P(L, "Opera Software")))
            {
                opera.Add(P(d.FullName, "Cache")); opera.Add(P(d.FullName, "Code Cache")); opera.Add(P(d.FullName, "GPUCache"));
            }
            foreach (var d in Fs.Dirs(P(R, "Opera Software")))
            {
                opera.Add(P(d.FullName, "Code Cache")); opera.Add(P(d.FullName, "GPUCache")); opera.Add(P(d.FullName, "ShaderCache"));
            }
            Add(l, Mk(CatWeb, "Opera cache", "Web page cache. You stay logged in.", Safety.Safe, false, opera.ToArray()), "opera");
            var ff = new List<string>();
            foreach (var d in Fs.Dirs(P(L, "Mozilla", "Firefox", "Profiles"))) ff.Add(P(d.FullName, "cache2"));
            Add(l, Mk(CatWeb, "Firefox cache", "Web page cache. You stay logged in.", Safety.Safe, false, ff.ToArray()), "firefox");

            // --- apps
            Electron(l, "VS Code", P(R, "Code"), "Code");
            Electron(l, "Cursor", P(R, "Cursor"), "Cursor");
            Electron(l, "Windsurf", P(R, "Windsurf"), "Windsurf");
            Electron(l, "Discord", P(R, "discord"), "Discord");
            Electron(l, "Slack", P(R, "Slack"), "slack");
            Electron(l, "Figma", P(R, "Figma"), "Figma");
            Electron(l, "Postman", P(R, "Postman"), "Postman");
            Electron(l, "ClickUp", P(R, "ClickUp"), "ClickUp");
            Add(l, Mk(CatApp, "Spotify cache", "Cached songs. They stream again when played.", Safety.Safe, false,
                P(L, "Spotify", "Storage"), P(L, "Spotify", "Data")), "Spotify");
            Add(l, Mk(CatApp, "Steam web cache", "Store page cache.", Safety.Safe, false, P(L, "Steam", "htmlcache")), "steam");
            Add(l, Mk(CatApp, "Telegram cache", "Downloaded media cache. Chats stay.", Safety.Safe, false,
                P(R, "Telegram Desktop", "tdata", "user_data"), P(R, "Telegram Desktop", "tdata", "emoji")), "Telegram");

            // --- old versions
            OldJetBrains(l);

            return l;
        }

        static void Chromium(List<CleanItem> l, string name, string userData, string proc)
        {
            if (!Fs.Exists(userData)) return;
            var paths = new List<string>();
            foreach (var prof in Fs.Dirs(userData))
            {
                if (!File.Exists(P(prof.FullName, "Preferences"))) continue;
                paths.Add(P(prof.FullName, "Cache"));
                paths.Add(P(prof.FullName, "Code Cache"));
                paths.Add(P(prof.FullName, "GPUCache"));
            }
            paths.Add(P(userData, "ShaderCache"));
            paths.Add(P(userData, "GrShaderCache"));
            paths.Add(P(userData, "GraphiteDawnCache"));
            Add(l, Mk(CatWeb, name + " cache", "Web page cache. You stay logged in. Close the browser first for best results.",
                Safety.Safe, false, paths.ToArray()), proc);
        }

        static void Electron(List<CleanItem> l, string name, string dir, string proc)
        {
            if (!Fs.Exists(dir)) return;
            Add(l, Mk(CatApp, name + " cache", "App cache. Rebuilds automatically. Settings and data stay.", Safety.Safe, false,
                P(dir, "Cache"), P(dir, "Code Cache"), P(dir, "GPUCache"), P(dir, "CachedData"), P(dir, "CachedExtensionVSIXs"),
                P(dir, "DawnCache"), P(dir, "DawnGraphiteCache"), P(dir, "DawnWebGPUCache")), proc);
        }

        // JetBrains keeps one folder per version (WebStorm2025.3, WebStorm2026.1, ...). Keep the newest.
        static void OldJetBrains(List<CleanItem> l)
        {
            var rx = new Regex(@"^([A-Za-z]+)(\d{4})\.(\d+)$");
            var found = new List<Tuple<string, int, DirectoryInfo, bool>>(); // product, version, dir, isRoaming
            foreach (var b in new[] { Tuple.Create(P(L, "JetBrains"), false), Tuple.Create(P(R, "JetBrains"), true) })
            {
                foreach (var d in Fs.Dirs(b.Item1))
                {
                    var m = rx.Match(d.Name);
                    if (!m.Success) continue;
                    int ver = int.Parse(m.Groups[2].Value) * 100 + int.Parse(m.Groups[3].Value);
                    found.Add(Tuple.Create(m.Groups[1].Value, ver, d, b.Item2));
                }
            }
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var g in found.GroupBy(f => f.Item1))
            {
                int newest = g.Max(f => f.Item2);
                foreach (var f in g.Where(x => x.Item2 != newest))
                {
                    string ver = (f.Item2 / 100) + "." + (f.Item2 % 100);
                    // Still installed? Then leave it alone.
                    bool installed = Fs.Dirs(P(pf, "JetBrains")).Any(d => d.Name.StartsWith(f.Item1 + " " + ver, StringComparison.OrdinalIgnoreCase));
                    if (installed) continue;
                    var it = Mk(CatOld, f.Item1 + " " + ver + (f.Item4 ? " - settings & plugins" : " - caches & logs"),
                        "You use a newer version (" + (newest / 100) + "." + (newest % 100) + "). Its settings were already imported.",
                        f.Item4 ? Safety.Review : Safety.Safe, false, f.Item3.FullName.Replace(@"\\?\", ""));
                    it.Mode = Mode.Whole;
                    Add(l, it);
                }
            }
        }

        public static long SizeOf(CleanItem it)
        {
            if (it.Mode == Mode.RecycleBin) return Native.RecycleSize(it.Paths.Count > 0 ? it.Paths[0] : null);
            if (it.Mode == Mode.Command && it.Paths.Count == 0) return -1;
            long t = 0;
            foreach (var p in it.Paths) t += Fs.PathSize(p, it.Pattern, it.MinAgeHours, it.Mode == Mode.Whole);
            // Without admin rights we often can't look inside system folders.
            if (t == 0 && it.NeedsAdmin && !IsAdmin) return -1;
            return t;
        }

        public static CleanItem RecycleItem(string root)
        {
            var rb = new CleanItem { Category = CatWin, Name = "Recycle Bin", Info = "Files you already deleted.", Mode = Mode.RecycleBin };
            rb.Paths.Add(root);
            return rb;
        }

        // drive = "C:\" etc. The system drive gets everything; other drives get their Recycle Bin and large files.
        public static List<CleanItem> ScanAll(IProgress<string> p, string drive = null)
        {
            drive = drive ?? Native.SysRoot;
            bool sys = drive.Equals(Native.SysRoot, StringComparison.OrdinalIgnoreCase);
            if (!sys)
            {
                var other = new List<CleanItem> { RecycleItem(drive) };
                other[0].Size = SizeOf(other[0]);
                other.AddRange(LargeFiles(p, drive));
                return other;
            }
            var list = Build();
            SystemLeftovers(list);
            if (p != null) p.Report("Looking for game launchers…");
            GameCaches(list);
            foreach (var it in list)
            {
                if (p != null) p.Report("Measuring: " + it.Name);
                if (it.Size < 0 || it.Category != CatGame) it.Size = SizeOf(it);
            }
            Leftovers(list, p);
            Virtualization(list);
            list.AddRange(LargeFiles(p, drive));
            return list;
        }

        // ---- folders never walked by the project / large-file finders
        static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData",
            "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Recovery"
        };
    }

    // ------------------------------------------------------------------ deleting
    static class Cleaner
    {
        public static long Clean(CleanItem it)
        {
            switch (it.Mode)
            {
                case Mode.RecycleBin:
                    {
                        string root = it.Paths.Count > 0 ? it.Paths[0] : null;
                        long before = Native.RecycleSize(root);
                        Native.EmptyRecycle(root);
                        return Math.Max(0, before - Native.RecycleSize(root));
                    }
                case Mode.Command:
                    {
                        string drive = it.Paths.Count > 0 ? Path.GetPathRoot(it.Paths[0]) : null;
                        long before = Native.FreeBytes(drive);
                        if (it.Cmd == "sagerun") SystemCommands.SageRun(it.CmdArgs);
                        else if (it.Cmd == "compact-vhdx") SystemCommands.CompactVhdx(it.CmdArgs);
                        else if (it.Cmd == "vss-old") SystemSpace.DeleteOld(it.CmdArgs);
                        else RunHidden(it.Cmd, it.CmdArgs);
                        return Math.Max(0, Native.FreeBytes(drive) - before);
                    }
                default:
                    {
                        long freed = 0;
                        if (it.ToRecycleBin)
                        {
                            // Whole: move the folder/file itself. Contents: move what's inside, keep the folder.
                            var targets = new List<string>();
                            foreach (var p in it.Paths)
                            {
                                if (!Fs.Exists(p)) continue;
                                if (it.Mode == Mode.Whole || File.Exists(Fs.Long(p))) { targets.Add(p); continue; }
                                try
                                {
                                    var d = new DirectoryInfo(p);
                                    targets.AddRange((it.Pattern != null ? d.GetFiles(it.Pattern) : d.GetFileSystemInfos()).Select(e => e.FullName));
                                }
                                catch { }
                            }
                            long before = SizeOfPaths(it);
                            it.RecycledPaths = targets;
                            Native.Recycle(targets);
                            return Math.Max(0, before - SizeOfPaths(it));
                        }
                        foreach (var p in it.Paths) freed += Fs.DeletePath(p, it.Pattern, it.MinAgeHours, it.Mode == Mode.Whole);
                        return freed;
                    }
            }
        }

        static long SizeOfPaths(CleanItem it)
        {
            long t = 0;
            foreach (var p in it.Paths) t += Fs.PathSize(p, it.Pattern, it.MinAgeHours, it.Mode == Mode.Whole);
            return t;
        }

        static void RunHidden(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var pr = Process.Start(psi))
            {
                pr.OutputDataReceived += (s, e) => { };
                pr.ErrorDataReceived += (s, e) => { };
                pr.BeginOutputReadLine(); pr.BeginErrorReadLine();
                pr.WaitForExit();
            }
        }
    }

    class FileLog : IProgress<string>
    {
        readonly string file; readonly Stopwatch sw = Stopwatch.StartNew();
        public FileLog(string f) { file = f; File.WriteAllText(f, ""); }
        public void Report(string s) { File.AppendAllText(file, sw.Elapsed.TotalSeconds.ToString("0.0") + "s  " + s + "\n"); }
    }

    static class Dev
    {
        public static int Report(string[] args)
        {
            IProgress<string> log = null;
            if (args.Length >= 3 && args[2] == "--log") log = new FileLog(args[1] + ".log");
            Scanner.IsAdmin = Native.IsAdmin();
            var sw = Stopwatch.StartNew();
            var list = Scanner.ScanAll(log);
            if (args.Length >= 3 && args[2] != "--log") list.AddRange(Scanner.FindProjectJunk(args[2], null));
            var sb = new StringBuilder();
            sb.AppendLine("admin=" + Scanner.IsAdmin + " seconds=" + sw.Elapsed.TotalSeconds.ToString("0"));
            foreach (var it in list)
                sb.AppendLine(string.Join("\t", it.Category, it.Name, Util.Fmt(it.Size), it.Safety.ToString(),
                    it.NeedsAdmin ? "admin" : "-", string.Join(" | ", it.Paths.Where(Fs.Exists))));
            File.WriteAllText(args[1], sb.ToString());
            return 0;
        }

        public static int SelfTest(string dir)
        {
            var log = new StringBuilder();
            bool ok = true;
            try
            {
                string fx = Path.Combine(dir, "fixture");
                string keep = Path.Combine(dir, "keep-target");
                Directory.CreateDirectory(keep);
                File.WriteAllText(Path.Combine(keep, "important.txt"), "must survive");

                // deep path > 260 chars
                string deep = fx;
                for (int i = 0; i < 12; i++) deep = Path.Combine(deep, "node_modules_level_" + i + "_xxxxxxxxxx");
                Directory.CreateDirectory(Fs.Long(deep));
                File.WriteAllText(Fs.Long(Path.Combine(deep, "deep.txt")), new string('a', 1000));
                log.AppendLine("deep path length: " + deep.Length);

                string ro = Path.Combine(fx, "readonly.txt");
                File.WriteAllText(ro, new string('b', 2000));
                File.SetAttributes(ro, FileAttributes.ReadOnly);

                // junction pointing outside the fixture
                var mk = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + Path.Combine(fx, "link") + "\" \"" + keep + "\"")
                { CreateNoWindow = true, UseShellExecute = false });
                mk.WaitForExit();

                var probe = new CleanItem { Mode = Mode.Whole };
                probe.Paths.Add(fx);
                long size = Scanner.SizeOf(probe);
                log.AppendLine("measured: " + size + " (expect 3000, junction not followed)");
                if (size != 3000) ok = false;

                long freed = Cleaner.Clean(probe);
                log.AppendLine("freed: " + freed);
                bool fxGone = !Directory.Exists(Fs.Long(fx));
                bool keepOk = File.Exists(Path.Combine(keep, "important.txt"));
                log.AppendLine("fixture removed: " + fxGone);
                log.AppendLine("junction target intact: " + keepOk);
                if (!fxGone || !keepOk || freed != 3000) ok = false;

                // Recycle Bin, "Contents" mode: files go to the bin, the folder itself stays.
                string rc = Path.Combine(dir, "recycle-contents");
                Directory.CreateDirectory(Path.Combine(rc, "ds-selftest-sub"));
                File.WriteAllText(Path.Combine(rc, "ds-selftest-a.txt"), new string('c', 4000));
                File.WriteAllText(Path.Combine(rc, "ds-selftest-sub", "b.txt"), new string('d', 1000));
                var rItem = new CleanItem { Mode = Mode.Contents, ToRecycleBin = true };
                rItem.Paths.Add(rc);
                long moved = Cleaner.Clean(rItem);
                bool folderKept = Directory.Exists(rc);
                bool emptied = folderKept && Directory.GetFileSystemEntries(rc).Length == 0;
                log.AppendLine("recycled (contents): " + moved + " (expect 5000), folder kept: " + folderKept + ", emptied: " + emptied);
                if (moved != 5000 || !folderKept || !emptied) ok = false;
            }
            catch (Exception ex) { ok = false; log.AppendLine("ERROR: " + ex); }
            try
            {
                // Age filter: files used recently are kept.
                string ac = Path.Combine(dir, "age-cache");
                Directory.CreateDirectory(ac);
                string oldF = Path.Combine(ac, "old.bin"), newF = Path.Combine(ac, "new.bin");
                File.WriteAllText(oldF, new string('o', 3000)); File.WriteAllText(newF, new string('n', 2000));
                var past = DateTime.Now.AddDays(-60);
                File.SetLastWriteTime(oldF, past); File.SetLastAccessTime(oldF, past);
                var aItem = new CleanItem { Mode = Mode.Contents, MinAgeHours = 30 * 24 };
                aItem.Paths.Add(ac);
                long measured = Scanner.SizeOf(aItem);
                long removed = Cleaner.Clean(aItem);
                bool keptNew = File.Exists(newF), goneOld = !File.Exists(oldF);
                log.AppendLine("age filter: measured " + measured + " (expect 3000), removed " + removed + ", new kept: " + keptNew + ", old removed: " + goneOld);
                if (measured != 3000 || removed != 3000 || !keptNew || !goneOld) ok = false;
            }
            catch (Exception ex) { ok = false; log.AppendLine("age ERROR: " + ex.Message); }            try
            {
                // Recycle Bin restore through the activity log
                Activity.Muted = true;
                string rf = Path.Combine(dir, "ds-restore-test.txt");
                File.WriteAllText(rf, "restore me");
                var ri = new CleanItem { Name = "ds-restore-test", Mode = Mode.Whole, ToRecycleBin = true };
                ri.Paths.Add(rf);
                Cleaner.Clean(ri);
                var entry = new ActivityEntry { Kind = "recycled", Name = ri.Name, Paths = ri.RecycledPaths };
                bool gone = !File.Exists(rf);
                int can = Activity.Restorable(entry);
                int back = Activity.Restore(entry);
                bool restored = File.Exists(rf) && File.ReadAllText(rf) == "restore me";
                log.AppendLine("restore: recycled " + gone + ", restorable " + can + ", restored " + back + ", content ok " + restored);
                if (!gone || can != 1 || back != 1 || !restored) ok = false;

                // What grew: two made-up snapshots for drive Z:
                string snaps = Path.Combine(Settings.Dir, "snapshots");
                Directory.CreateDirectory(snaps);
                string s1 = Path.Combine(snaps, "Z_20200101-1200.tsv"), s2 = Path.Combine(snaps, "Z_20200110-1200.tsv");
                File.WriteAllText(s1, "#total\t10000000000\t0\nZ:\\proj\t1000000000\nZ:\\proj\\node_modules\t500000000\nZ:\\games\t3000000000\n");
                File.WriteAllText(s2, "#total\t13000000000\t0\nZ:\\proj\t3600000000\nZ:\\proj\\node_modules\t3000000000\nZ:\\games\t3000000000\nZ:\\video\t400000000\n");
                DateTime? since; long total;
                var rows = Growth.Compare("Z:\\", 7, out since, out total);
                File.Delete(s1); File.Delete(s2);
                bool g = rows.Count == 2 && rows[0].Path == "Z:\\proj\\node_modules" && rows[0].Change == 2500000000
                         && rows[1].Path == "Z:\\video" && total == 3000000000;
                log.AppendLine("growth: " + string.Join(", ", rows.Select(r => r.Path + " +" + r.Change)) + " total +" + total + " -> " + (g ? "ok" : "FAIL"));
                if (!g) ok = false;

                // Auto-clean only touches Safe, direct-delete items
                var safe = new CleanItem { Category = Scanner.CatDev, Name = "t1", Safety = Safety.Safe, Mode = Mode.Contents, Size = 5 };
                var review = new CleanItem { Category = Scanner.CatDev, Name = "t2", Safety = Safety.Review, Mode = Mode.Contents, Size = 5 };
                var bin = new CleanItem { Category = Scanner.CatWin, Name = "t3", Safety = Safety.Safe, Mode = Mode.RecycleBin, Size = 5 };
                var big = new CleanItem { Category = Scanner.CatBig, Name = "t4", Safety = Safety.Safe, Mode = Mode.Whole, Size = 5 };
                var adm = new CleanItem { Category = Scanner.CatWin, Name = "t5", Safety = Safety.Safe, Mode = Mode.Contents, Size = 5, NeedsAdmin = true };
                bool a = AutoClean.Eligible(safe) && !AutoClean.Eligible(review) && !AutoClean.Eligible(bin) && !AutoClean.Eligible(big) && !AutoClean.Eligible(adm);
                log.AppendLine("auto-clean rules -> " + (a ? "ok" : "FAIL"));
                if (!a) ok = false;
            }
            catch (Exception ex) { ok = false; log.AppendLine("1.4 ERROR: " + ex); }
            try { if (!Health.SelfTest(log)) ok = false; } catch (Exception ex) { ok = false; log.AppendLine("health ERROR: " + ex.Message); }
            log.AppendLine(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
            File.WriteAllText(Path.Combine(dir, "selftest.txt"), log.ToString());
            return ok ? 0 : 1;
        }
    }
}
