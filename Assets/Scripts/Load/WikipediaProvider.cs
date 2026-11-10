// Wikipedia - the text source. Two calls: resolve the query to an article
// title, then pull the whole article as plaintext.
//
// Wikipedia has no date facet, so the date work happens here: the extract is
// split into paragraphs and each is scored by the years its prose mentions.
// It is not a date-filtered search - it is one article, sliced up and ranked.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    public class WikipediaProvider
    {
        const string Api = "https://en.wikipedia.org/w/api.php";

        public async Task<List<Card>> Fetch(string subject, int y0, int y1, int limit)
        {
            var cards = new List<Card>();

            // 1. resolve the title. srlimit=1 means whatever Wikipedia ranks
            //    first becomes the article, with no signal if it chose wrong.
            var hits = await Get($"{Api}?action=query&list=search"
                               + $"&srsearch={UnityWebRequest.EscapeURL(subject)}"
                               + "&srlimit=1&format=json");
            string title = (string)hits?["query"]?["search"]?.FirstOrDefault()?["title"];
            if (string.IsNullOrEmpty(title)) return cards;

            // 2. pull the article as plaintext
            var page = await Get($"{Api}?action=query&prop=extracts&explaintext=1"
                               + $"&titles={UnityWebRequest.EscapeURL(title)}"
                               + "&redirects=1&format=json");
            var pages = page?["query"]?["pages"] as JObject;
            string extract = pages?.Properties().Select(p => (string)p.Value["extract"])
                                                .FirstOrDefault() ?? "";
            if (extract.Length == 0) return cards;

            string url = "https://en.wikipedia.org/wiki/"
                       + UnityWebRequest.EscapeURL(title.Replace(" ", "_"));

            string section = "";
            foreach (var raw in extract.Split('\n'))
            {
                string para = raw.Trim();
                if (para.Length == 0) continue;
                if (para.StartsWith("=="))            // section heading
                {
                    section = para.Trim('=', ' ').Trim();
                    continue;
                }
                if (para.Length < 120) continue;

                var ys = DateParser.YearsIn(para);
                var inWindow = ys.Where(y => y >= y0 && y <= y1).ToList();
                var use = inWindow.Count > 0 ? inWindow : ys;

                cards.Add(new Card
                {
                    kind = "text",
                    title = section.Length > 0 ? $"{title} - {section}" : title,
                    source = "wikipedia",
                    body = para,
                    Years = ys,
                    YearBegin = use.Count > 0 ? use.Min() : (int?)null,
                    YearEnd = use.Count > 0 ? use.Max() : (int?)null,
                    DateConfidence = inWindow.Count > 0 ? 0.9f : (ys.Count > 0 ? 0.5f : 0f),
                    DateBasis = ys.Count > 0 ? "prose" : "none",
                    credit = "Wikipedia contributors",
                    license = "CC BY-SA 4.0",
                    SourceUrl = url,
                    Haystack = $"{title} {section} {para}",
                });
            }

            // The lead paragraph carries no dates but anchors the subject -
            // keep it regardless of period, then the best-dated passages.
            var lead = cards.FirstOrDefault();
            var dated = cards.Where(c => c.YearBegin.HasValue)
                             .OrderByDescending(c => Scoring.CardDate(c, y0, y1))
                             .Take(limit * 3).ToList();
            if (lead != null && !dated.Contains(lead)) dated.Insert(0, lead);
            return dated;
        }

        static async Task<JObject> Get(string url)
        {
            using (var req = await WebGate.Send(url, System.Threading.CancellationToken.None, 15, "wikipedia"))
            {
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[wikipedia] {req.error}");
                    return null;
                }
                try { return JObject.Parse(req.downloadHandler.text); }
                catch { return null; }
            }
        }
    }
}
