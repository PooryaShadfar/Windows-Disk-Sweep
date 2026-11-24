// Disk Sweep 1.5 - Duplicates tab, leftovers after uninstalling, restore point before big changes.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DiskSweep
{
    class DupCopyVM : INotifyPropertyChanged
    {
        public DupFile File { get; set; }
        public DupGroupVM Group { get; set; }
        public string Path { get { return File.Path; } }
        public string Sub { get; set; }
        bool chk;
        public bool IsChecked
        {
            get { return chk; }
            set
            {
                if (chk == value) return;
                chk = value;
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("IsChecked"));
                if (Changed != null) Changed();
            }
        }
        public event Action Changed;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    class DupGroupVM
    {
        public DupGroup Group { get; set; }
        public string Header { get; set; }
        public List<DupCopyVM> Copies { get; set; }
    }

    partial class MainUi
    {
        FrameworkElement dupPanel;
        TextBlock dupRoots, dupSummary, dupEmpty, dupRemoveLabel;
        ItemsControl dupList;
        Button dupScan, dupRemove;
        RadioButton toolDup;
        CheckBox optRestore;
        List<string> dupFolders;
        List<DupGroupVM> dupGroups = new List<DupGroupVM>();
        CancellationTokenSource dupCts;

        void WireTools3()
        {
            dupPanel = F<FrameworkElement>("DupPanel");
            dupRoots = F<TextBlock>("DupRoots"); dupSummary = F<TextBlock>("DupSummary"); dupEmpty = F<TextBlock>("DupEmpty");
            dupRemoveLabel = F<TextBlock>("DupRemoveLabel");
            dupList = F<ItemsControl>("DupList");
            dupScan = F<Button>("DupScan"); dupRemove = F<Button>("DupRemove");
            toolDup = F<RadioButton>("ToolDup");
            optRestore = F<CheckBox>("OptRestore");

            toolDup.Checked += (s, e) => ShowTool("dup");
            dupFolders = Duplicates.DefaultRoots();
            F<Button>("DupPick").Click += (s, e) =>
            {
                string f = null;
                try { f = FolderPicker.Pick(Window, "Pick a folder to look for duplicates in"); } catch { }
                if (string.IsNullOrEmpty(f)) return;
                dupFolders = new List<string> { f };
                ShowDupRoots();
            };
            dupScan.Click += (s, e) => FindDuplicates();
            dupRemove.Click += (s, e) => RemoveDuplicates();

            RoutedEventHandler restoreChanged = (s, e) =>
            {
                if (loadingSettings) return;
                Settings.RestoreBefore = optRestore.IsChecked == true;
                Settings.Save();
            };
            optRestore.Checked += restoreChanged;
            optRestore.Unchecked += restoreChanged;
        }

        void ShowDupRoots()
        {
            dupRoots.Text = "Looking in: " + string.Join(", ", dupFolders.Select(f => Path.GetFileName(f.TrimEnd('\\')).Length > 0 ? Path.GetFileName(f.TrimEnd('\\')) : f))
                            + ".  Files of 1 MB or more, compared by content.";
            if (dupGroups.Count == 0) dupEmpty.Text = "Click \"Find duplicates\". Nothing is removed until you check copies and confirm.";
            dupEmpty.Visibility = dupGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateDupSummary();
        }

        async void FindDuplicates()
        {
            if (dupCts != null) dupCts.Cancel();
            var cts = new CancellationTokenSource();
            dupCts = cts;
            dupScan.IsEnabled = false; dupRemove.IsEnabled = false;
            dupList.ItemsSource = null; dupGroups = new List<DupGroupVM>();
            dupEmpty.Visibility = Visibility.Visible;
            dupEmpty.Text = "Looking for duplicates…";
            var roots = dupFolders.ToList();
            try
            {
                var prog = new Progress<string>(s => { if (!cts.IsCancellationRequested) dupEmpty.Text = s; });
                var groups = await Task.Run(() => Duplicates.Find(roots, 1L << 20, prog, cts.Token));
                if (cts.IsCancellationRequested) return;
                dupGroups = groups.Take(300).Select(g =>
                {
                    var vm = new DupGroupVM { Group = g, Header = Path.GetFileName(g.Files[0].Path) + "   ·   " + Util.Fmt(g.Size) + "  ×  " + g.Files.Count + " copies" };
                    // Keep the oldest copy; check the others.
                    vm.Copies = g.Files.Select((f, i) =>
                    {
                        var c = new DupCopyVM
                        {
                            File = f, Group = vm,
                            Sub = (i == 0 ? "Oldest copy · kept  ·  " : "") + "created " + f.Created.ToString("yyyy-MM-dd") + ", changed " + f.Modified.ToString("yyyy-MM-dd")
                        };
                        c.IsChecked = i > 0;
                        c.Changed += UpdateDupSummary;
                        return c;
                    }).ToList();
                    return vm;
                }).ToList();
                dupList.ItemsSource = dupGroups;
                dupEmpty.Text = "No duplicates of 1 MB or more found.";
                dupEmpty.Visibility = dupGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { dupEmpty.Text = "Search failed: " + ex.Message; }
            finally { dupScan.IsEnabled = true; UpdateDupSummary(); }
        }

        void UpdateDupSummary()
        {
            var sel = dupGroups.SelectMany(g => g.Copies).Where(c => c.IsChecked).ToList();
            long bytes = sel.Sum(c => c.File.Size);
            int groups = dupGroups.Count;
            dupSummary.Text = groups == 0 ? "" : groups + " set" + (groups == 1 ? "" : "s") + " of duplicates  ·  " + sel.Count + " cop" + (sel.Count == 1 ? "y" : "ies")
                              + " checked  ·  " + Util.Fmt(bytes);
            dupRemoveLabel.Text = sel.Count == 0 ? "Move checked copies to Recycle Bin" : "Move " + Util.Fmt(bytes) + " to Recycle Bin";
            dupRemove.IsEnabled = sel.Count > 0 && !toolBusy;
        }

        async void RemoveDuplicates()
        {
            var allChecked = dupGroups.Where(g => g.Copies.All(c => c.IsChecked)).ToList();
            if (allChecked.Count > 0)
            {
                await ShowDialog("Keep at least one copy", "In " + allChecked.Count + " set" + (allChecked.Count == 1 ? "" : "s") + " every copy is checked, for example \""
                                 + Path.GetFileName(allChecked[0].Group.Files[0].Path) + "\". Uncheck the copy you want to keep.", null, null, "OK", null, false, false, null);
                return;
            }
            var sel = dupGroups.SelectMany(g => g.Copies).Where(c => c.IsChecked).ToList();
            long bytes = sel.Sum(c => c.File.Size);
            int ok = await ShowDialog("Move " + sel.Count + " duplicate cop" + (sel.Count == 1 ? "y" : "ies") + " to the Recycle Bin?",
                "One copy of each file stays where it is. The others go to the Recycle Bin, so you can restore them (also from Tools > Activity).",
                sel.Take(30).Select(c => new KeyValuePair<string, string>(c.Path, Util.Fmt(c.File.Size))).ToList(), null,
                "Move " + Util.Fmt(bytes), "Cancel", true, false, null);
            if (ok != 1) return;
            toolBusy = true; dupRemove.IsEnabled = false;
            var paths = sel.Select(c => c.Path).ToList();
            try { await Task.Run(() => Native.Recycle(paths)); }
            finally { toolBusy = false; }
            var moved = paths.Where(p => !File.Exists(p)).ToList();
            long movedBytes = sel.Where(c => moved.Contains(c.Path)).Sum(c => c.File.Size);
            Activity.Add("recycled", "Duplicate copies (" + moved.Count + ")", movedBytes, moved);
            if (movedBytes > 0) AddHistory(movedBytes, moved.Count);
            await ShowDialog("Moved to Recycle Bin", moved.Count + " of " + paths.Count + " copies moved (" + Util.Fmt(movedBytes) + "). Empty the Recycle Bin to free the space."
                             + (moved.Count < paths.Count ? " Some files were in use and stayed." : ""), null, null, "OK", null, false, false, null);
            // Drop moved copies and sets that no longer have duplicates
            foreach (var g in dupGroups) g.Copies = g.Copies.Where(c => !moved.Contains(c.Path)).ToList();
            dupGroups = dupGroups.Where(g => g.Copies.Count > 1).ToList();
            dupList.ItemsSource = null; dupList.ItemsSource = dupGroups;
            dupEmpty.Visibility = dupGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateDupSummary();
        }

        // ---------------------------------------------------------- after uninstalling
        // Waits (up to 20 minutes) until the app is gone from Windows' app list, then looks for folders it left behind.
        async void WatchUninstall(AppEntry app)
        {
            string name = app.Name;
            appsSummary.Text = "Waiting for the " + name + " uninstaller to finish…";
            DateTime until = DateTime.Now.AddMinutes(20);
            bool gone = false;
            while (DateTime.Now < until)
            {
                await Task.Delay(3000);
                if (!await Task.Run(() => AppLeftovers.StillInstalled(app))) { gone = true; break; }
            }
            if (!gone) { appsSummary.Text = name + " is still installed."; return; }
            await Task.Delay(2000);   // let the uninstaller finish removing files
            var remaining = await Task.Run(() => Apps.List());
            var left = await Task.Run(() => AppLeftovers.Find(app, remaining));
            apps = null; LoadApps();
            if (left.Count == 0)
            {
                await ShowDialog(name + " is uninstalled", "No leftover folders found.", null, null, "OK", null, false, false, null);
                return;
            }
            int ok = await ShowDialog(name + " left some folders behind",
                "These folders are named after the app and are still on your PC. Check the list; they go to the Recycle Bin, so you can restore them.",
                left.Select(l => new KeyValuePair<string, string>(l.Item1, Util.Fmt(l.Item2))).ToList(), null,
                "Move " + Util.Fmt(left.Sum(l => l.Item2)) + " to Recycle Bin", "Keep them", false, false, null);
            if (ok != 1) return;
            var paths = left.Select(l => l.Item1).ToList();
            await Task.Run(() => Native.Recycle(paths));
            var moved = paths.Where(p => !Directory.Exists(p) && !File.Exists(p)).ToList();
            long bytes = left.Where(l => moved.Contains(l.Item1)).Sum(l => l.Item2);
            Activity.Add("recycled", name + " leftovers", bytes, moved);
            if (bytes > 0) AddHistory(bytes, moved.Count);
            appsSummary.Text = "Moved " + Util.Fmt(bytes) + " of " + name + " leftovers to the Recycle Bin.";
        }

        // ---------------------------------------------------------- restore point
        // Returns a short note for the result dialog, or null when nothing was attempted.
        async Task<string> RestorePointFirst(string what, Action<string> status)
        {
            if (!isAdmin || !Settings.RestoreBefore) return null;
            if (status != null) status("Creating a restore point first…");
            string err = await Task.Run(() => RestorePoint.Create("Disk Sweep: " + what));
            if (err == null) { Activity.Add("system", "Restore point before: " + what, 0); return "A restore point was created first."; }
            return err.StartsWith("skipped") ? "Restore point " + err : "No restore point: " + err;
        }
    }
}
