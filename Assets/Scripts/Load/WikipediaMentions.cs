// What a Wikipedia card is about: the people, places and things its paragraph
// mentions. A card on Elvis's wedding mentions Priscilla Beaulieu, the
// Aladdin hotel and Las Vegas - those, not his albums of that year, are what
// "related" should mean for it.
//
// The article's links are fetched once (cached per article) and matched
// against the paragraph text: a link counts as mentioned when its title, or
// its title without a "(...)" qualifier, appears as a whole word. Overlapping
// matches keep the longest ("Priscilla Beaulieu" beats "Priscilla (film)").
// Text that links under a different name ("Priscilla" -> Priscilla Presley)
// is missed; most mentions are not.
//
// Then one batched request for descriptions, pictures and Wikidata ids.
// Wikipedia's API, not the query service: fast and not rate-limited.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    public class WikipediaMentions
    {
        const string Api = "https://en.wikipedia.org/w/api.php";

        public int maxPerCard = 8;

        readonly Dictionary<string, Task<List<string>>> _links = new Dictionary<string, Task<List<string>>>();
        readonly Dictionary<string, Task<List<RelatedItem>>> _cards = new Dictionary<string, Task<List<RelatedItem>>>();

        /// <summary>Things the card's paragraph mentions, in reading order.
        /// Empty for cards that are not Wikipedia paragraphs.</summary>
        public Task<List<RelatedItem>> For(Result r, int year, CancellationToken ct)
        {
            var c = r?.text;
            string article = ArticleOf(c);
            if (article == null) return Task.FromResult(new List<RelatedItem>());

            // A timeline card is a paragraph of the article: read that. A related
            // card (it knows its Wikidata item) only carries a short description:
            // read the lead of its own article instead.
            bool paragraph = string.IsNullOrEmpty(c.Wikidata) && !string.IsNullOrEmpty(c.body);
            string key = paragraph ? article + "|" + c.body.GetHashCode() : "lead|" + article;
            if (!_cards.TryGetValue(key, out var t))
            {
                t = paragraph ? Find(article, c.body, year, ct) : FromLead(article, year, ct);
                _cards[key] = t;
                t.ContinueWith(x => { if (x.IsFaulted || x.IsCanceled) _cards.Remove(key); },
                               TaskScheduler.FromCurrentSynchronizationContext());
            }
            return t;
        }

        async Task<List<RelatedItem>> FromLead(string article, int year, CancellationToken ct)
        {
            var j = await Get($"{Api}?action=query&format=json&prop=extracts&exintro=1&explaintext=1&redirects=1"
                            + $"&titles={UnityWebRequest.EscapeURL(article)}", ct);
            var page = (j?["query"]?["pages"] as JObject)?.Properties().Select(p => p.Value).FirstOrDefault();
            string lead = (string)page?["extract"];
            string title = (string)page?["title"] ?? article;
            if (string.IsNullOrEmpty(lead)) return new List<RelatedItem>();
            return await Find(title, lead, year, ct);
        }

        async Task<List<RelatedItem>> Find(string article, string text, int year, CancellationToken ct)
        {
            if (!_links.TryGetValue(article, out var lt))
            {
                lt = Links(article, ct);
                _links[article] = lt;
            }
            var links = await lt;

            // Every match with its span; then keep the longest of overlapping ones.
            var hits = new List<(int start, int len, string title)>();
            foreach (var title in links)
            {
                if (title == article) continue;
                foreach (var form in new[] { title, Regex.Replace(title, @"\s*\(.*\)$", "") }.Distinct())
                {
                    if (form.Length < 4 || Regex.IsMatch(form, @"^[\d\s]+$") || IsDate(form)) continue;
                    var m = Regex.Match(text, @"\b" + Regex.Escape(form) + @"\b");
                    if (m.Success) { hits.Add((m.Index, m.Length, title)); break; }
                }
            }
            var kept = new List<(int start, int len, string title)>();
            foreach (var h in hits.OrderBy(h => h.start).ThenByDescending(h => h.len))
            {
                if (kept.Any(k => h.start < k.start + k.len && k.start < h.start + h.len)) continue;
                kept.Add(h);
            }
            var titles = kept.OrderBy(k => k.start).Select(k => k.title).Take(maxPerCard).ToList();
            if (titles.Count == 0) return new List<RelatedItem>();
            return await Details(titles, year, ct);
        }

        // All article-namespace links of the page (follows continuation).
        async Task<List<string>> Links(string article, CancellationToken ct)
        {
            var all = new List<string>();
            string cont = "";
            for (int page = 0; page < 6; page++)
            {
                var j = await Get($"{Api}?action=query&format=json&prop=links&plnamespace=0&pllimit=max&redirects=1"
                                + $"&titles={UnityWebRequest.EscapeURL(article)}{cont}", ct);
                if (j == null) break;
                foreach (var pg in ((j["query"]?["pages"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()).Select(p => p.Value))
                    foreach (var l in (pg["links"] as JArray) ?? new JArray())
                        all.Add((string)l["title"]);
                string pl = (string)j["continue"]?["plcontinue"];
                if (pl == null) break;
                cont = "&plcontinue=" + UnityWebRequest.EscapeURL(pl);
            }
            return all;
        }

        // Description, picture and Wikidata id for each title, one request.
        async Task<List<RelatedItem>> Details(List<string> titles, int year, CancellationToken ct)
        {
            var j = await Get($"{Api}?action=query&format=json&redirects=1&prop=pageprops|pageimages|description"
                            + "&ppprop=wikibase_item&piprop=thumbnail&pithumbsize=" + WebGate.CardWidth + "&pilicense=any"
                            + $"&titles={UnityWebRequest.EscapeURL(string.Join("|", titles))}", ct);
            var q = j?["query"];
            if (q == null) return new List<RelatedItem>();

            var alias = new Dictionary<string, string>();
            foreach (var n in (q["normalized"] as JArray) ?? new JArray()) alias[(string)n["from"]] = (string)n["to"];
            foreach (var r in (q["redirects"] as JArray) ?? new JArray()) alias[(string)r["from"]] = (string)r["to"];
            var pages = new Dictionary<string, JToken>();
            foreach (var pg in ((q["pages"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()).Select(p => p.Value))
                if (pg["missing"] == null) pages[(string)pg["title"]] = pg;

            var result = new List<RelatedItem>();
            var seen = new HashSet<string>();
            foreach (var title in titles)
            {
                string t = title;
                for (int hop = 0; hop < 3 && alias.TryGetValue(t, out var to); hop++) t = to;
                if (!pages.TryGetValue(t, out var pg) || !seen.Add(t)) continue;
                var thumb = pg["thumbnail"];
                result.Add(new RelatedItem
                {
                    qid = (string)pg["pageprops"]?["wikibase_item"] ?? "wp:" + t,
                    label = t,
                    description = (string)pg["description"] ?? "",
                    article = "https://en.wikipedia.org/wiki/" + UnityWebRequest.EscapeURL(t.Replace(' ', '_')),
                    image = (string)thumb?["source"],
                    imageW = (int?)thumb?["width"] ?? 0,
                    imageH = (int?)thumb?["height"] ?? 0,
                    imageFromWikipedia = thumb != null,
                    imageTried = true,
                    relation = "mentioned in this card",
                    prop = "mention",
                    yearMin = year,
                    yearMax = year,
                    dateYear = year,
                    datePhrase = "",
                });
            }
            return result;
        }

        static readonly Regex Months = new Regex(
            @"^(January|February|March|April|May|June|July|August|September|October|November|December)( \d{1,2})?$");

        static bool IsDate(string s) => Months.IsMatch(s) || Regex.IsMatch(s, @"^\d{1,4}s?( in .*)?$");

        static string ArticleOf(Card c)
        {
            if (c == null || c.source != "wikipedia" || string.IsNullOrEmpty(c.SourceUrl)) return null;
            int i = c.SourceUrl.IndexOf("/wiki/", StringComparison.Ordinal);
            return i < 0 ? null : UnityWebRequest.UnEscapeURL(c.SourceUrl.Substring(i + 6)).Replace('_', ' ');
        }

        static async Task<JObject> Get(string url, CancellationToken ct)
        {
            string cached = WebCache.Read(url);
            if (cached != null)
            {
                try { return WebCache.Parse(cached) as JObject; } catch { }
            }
            using (var req = await WebGate.Send(url, ct, 20, "mentions"))
            {
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[mentions] {req.error}");
                    return null;
                }
                try
                {
                    using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(req.downloadHandler.text))
                           { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                    {
                        var j = JObject.Load(reader);
                        WebCache.Write(url, req.downloadHandler.text);
                        return j;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[mentions] unreadable answer: {e.Message}");
                    return null;
                }
            }
        }
    }
}
