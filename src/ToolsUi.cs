// Disk Sweep - Tools window: space map, apps by size, move a folder, compression.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DiskSweep
{
    class AppVM
    {
        public AppEntry App { get; set; }
        public string Name { get; set; }
        public string Sub { get; set; }
        public string SizeText { get; set; }
        public double Share { get; set; }
    }

    partial class MainUi
    {
        FrameworkElement toolsOverlay, mapPanel, appsPanel, movePanel, compressPanel;
        TextBlock toolTitle, toolSub, mapPath, mapStatus, appsSummary, moveSrc, moveTarget, moveHint, moveStatus, compactOsText, compSrc, compStatus;
        Canvas mapCanvas;
        Border mapHost;
        Button mapUp, moveGo, compGo, compactOsGo;
        ListBox appsList;
        ItemsControl moveDrives;
        RadioButton toolMap, toolApps, toolMove, toolCompress;

        // space map state
        readonly Dictionary<string, MapNode> mapCache = new Dictionary<string, MapNode>(StringComparer.OrdinalIgnoreCase);
        MapNode mapNode;
        CancellationTokenSource mapCts;
        bool mapBuilding;
        // other tools
        List<AppEntry> apps;
        string moveSource, moveTargetRoot, compFolder;
        bool toolBusy;

        static readonly string[] MapColors =
            { "#4F8FDB", "#5BB98C", "#E3A857", "#C86DD7", "#E06C75", "#56B6C2", "#9C89B8", "#7FB069", "#D4A373", "#F28CB1" };

        void WireTools()
        {
            toolsOverlay = F<FrameworkElement>("ToolsOverlay");
            mapPanel = F<FrameworkElement>("MapPanel"); appsPanel = F<FrameworkElement>("AppsPanel");
            movePanel = F<FrameworkElement>("MovePanel"); compressPanel = F<FrameworkElement>("CompressPanel");
            toolTitle = F<TextBlock>("ToolTitle"); toolSub = F<TextBlock>("ToolSub");
            mapPath = F<TextBlock>("MapPath"); mapStatus = F<TextBlock>("MapStatus");
            mapCanvas = F<Canvas>("MapCanvas"); mapHost = F<Border>("MapHost"); mapUp = F<Button>("MapUp");
            appsSummary = F<TextBlock>("AppsSummary"); appsList = F<ListBox>("AppsList");
            moveSrc = F<TextBlock>("MoveSrc"); moveTarget = F<TextBlock>("MoveTarget"); moveHint = F<TextBlock>("MoveHint");
            moveStatus = F<TextBlock>("MoveStatus"); moveGo = F<Button>("MoveGo"); moveDrives = F<ItemsControl>("MoveDrives");
            compactOsText = F<TextBlock>("CompactOsText"); compactOsGo = F<Button>("CompactOsGo");
            compSrc = F<TextBlock>("CompSrc"); compStatus = F<TextBlock>("CompStatus"); compGo = F<Button>("CompGo");
            toolMap = F<RadioButton>("ToolMap"); toolApps = F<RadioButton>("ToolApps");
            toolMove = F<RadioButton>("ToolMove"); toolCompress = F<RadioButton>("ToolCompress");

            F<Button>("BtnTools").Click += (s, e) => OpenTools();
            F<Button>("BtnCloseTools").Click += (s, e) => CloseTools();
            toolMap.Checked += (s, e) => ShowTool("map");
            toolApps.Checked += (s, e) => ShowTool("apps");
            toolMove.Checked += (s, e) => ShowTool("move");
            toolCompress.Checked += (s, e) => ShowTool("compress");

            // space map
            mapHost.SizeChanged += (s, e) => DrawMap();
            mapUp.Click += (s, e) => { if (mapNode != null && mapNode.Parent != null) { mapNode = mapNode.Parent; DrawMap(); } };
            F<Button>("MapPick").Click += (s, e) =>
            {
                string f = null;
                try { f = FolderPicker.Pick(Window, "Pick a folder to map"); } catch { }
                if (!string.IsNullOrEmpty(f)) BuildMap(f);
            };
            F<Button>("MapOpen").Click += (s, e) =>
            {
                if (mapNode != null) try { Process.Start("explorer.exe", "\"" + mapNode.Path + "\""); } catch { }
            };

            // apps
            F<Button>("AppsStore").Click += (s, e) => { try { Process.Start("ms-settings:appsfeatures"); } catch { } };
            appsList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(async (s, e) =>
            {
                var b = e.OriginalSource as Button;
                var vm = b == null ? null : b.DataContext as AppVM;
                if (vm == null || (b.Tag as string) != "uninstall") return;
                int ok = await ShowDialog("Uninstall " + vm.Name + "?",
                    "Disk Sweep opens the app's own uninstaller. Follow its steps; it may ask for permission. Disk Sweep doesn't remove anything itself.",
                    null, null, "Open uninstaller", "Cancel", false, false, null);
                if (ok != 1) return;
                string error = null;
                try { Apps.Uninstall(vm.App); }
                catch (Exception ex) { error = ex.Message; }
                if (error != null) await ShowDialog("Couldn't start the uninstaller", error, null, null, "OK", null, false, false, null);
            }));

            // move
            F<Button>("MoveDownloads").Click += (s, e) => SetMoveSource(Mover.DownloadsPath);
            F<Button>("MoveNpm").Click += (s, e) => SetMoveSource(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache"));
            F<Button>("MovePick").Click += (s, e) =>
            {
                string f = null;
                try { f = FolderPicker.Pick(Window, "Pick the folder to move"); } catch { }
                if (!string.IsNullOrEmpty(f)) SetMoveSource(f);
            };
            moveDrives.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) =>
            {
                var rb = e.OriginalSource as RadioButton;
                var d = rb == null ? null : rb.DataContext as DriveVM;
                if (d != null) { moveTargetRoot = d.Root; UpdateMove(); }
            }));
            moveGo.Click += (s, e) => DoMove();

            // compress
            compactOsGo.Click += (s, e) => DoCompactOs();
            F<Button>("CompPick").Click += async (s, e) =>
            {
                string f = null;
                try { f = FolderPicker.Pick(Window, "Pick a folder to compress"); } catch { }
                if (string.IsNullOrEmpty(f)) return;
                compFolder = f;
                string why = Compressor.CheckFolder(f);
                compSrc.Text = f + "  ·  measuring…";
                compGo.IsEnabled = false;
                long size = await Task.Run(() => Fs.PathSize(f, null, 0, true));
                compSrc.Text = f + "  ·  " + Util.Fmt(size);
                compStatus.Text = why ?? "";
                compGo.IsEnabled = why == null && !toolBusy;
            };
            compGo.Click += (s, e) => DoCompressFolder();
            toolsOverlay.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == toolsOverlay) CloseTools(); };
        }

        void OpenTools()
        {
            toolsOverlay.Visibility = Visibility.Visible;
            if (toolMap.IsChecked != true && toolApps.IsChecked != true && toolMove.IsChecked != true && toolCompress.IsChecked != true)
                toolMap.IsChecked = true;
            else ShowTool(toolMap.IsChecked == true ? "map" : toolApps.IsChecked == true ? "apps" : toolMove.IsChecked == true ? "move" : "compress");
        }

        void CloseTools()
        {
            if (toolBusy) return;   // a move or compression is running
            if (mapCts != null) mapCts.Cancel();
            toolsOverlay.Visibility = Visibility.Collapsed;
        }

        void ShowTool(string which)
        {
            mapPanel.Visibility = which == "map" ? Visibility.Visible : Visibility.Collapsed;
            appsPanel.Visibility = which == "apps" ? Visibility.Visible : Visibility.Collapsed;
            movePanel.Visibility = which == "move" ? Visibility.Visible : Visibility.Collapsed;
            compressPanel.Visibility = which == "compress" ? Visibility.Visible : Visibility.Collapsed;
            switch (which)
            {
                case "map":
                    toolTitle.Text = "Space map";
                    toolSub.Text = "Bigger box = more space. Click a box to look inside. Nothing is deleted here.";
                    if (mapNode == null || !mapNode.Path.StartsWith(selectedDrive, StringComparison.OrdinalIgnoreCase)) BuildMap(selectedDrive);
                    else DrawMap();
                    break;
                case "apps":
                    toolTitle.Text = "Apps by size";
                    toolSub.Text = "Installed programs, biggest first. Uninstall opens the app's own uninstaller.";
                    if (apps == null) LoadApps();
                    break;
                case "move":
                    toolTitle.Text = "Move a folder to another drive";
                    toolSub.Text = "Frees space on this drive. Apps keep finding the folder through a link.";
                    RefreshMoveDrives();
                    UpdateMove();
                    break;
                case "compress":
                    toolTitle.Text = "Compress instead of delete";
                    toolSub.Text = "Windows' built-in compression. Nothing is deleted and everything keeps working.";
                    LoadCompactOs();
                    break;
            }
        }

        // ---------------------------------------------------------- space map
        async void BuildMap(string root)
        {
            MapNode cached;
            if (mapCache.TryGetValue(root, out cached)) { mapNode = cached; DrawMap(); return; }
            if (mapCts != null) mapCts.Cancel();
            var cts = new CancellationTokenSource();
            mapCts = cts;
            mapBuilding = true;
            mapCanvas.Children.Clear();
            mapPath.Text = root;
            mapStatus.Text = "Measuring " + root + "… This can take a minute for a whole drive.";
            try
            {
                var prog = new Progress<string>(s => { if (!cts.IsCancellationRequested) mapStatus.Text = s; });
                var tree = await Task.Run(() => SpaceMap.Build(root, prog, cts.Token));
                mapCache[root] = tree;
                if (cts.IsCancellationRequested) return;
                mapBuilding = false;
                mapNode = tree;
                DrawMap();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { mapStatus.Text = "Couldn't measure: " + ex.Message; }
            finally { if (mapCts == cts) mapBuilding = false; }
        }

        void DrawMap()
        {
            if (mapNode == null || mapBuilding) return;
            mapCanvas.Children.Clear();
            mapPath.Text = mapNode.Path;
            mapUp.IsEnabled = mapNode.Parent != null;
            double w = mapHost.ActualWidth - 2, h = mapHost.ActualHeight - 2;
            if (w < 50 || h < 50) return;

            var kids = (mapNode.Children ?? new List<MapNode>()).ToList();
            // Show up to 60 boxes; group the rest so the map stays readable.
            if (kids.Count > 60)
            {
                var rest = kids.Skip(59).ToList();
                kids = kids.Take(59).ToList();
                kids.Add(new MapNode { Name = rest.Count + " smaller items", Path = mapNode.Path, Size = rest.Sum(r => r.Size), IsFiles = true, Parent = mapNode });
            }
            var rects = SpaceMap.Layout(kids, 0, 0, w, h);
            int i = 0;
            foreach (var r in rects)
            {
                var n = r.Node;
                var c = (Color)ColorConverter.ConvertFromString(n.IsFiles ? "#9AA0A6" : MapColors[i++ % MapColors.Length]);
                var fill = new SolidColorBrush(Color.FromArgb((byte)(Theme.Dark ? 0x66 : 0x48), c.R, c.G, c.B));
                var box = new Border
                {
                    Width = Math.Max(0, r.W - 3), Height = Math.Max(0, r.H - 3), Background = fill, CornerRadius = new CornerRadius(5),
                    BorderBrush = new SolidColorBrush(c), BorderThickness = new Thickness(1),
                    ToolTip = n.Name + "\n" + Util.Fmt(n.Size) + "  (" + (mapNode.Size > 0 ? (n.Size * 100.0 / mapNode.Size).ToString("0.0") : "0") + "%)"
                              + (n.IsFiles ? "" : "\n" + n.Path),
                    Cursor = n.IsFiles || n.Children == null || n.Children.Count == 0 ? Cursors.Arrow : Cursors.Hand
                };
                System.Windows.Automation.AutomationProperties.SetName(box, n.Name + ", " + Util.Fmt(n.Size));
                if (r.W > 64 && r.H > 34)
                {
                    var sp = new StackPanel { Margin = new Thickness(8, 6, 6, 4) };
                    sp.Children.Add(new TextBlock { Text = n.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.Get("Text") });
                    if (r.H > 50) sp.Children.Add(new TextBlock { Text = Util.Fmt(n.Size), FontSize = 12.5, Foreground = Theme.Get("Muted") });
                    box.Child = sp;
                }
                var node = n;
                box.MouseLeftButtonUp += (s, e) =>
                {
                    if (!node.IsFiles && node.Children != null && node.Children.Count > 0) { mapNode = node; DrawMap(); }
                };
                Canvas.SetLeft(box, r.X + 1.5);
                Canvas.SetTop(box, r.Y + 1.5);
                mapCanvas.Children.Add(box);
            }
            mapStatus.Text = Util.Fmt(mapNode.Size) + " in " + mapNode.Path + ".  Hover for details, click to go deeper"
                             + (mapNode.Parent != null ? ", Up to go back." : ".");
        }

        // ---------------------------------------------------------- apps
        async void LoadApps()
        {
            appsSummary.Text = "Reading installed apps…";
            var list = await Task.Run(() => Apps.List());
            apps = list;
            ShowApps();
            appsSummary.Text += "  Measuring apps without a size…";
            await Task.Run(() => Apps.MeasureMissing(list));
            ShowApps();
        }

        void ShowApps()
        {
            var sorted = apps.OrderByDescending(a => a.Size).ToList();
            long max = Math.Max(1, sorted.Count > 0 ? sorted[0].Size : 1);
            appsList.ItemsSource = sorted.Select(a => new AppVM
            {
                App = a,
                Name = a.Name,
                Sub = string.Join("  ·  ", new[] { a.Publisher, a.Version.Length > 0 ? "v" + a.Version : "", a.Installed.HasValue ? "installed " + a.Installed.Value.ToString("yyyy-MM-dd") : "" }
                                         .Where(x => !string.IsNullOrEmpty(x))),
                SizeText = a.Size > 0 ? Util.Fmt(a.Size) : "—",
                Share = a.Size * 100.0 / max
            }).ToList();
            appsSummary.Text = sorted.Count + " apps  ·  " + Util.Fmt(sorted.Sum(a => a.Size)) + " in total";
        }

        // ---------------------------------------------------------- move
        void RefreshMoveDrives()
        {
            string srcRoot = moveSource == null ? null : Path.GetPathRoot(moveSource);
            var drives = Settings.FixedDrives().Where(d => srcRoot == null || !d.Name.Equals(srcRoot, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.AvailableFreeSpace).ToList();
            if (moveTargetRoot == null || !drives.Any(d => d.Name.Equals(moveTargetRoot, StringComparison.OrdinalIgnoreCase)))
                moveTargetRoot = drives.Count > 0 ? drives[0].Name : null;
            moveDrives.ItemsSource = drives.Select(d => new DriveVM
            {
                Root = d.Name,
                Label = d.Name.TrimEnd('\\') + "   " + Util.Fmt(d.AvailableFreeSpace) + " free",
                IsSelected = d.Name.Equals(moveTargetRoot, StringComparison.OrdinalIgnoreCase)
            }).ToList();
        }

        async void SetMoveSource(string path)
        {
            moveSource = path;
            moveSrc.Text = path + "  ·  measuring…";
            RefreshMoveDrives();
            UpdateMove();
            long size = await Task.Run(() => Directory.Exists(path) ? Fs.PathSize(path, null, 0, true) : 0);
            if (moveSource == path) moveSrc.Text = path + "  ·  " + Util.Fmt(size);
        }

        void UpdateMove()
        {
            if (moveSource == null) { moveSrc.Text = "Pick a folder above."; moveGo.IsEnabled = false; moveTarget.Text = ""; moveHint.Text = ""; return; }
            string why = moveTargetRoot == null ? "There's no other drive to move to." : Mover.Check(moveSource, moveTargetRoot);
            moveTarget.Text = moveTargetRoot == null ? "" : "New place: " + Mover.TargetFor(moveSource, moveTargetRoot);
            moveHint.Text = why ?? "";
            moveGo.IsEnabled = why == null && !toolBusy;
        }

        async void DoMove()
        {
            string src = moveSource, dst = moveTargetRoot;
            if (src == null || dst == null || Mover.Check(src, dst) != null) return;
            int ok = await ShowDialog("Move " + Path.GetFileName(src.TrimEnd('\\')) + " to " + dst.TrimEnd('\\') + "?",
                "Disk Sweep copies everything to " + Mover.TargetFor(src, dst) + ", checks the copy, links the old place to the new one, then removes the old copy. Close apps that use this folder first.",
                null, null, "Move folder", "Cancel", false, false, null);
            if (ok != 1) return;
            toolBusy = true; cleaning = true;
            moveGo.IsEnabled = false;
            string err;
            try
            {
                var prog = new Progress<string>(s => moveStatus.Text = s);
                long before = Native.FreeBytes(Path.GetPathRoot(src));
                err = await Task.Run(() => Mover.Move(src, dst, prog));
                long freed = Math.Max(0, Native.FreeBytes(Path.GetPathRoot(src)) - before);
                moveStatus.Text = err == null ? "Done. Freed " + Util.Fmt(freed) + " on " + Path.GetPathRoot(src).TrimEnd('\\') + "." : "";
                if (err == null && freed > 0) { Settings.History.Add(new HistoryEntry { When = DateTime.Now, Bytes = freed, Items = 1 }); Settings.Save(); }
            }
            catch (Exception ex) { err = ex.Message; }
            finally { toolBusy = false; cleaning = false; }
            await ShowDialog(err == null ? "Folder moved" : "Folder not moved", err ?? ("It now lives in " + Mover.TargetFor(src, dst) + ". The old place links to it."),
                null, null, "OK", null, false, false, null);
            moveSource = null;
            RefreshMoveDrives();
            UpdateMove();
        }

        // ---------------------------------------------------------- compress
        async void LoadCompactOs()
        {
            compactOsText.Text = "Checking…";
            compactOsGo.IsEnabled = false;
            string state = await Task.Run(() => Compressor.CompactOsState());
            compactOsText.Text = state == "on" ? "Windows is already compressed."
                : state == "off" ? "Not compressed yet." + (isAdmin ? "" : " Needs administrator rights (shield button at the top).")
                : "Status unknown." + (isAdmin ? "" : " Needs administrator rights (shield button at the top).");
            compactOsGo.IsEnabled = state != "on" && isAdmin && !toolBusy;
        }

        async void DoCompactOs()
        {
            int ok = await ShowDialog("Compress Windows?",
                "Runs Windows' CompactOS. It takes 10–20 minutes and the PC stays usable. Note: Windows sometimes decides it isn't worth it on fast PCs; compressing anyway is safe and can be undone.",
                null, null, "Compress Windows", "Cancel", false, false, null);
            if (ok != 1) return;
            toolBusy = true; cleaning = true; compactOsGo.IsEnabled = false;
            compactOsText.Text = "Compressing Windows… keep Disk Sweep open. This takes a while.";
            long saved = 0;
            try { saved = await Task.Run(() => Compressor.EnableCompactOs()); }
            finally { toolBusy = false; cleaning = false; }
            if (saved > 0) { Settings.History.Add(new HistoryEntry { When = DateTime.Now, Bytes = saved, Items = 1 }); Settings.Save(); }
            await ShowDialog("Windows compressed", "Saved " + Util.Fmt(saved) + ".", null, null, "OK", null, false, false, null);
            LoadCompactOs();
        }

        async void DoCompressFolder()
        {
            string f = compFolder;
            if (f == null || Compressor.CheckFolder(f) != null) return;
            int ok = await ShowDialog("Compress " + Path.GetFileName(f.TrimEnd('\\')) + "?",
                "Files stay where they are and keep working. Files in use are skipped. This can take a few minutes for big folders.",
                null, null, "Compress folder", "Cancel", false, false, null);
            if (ok != 1) return;
            toolBusy = true; cleaning = true; compGo.IsEnabled = false;
            compStatus.Text = "Compressing…";
            long saved = 0;
            try { saved = await Task.Run(() => Compressor.CompressFolder(f)); }
            finally { toolBusy = false; cleaning = false; }
            compStatus.Text = "Saved " + Util.Fmt(saved) + ".";
            if (saved > 0) { Settings.History.Add(new HistoryEntry { When = DateTime.Now, Bytes = saved, Items = 1 }); Settings.Save(); }
        }
    }
}
