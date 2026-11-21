using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ManaBoxCompanion
{
    /// <summary>
    /// Built-in checks, run with:  ManaBoxCompanion.exe --open file.csv --selftest report.txt
    /// They drive the real window and dialogs, so a release can be verified without clicking through it by hand.
    /// </summary>
    partial class MainWindow
    {
        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var d = child as DependencyObject;
                if (d == null) continue;
                if (d is T) yield return (T)d;
                foreach (T t in Descendants<T>(d)) yield return t;
            }
        }

        static void ClickDefault(Window w)
        {
            Button ok = Descendants<Button>(w).First(b => b.IsDefault);
            ok.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        void SelfTest(string reportPath)
        {
            var log = new StringBuilder();
            int failures = 0;
            Action<string, bool, string> check = delegate(string name, bool ok, string detail)
            {
                if (!ok) failures++;
                log.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (string.IsNullOrEmpty(detail) ? "" : "   [" + detail + "]"));
            };
            string dir = Path.GetDirectoryName(reportPath);

            try
            {
                // 1. CSV with awkward fields survives a load/save round trip byte for byte.
                string tricky = "﻿Name,Set code,Quantity,Scryfall ID,Notes\r\n" +
                                "\"Sheoldred, the Apocalypse\",DMU,1,,plain\r\n" +
                                "\"Kongming, \"\"Sleeping Dragon\"\"\",PTK,2,,\"two\r\nlines\"\r\n" +
                                "Fire // Ice,MH2,3,,\r\n";
                string a = Path.Combine(dir, "selftest-in.csv"), b = Path.Combine(dir, "selftest-out.csv");
                File.WriteAllText(a, tricky, new UTF8Encoding(false));
                CollectionDoc t = CollectionDoc.Load(a);
                t.Save(b);
                check("csv round trip is byte-identical", File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)), null);
                check("csv quoted fields parsed", t.Entries.Count == 3 && t.Entries[0].Name == "Sheoldred, the Apocalypse"
                    && t.Entries[1].Name == "Kongming, \"Sleeping Dragon\"" && t.Entries[1].Row[4] == "two\r\nlines" && t.Entries[2].Quantity == 3,
                    t.Entries.Count + " rows");

                // 2. Filters and sorting.
                check("collection loaded with Scryfall data", doc != null && doc.Entries.Count > 0 && doc.Entries.All(e => e.Card != null),
                    doc == null ? "no doc" : doc.Entries.Count(e => e.Card != null) + "/" + doc.Entries.Count);
                int total = doc.Entries.Count;
                SearchBox.Text = "t:creature";
                searchTimer.Stop();
                ApplyFilter();
                check("search t:creature", view.Count > 0 && view.Count < total && view.All(e => e.TypeLine.Contains("Creature")), view.Count + " of " + total);
                SearchBox.Text = "";
                colourPips[3].IsChecked = true;   // red
                ApplyFilter();
                check("colour filter R", view.Count > 0 && view.All(e => e.Identity.Contains("R")), view.Count.ToString());
                colourPips[3].IsChecked = false;
                RarityBox.SelectedIndex = 4;      // mythic
                check("rarity filter mythic", view.Count > 0 && view.All(e => e.Rarity == "mythic"), view.Count.ToString());
                RarityBox.SelectedIndex = 0;
                SortBox.SelectedItem = "Price";
                bool sorted = true;
                for (int i = 1; i < view.Count; i++) if (view[i].Price > view[i - 1].Price) sorted = false;
                check("sort by price, highest first", view.Count == total && sorted, view.Count > 0 ? view[0].Name + " " + view[0].PriceText : "");
                SortBox.SelectedItem = "Name";
                ViewList.IsChecked = true;
                check("list view shows the same rows", ListGrid.Items.Count == total && ListGrid.Visibility == Visibility.Visible, ListGrid.Items.Count.ToString());
                ViewGallery.IsChecked = true;

                // 3. Bulk edit through the real dialog.
                List<Entry> three = view.Take(3).ToList();
                int[] before = three.Select(e => e.Quantity).ToArray();
                foreach (Entry e in three) e.IsSelected = true;
                string shot;
                opts.TryGetValue("dialogshot", out shot);
                Ui.DialogHook = delegate(Window w)
                {
                    List<ComboBox> combos = Descendants<ComboBox>(w).ToList();
                    List<TextBox> boxes = Descendants<TextBox>(w).ToList();
                    combos[0].SelectedIndex = 2;                 // quantity: add
                    boxes[0].Text = "2";
                    combos[1].SelectedIndex = 6;                 // condition: played
                    boxes[2].Text = "Test Binder";
                    if (shot != null)
                    {
                        w.UpdateLayout();
                        var fe = (FrameworkElement)w.Content;
                        DpiScale dpi = VisualTreeHelper.GetDpi(w);
                        double fw = fe.ActualWidth + fe.Margin.Left + fe.Margin.Right, fh = fe.ActualHeight + fe.Margin.Top + fe.Margin.Bottom;
                        var bmp = new RenderTargetBitmap((int)Math.Ceiling(fw * dpi.DpiScaleX), (int)Math.Ceiling(fh * dpi.DpiScaleY),
                            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                        var bg = new DrawingVisual();
                        using (DrawingContext dc = bg.RenderOpen()) dc.DrawRectangle(Ui.B("Bg"), null, new Rect(0, 0, fw, fh));
                        bmp.Render(bg);
                        bmp.Render(fe);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(bmp));
                        using (FileStream f = File.Create(shot)) enc.Save(f);
                    }
                    ClickDefault(w);
                };
                EditSelected();
                check("bulk edit: quantity +2", three.Select(e => e.Quantity).SequenceEqual(before.Select(q => q + 2)),
                    string.Join(",", three.Select(e => e.Quantity.ToString()).ToArray()));
                check("bulk edit: condition and binder", three.All(e => e.Condition == "played" && e.Binder == "Test Binder"),
                    three[0].Condition + " / " + three[0].Binder);
                check("bulk edit: marks collection as edited", dirty, null);
                check("bulk edit: binder filter lists new binder", BinderBox.Items.Contains("Test Binder"), null);

                // Setting a single entry to quantity 0 removes it.
                ClearSelection();
                Entry victim = view[view.Count - 1];
                victim.IsSelected = true;
                Ui.DialogHook = delegate(Window w)
                {
                    Descendants<TextBox>(w).First().Text = "0";
                    ClickDefault(w);
                };
                EditSelected();
                check("edit to quantity 0 removes the entry", doc.Entries.Count == total - 1 && !doc.Entries.Contains(victim) && view.Count == total - 1,
                    doc.Entries.Count + " left");

                // The edited collection still exports as a valid ManaBox CSV.
                string edited = Path.Combine(dir, "selftest-edited.csv");
                doc.Save(edited);
                CollectionDoc again = CollectionDoc.Load(edited);
                check("edited export reloads", again.Entries.Count == total - 1 && again.Headers.SequenceEqual(doc.Headers)
                    && again.Entries.Count(e => e.Binder == "Test Binder") == 3, again.Entries.Count + " rows");

                // 4. Decks.
                var deck = new DeckData();
                deck.Name = "parse test";
                ParseDeckText("Deck\n4 Lightning Bolt (2X2) 117\n2x Counterspell\n1 Sol Ring\n4 Lightning Bolt (2X2) 117\n\nSideboard\n3 Negate\nSB: 1 Duress\n", deck);
                check("deck list parsing", deck.Cards.Count == 5 && deck.Cards.Where(c => c.Board == "main").Sum(c => c.Qty) == 11
                    && deck.Cards.Where(c => c.Board == "side").Sum(c => c.Qty) == 4 && deck.Cards[0].Set == "2X2" && deck.Cards[0].Number == "117",
                    deck.Cards.Count + " lines");

                Ui.DialogHook = delegate(Window w)
                {
                    Descendants<TextBox>(w).First().Text = "My Test Deck";
                    ClickDefault(w);
                };
                int decksBefore = deckFile.Decks.Count;
                ClearSelection();
                foreach (Entry e in view.Take(5)) e.IsSelected = true;
                DeckPick.SelectedItem = NewDeckItem;
                AddSelectionToDeck();
                check("new deck from selection", deckFile.Decks.Count == decksBefore + 1 && currentDeck != null && currentDeck.Name == "My Test Deck"
                    && currentDeck.Cards.Count == 5 && deckView.Count == 5 && deckView.All(c => c.Card != null && c.OwnedQty > 0),
                    currentDeck == null ? "none" : currentDeck.Name + ", " + currentDeck.Cards.Count + " cards");
                DeckGrid.SelectedItems.Clear();
                DeckGrid.SelectedItems.Add(deckView[0]);
                ChangeDeckQty(3);
                DeckGrid.SelectedItems.Clear();
                DeckGrid.SelectedItems.Add(deckView.First(c => c.Qty == 1));
                MoveDeckCards();
                string text = DeckText(currentDeck);
                check("deck quantity, sideboard and text export", deckView.Sum(c => c.Qty) == 8 && deckView.Count(c => c.IsSide) == 1
                    && text.StartsWith("Deck") && text.Contains("Sideboard") && text.Contains("4 " + deckView.First(c => c.Qty == 4).Name),
                    text.Replace("\r\n", " | "));
                Ui.DialogHook = null;

                // 5. Print layout builds and paginates.
                var rows = new List<string[]>();
                foreach (Entry e in doc.Entries) rows.Add(new[] { "☐", e.Quantity.ToString(), e.Name, e.SetCode, e.Number, e.FinishLabel, e.ConditionLabel, e.PriceText, e.TotalText });
                FlowDocument fd = Printer.Build("Test", "subtitle", new[] { "", "Qty", "Name", "Set", "#", "Finish", "Condition", "Price", "Total" },
                    new double[] { 0.5, 0.8, 6, 1.1, 1.1, 1.3, 2, 1.6, 1.7 }, new[] { false, true, false, false, false, false, false, true, true }, rows, 793, 1122);
                DocumentPaginator pg = ((IDocumentPaginatorSource)fd).DocumentPaginator;
                pg.ComputePageCount();
                check("print layout paginates", pg.PageCount >= 2, pg.PageCount + " pages for " + rows.Count + " rows");

                // 6. Value tab and history.
                RecordSnapshot();
                Tabs.SelectedIndex = 2;
                RefreshValue();
                check("value tab populated", ValueTiles.Children.Count >= 4 && TopCards.Children.Count > 0 && ByRarity.Children.Count >= 4
                    && history.Items.Any(x => x.Key == doc.Key && x.Usd > 0 && x.Eur > 0), ValueTiles.Children.Count + " tiles");
                CurrencyBox.SelectedItem = "EUR";
                check("currency switch to EUR", Fmt.Currency == "EUR" && doc.Entries.Any(e => e.PriceText.StartsWith("€")), doc.Entries[0].PriceText);
                CurrencyBox.SelectedItem = "USD";
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("FAIL  exception: " + ex);
            }
            log.AppendLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            File.WriteAllText(reportPath, log.ToString());
            dirty = false;
        }
    }
}
