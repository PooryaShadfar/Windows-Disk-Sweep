// Disk Sweep - finders added in 1.3:
//   - game launcher caches (Steam shader cache per game, Steam leftovers, Epic, Battle.net, EA, Ubisoft, GOG)
//   - Windows.old and Windows setup leftovers (via Windows' own Disk Cleanup handlers)
//   - WSL and Docker virtual disks (compact them; nothing inside is deleted)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DiskSweep
{
    static partial class Scanner
    {
        public const string CatGame = "Game caches";
        public const string CatVirt = "WSL & Docker";

        // ================================================================ games
        static List<string> SteamLibraries()
        {
            var libs = new List<string>();
            try
            {
                string steam = null;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    if (k != null) steam = k.GetValue("SteamPath") as string;
                if (string.IsNullOrEmpty(steam)) return libs;
                steam = steam.Replace('/', '\\');
                libs.Add(steam);
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch { }
            return libs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string SteamGameName(string lib, string appId)
        {
            try
            {
                string acf = Path.Combine(lib, "steamapps", "appmanifest_" + appId + ".acf");
                if (!File.Exists(acf)) return null;
                var m = Regex.Match(File.ReadAllText(acf), "\"name\"\\s+\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        public static void GameCaches(List<CleanItem> l)
        {
            foreach (var lib in SteamLibraries())
            {
                string drive = Path.GetPathRoot(lib).TrimEnd('\\').ToUpperInvariant();
                // Shader cache: one item per game that has a big cache, the rest grouped.
                var rest = new List<string>();
                foreach (var d in Fs.Dirs(Path.Combine(lib, "steamapps", "shadercache")))
                {
                    string path = d.FullName.StartsWith(@"\\?\") ? d.FullName.Substring(4) : d.FullName;
                    long size = Fs.PathSize(path, null, 0, true);
                    string name = SteamGameName(lib, d.Name);
                    if (size >= 50 * MB && name != null)
                    {
                        var it = Mk(CatGame, "Steam shader cache: " + name, "Saved graphics data for this game. Rebuilt when you play. The first minutes may stutter a little.",
                            Safety.Safe, false, path);
                        it.Mode = Mode.Whole; it.Size = size; it.Processes = new[] { "steam" };
                        l.Add(it);
                    }
                    else rest.Add(path);
                }
                if (rest.Count > 0)
                {
                    var it = Mk(CatGame, "Steam shader cache (" + drive + ", other games)", "Saved graphics data. Rebuilt when you play.", Safety.Safe, false, rest.ToArray());
                    it.Mode = Mode.Whole; it.Processes = new[] { "steam" };
                    Add(l, it, "steam");
                }
                Add(l, Mk(CatGame, "Steam temporary files (" + drive + ")", "Left over from game updates.", Safety.Safe, false,
                    Path.Combine(lib, "steamapps", "temp")), "steam");
                var dl = Mk(CatGame, "Old Steam downloads (" + drive + ")", "Unfinished or leftover game downloads not touched for 7 days. A paused download restarts.",
                    Safety.Review, false, Path.Combine(lib, "steamapps", "downloading"));
                dl.MinAgeHours = 7 * 24;
                Add(l, dl, "steam");
            }

            var epic = new List<string>();
            foreach (var d in Fs.Dirs(P(L, "EpicGamesLauncher", "Saved")))
                if (d.Name.StartsWith("webcache", StringComparison.OrdinalIgnoreCase) || d.Name.Equals("Logs", StringComparison.OrdinalIgnoreCase))
                    epic.Add(d.FullName.StartsWith(@"\\?\") ? d.FullName.Substring(4) : d.FullName);
            Add(l, Mk(CatGame, "Epic Games Launcher cache", "Store page cache and logs. Your games stay.", Safety.Safe, false, epic.ToArray()), "EpicGamesLauncher");

            Add(l, Mk(CatGame, "Battle.net cache", "Launcher cache and logs. Your games stay.", Safety.Safe, false,
                P(R, "Battle.net", "Cache"), P(L, "Battle.net", "Cache"), P(L, "Battle.net", "BrowserCache"), P(L, "Battle.net", "Logs")), "Battle.net");
            Add(l, Mk(CatGame, "Battle.net old update data", "Downloaded update data the Battle.net agent already used.", Safety.Review, false,
                P(PD, "Battle.net", "Agent", "data", "cache")), "Agent", "Battle.net");
            Add(l, Mk(CatGame, "EA app cache", "Launcher cache and logs. Your games stay.", Safety.Safe, false,
                P(L, "Electronic Arts", "EA Desktop", "CEF", "BrowserCache"), P(L, "Electronic Arts", "EA Desktop", "Logs"), P(PD, "EA Desktop", "Logs")), "EADesktop");
            Add(l, Mk(CatGame, "Ubisoft Connect cache", "Launcher cache and logs. Your games stay.", Safety.Safe, false,
                P(L, "Ubisoft Game Launcher", "cache"), P(L, "Ubisoft Game Launcher", "logs")), "UbisoftConnect", "upc");
            Add(l, Mk(CatGame, "GOG Galaxy cache", "Launcher cache and logs. Your games stay.", Safety.Safe, false,
                P(PD, "GOG.com", "Galaxy", "webcache"), P(PD, "GOG.com", "Galaxy", "logs")), "GalaxyClient");
        }

        // ================================================================ Windows.old and setup leftovers
        public static void SystemLeftovers(List<CleanItem> l)
        {
            string root = Native.SysRoot;
            var old = Mk(CatWin, "Previous Windows installation (Windows.old)",
                "Kept after a Windows upgrade so you can go back. After removing it you can't roll back to the old version.",
                Safety.Review, true, P(root, "Windows.old"));
            old.Mode = Mode.Command; old.Cmd = "sagerun"; old.CmdArgs = "Previous Installations";
            if (Fs.Exists(old.Paths[0])) l.Add(old);

            var setup = Mk(CatWin, "Windows setup leftovers", "Files from Windows upgrades and installs that are no longer needed. Uses Windows' own Disk Cleanup.",
                Safety.Safe, true, P(root, "$Windows.~BT"), P(root, "$Windows.~WS"), P(root, "$WinREAgent"), P(root, "ESD"));
            setup.Mode = Mode.Command; setup.Cmd = "sagerun";
            setup.CmdArgs = "Temporary Setup Files|Windows ESD installation files|Windows Upgrade Log Files|Setup Log Files";
            if (setup.Paths.Any(Fs.Exists)) l.Add(setup);

            // System Restore points: keep the newest, remove older ones.
            var vss = Mk(CatWin, "Old System Restore points", "Keeps your newest restore point and removes older ones. Set a size limit in Tools > System.",
                Safety.Review, true);
            vss.Mode = Mode.Command; vss.Cmd = "vss-old"; vss.CmdArgs = root; vss.Size = -1;
            if (IsAdmin)
            {
                var info = SystemSpace.Restore(root);
                if (info.Available && info.Count > 1)
                {
                    vss.Name = "Old System Restore points (" + (info.Count - 1) + " of " + info.Count + ")";
                    vss.Info = "Restore points use " + Util.Fmt(info.Used) + " in total. Keeps the newest one and removes the rest. Set a size limit in Tools > System.";
                    l.Add(vss);
                }
            }
            else l.Add(vss);
        }

        // ================================================================ WSL and Docker
        public static void Virtualization(List<CleanItem> l)
        {
            var disks = new List<string>();
            foreach (var d in Fs.Dirs(P(L, "Packages")))
            {
                string v = Path.Combine(d.FullName.StartsWith(@"\\?\") ? d.FullName.Substring(4) : d.FullName, "LocalState", "ext4.vhdx");
                if (File.Exists(v)) disks.Add(v);
            }
            foreach (var v in new[]
            {
                P(L, "Docker", "wsl", "data", "ext4.vhdx"), P(L, "Docker", "wsl", "main", "ext4.vhdx"),
                P(L, "Docker", "wsl", "disk", "docker_data.vhdx"), P(L, "Docker", "wsl", "distro", "ext4.vhdx")
            })
                if (File.Exists(v)) disks.Add(v);
            // WSL distros installed with "wsl --import" or newer layouts
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                    if (k != null)
                        foreach (var sub in k.GetSubKeyNames())
                            using (var s = k.OpenSubKey(sub))
                            {
                                var bp = s == null ? null : s.GetValue("BasePath") as string;
                                if (string.IsNullOrEmpty(bp)) continue;
                                bp = bp.StartsWith(@"\\?\") ? bp.Substring(4) : bp;
                                string v = Path.Combine(bp, "ext4.vhdx");
                                if (File.Exists(v)) disks.Add(v);
                            }
            }
            catch { }

            foreach (var v in disks.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                long size = 0;
                try { size = new FileInfo(v).Length; } catch { }
                if (size < 1L << 30) continue;
                bool docker = v.IndexOf(@"\Docker\", StringComparison.OrdinalIgnoreCase) >= 0;
                string who = docker ? "Docker" : DistroName(v);
                var it = Mk(CatVirt, "Shrink " + who + " virtual disk (" + Util.Fmt(size) + ")",
                    "These disks grow but never shrink by themselves. Shrinking gives back the unused part. Nothing inside is deleted. "
                    + "WSL" + (docker ? " and Docker" : "") + " will be shut down first.", Safety.Review, true, v);
                it.Mode = Mode.Command; it.Cmd = "compact-vhdx"; it.CmdArgs = v;
                it.Size = -1;
                it.Processes = docker ? new[] { "Docker Desktop" } : new string[0];
                l.Add(it);
            }

            // Docker build cache (only when the docker command is available)
            string dockerExe = FindOnPath("docker.exe");
            if (dockerExe != null)
            {
                var it = Mk(CatVirt, "Docker build cache", "Cached build layers. Rebuilt on the next docker build. Images and containers stay.",
                    Safety.Safe, false);
                it.Mode = Mode.Command; it.Cmd = dockerExe; it.CmdArgs = "builder prune -af";
                l.Add(it);
                var img = Mk(CatVirt, "Unused Docker images", "Images no container uses. They download again when needed.", Safety.Review, false);
                img.Mode = Mode.Command; img.Cmd = dockerExe; img.CmdArgs = "image prune -af";
                l.Add(img);
            }
        }

        static string DistroName(string vhdx)
        {
            string dir = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vhdx)) ?? "");
            if (dir.IndexOf("Ubuntu", StringComparison.OrdinalIgnoreCase) >= 0) return "Ubuntu (WSL)";
            if (dir.IndexOf("Debian", StringComparison.OrdinalIgnoreCase) >= 0) return "Debian (WSL)";
            if (dir.IndexOf("SUSE", StringComparison.OrdinalIgnoreCase) >= 0) return "openSUSE (WSL)";
            if (dir.IndexOf("Kali", StringComparison.OrdinalIgnoreCase) >= 0) return "Kali (WSL)";
            return "WSL";
        }

        public static string FindOnPath(string exe)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    if (dir.Trim().Length == 0) continue;
                    string p = Path.Combine(dir.Trim().Trim('"'), exe);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ special commands
    static class SystemCommands
    {
        static int Run(string file, string args, int timeoutMs = -1)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs < 0 ? int.MaxValue : timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        // Runs Windows' Disk Cleanup with only the given handlers ticked.
        public static void SageRun(string handlers)
        {
            const string baseKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
            const string flag = "StateFlags0177";
            var wanted = handlers.Split('|');
            using (var root = Registry.LocalMachine.OpenSubKey(baseKey, true))
            {
                if (root == null) return;
                foreach (var name in root.GetSubKeyNames())
                    using (var k = root.OpenSubKey(name, true))
                    {
                        if (k == null) continue;
                        if (wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) k.SetValue(flag, 2, RegistryValueKind.DWord);
                        else k.DeleteValue(flag, false);
                    }
            }
            Run(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"), "/sagerun:177");
            using (var root = Registry.LocalMachine.OpenSubKey(baseKey, true))
                if (root != null)
                    foreach (var name in root.GetSubKeyNames())
                        using (var k = root.OpenSubKey(name, true)) if (k != null) k.DeleteValue(flag, false);
        }

        // Shuts down WSL, then shrinks the virtual disk with diskpart (read-only attach + compact).
        public static void CompactVhdx(string path)
        {
            Run(Path.Combine(Environment.SystemDirectory, "wsl.exe"), "--shutdown", 120000);
            string script = Path.Combine(Path.GetTempPath(), "disksweep-compact.txt");
            File.WriteAllText(script, "select vdisk file=\"" + path + "\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n");
            try { Run(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), "/s \"" + script + "\""); }
            finally { try { File.Delete(script); } catch { } }
        }
    }
}
