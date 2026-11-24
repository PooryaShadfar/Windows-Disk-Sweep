// Disk Sweep 1.4 - Tools tabs: What grew?, System (restore points, reserved storage), Activity; auto-clean setting.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DiskSweep
{
    class GrowthVM
    {
        public string Name { get; set; }
        public string Sub { get; set; }
        public string ChangeText { get; set; }
        public double Share { get; set; }
    }

    class ActivityVM
    {
        public ActivityEntry Entry { get; set; }
        public string Name { get; set; }
        public string KindText { get; set; }
        public string Sub { get; set; }
        public string SizeText { get; set; }
        public Visibility RestoreVis { get; set; }
    }

    partial class MainUi
    {
        FrameworkElement growthPanel, systemPanel, activityPanel;
        TextBlock growthSummary, growthStatus, growthEmpty, restoreText, reservedText, systemStatus, activitySummary, activityEmpty, autoStatus;
        ListBox growthList, activityList;
        Button restoreDelete, restoreLimitGo, reservedGo, growSnap;
        TextBox restoreLimit;
        RadioButton toolGrowth, toolSystem, toolActivity, growRange1, growRange7, growRange30;
        CheckBox optAuto;
        bool snapshotRunning;
        string reservedState = "unknown";

        void WireTools2()
        {
            growthPanel = F<FrameworkElement>("GrowthPanel"); systemPanel = F<FrameworkElement>("SystemPanel"); activityPanel = F<FrameworkElement>("ActivityPanel");
            growthSummary = F<TextBlock>("GrowthSummary"); growthStatus = F<TextBlock>("GrowthStatus"); growthEmpty = F<TextBlock>("GrowthEmpty");
            restoreText = F<TextBlock>("RestoreText"); reservedText = F<TextBlock>("ReservedText"); systemStatus = F<TextBlock>("SystemStatus");
            activitySummary = F<TextBlock>("ActivitySummary"); activityEmpty = F<TextBlock>("ActivityEmpty"); autoStatus = F<TextBlock>("AutoStatus");
            growthList = F<ListBox>("GrowthList"); activityList = F<ListBox>("ActivityList");
            restoreDelete = F<Button>("RestoreDelete"); restoreLimitGo = F<Button>("RestoreLimitGo"); reservedGo = F<Button>("ReservedGo"); growSnap = F<Button>("GrowSnap");
            restoreLimit = F<TextBox>("RestoreLimit");
            toolGrowth = F<RadioButton>("ToolGrowth"); toolSystem = F<RadioButton>("ToolSystem"); toolActivity = F<RadioButton>("ToolActivity");
            growRange1 = F<RadioButton>("GrowRange1"); growRange7 = F<RadioButton>("GrowRange7"); growRange30 = F<RadioButton>("GrowRange30");
            optAuto = F<CheckBox>("OptAuto");

            toolGrowth.Checked += (s, e) => ShowTool("growth");
            toolSystem.Checked += (s, e) => ShowTool("system");
            toolActivity.Checked += (s, e) => ShowTool("activity");
            foreach (var r in new[] { growRange1, growRange7, growRange30 }) r.Checked += (s, e) => ShowGrowth();
            growSnap.Click += (s, e) => TakeSnapshot(true);

            F<FrameworkElement>("SystemAdminNote").Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;
            restoreDelete.Click += (s, e) => DeleteOldRestorePoints();
            restoreLimitGo.Click += (s, e) => SetRestoreLimit();
            restoreLimit.PreviewTextInput += (s, e) => { e.Handled = !e.Text.All(char.IsDigit); };
            reservedGo.Click += (s, e) => ToggleReserved();

            F<Button>("ActivityClear").Click += async (s, e) =>
            {
                if (await ShowDialog("Clear the activity log?", "The list of what Disk Sweep removed is deleted. Files in the Recycle Bin stay there.",
                        null, null, "Clear log", "Cancel", false, false, null) == 1) { Activity.Clear(); ShowActivity(); }
            };
            activityList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(async (s, e) =>
            {
                var b = e.OriginalSource as Button;
                var vm = b == null ? null : b.DataContext as ActivityVM;
                if (vm == null || (b.Tag as string) != "restore") return;
                int ok = await ShowDialog("Restore " + vm.Name + "?", "Puts the files back from the Recycle Bin where they were. Nothing that already exists is overwritten.",
                    null, null, "Restore", "Cancel", false, false, null);
                if (ok != 1) return;
                var entry = vm.Entry;
                int n = await Task.Run(() => Activity.Restore(entry));
                await ShowDialog(n > 0 ? "Restored" : "Nothing restored", n > 0 ? n + " item" + (n == 1 ? "" : "s") + " put back where they were."
                                 : "The files are no longer in the Recycle Bin, or something already exists at their old place.", null, null, "OK", null, false, false, null);
                ShowActivity();
                Scan();
            }));

            optAuto.Checked += (s, e) => SaveAuto();
            optAuto.Unchecked += (s, e) => SaveAuto();
        }

        // ---------------------------------------------------------- what grew
        void ShowGrowth()
        {
            string drive = Native.SysRoot;
            int days = growRange1.IsChecked == true ? 0 : growRange30.IsChecked == true ? 30 : 7;
            DateTime? since; long total;
            var rows = Growth.Compare(drive, days, out since, out total);
            var latest = Growth.Latest(drive);
            long max = Math.Max(1, rows.Count > 0 ? rows.Max(r => r.Change) : 1);
            growthList.ItemsSource = rows.Select(r => new GrowthVM
            {
                Name = r.Path.EndsWith("\\[files]") ? r.Path.Substring(0, r.Path.Length - 8) + "  (files directly in it)" : r.Path,
                Sub = "was " + Util.Fmt(r.Before) + "  →  now " + Util.Fmt(r.After),
                ChangeText = "+" + Util.Fmt(r.Change),
                Share = r.Change * 100.0 / max
            }).ToList();

            if (since == null)
            {
                growthSummary.Text = "Drive " + drive.TrimEnd('\\');
                growthEmpty.Text = latest == null
                    ? "No snapshot yet. Disk Sweep takes one in the background once a day when you open it, or click \"Take snapshot now\". Check back after the next one to see what grew."
                    : "One snapshot so far (" + latest.Value.ToString("yyyy-MM-dd HH:mm") + "). Disk Sweep takes the next one tomorrow when you open it. Then you'll see what grew.";
                growthEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                growthSummary.Text = "Drive " + drive.TrimEnd('\\') + ":  " + (total >= 0 ? "+" : "−") + Util.Fmt(Math.Abs(total)) + " used since "
                                     + since.Value.ToString("yyyy-MM-dd") + " (" + Math.Max(0, (int)(latest.Value - since.Value).TotalDays) + " days)";
                growthEmpty.Text = "Nothing grew by 100 MB or more in this time.";
                growthEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            growthStatus.Text = snapshotRunning ? "Taking a snapshot in the background…"
                : latest == null ? "" : "Latest snapshot: " + latest.Value.ToString("yyyy-MM-dd HH:mm") + ". Folders of 20 MB or more, five levels deep, are recorded.";
        }

        // Measures the system drive (like the space map) and stores folder sizes. Runs in the background.
        async void TakeSnapshot(bool manual)
        {
            if (snapshotRunning) return;
            string drive = Native.SysRoot;
            if (!manual)
            {
                var last = Growth.Latest(drive);
                if (last.HasValue && (DateTime.Now - last.Value).TotalHours < 20) return;
            }
            snapshotRunning = true;
            if (manual) { growSnap.IsEnabled = false; growthStatus.Text = "Measuring " + drive + "… about a minute."; }
            try
            {
                var tree = await Task.Run(() =>
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    var t = SpaceMap.Build(drive, null, CancellationToken.None);
                    Growth.Save(drive, t);
                    return t;
                });
                mapCache[drive] = tree;   // the space map opens instantly afterwards
            }
            catch { }
            finally
            {
                snapshotRunning = false;
                growSnap.IsEnabled = true;
                if (growthPanel.Visibility == Visibility.Visible) ShowGrowth();
            }
        }

        // ---------------------------------------------------------- system
        async void LoadSystem()
        {
            restoreDelete.IsEnabled = restoreLimitGo.IsEnabled = reservedGo.IsEnabled = false;
            if (!isAdmin)
            {
                restoreText.Text = "Needs administrator rights.";
                reservedText.Text = "Needs administrator rights.";
                return;
            }
            restoreText.Text = "Checking…"; reservedText.Text = "Checking…";
            string drive = Native.SysRoot;
            var info = await Task.Run(() => SystemSpace.Restore(drive));
            restoreText.Text = !info.Available ? "Couldn't read restore points."
                : info.Count == 0 ? "No restore points on " + drive.TrimEnd('\\') + ". System Restore may be turned off."
                : info.Count + " restore point" + (info.Count == 1 ? "" : "s") + " using " + Util.Fmt(info.Used)
                  + (info.Max > 0 ? " (limit " + Util.Fmt(info.Max) + ")" : "") + ".";
            restoreDelete.IsEnabled = info.Available && info.Count > 1 && !toolBusy;
            restoreLimitGo.IsEnabled = !toolBusy;

            reservedState = await Task.Run(() => SystemSpace.ReservedState());
            reservedText.Text = reservedState == "on" ? "Reserved storage is on." : reservedState == "off" ? "Reserved storage is off." : "Status unknown.";
            reservedGo.Content = reservedState == "off" ? "Turn on reserved storage" : "Turn off reserved storage";
            reservedGo.IsEnabled = !toolBusy;
        }

        async void DeleteOldRestorePoints()
        {
            int ok = await ShowDialog("Remove old restore points?", "Keeps the newest restore point and removes all older ones on " + Native.SysRoot.TrimEnd('\\')
                + ". You won't be able to go back to those older points.", null, null, "Remove old points", "Cancel", true, false, null);
            if (ok != 1) return;
            toolBusy = true; cleaning = true;
            string drive = Native.SysRoot;
            long before = Native.FreeBytes(drive);
            int n = 0;
            try { n = await Task.Run(() => SystemSpace.DeleteOld(drive)); }
            finally { toolBusy = false; cleaning = false; }
            long freed = Math.Max(0, Native.FreeBytes(drive) - before);
            if (n > 0) { Activity.Add("system", "Old System Restore points (" + n + ")", freed); AddHistory(freed, n); }
            systemStatus.Text = n == 0 ? "No restore points were removed." : "Removed " + n + " restore point" + (n == 1 ? "" : "s") + ", freed " + Util.Fmt(freed) + ".";
            LoadSystem();
        }

        async void SetRestoreLimit()
        {
            int pct;
            if (!int.TryParse(restoreLimit.Text, out pct) || pct < 1 || pct > 50) { systemStatus.Text = "Enter a limit between 1 and 50 %."; return; }
            toolBusy = true;
            bool done;
            try { done = await Task.Run(() => SystemSpace.SetLimit(Native.SysRoot, pct)); }
            finally { toolBusy = false; }
            systemStatus.Text = done ? "Restore points may now use at most " + pct + "% of the drive. Windows removes the oldest ones when they need more."
                                     : "Windows didn't accept the limit. System Restore may be turned off for this drive.";
            if (done) Activity.Add("system", "Restore point limit set to " + pct + "%", 0);
            LoadSystem();
        }

        async void ToggleReserved()
        {
            bool turnOn = reservedState == "off";
            int ok = await ShowDialog(turnOn ? "Turn on reserved storage?" : "Turn off reserved storage?",
                turnOn ? "Windows sets aside about 7 GB for updates again." : "Windows gives back about 7 GB. If a big update ever needs room, Windows asks you to free some space first.",
                null, null, turnOn ? "Turn on" : "Turn off", "Cancel", false, false, null);
            if (ok != 1) return;
            toolBusy = true; cleaning = true;
            long before = Native.FreeBytes();
            string err;
            try { err = await Task.Run(() => SystemSpace.SetReserved(turnOn)); }
            finally { toolBusy = false; cleaning = false; }
            long freed = Math.Max(0, Native.FreeBytes() - before);
            systemStatus.Text = err != null ? "Windows said: " + err + (err.IndexOf("progress", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("pending", StringComparison.OrdinalIgnoreCase) >= 0
                                                ? "  (An update may be pending. Restart the PC and try again.)" : "")
                : turnOn ? "Reserved storage is on again." : "Reserved storage is off. Freed " + Util.Fmt(freed) + ".";
            if (err == null) { Activity.Add("system", turnOn ? "Reserved storage turned on" : "Reserved storage turned off", freed); if (freed > 0) AddHistory(freed, 1); }
            LoadSystem();
        }

        // ---------------------------------------------------------- activity
        void ShowActivity()
        {
            var entries = Activity.Read().Where(a => a.Kind != "restored").Take(200).ToList();
            var restorable = new HashSet<ActivityEntry>();
            foreach (var e in entries.Where(x => x.Kind == "recycled").Take(60))
                if (Activity.Restorable(e) > 0) restorable.Add(e);
            activityList.ItemsSource = entries.Select(a => new ActivityVM
            {
                Entry = a,
                Name = a.Name,
                KindText = a.Kind == "recycled" ? "To Recycle Bin" : a.Kind == "deleted" ? "Deleted" : a.Kind == "auto" ? "Auto-clean"
                         : a.Kind == "moved" ? "Moved" : a.Kind == "compressed" ? "Compressed" : "System",
                Sub = a.When.ToString("yyyy-MM-dd  HH:mm") + (a.Paths.Count > 0 ? "   ·   " + a.Paths[0] + (a.Paths.Count > 1 ? "  (+" + (a.Paths.Count - 1) + " more)" : "") : ""),
                SizeText = a.Bytes > 0 ? Util.Fmt(a.Bytes) : "",
                RestoreVis = restorable.Contains(a) ? Visibility.Visible : Visibility.Collapsed
            }).ToList();
            activityEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            activitySummary.Text = entries.Count == 0 ? "" : entries.Count + (entries.Count == 1 ? " action  ·  " : " actions  ·  ") + Util.Fmt(entries.Sum(x => x.Bytes)) + " in total"
                                   + (restorable.Count > 0 ? "  ·  " + restorable.Count + " can be restored" : "");
        }

        void AddHistory(long bytes, int items)
        {
            Settings.History.Add(new HistoryEntry { When = DateTime.Now, Bytes = bytes, Items = items });
            Settings.Save();
        }

        // ---------------------------------------------------------- auto-clean setting
        void FillAuto()
        {
            bool on = Settings.AutoClean && AutoClean.TaskExists();
            optAuto.IsChecked = on;
            autoStatus.Text = on ? "On. Next run: Sunday at 12:00." : "Off.";
        }

        void SaveAuto()
        {
            if (loadingSettings) return;
            bool want = optAuto.IsChecked == true;
            bool ok = AutoClean.SetTask(want);
            Settings.AutoClean = want && ok;
            Settings.Save();
            if (want && !ok) { loadingSettings = true; optAuto.IsChecked = false; loadingSettings = false; }
            autoStatus.Text = !want ? "Off." : ok ? "On. Next run: Sunday at 12:00." : "Couldn't create the scheduled task. Try again, or run Disk Sweep as administrator.";
        }
    }
}
