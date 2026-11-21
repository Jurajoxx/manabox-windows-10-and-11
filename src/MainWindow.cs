using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("ManaBox Companion")]
[assembly: AssemblyProduct("ManaBox Companion")]
[assembly: AssemblyDescription("Unofficial Windows companion for ManaBox collection exports")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace ManaBoxCompanion
{
    static class Program
    {
        public static bool Automated;

        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 12;
            Automated = args.Any(a => a == "--screenshot" || a == "--export" || a == "--selftest");

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Ui.LoadXaml("Theme.xaml"));
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                e.Handled = true;
                if (Automated)
                {
                    try { File.WriteAllText(AppData.PathOf("error.log"), e.Exception.ToString()); } catch { }
                    Environment.Exit(1);
                }
                MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "ManaBox Companion",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            try
            {
                app.Run(new MainWindow(args));
            }
            catch (Exception ex)
            {
                if (Automated)
                {
                    try { File.WriteAllText(AppData.PathOf("error.log"), ex.ToString()); } catch { }
                    Environment.Exit(1);
                }
                MessageBox.Show("ManaBox Companion could not start:\n\n" + ex.Message, "ManaBox Companion",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    partial class MainWindow : Window
    {
        static readonly string[] SortKeys = { "Name", "Price", "Total value", "Quantity", "Set", "Number", "Mana value",
                                              "Rarity", "Type", "Colour", "Finish", "Condition", "Binder" };
        static readonly string[] Rarities = { "All rarities", "Common", "Uncommon", "Rare", "Mythic", "Special" };
        static readonly string[] CardTypes = { "All types", "Creature", "Instant", "Sorcery", "Artifact", "Enchantment",
                                               "Planeswalker", "Land", "Battle" };
        static readonly string[] Finishes = { "All finishes", "Non-foil", "Foil", "Etched" };
        const double TileWidth = 222;

        readonly FrameworkElement root;
        readonly Dictionary<string, string> opts = new Dictionary<string, string>();
        readonly DispatcherTimer searchTimer = new DispatcherTimer();
        Settings settings;
        CollectionDoc doc;
        List<Entry> view = new List<Entry>();
        List<string> setCodes = new List<string>();
        Entry current, anchor;
        string sortKey = "Name", statusNote;
        bool sortDesc, dirty, syncing, loadingUi, pricesComplete;
        int galleryCols;
        CancellationTokenSource fetchCts;

        Button OpenBtn, ExportBtn, RefreshBtn, SortDirBtn, ResetBtn, EditBtn, DeleteBtn, AddDeckBtn, PrintBtn, ScryfallBtn;
        ComboBox CurrencyBox, RarityBox, TypeBox, SetBox, FoilBox, BinderBox, SortBox, DeckPick;
        ToggleButton[] colourPips;
        RadioButton ViewGallery, ViewList;
        TextBox SearchBox;
        TextBlock FileLabel, Status, NoResults, DetailEmpty, SelLabel;
        ProgressBar Progress;
        TabControl Tabs;
        ItemsControl Gallery;
        DataGrid ListGrid;
        Border Welcome;
        ScrollViewer DetailScroll;
        StackPanel DetailPanel;
        Image DetailImage;

        T F<T>(string name) where T : class
        {
            T v = root.FindName(name) as T;
            if (v == null) throw new InvalidOperationException("Missing UI element: " + name);
            return v;
        }

        public MainWindow(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    string key = args[i].Substring(2);
                    string val = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "1";
                    opts[key] = val;
                }
                else if (!opts.ContainsKey("open")) opts["open"] = args[i];
            }

            settings = AppData.Load<Settings>("settings.json");
            Fmt.Currency = settings.Currency == "EUR" ? "EUR" : "USD";
            if (opts.ContainsKey("currency")) Fmt.Currency = opts["currency"];

            Title = "ManaBox Companion";
            MinWidth = 1040;
            MinHeight = 660;
            Width = settings.Width >= MinWidth ? Math.Min(settings.Width, SystemParameters.WorkArea.Width) : Math.Min(1500, SystemParameters.WorkArea.Width);
            Height = settings.Height >= MinHeight ? Math.Min(settings.Height, SystemParameters.WorkArea.Height) : Math.Min(920, SystemParameters.WorkArea.Height);
            if (opts.ContainsKey("size"))
            {
                string[] wh = opts["size"].Split('x');
                Width = double.Parse(wh[0]);
                Height = double.Parse(wh[1]);
            }
            else if (settings.Maximized) WindowState = WindowState.Maximized;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (Program.Automated) { ShowActivated = false; ShowInTaskbar = false; }
            if (opts.ContainsKey("pos"))
            {
                string[] xy = opts["pos"].Split(',');
                Ui.PlaceAt(this, int.Parse(xy[0]), int.Parse(xy[1]));
            }
            Ui.StyleWindow(this);

            root = (FrameworkElement)Ui.LoadXaml("Main.xaml");
            Content = root;
            FindControls();
            HookCollection();
            InitDecks();
            InitValue();
            UpdateSortButton();
            UpdateSelectionUi();
            UpdateStatus(null);

            AllowDrop = true;
            Drop += OnDrop;
            PreviewKeyDown += OnKey;
            Closing += OnClosing;
            Loaded += async delegate { await Startup(); };
        }

        void FindControls()
        {
            OpenBtn = F<Button>("OpenBtn"); ExportBtn = F<Button>("ExportBtn"); RefreshBtn = F<Button>("RefreshBtn");
            SortDirBtn = F<Button>("SortDirBtn"); ResetBtn = F<Button>("ResetBtn"); EditBtn = F<Button>("EditBtn");
            DeleteBtn = F<Button>("DeleteBtn"); AddDeckBtn = F<Button>("AddDeckBtn"); PrintBtn = F<Button>("PrintBtn");
            ScryfallBtn = F<Button>("ScryfallBtn");
            CurrencyBox = F<ComboBox>("CurrencyBox"); RarityBox = F<ComboBox>("RarityBox"); TypeBox = F<ComboBox>("TypeBox");
            SetBox = F<ComboBox>("SetBox"); FoilBox = F<ComboBox>("FoilBox"); BinderBox = F<ComboBox>("BinderBox");
            SortBox = F<ComboBox>("SortBox"); DeckPick = F<ComboBox>("DeckPick");
            colourPips = new[] { F<ToggleButton>("ColW"), F<ToggleButton>("ColU"), F<ToggleButton>("ColB"),
                                 F<ToggleButton>("ColR"), F<ToggleButton>("ColG"), F<ToggleButton>("ColC") };
            ViewGallery = F<RadioButton>("ViewGallery"); ViewList = F<RadioButton>("ViewList");
            SearchBox = F<TextBox>("SearchBox");
            FileLabel = F<TextBlock>("FileLabel"); Status = F<TextBlock>("Status"); NoResults = F<TextBlock>("NoResults");
            DetailEmpty = F<TextBlock>("DetailEmpty"); SelLabel = F<TextBlock>("SelLabel");
            Progress = F<ProgressBar>("Progress"); Tabs = F<TabControl>("Tabs");
            Gallery = F<ItemsControl>("Gallery"); ListGrid = F<DataGrid>("ListGrid");
            Welcome = F<Border>("Welcome"); DetailScroll = F<ScrollViewer>("DetailScroll");
            DetailPanel = F<StackPanel>("DetailPanel"); DetailImage = F<Image>("DetailImage");
        }

        void HookCollection()
        {
            loadingUi = true;
            CurrencyBox.ItemsSource = new[] { "USD", "EUR" };
            CurrencyBox.SelectedItem = Fmt.Currency;
            RarityBox.ItemsSource = Rarities; RarityBox.SelectedIndex = 0;
            TypeBox.ItemsSource = CardTypes; TypeBox.SelectedIndex = 0;
            FoilBox.ItemsSource = Finishes; FoilBox.SelectedIndex = 0;
            SetBox.ItemsSource = new[] { "All sets" }; SetBox.SelectedIndex = 0;
            BinderBox.ItemsSource = new[] { "All binders" }; BinderBox.SelectedIndex = 0;
            BinderBox.Visibility = Visibility.Collapsed;
            SortBox.ItemsSource = SortKeys; SortBox.SelectedItem = sortKey;
            if (settings.View == "list") ViewList.IsChecked = true;
            loadingUi = false;
            ApplyViewMode();

            OpenBtn.Click += delegate { OpenDialog(); };
            F<Button>("WelcomeOpenBtn").Click += delegate { OpenDialog(); };
            F<Button>("WelcomeSampleBtn").Click += async delegate { await OpenSample(); };
            ExportBtn.Click += delegate { ExportCsv(); };
            RefreshBtn.Click += async delegate { await FetchPrices(true); };

            searchTimer.Interval = TimeSpan.FromMilliseconds(180);
            searchTimer.Tick += delegate { searchTimer.Stop(); ApplyFilter(); };
            SearchBox.TextChanged += delegate { if (!loadingUi) { searchTimer.Stop(); searchTimer.Start(); } };
            foreach (ToggleButton pip in colourPips) pip.Click += delegate { ApplyFilter(); };
            foreach (ComboBox box in new[] { RarityBox, TypeBox, SetBox, FoilBox, BinderBox })
                box.SelectionChanged += delegate { if (!loadingUi) ApplyFilter(); };
            SortBox.SelectionChanged += delegate
            {
                if (loadingUi || SortBox.SelectedItem == null) return;
                sortKey = (string)SortBox.SelectedItem;
                sortDesc = DefaultDescending(sortKey);
                UpdateSortButton();
                ApplyFilter();
            };
            SortDirBtn.Click += delegate { sortDesc = !sortDesc; UpdateSortButton(); ApplyFilter(); };
            ResetBtn.Click += delegate { ResetFilters(); ApplyFilter(); };
            ViewGallery.Checked += delegate { if (!loadingUi) ApplyViewMode(); };
            ViewList.Checked += delegate { if (!loadingUi) ApplyViewMode(); };
            CurrencyBox.SelectionChanged += delegate
            {
                if (loadingUi || CurrencyBox.SelectedItem == null) return;
                Fmt.Currency = (string)CurrencyBox.SelectedItem;
                settings.Currency = Fmt.Currency;
                if (doc != null) { foreach (Entry en in doc.Entries) en.Refresh(); ApplyFilter(); }
                RefreshDeck();
                RefreshValue();
            };

            Gallery.MouseLeftButtonDown += OnGalleryClick;
            Gallery.SizeChanged += delegate { RebuildRows(); };
            ListGrid.SelectionChanged += OnGridSelection;
            ListGrid.Sorting += OnGridSorting;
            ListGrid.MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
            {
                var fe = e.OriginalSource as FrameworkElement;
                if (fe != null && fe.DataContext is Entry) EditSelected();
            };

            EditBtn.Click += delegate { EditSelected(); };
            DeleteBtn.Click += delegate { DeleteSelected(); };
            AddDeckBtn.Click += delegate { AddSelectionToDeck(); };
            PrintBtn.Click += delegate { PrintCollection(); };
            ScryfallBtn.Click += delegate
            {
                if (current != null && current.Card != null && !string.IsNullOrEmpty(current.Card.Uri)) OpenUrl(current.Card.Uri);
            };
            Tabs.SelectionChanged += delegate(object s, SelectionChangedEventArgs e)
            {
                if (e.OriginalSource != Tabs) return;
                if (Tabs.SelectedIndex == 1) RefreshDeck();
                if (Tabs.SelectedIndex == 2) RefreshValue();
            };
        }

        static void OpenUrl(string url)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ================= startup, loading, saving =================

        async Task Startup()
        {
            string open;
            if (!opts.TryGetValue("open", out open)) open = Program.Automated ? null : settings.LastFile;
            if (!string.IsNullOrEmpty(open) && File.Exists(open)) await LoadFile(open);
            else await FetchPrices(false);
            if (Program.Automated) await RunAutomation();
        }

        bool ConfirmDiscard()
        {
            if (!dirty || doc == null) return true;
            MessageBoxResult r = MessageBox.Show(this,
                "You have changes that are not exported yet.\n\nExport them to a CSV file first?",
                "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) return ExportCsv();
            return true;
        }

        async void OpenDialog()
        {
            if (!ConfirmDiscard()) return;
            var dlg = new OpenFileDialog();
            dlg.Title = "Open a ManaBox CSV export";
            dlg.Filter = "ManaBox export (*.csv)|*.csv|All files (*.*)|*.*";
            if (dlg.ShowDialog(this) == true) await LoadFile(dlg.FileName);
        }

        async Task OpenSample()
        {
            string path = AppData.PathOf("ManaBox sample collection.csv");
            using (Stream s = Ui.Resource("sample.csv"))
            using (FileStream f = File.Create(path)) s.CopyTo(f);
            await LoadFile(path);
        }

        async void OnDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            if (!ConfirmDiscard()) return;
            Tabs.SelectedIndex = 0;
            await LoadFile(files[0]);
        }

        async Task LoadFile(string path)
        {
            CollectionDoc loaded;
            try { loaded = CollectionDoc.Load(path); }
            catch (Exception ex)
            {
                if (Program.Automated) throw;
                MessageBox.Show(this, "Could not open this file.\n\n" + ex.Message, "ManaBox Companion",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            doc = loaded;
            dirty = false;
            pricesComplete = false;
            anchor = null;
            statusNote = null;
            settings.LastFile = path;
            SaveSettings();
            Welcome.Visibility = Visibility.Collapsed;
            AssignCards(false);
            ResetFilters();
            RebuildFilterOptions();
            ShowDetail(null);
            ApplyFilter();
            RefreshDeck();
            RefreshValue();
            await FetchPrices(false);
        }

        void AssignCards(bool refresh)
        {
            if (doc == null) return;
            foreach (Entry e in doc.Entries)
            {
                CardInfo c = Scryfall.I.Find(e.ScryfallId, e.RawSet, e.RawNumber, e.RawName);
                if (!ReferenceEquals(c, e.Card))
                {
                    e.Card = c;
                    if (refresh) e.Refresh();
                }
            }
        }

        async Task FetchPrices(bool force)
        {
            if (fetchCts != null) fetchCts.Cancel();
            var cts = fetchCts = new CancellationTokenSource();
            var lookups = new List<Lookup>();
            if (doc != null)
                foreach (Entry e in doc.Entries)
                    lookups.Add(new Lookup { Id = e.ScryfallId, Set = e.RawSet, Number = e.RawNumber, Name = e.RawName });
            foreach (DeckData d in deckFile.Decks)
                foreach (DeckCardData c in d.Cards)
                    lookups.Add(new Lookup { Id = c.Id, Set = c.Set, Number = c.Number, Name = c.Name });
            if (lookups.Count == 0) return;

            Progress.Value = 0;
            Progress.IsIndeterminate = true;
            Progress.Visibility = Visibility.Visible;
            RefreshBtn.IsEnabled = false;
            statusNote = "Checking Scryfall for card data and prices…";
            UpdateStatus(statusNote);

            FetchResult r = await Scryfall.I.EnsureAsync(lookups, force, delegate(int done, int total)
            {
                if (cts != fetchCts) return;
                Progress.IsIndeterminate = false;
                Progress.Value = (double)done / total;
                UpdateStatus(string.Format("Downloading card data from Scryfall… {0} of {1}", Fmt.Int(done), Fmt.Int(total)));
                AssignCards(true);
            }, cts.Token);
            if (cts != fetchCts) return;   // a newer fetch took over

            Progress.Visibility = Visibility.Hidden;
            Progress.IsIndeterminate = false;
            RefreshBtn.IsEnabled = true;
            AssignCards(true);
            pricesComplete = r.Error == null;
            if (r.Error != null) statusNote = "Could not reach Scryfall (" + r.Error + "). Showing saved data.";
            else if (r.NotFound > 0) statusNote = Fmt.Int(r.NotFound) + " cards were not found on Scryfall.";
            else if (r.Requested > 0) statusNote = "Prices updated.";
            else statusNote = null;

            if (doc != null)
            {
                ApplyFilter();
                if (pricesComplete) RecordSnapshot();
                if (current != null) ShowDetail(current);
            }
            RefreshDeck();
            RefreshValue();
            UpdateStatus(statusNote);
        }

        bool ExportCsv()
        {
            if (doc == null) return false;
            var dlg = new SaveFileDialog();
            dlg.Title = "Export a CSV to import in ManaBox";
            dlg.Filter = "CSV file (*.csv)|*.csv";
            string name = doc.Key;
            if (!name.EndsWith("-edited", StringComparison.OrdinalIgnoreCase)) name += "-edited";
            dlg.FileName = name + ".csv";
            try { dlg.InitialDirectory = Path.GetDirectoryName(doc.Path); } catch { }
            if (dlg.ShowDialog(this) != true) return false;
            try
            {
                doc.Save(dlg.FileName);
                dirty = false;
                UpdateStatus("Exported " + Path.GetFileName(dlg.FileName) + ". Import this file in ManaBox to apply your changes.");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the file.\n\n" + ex.Message, "ManaBox Companion",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        void SaveSettings()
        {
            settings.Currency = Fmt.Currency;
            settings.View = ViewList.IsChecked == true ? "list" : "gallery";
            if (!Program.Automated) AppData.Save("settings.json", settings);
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!Program.Automated && !ConfirmDiscard()) { e.Cancel = true; return; }
            settings.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { settings.Width = ActualWidth; settings.Height = ActualHeight; }
            SaveSettings();
        }

        // ================= filtering and sorting =================

        class Term { public char Field; public string Text; }

        static bool DefaultDescending(string key)
        {
            return key == "Price" || key == "Total value" || key == "Quantity" || key == "Rarity";
        }

        void UpdateSortButton()
        {
            SortDirBtn.Content = sortDesc ? "↓ High first" : "↑ Low first";
            if (sortKey == "Name" || sortKey == "Set" || sortKey == "Type" || sortKey == "Binder" || sortKey == "Finish" || sortKey == "Condition")
                SortDirBtn.Content = sortDesc ? "↓ Z to A" : "↑ A to Z";
        }

        void ResetFilters()
        {
            loadingUi = true;
            SearchBox.Text = "";
            foreach (ToggleButton pip in colourPips) pip.IsChecked = false;
            RarityBox.SelectedIndex = 0;
            TypeBox.SelectedIndex = 0;
            FoilBox.SelectedIndex = 0;
            if (SetBox.Items.Count > 0) SetBox.SelectedIndex = 0;
            if (BinderBox.Items.Count > 0) BinderBox.SelectedIndex = 0;
            loadingUi = false;
        }

        void RebuildFilterOptions()
        {
            if (doc == null) return;
            loadingUi = true;
            string oldSet = SetBox.SelectedIndex > 0 && SetBox.SelectedIndex - 1 < setCodes.Count ? setCodes[SetBox.SelectedIndex - 1] : null;
            string oldBinder = BinderBox.SelectedIndex > 0 ? BinderBox.SelectedItem as string : null;

            var sets = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry e in doc.Entries)
            {
                string code = e.SetCode;
                if (code.Length == 0) continue;
                string label = e.SetName.Length > 0 ? e.SetName + " (" + code + ")" : code;
                if (!sets.ContainsKey(label)) sets[label] = code;
            }
            var labels = new List<string>();
            labels.Add("All sets");
            setCodes = new List<string>();
            foreach (KeyValuePair<string, string> kv in sets) { labels.Add(kv.Key); setCodes.Add(kv.Value); }
            SetBox.ItemsSource = labels;
            int si = oldSet == null ? -1 : setCodes.FindIndex(c => string.Equals(c, oldSet, StringComparison.OrdinalIgnoreCase));
            SetBox.SelectedIndex = si + 1;

            var binders = new List<string>();
            binders.Add("All binders");
            binders.AddRange(doc.Entries.Select(e => e.Binder).Where(b => b.Length > 0).Distinct().OrderBy(b => b));
            BinderBox.ItemsSource = binders;
            BinderBox.SelectedIndex = oldBinder != null && binders.Contains(oldBinder) ? binders.IndexOf(oldBinder) : 0;
            BinderBox.Visibility = binders.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            foreach (DataGridColumn col in ListGrid.Columns)
                if ((col.Header as string) == "Binder") col.Visibility = doc.cBinder >= 0 ? Visibility.Visible : Visibility.Collapsed;
            loadingUi = false;
        }

        static List<Term> ParseSearch(string text)
        {
            var terms = new List<Term>();
            int i = 0;
            text = text.Trim().ToLowerInvariant();
            while (i < text.Length)
            {
                while (i < text.Length && text[i] == ' ') i++;
                if (i >= text.Length) break;
                int start = i;
                bool quoted = false;
                var sb = new System.Text.StringBuilder();
                while (i < text.Length && (quoted || text[i] != ' '))
                {
                    if (text[i] == '"') quoted = !quoted; else sb.Append(text[i]);
                    i++;
                }
                string word = sb.ToString();
                var t = new Term();
                t.Field = ' ';
                t.Text = word;
                int colon = word.IndexOf(':');
                if (colon > 0 && colon < word.Length - 1)
                {
                    string prefix = word.Substring(0, colon);
                    char f = prefix == "t" || prefix == "type" ? 't'
                           : prefix == "o" || prefix == "text" ? 'o'
                           : prefix == "s" || prefix == "set" || prefix == "e" ? 's'
                           : prefix == "r" || prefix == "rarity" ? 'r'
                           : ' ';
                    if (f != ' ') { t.Field = f; t.Text = word.Substring(colon + 1); }
                }
                if (t.Text.Length > 0) terms.Add(t);
                if (i == start) i++;
            }
            return terms;
        }

        static int NumberKey(string number)
        {
            int n = 0, digits = 0;
            foreach (char c in number)
            {
                if (c < '0' || c > '9') break;
                if (digits < 8) n = n * 10 + (c - '0');
                digits++;
            }
            return digits == 0 ? int.MaxValue : n;
        }

        static int RarityRank(string r)
        {
            switch (r) { case "common": return 0; case "uncommon": return 1; case "rare": return 2; case "mythic": return 3; default: return 4; }
        }

        static IComparable SortValue(Entry e, string key)
        {
            switch (key)
            {
                case "Price": return e.Price;
                case "Total value": return e.Total;
                case "Quantity": return e.Quantity;
                case "Set": return e.SetCode;
                case "Number": return NumberKey(e.Number);
                case "Mana value": return e.Cmc;
                case "Rarity": return RarityRank(e.Rarity);
                case "Type": return e.TypeLine;
                case "Colour": return e.Identity.Length == 0 ? "~" : (e.Identity.Length > 1 ? "Z" + e.Identity : e.Identity);
                case "Finish": return e.Finish;
                case "Condition": return e.Condition;
                case "Binder": return e.Binder;
                default: return e.Name;
            }
        }

        void ApplyFilter()
        {
            if (doc == null) return;
            List<Term> terms = ParseSearch(SearchBox.Text);
            string colours = "";
            foreach (ToggleButton pip in colourPips) if (pip.IsChecked == true) colours += (string)pip.Content;
            string rarity = RarityBox.SelectedIndex > 0 ? ((string)RarityBox.SelectedItem).ToLowerInvariant() : null;
            string type = TypeBox.SelectedIndex > 0 ? (string)TypeBox.SelectedItem : null;
            string set = SetBox.SelectedIndex > 0 && SetBox.SelectedIndex - 1 < setCodes.Count ? setCodes[SetBox.SelectedIndex - 1] : null;
            string finish = FoilBox.SelectedIndex == 1 ? "normal" : FoilBox.SelectedIndex == 2 ? "foil" : FoilBox.SelectedIndex == 3 ? "etched" : null;
            string binder = BinderBox.SelectedIndex > 0 ? (string)BinderBox.SelectedItem : null;

            Func<Entry, bool> match = delegate(Entry e)
            {
                foreach (Term t in terms)
                {
                    bool ok;
                    switch (t.Field)
                    {
                        case 't': ok = e.TypeLine.ToLowerInvariant().Contains(t.Text); break;
                        case 'o': ok = e.OracleText.ToLowerInvariant().Contains(t.Text); break;
                        case 's': ok = e.SetCode.ToLowerInvariant() == t.Text || e.SetName.ToLowerInvariant().Contains(t.Text); break;
                        case 'r': ok = e.Rarity.StartsWith(t.Text, StringComparison.Ordinal); break;
                        default: ok = e.Blob.Contains(t.Text); break;
                    }
                    if (!ok) return false;
                }
                if (colours.Length > 0)
                {
                    string id = e.Identity;
                    if (e.Card == null) return false;
                    foreach (char c in colours)
                    {
                        if (c == 'C') { if (id.Length > 0) return false; }
                        else if (id.IndexOf(c) < 0) return false;
                    }
                }
                if (rarity != null)
                {
                    string r = e.Rarity;
                    if (rarity == "special") { if (r == "common" || r == "uncommon" || r == "rare" || r == "mythic" || r.Length == 0) return false; }
                    else if (r != rarity) return false;
                }
                if (type != null && e.TypeLine.IndexOf(type, StringComparison.OrdinalIgnoreCase) < 0) return false;
                if (set != null && !string.Equals(e.SetCode, set, StringComparison.OrdinalIgnoreCase)) return false;
                if (finish != null && e.Finish != finish) return false;
                if (binder != null && e.Binder != binder) return false;
                return true;
            };

            string key = sortKey;
            IEnumerable<Entry> filtered = doc.Entries.Where(match);
            IOrderedEnumerable<Entry> ordered = sortDesc
                ? filtered.OrderByDescending(e => SortValue(e, key))
                : filtered.OrderBy(e => SortValue(e, key));
            view = ordered.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.SetCode).ThenBy(e => NumberKey(e.Number)).ToList();

            var inView = new HashSet<Entry>(view);
            foreach (Entry e in doc.Entries) if (e.IsSelected && !inView.Contains(e)) e.IsSelected = false;
            if (current != null && !inView.Contains(current)) ShowDetail(null);
            if (anchor != null && !inView.Contains(anchor)) anchor = null;

            syncing = true;
            ListGrid.ItemsSource = view;
            if (ListGrid.Visibility == Visibility.Visible) SyncGridSelection();
            syncing = false;
            foreach (DataGridColumn col in ListGrid.Columns)
                col.SortDirection = col.SortMemberPath == sortKey
                    ? (sortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending)
                    : (ListSortDirection?)null;

            galleryCols = 0;
            RebuildRows();
            NoResults.Visibility = view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelectionUi();
            UpdateStatus(statusNote);
        }

        void RebuildRows()
        {
            if (Gallery.Visibility != Visibility.Visible || Gallery.ActualWidth < 60) return;
            int cols = Math.Max(1, (int)((Gallery.ActualWidth - 18) / TileWidth));
            if (cols == galleryCols) return;
            galleryCols = cols;
            var rows = new List<CardRow>();
            for (int i = 0; i < view.Count; i += cols)
                rows.Add(new CardRow { Items = view.GetRange(i, Math.Min(cols, view.Count - i)) });
            Gallery.ItemsSource = rows;
        }

        void ApplyViewMode()
        {
            bool list = ViewList.IsChecked == true;
            Gallery.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
            ListGrid.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
            if (list)
            {
                syncing = true;
                SyncGridSelection();
                syncing = false;
            }
            else
            {
                galleryCols = 0;
                RebuildRows();
            }
        }

        // ================= selection =================

        List<Entry> Selected() { return view.Where(e => e.IsSelected).ToList(); }

        void ClearSelection()
        {
            if (doc == null) return;
            foreach (Entry e in doc.Entries) e.IsSelected = false;
        }

        void SyncGridSelection()
        {
            ListGrid.SelectedItems.Clear();
            foreach (Entry e in view) if (e.IsSelected) ListGrid.SelectedItems.Add(e);
        }

        void OnGalleryClick(object sender, MouseButtonEventArgs e)
        {
            var fe = e.OriginalSource as FrameworkElement;
            Entry en = fe != null ? fe.DataContext as Entry : null;
            if (en == null) return;
            ModifierKeys mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
            if (e.ClickCount >= 2)
            {
                if (!en.IsSelected) { ClearSelection(); en.IsSelected = true; }
            }
            else if (shift && anchor != null && view.Contains(anchor))
            {
                int a = view.IndexOf(anchor), b = view.IndexOf(en);
                if (!ctrl) ClearSelection();
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) view[i].IsSelected = true;
            }
            else if (ctrl)
            {
                en.IsSelected = !en.IsSelected;
                anchor = en;
            }
            else
            {
                ClearSelection();
                en.IsSelected = true;
                anchor = en;
            }
            ShowDetail(en);
            UpdateSelectionUi();
            if (e.ClickCount == 2) EditSelected();
        }

        void OnGridSelection(object sender, SelectionChangedEventArgs e)
        {
            if (syncing || e.OriginalSource != ListGrid) return;
            Entry last = null;
            foreach (object o in e.RemovedItems) { var en = o as Entry; if (en != null) en.IsSelected = false; }
            foreach (object o in e.AddedItems) { var en = o as Entry; if (en != null) { en.IsSelected = true; last = en; } }
            if (last == null) last = ListGrid.SelectedItem as Entry;
            if (last != null) { anchor = last; ShowDetail(last); }
            UpdateSelectionUi();
        }

        void OnGridSorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            string key = e.Column.SortMemberPath;
            if (string.IsNullOrEmpty(key)) return;
            if (sortKey == key) sortDesc = !sortDesc;
            else { sortKey = key; sortDesc = DefaultDescending(key); }
            loadingUi = true;
            SortBox.SelectedItem = sortKey;
            loadingUi = false;
            UpdateSortButton();
            ApplyFilter();
        }

        async void ShowDetail(Entry e)
        {
            current = e;
            if (e == null)
            {
                DetailScroll.Visibility = Visibility.Collapsed;
                DetailEmpty.Visibility = Visibility.Visible;
                DetailPanel.DataContext = null;
                DetailImage.Source = null;
                return;
            }
            DetailEmpty.Visibility = Visibility.Collapsed;
            DetailScroll.Visibility = Visibility.Visible;
            DetailPanel.DataContext = e;
            ScryfallBtn.IsEnabled = e.Card != null && !string.IsNullOrEmpty(e.Card.Uri);
            DetailImage.Source = e.Card != null ? Images.TryGet(e.Card.Id) : null;
            if (e.Card == null) return;
            ImageSource full = await Images.LoadAsync(e.Card, 0);
            if (current == e && full != null) DetailImage.Source = full;
        }

        void UpdateSelectionUi()
        {
            List<Entry> sel = Selected();
            if (sel.Count == 0) SelLabel.Text = "No cards selected";
            else
                SelLabel.Text = string.Format("{0} selected · {1} cards · {2}", Fmt.Int(sel.Count),
                    Fmt.Int(sel.Sum(x => x.Quantity)), Fmt.Money(sel.Sum(x => x.Total)));
            EditBtn.IsEnabled = sel.Count > 0;
            DeleteBtn.IsEnabled = sel.Count > 0;
            AddDeckBtn.IsEnabled = sel.Count > 0;
            PrintBtn.IsEnabled = view.Count > 0;
            PrintBtn.Content = sel.Count > 0 ? "Print selected…" : "Print list…";
            ExportBtn.IsEnabled = doc != null;
        }

        void UpdateStatus(string note)
        {
            if (doc == null)
            {
                FileLabel.Text = "";
                Status.Text = note ?? "Open a ManaBox CSV export to get started.";
                return;
            }
            FileLabel.Text = Path.GetFileName(doc.Path) + (dirty ? "  •  edited, not exported" : "");
            string s = string.Format("{0} cards in {1} entries · worth {2}", Fmt.Int(doc.Entries.Sum(e => e.Quantity)),
                Fmt.Int(doc.Entries.Count), Fmt.Money(doc.Entries.Sum(e => e.Total)));
            if (view.Count != doc.Entries.Count)
                s += string.Format("    |    showing {0} entries ({1} cards, {2})", Fmt.Int(view.Count),
                    Fmt.Int(view.Sum(e => e.Quantity)), Fmt.Money(view.Sum(e => e.Total)));
            if (!string.IsNullOrEmpty(note)) s += "    |    " + note;
            Status.Text = s;
        }

        // ================= editing =================

        void MarkEdited()
        {
            dirty = true;
            RebuildFilterOptions();
            UpdateSelectionUi();
            UpdateStatus(statusNote);
            if (current != null && !doc.Entries.Contains(current)) ShowDetail(null);
        }

        void EditSelected()
        {
            List<Entry> sel = Selected();
            if (sel.Count == 0 || doc == null) return;
            List<Entry> emptied;
            if (!EditDialog.Show(this, doc, sel, out emptied)) return;
            foreach (Entry e in sel) e.Refresh();
            if (emptied.Count > 0)
            {
                foreach (Entry e in emptied) { e.IsSelected = false; doc.Entries.Remove(e); }
                MarkEdited();
                ApplyFilter();
            }
            else MarkEdited();
        }

        void DeleteSelected()
        {
            List<Entry> sel = Selected();
            if (sel.Count == 0 || doc == null) return;
            string what = sel.Count == 1 ? "“" + sel[0].Name + "”" : Fmt.Int(sel.Count) + " entries (" + Fmt.Int(sel.Sum(e => e.Quantity)) + " cards)";
            if (MessageBox.Show(this, "Remove " + what + " from the collection?\n\nNothing changes in ManaBox until you export and import the CSV.",
                    "Delete cards", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var gone = new HashSet<Entry>(sel);
            doc.Entries.RemoveAll(e => gone.Contains(e));
            foreach (Entry e in sel) e.IsSelected = false;
            MarkEdited();
            ApplyFilter();
        }

        void PrintCollection()
        {
            if (doc == null) return;
            List<Entry> items = Selected();
            if (items.Count == 0) items = view;
            if (items.Count == 0) return;
            var rows = new List<string[]>();
            foreach (Entry e in items)
                rows.Add(new[] { "☐", e.Quantity.ToString(), e.Name, e.SetCode, e.Number, e.FinishLabel, e.ConditionLabel, e.PriceText, e.TotalText });
            string subtitle = string.Format("{0} cards in {1} entries · total {2} · printed {3:d MMM yyyy}",
                Fmt.Int(items.Sum(e => e.Quantity)), Fmt.Int(items.Count), Fmt.Money(items.Sum(e => e.Total)), DateTime.Now);
            Printer.Print(this, doc.Key, subtitle,
                new[] { "", "Qty", "Name", "Set", "#", "Finish", "Condition", "Price", "Total" },
                new double[] { 0.5, 0.8, 6, 1.1, 1.1, 1.3, 2, 1.6, 1.7 },
                new[] { false, true, false, false, false, false, false, true, true }, rows);
        }

        // ================= keyboard =================

        void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool typing = Keyboard.FocusedElement is TextBox;
            if (ctrl && e.Key == Key.O) { OpenDialog(); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { ExportCsv(); e.Handled = true; }
            else if (ctrl && e.Key == Key.F) { Tabs.SelectedIndex = 0; SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (Tabs.SelectedIndex == 0 && !typing && doc != null)
            {
                bool gallery = Gallery.Visibility == Visibility.Visible;
                if (ctrl && e.Key == Key.A && gallery)
                {
                    foreach (Entry en in view) en.IsSelected = true;
                    UpdateSelectionUi();
                    e.Handled = true;
                }
                else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
                else if (e.Key == Key.Escape)
                {
                    ClearSelection();
                    if (!gallery) { syncing = true; ListGrid.SelectedItems.Clear(); syncing = false; }
                    UpdateSelectionUi();
                    e.Handled = true;
                }
            }
        }

        // ================= scripted runs (used to test the build and make screenshots) =================

        async Task RunAutomation()
        {
            string v;
            if (opts.TryGetValue("search", out v)) { SearchBox.Text = v; searchTimer.Stop(); ApplyFilter(); }
            if (opts.TryGetValue("view", out v) && v == "list") ViewList.IsChecked = true;
            if (opts.TryGetValue("select", out v))
            {
                int n = int.Parse(v);
                for (int i = 0; i < n && i < view.Count; i++) view[i].IsSelected = true;
                if (ListGrid.Visibility == Visibility.Visible) { syncing = true; SyncGridSelection(); syncing = false; }
                UpdateSelectionUi();
            }
            if (opts.TryGetValue("detail", out v))
            {
                int n = int.Parse(v);
                if (n < view.Count) ShowDetail(view[n]);
            }
            if (opts.TryGetValue("selftest", out v)) SelfTest(v);
            if (opts.ContainsKey("deckdemo")) BuildDemoDeck();
            if (opts.TryGetValue("tab", out v)) Tabs.SelectedIndex = int.Parse(v);
            if (opts.TryGetValue("export", out v) && doc != null) doc.Save(v);
            if (opts.TryGetValue("report", out v) && doc != null)
            {
                File.WriteAllText(v, string.Format("entries={0}\ncards={1}\nmatched={2}\npriced={3}\nusd={4:0.00}\neur={5:0.00}\npricesComplete={6}\nnote={7}\n",
                    doc.Entries.Count, doc.Entries.Sum(e => e.Quantity), doc.Entries.Count(e => e.Card != null),
                    doc.Entries.Count(e => e.UnitPrice("USD") != null), doc.Entries.Sum(e => e.Value("USD")),
                    doc.Entries.Sum(e => e.Value("EUR")), pricesComplete, statusNote));
            }
            if (opts.TryGetValue("screenshot", out v))
            {
                int idle = 0;
                for (int i = 0; i < 100 && idle < 5; i++)
                {
                    await Task.Delay(300);
                    idle = Images.Busy ? 0 : idle + 1;
                }
                await Task.Delay(400);
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (FileStream f = File.Create(v)) enc.Save(f);
            }
            Close();
        }
    }
}
