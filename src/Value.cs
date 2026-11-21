using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ManaBoxCompanion
{
    partial class MainWindow
    {
        HistoryFile history = new HistoryFile();
        WrapPanel ValueTiles;
        Canvas ChartCanvas;
        StackPanel TopCards, ByRarity, BySet;
        TextBlock ValueNote, ChartTitle;

        void InitValue()
        {
            history = AppData.Load<HistoryFile>("history.json");
            if (history.Items == null) history.Items = new List<Snapshot>();
            ValueTiles = F<WrapPanel>("ValueTiles"); ChartCanvas = F<Canvas>("ChartCanvas");
            TopCards = F<StackPanel>("TopCards"); ByRarity = F<StackPanel>("ByRarity"); BySet = F<StackPanel>("BySet");
            ValueNote = F<TextBlock>("ValueNote"); ChartTitle = F<TextBlock>("ChartTitle");
            ChartCanvas.SizeChanged += delegate { DrawChart(); };
            RefreshValue();
        }

        /// <summary>Stores today's total for this collection (one snapshot per day, keyed by file name).</summary>
        void RecordSnapshot()
        {
            if (doc == null || doc.Entries.Count == 0) return;
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string key = doc.Key;
            Snapshot s = history.Items.FirstOrDefault(x => x.Key == key && x.Date == today);
            if (s == null)
            {
                s = new Snapshot();
                s.Key = key;
                s.Date = today;
                history.Items.Add(s);
            }
            s.Usd = Math.Round(doc.Entries.Sum(e => e.Value("USD")), 2);
            s.Eur = Math.Round(doc.Entries.Sum(e => e.Value("EUR")), 2);
            s.Cards = doc.Entries.Sum(e => e.Quantity);
            s.Unique = doc.Entries.Count;
            AppData.Save("history.json", history);
        }

        static Border Tile(string label, string value, string sub, string valueBrush)
        {
            var b = new Border();
            b.Background = Ui.B("Panel");
            b.CornerRadius = new CornerRadius(10);
            b.Padding = new Thickness(18, 14, 18, 14);
            b.Margin = new Thickness(0, 0, 14, 14);
            b.MinWidth = 200;
            var sp = new StackPanel();
            sp.Children.Add(Ui.Text(label.ToUpperInvariant(), 11, "Dim", true));
            TextBlock v = Ui.Text(value, 25, valueBrush, true);
            v.Margin = new Thickness(0, 4, 0, 2);
            v.TextWrapping = TextWrapping.NoWrap;
            sp.Children.Add(v);
            TextBlock s = Ui.Text(sub, 12, "Dim", false);
            s.MaxWidth = 260;
            sp.Children.Add(s);
            b.Child = sp;
            return b;
        }

        /// <summary>A label, a proportional bar and a value on one line.</summary>
        static Grid BarRow(string label, double value, double max, string text, string brush, double labelWidth, double valueWidth)
        {
            var g = new Grid();
            g.Margin = new Thickness(0, 0, 0, 7);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueWidth) });
            TextBlock l = Ui.Text(label, 13, "Fg", false);
            l.TextWrapping = TextWrapping.NoWrap;
            l.TextTrimming = TextTrimming.CharacterEllipsis;
            l.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(l);

            var track = new Grid();
            track.Height = 10;
            track.VerticalAlignment = VerticalAlignment.Center;
            double share = max <= 0 ? 0 : Math.Max(0, Math.Min(1, value / max));
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.004), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - share, 0.0001), GridUnitType.Star) });
            var bg = new Border();
            bg.Background = Ui.B("Ctl");
            bg.CornerRadius = new CornerRadius(5);
            Grid.SetColumnSpan(bg, 2);
            track.Children.Add(bg);
            var fill = new Border();
            fill.Background = Ui.B(brush);
            fill.CornerRadius = new CornerRadius(5);
            track.Children.Add(fill);
            Grid.SetColumn(track, 1);
            g.Children.Add(track);

            TextBlock v = Ui.Text(text, 13, "Dim", false);
            v.TextAlignment = TextAlignment.Right;
            v.TextWrapping = TextWrapping.NoWrap;
            v.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(v, 2);
            g.Children.Add(v);
            return g;
        }

        void RefreshValue()
        {
            if (ValueTiles == null) return;
            ValueTiles.Children.Clear();
            TopCards.Children.Clear();
            ByRarity.Children.Clear();
            BySet.Children.Clear();
            if (doc == null || doc.Entries.Count == 0)
            {
                ValueNote.Text = "Open a ManaBox CSV export on the Collection tab to see what your collection is worth.";
                DrawChart();
                return;
            }

            string cur = Fmt.Currency;
            List<Entry> all = doc.Entries;
            double total = all.Sum(e => e.Total);
            int cards = all.Sum(e => e.Quantity);
            int unpriced = all.Count(e => e.UnitPrice(cur) == null);

            ValueTiles.Children.Add(Tile("Collection value", Fmt.Money(total), "Scryfall market prices in " + cur, "Good"));
            ValueTiles.Children.Add(Tile("Cards", Fmt.Int(cards), Fmt.Int(all.Count) + " different entries", "Fg"));
            ValueTiles.Children.Add(Tile("Average per card", Fmt.Money(cards > 0 ? total / cards : 0), "Total value divided by card count", "Fg"));

            List<Entry> bought = all.Where(e => (e.PurchasePrice ?? 0) > 0 && (e.PurchaseCurrency.Length == 0 || e.PurchaseCurrency == cur)).ToList();
            if (bought.Count > 0)
            {
                double paid = bought.Sum(e => e.PurchasePrice.Value * e.Quantity);
                double now = bought.Sum(e => e.Total);
                double diff = now - paid;
                ValueTiles.Children.Add(Tile("You paid", Fmt.Money(paid), "For the " + Fmt.Int(bought.Sum(e => e.Quantity)) + " cards with a purchase price", "Fg"));
                string pct = paid > 0 ? string.Format(CultureInfo.InvariantCulture, " ({0:+0.#;-0.#}%)", diff / paid * 100) : "";
                ValueTiles.Children.Add(Tile(diff >= 0 ? "Gain" : "Loss", (diff >= 0 ? "+" : "−") + Fmt.Money(Math.Abs(diff)) + pct,
                    "Those cards are now worth " + Fmt.Money(now), diff >= 0 ? "Good" : "Bad"));
            }
            else
                ValueTiles.Children.Add(Tile("You paid", "—", "No purchase prices in " + cur + " in this export", "Dim"));

            var notes = new List<string>();
            if (!pricesComplete) notes.Add("Prices may be out of date: Scryfall has not been reached since this collection was opened.");
            if (unpriced > 0) notes.Add(Fmt.Int(unpriced) + " entries have no " + cur + " price on Scryfall and count as " + Fmt.Money(0) + ".");
            ValueNote.Text = string.Join(" ", notes.ToArray());

            // Most valuable cards
            foreach (Entry e in all.OrderByDescending(x => x.Total).Take(15))
            {
                if (e.Total <= 0) break;
                var row = new DockPanel();
                row.Margin = new Thickness(0, 0, 0, 9);
                TextBlock val = Ui.Text(Fmt.Money(e.Total), 13, "Good", true);
                val.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(val, Dock.Right);
                row.Children.Add(val);
                var left = new StackPanel();
                TextBlock name = Ui.Text((e.Quantity > 1 ? e.Quantity + "× " : "") + e.Name, 13, "Fg", false);
                name.TextWrapping = TextWrapping.NoWrap;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                left.Children.Add(name);
                left.Children.Add(Ui.Text(e.SetLine + (e.IsFoil ? " · " + e.FinishLabel : ""), 11, "Dim", false));
                row.Children.Add(left);
                TopCards.Children.Add(row);
            }
            if (TopCards.Children.Count == 0) TopCards.Children.Add(Ui.Text("No prices yet.", 13, "Dim", false));

            // By rarity
            string[] order = { "mythic", "rare", "uncommon", "common" };
            var byRarity = new List<KeyValuePair<string, double>>();
            foreach (string r in order) byRarity.Add(new KeyValuePair<string, double>(Fmt.Title(r), all.Where(e => e.Rarity == r).Sum(e => e.Total)));
            double other = all.Where(e => Array.IndexOf(order, e.Rarity) < 0).Sum(e => e.Total);
            if (other > 0) byRarity.Add(new KeyValuePair<string, double>("Other", other));
            double maxR = Math.Max(0.01, byRarity.Max(x => x.Value));
            foreach (KeyValuePair<string, double> kv in byRarity)
                ByRarity.Children.Add(BarRow(kv.Key, kv.Value, maxR, Fmt.Money(kv.Value), "Accent", 110, 110));

            // By set
            var sets = all.GroupBy(e => e.SetCode).Select(g => new { Name = g.First().SetName.Length > 0 ? g.First().SetName : g.Key, Value = g.Sum(e => e.Total) })
                .OrderByDescending(x => x.Value).Take(8).ToList();
            double maxS = sets.Count > 0 ? Math.Max(0.01, sets[0].Value) : 1;
            foreach (var s in sets) BySet.Children.Add(BarRow(s.Name, s.Value, maxS, Fmt.Money(s.Value), "Gold", 210, 110));

            DrawChart();
        }

        void DrawChart()
        {
            if (ChartCanvas == null) return;
            ChartCanvas.Children.Clear();
            double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
            if (w < 120 || h < 80) return;
            string cur = Fmt.Currency;
            List<Snapshot> pts = doc == null ? new List<Snapshot>()
                : history.Items.Where(x => x.Key == doc.Key).OrderBy(x => x.Date, StringComparer.Ordinal).ToList();
            ChartTitle.Text = doc == null ? "Collection value over time" : "Value of “" + doc.Key + "” over time";
            if (pts.Count == 0)
            {
                TextBlock t = Ui.Text(doc == null ? "No collection open." : "No snapshots yet. One is saved as soon as fresh prices have loaded.", 13, "Dim", false);
                Canvas.SetLeft(t, 4);
                Canvas.SetTop(t, h / 2 - 10);
                ChartCanvas.Children.Add(t);
                return;
            }

            const double left = 78, right = 18, top = 12, bottom = 26;
            double plotW = w - left - right, plotH = h - top - bottom;
            Func<Snapshot, double> val = delegate(Snapshot s) { return cur == "EUR" ? s.Eur : s.Usd; };
            double min = pts.Min(val), max = pts.Max(val);
            double pad = Math.Max((max - min) * 0.15, Math.Max(max * 0.05, 1));
            min = Math.Max(0, min - pad);
            max = max + pad;

            Func<string, DateTime> date = delegate(string s) { return DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture); };
            DateTime d0 = date(pts[0].Date), d1 = date(pts[pts.Count - 1].Date);
            double span = Math.Max(1, (d1 - d0).TotalDays);
            Func<Snapshot, Point> at = delegate(Snapshot s)
            {
                double x = pts.Count == 1 ? left + plotW / 2 : left + plotW * (date(s.Date) - d0).TotalDays / span;
                double y = top + plotH * (1 - (val(s) - min) / (max - min));
                return new Point(x, y);
            };

            for (int i = 0; i <= 4; i++)
            {
                double y = top + plotH * i / 4;
                var line = new Line();
                line.X1 = left; line.X2 = w - right; line.Y1 = y; line.Y2 = y;
                line.Stroke = Ui.B("Line");
                line.StrokeThickness = 1;
                ChartCanvas.Children.Add(line);
                TextBlock label = Ui.Text(Fmt.Money(max - (max - min) * i / 4), 11, "Dim", false);
                label.Width = left - 10;
                label.TextAlignment = TextAlignment.Right;
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - 8);
                ChartCanvas.Children.Add(label);
            }

            if (pts.Count > 1)
            {
                var area = new Polygon();
                area.Fill = new SolidColorBrush(Color.FromArgb(40, 0x7C, 0x5C, 0xFF));
                area.Points.Add(new Point(at(pts[0]).X, top + plotH));
                foreach (Snapshot s in pts) area.Points.Add(at(s));
                area.Points.Add(new Point(at(pts[pts.Count - 1]).X, top + plotH));
                ChartCanvas.Children.Add(area);
                var poly = new Polyline();
                poly.Stroke = Ui.B("Accent");
                poly.StrokeThickness = 2.5;
                poly.StrokeLineJoin = PenLineJoin.Round;
                foreach (Snapshot s in pts) poly.Points.Add(at(s));
                ChartCanvas.Children.Add(poly);
            }
            foreach (Snapshot s in pts)
            {
                if (pts.Count > 40 && s != pts[pts.Count - 1]) continue;
                Point p = at(s);
                var dot = new Ellipse();
                dot.Width = 9; dot.Height = 9;
                dot.Fill = Ui.B("Accent");
                dot.Stroke = Ui.B("Panel");
                dot.StrokeThickness = 2;
                dot.ToolTip = date(s.Date).ToString("d MMM yyyy") + ": " + Fmt.Money(val(s)) + " · " + Fmt.Int(s.Cards) + " cards";
                Canvas.SetLeft(dot, p.X - 4.5);
                Canvas.SetTop(dot, p.Y - 4.5);
                ChartCanvas.Children.Add(dot);
            }

            TextBlock first = Ui.Text(d0.ToString("d MMM yyyy"), 11, "Dim", false);
            Canvas.SetLeft(first, pts.Count == 1 ? left + plotW / 2 - 32 : left);
            Canvas.SetTop(first, h - 18);
            ChartCanvas.Children.Add(first);
            if (pts.Count > 1)
            {
                TextBlock last = Ui.Text(d1.ToString("d MMM yyyy"), 11, "Dim", false);
                last.Width = 90;
                last.TextAlignment = TextAlignment.Right;
                Canvas.SetLeft(last, w - right - 90);
                Canvas.SetTop(last, h - 18);
                ChartCanvas.Children.Add(last);
            }
            else
            {
                Point p = at(pts[0]);
                TextBlock only = Ui.Text(Fmt.Money(val(pts[0])) + " today. The line appears once there are snapshots from more than one day.", 12, "Dim", false);
                only.MaxWidth = plotW - 20;
                Canvas.SetLeft(only, left + 10);
                Canvas.SetTop(only, Math.Min(p.Y + 12, h - 46));
                ChartCanvas.Children.Add(only);
            }
        }
    }
}
