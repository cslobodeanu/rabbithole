// The vertical axis: what else, around the same time, is linked to the
// subject. People born / died / working there, buildings and works located
// there, and the subject's own dated statements (for a person: works,
// events, relatives...).
//
// Wikidata answers with typed links and real dates, so no guessing from
// prose. One round of queries per (anchor, year range) - the whole search
// window plus a margin - is cached; each column then just filters by year,
// so moving along the timeline costs nothing.
//
// Query cost matters: a generic "anything linked to Strasbourg" query took
// ~58 s; restricting to meaningful link properties brings it to ~5 s.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    public class RelatedItem
    {
        public string qid, label, description, image, article;
        public bool human;
        public int yearMin = int.MaxValue, yearMax = int.MinValue;
        public string datePhrase;      // "born", "built", "published"...
        public int dateYear;
        public string relation;        // "born in Strasbourg"
        public string prop;            // the link property, e.g. "P175"

        // Set when the picture came from the item's Wikipedia article rather
        // than Wikidata (album covers, posters: often non-free, hosted on
        // en.wikipedia, not Commons).
        public bool imageFromWikipedia, imageTried;
        public int imageW, imageH;

        // Items from other sources (Commons, Internet Archive, Europeana...):
        // where they come from and what they are. Null for Wikidata items.
        public string source, credit, license, link;
        public string mediaKind, mediaUrl;     // "video" / "audio" / "image", and what to play
        public string facet;                   // the card's label, when set ("video · Archive")

        public bool Near(int year, int half) =>
            yearMin != int.MaxValue && yearMin - half <= year && year <= yearMax + half;
    }

    public class WikidataRelated
    {
        const string Sparql = "https://query.wikidata.org/sparql";
        const string Api = "https://www.wikidata.org/w/api.php";
        const string WikiApi = "https://en.wikipedia.org/w/api.php";

        // Items that point at the anchor through one of these. Restricting the
        // properties is what keeps the query fast.
        static readonly Dictionary<string, string> Inbound = new Dictionary<string, string>
        {
            ["P19"] = "born in", ["P20"] = "died in", ["P131"] = "located in", ["P276"] = "at",
            ["P937"] = "worked in", ["P551"] = "lived in", ["P69"] = "studied at", ["P108"] = "worked for",
            ["P1071"] = "made in", ["P840"] = "set in", ["P159"] = "based in", ["P740"] = "formed in",
            ["P1427"] = "started in", ["P1444"] = "ended in", ["P180"] = "depicts", ["P921"] = "about",
            ["P50"] = "by", ["P170"] = "by", ["P710"] = "with", ["P1441"] = "appears in",
            ["P138"] = "named after", ["P361"] = "part of", ["P1066"] = "student of", ["P802"] = "teacher of",
            ["P737"] = "influenced by", ["P26"] = "spouse of", ["P40"] = "child of", ["P22"] = "child of",
            ["P25"] = "child of", ["P3373"] = "sibling of",
            ["P175"] = "performed by", ["P161"] = "featuring", ["P86"] = "music by", ["P162"] = "produced by",
        };

        // What a non-person item most likely is, from how it links to the
        // anchor. Only used for the card's label; unknown links show the year alone.
        static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>
        {
            ["P175"] = "recording", ["P161"] = "film", ["P86"] = "composition", ["P162"] = "production",
            ["P50"] = "work", ["P170"] = "work", ["P131"] = "place", ["P276"] = "place", ["P159"] = "place",
            ["P180"] = "image", ["P921"] = "work about it", ["P840"] = "story", ["P1441"] = "work",
            ["P710"] = "event", ["P1071"] = "work", ["P740"] = "group", ["P108"] = "organisation",
            ["P69"] = "school",
        };

        static readonly Dictionary<string, string> DateProps = new Dictionary<string, string>
        {
            ["P569"] = "born", ["P570"] = "died", ["P571"] = "founded", ["P585"] = "",
            ["P580"] = "from", ["P582"] = "until", ["P577"] = "published", ["P1619"] = "opened",
            ["P576"] = "dissolved",
        };

        readonly Dictionary<string, Task<(string qid, string label)>> _anchors = new Dictionary<string, Task<(string, string)>>();
        readonly Dictionary<string, Task<List<RelatedItem>>> _windows = new Dictionary<string, Task<List<RelatedItem>>>();

        public int perQuery = 400;

        // The link properties actually queried - measured fast for a place
        // (Strasbourg, ~2.4 s) and a person (Elvis Presley, ~1.4 s). The
        // first row suits places, the second people and works: without it a
        // person gets almost nothing (Elvis: 9 items; with it: 351).
        static readonly string[] Queried =
        {
            "P19", "P20", "P131", "P276", "P937", "P551", "P69", "P108",
            "P1071", "P840", "P159", "P740", "P1427", "P1444", "P180", "P921",
            "P175", "P161", "P50", "P170", "P86", "P162", "P710", "P1441",
        };

        // ---- anchor --------------------------------------------------------

        /// <summary>The Wikidata item a panel is about: its Wikipedia article's
        /// item if it has one, else the search subject.</summary>
        public Task<(string qid, string label)> Anchor(Result r, string subject, CancellationToken ct)
        {
            // A related card already knows its item.
            string known = r?.text?.Wikidata ?? r?.image?.Wikidata;
            if (!string.IsNullOrEmpty(known) && known.StartsWith("Q"))
                return Task.FromResult((known, r.text?.title ?? r.image?.title ?? known));

            string article = ArticleOf(r?.text);
            string key = article != null ? "wp:" + article : "s:" + (subject ?? "").ToLowerInvariant();
            if (!_anchors.TryGetValue(key, out var t))
            {
                t = article != null ? FromArticle(article, subject, ct) : FromSearch(subject, ct);
                _anchors[key] = t;
            }
            return t;
        }

        async Task<(string, string)> FromArticle(string article, string subject, CancellationToken ct)
        {
            var j = await Get($"{WikiApi}?action=query&format=json&prop=pageprops&ppprop=wikibase_item&redirects=1"
                            + $"&titles={UnityWebRequest.EscapeURL(article)}", ct);
            var page = (j?["query"]?["pages"] as JObject)?.Properties().Select(p => p.Value).FirstOrDefault();
            string qid = (string)page?["pageprops"]?["wikibase_item"];
            if (!string.IsNullOrEmpty(qid)) return (qid, (string)page["title"] ?? article);
            return await FromSearch(subject, ct);
        }

        async Task<(string, string)> FromSearch(string subject, CancellationToken ct)
        {
            // Pivot queries carry years ("Strasbourg cathedral 1700"); entity search does not want them.
            string clean = new string((subject ?? "").Where(ch => !char.IsDigit(ch)).ToArray()).Trim();
            if (clean.Length == 0) return (null, null);
            var j = await Get($"{Api}?action=wbsearchentities&format=json&language=en&limit=1"
                            + $"&search={UnityWebRequest.EscapeURL(clean)}", ct);
            var hit = j?["search"]?.FirstOrDefault();
            return ((string)hit?["id"], (string)hit?["label"] ?? clean);
        }

        // ---- related items -------------------------------------------------

        public Task<List<RelatedItem>> Window(string qid, string anchorLabel, int y0, int y1, CancellationToken ct)
        {
            string key = $"{qid}:{y0}:{y1}";
            if (!_windows.TryGetValue(key, out var t))
            {
                t = Fetch(qid, anchorLabel, y0, y1, ct);
                _windows[key] = t;
                // A cancelled / failed fetch must not stay cached.
                t.ContinueWith(x => { if (x.IsFaulted || x.IsCanceled) _windows.Remove(key); },
                               TaskScheduler.FromCurrentSynchronizationContext());
            }
            return t;
        }

        async Task<List<RelatedItem>> Fetch(string qid, string anchorLabel, int y0, int y1, CancellationToken ct)
        {
            string from = $"\"{Year(y0)}-01-01\"^^xsd:dateTime", to = $"\"{Year(y1 + 1)}-01-01\"^^xsd:dateTime";
            string inbound = string.Join(" ", Queried.Select(p => "wdt:" + p));
            const string tail = @"
  OPTIONAL { ?item wdt:P18 ?image }
  OPTIONAL { ?article schema:about ?item ; schema:isPartOf <https://en.wikipedia.org/> }
  SERVICE wikibase:label { bd:serviceParam wikibase:language ""en,fr,de,it,es"". }";

            // Shape matters more than size here. This single pattern answered
            // in 2-5 s; splitting it into capped sub-queries per kind (people /
            // things / the anchor's own statements) timed out (504) every time.
            // So: one pattern, people and things together, balanced locally.
            // The date is a property path, so which date it was (born / died /
            // built) is not reported - the description usually says.
            string sparql = $@"SELECT ?item ?itemLabel ?itemDescription ?date ?p ?human ?image ?article WHERE {{
  VALUES ?p {{ {inbound} }}
  ?item ?p wd:{qid} .
  ?item (wdt:P585|wdt:P580|wdt:P571|wdt:P569|wdt:P570|wdt:P577|wdt:P1619) ?date .
  FILTER(?date >= {from} && ?date < {to})
  BIND(EXISTS {{ ?item wdt:P31 wd:Q5 }} AS ?human) {tail}
}} LIMIT {perQuery}";

            var rows = await Query(sparql, ct);

            var byId = new Dictionary<string, RelatedItem>();
            Collect(rows, byId, anchorLabel);
            byId.Remove(qid);
            return byId.Values.Where(i => i.yearMin != int.MaxValue).ToList();
        }

        static void Collect(JArray rows, Dictionary<string, RelatedItem> byId, string anchor)
        {
            if (rows == null) return;
            foreach (var r in rows)
            {
                bool outbound = false;
                bool human = (string)r["human"]?["value"] == "true";
                string id = Last((string)r["item"]?["value"]);
                if (string.IsNullOrEmpty(id)) continue;
                if (!byId.TryGetValue(id, out var it))
                {
                    it = new RelatedItem
                    {
                        qid = id,
                        label = (string)r["itemLabel"]?["value"] ?? id,
                        description = (string)r["itemDescription"]?["value"] ?? "",
                        human = human,
                    };
                    byId[id] = it;
                }
                it.image ??= Https((string)r["image"]?["value"]);
                it.article ??= (string)r["article"]?["value"];

                if (it.relation == null)
                {
                    if (outbound)
                    {
                        string pl = (string)r["propLabel"]?["value"];
                        if (!string.IsNullOrEmpty(pl)) it.relation = $"{anchor}: {pl}";
                    }
                    else if (Inbound.TryGetValue(Last((string)r["p"]?["value"]) ?? "", out var phrase))
                    {
                        it.relation = $"{phrase} {anchor}";
                        it.prop = Last((string)r["p"]?["value"]);
                    }
                }

                int? y = ParseYear((string)r["date"]?["value"]);
                if (!y.HasValue) continue;
                if (y.Value < it.yearMin) it.yearMin = y.Value;
                if (y.Value > it.yearMax) it.yearMax = y.Value;
                // Prefer birth / founding as the headline date.
                string dp = Last((string)r["dp"]?["value"]);
                if (it.datePhrase == null || dp == "P569" || dp == "P571")
                {
                    it.datePhrase = DateProps.TryGetValue(dp ?? "", out var w) ? w : "";
                    it.dateYear = y.Value;
                }
            }
        }

        // ---- pictures ------------------------------------------------------

        /// <summary>Wikidata has a picture (P18) for few songs, films or albums;
        /// their Wikipedia articles usually lead with one (cover, poster). One
        /// batched request for the items that have an article but no picture.</summary>
        public async Task FillImages(List<RelatedItem> items, CancellationToken ct)
        {
            var need = items.Where(i => i.image == null && i.article != null && !i.imageTried).ToList();
            if (need.Count == 0) return;
            foreach (var i in need) i.imageTried = true;

            var titleOf = need.ToDictionary(i => i, i => TitleOf(i.article));
            string titles = string.Join("|", titleOf.Values.Where(t => t != null).Distinct());
            if (titles.Length == 0) return;

            var j = await Get($"{WikiApi}?action=query&format=json&prop=pageimages&piprop=thumbnail"
                            + $"&pithumbsize={WebGate.CardWidth}&pilicense=any&redirects=1&titles={UnityWebRequest.EscapeURL(titles)}", ct);
            var q = j?["query"];
            if (q == null) return;

            // Titles come back normalised and redirected; follow both.
            var alias = new Dictionary<string, string>();
            foreach (var n in (q["normalized"] as JArray) ?? new JArray()) alias[(string)n["from"]] = (string)n["to"];
            foreach (var r in (q["redirects"] as JArray) ?? new JArray()) alias[(string)r["from"]] = (string)r["to"];
            var thumbs = new Dictionary<string, JToken>();
            foreach (var page in ((q["pages"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()).Select(p => p.Value))
                if (page["thumbnail"] != null) thumbs[(string)page["title"]] = page["thumbnail"];

            foreach (var kv in titleOf)
            {
                string t = kv.Value;
                for (int hop = 0; hop < 3 && t != null && alias.TryGetValue(t, out var to); hop++) t = to;
                if (t == null || !thumbs.TryGetValue(t, out var th)) continue;
                kv.Key.image = (string)th["source"];
                kv.Key.imageW = (int?)th["width"] ?? 0;
                kv.Key.imageH = (int?)th["height"] ?? 0;
                kv.Key.imageFromWikipedia = true;
            }
        }

        static string TitleOf(string articleUrl)
        {
            int i = articleUrl.IndexOf("/wiki/", StringComparison.Ordinal);
            return i < 0 ? null : UnityWebRequest.UnEscapeURL(articleUrl.Substring(i + 6)).Replace('_', ' ');
        }

        // ---- to cards ------------------------------------------------------

        /// <summary>A grid row for a related item: a text card, plus an image card when it has one.</summary>
        public static Result ToResult(RelatedItem it)
        {
            string facet = it.facet ?? (it.human ? "person" : (it.prop != null && Kinds.TryGetValue(it.prop, out var k) ? k : null));
            // A mention has no date of its own (it carries the card's year only for placement).
            bool mention = it.prop == "mention";
            bool dated = it.yearMin != int.MaxValue;
            int? y0 = dated ? it.yearMin : (int?)null, y1 = dated ? it.yearMax : (int?)null;
            string when = mention || !dated ? null
                        : string.IsNullOrEmpty(it.datePhrase) ? it.dateYear.ToString() : $"{it.datePhrase} {it.dateYear}";
            var sb = new StringBuilder();
            if (it.mediaKind == "video") sb.Append("Video\n\n");
            else if (it.mediaKind == "audio") sb.Append("Recording\n\n");
            if (!string.IsNullOrEmpty(it.description)) sb.Append(Capital(it.description)).Append("\n\n");
            if (when != null) sb.Append(when).Append("  ·  ");
            if (!string.IsNullOrEmpty(it.relation)) sb.Append(it.relation);

            bool other = it.source != null;   // not Wikidata / Wikipedia
            string basis = other ? it.source : "wikidata";
            var text = new Card
            {
                kind = "text",
                title = it.label,
                source = other ? it.source : it.article != null ? "wikipedia" : "wikidata",
                body = sb.ToString(),
                SourceUrl = it.link ?? it.article ?? $"https://www.wikidata.org/wiki/{it.qid}",
                YearBegin = y0,
                YearEnd = y1,
                DateConfidence = dated ? 0.95f : 0f,
                DateBasis = dated ? basis : "undated",
                credit = it.credit ?? "Wikidata",
                Wikidata = it.qid,
                license = it.license ?? "CC0",
                MediaUrl = it.mediaUrl,
                MediaKind = it.mediaKind,
            };

            Card image = null;
            if (!string.IsNullOrEmpty(it.image) && other)
            {
                image = new Card
                {
                    kind = "image",
                    // Commons cards are looked up again by file name (detail view).
                    title = it.source == "commons" && it.qid.StartsWith("commons:") ? it.qid.Substring(8) : it.label,
                    source = it.source,
                    body = it.label,
                    ImageUrl = it.mediaKind == "image" && it.mediaUrl != null ? it.mediaUrl : it.image,
                    ThumbUrl = it.image,
                    ThumbW = it.imageW,
                    ThumbH = it.imageH,
                    YearBegin = y0,
                    YearEnd = y1,
                    DateConfidence = dated ? 0.95f : 0f,
                    DateBasis = dated ? basis : "undated",
                    credit = it.credit,
                    license = it.license ?? "see source",
                    SourceUrl = it.link,
                    MediaUrl = it.mediaUrl,
                    MediaKind = it.mediaKind,
                };
            }
            else if (!string.IsNullOrEmpty(it.image) && it.imageFromWikipedia)
            {
                image = new Card
                {
                    kind = "image",
                    title = it.label,
                    source = "wikipedia",
                    body = it.label,
                    ImageUrl = it.image,
                    ThumbUrl = it.image,
                    ThumbW = it.imageW,
                    ThumbH = it.imageH,
                    YearBegin = y0,
                    YearEnd = y1,
                    DateConfidence = dated ? 0.95f : 0f,
                    DateBasis = dated ? "wikidata" : "undated",
                    credit = "via Wikipedia",
                    license = "see source - may be non-free",
                    SourceUrl = it.article,
                };
            }
            else if (!string.IsNullOrEmpty(it.image))
            {
                string file = UnityWebRequest.UnEscapeURL(Last(it.image) ?? "");
                image = new Card
                {
                    kind = "image",
                    title = file,
                    source = "commons",
                    body = it.label,
                    ImageUrl = it.image,
                    ThumbUrl = it.image + "?width=" + WebGate.CardWidth,
                    YearBegin = y0,
                    YearEnd = y1,
                    DateConfidence = dated ? 0.95f : 0f,
                    DateBasis = dated ? "wikidata" : "undated",
                    SourceUrl = "https://commons.wikimedia.org/wiki/File:" + UnityWebRequest.EscapeURL(file.Replace(' ', '_')),
                };
            }

            string label = mention ? "in this card"
                         : !dated ? facet ?? "related"
                         : facet != null ? $"{it.dateYear} · {facet}" : it.dateYear.ToString();
            return new Result
            {
                YearBegin = y0,
                YearEnd = y1,
                label = label,
                DateConfidence = dated ? 0.95f : 0f,
                text = text,
                image = image,
            };
        }

        // ---- plumbing ------------------------------------------------------

        static string ArticleOf(Card c)
        {
            if (c == null || c.source != "wikipedia" || string.IsNullOrEmpty(c.SourceUrl)) return null;
            int i = c.SourceUrl.IndexOf("/wiki/", StringComparison.Ordinal);
            return i < 0 ? null : UnityWebRequest.UnEscapeURL(c.SourceUrl.Substring(i + 6)).Replace('_', ' ');
        }

        // Newtonsoft turns ISO date strings into DateTime while parsing; read
        // back as text they come out in the machine's locale ("01/01/1956
        // 00:00:00") and every year is lost. Keep strings as strings.
        static JObject ParseJson(string text)
        {
            using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(text))
                   { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                return JObject.Load(reader);
        }

        static string Year(int y) => y.ToString("0000", CultureInfo.InvariantCulture);

        // "1712-01-01T00:00:00Z", "-0044-03-15T00:00:00Z"
        static int? ParseYear(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            bool neg = s[0] == '-';
            int start = neg || s[0] == '+' ? 1 : 0;
            int dash = s.IndexOf('-', start);
            if (dash < 0) return null;
            if (!int.TryParse(s.Substring(start, dash - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) return null;
            return neg ? -y : y;
        }

        static string Last(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            int i = uri.LastIndexOf('/');
            return i >= 0 ? uri.Substring(i + 1) : uri;
        }

        static string Https(string u) => u != null && u.StartsWith("http://") ? "https://" + u.Substring(7) : u;

        static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>Set while the query service has asked us to wait (HTTP 429).</summary>
        public static Action<int> Throttled;

        async Task<JArray> Query(string sparql, CancellationToken ct)
        {
            string url = $"{Sparql}?format=json&query={UnityWebRequest.EscapeURL(sparql)}";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var (json, retryAfter) = await GetWithRetryHint(url, ct, 45);
                if (json != null) return json["results"]?["bindings"] as JArray;
                if (retryAfter <= 0 || attempt == 1) return null;
                // Busy: wait what the server asks (capped), then try once more.
                int wait = Mathf.Clamp(retryAfter, 1, 75);
                Throttled?.Invoke(wait);
                await Task.Delay(wait * 1000, ct);
            }
            return null;
        }

        static async Task<JObject> Get(string url, CancellationToken ct, int timeout = 20) =>
            (await GetWithRetryHint(url, ct, timeout)).json;

        // json, or null and the Retry-After seconds on a 429 (0 otherwise).
        static async Task<(JObject json, int retryAfter)> GetWithRetryHint(string url, CancellationToken ct, int timeout)
        {
            // Answered before (this session or an earlier one): no request at all.
            string cached = WebCache.Read(url);
            if (cached != null)
            {
                try { return (ParseJson(cached), 0); } catch { }
            }
            // SPARQL handles its own 429 retry (it tells the UI); others retry here.
            using (var req = await WebGate.Send(url, ct, timeout, "related",
                       accept: "application/sparql-results+json, application/json",
                       retries: url.StartsWith(Sparql) ? 0 : 3))
            {
                if (req.result != UnityWebRequest.Result.Success)
                {
                    if (req.responseCode == 429)
                    {
                        int.TryParse(req.GetResponseHeader("Retry-After") ?? "", out int ra);
                        Debug.LogWarning($"[related] query service busy (429), retry after {ra}s");
                        return (null, ra > 0 ? ra : 60);
                    }
                    Debug.LogWarning($"[related] {req.error}");
                    return (null, 0);
                }
                try
                {
                    var j = ParseJson(req.downloadHandler.text);
                    WebCache.Write(url, req.downloadHandler.text);
                    return (j, 0);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[related] unreadable answer: {e.Message}");
                    return (null, 0);
                }
            }
        }
    }
}
