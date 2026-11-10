// More of what a card is related to, page by page, so a row never has to end.
//
// Each source is a Pager: call Next() for the next page until Done. A row
// asks its pagers in turn as you scroll towards its end. All of these are
// plain APIs without the Wikidata query service's rate limit:
//
//   statements    the item's own statements (spouse, works, awards, followed by...),
//                 via the Wikidata entity API
//   links         the rest of the article's links (the lead is "mentions")
//   links here    articles that link to it
//   search        Wikipedia full-text search for it
//   Commons       pictures and videos of it
//   Archive       Internet Archive films, recordings, pictures
//   Europeana     European libraries, archives and museums
//   Openverse     openly licensed pictures
//
// Every answer goes through WebCache, so a row seen once is free next time.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace XrProbe
{
    public class Pager
    {
        public readonly string name;
        readonly Func<int, CancellationToken, Task<(List<RelatedItem> items, bool more)>> _fetch;
        int _page, _pagesLeft;

        public Pager(string name, int maxPages, Func<int, CancellationToken, Task<(List<RelatedItem>, bool)>> fetch)
        {
            this.name = name;
            _pagesLeft = maxPages;
            _fetch = fetch;
        }

        public bool Done => _pagesLeft <= 0;

        public async Task<List<RelatedItem>> Next(CancellationToken ct)
        {
            if (Done) return new List<RelatedItem>();
            _pagesLeft--;
            List<RelatedItem> items; bool more;
            try { (items, more) = await _fetch(_page++, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[sources] {name}: {e.Message}");
                (items, more) = (null, false);
            }
            if (!more) _pagesLeft = 0;
            return items ?? new List<RelatedItem>();
        }
    }

    public static class MoreSources
    {
        const string WikiApi = "https://en.wikipedia.org/w/api.php";
        const string DataApi = "https://www.wikidata.org/w/api.php";

        public static string EuropeanaKey = "api2demo";

        static readonly List<RelatedItem> None = new List<RelatedItem>();

        // ---- what to search for ---------------------------------------------

        /// <summary>A search phrase for a card: its title without file
        /// extensions, "File:", qualifiers in brackets; at most six words.</summary>
        public static string TermOf(Card c)
        {
            string t = c?.title ?? "";
            t = Regex.Replace(t, @"^File:", "");
            t = Regex.Replace(t, @"\.(jpe?g|png|tiff?|gif|svg|webm|ogv|ogg|mp4|pdf|djvu)$", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\s*\([^)]*\)", "");
            t = Regex.Replace(t, @"[_\-–—,;:|/\\""\[\]{}]+", " ");
            var words = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(6);
            return string.Join(" ", words).Trim();
        }

        static string E(string s) => UnityWebRequest.EscapeURL(s ?? "");

        // ---- Wikidata: the item's own statements ------------------------------

        static readonly Dictionary<string, string> Outbound = new Dictionary<string, string>
        {
            ["P26"] = "spouse", ["P451"] = "partner", ["P40"] = "child", ["P22"] = "father", ["P25"] = "mother",
            ["P3373"] = "sibling", ["P1038"] = "relative", ["P800"] = "notable work", ["P166"] = "award",
            ["P1411"] = "nominated for", ["P463"] = "member of", ["P1344"] = "took part in", ["P793"] = "event",
            ["P737"] = "influenced by", ["P69"] = "studied at", ["P108"] = "worked for", ["P19"] = "born in",
            ["P20"] = "died in", ["P119"] = "buried at", ["P551"] = "lived in", ["P264"] = "label",
            ["P361"] = "part of", ["P527"] = "has part", ["P138"] = "named after", ["P155"] = "follows",
            ["P156"] = "followed by", ["P1365"] = "replaces", ["P1366"] = "replaced by", ["P828"] = "cause",
            ["P1542"] = "effect", ["P112"] = "founded by", ["P170"] = "made by", ["P50"] = "author",
            ["P57"] = "director", ["P161"] = "cast", ["P175"] = "performer", ["P86"] = "composer",
            ["P162"] = "producer", ["P710"] = "participant", ["P276"] = "location", ["P131"] = "located in",
            ["P127"] = "owned by", ["P1830"] = "owns", ["P749"] = "parent organisation", ["P355"] = "subsidiary",
            ["P488"] = "chair", ["P169"] = "chief executive", ["P6"] = "head of government", ["P35"] = "head of state",
            ["P36"] = "capital", ["P1376"] = "capital of", ["P190"] = "twin town", ["P47"] = "borders",
            ["P1441"] = "appears in", ["P674"] = "characters", ["P840"] = "set in", ["P921"] = "about",
            ["P180"] = "depicts", ["P1066"] = "student of", ["P802"] = "student", ["P1327"] = "partner in work",
        };

        static readonly string[] OwnDates = { "P569", "P571", "P577", "P580", "P585", "P1619", "P570", "P576" };

        public static Pager Statements(string qid, string anchorLabel) =>
            new Pager("statements", 1, async (page, ct) =>
            {
                if (string.IsNullOrEmpty(qid) || !qid.StartsWith("Q")) return (None, false);
                var j = await WebCache.GetJson($"{DataApi}?action=wbgetentities&format=json&props=claims&ids={qid}", ct, 20, "statements");
                var claims = j?["entities"]?[qid]?["claims"] as JObject;
                if (claims == null) return (None, false);

                // target item -> (phrase, year from the statement's qualifiers)
                var targets = new Dictionary<string, (string phrase, int? year)>();
                foreach (var kv in Outbound)
                {
                    foreach (var st in (claims[kv.Key] as JArray) ?? new JArray())
                    {
                        string id = (string)st["mainsnak"]?["datavalue"]?["value"]?["id"];
                        if (string.IsNullOrEmpty(id) || id == qid || targets.ContainsKey(id)) continue;
                        int? y = null;
                        foreach (var q in new[] { "P585", "P580", "P582" })
                        {
                            y = Year((string)st["qualifiers"]?[q]?.FirstOrDefault()?["datavalue"]?["value"]?["time"]);
                            if (y.HasValue) break;
                        }
                        targets[id] = (kv.Value, y);
                    }
                }
                if (targets.Count == 0) return (None, false);

                var ids = targets.Keys.Take(50).ToList();
                var d = await WebCache.GetJson($"{DataApi}?action=wbgetentities&format=json&props=labels|descriptions|claims|sitelinks"
                                             + $"&languages=en&sitefilter=enwiki&ids={string.Join("|", ids)}", ct, 25, "statements");
                var ents = d?["entities"] as JObject;
                if (ents == null) return (None, false);

                var items = new List<RelatedItem>();
                foreach (var id in ids)
                {
                    var e = ents[id];
                    if (e == null || e["missing"] != null) continue;
                    var (phrase, qy) = targets[id];
                    var c = e["claims"] as JObject;
                    int? own = null;
                    foreach (var p in OwnDates)
                    {
                        own = Year((string)c?[p]?.FirstOrDefault()?["mainsnak"]?["datavalue"]?["value"]?["time"]);
                        if (own.HasValue) break;
                    }
                    int? y = qy ?? own;
                    string file = (string)c?["P18"]?.FirstOrDefault()?["mainsnak"]?["datavalue"]?["value"];
                    string title = (string)e["sitelinks"]?["enwiki"]?["title"];
                    bool human = ((c?["P31"] as JArray) ?? new JArray())
                        .Any(s => (string)s["mainsnak"]?["datavalue"]?["value"]?["id"] == "Q5");
                    var it = new RelatedItem
                    {
                        qid = id,
                        label = (string)e["labels"]?["en"]?["value"] ?? title ?? id,
                        description = (string)e["descriptions"]?["en"]?["value"] ?? "",
                        human = human,
                        image = file != null ? "https://commons.wikimedia.org/wiki/Special:FilePath/" + Uri.EscapeDataString(file.Replace(' ', '_')) : null,
                        article = title != null ? "https://en.wikipedia.org/wiki/" + E(title.Replace(' ', '_')) : null,
                        relation = $"{anchorLabel}: {phrase}",
                        prop = "statement",
                        facet = human ? "person" : phrase,
                    };
                    if (y.HasValue) { it.yearMin = it.yearMax = it.dateYear = y.Value; it.datePhrase = ""; }
                    items.Add(it);
                }
                // Dated first, in date order; then the rest.
                items = items.OrderBy(i => i.yearMin == int.MaxValue ? 1 : 0).ThenBy(i => i.dateYear).ToList();
                return (items, false);
            });

        // ---- Wikipedia: links, links here, search -------------------------------

        static readonly string PageProps = "&prop=pageprops|pageimages|description&ppprop=wikibase_item"
                               + "&piprop=thumbnail&pithumbsize=" + WebGate.CardWidth + "&pilicense=any&pilimit=50";

        public static Pager ArticleLinks(string article, int pages = 4)
        {
            string cont = null;
            return new Pager("links", pages, async (page, ct) =>
            {
                if (string.IsNullOrEmpty(article)) return (None, false);
                string url = $"{WikiApi}?action=query&format=json&redirects=1&generator=links&gplnamespace=0&gpllimit=40"
                           + PageProps + $"&titles={E(article)}" + (cont != null ? "&gplcontinue=" + E(cont) : "");
                var j = await WebCache.GetJson(url, ct, 20, "links");
                cont = (string)j?["continue"]?["gplcontinue"];
                var items = FromPages(j, "link", $"linked from {article}", "linked", article);
                return (items, cont != null);
            });
        }

        public static Pager Backlinks(string article, int pages = 2)
        {
            string cont = null;
            return new Pager("links here", pages, async (page, ct) =>
            {
                if (string.IsNullOrEmpty(article)) return (None, false);
                string url = $"{WikiApi}?action=query&format=json&redirects=1&generator=linkshere&glhnamespace=0"
                           + "&glhlimit=40&glhshow=!redirect" + PageProps + $"&titles={E(article)}"
                           + (cont != null ? "&glhcontinue=" + E(cont) : "");
                var j = await WebCache.GetJson(url, ct, 20, "links here");
                cont = (string)j?["continue"]?["glhcontinue"];
                var items = FromPages(j, "backlink", $"links to {article}", "links here", article);
                return (items, cont != null);
            });
        }

        public static Pager WikiSearch(string term, string exclude, int pages = 3)
        {
            const int n = 15;
            return new Pager("search", pages, async (page, ct) =>
            {
                if (string.IsNullOrEmpty(term)) return (None, false);
                string url = $"{WikiApi}?action=query&format=json&generator=search&gsrnamespace=0&gsrlimit={n}"
                           + $"&gsroffset={page * n}&gsrsearch={E(term)}" + PageProps;
                var j = await WebCache.GetJson(url, ct, 20, "search");
                var items = FromPages(j, "search", $"Wikipedia search: {term}", "search", exclude);
                return (items, j?["continue"] != null);
            });
        }

        static readonly Regex DateTitle = new Regex(
            @"^((January|February|March|April|May|June|July|August|September|October|November|December)( \d{1,2})?|\d{1,4}s?( in .*| BC)?|\d{1,2}(st|nd|rd|th) century.*)$");

        static List<RelatedItem> FromPages(JToken j, string prop, string relation, string facet, string exclude)
        {
            var pages = (j?["query"]?["pages"] as JObject)?.Properties().Select(p => p.Value) ?? Enumerable.Empty<JToken>();
            var items = new List<RelatedItem>();
            foreach (var pg in pages.OrderBy(p => (int?)p["index"] ?? 0).ThenBy(p => (string)p["title"]))
            {
                string t = (string)pg["title"];
                if (pg["missing"] != null || string.IsNullOrEmpty(t) || t == exclude) continue;
                if (DateTitle.IsMatch(t) || t.StartsWith("List of") || t.StartsWith("Lists of")
                    || t.Contains("(disambiguation)") || t.StartsWith("Timeline of")) continue;
                var th = pg["thumbnail"];
                items.Add(new RelatedItem
                {
                    qid = (string)pg["pageprops"]?["wikibase_item"] ?? "wp:" + t,
                    label = t,
                    description = (string)pg["description"] ?? "",
                    article = "https://en.wikipedia.org/wiki/" + E(t.Replace(' ', '_')),
                    image = (string)th?["source"],
                    imageW = (int?)th?["width"] ?? 0,
                    imageH = (int?)th?["height"] ?? 0,
                    imageFromWikipedia = th != null,
                    imageTried = true,
                    relation = relation,
                    prop = prop,
                    facet = facet,
                });
            }
            // Pictures first: a row of text-only cards reads like a list.
            return items.OrderBy(i => i.image == null ? 1 : 0).ToList();
        }

        // ---- Wikimedia Commons: pictures and videos ------------------------------

        public static Pager Commons(string term, bool video, int pages = 0)
        {
            const int n = 12;
            return new Pager(video ? "Commons video" : "Commons", pages > 0 ? pages : video ? 2 : 3, async (page, ct) =>
            {
                if (string.IsNullOrEmpty(term)) return (None, false);
                string q = (video ? "filetype:video " : "filetype:bitmap ") + term;
                string url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6"
                           + $"&gsrlimit={n}&gsroffset={page * n}&gsrsearch={E(q)}"
                           + "&prop=imageinfo&iiprop=url|size|mediatype|extmetadata&iiurlwidth=" + WebGate.CardWidth + ""
                           + "&iiextmetadatafilter=DateTimeOriginal|ObjectName|ImageDescription|Artist|LicenseShortName";
                var j = await WebCache.GetJson(url, ct, 20, "commons");
                var pages = (j?["query"]?["pages"] as JObject)?.Properties().Select(p => p.Value) ?? Enumerable.Empty<JToken>();
                var items = new List<RelatedItem>();
                foreach (var pg in pages.OrderBy(p => (int?)p["index"] ?? 0))
                {
                    var info = pg["imageinfo"]?.FirstOrDefault();
                    if (info == null) continue;
                    var meta = info["extmetadata"];
                    string M(string k) => Strip((string)meta?[k]?["value"]);
                    string file = ((string)pg["title"] ?? "").Replace("File:", "");
                    string name = M("ObjectName");
                    if (string.IsNullOrEmpty(name)) name = TermOf(new Card { title = file });
                    var date = DateParser.Parse(M("DateTimeOriginal"));
                    if (!date.Begin.HasValue) date = DateParser.Parse(file);
                    var it = new RelatedItem
                    {
                        qid = "commons:" + file,
                        label = name,
                        description = Clip(M("ImageDescription"), 300),
                        image = (string)info["thumburl"],
                        imageW = (int?)info["thumbwidth"] ?? 0,
                        imageH = (int?)info["thumbheight"] ?? 0,
                        imageFromWikipedia = true, imageTried = true,
                        link = (string)info["descriptionurl"],
                        mediaUrl = (string)info["url"],
                        mediaKind = video ? "video" : "image",
                        source = "commons",
                        credit = Clip(M("Artist"), 80),
                        license = M("LicenseShortName"),
                        relation = $"Commons: {term}",
                        prop = "commons",
                        facet = (video ? "video" : "picture") + " · Commons",
                    };
                    SetDate(it, date.Begin, date.End);
                    items.Add(it);
                }
                return (items, j?["continue"] != null);
            });
        }

        // ---- Internet Archive: films, recordings, pictures -----------------------

        public static Pager Archive(string term, int year, int pages = 3)
        {
            return new Pager("Internet Archive", pages, async (page, ct) =>
            {
                string clean = Regex.Replace(term ?? "", @"[^\p{L}\p{N} ]", " ").Trim();
                if (clean.Length == 0) return (None, false);
                // First page close to the card's year, then without a date.
                string q = $"title:({clean}) AND mediatype:(movies OR audio OR image)"
                         + (page == 0 ? $" AND year:[{year - 15} TO {year + 15}]" : "");
                int p = page == 0 ? 1 : page;
                string url = "https://archive.org/advancedsearch.php?q=" + E(q)
                           + "&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=year&fl%5B%5D=mediatype"
                           + "&fl%5B%5D=description&fl%5B%5D=creator&fl%5B%5D=licenseurl"
                           + $"&sort%5B%5D=downloads+desc&rows=12&page={p}&output=json";
                var j = await WebCache.GetJson(url, ct, 25, "archive");
                var docs = j?["response"]?["docs"] as JArray;
                var items = new List<RelatedItem>();
                foreach (var d in docs ?? new JArray())
                {
                    string id = (string)d["identifier"];
                    if (string.IsNullOrEmpty(id)) continue;
                    string media = (string)d["mediatype"];
                    string kind = media == "movies" ? "video" : media == "audio" ? "recording" : "picture";
                    var it = new RelatedItem
                    {
                        qid = "ia:" + id,
                        label = Clip(Text(d["title"]), 90),
                        description = Clip(Strip(Text(d["description"])), 300),
                        image = "https://archive.org/services/img/" + id,
                        imageFromWikipedia = true, imageTried = true,
                        link = "https://archive.org/details/" + id,
                        mediaUrl = "https://archive.org/details/" + id,
                        mediaKind = media == "movies" ? "video" : media == "audio" ? "audio" : "image",
                        source = "archive",
                        credit = Clip(Text(d["creator"]), 80),
                        license = (string)d["licenseurl"] ?? "see source",
                        relation = $"Internet Archive: {clean}",
                        prop = "archive",
                        facet = kind + " · Archive",
                    };
                    int? y = int.TryParse(Text(d["year"]), out int yy) ? yy : (int?)null;
                    SetDate(it, y, y);
                    items.Add(it);
                }
                int found = (int?)j?["response"]?["numFound"] ?? 0;
                // Page 0 is the dated query: always go on to the undated one.
                return (items, page == 0 || found > p * 12);
            });
        }

        // ---- Europeana -------------------------------------------------------------

        public static Pager Europeana(string term, int year, int pages = 3)
        {
            const int n = 12;
            return new Pager("Europeana", pages, async (page, ct) =>
            {
                if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(EuropeanaKey)) return (None, false);
                int start = 1 + (page == 0 ? 0 : (page - 1) * n);
                string url = "https://api.europeana.eu/record/v2/search.json?wskey=" + E(EuropeanaKey)
                           + $"&query={E("\"" + term + "\"")}&rows={n}&start={start}&media=true&thumbnail=true"
                           + (page == 0 ? "&qf=" + E($"YEAR:[{year - 20} TO {year + 20}]") : "");
                var j = await WebCache.GetJson(url, ct, 25, "europeana");
                var items = new List<RelatedItem>();
                foreach (var d in (j?["items"] as JArray) ?? new JArray())
                {
                    string id = (string)d["id"];
                    string type = (string)d["type"];
                    string kind = type == "VIDEO" ? "video" : type == "SOUND" ? "recording" : type == "TEXT" ? "document" : "picture";
                    var it = new RelatedItem
                    {
                        qid = "eu:" + id,
                        label = Clip(Text(d["title"]), 90),
                        description = Clip(Strip(Text(d["dcDescription"])), 300),
                        image = Text(d["edmPreview"]),
                        imageFromWikipedia = true, imageTried = true,
                        link = (string)d["guid"],
                        mediaKind = type == "VIDEO" ? "video" : type == "SOUND" ? "audio" : "image",
                        source = "europeana",
                        credit = Text(d["dataProvider"]),
                        license = Text(d["rights"]),
                        relation = $"Europeana: {term}",
                        prop = "europeana",
                        facet = kind + " · Europeana",
                    };
                    int? y = int.TryParse(Text(d["year"]), out int yy) ? yy : (int?)null;
                    SetDate(it, y, y);
                    if (!string.IsNullOrEmpty(it.label)) items.Add(it);
                }
                int total = (int?)j?["totalResults"] ?? 0;
                return (items, page == 0 || total > start + n);
            });
        }

        // ---- Openverse ----------------------------------------------------------------

        public static Pager Openverse(string term, int pages = 2)
        {
            return new Pager("Openverse", pages, async (page, ct) =>
            {
                if (string.IsNullOrEmpty(term)) return (None, false);
                string url = $"https://api.openverse.org/v1/images/?q={E(term)}&page_size=12&page={page + 1}";
                var j = await WebCache.GetJson(url, ct, 25, "openverse");
                var items = new List<RelatedItem>();
                foreach (var d in (j?["results"] as JArray) ?? new JArray())
                {
                    string lic = $"CC {((string)d["license"] ?? "").ToUpperInvariant()} {(string)d["license_version"]}".Trim();
                    items.Add(new RelatedItem
                    {
                        qid = "ov:" + (string)d["id"],
                        label = Clip((string)d["title"] ?? term, 90),
                        description = "",
                        image = (string)d["thumbnail"] ?? (string)d["url"],
                        imageW = (int?)d["width"] ?? 0,
                        imageH = (int?)d["height"] ?? 0,
                        imageFromWikipedia = true, imageTried = true,
                        link = (string)d["foreign_landing_url"],
                        mediaUrl = (string)d["url"],
                        mediaKind = "image",
                        source = "openverse",
                        credit = (string)d["creator"],
                        license = lic,
                        relation = $"Openverse: {term}",
                        prop = "openverse",
                        facet = "picture · " + ((string)d["provider"] ?? "Openverse"),
                    });
                }
                return (items, (int?)j?["page_count"] > page + 1);
            });
        }

        // ---- helpers ------------------------------------------------------------------

        static void SetDate(RelatedItem it, int? b, int? e)
        {
            if (!b.HasValue) return;
            it.yearMin = b.Value;
            it.yearMax = e ?? b.Value;
            it.dateYear = b.Value;
            it.datePhrase = "";
        }

        // "+1967-05-01T00:00:00Z"
        static int? Year(string t)
        {
            if (string.IsNullOrEmpty(t)) return null;
            var m = Regex.Match(t, @"^([+-])(\d+)-");
            if (!m.Success || !int.TryParse(m.Groups[2].Value, out int y)) return null;
            return m.Groups[1].Value == "-" ? -y : y;
        }

        // A field that may be a string or an array of strings.
        static string Text(JToken t)
        {
            if (t == null) return "";
            if (t is JArray a) return a.Count > 0 ? (string)a[0] ?? "" : "";
            return (string)t ?? "";
        }

        static string Strip(string s) =>
            string.IsNullOrEmpty(s) ? "" : Regex.Replace(Regex.Replace(s, "<[^>]+>", " "), @"\s+", " ").Trim();

        static string Clip(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n - 1).TrimEnd() + "…";
    }
}
