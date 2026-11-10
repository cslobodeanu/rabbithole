// More for the timeline itself, inside its window of years, from sources that
// can filter by date - where Commons (text search: modern photos rarely say
// their year) and the one sliced Wikipedia article run thin, recent decades
// especially.
//
//   Wikidata          what is linked to the subject's item (films, books, TV
//                     episodes, events, people "about" / "depicting" it...)
//                     with a date inside the window - already paired with
//                     picture and article
//   Internet Archive  films, recordings, pictures and scans with a year inside
//                     the window
//
// Both go through WebCache, so a window asked again (or extended into) is free.

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
    public static class TimelineSources
    {
        static readonly WikidataRelated _wikidata = new WikidataRelated();

        /// <summary>Items linked to the subject's Wikidata item, dated inside
        /// [y0, y1], as timeline results. At most `limit`, spread over the years.
        /// Call from the main thread.</summary>
        public static async Task<List<Result>> Wikidata(string subject, int y0, int y1, int limit, CancellationToken ct)
        {
            var (qid, label) = await _wikidata.Anchor(null, subject, ct);
            if (string.IsNullOrEmpty(qid)) return new List<Result>();
            var items = await _wikidata.Window(qid, label ?? subject, y0, y1, ct) ?? new List<RelatedItem>();

            // Spread over the years: one per year in turn, the ones with a
            // picture and an article first within a year.
            var byYear = items.GroupBy(i => i.dateYear)
                              .Select(g => new Queue<RelatedItem>(g.OrderByDescending(i => (i.image != null ? 2 : 0) + (i.article != null ? 1 : 0))))
                              .ToList();
            var pick = new List<RelatedItem>();
            while (pick.Count < limit && byYear.Count > 0)
                for (int g = byYear.Count - 1; g >= 0 && pick.Count < limit; g--)
                {
                    pick.Add(byYear[g].Dequeue());
                    if (byYear[g].Count == 0) byYear.RemoveAt(g);
                }

            try { await _wikidata.FillImages(pick, ct); }
            catch (OperationCanceledException) { throw; }
            catch { }
            UnityEngine.Debug.Log($"[timeline] Wikidata {y0}–{y1}: {pick.Count} of {items.Count} linked to {label}");
            return pick.Select(WikidataRelated.ToResult).ToList();
        }

        /// <summary>Internet Archive items about the subject with a year in
        /// [y0, y1], most downloaded first, as picture cards.</summary>
        public static async Task<List<Card>> Archive(string subject, int y0, int y1, int limit)
        {
            var cards = new List<Card>();
            string clean = Regex.Replace(subject ?? "", @"[^\p{L}\p{N} ]", " ").Trim();
            if (clean.Length == 0) return cards;
            string q = $"(title:({clean}) OR subject:({clean})) AND year:[{y0} TO {y1}] AND mediatype:(movies OR audio OR image OR texts)";
            string url = "https://archive.org/advancedsearch.php?q=" + UnityWebRequest.EscapeURL(q)
                       + "&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=year&fl%5B%5D=mediatype&fl%5B%5D=subject"
                       + "&fl%5B%5D=description&fl%5B%5D=creator&fl%5B%5D=licenseurl"
                       + $"&sort%5B%5D=downloads+desc&rows={Math.Min(100, Math.Max(10, limit))}&page=1&output=json";
            var j = await WebCache.GetJson(url, CancellationToken.None, 25, "archive");
            foreach (var d in (j?["response"]?["docs"] as JArray) ?? new JArray())
            {
                string id = (string)d["identifier"];
                if (string.IsNullOrEmpty(id) || !int.TryParse(Text(d["year"]), out int year)) continue;
                if (year < y0 || year > y1) continue;
                string media = (string)d["mediatype"];
                string title = Clip(Text(d["title"]), 120);
                string desc = Clip(Regex.Replace(Text(d["description"]), "<[^>]+>", " ").Trim(), 600);
                string kind = media == "movies" ? "video" : media == "audio" ? "audio" : "image";
                cards.Add(new Card
                {
                    kind = "image",
                    title = title,
                    source = "archive",
                    body = (kind == "video" ? "Video\n\n" : kind == "audio" ? "Recording\n\n" : "") + desc,
                    ImageUrl = "https://archive.org/services/img/" + id,
                    ThumbUrl = "https://archive.org/services/img/" + id,
                    YearBegin = year,
                    YearEnd = year,
                    DateConfidence = 0.85f,
                    DateBasis = "archive year",
                    credit = Clip(Text(d["creator"]), 80),
                    license = (string)d["licenseurl"] ?? "see source",
                    SourceUrl = "https://archive.org/details/" + id,
                    MediaUrl = "https://archive.org/details/" + id,
                    MediaKind = kind,
                    Haystack = $"{title} {Text(d["subject"])} {desc}",
                });
            }
            return cards;
        }

        static string Text(JToken t) =>
            t == null ? "" : t.Type == JTokenType.Array ? string.Join(" ", t.Select(x => (string)x)) : (string)t ?? "";

        static string Clip(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s ?? "" : s.Substring(0, n - 1) + "…";
    }
}
