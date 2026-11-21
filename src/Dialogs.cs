using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ManaBoxCompanion
{
    /// <summary>Edits one entry, or applies the same change to many at once.</summary>
    static class EditDialog
    {
        static readonly string[] Conditions = { "mint", "near_mint", "excellent", "good", "light_played", "played", "poor" };
        const string Keep = "No change";

        static Grid Row(string label, UIElement control)
        {
            var g = new Grid();
            g.Margin = new Thickness(0, 0, 0, 10);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock t = Ui.Text(label, 13, "Dim", false);
            t.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(t);
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }

        /// <summary>Returns true when something changed. Entries whose quantity dropped to zero come back in <paramref name="emptied"/>.</summary>
        public static bool Show(Window owner, CollectionDoc doc, List<Entry> items, out List<Entry> emptied)
        {
            emptied = new List<Entry>();
            bool single = items.Count == 1;
            Entry first = items[0];
            Window w = Ui.Dialog(owner, single ? "Edit " + first.Name : "Bulk edit " + items.Count + " entries", 440);
            var sp = new StackPanel();
            sp.Margin = new Thickness(20, 18, 20, 18);

            sp.Children.Add(Ui.Text(single
                ? first.Name + "  ·  " + first.SetLine
                : "The same change is applied to all " + items.Count + " selected entries. Leave a field empty or on “No change” to keep each card's own value.",
                13, single ? "Fg" : "Dim", single));
            ((FrameworkElement)sp.Children[0]).Margin = new Thickness(0, 0, 0, 14);

            // Quantity
            var qtyMode = new ComboBox();
            qtyMode.ItemsSource = new[] { Keep, "Set to", "Add" };
            qtyMode.SelectedIndex = single ? 1 : 0;
            qtyMode.Width = 110;
            var qtyBox = new TextBox();
            qtyBox.Margin = new Thickness(8, 0, 0, 0);
            qtyBox.Tag = "number (negative to remove)";
            if (single) qtyBox.Text = first.Quantity.ToString();
            var qtyPanel = new DockPanel();
            DockPanel.SetDock(qtyMode, Dock.Left);
            qtyPanel.Children.Add(qtyMode);
            qtyPanel.Children.Add(qtyBox);
            if (doc.cQty >= 0) sp.Children.Add(Row("Quantity", qtyPanel));

            // Condition
            var condValues = new List<string>(Conditions);
            foreach (string c in doc.Entries.Select(e => e.Condition).Where(c => c.Length > 0).Distinct())
                if (!condValues.Contains(c)) condValues.Add(c);
            var condBox = new ComboBox();
            var condLabels = new List<string>();
            condLabels.Add(Keep);
            condLabels.AddRange(condValues.Select(c => Fmt.Title(c)));
            condBox.ItemsSource = condLabels;
            condBox.SelectedIndex = single ? condValues.IndexOf(first.Condition) + 1 : 0;
            if (doc.cCondition >= 0) sp.Children.Add(Row("Condition", condBox));

            // Finish
            string[] finishValues = { "normal", "foil", "etched" };
            var finBox = new ComboBox();
            finBox.ItemsSource = new[] { Keep, "Non-foil", "Foil", "Etched" };
            finBox.SelectedIndex = single ? Array.IndexOf(finishValues, first.Finish) + 1 : 0;
            if (doc.cFoil >= 0) sp.Children.Add(Row("Finish", finBox));

            // Language, binder, purchase price
            var langBox = new TextBox();
            langBox.Tag = single ? "" : "e.g. en, de, ja";
            if (single) langBox.Text = first.Language;
            if (doc.cLanguage >= 0) sp.Children.Add(Row("Language", langBox));

            var binderBox = new TextBox();
            binderBox.Tag = single ? "" : "binder name";
            if (single) binderBox.Text = first.Binder;
            if (doc.cBinder >= 0) sp.Children.Add(Row("Binder", binderBox));

            var priceBox = new TextBox();
            priceBox.Tag = "price paid per card";
            if (single && first.PurchasePrice != null) priceBox.Text = Fmt.NumText(first.PurchasePrice.Value);
            if (doc.cPurchase >= 0) sp.Children.Add(Row("Purchase price", priceBox));

            TextBlock error = Ui.Text("", 12, "Bad", false);
            error.Margin = new Thickness(0, 0, 0, 8);
            error.Visibility = Visibility.Collapsed;
            sp.Children.Add(error);
            sp.Children.Add(Ui.Text("Cards whose quantity reaches 0 are removed from the collection.", 12, "Dim", false));

            var buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Margin = new Thickness(0, 16, 0, 0);
            var ok = new Button();
            ok.Content = single ? "Save" : "Apply to " + items.Count;
            ok.IsDefault = true;
            ok.MinWidth = 96;
            ok.Style = (Style)Application.Current.Resources["Primary"];
            var cancel = new Button();
            cancel.Content = "Cancel";
            cancel.IsCancel = true;
            cancel.MinWidth = 84;
            cancel.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            sp.Children.Add(buttons);
            w.Content = sp;

            bool changed = false;
            var removed = emptied;
            ok.Click += delegate
            {
                int qty = 0;
                if (qtyMode.SelectedIndex > 0 && !int.TryParse(qtyBox.Text.Trim(), out qty))
                {
                    error.Text = "Quantity must be a whole number.";
                    error.Visibility = Visibility.Visible;
                    return;
                }
                double? price = null;
                string priceText = priceBox.Text.Trim();
                if (priceText.Length > 0)
                {
                    price = Fmt.Num(priceText);
                    if (price == null || price.Value < 0)
                    {
                        error.Text = "Purchase price must be a number, for example 2.50.";
                        error.Visibility = Visibility.Visible;
                        return;
                    }
                }
                string lang = langBox.Text.Trim();
                string binder = binderBox.Text.Trim();
                foreach (Entry e in items)
                {
                    if (qtyMode.SelectedIndex == 1) e.Quantity = qty;
                    else if (qtyMode.SelectedIndex == 2) e.Quantity = e.Quantity + qty;
                    if (condBox.SelectedIndex > 0) e.Condition = condValues[condBox.SelectedIndex - 1];
                    if (finBox.SelectedIndex > 0) e.Finish = finishValues[finBox.SelectedIndex - 1];
                    if (lang.Length > 0 || single) { if (doc.cLanguage >= 0) e.Language = lang; }
                    if (binder.Length > 0 || single) { if (doc.cBinder >= 0) e.Binder = binder; }
                    if (price != null) e.PurchasePrice = price;
                    else if (single && doc.cPurchase >= 0) e.PurchasePrice = null;
                    if (e.Quantity <= 0) removed.Add(e);
                }
                changed = true;
                w.DialogResult = true;
            };
            w.ShowDialog();
            return changed;
        }
    }

    /// <summary>Prints a simple table through the normal Windows print dialog (works with "Microsoft Print to PDF").</summary>
    static class Printer
    {
        public static void Print(Window owner, string title, string subtitle, string[] headers, double[] widths, bool[] right, List<string[]> rows)
        {
            var dlg = new PrintDialog();
            if (dlg.ShowDialog() != true) return;
            FlowDocument doc = Build(title, subtitle, headers, widths, right, rows, dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
            dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, title);
        }

        public static FlowDocument Build(string title, string subtitle, string[] headers, double[] widths, bool[] right,
                                         List<string[]> rows, double pageWidth, double pageHeight)
        {
            var doc = new FlowDocument();
            doc.FontFamily = new FontFamily("Segoe UI");
            doc.FontSize = 11;
            doc.Foreground = Brushes.Black;
            doc.Background = Brushes.White;
            doc.PageWidth = pageWidth;
            doc.PageHeight = pageHeight;
            doc.PagePadding = new Thickness(48, 44, 48, 44);
            doc.ColumnWidth = double.PositiveInfinity;

            var head = new Paragraph(new Run(title));
            head.FontSize = 20;
            head.FontWeight = FontWeights.SemiBold;
            head.Margin = new Thickness(0);
            doc.Blocks.Add(head);
            var sub = new Paragraph(new Run(subtitle));
            sub.Foreground = Brushes.DimGray;
            sub.Margin = new Thickness(0, 2, 0, 12);
            doc.Blocks.Add(sub);

            var table = new Table();
            table.CellSpacing = 0;
            for (int i = 0; i < headers.Length; i++)
                table.Columns.Add(new TableColumn { Width = new GridLength(widths[i], GridUnitType.Star) });
            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            var hr = new TableRow();
            hr.FontWeight = FontWeights.SemiBold;
            for (int i = 0; i < headers.Length; i++) hr.Cells.Add(Cell(headers[i], right[i], true));
            group.Rows.Add(hr);

            Brush stripe = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF6));
            for (int r = 0; r < rows.Count; r++)
            {
                var tr = new TableRow();
                if (r % 2 == 1) tr.Background = stripe;
                for (int i = 0; i < headers.Length; i++) tr.Cells.Add(Cell(rows[r][i], right[i], false));
                group.Rows.Add(tr);
            }
            doc.Blocks.Add(table);

            var foot = new Paragraph(new Run("Printed with ManaBox Companion. Prices from Scryfall."));
            foot.Foreground = Brushes.Gray;
            foot.FontSize = 9;
            foot.Margin = new Thickness(0, 12, 0, 0);
            doc.Blocks.Add(foot);
            return doc;
        }

        static TableCell Cell(string text, bool right, bool header)
        {
            var p = new Paragraph(new Run(text ?? ""));
            p.Margin = new Thickness(0);
            p.TextAlignment = right ? TextAlignment.Right : TextAlignment.Left;
            var c = new TableCell(p);
            c.Padding = new Thickness(4, 3, 4, 3);
            if (header)
            {
                c.BorderBrush = Brushes.Black;
                c.BorderThickness = new Thickness(0, 0, 0, 1);
            }
            return c;
        }
    }
}
