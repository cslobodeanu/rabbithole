// A picture for a card that came without one.
//
// Cheapest first, and batched where the API allows, so it can run for every
// card (and every wall tile) without flooding anyone:
//
//   1. the Wikipedia article's lead image        (pageimages, 50 titles per request)
//   2. the Wikidata item's picture statements     (wbgetentities, 20 items per request):
//      image, poster, logo, flag, coat of arms, grave, collage, interior,
//      aerial view, and a locator map as the last resort
//   3. a Commons search for the title            (one request per card; optional)
//
// Answers are cached on disk (WebCache) and in memory, misses too.

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
    public class Picture
    {
        public string url, page, source, title, credit, license;
        public int width, height;
    }

    public static class PictureFinder
    {
        const string WikiApi = "https://en.wikipedia.org/w/api.php";
        const string DataApi = "https://www.wikidata.org/w/api.php";
        const string CommonsApi = "https://commons.wikimedia.org/w/api.php";

        // Best first.
        static readonly string[] PictureProps = { "P18", "P3383", "P2716", "P5775", "P8592", "P1442", "P154", "P41", "P94", "P242" };

        static readonly Dictionary<string, Task<Picture>> _found = new Dictionary<string, Task<Picture>>();

        /// <summary>A picture for this result, or null. search: also try a
        /// Commons search for its title (one request per call).</summary>
        public static Task<Picture> Find(Result r, bool search, CancellationToken ct = default)
        {
            var c = r?.text ?? r?.image;
            if (c == null || c.source == "deduction") return Task.FromResult<Picture>(null);
            // A passage cut from an article ("UFO - History", dated by its prose)
            // is not the article: its lead image would land on every passage.
            if (c.source == "wikipedia" && (c.DateBasis == "prose" || c.DateBasis == "none")) return Task.FromResult<Picture>(null);
            string article = ArticleOf(c);
            // Only a card that is the article itself gets its lead image.
            if (article != null && !SameTitle(c.title, article)) article = null;
            string qid = c.Wikidata != null && Regex.IsMatch(c.Wikidata, @"^Q\d+$") ? c.Wikidata : null;
            string title = Clean(c.title);
            if (article == null && qid == null && (!search || title.Length < 3)) return Task.FromResult<Picture>(null);

            string key = (article ?? "") + "|" + (qid ?? "") + "|" + (search ? title : "");
            lock (_found)
            {
                if (_found.TryGetValue(key, out var t)) return t;
                t = Run(article, qid, search ? title : null, ct);
                _found[key] = t;
                return t;
            }
        }

        static async Task<Picture> Run(string article, string qid, string searchTitle, CancellationToken ct)
        {
            try
            {
                if (article != null)
                {
                    var p = await _leads.Get(article, ct);
                    if (p != null) return p;
                }
                if (qid != null)
                {
                    var p = await _items.Get(qid, ct);
                    if (p != null) return p;
                }
                if (!string.IsNullOrEmpty(searchTitle)) return await Search(searchTitle, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { UnityEngine.Debug.LogWarning($"[pictures] {e.Message}"); }
            return null;
        }

        // ---- 1. Wikipedia lead images ------------------------------------------

        static readonly Batcher<Picture> _leads = new Batcher<Picture>(50, async (titles, ct) =>
        {
            string url = $"{WikiApi}?action=query&format=json&redirects=1&prop=pageimages|pageprops&piprop=thumbnail"
                       + $"&pithumbsize={WebGate.CardWidth}&pilicense=any&pilimit=50"
                       + "&titles=" + UnityWebRequest.EscapeURL(string.Join("|", titles));
            var j = await WebCache.GetJson(url, ct, 20, "pictures");
            var result = new Dictionary<string, Picture>();
            if (j == null) return result;

            // asked title -> final title, through normalisation and redirects
            var to = new Dictionary<string, string>();
            foreach (var n in (j["query"]?["normalized"] as JArray) ?? new JArray()) to[(string)n["from"]] = (string)n["to"];
            foreach (var n in (j["query"]?["redirects"] as JArray) ?? new JArray()) to[(string)n["from"]] = (string)n["to"];
            var pages = new Dictionary<string, JToken>();
            foreach (var p in (j["query"]?["pages"] as JObject)?.Properties().Select(x => x.Value) ?? Enumerable.Empty<JToken>())
                if (p["title"] != null) pages[(string)p["title"]] = p;

            foreach (var t in titles)
            {
                string f = t;
                for (int i = 0; i < 3 && to.TryGetValue(f, out var next); i++) f = next;
                if (!pages.TryGetValue(f, out var pg)) continue;
                var th = pg["thumbnail"];
                string src = (string)th?["source"];
                if (string.IsNullOrEmpty(src)) continue;
                result[t] = new Picture
                {
                    url = src, width = (int?)th["width"] ?? 0, height = (int?)th["height"] ?? 0,
                    source = "wikipedia", title = f,
                    page = "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(f.Replace(' ', '_')),
                };
            }
            return result;
        });

        // ---- 2. Wikidata picture statements ---------------------------------------

        static readonly Batcher<Picture> _items = new Batcher<Picture>(20, async (ids, ct) =>
        {
            var j = await WebCache.GetJson($"{DataApi}?action=wbgetentities&format=json&props=claims&ids={string.Join("|", ids)}",
                                           ct, 30, "pictures");
            var result = new Dictionary<string, Picture>();
            var entities = j?["entities"] as JObject;
            if (entities == null) return result;
            foreach (var id in ids)
            {
                var claims = entities[id]?["claims"] as JObject;
                if (claims == null) continue;
                foreach (var prop in PictureProps)
                {
                    string file = FileOf(claims[prop] as JArray);
                    if (file == null) continue;
                    result[id] = new Picture
                    {
                        url = WebGate.Direct("https://commons.wikimedia.org/wiki/Special:FilePath/" +
                                             Uri.EscapeDataString(file.Replace(' ', '_')) + "?width=" + WebGate.CardWidth),
                        source = "commons", title = file,
                        page = "https://commons.wikimedia.org/wiki/File:" + Uri.EscapeDataString(file.Replace(' ', '_')),
                    };
                    break;
                }
            }
            return result;
        });

        // Preferred statement first, never a deprecated one.
        static string FileOf(JArray statements)
        {
            if (statements == null) return null;
            JToken best = null;
            foreach (var s in statements)
            {
                string rank = (string)s["rank"];
                if (rank == "deprecated") continue;
                if (best == null || rank == "preferred") best = s;
                if (rank == "preferred") break;
            }
            return (string)best?["mainsnak"]?["datavalue"]?["value"];
        }

        // ---- 3. Commons search ------------------------------------------------------

        static async Task<Picture> Search(string title, CancellationToken ct)
        {
            string q = "filetype:bitmap \"" + title.Replace("\"", "") + "\"";
            string url = $"{CommonsApi}?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=1"
                       + "&gsrsearch=" + UnityWebRequest.EscapeURL(q)
                       + $"&prop=imageinfo&iiprop=url|size|extmetadata&iiurlwidth={WebGate.CardWidth}"
                       + "&iiextmetadatafilter=Artist|LicenseShortName";
            var j = await WebCache.GetJson(url, ct, 20, "pictures");
            var pg = (j?["query"]?["pages"] as JObject)?.Properties().Select(x => x.Value).FirstOrDefault();
            var info = pg?["imageinfo"]?.FirstOrDefault();
            if (info == null) return null;
            string Meta(string k) => Regex.Replace((string)info["extmetadata"]?[k]?["value"] ?? "", "<[^>]+>", "").Trim();
            return new Picture
            {
                url = (string)info["thumburl"] ?? (string)info["url"],
                width = (int?)info["thumbwidth"] ?? 0, height = (int?)info["thumbheight"] ?? 0,
                source = "commons", title = ((string)pg["title"] ?? "").Replace("File:", ""),
                page = (string)info["descriptionurl"], credit = Meta("Artist"), license = Meta("LicenseShortName"),
            };
        }

        /// <summary>Give a result the picture found for it (cards, the detail
        /// view and the board then all show it).</summary>
        public static void Attach(Result r, Picture p)
        {
            if (r == null || p == null || r.image != null) return;
            r.image = new Card
            {
                kind = "image", title = p.title, source = p.source, body = "",
                ImageUrl = p.url, ThumbUrl = p.url, ThumbW = p.width, ThumbH = p.height,
                SourceUrl = p.page ?? "", credit = p.credit ?? "", license = p.license ?? "",
                YearBegin = r.YearBegin, YearEnd = r.YearEnd, DateBasis = "picture of the subject",
            };
        }

        // ---- helpers ------------------------------------------------------------------

        static string ArticleOf(Card c)
        {
            if (string.IsNullOrEmpty(c?.SourceUrl)) return null;
            int i = c.SourceUrl.IndexOf("wikipedia.org/wiki/", StringComparison.Ordinal);
            if (i < 0 || !c.SourceUrl.Contains("en.wikipedia.org")) return null;
            string t = UnityWebRequest.UnEscapeURL(c.SourceUrl.Substring(i + 19)).Replace('_', ' ');
            int hash = t.IndexOf('#');
            return hash >= 0 ? t.Substring(0, hash) : t;
        }

        static bool SameTitle(string a, string b)
        {
            string N(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            return N(a) == N(b);
        }

        static string Clean(string t)
        {
            t = MoreSources.TermOf(new Card { title = t ?? "" });
            return t.Length > 60 ? t.Substring(0, 60) : t;
        }

        /// <summary>Collects single requests for a moment and answers them
        /// with one request per batch.</summary>
        class Batcher<T> where T : class
        {
            readonly int _max;
            readonly Func<List<string>, CancellationToken, Task<Dictionary<string, T>>> _fetch;
            readonly Dictionary<string, TaskCompletionSource<T>> _waiting = new Dictionary<string, TaskCompletionSource<T>>();
            bool _scheduled;

            public Batcher(int max, Func<List<string>, CancellationToken, Task<Dictionary<string, T>>> fetch)
            {
                _max = max;
                _fetch = fetch;
            }

            public Task<T> Get(string key, CancellationToken ct)
            {
                lock (_waiting)
                {
                    if (!_waiting.TryGetValue(key, out var tcs))
                    {
                        tcs = new TaskCompletionSource<T>();
                        _waiting[key] = tcs;
                    }
                    if (!_scheduled) { _scheduled = true; _ = Flush(); }
                    return tcs.Task;
                }
            }

            async Task Flush()
            {
                await Task.Delay(250);          // let a row's worth of cards ask first
                while (true)
                {
                    List<KeyValuePair<string, TaskCompletionSource<T>>> batch;
                    lock (_waiting)
                    {
                        batch = _waiting.Take(_max).ToList();
                        foreach (var kv in batch) _waiting.Remove(kv.Key);
                        if (batch.Count == 0) { _scheduled = false; return; }
                    }
                    Dictionary<string, T> found = null;
                    try { found = await _fetch(batch.Select(kv => kv.Key).ToList(), CancellationToken.None); }
                    catch (Exception e) { UnityEngine.Debug.LogWarning($"[pictures] {e.Message}"); }
                    foreach (var kv in batch)
                        kv.Value.TrySetResult(found != null && found.TryGetValue(kv.Key, out var v) ? v : null);
                }
            }
        }
    }
}
