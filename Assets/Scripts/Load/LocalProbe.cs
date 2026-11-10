// On-device version of the probe pipeline - no server, no PC.
//
// The Python server exists only as IPC between Python and the browser. Unity
// can hit the archive APIs directly, so the whole HTTP layer collapses into
// this. Same ProbeResult type as ProbeClient, so the display code cannot tell
// which one it is talking to.
//
// This file is the seed: the awaitable plumbing, the date logic (the part
// that took the longest to get right in Python), and Commons as a worked
// example. The other four providers follow the same shape.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    /// <summary>Either the remote server or the on-device pipeline.</summary>
    public interface IProbeSource
    {
        Task<ProbeResult> Search(string subject, int from, int to, int top, bool reel);
        // Progressive search: invoke onRow for each Result as it becomes
        // available. Implementations may choose to emit partial results.
        Task Search(string subject, int from, int to, int top, bool reel, Action<Result> onRow, System.Threading.CancellationToken cancellationToken = default);
    }

    // ---- await support for UnityWebRequest -------------------------------
    // Lets providers fan out with Task.WhenAll instead of nested coroutines.
    // The request itself still runs on Unity's loop; only the awaiting is ours.

    public struct UwrAwaiter : INotifyCompletion
    {
        readonly UnityWebRequestAsyncOperation _op;
        public UwrAwaiter(UnityWebRequestAsyncOperation op) { _op = op; }
        public bool IsCompleted => _op.isDone;
        public void OnCompleted(Action k) { _op.completed += _ => k(); }
        public UnityWebRequest GetResult() => _op.webRequest;
    }

    public static class UwrExtensions
    {
        public static UwrAwaiter GetAwaiter(this UnityWebRequestAsyncOperation op)
            => new UwrAwaiter(op);
    }

    // ---- date resolution -------------------------------------------------
    // Direct port. .NET regex takes the same syntax, so these are the Python
    // patterns unchanged apart from RegexOptions.

    public static class DateParser
    {
        const string CenturyWords = @"century|centuries|si[eè]cle|secolo|siglo|Jahrhundert|eeuw";

        static readonly Regex Year    = new Regex(@"\b(1[0-9]{3}|20[0-2][0-9])\b");
        static readonly Regex Iso     = new Regex(@"\b(1\d{3}|20\d{2})-\d{2}(?:-\d{2})?\b");
        static readonly Regex Between = new Regex(
            @"\b(?:between|entre|zwischen|tra)\s+(\d{3,4})\s+(?:and|et|und|e)\s+(\d{3,4})\b",
            RegexOptions.IgnoreCase);
        static readonly Regex Range   = new Regex(@"\b(1\d{3}|20\d{2})\s*[-–—/]\s*(1\d{3}|20\d{2})\b");
        static readonly Regex Circa   = new Regex(
            @"\b(?:c|ca|circa|about|around|approx|vers|environ|um|gegen|hacia|verso)\.?\s*(1\d{3}|20\d{2})\b",
            RegexOptions.IgnoreCase);
        static readonly Regex Decade  = new Regex(@"\b(1\d{2}0|20[0-2]0)s\b");
        static readonly Regex CentNum = new Regex(
            $@"\b(\d{{1,2}})\s*(?:st|nd|rd|th|e|è?me|er|\.)?[\s-]*(?:{CenturyWords})",
            RegexOptions.IgnoreCase);
        static readonly Regex CentRom = new Regex(
            $@"\b([ivxIVX]{{1,5}})\s*(?:e|è?me|er|\.)?[\s-]*(?:{CenturyWords})");

        static readonly Dictionary<string, int> Roman = new Dictionary<string, int>
        {
            {"i",1},{"ii",2},{"iii",3},{"iv",4},{"v",5},{"vi",6},{"vii",7},{"viii",8},
            {"ix",9},{"x",10},{"xi",11},{"xii",12},{"xiii",13},{"xiv",14},{"xv",15},
            {"xvi",16},{"xvii",17},{"xviii",18},{"xix",19},{"xx",20},{"xxi",21},
        };

        public struct Parsed
        {
            public int? Begin, End;
            public float Confidence;
            public List<int> Years;
        }

        public static List<int> YearsIn(string text)
        {
            var outp = new List<int>();
            if (string.IsNullOrEmpty(text)) return outp;
            foreach (Match m in Year.Matches(text)) outp.Add(int.Parse(m.Groups[1].Value));
            return outp;
        }

        public static Parsed Parse(string text)
        {
            var none = new Parsed { Years = new List<int>() };
            if (string.IsNullOrWhiteSpace(text)) return none;
            string t = Regex.Replace(text, @"\s+", " ").Trim();

            Match m;
            if ((m = Iso.Match(t)).Success)
            {
                int y = int.Parse(m.Groups[1].Value);
                return Make(y, y, 1.0f, t);
            }
            if ((m = Between.Match(t)).Success || (m = Range.Match(t)).Success)
            {
                int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
                if (a > b) (a, b) = (b, a);
                return Make(a, b, b - a <= 50 ? 0.9f : 0.6f, t);
            }
            if ((m = Circa.Match(t)).Success)
            {
                int y = int.Parse(m.Groups[1].Value);
                return Make(y - 5, y + 5, 0.75f, t);
            }
            if ((m = Decade.Match(t)).Success)
            {
                int d = int.Parse(m.Groups[1].Value);
                return Make(d, d + 9, 0.7f, t);
            }
            if ((m = CentNum.Match(t)).Success)
            {
                int n = int.Parse(m.Groups[1].Value);
                if (n >= 1 && n <= 21) return Make((n - 1) * 100 + 1, n * 100, 0.5f, t);
            }
            if ((m = CentRom.Match(t)).Success &&
                Roman.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out int rn))
                return Make((rn - 1) * 100 + 1, rn * 100, 0.5f, t);

            var ys = YearsIn(t);
            if (ys.Count > 0)
                return Make(ys.Min(), ys.Max(), ys.Count == 1 ? 0.85f : 0.35f, t);

            return none;
        }

        static Parsed Make(int b, int e, float conf, string src) => new Parsed
        {
            Begin = b, End = e, Confidence = conf, Years = YearsIn(src)
        };

        /// <summary>First candidate that parses wins; trust scales confidence.</summary>
        public static (Parsed parsed, string basis) Resolve(
            params (string basis, string text, float trust)[] candidates)
        {
            foreach (var c in candidates)
            {
                var p = Parse(c.text);
                if (p.Begin.HasValue)
                {
                    p.Confidence = Mathf.Round(p.Confidence * c.trust * 1000f) / 1000f;
                    return (p, c.basis);
                }
            }
            return (new Parsed { Years = new List<int>() }, "none");
        }
    }

    // ---- provider: Wikimedia Commons ------------------------------------

    public class CommonsProvider
    {
        const string Api = "https://commons.wikimedia.org/w/api.php";

        // Wikimedia rate-limits placeholder agents. Put a real contact here.
        public static string UserAgent = "xr-investigation-probe/1.0 (you@yourdomain.com)";

        public async Task<List<Card>> Fetch(string subject, int y0, int y1, int limit)
        {
            // Both queries at once: one requiring an era term (this is what
            // surfaces period material), one broad. Parenthesise the OR -
            // without it CirrusSearch parses the year clause as unconstrained.
            string era = "(" + string.Join(" OR ", EraTerms(y0, y1)) + ")";
            var searches = new[]
            {
                $"filetype:bitmap \"{subject}\" {era}",
                $"filetype:bitmap \"{subject}\"",
            };

            var batches = await Task.WhenAll(searches.Select(s => Query(s, Mathf.Min(limit * 4, 50))));

            var seen = new HashSet<string>();
            var cards = new List<Card>();
            foreach (var pages in batches)
            {
                if (pages == null) continue;
                foreach (var page in pages.Properties().Select(p => p.Value))
                {
                    var info = page["imageinfo"]?.FirstOrDefault();
                    var meta = info?["extmetadata"];
                    if (info == null) continue;

                    string url = (string)info["url"];
                    if (string.IsNullOrEmpty(url) || !seen.Add(url)) continue;

                    string M(string key) => Strip((string)meta?[key]?["value"] ?? "");

                    string title = Strip((string)page["title"] ?? "").Replace("File:", "");
                    string desc = M("ImageDescription");
                    string cats = M("Categories").Replace("|", " . ");

                    // Categories outrank the date field: Commons categorises
                    // far more consistently than it fills DateTimeOriginal.
                    // Never DateTime - that is the upload timestamp.
                    var (p, basis) = DateParser.Resolve(
                        ("date-field", M("DateTimeOriginal"), 1.0f),
                        ("category", cats, 0.95f),
                        ("filename", title, 0.7f),
                        ("description", desc, 0.6f));

                    cards.Add(new Card
                    {
                        kind = "image",
                        title = title,
                        source = "commons",
                        body = desc.Length > 600 ? desc.Substring(0, 600) : desc,
                        ImageUrl = url,
                        ThumbUrl = (string)info["thumburl"] ?? url,
                        YearBegin = p.Begin,
                        YearEnd = p.End,
                        DateConfidence = p.Confidence,
                        DateBasis = basis,
                        credit = M("Artist"),
                        license = M("LicenseShortName"),
                        SourceUrl = (string)info["descriptionurl"] ?? "",
                        // thumbwidth/height when a thumb was rendered,
                        // otherwise the original dimensions.
                        ThumbW = (int?)info["thumbwidth"] ?? (int?)info["width"] ?? 0,
                        ThumbH = (int?)info["thumbheight"] ?? (int?)info["height"] ?? 0,
                    });
                }
            }
            return cards;
        }

        static IEnumerable<string> EraTerms(int y0, int y1)
        {
            if (y1 - y0 > 40)
                return Enumerable.Range(y0, y1 - y0 + 1)
                    .Select(y => $"{y / 10 * 10}s").Distinct();
            return Enumerable.Range(y0, y1 - y0 + 1).Select(y => y.ToString());
        }

        async Task<JObject> Query(string search, int n)
        {
            string url = $"{Api}?action=query&format=json&generator=search"
                       + $"&gsrsearch={UnityWebRequest.EscapeURL(search)}"
                       + $"&gsrnamespace=6&gsrlimit={Mathf.Min(n, 50)}"
                       + "&prop=imageinfo&iiprop=url|extmetadata|size&iiurlwidth=" + WebGate.CardWidth + ""
                       + "&iiextmetadatalanguage=en";

            using (var req = await WebGate.Send(url, System.Threading.CancellationToken.None, 15, "commons"))
            {
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[commons] {req.error}");
                    return null;
                }
                try
                {
                    return (JObject)JObject.Parse(req.downloadHandler.text)["query"]?["pages"];
                }
                catch { return null; }
            }
        }

        static string Strip(string s) =>
            string.IsNullOrEmpty(s) ? "" :
            Regex.Replace(Regex.Replace(s, "<[^>]+>", " "), @"\s+", " ").Trim();
    }
}
