// Disk Sweep - WPF user interface. Layout lives in MainWindow.xaml (embedded resource).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DiskSweep
{
    // ------------------------------------------------------------------ theme (follows Windows light/dark)
    static class Theme
    {
        public static bool Dark;
        public static Color Bg;

        static readonly Dictionary<string, string> Light = new Dictionary<string, string>
        {
            {"Bg", "#F3F3F3"}, {"Surface", "#FFFFFF"}, {"Hover", "#0C000000"}, {"Selected", "#EEF5FC"}, {"SelectedLine", "#C7DDF2"},
            {"Line", "#E2E2E2"}, {"Text", "#1B1B1B"}, {"Muted", "#626262"}, {"Accent", "#0F6CBD"}, {"AccentFg", "#FFFFFF"},
            {"Track", "#E4E4E4"}, {"Thumb", "#C4C4C4"}, {"DangerBrush", "#C42B1C"}, {"DangerFg", "#FFFFFF"}, {"WarnFg", "#9D5D00"},
            {"AdminBg", "#ECECEC"}, {"AdminFg", "#555555"}, {"SafeBg", "#DFF6DD"}, {"SafeFg", "#0E700E"},
            {"ReviewBg", "#FFF4CE"}, {"ReviewFg", "#8A5300"}
        };

        static readonly Dictionary<string, string> DarkMap = new Dictionary<string, string>
        {
            {"Bg", "#1F1F1F"}, {"Surface", "#2A2A2A"}, {"Hover", "#12FFFFFF"}, {"Selected", "#1D3346"}, {"SelectedLine", "#2D5577"},
            {"Line", "#383838"}, {"Text", "#F3F3F3"}, {"Muted", "#A6A6A6"}, {"Accent", "#4CC2FF"}, {"AccentFg", "#00253D"},
            {"Track", "#3A3A3A"}, {"Thumb", "#5C5C5C"}, {"DangerBrush", "#D13438"}, {"DangerFg", "#FFFFFF"}, {"WarnFg", "#F2C661"},
            {"AdminBg", "#3A3A3A"}, {"AdminFg", "#D0D0D0"}, {"SafeBg", "#1D3B22"}, {"SafeFg", "#7FD47A"},
            {"ReviewBg", "#43391A"}, {"ReviewFg", "#F2C661"}
        };

        public static ResourceDictionary Res;

        public static void Apply(ResourceDictionary r)
        {
            Dark = ReadDark();
            Res = r;
            foreach (var kv in Dark ? DarkMap : Light)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
                b.Freeze();
                r[kv.Key] = b;
            }
            Bg = ((SolidColorBrush)r["Bg"]).Color;
        }

        public static Brush Get(string key) { return (Brush)Res[key]; }

        static bool ReadDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ view models
    class ItemVM : INotifyPropertyChanged
    {
        public CleanItem Item { get; private set; }
        public bool IsAvailable { get; private set; }
        public string Group { get; private set; }
        public string Name { get { return Item.Name; } }
        public string Info { get { return Item.Info; } }
        public string Warning { get; private set; }
        public Visibility WarningVis { get { return string.IsNullOrEmpty(Warning) ? Visibility.Collapsed : Visibility.Visible; } }
        public string SizeText { get { return Item.Size < 0 ? "—" : Util.Fmt(Item.Size); } }
        public string BadgeText { get { return Item.Safety == Safety.Safe ? "Safe" : "Check first"; } }
        public Brush BadgeBg { get { return Theme.Get(Item.Safety == Safety.Safe ? "SafeBg" : "ReviewBg"); } }
        public Brush BadgeFg { get { return Theme.Get(Item.Safety == Safety.Safe ? "SafeFg" : "ReviewFg"); } }
        public Visibility AdminVis { get { return Item.NeedsAdmin ? Visibility.Visible : Visibility.Collapsed; } }
        public string Tip { get; private set; }

        bool chk;
        public bool IsChecked
        {
            get { return chk; }
            set
            {
                if (chk == value) return;
                chk = value;
                Item.Checked = value;
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("IsChecked"));
                if (Changed != null) Changed();
            }
        }

        public event Action Changed;
        public event PropertyChangedEventHandler PropertyChanged;

        public ItemVM(CleanItem it, bool available, string group)
        {
            Item = it;
            IsAvailable = available;
            Group = group;
            chk = it.Checked && available;
            if (!available) Warning = "Needs administrator rights. Click \"Run as administrator\" at the top.";
            else if (!string.IsNullOrEmpty(it.RunningText)) Warning = "Close " + it.RunningText + " first, or files in use are skipped.";
            if (it.Size < 0 && available && it.Mode == Mode.Command) Warning = "Size is only known after cleaning.";
            var paths = it.Paths.Where(Fs.Exists).Take(10).ToList();
            Tip = paths.Count == 0 ? it.Info : string.Join("\n", paths);
        }
    }

    class NavVM
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Glyph { get; set; }
        public string SizeText { get; set; }
    }

    // ------------------------------------------------------------------ modern folder picker (IFileOpenDialog)
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr specs);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si);
            void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si);
            void GetCurrentSelection(out IShellItem si);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult(out IShellItem si);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem si);
            void GetDisplayName(uint sigdn, out IntPtr name);
        }

        public static string Pick(Window owner, string title)
        {
            var dlg = (IFileOpenDialog)new FileOpenDialogCom();
            try
            {
                dlg.SetOptions(0x20 | 0x40 | 0x800); // pick folders, file system only, path must exist
                dlg.SetTitle(title);
                dlg.SetOkButtonLabel("Search this folder");
                if (dlg.Show(new WindowInteropHelper(owner).Handle) != 0) return null;
                IShellItem item;
                dlg.GetResult(out item);
                IntPtr p;
                item.GetDisplayName(0x80058000, out p); // SIGDN_FILESYSPATH
                string path = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                return path;
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }
    }

    // ------------------------------------------------------------------ main window logic
    class MainUi
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static string TestItemPath;
        public Window Window;
        ItemsControl list, dlgList;
        ListBox nav;
        TextBlock driveLabel, driveFree, driveSub, foundText, foundSub, selText, selSub, statusText, scanText, scanSub, cleanLabel;
        TextBlock dlgTitle, dlgText, dlgWarn, dlgStatus;
        ProgressBar driveBar, dlgProgress;
        Button btnScan, btnFolder, btnAdmin, btnSafe, btnNone, btnClean, dlgOk, dlgCancel;
        FrameworkElement scanPanel, emptyPanel, listScroll, overlay, dlgListBox, dlgWarnRow, dlgButtons;

        readonly ObservableCollection<ItemVM> vms = new ObservableCollection<ItemVM>();
        ListCollectionView view;
        string filterKey;
        List<CleanItem> items = new List<CleanItem>();
        List<CleanItem> extra = new List<CleanItem>();
        bool isAdmin, busy, cleaning;
        TaskCompletionSource<bool> dlgTcs;

        static readonly Dictionary<string, string> Glyphs = new Dictionary<string, string>
        {
            {Scanner.CatWin, ""}, {Scanner.CatGpu, ""}, {Scanner.CatDev, ""},
            {Scanner.CatWeb, ""}, {Scanner.CatApp, ""}, {Scanner.CatOld, ""},
            {"Project node_modules", ""}
        };

        public MainUi()
        {
            isAdmin = Native.IsAdmin();
            Scanner.IsAdmin = isAdmin;

            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(s);
            Theme.Apply(Window.Resources);
            if (isAdmin) Window.Title += "  (Administrator)";
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    Window.Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { }

            list = F<ItemsControl>("List"); dlgList = F<ItemsControl>("DlgList"); nav = F<ListBox>("Nav");
            driveLabel = F<TextBlock>("DriveLabel"); driveFree = F<TextBlock>("DriveFree"); driveSub = F<TextBlock>("DriveSub");
            foundText = F<TextBlock>("FoundText"); foundSub = F<TextBlock>("FoundSub");
            selText = F<TextBlock>("SelText"); selSub = F<TextBlock>("SelSub"); statusText = F<TextBlock>("StatusText");
            scanText = F<TextBlock>("ScanText"); scanSub = F<TextBlock>("ScanSub"); cleanLabel = F<TextBlock>("CleanLabel");
            dlgTitle = F<TextBlock>("DlgTitle"); dlgText = F<TextBlock>("DlgText"); dlgWarn = F<TextBlock>("DlgWarn"); dlgStatus = F<TextBlock>("DlgStatus");
            driveBar = F<ProgressBar>("DriveBar"); dlgProgress = F<ProgressBar>("DlgProgress");
            btnScan = F<Button>("BtnScan"); btnFolder = F<Button>("BtnFolder"); btnAdmin = F<Button>("BtnAdmin");
            btnSafe = F<Button>("BtnSafe"); btnNone = F<Button>("BtnNone"); btnClean = F<Button>("BtnClean");
            dlgOk = F<Button>("DlgOk"); dlgCancel = F<Button>("DlgCancel");
            scanPanel = F<FrameworkElement>("ScanPanel"); emptyPanel = F<FrameworkElement>("EmptyPanel");
            listScroll = list; overlay = F<FrameworkElement>("Overlay");
            dlgListBox = F<FrameworkElement>("DlgListBox"); dlgWarnRow = F<FrameworkElement>("DlgWarnRow"); dlgButtons = F<FrameworkElement>("DlgButtons");

            btnAdmin.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;

            view = new ListCollectionView(vms);
            view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            view.Filter = o => filterKey == null || ((ItemVM)o).Item.Category == filterKey;
            list.ItemsSource = view;

            nav.SelectionChanged += (s, e) =>
            {
                var n = nav.SelectedItem as NavVM;
                filterKey = n == null ? null : n.Key;
                view.Refresh();
            };
            btnScan.Click += (s, e) => Scan();
            btnFolder.Click += (s, e) => FindFolder();
            btnAdmin.Click += (s, e) => RestartAdmin();
            btnSafe.Click += (s, e) => TickSafe();
            btnNone.Click += (s, e) => { foreach (var v in vms) v.IsChecked = false; };
            btnClean.Click += (s, e) => Clean();
            dlgOk.Click += (s, e) => CloseDialog(true);
            dlgCancel.Click += (s, e) => CloseDialog(false);
            Window.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && overlay.Visibility == Visibility.Visible && dlgCancel.Visibility == Visibility.Visible) CloseDialog(false);
                if (e.Key == Key.F5 && !busy) Scan();
            };
            Window.Closing += (s, e) => { if (cleaning) e.Cancel = true; };
            Window.SourceInitialized += (s, e) => StyleTitleBar();
            Window.Loaded += (s, e) => Scan();

            UpdateDrive();
            UpdateSel();
        }

        T F<T>(string name) where T : class { return (T)Window.FindName(name); }

        bool Available(CleanItem it) { return isAdmin || !it.NeedsAdmin; }

        void StyleTitleBar()
        {
            try
            {
                var h = new WindowInteropHelper(Window).Handle;
                int dark = Theme.Dark ? 1 : 0;
                DwmSetWindowAttribute(h, 20, ref dark, 4);                      // dark title bar
                int cap = Theme.Bg.R | (Theme.Bg.G << 8) | (Theme.Bg.B << 16);
                DwmSetWindowAttribute(h, 35, ref cap, 4);                       // Windows 11: caption color = app background
            }
            catch { }
        }

        // ---------------------------------------------------------- state -> screen
        void Rebuild()
        {
            var all = items.Concat(extra).Where(i => i.Size < 0 || i.Size >= (1L << 20)).ToList();
            var cats = all.Select(i => i.Category).Distinct().ToList();
            var totals = cats.ToDictionary(c => c, c => all.Where(i => i.Category == c && i.Size > 0 && Available(i)).Sum(i => i.Size));

            vms.Clear();
            foreach (var c in cats)
                foreach (var it in all.Where(i => i.Category == c).OrderByDescending(i => Available(i)).ThenByDescending(i => i.Size))
                {
                    var vm = new ItemVM(it, Available(it), c.ToUpperInvariant() + "   ·   " + Util.Fmt(totals[c]));
                    vm.Changed += UpdateSel;
                    vms.Add(vm);
                }

            string keep = filterKey;
            var navs = new List<NavVM> { new NavVM { Key = null, Name = "All items", Glyph = "", SizeText = Util.Fmt(totals.Values.Sum()) } };
            foreach (var c in cats)
            {
                string g;
                navs.Add(new NavVM { Key = c, Name = c, Glyph = Glyphs.TryGetValue(c, out g) ? g : "", SizeText = Util.Fmt(totals[c]) });
            }
            nav.ItemsSource = navs;
            nav.SelectedItem = navs.FirstOrDefault(n => n.Key == keep) ?? navs[0];

            emptyPanel.Visibility = vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateDrive();
            UpdateSel();
        }

        void UpdateDrive()
        {
            long free = Native.FreeBytes(), total = Native.TotalBytes(), used = total - free;
            driveLabel.Text = "Drive " + Native.SysRoot.TrimEnd('\\');
            driveFree.Text = Util.Fmt(free) + " free";
            driveBar.Value = total > 0 ? used * 100.0 / total : 0;
            driveSub.Text = Util.Fmt(used) + " used of " + Util.Fmt(total);

            var avail = items.Concat(extra).Where(i => Available(i) && (i.Size < 0 || i.Size >= (1L << 20))).ToList();
            int locked = items.Concat(extra).Count(i => !Available(i) && i.Size != 0);
            foundText.Text = items.Count == 0 ? "…" : Util.Fmt(avail.Where(i => i.Size > 0).Sum(i => i.Size));
            foundSub.Text = items.Count == 0 ? "Scanning…"
                : avail.Count + " items found" + (locked > 0 ? "  ·  " + locked + " more with admin rights" : "");
        }

        void UpdateSel()
        {
            var sel = Selected();
            long sum = sel.Where(i => i.Size > 0).Sum(i => i.Size);
            selText.Text = Util.Fmt(sum) + (sel.Any(i => i.Size < 0) ? "+" : "");
            selSub.Text = sel.Count == 0 ? "Tick items below" : sel.Count + " item" + (sel.Count == 1 ? "" : "s") + " selected";
            cleanLabel.Text = sel.Count == 0 ? "Clean selected" : "Clean " + Util.Fmt(sum) + (sel.Any(i => i.Size < 0) ? "+" : "");
            btnClean.IsEnabled = !busy && sel.Count > 0;
        }

        List<CleanItem> Selected()
        {
            return vms.Where(v => v.IsChecked && v.IsAvailable).Select(v => v.Item).ToList();
        }

        void SetBusy(bool on)
        {
            busy = on;
            foreach (var b in new[] { btnScan, btnFolder, btnAdmin, btnSafe, btnNone }) b.IsEnabled = !on;
            UpdateSel();
        }

        void TickSafe()
        {
            foreach (ItemVM v in view)
                if (v.IsAvailable && v.Item.Safety == Safety.Safe && v.Item.Mode != Mode.Command) v.IsChecked = true;
        }

        static void MarkRunning(CleanItem it)
        {
            var r = it.Processes.Where(Native.IsRunning).Select(n => char.ToUpper(n[0]) + n.Substring(1)).ToList();
            it.RunningText = r.Count == 0 ? null : string.Join(", ", r);
        }

        // ---------------------------------------------------------- actions
        async void Scan()
        {
            if (busy) return;
            SetBusy(true);
            listScroll.Visibility = Visibility.Collapsed;
            emptyPanel.Visibility = Visibility.Collapsed;
            scanPanel.Visibility = Visibility.Visible;
            scanText.Text = "Scanning your drive…";
            scanSub.Text = "This usually takes under a minute.";
            try
            {
                var prog = new Progress<string>(s => scanSub.Text = s);
                var prev = new HashSet<string>(items.Concat(extra).Where(i => i.Checked).Select(i => i.Key));
                var ex = extra.ToList();
                var result = await Task.Run(() =>
                {
                    var r = Scanner.ScanAll(prog);
                    if (TestItemPath != null && Fs.Exists(TestItemPath))
                    {
                        var t = new CleanItem { Category = "Test", Name = "Test folder", Info = TestItemPath, Mode = Mode.Whole };
                        t.Paths.Add(TestItemPath);
                        t.Size = Scanner.SizeOf(t);
                        r.Insert(0, t);
                    }
                    foreach (var it in ex) it.Size = Scanner.SizeOf(it);
                    foreach (var it in r.Concat(ex)) MarkRunning(it);
                    return r;
                });
                foreach (var it in result) it.Checked = prev.Contains(it.Key);
                items = result;
                extra = ex.Where(i => i.Size != 0).ToList();
                Rebuild();
                statusText.Text = "Last scan " + DateTime.Now.ToShortTimeString() + ".  Hover an item to see its folders.";
            }
            catch (Exception e)
            {
                statusText.Text = "Scan failed: " + e.Message;
            }
            finally
            {
                scanPanel.Visibility = Visibility.Collapsed;
                listScroll.Visibility = Visibility.Visible;
                SetBusy(false);
            }
        }

        async void FindFolder()
        {
            if (busy) return;
            string root;
            try { root = FolderPicker.Pick(Window, "Pick the folder with your code projects"); }
            catch (Exception e) { statusText.Text = "Folder picker failed: " + e.Message; return; }
            if (string.IsNullOrEmpty(root)) return;

            SetBusy(true);
            listScroll.Visibility = Visibility.Collapsed;
            emptyPanel.Visibility = Visibility.Collapsed;
            scanPanel.Visibility = Visibility.Visible;
            scanText.Text = "Looking for node_modules…";
            scanSub.Text = root;
            try
            {
                var prog = new Progress<string>(s => scanSub.Text = s);
                var found = await Task.Run(() =>
                {
                    var f = Scanner.FindProjects(root, prog);
                    foreach (var it in f) MarkRunning(it);
                    return f;
                });
                string prefix = root.TrimEnd('\\') + "\\";
                extra.RemoveAll(i => i.Paths.Count > 0 && i.Paths[0].StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                extra.AddRange(found.Where(f => f.Size != 0));
                filterKey = found.Count > 0 ? "Project node_modules" : filterKey;
                Rebuild();
                statusText.Text = found.Count == 0 ? "No node_modules folders in " + root + "."
                    : "Found " + found.Count + " node_modules folders in " + root + ".";
            }
            catch (Exception e)
            {
                statusText.Text = "Search failed: " + e.Message;
            }
            finally
            {
                scanPanel.Visibility = Visibility.Collapsed;
                listScroll.Visibility = Visibility.Visible;
                if (vms.Count == 0) emptyPanel.Visibility = Visibility.Visible;
                SetBusy(false);
            }
        }

        async void Clean()
        {
            var sel = Selected();
            if (sel.Count == 0 || busy) return;
            long total = sel.Where(i => i.Size > 0).Sum(i => i.Size);
            string totalText = Util.Fmt(total) + (sel.Any(i => i.Size < 0) ? "+" : "");

            var rows = sel.Select(i => new KeyValuePair<string, string>(i.Name, i.Size < 0 ? "—" : Util.Fmt(i.Size))).ToList();
            var running = sel.Where(i => !string.IsNullOrEmpty(i.RunningText)).Select(i => i.RunningText).Distinct().ToList();
            string warn = running.Count > 0 ? "Close " + string.Join(", ", running) + " first, or files in use will be skipped." : null;

            bool ok = await ShowDialog("Delete " + sel.Count + " item" + (sel.Count == 1 ? "" : "s") + "?",
                "This permanently removes about " + totalText + ". It can't be undone.",
                rows, warn, "Delete " + totalText, "Cancel", true, false);
            if (!ok) return;

            cleaning = true;
            SetBusy(true);
            var progressDialog = ShowDialog("Cleaning…", "Please keep Disk Sweep open.", null, null, null, null, false, true);
            dlgProgress.Maximum = sel.Count;
            dlgProgress.Value = 0;
            long freeBefore = Native.FreeBytes();
            var results = new List<KeyValuePair<string, string>>();
            string error = null;
            try
            {
                for (int i = 0; i < sel.Count; i++)
                {
                    var it = sel[i];
                    dlgStatus.Text = (i + 1) + " of " + sel.Count + ":  " + it.Name + (it.Mode == Mode.Command ? "  (can take a few minutes)" : "");
                    long freed = await Task.Run(() => Cleaner.Clean(it));
                    bool partial = it.Size > 0 && freed < it.Size * 0.9;
                    results.Add(new KeyValuePair<string, string>(it.Name, Util.Fmt(freed) + (partial ? "  (some in use)" : "")));
                    it.Checked = false;
                    dlgProgress.Value = i + 1;
                }
            }
            catch (Exception e) { error = "Stopped early: " + e.Message; }
            cleaning = false;

            long gained = Math.Max(0, Native.FreeBytes() - freeBefore);
            bool anyPartial = results.Any(r => r.Value.Contains("in use"));
            await ShowDialog("Freed " + Util.Fmt(gained),
                "Your drive now has " + Util.Fmt(Native.FreeBytes()) + " free.",
                results,
                error ?? (anyPartial ? "Some files were in use and were skipped. Close those apps and clean again to get the rest." : null),
                "Done", null, false, false);
            SetBusy(false);
            Scan();
        }

        void RestartAdmin()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location) { Verb = "runas", UseShellExecute = true });
                Window.Close();
            }
            catch (Win32Exception) { /* user said no */ }
        }

        // ---------------------------------------------------------- in-window dialog
        Task<bool> ShowDialog(string title, string text, List<KeyValuePair<string, string>> rows, string warn,
                              string okText, string cancelText, bool danger, bool progress)
        {
            dlgTitle.Text = title;
            dlgText.Text = text;
            dlgList.ItemsSource = rows;
            dlgListBox.Visibility = rows == null || rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            dlgWarn.Text = warn ?? "";
            dlgWarnRow.Visibility = string.IsNullOrEmpty(warn) ? Visibility.Collapsed : Visibility.Visible;
            dlgProgress.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
            dlgStatus.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
            dlgStatus.Text = "";
            dlgOk.Content = okText;
            dlgOk.Visibility = okText == null ? Visibility.Collapsed : Visibility.Visible;
            dlgOk.Style = (Style)Window.Resources[danger ? "Danger" : "Primary"];
            dlgCancel.Content = cancelText;
            dlgCancel.Visibility = cancelText == null ? Visibility.Collapsed : Visibility.Visible;
            dlgButtons.Visibility = okText == null && cancelText == null ? Visibility.Collapsed : Visibility.Visible;
            overlay.Visibility = Visibility.Visible;
            if (cancelText != null) dlgCancel.Focus(); else if (okText != null) dlgOk.Focus();
            dlgTcs = new TaskCompletionSource<bool>();
            return dlgTcs.Task;
        }

        void CloseDialog(bool result)
        {
            overlay.Visibility = Visibility.Collapsed;
            var t = dlgTcs;
            dlgTcs = null;
            if (t != null) t.TrySetResult(result);
        }
    }

    // ------------------------------------------------------------------ entry
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

            // Developer checks without UI:
            //   DiskSweep.exe --report out.txt [projectFolder]   scan only, write results
            //   DiskSweep.exe --selftest emptyFolder             test delete logic on a fixture
            if (args.Length >= 2 && args[0] == "--report") return Dev.Report(args);
            if (args.Length >= 2 && args[0] == "--selftest") return Dev.SelfTest(args[1]);
            //   DiskSweep.exe --test-item <folder>                adds that folder as a deletable item (UI testing)
            if (args.Length >= 2 && args[0] == "--test-item") MainUi.TestItemPath = Path.GetFullPath(args[1]);

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "Disk Sweep", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            var ui = new MainUi();
            return app.Run(ui.Window);
        }
    }
}
