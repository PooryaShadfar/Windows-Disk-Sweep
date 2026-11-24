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
            {"ReviewBg", "#FFF4CE"}, {"ReviewFg", "#8A5300"}, {"BadBg", "#FDE7E9"}, {"BadFg", "#A4262C"}
        };

        static readonly Dictionary<string, string> DarkMap = new Dictionary<string, string>
        {
            {"Bg", "#1F1F1F"}, {"Surface", "#2A2A2A"}, {"Hover", "#12FFFFFF"}, {"Selected", "#1D3346"}, {"SelectedLine", "#2D5577"},
            {"Line", "#383838"}, {"Text", "#F3F3F3"}, {"Muted", "#A6A6A6"}, {"Accent", "#4CC2FF"}, {"AccentFg", "#00253D"},
            {"Track", "#3A3A3A"}, {"Thumb", "#5C5C5C"}, {"DangerBrush", "#D13438"}, {"DangerFg", "#FFFFFF"}, {"WarnFg", "#F2C661"},
            {"AdminBg", "#3A3A3A"}, {"AdminFg", "#D0D0D0"}, {"SafeBg", "#1D3B22"}, {"SafeFg", "#7FD47A"},
            {"ReviewBg", "#43391A"}, {"ReviewFg", "#F2C661"}, {"BadBg", "#4A2427"}, {"BadFg", "#FF99A4"}
        };

        public static ResourceDictionary Res;

        // mode: "System" follows Windows, or "Light" / "Dark"
        public static void Apply(ResourceDictionary r, string mode)
        {
            Dark = mode == "Dark" || (mode != "Light" && ReadDark());
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
        public Visibility RecycleVis { get { return Item.ToRecycleBin ? Visibility.Visible : Visibility.Collapsed; } }
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
            if (!available) Warning = "Needs administrator rights. Click the shield button at the top to run as administrator.";
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

    // ------------------------------------------------------------------ drive health cards
    class StatVM
    {
        public string Label { get; set; }
        public string Value { get; set; }
        public string Note { get; set; }
    }

    class DiskVM
    {
        public string Model { get; set; }
        public string Sub { get; set; }
        public string StatusText { get; set; }
        public string StatusGlyph { get; set; }
        public Brush StatusBg { get; set; }
        public Brush StatusFg { get; set; }
        public string WearText { get; set; }
        public double WearValue { get; set; }
        public Visibility WearVis { get; set; }
        public List<StatVM> Stats { get; set; }
        public string ProblemsText { get; set; }
        public Visibility ProblemsVis { get; set; }
        public string Hint { get; set; }
        public Visibility HintVis { get; set; }

        public static string Glyph(HealthLevel l)
        {
            return l == HealthLevel.Good ? "" : l == HealthLevel.Warning ? "" : l == HealthLevel.Bad ? "" : "";
        }

        public static string BgKey(HealthLevel l)
        {
            return l == HealthLevel.Good ? "SafeBg" : l == HealthLevel.Warning ? "ReviewBg" : l == HealthLevel.Bad ? "BadBg" : "AdminBg";
        }

        public static string FgKey(HealthLevel l)
        {
            return l == HealthLevel.Good ? "SafeFg" : l == HealthLevel.Warning ? "ReviewFg" : l == HealthLevel.Bad ? "BadFg" : "AdminFg";
        }

        public DiskVM(DiskHealth d, bool admin)
        {
            Model = d.Model;
            Sub = d.Bus + " " + d.Kind + "  ·  " + Util.Fmt(d.Size) + (d.Letters.Count > 0 ? "  ·  " + string.Join(" ", d.Letters) : "");
            StatusText = Health.LevelText(d.Level);
            StatusGlyph = Glyph(d.Level);
            StatusBg = Theme.Get(BgKey(d.Level));
            StatusFg = Theme.Get(FgKey(d.Level));

            WearVis = d.WearPercent.HasValue ? Visibility.Visible : Visibility.Collapsed;
            WearValue = Math.Min(100, d.WearPercent ?? 0);
            WearText = d.WearPercent.HasValue ? d.WearPercent + "%  ·  about " + Math.Max(0, 100 - d.WearPercent.Value) + "% left" : "";

            var s = new List<StatVM> { new StatVM { Label = "Windows status", Value = d.WindowsStatus, Note = "from Windows" } };
            if (d.Temperature.HasValue)
                s.Add(new StatVM { Label = "Temperature", Value = d.Temperature + " °C", Note = d.TemperatureMax.HasValue ? "highest seen " + d.TemperatureMax + " °C" : "" });
            if (d.PowerOnHours.HasValue)
            {
                double years = d.PowerOnHours.Value / (24.0 * 365);
                s.Add(new StatVM { Label = "Power-on time", Value = d.PowerOnHours.Value.ToString("N0") + " hours", Note = "about " + years.ToString("0.0") + " years running" });
            }
            if (d.ReadErrors.HasValue || d.WriteErrors.HasValue)
            {
                long e = (d.ReadErrors ?? 0) + (d.WriteErrors ?? 0);
                s.Add(new StatVM { Label = "Uncorrected errors", Value = e == 0 ? "None" : e.ToString("N0"), Note = "reads and writes" });
            }
            if (d.Reallocated.HasValue) s.Add(new StatVM { Label = "Replaced sectors", Value = d.Reallocated == 0 ? "None" : d.Reallocated.Value.ToString("N0"), Note = "bad spots the drive swapped out" });
            if (d.Pending.HasValue) s.Add(new StatVM { Label = "Pending sectors", Value = d.Pending == 0 ? "None" : d.Pending.Value.ToString("N0"), Note = "unreadable spots waiting" });
            if (d.PredictFailure.HasValue) s.Add(new StatVM { Label = "Failure warning", Value = d.PredictFailure.Value ? "Predicted" : "None", Note = "the drive's own check (SMART)" });
            if (d.StartStops.HasValue) s.Add(new StatVM { Label = "Power cycles", Value = d.StartStops.Value.ToString("N0"), Note = "times switched on" });
            Stats = s;

            ProblemsText = string.Join("\n", d.Problems.Select(p => "•  " + p));
            if (d.Level == HealthLevel.Bad) ProblemsText += "\nBack up important files from this drive soon.";
            ProblemsVis = d.Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            Hint = d.DetailsAvailable ? "" : admin
                ? "This drive doesn't share detailed health data with Windows. Some USB enclosures and older drives don't."
                : "Run as administrator to see life used, temperature, power-on time and errors.";
            HintVis = string.IsNullOrEmpty(Hint) ? Visibility.Collapsed : Visibility.Visible;
        }
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
    partial class MainUi
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static string TestItemPath;
        public static bool TestRecycle;
        public Window Window;
        ItemsControl list, dlgList;
        ListBox nav;
        TextBlock driveLabel, driveFree, driveSub, foundText, foundSub, selText, selSub, statusText, scanText, scanSub, cleanLabel;
        TextBlock dlgTitle, dlgText, dlgWarn, dlgStatus;
        ProgressBar driveBar, dlgProgress;
        Button btnScan, btnFolder, btnAdmin, btnSafe, btnNone, btnClean, dlgOk, dlgCancel, dlgAlt;
        FrameworkElement scanPanel, emptyPanel, listScroll, overlay, dlgListBox, dlgWarnRow, dlgButtons;
        // settings page + drives
        FrameworkElement settingsOverlay;
        ItemsControl drivesCtl, hiddenList, histList;
        RadioButton themeSystem, themeLight, themeDark;
        CheckBox optRecycle, optAlert;
        TextBox alertGB, cacheDays, projectDays;
        TextBlock alertStatus, hiddenEmpty, histTotal, histSub, aboutText;
        string selectedDrive = Native.SysRoot;
        bool loadingSettings;
        const string RepoUrl = "https://github.com/PooryaShadfar/Windows-Disk-Sweep";

        readonly ObservableCollection<ItemVM> vms = new ObservableCollection<ItemVM>();
        ListCollectionView view;
        string filterKey;
        List<CleanItem> items = new List<CleanItem>();
        bool isAdmin, busy, cleaning;
        TaskCompletionSource<int> dlgTcs;

        static readonly Dictionary<string, string> Glyphs = new Dictionary<string, string>
        {
            {Scanner.CatWin, ""}, {Scanner.CatGpu, ""}, {Scanner.CatDev, ""},
            {Scanner.CatWeb, ""}, {Scanner.CatApp, ""}, {Scanner.CatOld, ""},
            {Scanner.CatProj, "\uE8B7"}, {Scanner.CatLeft, "\uEA86"}, {Scanner.CatBig, "\uE7C3"},
            {Scanner.CatGame, "\uE7FC"}, {Scanner.CatVirt, "\uE7F8"}
        };

        public MainUi()
        {
            isAdmin = Native.IsAdmin();
            Scanner.IsAdmin = isAdmin;

            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(s);
            Settings.Load();
            Theme.Apply(Window.Resources, Settings.Theme);
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
            dlgOk = F<Button>("DlgOk"); dlgCancel = F<Button>("DlgCancel"); dlgAlt = F<Button>("DlgAlt");
            scanPanel = F<FrameworkElement>("ScanPanel"); emptyPanel = F<FrameworkElement>("EmptyPanel");
            listScroll = list; overlay = F<FrameworkElement>("Overlay");
            dlgListBox = F<FrameworkElement>("DlgListBox"); dlgWarnRow = F<FrameworkElement>("DlgWarnRow"); dlgButtons = F<FrameworkElement>("DlgButtons");

            btnAdmin.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;

            settingsOverlay = F<FrameworkElement>("SettingsOverlay");
            drivesCtl = F<ItemsControl>("Drives"); hiddenList = F<ItemsControl>("HiddenList"); histList = F<ItemsControl>("HistList");
            themeSystem = F<RadioButton>("ThemeSystem"); themeLight = F<RadioButton>("ThemeLight"); themeDark = F<RadioButton>("ThemeDark");
            optRecycle = F<CheckBox>("OptRecycle"); optAlert = F<CheckBox>("OptAlert"); alertGB = F<TextBox>("AlertGB");
            cacheDays = F<TextBox>("CacheDays"); projectDays = F<TextBox>("ProjectDays");
            alertStatus = F<TextBlock>("AlertStatus"); hiddenEmpty = F<TextBlock>("HiddenEmpty");
            histTotal = F<TextBlock>("HistTotal"); histSub = F<TextBlock>("HistSub"); aboutText = F<TextBlock>("AboutText");
            WireSettings();
            WireDrives();
            WireHealth();
            WireTools();
            WireTools2();

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
            btnFolder.Click += (s, e) => CodeFolders();
            btnAdmin.Click += (s, e) => RestartAdmin();
            btnSafe.Click += (s, e) => TickSafe();
            btnNone.Click += (s, e) => { foreach (var v in vms) v.IsChecked = false; };
            btnClean.Click += (s, e) => Clean();
            dlgOk.Click += (s, e) => CloseDialog(1);
            dlgAlt.Click += (s, e) => CloseDialog(2);
            dlgCancel.Click += (s, e) => CloseDialog(0);
            Window.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && overlay.Visibility == Visibility.Visible && dlgCancel.Visibility == Visibility.Visible) CloseDialog(0);
                else if (e.Key == Key.Escape && settingsOverlay.Visibility == Visibility.Visible) CloseSettings();
                else if (e.Key == Key.Escape && healthOverlay.Visibility == Visibility.Visible) healthOverlay.Visibility = Visibility.Collapsed;
                else if (e.Key == Key.Escape && toolsOverlay.Visibility == Visibility.Visible) CloseTools();
                if (e.Key == Key.F5 && !busy) Scan();
            };
            Window.Closing += (s, e) => { if (cleaning) e.Cancel = true; };
            Window.SourceInitialized += (s, e) => StyleTitleBar();
            Window.Loaded += (s, e) => Scan();

            UpdateDrive();
            UpdateSel();
        }

        T F<T>(string name) where T : class { return (T)Window.FindName(name); }

        internal static string HideKey(CleanItem it)
        {
            return it.Category + "|" + (it.Paths.Count > 0 ? it.Paths[0] : it.Name);
        }

        // ---------------------------------------------------------- drives
        // ---------------------------------------------------------- drive health
        List<DiskHealth> disks = new List<DiskHealth>();
        FrameworkElement healthOverlay;
        ItemsControl healthList;
        TextBlock healthIntro, healthBadgeText;
        System.Windows.Shapes.Ellipse healthDot;
        Button btnHealthBadge;

        void WireHealth()
        {
            healthOverlay = F<FrameworkElement>("HealthOverlay");
            healthList = F<ItemsControl>("HealthList");
            healthIntro = F<TextBlock>("HealthIntro");
            healthBadgeText = F<TextBlock>("HealthBadgeText");
            healthDot = F<System.Windows.Shapes.Ellipse>("HealthDot");
            btnHealthBadge = F<Button>("BtnHealthBadge");
            F<Button>("BtnHealth").Click += (s, e) => OpenHealth();
            btnHealthBadge.Click += (s, e) => OpenHealth();
            F<Button>("BtnCloseHealth").Click += (s, e) => healthOverlay.Visibility = Visibility.Collapsed;
            healthOverlay.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == healthOverlay) healthOverlay.Visibility = Visibility.Collapsed; };
            F<Button>("BtnHealthRefresh").Click += (s, e) => LoadHealth();
            var admin = F<Button>("BtnHealthAdmin");
            admin.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;
            admin.Click += (s, e) => RestartAdmin();
            Window.Loaded += (s, e) => LoadHealth();   // after the UI thread is running, so results come back to it
        }

        async void LoadHealth()
        {
            healthIntro.Text = "Reading drive health…";
            try { disks = await Task.Run(() => Health.Read()); }
            catch { disks = new List<DiskHealth>(); }
            FillHealth();
            UpdateHealthBadge();
        }

        void OpenHealth()
        {
            FillHealth();
            healthOverlay.Visibility = Visibility.Visible;
            F<Button>("BtnCloseHealth").Focus();
        }

        void FillHealth()
        {
            healthIntro.Text = disks.Count == 0 ? "Windows didn't report any drives."
                : isAdmin ? "Read from your drives just now. The numbers come from the drives themselves, through Windows."
                : "Basic status works for everyone. Life used, temperature, power-on time and error counts need administrator rights.";
            healthList.ItemsSource = disks.Select(d => new DiskVM(d, isAdmin)).ToList();
        }

        void UpdateHealthBadge()
        {
            if (btnHealthBadge == null) return;
            string letter = selectedDrive.TrimEnd('\\');
            var d = disks.FirstOrDefault(x => x.Letters.Any(l => l.Equals(letter, StringComparison.OrdinalIgnoreCase)));
            if (d == null) { btnHealthBadge.Visibility = Visibility.Collapsed; return; }
            healthDot.Fill = Theme.Get(DiskVM.FgKey(d.Level));
            healthBadgeText.Text = d.Kind + "  ·  " + Health.LevelText(d.Level) + (d.WearPercent.HasValue ? "  ·  " + d.WearPercent + "% used" : "");
            btnHealthBadge.Visibility = Visibility.Visible;
        }

        class DriveVM
        {
            public string Root { get; set; }
            public string Label { get; set; }
            public string Tip { get; set; }
            public bool IsSelected { get; set; }
        }

        void WireDrives()
        {
            drivesCtl.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) =>
            {
                var rb = e.OriginalSource as RadioButton;
                var d = rb == null ? null : rb.DataContext as DriveVM;
                if (d == null || busy || d.Root.Equals(selectedDrive, StringComparison.OrdinalIgnoreCase)) return;
                selectedDrive = d.Root;
                filterKey = null;
                Scan();
            }));
            RefreshDrives();
        }

        void RefreshDrives()
        {
            var drives = Settings.FixedDrives().Select(d => new DriveVM
            {
                Root = d.Name,
                Label = d.Name.TrimEnd('\\'),
                Tip = (string.IsNullOrEmpty(d.VolumeLabel) ? "Drive " + d.Name.TrimEnd('\\') : d.VolumeLabel) + ":  " + Util.Fmt(d.AvailableFreeSpace) + " free",
                IsSelected = d.Name.Equals(selectedDrive, StringComparison.OrdinalIgnoreCase)
            }).ToList();
            drivesCtl.ItemsSource = drives;
            drivesCtl.Visibility = drives.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------- settings page
        void WireSettings()
        {
            F<Button>("BtnSettings").Click += (s, e) => OpenSettings();
            F<Button>("BtnCloseSettings").Click += (s, e) => CloseSettings();
            settingsOverlay.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == settingsOverlay) CloseSettings(); };

            RoutedEventHandler theme = (s, e) =>
            {
                if (loadingSettings) return;
                Settings.Theme = themeDark.IsChecked == true ? "Dark" : themeLight.IsChecked == true ? "Light" : "System";
                Settings.Save();
                ApplyTheme();
            };
            themeSystem.Checked += theme; themeLight.Checked += theme; themeDark.Checked += theme;

            RoutedEventHandler recycleChanged = (s, e) =>
            {
                if (loadingSettings) return;
                Settings.RecycleReview = optRecycle.IsChecked == true;
                Settings.Save();
                Rebuild();
            };
            optRecycle.Checked += recycleChanged;
            optRecycle.Unchecked += recycleChanged;
            optAlert.Checked += (s, e) => SaveAlert();
            optAlert.Unchecked += (s, e) => SaveAlert();
            alertGB.LostFocus += (s, e) => SaveAlert();
            alertGB.KeyDown += (s, e) => { if (e.Key == Key.Enter) SaveAlert(); };
            alertGB.PreviewTextInput += (s, e) => { e.Handled = !e.Text.All(char.IsDigit); };
            foreach (var box in new[] { cacheDays, projectDays })
            {
                box.PreviewTextInput += (s, e) => { e.Handled = !e.Text.All(char.IsDigit); };
                box.LostFocus += (s, e) => SaveDays();
                box.KeyDown += (s, e) => { if (e.Key == Key.Enter) SaveDays(); };
            }
            hiddenList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var b = e.OriginalSource as Button;
                if (b == null || (b.Tag as string) != "unhide") return;
                var kv = (KeyValuePair<string, string>)b.DataContext;
                Settings.Hidden.Remove(kv.Key);
                Settings.Save();
                FillSettings();
                Rebuild();
            }));
            list.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var b = e.OriginalSource as Button;
                if (b == null || (b.Tag as string) != "hide") return;
                var vm = b.DataContext as ItemVM;
                if (vm == null || busy) return;
                Settings.Hidden[HideKey(vm.Item)] = vm.Item.Name + "  (" + vm.Item.Category + ")";
                Settings.Save();
                vm.IsChecked = false;
                Rebuild();
                statusText.Text = "Hidden \"" + vm.Item.Name + "\". Show it again in Settings.";
            }));
            F<Button>("BtnClearHist").Click += (s, e) => { Settings.History.Clear(); Settings.Save(); FillSettings(); UpdateStatus(); };
            F<Button>("BtnGitHub").Click += (s, e) => { try { Process.Start(RepoUrl); } catch { } };

            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Settings.Theme == "System")
                    Window.Dispatcher.BeginInvoke(new Action(ApplyTheme));
            };
        }

        void ApplyTheme()
        {
            Theme.Apply(Window.Resources, Settings.Theme);
            StyleTitleBar();
            Rebuild();   // badge colors are bound per item
            FillHealth();
            UpdateHealthBadge();
        }

        void SaveAlert()
        {
            if (loadingSettings) return;
            int gb;
            if (int.TryParse(alertGB.Text, out gb) && gb > 0 && gb < 100000) Settings.AlertGB = gb;
            alertGB.Text = Settings.AlertGB.ToString();
            bool want = optAlert.IsChecked == true;
            bool ok = Settings.SetAlertTask(want);
            Settings.AlertOn = want && ok;
            Settings.Save();
            if (want && !ok) optAlert.IsChecked = false;
            alertStatus.Text = !want ? "Off."
                : ok ? "On. You'll get a notification if any drive drops below " + Settings.AlertGB + " GB free (at most once a day)."
                : "Couldn't create the scheduled check. Try again, or run Disk Sweep as administrator.";
        }

        void SaveDays()
        {
            if (loadingSettings) return;
            int c, p;
            if (int.TryParse(cacheDays.Text, out c) && c >= 0 && c <= 3650) Settings.CacheDays = c;
            if (int.TryParse(projectDays.Text, out p) && p >= 0 && p <= 3650) Settings.ProjectDays = p;
            cacheDays.Text = Settings.CacheDays.ToString();
            projectDays.Text = Settings.ProjectDays.ToString();
            Settings.Save();
        }

        void OpenSettings()
        {
            FillSettings();
            settingsOverlay.Visibility = Visibility.Visible;
            F<Button>("BtnCloseSettings").Focus();
        }

        void CloseSettings()
        {
            SaveAlert();
            SaveDays();
            settingsOverlay.Visibility = Visibility.Collapsed;
        }

        void FillSettings()
        {
            loadingSettings = true;
            themeSystem.IsChecked = Settings.Theme == "System";
            themeLight.IsChecked = Settings.Theme == "Light";
            themeDark.IsChecked = Settings.Theme == "Dark";
            optRecycle.IsChecked = Settings.RecycleReview;
            bool task = Settings.AlertTaskExists();
            optAlert.IsChecked = Settings.AlertOn && task;
            alertGB.Text = Settings.AlertGB.ToString();
            cacheDays.Text = Settings.CacheDays.ToString();
            projectDays.Text = Settings.ProjectDays.ToString();
            FillAuto();
            alertStatus.Text = optAlert.IsChecked == true
                ? "On. You'll get a notification if any drive drops below " + Settings.AlertGB + " GB free (at most once a day)."
                : "Off.";

            var hidden = Settings.Hidden.OrderBy(kv => kv.Value).ToList();
            hiddenList.ItemsSource = hidden;
            hiddenEmpty.Visibility = hidden.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            histTotal.Text = Util.Fmt(Settings.TotalCleaned) + " cleaned";
            histSub.Text = Settings.History.Count == 0 ? "No cleanups yet."
                : "In " + Settings.History.Count + " cleanup" + (Settings.History.Count == 1 ? "" : "s") + ". Latest first:";
            histList.ItemsSource = Settings.History.OrderByDescending(h => h.When).Take(12)
                .Select(h => new KeyValuePair<string, string>(h.When.ToString("yyyy-MM-dd  HH:mm") + "   ·   " + h.Items + " item" + (h.Items == 1 ? "" : "s"),
                                                            Util.Fmt(h.Bytes))).ToList();

            F<Button>("BtnClearHist").Visibility = Settings.History.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            aboutText.Text = "Disk Sweep " + ver.Major + "." + ver.Minor + "." + ver.Build + ".  Free and open source (MIT). "
                + "No telemetry, no network access. Settings are stored in " + Settings.Dir + ".";
            loadingSettings = false;
        }

        void UpdateStatus()
        {
            var folders = Settings.LoadFolders();
            statusText.Text = (lastScan == DateTime.MinValue ? "" : "Last scan " + lastScan.ToShortTimeString() + ".  ")
                + (Settings.TotalCleaned > 0 ? "Cleaned so far: " + Util.Fmt(Settings.TotalCleaned) + ".  " : "")
                + (folders.Count > 0 ? "Code folders: " + string.Join(", ", folders) + "." : "Add your code folders to find build folders.")
                + (Scanner.RecentProjectsKept > 0 ? "  Kept " + Scanner.RecentProjectsKept + " build folders of projects you used in the last " + Settings.ProjectDays + " days." : "");
        }

        DateTime lastScan = DateTime.MinValue;

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
            foreach (var it in items)
                it.ToRecycleBin = it.AlwaysRecycle || it.Category == Scanner.CatBig
                    || (Settings.RecycleReview && it.Safety == Safety.Review && (it.Mode == Mode.Whole || it.Mode == Mode.Contents));
            var all = items.Where(i => (i.Size < 0 || i.Size >= (1L << 20)) && !Settings.Hidden.ContainsKey(HideKey(i))).ToList();
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
            var navs = new List<NavVM> { new NavVM { Key = null, Name = "All items", Glyph = "", SizeText = Util.Fmt(totals.Where(kv => kv.Key != Scanner.CatBig).Sum(kv => kv.Value)) } };
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
            long free = Native.FreeBytes(selectedDrive), total = Native.TotalBytes(selectedDrive), used = total - free;
            driveLabel.Text = "Drive " + selectedDrive.TrimEnd('\\');
            driveFree.Text = Util.Fmt(free) + " free";
            driveBar.Value = total > 0 ? used * 100.0 / total : 0;
            driveSub.Text = Util.Fmt(used) + " used of " + Util.Fmt(total);
            UpdateHealthBadge();

            var shown = items.Where(i => !Settings.Hidden.ContainsKey(HideKey(i))).ToList();
            var avail = shown.Where(i => Available(i) && (i.Size < 0 || i.Size >= (1L << 20))).ToList();
            int locked = shown.Count(i => !Available(i) && i.Size != 0);
            // Large files are personal: list them, but don't count them as junk you "can free".
            var junk = avail.Where(i => i.Category != Scanner.CatBig).ToList();
            long big = avail.Where(i => i.Category == Scanner.CatBig && i.Size > 0).Sum(i => i.Size);
            foundText.Text = items.Count == 0 ? "…" : Util.Fmt(junk.Where(i => i.Size > 0).Sum(i => i.Size));
            foundSub.Text = items.Count == 0 ? "Scanning…"
                : junk.Count + " items found" + (locked > 0 ? "  ·  " + locked + " more with admin rights" : "")
                  + (big > 0 ? "\n+ " + Util.Fmt(big) + " in large files to review" : "");
        }

        void UpdateSel()
        {
            var sel = Selected();
            long sum = sel.Where(i => i.Size > 0).Sum(i => i.Size);
            selText.Text = Util.Fmt(sum) + (sel.Any(i => i.Size < 0) ? "+" : "");
            selSub.Text = sel.Count == 0 ? "Check items below" : sel.Count + " item" + (sel.Count == 1 ? "" : "s") + " selected";
            cleanLabel.Text = sel.Count == 0 ? "Remove Selected" : "Remove " + Util.Fmt(sum) + (sel.Any(i => i.Size < 0) ? "+" : "");
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
            scanSub.Text = "This usually takes about a minute.";
            try
            {
                var prog = new Progress<string>(s => scanSub.Text = s);
                var prev = new HashSet<string>(items.Where(i => i.Checked).Select(i => i.Key));
                string drive = selectedDrive;
                var folders = Settings.LoadFolders().Where(f => f.StartsWith(drive, StringComparison.OrdinalIgnoreCase)).ToList();
                var result = await Task.Run(() =>
                {
                    Scanner.RecentProjectsKept = 0;
                    var r = Scanner.ScanAll(prog, drive);
                    foreach (var f in folders)
                        if (Directory.Exists(f)) r.AddRange(Scanner.FindProjectJunk(f, prog));
                    if (TestItemPath != null && Fs.Exists(TestItemPath))
                    {
                        var t = new CleanItem { Category = "Test", Name = "Test folder", Info = TestItemPath, Mode = Mode.Whole, ToRecycleBin = TestRecycle, AlwaysRecycle = TestRecycle };
                        t.Paths.Add(TestItemPath);
                        t.Size = Scanner.SizeOf(t);
                        r.Insert(0, t);
                    }
                    foreach (var it in r) MarkRunning(it);
                    return r;
                });
                foreach (var it in result) it.Checked = prev.Contains(it.Key);
                items = result;
                Rebuild();
                lastScan = DateTime.Now;
                TakeSnapshot(false);   // once a day, in the background
                RefreshDrives();
                UpdateStatus();

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

        async void CodeFolders()
        {
            if (busy) return;
            var folders = Settings.LoadFolders();
            if (folders.Count > 0)
            {
                int r = await ShowDialog("Code folders",
                    "On every scan, Disk Sweep looks inside these folders for node_modules, build caches and other build output.",
                    folders.Select(f => new KeyValuePair<string, string>(f, Directory.Exists(f) ? "" : "missing")).ToList(),
                    null, "Add folder…", "Close", false, false, "Forget all");
                if (r == 2) { Settings.SaveFolders(new List<string>()); Scan(); return; }
                if (r != 1) return;
            }
            string root;
            try { root = FolderPicker.Pick(Window, "Pick the folder with your code projects"); }
            catch (Exception e) { statusText.Text = "Folder picker failed: " + e.Message; return; }
            if (string.IsNullOrEmpty(root)) return;
            if (!folders.Contains(root, StringComparer.OrdinalIgnoreCase)) folders.Add(root);
            Settings.SaveFolders(folders);
            filterKey = Scanner.CatProj;
            Scan();
        }

        async void Clean()
        {
            var sel = Selected();
            if (sel.Count == 0 || busy) return;
            var del = sel.Where(i => !i.ToRecycleBin).ToList();
            var rec = sel.Where(i => i.ToRecycleBin).ToList();
            long total = sel.Where(i => i.Size > 0).Sum(i => i.Size);
            string totalText = Util.Fmt(total) + (sel.Any(i => i.Size < 0) ? "+" : "");

            var rows = sel.Select(i => new KeyValuePair<string, string>(i.Name,
                (i.Size < 0 ? "—" : Util.Fmt(i.Size)) + (i.ToRecycleBin ? "  ♻" : ""))).ToList();
            var running = sel.Where(i => !string.IsNullOrEmpty(i.RunningText)).Select(i => i.RunningText).Distinct().ToList();
            string warn = running.Count > 0 ? "Close " + string.Join(", ", running) + " first, or files in use will be skipped." : null;

            string text;
            if (rec.Count == 0) text = "This permanently removes about " + totalText + ". It can't be undone.";
            else if (del.Count == 0) text = "These files go to the Recycle Bin (♻), so you can restore them. Their space is freed when you empty the Recycle Bin.";
            else text = "Items marked ♻ go to the Recycle Bin, so you can restore them. Everything else is removed permanently.";

            int ok = await ShowDialog((del.Count == 0 ? "Move " : "Delete ") + sel.Count + " item" + (sel.Count == 1 ? "" : "s") + "?",
                text, rows, warn, (del.Count == 0 ? "Move " : "Delete ") + totalText, "Cancel", true, false, null);
            if (ok != 1) return;

            cleaning = true;
            SetBusy(true);
            var progressDialog = ShowDialog("Cleaning…", "Please keep Disk Sweep open.", null, null, null, null, false, true, null);
            dlgProgress.Maximum = sel.Count;
            dlgProgress.Value = 0;
            long freeBefore = Native.FreeBytes(selectedDrive);
            var results = new List<KeyValuePair<string, string>>();
            string error = null;
            long recycled = 0;
            try
            {
                for (int i = 0; i < sel.Count; i++)
                {
                    var it = sel[i];
                    dlgStatus.Text = (i + 1) + " of " + sel.Count + ":  " + it.Name + (it.Mode == Mode.Command ? "  (can take a few minutes)" : "");
                    long freed = await Task.Run(() => Cleaner.Clean(it));
                    it.CleanedBytes = freed;
                    Activity.Add(it.ToRecycleBin ? "recycled" : it.Mode == Mode.Command || it.Mode == Mode.RecycleBin ? "system" : "deleted",
                                 it.Name, freed, it.ToRecycleBin ? (it.RecycledPaths ?? it.Paths) : it.Paths);
                    bool partial = it.Size > 0 && freed < it.Size * 0.9;
                    if (it.ToRecycleBin) recycled += freed;
                    string what = it.ToRecycleBin
                        ? (freed > 0 ? Util.Fmt(freed) + " to Recycle Bin" : "could not move (in use?)")
                        : Util.Fmt(freed) + (partial ? "  (some in use)" : "");
                    results.Add(new KeyValuePair<string, string>(it.Name, what));
                    it.Checked = false;
                    dlgProgress.Value = i + 1;
                }
            }
            catch (Exception e) { error = "Stopped early: " + e.Message; }
            cleaning = false;

            long gained = Math.Max(0, Native.FreeBytes(selectedDrive) - freeBefore);
            long cleanedTotal = 0;
            foreach (var it in sel) cleanedTotal += it.CleanedBytes;
            if (cleanedTotal > 0) { Settings.History.Add(new HistoryEntry { When = DateTime.Now, Bytes = cleanedTotal, Items = sel.Count }); Settings.Save(); }
            bool anyPartial = results.Any(r => r.Value.Contains("in use"));
            string note = error ?? (anyPartial ? "Some files were in use and were skipped. Close those apps and clean again to get the rest." : null);
            if (note == null && recycled > 0) note = Util.Fmt(recycled) + " is in the Recycle Bin. Empty it to free that space too.";
            await ShowDialog(del.Count == 0 ? "Moved to Recycle Bin" : "Freed " + Util.Fmt(gained),
                "Drive " + selectedDrive.TrimEnd('\\') + " now has " + Util.Fmt(Native.FreeBytes(selectedDrive)) + " free.",
                results, note, "Done", null, false, false, null);
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
        // Returns 1 = OK, 2 = alternative button, 0 = cancel / Esc.
        Task<int> ShowDialog(string title, string text, List<KeyValuePair<string, string>> rows, string warn,
                             string okText, string cancelText, bool danger, bool progress, string altText)
        {
            dlgAlt.Content = altText;
            dlgAlt.Visibility = altText == null ? Visibility.Collapsed : Visibility.Visible;
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
            dlgTcs = new TaskCompletionSource<int>();
            return dlgTcs.Task;
        }

        void CloseDialog(int result)
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
            if (args.Length >= 1 && args[0] == "--check-space") return Settings.CheckSpace();
            if (args.Length >= 1 && args[0] == "--auto-clean") return AutoClean.Run();         // run by Task Scheduler   // run by Task Scheduler
                        if (args.Length >= 3 && args[0] == "--test-move")      // <folder> <targetDriveRoot>
            {
                string err = Mover.Check(args[1], args[2]) ?? Mover.Move(args[1], args[2], null);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "disksweep-test-move.txt"), err ?? "OK " + Mover.TargetFor(args[1], args[2]));
                return err == null ? 0 : 1;
            }
            if (args.Length >= 3 && args[0] == "--test-map")       // <folder> <outFile>
            {
                var tree = SpaceMap.Build(args[1], null, System.Threading.CancellationToken.None);
                var sb = new System.Text.StringBuilder("total " + tree.Size + "\n");
                foreach (var c in tree.Children.Take(15)) sb.AppendLine(c.Size + "\t" + c.Name);
                File.WriteAllText(args[2], sb.ToString());
                return 0;
            }
            if (args.Length >= 3 && args[0] == "--test-compress")  // <folder> <outFile>
            {
                File.WriteAllText(args[2], (Compressor.CheckFolder(args[1]) ?? "saved " + Compressor.CompressFolder(args[1])));
                return 0;
            }            if (args.Length >= 2 && args[0] == "--report") return Dev.Report(args);
            if (args.Length >= 2 && args[0] == "--health-report") { File.WriteAllText(args[1], Health.Report()); return 0; }
            if (args.Length >= 2 && args[0] == "--selftest") return Dev.SelfTest(args[1]);
            //   DiskSweep.exe --test-item <folder>                adds that folder as a deletable item (UI testing)
            if (args.Length >= 2 && (args[0] == "--test-item" || args[0] == "--test-recycle")) { MainUi.TestItemPath = Path.GetFullPath(args[1]); MainUi.TestRecycle = args[0] == "--test-recycle"; }

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
