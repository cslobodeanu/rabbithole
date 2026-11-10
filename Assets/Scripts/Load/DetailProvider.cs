// "Tell me more" for one result: what the card had to leave out.
//
// Cards are deliberately thin - a 640px thumb, 600 characters of a Commons
// description, one Wikipedia paragraph. When a panel is opened full screen
// this goes back to the source for the rest:
//
//   commons    full description, artist, credit, licence, original date,
//              categories, object name, and a 2048px rendition
//   wikipedia  the whole section the paragraph came from, the article's
//              lead, and the article's lead image if the row has none
//
// Network-only, stateless, cancellable. A failed source just leaves its
// fields empty - the detail view shows what the card already had.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    public class Detail
    {
        public string title;
        public string text;                 // plain text, paragraphs separated by blank lines
        public string imageUrl;             // large rendition, may be null
        public int imageW, imageH;
        public string sourceUrl;
        public readonly List<KeyValuePair<string, string>> facts = new List<KeyValuePair<string, string>>();

        public void Fact(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Add(new KeyValuePair<string, string>(key, value.Trim()));
        }
    }

    public class DetailProvider
    {
        const string CommonsApi = "https://commons.wikimedia.org/w/api.php";
        const string WikiApi = "https://en.wikipedia.org/w/api.php";

        public int imageWidth = 1920;   // a standard Wikimedia width

        /// <summary>Fetch more about a result. Never throws except on cancellation.</summary>
        public async Task<Detail> Fetch(Result r, CancellationToken ct)
        {
            var d = new Detail();
            if (r == null) return d;

            var jobs = new List<Task>();
            if (r.image != null && r.image.source == "commons") jobs.Add(Commons(r.image, d, ct));
            if (r.text != null && r.text.source == "wikipedia") jobs.Add(Wikipedia(r.text, d, r.image == null, ct));
            try { await Task.WhenAll(jobs); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Debug.LogWarning($"[detail] {e.Message}"); }
            ct.ThrowIfCancellationRequested();
            return d;
        }

        // ---- commons -------------------------------------------------------

        async Task Commons(Card c, Detail d, CancellationToken ct)
        {
            string file = c.title.StartsWith("File:") ? c.title : "File:" + c.title;
            var json = await Get($"{CommonsApi}?action=query&format=json&titles={UnityWebRequest.EscapeURL(file)}"
                               + $"&prop=imageinfo&iiprop=url|extmetadata|size&iiurlwidth={imageWidth}"
                               + "&iiextmetadatalanguage=en", ct);
            var page = (json?["query"]?["pages"] as JObject)?.Properties().Select(p => p.Value).FirstOrDefault();
            var info = page?["imageinfo"]?.FirstOrDefault();
            if (info == null) return;
            var meta = info["extmetadata"];
            string M(string key) => Strip((string)meta?[key]?["value"] ?? "");

            lock (d)
            {
                d.title ??= Strip(M("ObjectName")).Length > 0 ? M("ObjectName") : c.title;
                string desc = M("ImageDescription");
                if (desc.Length > 0) d.text = Join(d.text, desc);
                d.imageUrl = (string)info["thumburl"] ?? (string)info["url"];
                d.imageW = (int?)info["thumbwidth"] ?? (int?)info["width"] ?? 0;
                d.imageH = (int?)info["thumbheight"] ?? (int?)info["height"] ?? 0;
                d.sourceUrl ??= (string)info["descriptionurl"];

                d.Fact("Date", M("DateTimeOriginal"));
                d.Fact("Artist", M("Artist"));
                d.Fact("Credit", M("Credit"));
                d.Fact("License", M("LicenseShortName"));
                d.Fact("Categories", M("Categories").Replace("|", " · "));
                int w = (int?)info["width"] ?? 0, h = (int?)info["height"] ?? 0;
                if (w > 0 && h > 0) d.Fact("Original", $"{w} × {h} px");
            }
        }

        // ---- wikipedia -----------------------------------------------------

        async Task Wikipedia(Card c, Detail d, bool wantImage, CancellationToken ct)
        {
            string article = ArticleFrom(c);
            if (string.IsNullOrEmpty(article)) return;

            var json = await Get($"{WikiApi}?action=query&format=json&prop=extracts|pageimages&explaintext=1"
                               + $"&redirects=1&pithumbsize=1280&titles={UnityWebRequest.EscapeURL(article)}", ct);
            var page = (json?["query"]?["pages"] as JObject)?.Properties().Select(p => p.Value).FirstOrDefault();
            string extract = (string)page?["extract"] ?? "";
            if (extract.Length == 0) return;

            // Split into (section, paragraphs); find the section holding the card's passage.
            var sections = new List<(string name, List<string> paras)> { ("", new List<string>()) };
            foreach (var raw in extract.Split('\n'))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                if (p.StartsWith("==")) { sections.Add((p.Trim('=', ' ').Trim(), new List<string>())); continue; }
                sections[sections.Count - 1].paras.Add(p);
            }

            string probe = (c.body ?? "").Trim();
            if (probe.Length > 80) probe = probe.Substring(0, 80);
            var hit = sections.FirstOrDefault(s => s.paras.Any(p => probe.Length > 0 && p.Contains(probe)));
            var lead = sections[0].paras;

            var sb = new StringBuilder();
            if (hit.paras != null && hit.paras.Count > 0)
            {
                if (hit.name.Length > 0) sb.Append(hit.name.ToUpperInvariant()).Append("\n\n");
                sb.Append(string.Join("\n\n", hit.paras));
            }
            if (lead.Count > 0 && (hit.paras == null || hit.name.Length > 0))
            {
                if (sb.Length > 0) sb.Append("\n\n\n");
                sb.Append("ABOUT ").Append(((string)page["title"] ?? article).ToUpperInvariant()).Append("\n\n");
                sb.Append(string.Join("\n\n", lead.Take(3)));
            }

            lock (d)
            {
                d.title ??= c.title;
                if (sb.Length > 0) d.text = Join(sb.ToString(), d.text);
                d.sourceUrl ??= c.SourceUrl;
                d.Fact("Article", (string)page["title"]);
                if (wantImage && d.imageUrl == null)
                {
                    var thumb = page["thumbnail"];
                    d.imageUrl = (string)thumb?["source"];
                    d.imageW = (int?)thumb?["width"] ?? 0;
                    d.imageH = (int?)thumb?["height"] ?? 0;
                }
            }
        }

        // ".../wiki/Strasbourg_Cathedral" -> "Strasbourg Cathedral"; else the
        // part of "Article - Section" before the dash.
        static string ArticleFrom(Card c)
        {
            if (!string.IsNullOrEmpty(c.SourceUrl))
            {
                int i = c.SourceUrl.IndexOf("/wiki/", StringComparison.Ordinal);
                if (i >= 0) return UnityWebRequest.UnEscapeURL(c.SourceUrl.Substring(i + 6)).Replace('_', ' ');
            }
            string t = c.title ?? "";
            int dash = t.IndexOf(" - ", StringComparison.Ordinal);
            return dash > 0 ? t.Substring(0, dash) : t;
        }

        // ---- plumbing ------------------------------------------------------

        static string Join(string a, string b) =>
            string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + "\n\n\n" + b;

        static async Task<JObject> Get(string url, CancellationToken ct)
        {
            using (var req = await WebGate.Send(url, ct, 20, "detail"))
            {
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[detail] {req.error}");
                    return null;
                }
                try { return ParseJson(req.downloadHandler.text); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[detail] unreadable answer: {e.Message}");
                    return null;
                }
            }
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

        static string Strip(string s) =>
            string.IsNullOrEmpty(s) ? "" :
            Regex.Replace(Regex.Replace(s, "<[^>]+>", " "), @"\s+", " ").Trim();
    }
}
