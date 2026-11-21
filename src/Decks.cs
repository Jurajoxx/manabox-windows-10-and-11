using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ManaBoxCompanion
{
    partial class MainWindow
    {
        const string NewDeckItem = "＋ New deck…";
        const string ManaBoxHeader = "Name,Set code,Set name,Collector number,Foil,Rarity,Quantity,ManaBox ID,Scryfall ID,Purchase price,Misprint,Altered,Condition,Language,Purchase price currency";

        DeckFile deckFile = new DeckFile();
        DeckData currentDeck;
        List<DeckCard> deckView = new List<DeckCard>();
        bool deckSync;
        ListBox DeckList;
        DataGrid DeckGrid;
        TextBlock DeckTitle, DeckEmpty;
        TextBox DeckAddBox;
        StackPanel DeckStats;
        Button[] deckButtons;

        void InitDecks()
        {
            deckFile = AppData.Load<DeckFile>("decks.json");
            if (deckFile.Decks == null) deckFile.Decks = new List<DeckData>();
            foreach (DeckData d in deckFile.Decks) if (d.Cards == null) d.Cards = new List<DeckCardData>();

            DeckList = F<ListBox>("DeckList"); DeckGrid = F<DataGrid>("DeckGrid");
            DeckTitle = F<TextBlock>("DeckTitle"); DeckEmpty = F<TextBlock>("DeckEmpty");
            DeckAddBox = F<TextBox>("DeckAddBox"); DeckStats = F<StackPanel>("DeckStats");
            deckButtons = new[] { F<Button>("DeckAddBtn"), F<Button>("DeckMinusBtn"), F<Button>("DeckPlusBtn"), F<Button>("DeckMoveBtn"),
                                  F<Button>("DeckRemoveBtn"), F<Button>("DeckCopyBtn"), F<Button>("DeckExportTxtBtn"),
                                  F<Button>("DeckExportCsvBtn"), F<Button>("DeckPrintBtn"), F<Button>("RenameDeckBtn"), F<Button>("DeleteDeckBtn") };

            F<Button>("NewDeckBtn").Click += delegate { NewDeckPrompt(); };
            F<Button>("ImportDeckBtn").Click += delegate { ImportDeck(); };
            F<Button>("RenameDeckBtn").Click += delegate
            {
                if (currentDeck == null) return;
                string name = Ui.Prompt(this, "Rename deck", "Deck name", currentDeck.Name);
                if (name == null) return;
                currentDeck.Name = name;
                SaveDecks();
                RefreshDeckLists(currentDeck);
            };
            F<Button>("DeleteDeckBtn").Click += delegate
            {
                if (currentDeck == null) return;
                if (MessageBox.Show(this, "Delete the deck “" + currentDeck.Name + "”?", "Delete deck",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                deckFile.Decks.Remove(currentDeck);
                SaveDecks();
                RefreshDeckLists(deckFile.Decks.FirstOrDefault());
            };
            F<Button>("DeckAddBtn").Click += delegate { DeckAddByName(); };
            DeckAddBox.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { DeckAddByName(); e.Handled = true; } };
            F<Button>("DeckMinusBtn").Click += delegate { ChangeDeckQty(-1); };
            F<Button>("DeckPlusBtn").Click += delegate { ChangeDeckQty(1); };
            F<Button>("DeckRemoveBtn").Click += delegate { ChangeDeckQty(-100000); };
            F<Button>("DeckMoveBtn").Click += delegate { MoveDeckCards(); };
            F<Button>("DeckCopyBtn").Click += delegate
            {
                if (currentDeck == null) return;
                try { Clipboard.SetText(DeckText(currentDeck)); UpdateStatus("Deck list copied to the clipboard."); } catch { }
            };
            F<Button>("DeckExportTxtBtn").Click += delegate { ExportDeck(false); };
            F<Button>("DeckExportCsvBtn").Click += delegate { ExportDeck(true); };
            F<Button>("DeckPrintBtn").Click += delegate { PrintDeck(); };

            DeckList.SelectionChanged += delegate
            {
                if (deckSync) return;
                currentDeck = DeckList.SelectedItem as DeckData;
                deckSync = true;
                DeckPick.SelectedItem = currentDeck != null ? (object)currentDeck : NewDeckItem;
                deckSync = false;
                RefreshDeck();
            };
            DeckPick.SelectionChanged += delegate
            {
                if (deckSync) return;
                var d = DeckPick.SelectedItem as DeckData;
                if (d == null) return;
                currentDeck = d;
                deckSync = true;
                DeckList.SelectedItem = d;
                deckSync = false;
                RefreshDeck();
            };
            DeckGrid.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Delete) { ChangeDeckQty(-100000); e.Handled = true; }
                else if (e.Key == Key.Add || e.Key == Key.OemPlus) { ChangeDeckQty(1); e.Handled = true; }
                else if (e.Key == Key.Subtract || e.Key == Key.OemMinus) { ChangeDeckQty(-1); e.Handled = true; }
            };
            RefreshDeckLists(deckFile.Decks.FirstOrDefault());
        }

        void SaveDecks() { AppData.Save("decks.json", deckFile); }

        void RefreshDeckLists(DeckData select)
        {
            deckSync = true;
            currentDeck = select;
            DeckList.ItemsSource = new List<DeckData>(deckFile.Decks);
            DeckList.SelectedItem = select;
            var pick = new List<object>();
            foreach (DeckData d in deckFile.Decks) pick.Add(d);
            pick.Add(NewDeckItem);
            DeckPick.ItemsSource = pick;
            DeckPick.SelectedItem = select != null ? (object)select : NewDeckItem;
            deckSync = false;
            RefreshDeck();
        }

        DeckData NewDeckPrompt()
        {
            string name = Ui.Prompt(this, "New deck", "Deck name", "New deck");
            if (name == null) return null;
            var d = new DeckData();
            d.Name = name;
            deckFile.Decks.Add(d);
            SaveDecks();
            RefreshDeckLists(d);
            return d;
        }

        static void AddCard(DeckData deck, string id, string name, string set, string number, int qty, string board)
        {
            foreach (DeckCardData c in deck.Cards)
            {
                if (c.Board != board) continue;
                bool same = !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(c.Id)
                    ? string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Set ?? "", set ?? "", StringComparison.OrdinalIgnoreCase);
                if (same) { c.Qty += qty; return; }
            }
            var n = new DeckCardData();
            n.Id = id; n.Name = name; n.Set = set; n.Number = number; n.Qty = qty; n.Board = board;
            deck.Cards.Add(n);
        }

        void RefreshDeck()
        {
            if (DeckGrid == null) return;
            var owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (doc != null)
                foreach (Entry e in doc.Entries)
                {
                    string n = e.Card != null ? e.Card.Name : e.Name;
                    int have;
                    owned.TryGetValue(n, out have);
                    owned[n] = have + e.Quantity;
                }

            var keep = new HashSet<DeckCardData>(DeckGrid.SelectedItems.OfType<DeckCard>().Select(x => x.D));
            var list = new List<DeckCard>();
            if (currentDeck != null)
                foreach (DeckCardData c in currentDeck.Cards)
                {
                    var dc = new DeckCard();
                    dc.D = c;
                    dc.Card = Scryfall.I.Find(c.Id, c.Set, c.Number, c.Name);
                    if (dc.Card != null && string.IsNullOrEmpty(c.Id)) c.Id = dc.Card.Id;
                    int have;
                    if (owned.TryGetValue(dc.Name, out have)) dc.OwnedQty = have;
                    list.Add(dc);
                }
            deckView = list.OrderBy(x => x.IsSide).ThenBy(x => x.IsLand).ThenBy(x => x.Cmc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            DeckGrid.ItemsSource = deckView;
            foreach (DeckCard dc in deckView) if (keep.Contains(dc.D)) DeckGrid.SelectedItems.Add(dc);

            DeckTitle.Text = currentDeck == null ? "No deck selected" : currentDeck.Name;
            DeckEmpty.Visibility = deckView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DeckEmpty.Text = currentDeck == null
                ? "Create a deck with “New deck”, or import a deck list."
                : "This deck is empty. Add cards by name above, or select cards on the Collection tab and press “Add to deck”.";
            foreach (Button b in deckButtons) b.IsEnabled = currentDeck != null;
            DeckAddBox.IsEnabled = currentDeck != null;
            BuildDeckStats();
        }

        void BuildDeckStats()
        {
            DeckStats.Children.Clear();
            if (currentDeck == null) { DeckStats.Children.Add(Ui.Text("Deck statistics appear here.", 13, "Dim", false)); return; }
            List<DeckCard> main = deckView.Where(x => !x.IsSide).ToList();
            List<DeckCard> side = deckView.Where(x => x.IsSide).ToList();
            int mainCount = main.Sum(x => x.Qty), sideCount = side.Sum(x => x.Qty);
            int missing = deckView.Sum(x => x.Missing);
            double missingCost = deckView.Sum(x => (x.Price ?? 0) * x.Missing);

            DeckStats.Children.Add(Ui.Text("MAIN DECK", 11, "Dim", true));
            DeckStats.Children.Add(Ui.Text(mainCount + " cards", 24, "Fg", true));
            DeckStats.Children.Add(Ui.Text(sideCount + " in the sideboard", 13, "Dim", false));
            DeckStats.Children.Add(Gap(14));
            DeckStats.Children.Add(Ui.Text("DECK VALUE", 11, "Dim", true));
            DeckStats.Children.Add(Ui.Text(Fmt.Money(deckView.Sum(x => x.Total)), 24, "Good", true));
            if (doc == null) DeckStats.Children.Add(Ui.Text("Open your collection to see which cards you own.", 13, "Dim", false));
            else if (missing == 0 && deckView.Count > 0) DeckStats.Children.Add(Ui.Text("You own every card in this deck.", 13, "Good", false));
            else if (missing > 0) DeckStats.Children.Add(Ui.Text(missing + " cards missing, about " + Fmt.Money(missingCost) + " to buy", 13, "Gold", false));

            // Mana curve (non-land cards in the main deck)
            DeckStats.Children.Add(Gap(18));
            DeckStats.Children.Add(Ui.Text("MANA CURVE", 11, "Dim", true));
            var curve = new int[8];
            foreach (DeckCard c in main) if (!c.IsLand) curve[Math.Min(7, Math.Max(0, (int)c.Cmc))] += c.Qty;
            int top = Math.Max(1, curve.Max());
            var grid = new Grid();
            grid.Margin = new Thickness(0, 8, 0, 0);
            grid.Height = 132;
            for (int i = 0; i < 8; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                var col = new DockPanel();
                col.Margin = new Thickness(2, 0, 2, 0);
                TextBlock label = Ui.Text(i == 7 ? "7+" : i.ToString(), 11, "Dim", false);
                label.TextAlignment = TextAlignment.Center;
                DockPanel.SetDock(label, Dock.Bottom);
                col.Children.Add(label);
                var bar = new Border();
                bar.Background = Ui.B("Accent");
                bar.CornerRadius = new CornerRadius(3, 3, 0, 0);
                bar.Height = Math.Max(curve[i] > 0 ? 3 : 0, 92.0 * curve[i] / top);
                bar.VerticalAlignment = VerticalAlignment.Bottom;
                bar.Margin = new Thickness(0, 0, 0, 3);
                DockPanel.SetDock(bar, Dock.Bottom);
                col.Children.Add(bar);
                TextBlock count = Ui.Text(curve[i] > 0 ? curve[i].ToString() : "", 11, "Fg", false);
                count.TextAlignment = TextAlignment.Center;
                count.VerticalAlignment = VerticalAlignment.Bottom;
                col.Children.Add(count);
                Grid.SetColumn(col, i);
                grid.Children.Add(col);
            }
            DeckStats.Children.Add(grid);

            // Card types
            DeckStats.Children.Add(Gap(18));
            DeckStats.Children.Add(Ui.Text("CARD TYPES", 11, "Dim", true));
            DeckStats.Children.Add(Gap(6));
            string[] types = { "Creature", "Instant", "Sorcery", "Artifact", "Enchantment", "Planeswalker", "Land" };
            var counts = new List<KeyValuePair<string, double>>();
            foreach (string t in types)
            {
                int n = main.Where(x => x.TypeLine.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).Sum(x => x.Qty);
                if (n > 0) counts.Add(new KeyValuePair<string, double>(t, n));
            }
            double maxType = counts.Count > 0 ? counts.Max(x => x.Value) : 1;
            foreach (KeyValuePair<string, double> kv in counts)
                DeckStats.Children.Add(BarRow(kv.Key, kv.Value, maxType, ((int)kv.Value).ToString(), "Accent", 92, 36));
            if (counts.Count == 0) DeckStats.Children.Add(Ui.Text("No cards yet.", 13, "Dim", false));
        }

        static FrameworkElement Gap(double height)
        {
            var b = new Border();
            b.Height = height;
            return b;
        }

        async void DeckAddByName()
        {
            string name = DeckAddBox.Text.Trim();
            if (name.Length == 0 || currentDeck == null) return;
            DeckData deck = currentDeck;
            CardInfo card = null;
            if (doc != null)
            {
                Entry exact = doc.Entries.FirstOrDefault(e => e.Card != null && string.Equals(e.Card.Name, name, StringComparison.OrdinalIgnoreCase));
                if (exact != null) card = exact.Card;
                else
                {
                    List<CardInfo> partial = doc.Entries.Where(e => e.Card != null && e.Card.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(e => e.Card).GroupBy(c => c.Name).Select(g => g.First()).Take(2).ToList();
                    if (partial.Count == 1) card = partial[0];
                }
            }
            if (card == null) card = Scryfall.I.Find(null, null, null, name);
            if (card == null)
            {
                UpdateStatus("Searching Scryfall for “" + name + "”…");
                DeckAddBox.IsEnabled = false;
                card = await Scryfall.I.NamedAsync(name);
                DeckAddBox.IsEnabled = true;
            }
            if (card == null)
            {
                UpdateStatus("No card matching “" + name + "” was found. Check the spelling, or be more specific.");
                DeckAddBox.Focus();
                return;
            }
            AddCard(deck, card.Id, card.Name, card.Set, card.Number, 1, "main");
            SaveDecks();
            DeckAddBox.Text = "";
            RefreshDeck();
            DeckAddBox.Focus();
            UpdateStatus("Added " + card.Name + " to “" + deck.Name + "”.");
        }

        void AddSelectionToDeck()
        {
            List<Entry> sel = Selected();
            if (sel.Count == 0) return;
            DeckData deck = DeckPick.SelectedItem as DeckData;
            if (deck == null) deck = NewDeckPrompt();
            if (deck == null) return;
            foreach (Entry e in sel)
                AddCard(deck, e.Card != null ? e.Card.Id : e.ScryfallId, e.Card != null ? e.Card.Name : e.Name, e.RawSet, e.RawNumber, 1, "main");
            SaveDecks();
            RefreshDeckLists(deck);
            UpdateStatus("Added " + sel.Count + (sel.Count == 1 ? " card" : " cards") + " to “" + deck.Name + "”. Open the Decks tab to see it.");
        }

        void ChangeDeckQty(int delta)
        {
            if (currentDeck == null) return;
            List<DeckCard> sel = DeckGrid.SelectedItems.OfType<DeckCard>().ToList();
            if (sel.Count == 0) return;
            foreach (DeckCard c in sel)
            {
                c.D.Qty += delta;
                if (c.D.Qty <= 0) currentDeck.Cards.Remove(c.D);
            }
            SaveDecks();
            RefreshDeck();
        }

        void MoveDeckCards()
        {
            if (currentDeck == null) return;
            List<DeckCard> sel = DeckGrid.SelectedItems.OfType<DeckCard>().ToList();
            foreach (DeckCard c in sel)
            {
                currentDeck.Cards.Remove(c.D);
                AddCard(currentDeck, c.D.Id, c.D.Name, c.D.Set, c.D.Number, c.D.Qty, c.IsSide ? "main" : "side");
            }
            if (sel.Count > 0) { SaveDecks(); RefreshDeck(); }
        }

        string DeckText(DeckData deck)
        {
            var sb = new StringBuilder();
            Action<bool> section = delegate(bool sideboard)
            {
                foreach (DeckCard c in deckView.Where(x => x.IsSide == sideboard))
                {
                    sb.Append(c.Qty).Append(' ').Append(c.Name);
                    string set = c.Card != null ? c.Card.Set : c.D.Set;
                    string num = c.Card != null ? c.Card.Number : c.D.Number;
                    if (!string.IsNullOrEmpty(set))
                    {
                        sb.Append(" (").Append(set.ToUpperInvariant()).Append(')');
                        if (!string.IsNullOrEmpty(num)) sb.Append(' ').Append(num);
                    }
                    sb.AppendLine();
                }
            };
            sb.AppendLine("Deck");
            section(false);
            if (deckView.Any(x => x.IsSide))
            {
                sb.AppendLine();
                sb.AppendLine("Sideboard");
                section(true);
            }
            return sb.ToString();
        }

        void ExportDeck(bool csv)
        {
            if (currentDeck == null) return;
            var dlg = new SaveFileDialog();
            dlg.Title = "Export deck";
            dlg.Filter = csv ? "CSV file (*.csv)|*.csv" : "Deck list (*.txt)|*.txt";
            dlg.FileName = string.Join("_", currentDeck.Name.Split(Path.GetInvalidFileNameChars())) + (csv ? ".csv" : ".txt");
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                if (!csv) File.WriteAllText(dlg.FileName, DeckText(currentDeck), new UTF8Encoding(false));
                else
                {
                    var sb = new StringBuilder();
                    sb.Append(ManaBoxHeader);
                    foreach (DeckCard c in deckView)
                    {
                        CardInfo k = c.Card;
                        string[] row =
                        {
                            c.Name, (k != null ? k.Set : c.D.Set ?? "").ToUpperInvariant(), k != null ? k.SetName : "", k != null ? k.Number : c.D.Number ?? "",
                            "normal", k != null ? k.Rarity : "", c.Qty.ToString(), "", k != null ? k.Id : c.D.Id ?? "", "", "false", "false", "near_mint", "en", ""
                        };
                        sb.Append('\n').Append(Csv.Line(row, ','));
                    }
                    sb.Append('\n');
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
                }
                UpdateStatus("Exported “" + currentDeck.Name + "” to " + Path.GetFileName(dlg.FileName) + ".");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the file.\n\n" + ex.Message, "ManaBox Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void PrintDeck()
        {
            if (currentDeck == null || deckView.Count == 0) return;
            var rows = new List<string[]>();
            foreach (DeckCard c in deckView)
                rows.Add(new[] { "☐", c.Qty.ToString(), c.Name, c.ManaCost, c.TypeLine, c.IsSide ? "Side" : "Main", c.OwnedQty.ToString(), c.TotalText });
            string subtitle = string.Format("{0} main · {1} sideboard · {2} · printed {3:d MMM yyyy}",
                deckView.Where(x => !x.IsSide).Sum(x => x.Qty), deckView.Where(x => x.IsSide).Sum(x => x.Qty),
                Fmt.Money(deckView.Sum(x => x.Total)), DateTime.Now);
            Printer.Print(this, currentDeck.Name, subtitle,
                new[] { "", "Qty", "Name", "Mana", "Type", "Board", "Owned", "Total" },
                new double[] { 0.5, 0.8, 5, 1.8, 4.5, 1.1, 1.2, 1.6 },
                new[] { false, true, false, false, false, false, true, true }, rows);
        }

        static readonly Regex DeckLine = new Regex(@"^(?:SB:\s*)?(\d+)\s*x?\s+(.+?)(?:\s+\(([A-Za-z0-9]{2,6})\)(?:\s+(\S+))?)?\s*$", RegexOptions.Compiled);

        static void ParseDeckText(string text, DeckData deck)
        {
            string board = "main";
            bool explicitSections = false, sawCards = false;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    if (sawCards && !explicitSections) board = "side";   // MTGO style: blank line before the sideboard
                    continue;
                }
                if (line.StartsWith("//") || line.StartsWith("#")) continue;
                string head = line.TrimEnd(':').ToLowerInvariant();
                if (head == "deck" || head == "main" || head == "mainboard" || head == "commander" || head == "companion")
                {
                    board = "main"; explicitSections = true; continue;
                }
                if (head == "sideboard" || head == "side" || head == "maybeboard")
                {
                    board = "side"; explicitSections = true; continue;
                }
                Match m = DeckLine.Match(line);
                if (!m.Success) continue;
                string target = line.StartsWith("SB:", StringComparison.OrdinalIgnoreCase) ? "side" : board;
                AddCard(deck, null, m.Groups[2].Value.Trim(), m.Groups[3].Success ? m.Groups[3].Value : null,
                    m.Groups[4].Success ? m.Groups[4].Value : null, int.Parse(m.Groups[1].Value), target);
                sawCards = true;
            }
        }

        async void ImportDeck()
        {
            var dlg = new OpenFileDialog();
            dlg.Title = "Import a deck list";
            dlg.Filter = "Deck lists (*.txt;*.csv;*.dec)|*.txt;*.csv;*.dec|All files (*.*)|*.*";
            if (dlg.ShowDialog(this) != true) return;
            var deck = new DeckData();
            deck.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
            try
            {
                if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    CollectionDoc d = CollectionDoc.Load(dlg.FileName);
                    foreach (Entry e in d.Entries)
                        AddCard(deck, e.ScryfallId, e.RawName, e.RawSet, e.RawNumber, Math.Max(1, e.Quantity), "main");
                }
                else ParseDeckText(File.ReadAllText(dlg.FileName), deck);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not read this deck list.\n\n" + ex.Message, "ManaBox Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (deck.Cards.Count == 0)
            {
                MessageBox.Show(this, "No cards were found in this file.\n\nExpected lines such as “4 Lightning Bolt” or “4 Lightning Bolt (2X2) 117”.",
                    "ManaBox Companion", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            deckFile.Decks.Add(deck);
            SaveDecks();
            RefreshDeckLists(deck);
            await FetchPrices(false);
            SaveDecks();
        }

        void BuildDemoDeck()
        {
            if (doc == null) return;
            var deck = new DeckData();
            deck.Name = "Sample deck";
            int i = 0;
            foreach (Entry e in doc.Entries.Where(x => x.Card != null).OrderBy(x => x.Card.Cmc).ThenBy(x => x.Name))
            {
                bool land = e.Card.TypeLine != null && e.Card.TypeLine.Contains("Land");
                AddCard(deck, e.Card.Id, e.Card.Name, e.RawSet, e.RawNumber, land ? 4 : 1 + (i % 4), i % 9 == 8 ? "side" : "main");
                if (++i >= 34) break;
            }
            deckFile.Decks.Clear();
            deckFile.Decks.Add(deck);
            RefreshDeckLists(deck);
        }
    }
}
