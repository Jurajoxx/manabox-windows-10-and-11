using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ManaBoxCompanion
{
    /// <summary>What we know about a card we want Scryfall data for.</summary>
    class Lookup
    {
        public string Id, Set, Number, Name;

        public string Key
        {
            get
            {
                if (!string.IsNullOrEmpty(Id)) return "id:" + Id.ToLowerInvariant();
                if (!string.IsNullOrEmpty(Set) && !string.IsNullOrEmpty(Number)) return "p:" + Set.ToLowerInvariant() + "|" + Number.ToLowerInvariant();
                if (!string.IsNullOrEmpty(Name)) return "n:" + Name.ToLowerInvariant();
                return null;
            }
        }
    }

    class FetchResult
    {
        public int Requested, NotFound;
        public string Error;
    }

    /// <summary>
    /// Talks to Scryfall's public API (https://scryfall.com/docs/api) and keeps a disk cache so the app
    /// works offline and stays well inside Scryfall's rate limits.
    /// </summary>
    class Scryfall
    {
        public static readonly Scryfall I = new Scryfall();
        public const string UserAgent = "ManaBoxCompanion/1.0";
        const long FreshSeconds = 20 * 3600;   // Scryfall updates prices once a day
        const int BatchSize = 75;              // maximum for /cards/collection

        readonly HttpClient http = new HttpClient();
        readonly Dictionary<string, CardInfo> byId = new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, CardInfo> byPrint = new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, CardInfo> byName = new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);

        Scryfall()
        {
            http.Timeout = TimeSpan.FromSeconds(40);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            foreach (CardInfo c in AppData.Load<CardCache>("cards.json").Cards) Index(c);
        }

        static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }

        void Index(CardInfo c)
        {
            if (c == null || string.IsNullOrEmpty(c.Id)) return;
            byId[c.Id] = c;
            if (!string.IsNullOrEmpty(c.Set) && !string.IsNullOrEmpty(c.Number)) byPrint[c.Set + "|" + c.Number] = c;
            if (!string.IsNullOrEmpty(c.Name))
            {
                byName[c.Name] = c;
                int split = c.Name.IndexOf(" // ", StringComparison.Ordinal);
                if (split > 0) byName[c.Name.Substring(0, split)] = c;
            }
        }

        public CardInfo Find(string id, string set, string number, string name)
        {
            CardInfo c;
            if (!string.IsNullOrEmpty(id)) return byId.TryGetValue(id, out c) ? c : null;
            if (!string.IsNullOrEmpty(set) && !string.IsNullOrEmpty(number))
                return byPrint.TryGetValue(set + "|" + number, out c) ? c : null;
            if (!string.IsNullOrEmpty(name)) return byName.TryGetValue(name, out c) ? c : null;
            return null;
        }

        public CardInfo Find(Lookup l) { return Find(l.Id, l.Set, l.Number, l.Name); }

        /// <summary>Downloads data for every card that is missing or whose prices are older than a day.</summary>
        public async Task<FetchResult> EnsureAsync(IEnumerable<Lookup> lookups, bool force, Action<int, int> progress, CancellationToken ct)
        {
            var result = new FetchResult();
            long now = Now();
            var need = new List<Lookup>();
            var seen = new HashSet<string>();
            foreach (Lookup l in lookups)
            {
                string key = l.Key;
                if (key == null || !seen.Add(key)) continue;
                CardInfo c = Find(l);
                if (c != null && !force && now - c.Fetched < FreshSeconds) continue;
                need.Add(l);
            }
            result.Requested = need.Count;
            if (need.Count == 0) return result;

            int done = 0;
            try
            {
                for (int i = 0; i < need.Count; i += BatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    List<Lookup> batch = need.GetRange(i, Math.Min(BatchSize, need.Count - i));
                    var ids = new List<object>();
                    foreach (Lookup l in batch)
                    {
                        var d = new Dictionary<string, object>();
                        if (!string.IsNullOrEmpty(l.Id)) d["id"] = l.Id;
                        else if (!string.IsNullOrEmpty(l.Set) && !string.IsNullOrEmpty(l.Number)) { d["set"] = l.Set.ToLowerInvariant(); d["collector_number"] = l.Number; }
                        else d["name"] = l.Name;
                        ids.Add(d);
                    }
                    var body = new Dictionary<string, object>();
                    body["identifiers"] = ids;
                    string json = await PostAsync("https://api.scryfall.com/cards/collection", AppData.NewJs().Serialize(body), ct);
                    var root = AppData.NewJs().DeserializeObject(json) as Dictionary<string, object>;
                    if (root != null)
                    {
                        object[] data = Get(root, "data") as object[];
                        if (data != null)
                            foreach (object o in data)
                            {
                                var cd = o as Dictionary<string, object>;
                                if (cd != null) Index(Parse(cd));
                            }
                        object[] missing = Get(root, "not_found") as object[];
                        if (missing != null) result.NotFound += missing.Length;
                    }
                    done += batch.Count;
                    if (progress != null) progress(done, need.Count);
                    await Task.Delay(110, ct);   // Scryfall asks for 50-100 ms between requests
                }
            }
            catch (OperationCanceledException) { result.Error = "cancelled"; }
            catch (Exception ex) { result.Error = ex.Message; }
            SaveCache();
            return result;
        }

        /// <summary>Looks a card up by (possibly misspelled) name. Returns null when Scryfall has no match.</summary>
        public async Task<CardInfo> NamedAsync(string name)
        {
            try
            {
                string url = "https://api.scryfall.com/cards/named?fuzzy=" + Uri.EscapeDataString(name);
                using (HttpResponseMessage r = await http.GetAsync(url))
                {
                    if (!r.IsSuccessStatusCode) return null;
                    var d = AppData.NewJs().DeserializeObject(await r.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                    if (d == null) return null;
                    CardInfo c = Parse(d);
                    Index(c);
                    SaveCache();
                    return c;
                }
            }
            catch { return null; }
        }

        async Task<string> PostAsync(string url, string body, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                using (HttpResponseMessage r = await http.PostAsync(url, content, ct))
                {
                    if ((int)r.StatusCode == 429 && attempt < 3)
                    {
                        await Task.Delay(2000 * (attempt + 1), ct);
                        continue;
                    }
                    if (!r.IsSuccessStatusCode)
                        throw new HttpRequestException("Scryfall answered " + (int)r.StatusCode + " " + r.ReasonPhrase);
                    return await r.Content.ReadAsStringAsync();
                }
            }
        }

        void SaveCache()
        {
            var cache = new CardCache();
            cache.Cards = new List<CardInfo>(byId.Values);
            Task.Run(delegate { AppData.Save("cards.json", cache); });
        }

        // ----- JSON helpers -----
        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }
        static string Str(Dictionary<string, object> d, string key) { return Get(d, key) as string; }
        static double? Dbl(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null) return null;
            string s = v as string;
            if (s != null) return Fmt.Num(s);
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return null; }
        }
        static string Join(object v)
        {
            object[] arr = v as object[];
            if (arr == null) return null;
            var sb = new StringBuilder();
            foreach (object o in arr) sb.Append(o);
            return sb.ToString();
        }

        static CardInfo Parse(Dictionary<string, object> d)
        {
            var c = new CardInfo();
            c.Id = Str(d, "id");
            c.Name = Str(d, "name");
            c.Set = Str(d, "set");
            c.SetName = Str(d, "set_name");
            c.Number = Str(d, "collector_number");
            c.Rarity = Str(d, "rarity");
            c.TypeLine = Str(d, "type_line");
            c.ManaCost = Str(d, "mana_cost");
            c.Cmc = Dbl(d, "cmc") ?? 0;
            c.Text = Str(d, "oracle_text");
            c.Uri = Str(d, "scryfall_uri");
            c.Colors = Join(Get(d, "colors"));
            c.Identity = Join(Get(d, "color_identity")) ?? "";
            c.Image = Str(Get(d, "image_uris") as Dictionary<string, object>, "normal");

            // Double-faced and split cards keep some fields on their faces.
            object[] faces = Get(d, "card_faces") as object[];
            if (faces != null && faces.Length > 0)
            {
                var texts = new List<string>();
                var costs = new List<string>();
                foreach (object o in faces)
                {
                    var f = o as Dictionary<string, object>;
                    if (f == null) continue;
                    string t = Str(f, "oracle_text");
                    if (!string.IsNullOrEmpty(t)) texts.Add(t);
                    string m = Str(f, "mana_cost");
                    if (!string.IsNullOrEmpty(m)) costs.Add(m);
                }
                var front = faces[0] as Dictionary<string, object>;
                if (string.IsNullOrEmpty(c.Image)) c.Image = Str(Get(front, "image_uris") as Dictionary<string, object>, "normal");
                if (string.IsNullOrEmpty(c.Text)) c.Text = string.Join("\n—\n", texts.ToArray());
                if (string.IsNullOrEmpty(c.ManaCost)) c.ManaCost = string.Join(" // ", costs.ToArray());
                if (c.Colors == null) c.Colors = Join(Get(front, "colors"));
            }
            if (c.Colors == null) c.Colors = "";

            var prices = Get(d, "prices") as Dictionary<string, object>;
            c.Usd = Dbl(prices, "usd");
            c.UsdFoil = Dbl(prices, "usd_foil");
            c.UsdEtched = Dbl(prices, "usd_etched");
            c.Eur = Dbl(prices, "eur");
            c.EurFoil = Dbl(prices, "eur_foil");
            c.Fetched = Now();
            return c;
        }
    }

    /// <summary>
    /// Card images: downloaded once into the disk cache, decoded off the UI thread, and kept in a small
    /// in-memory LRU so scrolling a large collection does not eat all the RAM. UI thread only.
    /// </summary>
    static class Images
    {
        const int ThumbWidth = 300;
        const int MemoryLimit = 260;
        const int MaxParallel = 6;
        const int MaxQueued = 240;

        class Job { public CardInfo Card; public Action<ImageSource> Done; }

        static readonly string Dir = Path.Combine(AppData.Dir, "images");
        static readonly HttpClient http = new HttpClient();
        static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, ImageSource>>> memory =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, ImageSource>>>();
        static readonly LinkedList<KeyValuePair<string, ImageSource>> order = new LinkedList<KeyValuePair<string, ImageSource>>();
        static readonly LinkedList<Job> queue = new LinkedList<Job>();
        static readonly Dictionary<string, Task<string>> downloads = new Dictionary<string, Task<string>>();
        static int running;

        static Images()
        {
            Directory.CreateDirectory(Dir);
            http.Timeout = TimeSpan.FromSeconds(40);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Scryfall.UserAgent);
        }

        public static bool Busy { get { return running > 0 || queue.Count > 0; } }

        public static ImageSource TryGet(string id)
        {
            LinkedListNode<KeyValuePair<string, ImageSource>> node;
            if (!memory.TryGetValue(id, out node)) return null;
            order.Remove(node);
            order.AddFirst(node);
            return node.Value.Value;
        }

        static void Remember(string id, ImageSource img)
        {
            if (memory.ContainsKey(id)) return;
            memory[id] = order.AddFirst(new KeyValuePair<string, ImageSource>(id, img));
            while (order.Count > MemoryLimit)
            {
                memory.Remove(order.Last.Value.Key);
                order.RemoveLast();
            }
        }

        /// <summary>Queues a thumbnail. Newest requests are served first, so what is on screen wins.</summary>
        public static void Request(CardInfo card, Action<ImageSource> done)
        {
            var job = new Job();
            job.Card = card;
            job.Done = done;
            queue.AddFirst(job);
            while (queue.Count > MaxQueued)
            {
                Job old = queue.Last.Value;
                queue.RemoveLast();
                old.Done(null);
            }
            Pump();
        }

        static void Pump()
        {
            while (running < MaxParallel && queue.Count > 0)
            {
                Job job = queue.First.Value;
                queue.RemoveFirst();
                running++;
                Run(job);
            }
        }

        static async void Run(Job job)
        {
            ImageSource img = null;
            try
            {
                img = TryGet(job.Card.Id);
                if (img == null)
                {
                    img = await LoadAsync(job.Card, ThumbWidth);
                    if (img != null) Remember(job.Card.Id, img);
                }
            }
            catch { }
            running--;
            try { job.Done(img); } catch { }
            Pump();
        }

        /// <summary>Loads a card image at the given decode width (0 = full size). Returns null on failure.</summary>
        public static async Task<ImageSource> LoadAsync(CardInfo card, int decodeWidth)
        {
            if (card == null || string.IsNullOrEmpty(card.Image) || string.IsNullOrEmpty(card.Id)) return null;
            try
            {
                string file = await EnsureFileAsync(card);
                if (file == null) return null;
                return await Task.Run(delegate
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
                        bi.UriSource = new Uri(file);
                        bi.EndInit();
                        bi.Freeze();
                        return (ImageSource)bi;
                    }
                    catch
                    {
                        try { File.Delete(file); } catch { }
                        return null;
                    }
                });
            }
            catch { return null; }
        }

        static Task<string> EnsureFileAsync(CardInfo card)
        {
            string file = Path.Combine(Dir, card.Id + ".jpg");
            if (File.Exists(file)) return Task.FromResult(file);
            Task<string> t;
            if (downloads.TryGetValue(card.Id, out t)) return t;
            t = DownloadAsync(card, file);
            downloads[card.Id] = t;
            return t;
        }

        static async Task<string> DownloadAsync(CardInfo card, string file)
        {
            try
            {
                byte[] bytes = await http.GetByteArrayAsync(card.Image);
                if (bytes == null || bytes.Length < 100) return null;
                string tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await Task.Run(delegate
                {
                    File.WriteAllBytes(tmp, bytes);
                    if (File.Exists(file)) File.Delete(tmp); else File.Move(tmp, file);
                });
                return file;
            }
            catch { return null; }
            finally { downloads.Remove(card.Id); }
        }
    }
}
