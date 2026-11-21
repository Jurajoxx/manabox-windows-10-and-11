using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace ManaBoxCompanion
{
    /// <summary>The subset of a Scryfall card object the app needs. Cached on disk.</summary>
    class CardInfo
    {
        public string Id, Name, Set, SetName, Number, Rarity, TypeLine, ManaCost, Colors, Identity, Text, Image, Uri;
        public double Cmc;
        public double? Usd, UsdFoil, UsdEtched, Eur, EurFoil;
        public long Fetched;

        /// <summary>Market price for one copy. Falls back to another finish when the exact one has no price.</summary>
        public double? Price(string currency, string finish)
        {
            if (currency == "EUR")
            {
                if (finish == "normal") return Eur ?? EurFoil;
                return EurFoil ?? Eur;
            }
            if (finish == "foil") return UsdFoil ?? UsdEtched ?? Usd;
            if (finish == "etched") return UsdEtched ?? UsdFoil ?? Usd;
            return Usd ?? UsdFoil ?? UsdEtched;
        }
    }

    class CardCache
    {
        public List<CardInfo> Cards = new List<CardInfo>();
    }

    /// <summary>A ManaBox CSV export. Every column is kept as-is so the file round-trips back into ManaBox.</summary>
    class CollectionDoc
    {
        public string Path;
        public char Delim = ',';
        public string NewLine = "\n";
        public bool Bom, TrailingNewline;
        public string[] Headers;
        public List<Entry> Entries = new List<Entry>();

        public int cName = -1, cSetCode = -1, cSetName = -1, cNumber = -1, cFoil = -1, cRarity = -1, cQty = -1,
                   cScryfall = -1, cPurchase = -1, cCondition = -1, cLanguage = -1, cCurrency = -1, cBinder = -1;

        public string Key { get { return System.IO.Path.GetFileNameWithoutExtension(Path); } }

        int Col(params string[] names)
        {
            for (int i = 0; i < Headers.Length; i++)
            {
                string h = (Headers[i] ?? "").Trim().ToLowerInvariant();
                foreach (string n in names) if (h == n) return i;
            }
            return -1;
        }

        static int Count(string s, char c)
        {
            int n = 0;
            foreach (char x in s) if (x == c) n++;
            return n;
        }

        public static CollectionDoc Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var d = new CollectionDoc();
            d.Path = path;
            d.Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int skip = d.Bom ? 3 : 0;
            string text = Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
            d.NewLine = text.Contains("\r\n") ? "\r\n" : "\n";
            d.TrailingNewline = text.EndsWith("\n");

            int eol = text.IndexOf('\n');
            string first = eol < 0 ? text : text.Substring(0, eol);
            int commas = Count(first, ','), semis = Count(first, ';'), tabs = Count(first, '\t');
            d.Delim = semis > commas && semis >= tabs ? ';' : (tabs > commas ? '\t' : ',');

            List<string[]> rows = Csv.Parse(text, d.Delim);
            if (rows.Count == 0) throw new InvalidDataException("The file is empty.");
            d.Headers = rows[0];
            d.cName = d.Col("name", "card name", "card");
            d.cSetCode = d.Col("set code", "set", "edition code");
            d.cSetName = d.Col("set name", "edition", "edition name");
            d.cNumber = d.Col("collector number", "card number", "number");
            d.cFoil = d.Col("foil", "finish", "printing");
            d.cRarity = d.Col("rarity");
            d.cQty = d.Col("quantity", "qty", "count", "amount");
            d.cScryfall = d.Col("scryfall id", "scryfall_id");
            d.cPurchase = d.Col("purchase price");
            d.cCondition = d.Col("condition");
            d.cLanguage = d.Col("language", "lang");
            d.cCurrency = d.Col("purchase price currency", "currency");
            d.cBinder = d.Col("binder name", "binder");
            if (d.cName < 0 && d.cScryfall < 0)
                throw new InvalidDataException("This doesn't look like a ManaBox CSV export: it has no “Name” or “Scryfall ID” column.");

            for (int i = 1; i < rows.Count; i++)
            {
                string[] row = rows[i];
                if (row.Length < d.Headers.Length)
                {
                    int old = row.Length;
                    Array.Resize(ref row, d.Headers.Length);
                    for (int k = old; k < row.Length; k++) row[k] = "";
                }
                d.Entries.Add(new Entry(d, row));
            }
            return d;
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.Append(Csv.Line(Headers, Delim));
            foreach (Entry e in Entries)
            {
                sb.Append(NewLine);
                sb.Append(Csv.Line(e.Row, Delim));
            }
            if (TrailingNewline) sb.Append(NewLine);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(Bom));
        }
    }

    /// <summary>One row of the collection, plus the Scryfall data matched to it.</summary>
    class Entry : INotifyPropertyChanged
    {
        public readonly CollectionDoc Doc;
        public readonly string[] Row;
        public CardInfo Card;
        bool selected, loadingImage;
        string blob;

        public event PropertyChangedEventHandler PropertyChanged;

        public Entry(CollectionDoc doc, string[] row) { Doc = doc; Row = row; }

        string G(int c) { return c >= 0 && c < Row.Length && Row[c] != null ? Row[c] : ""; }
        void S(int c, string v) { if (c >= 0 && c < Row.Length) Row[c] = v; }

        void Raise(string name)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>Tell the UI that everything about this entry may have changed.</summary>
        public void Refresh() { blob = null; Raise(string.Empty); }

        // ----- data from the CSV -----
        public string Name
        {
            get
            {
                string n = G(Doc.cName);
                return n.Length > 0 ? n : (Card != null ? Card.Name : "(unknown card)");
            }
        }
        public string ScryfallId { get { return G(Doc.cScryfall).Trim(); } }
        public string RawSet { get { return G(Doc.cSetCode).Trim(); } }
        public string RawNumber { get { return G(Doc.cNumber).Trim(); } }
        public string RawName { get { return G(Doc.cName).Trim(); } }

        public string SetCode
        {
            get
            {
                string s = RawSet;
                if (s.Length == 0 && Card != null) s = Card.Set ?? "";
                return s.ToUpperInvariant();
            }
        }
        public string SetName
        {
            get
            {
                string s = G(Doc.cSetName);
                return s.Length > 0 ? s : (Card != null ? Card.SetName ?? "" : "");
            }
        }
        public string Number
        {
            get
            {
                string s = RawNumber;
                return s.Length > 0 ? s : (Card != null ? Card.Number ?? "" : "");
            }
        }
        public string Finish
        {
            get
            {
                string f = G(Doc.cFoil).Trim().ToLowerInvariant();
                if (f == "foil" || f == "true" || f == "yes" || f == "1") return "foil";
                if (f == "etched") return "etched";
                return "normal";
            }
            set { S(Doc.cFoil, value); }
        }
        public string Rarity
        {
            get
            {
                string s = G(Doc.cRarity).Trim().ToLowerInvariant();
                return s.Length > 0 ? s : (Card != null ? Card.Rarity ?? "" : "");
            }
        }
        public int Quantity
        {
            get
            {
                if (Doc.cQty < 0) return 1;
                int q;
                return int.TryParse(G(Doc.cQty).Trim(), out q) ? q : 0;
            }
            set { S(Doc.cQty, value.ToString()); }
        }
        public string Condition { get { return G(Doc.cCondition).Trim(); } set { S(Doc.cCondition, value); } }
        public string Language { get { return G(Doc.cLanguage).Trim(); } set { S(Doc.cLanguage, value); } }
        public string Binder { get { return G(Doc.cBinder).Trim(); } set { S(Doc.cBinder, value); } }
        public string PurchaseCurrency { get { return G(Doc.cCurrency).Trim().ToUpperInvariant(); } }
        public double? PurchasePrice
        {
            get { return Fmt.Num(G(Doc.cPurchase)); }
            set { S(Doc.cPurchase, value == null ? "" : Fmt.NumText(value.Value)); }
        }

        // ----- data from Scryfall -----
        public string TypeLine { get { return Card != null ? Card.TypeLine ?? "" : ""; } }
        public string ManaCost { get { return Card != null ? Card.ManaCost ?? "" : ""; } }
        public string OracleText { get { return Card != null ? Card.Text ?? "" : ""; } }
        public double Cmc { get { return Card != null ? Card.Cmc : 0; } }
        public string Identity { get { return Card != null ? Card.Identity ?? "" : ""; } }

        public double? UnitPrice(string currency) { return Card == null ? null : Card.Price(currency, Finish); }
        public double Value(string currency) { return (UnitPrice(currency) ?? 0) * Quantity; }
        public double Price { get { return UnitPrice(Fmt.Currency) ?? 0; } }
        public double Total { get { return Value(Fmt.Currency); } }

        // ----- display strings -----
        public string QtyLabel { get { return "×" + Quantity; } }
        public bool IsFoil { get { return Finish != "normal"; } }
        public string FoilLabel { get { return Finish.ToUpperInvariant(); } }
        public string FinishLabel { get { return Finish == "normal" ? "" : Fmt.Title(Finish); } }
        public string RarityLabel { get { return Fmt.Title(Rarity); } }
        public string ConditionLabel { get { return Fmt.Title(Condition); } }
        public string PriceText { get { return Fmt.Money(UnitPrice(Fmt.Currency)); } }
        public string TotalText { get { return UnitPrice(Fmt.Currency) == null ? "—" : Fmt.Money(Total); } }
        public string SetLine { get { return SetCode + (Number.Length > 0 ? " · #" + Number : ""); } }

        public string SetFull
        {
            get
            {
                string s = SetName;
                if (SetCode.Length > 0) s += (s.Length > 0 ? " (" + SetCode + ")" : SetCode);
                if (Number.Length > 0) s += " · #" + Number;
                if (Rarity.Length > 0) s += " · " + RarityLabel;
                return s;
            }
        }
        public string OwnedText
        {
            get
            {
                var parts = new List<string>();
                parts.Add("You own " + Quantity);
                if (IsFoil) parts.Add(FinishLabel);
                if (Condition.Length > 0) parts.Add(ConditionLabel);
                if (Language.Length > 0) parts.Add(Language);
                if (Binder.Length > 0) parts.Add("in “" + Binder + "”");
                return string.Join(" · ", parts.ToArray());
            }
        }
        public string ValueText
        {
            get
            {
                if (UnitPrice(Fmt.Currency) == null) return Card == null ? "No Scryfall data yet" : "No " + Fmt.Currency + " price available";
                return Fmt.Money(Price) + " each · " + Fmt.Money(Total) + " total";
            }
        }
        public string PurchaseText
        {
            get
            {
                double? p = PurchasePrice;
                if (p == null || p.Value <= 0) return "";
                string cur = PurchaseCurrency.Length > 0 ? PurchaseCurrency : Fmt.Currency;
                return "Bought for " + Fmt.Money(p, cur) + " each";
            }
        }
        public string MarketText
        {
            get
            {
                if (Card == null) return "";
                var parts = new List<string>();
                if (Card.Usd != null) parts.Add("$" + Fmt.NumText(Card.Usd.Value));
                if (Card.UsdFoil != null) parts.Add("foil $" + Fmt.NumText(Card.UsdFoil.Value));
                if (Card.UsdEtched != null) parts.Add("etched $" + Fmt.NumText(Card.UsdEtched.Value));
                if (Card.Eur != null) parts.Add("€" + Fmt.NumText(Card.Eur.Value));
                if (Card.EurFoil != null) parts.Add("foil €" + Fmt.NumText(Card.EurFoil.Value));
                return parts.Count == 0 ? "" : "Market: " + string.Join(" · ", parts.ToArray());
            }
        }

        public bool IsSelected
        {
            get { return selected; }
            set { if (selected != value) { selected = value; Raise("IsSelected"); } }
        }

        /// <summary>Lower-case text the search box matches against.</summary>
        public string Blob
        {
            get
            {
                if (blob == null)
                    blob = (Name + "\n" + TypeLine + "\n" + OracleText + "\n" + SetCode + "\n" + SetName).ToLowerInvariant();
                return blob;
            }
        }

        /// <summary>Thumbnail for the gallery. Loads lazily; the binding is re-read once it arrives.</summary>
        public ImageSource Image
        {
            get
            {
                if (Card == null || string.IsNullOrEmpty(Card.Image)) return null;
                ImageSource img = Images.TryGet(Card.Id);
                if (img != null) return img;
                if (!loadingImage)
                {
                    loadingImage = true;
                    Images.Request(Card, delegate(ImageSource result)
                    {
                        loadingImage = false;
                        if (result != null) Raise("Image");
                    });
                }
                return null;
            }
        }
    }

    /// <summary>A row of tiles in the gallery (rows are virtualised, tiles inside a row are not).</summary>
    class CardRow
    {
        public List<Entry> Items { get; set; }
    }

    // ----- decks -----
    class DeckCardData
    {
        public string Id, Name, Set, Number, Board;
        public int Qty;
    }

    class DeckData
    {
        public string Name;
        public List<DeckCardData> Cards = new List<DeckCardData>();
        public override string ToString() { return Name; }
    }

    class DeckFile
    {
        public List<DeckData> Decks = new List<DeckData>();
    }

    /// <summary>Display wrapper for one line of a deck.</summary>
    class DeckCard
    {
        public DeckCardData D;
        public CardInfo Card;
        public int OwnedQty;

        public bool IsSide { get { return D.Board == "side"; } }
        public string Board { get { return IsSide ? "Sideboard" : "Main"; } }
        public int Qty { get { return D.Qty; } }
        public string Name { get { return Card != null ? Card.Name : D.Name; } }
        public string ManaCost { get { return Card != null ? Card.ManaCost ?? "" : ""; } }
        public string TypeLine { get { return Card != null ? Card.TypeLine ?? "" : ""; } }
        public double Cmc { get { return Card != null ? Card.Cmc : 0; } }
        public bool IsLand { get { return TypeLine.IndexOf("Land", StringComparison.OrdinalIgnoreCase) >= 0; } }
        public int Missing { get { return Math.Max(0, D.Qty - OwnedQty); } }
        public string OwnedText { get { return Missing == 0 ? OwnedQty.ToString() : OwnedQty + "  (need " + Missing + ")"; } }
        public double? Price { get { return Card == null ? null : Card.Price(Fmt.Currency, "normal"); } }
        public double Total { get { return (Price ?? 0) * D.Qty; } }
        public string PriceText { get { return Fmt.Money(Price); } }
        public string TotalText { get { return Price == null ? "—" : Fmt.Money(Total); } }
    }

    // ----- value history -----
    class Snapshot
    {
        public string Key, Date;
        public double Usd, Eur;
        public int Cards, Unique;
    }

    class HistoryFile
    {
        public List<Snapshot> Items = new List<Snapshot>();
    }
}
