// The orchestrator - this is what calls CommonsProvider.Fetch and friends.
// Port of investigate() from probe.py: fan out, score on two axes, rank,
// pair into results. Ranks, never filters: on sources this sparsely dated,
// stacked filters turn an 80%-good result set into an empty one.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace XrProbe
{
    public class LocalProbe : IProbeSource
    {
        readonly CommonsProvider _commons = new CommonsProvider();
        readonly WikipediaProvider _wikipedia = new WikipediaProvider();
        // Add the rest here as they are ported; each is one line.
        // readonly EuropeanaProvider _europeana = new EuropeanaProvider();
        // readonly NewsProvider     _news      = new NewsProvider();

        public async Task<ProbeResult> Search(string subject, int y0, int y1,
                                              int top, bool reel)
        {
            int perSource = reel ? 60 : 8;
            if (reel) top = Mathf.Max(top, 250);

            var started = Time.realtimeSinceStartup;

            // Fan out. A slow or failing source delays nothing else - each
            // task swallows its own exception and returns an empty list.
            var jobs = new Dictionary<string, Task<List<Card>>>
            {
                ["commons"] = Guard("commons", () => _commons.Fetch(subject, y0, y1, perSource)),
                ["wikipedia"] = Guard("wikipedia", () => _wikipedia.Fetch(subject, y0, y1, perSource)),
                ["archive"] = Guard("archive", () => TimelineSources.Archive(subject, y0, y1, perSource)),
            };
            var linked = WikidataGuard(subject, y0, y1, perSource, default);
            await Task.WhenAll(jobs.Values);

            var retrieved = jobs.Values.SelectMany(t => t.Result).ToList();

            // Scoring and ranking are pure C# - keep them off the frame.
            var result = await Task.Run(() =>
                Rank(retrieved, subject, y0, y1, top));

            result.results.AddRange(await linked);
            result.query = new Query { subject = subject, YearFrom = y0, YearTo = y1 };
            result.Timing = new Dictionary<string, int>
            {
                ["total"] = Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f)
            };
            return result;
        }

        // Progressive search implementation. Emits results via onResult as
        // providers finish and intermediate ranking completes.
        public async Task Search(string subject, int y0, int y1,
                                 int top, bool reel, Action<Result> onResult, System.Threading.CancellationToken cancellationToken = default)
        {
            int perSource = reel ? 60 : 8;
            if (reel) top = Mathf.Max(top, 250);

            var started = Time.realtimeSinceStartup;

            var jobs = new Dictionary<string, Task<List<Card>>>
            {
                ["commons"] = Guard("commons", () => _commons.Fetch(subject, y0, y1, perSource)),
                ["wikipedia"] = Guard("wikipedia", () => _wikipedia.Fetch(subject, y0, y1, perSource)),
                ["archive"] = Guard("archive", () => TimelineSources.Archive(subject, y0, y1, perSource)),
            };

            var retrieved = new List<Card>();
            var emitted = new HashSet<string>();
            var locker = new object();
            // Limit total emitted results to the requested top to avoid
            // producing more results than requested during progressive updates.
            int emittedCount = 0;

            static string CardId(Card c)
            {
                if (c == null) return "";
                // Prefer stable URL-like identifiers when available.
                if (!string.IsNullOrEmpty(c.ImageUrl)) return c.ImageUrl;
                if (!string.IsNullOrEmpty(c.ThumbUrl)) return c.ThumbUrl;
                if (!string.IsNullOrEmpty(c.SourceUrl)) return c.SourceUrl;
                if (!string.IsNullOrEmpty(c.title)) return c.title;
                return "";
            }

            static string ResultKey(Result result)
            {
                var img = result.image != null ? CardId(result.image) : "";
                var txt = result.text != null ? CardId(result.text) : "";
                return img + "|" + txt + "|" + (result.label ?? "");
            }

            // For each job, attach a continuation to process partial results.
            // They run on the thread pool: kept, so the final pass waits for them.
            var merges = new List<Task>();
            foreach (var kv in jobs)
            {
                var task = kv.Value;
                merges.Add(task.ContinueWith(t =>
                {
                    List<Card> cards = t.IsCompletedSuccessfully ? t.Result : new List<Card>();
                    lock (locker)
                    {
                        // Track IDs for newly arrived cards so we only emit results
                        // that involve at least one new card from this provider.
                        var newIds = new HashSet<string>(cards.Select(c => CardId(c)));

                        retrieved.AddRange(cards);
                        var partial = Rank(new List<Card>(retrieved), subject, y0, y1, top);
                        // Emit any new results not yet emitted that include at least
                        // one of the newly arrived cards.
                        foreach (var result in partial.results)
                        {
                            if (cancellationToken.IsCancellationRequested) return;

                            var resultImgId = result.image != null ? CardId(result.image) : "";
                            var resultTxtId = result.text != null ? CardId(result.text) : "";
                            if (!newIds.Contains(resultImgId) && !newIds.Contains(resultTxtId))
                                continue;

                            string key = ResultKey(result);
                            if (!emitted.Contains(key))
                            {
                                emitted.Add(key);
                                try { onResult?.Invoke(result); } catch { }
                            }
                        }
                    }
                }));
            }

            // Linked Wikidata items come already paired and dated: straight out.
            async Task EmitLinked()
            {
                var list = await WikidataGuard(subject, y0, y1, perSource, cancellationToken);
                lock (locker)
                {
                    foreach (var result in list)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        if (emitted.Add("wd|" + ResultKey(result)))
                            try { onResult?.Invoke(result); } catch { }
                    }
                }
            }
            var linkedTask = EmitLinked();

            await Task.WhenAll(jobs.Values);
            await Task.WhenAll(merges);       // every source merged before the final pass
            await linkedTask;

            // Final pass to ensure any remaining results are emitted (respect cap and use stable keys)
            lock (locker)
            {
                var final = Rank(new List<Card>(retrieved), subject, y0, y1, top);
                foreach (var result in final.results)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (emittedCount >= top) break;
                    string key = ResultKey(result);
                    if (!emitted.Contains(key))
                    {
                        emitted.Add(key);
                        emittedCount++;
                        try { onResult?.Invoke(result); } catch { }
                        if (emittedCount >= top) break;
                    }
                }
            }

            // Optionally record timing somewhere; caller doesn't use it here.
            await Task.CompletedTask;
        }

        static async Task<List<Result>> WikidataGuard(string subject, int y0, int y1, int perSource, System.Threading.CancellationToken ct)
        {
            try { return await TimelineSources.Wikidata(subject, y0, y1, perSource * 2, ct); }
            catch (OperationCanceledException) { return new List<Result>(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[wikidata] timeline: {e.Message}");
                return new List<Result>();
            }
        }

        static Task<List<Card>> Guard(string name, Func<Task<List<Card>>> fn)
        {
            return fn().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Debug.LogWarning($"[{name}] {t.Exception?.GetBaseException().Message}");
                    return new List<Card>();
                }
                return t.Result ?? new List<Card>();
            });
        }

        // ---- ranking -----------------------------------------------------

        static ProbeResult Rank(List<Card> retrieved, string subject,
                                int y0, int y1, int top)
        {
            float floor = Scoring.RelevanceFloor(subject);
            int width = Mathf.Max(1, y1 - y0 + 1);

            var scored = new List<Card>();
            var undated = new List<Card>();

            foreach (var c in retrieved)
            {
                c.SubjectScore = Scoring.Relevance(c, subject);

                // A card listing several years is several mentions, not one
                // interval. Narrow the label to years inside the window.
                if (c.Years != null && c.Years.Count > 1)
                {
                    var hits = c.Years.Where(y => y >= y0 && y <= y1).ToList();
                    if (hits.Count > 0) { c.YearBegin = hits.Min(); c.YearEnd = hits.Max(); }
                }

                c.DateScore = Scoring.CardDate(c, y0, y1);

                if (c.YearBegin.HasValue)
                {
                    // Normalise a reversed span rather than dividing by zero.
                    int lo = c.YearBegin.Value, hi = c.YearEnd ?? c.YearBegin.Value;
                    if (hi < lo) (lo, hi) = (hi, lo);
                    c.YearBegin = lo; c.YearEnd = hi;

                    // A span far wider than the window locates nothing in
                    // time - fold that into confidence so it reads as vague
                    // rather than posing as a firm date.
                    float precision = Mathf.Min(1f, (float)width / Mathf.Max(1, hi - lo + 1));
                    c.DateConfidence *= 0.4f + 0.6f * precision;
                }

                float k = c.DateConfidence;
                float dated = c.DateScore * k + 0.25f * (1f - k);
                c.score = 0.5f * c.SubjectScore + 0.5f * dated;

                if (!c.YearBegin.HasValue && !c.YearEnd.HasValue) undated.Add(c);
                else scored.Add(c);
            }

            scored.Sort((a, b) => b.score.CompareTo(a.score));
            undated.Sort((a, b) => b.SubjectScore.CompareTo(a.SubjectScore));
            scored.AddRange(undated);                 // last, never interleaved

            scored = CapPerCollection(scored, Mathf.Max(3, top / 3), top * 2);

            var images = scored.Where(c => c.kind == "image").Take(top).ToList();
            var texts  = scored.Where(c => c.kind == "text").Take(top).ToList();

            return new ProbeResult
            {
                images = images,
                text = texts,
                results = BuildResultList(images, texts, Mathf.Max(5, (y1 - y0) / 4)),
            };
        }

        /// <summary>
        /// A scanned archive is one file per page, all sharing a title, and
        /// without a cap one collection eats the whole result set. But the cap
        /// is for diversity, not for shrinking output: overflow is held back
        /// and added again if diversifying left us short.
        /// </summary>
        static List<Card> CapPerCollection(List<Card> cards, int per, int target)
        {
            var seen = new Dictionary<string, int>();
            var kept = new List<Card>();
            var spill = new List<Card>();

            foreach (var c in cards)
            {
                var stem = string.Join(" ", Scoring.Tokens(c.title).OrderBy(t => t).Take(4));
                seen.TryGetValue(stem, out int n);
                seen[stem] = ++n;
                (n <= per ? kept : spill).Add(c);
            }
            if (kept.Count < target)
                kept.AddRange(spill.Take(target - kept.Count));
            return kept;
        }

        // ---- pairing -----------------------------------------------------

        static int Mid(Card c) => (c.YearBegin.Value + (c.YearEnd ?? c.YearBegin.Value)) / 2;

        /// <summary>
        /// Walk both sequences in date order like a merge. Where one side has
        /// no partner within tolerance the result carries a single cell - an
        /// image beside prose from eighty years later would imply a
        /// connection that is not there.
        /// </summary>
        static List<Result> BuildResultList(List<Card> images, List<Card> texts, int tolerance)
        {
            var di = images.Where(c => c.YearBegin.HasValue).OrderBy(Mid).ToList();
            var dt = texts.Where(c => c.YearBegin.HasValue).OrderBy(Mid).ToList();
            var ui = images.Where(c => !c.YearBegin.HasValue).ToList();
            var ut = texts.Where(c => !c.YearBegin.HasValue).ToList();

            var results = new List<Result>();
            int i = 0, t = 0;

            while (i < di.Count && t < dt.Count)
            {
                Card a = di[i], b = dt[t];
                bool overlap = a.YearBegin <= (b.YearEnd ?? b.YearBegin)
                            && b.YearBegin <= (a.YearEnd ?? a.YearBegin);

                if (overlap || Mathf.Abs(Mid(a) - Mid(b)) <= tolerance)
                { results.Add(MakeResult(a, b)); i++; t++; }
                else if (Mid(a) < Mid(b)) { results.Add(MakeResult(a, null)); i++; }
                else { results.Add(MakeResult(null, b)); t++; }
            }
            for (; i < di.Count; i++) results.Add(MakeResult(di[i], null));
            for (; t < dt.Count; t++) results.Add(MakeResult(null, dt[t]));

            for (int n = 0; n < Mathf.Max(ui.Count, ut.Count); n++)
                results.Add(MakeResult(n < ui.Count ? ui[n] : null,
                                 n < ut.Count ? ut[n] : null));
            return results;
        }

        static Result MakeResult(Card img, Card txt)
        {
            var ys = new List<int>();
            foreach (var c in new[] { img, txt })
            {
                if (c == null) continue;
                if (c.YearBegin.HasValue) ys.Add(c.YearBegin.Value);
                if (c.YearEnd.HasValue) ys.Add(c.YearEnd.Value);
            }
            var confs = new[] { img, txt }.Where(c => c != null)
                                          .Select(c => c.DateConfidence).ToList();

            return new Result
            {
                image = img,
                text = txt,
                YearBegin = ys.Count > 0 ? ys.Min() : (int?)null,
                YearEnd = ys.Count > 0 ? ys.Max() : (int?)null,
                label = ys.Count == 0 ? "undated"
                      : ys.Min() == ys.Max() ? ys.Min().ToString()
                      : $"{ys.Min()}\u2013{ys.Max()}",
                // A result is only as trustworthy as its weakest date.
                DateConfidence = confs.Count > 0 ? confs.Min() : 0f,
            };
        }
    }

    // ---- scoring helpers -------------------------------------------------

    public static class Scoring
    {
        static readonly HashSet<string> Stop = new HashSet<string>
        { "the", "a", "an", "of", "in", "on", "at", "and", "or", "de", "la" };

        public static HashSet<string> Tokens(string s)
        {
            var outp = new HashSet<string>();
            if (string.IsNullOrEmpty(s)) return outp;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), "[a-z0-9]+"))
                if (m.Value.Length > 2 && !Stop.Contains(m.Value)) outp.Add(m.Value);
            return outp;
        }

        /// <summary>Short subjects must match every word; long ones ease off.</summary>
        public static float RelevanceFloor(string subject) =>
            Tokens(subject).Count <= 3 ? 1f : 0.6f;

        /// <summary>
        /// Fraction of the subject's words found in the card's own text.
        /// Date and subject relevance are different questions - a 1965 truck
        /// sits perfectly inside a 1960-1980 window and is still not Trump.
        /// </summary>
        public static float Relevance(Card c, string subject)
        {
            var want = Tokens(subject);
            if (want.Count == 0) return 1f;
            var have = Tokens(!string.IsNullOrEmpty(c.Haystack)
                ? c.Haystack
                : $"{c.title} {c.body} {string.Join(" ", c.keywords ?? new List<string>())}");
            return (float)want.Count(w => have.Contains(w)) / want.Count;
        }

        public static float DateScore(int? begin, int? end, int y0, int y1)
        {
            if (!begin.HasValue && !end.HasValue) return 0.25f;
            int b = begin ?? end.Value, e = end ?? begin.Value;
            if (e < b) (b, e) = (e, b);

            int overlap = Mathf.Min(e, y1) - Mathf.Max(b, y0);
            if (overlap >= 0)
            {
                int own = Mathf.Max(e - b, 1);
                return Mathf.Min(1f, 0.6f + 0.4f * (Mathf.Min(overlap + 1, own) / (float)own));
            }
            int distance = e < y0 ? y0 - e : b - y1;
            return Mathf.Max(0f, 0.5f - distance / 100f);
        }

        /// <summary>
        /// Prose gets scored by its best-matching year, not its full span: a
        /// paragraph citing 1601 and 2014 is not a 17th-century paragraph.
        /// </summary>
        public static float CardDate(Card c, int y0, int y1)
        {
            if (c.Years == null || c.Years.Count == 0)
                return DateScore(c.YearBegin, c.YearEnd, y0, y1);

            float best = c.Years.Max(y => DateScore(y, y, y0, y1));
            float density = c.Years.Count(y => y >= y0 && y <= y1) / (float)c.Years.Count;
            return 0.7f * best + 0.3f * density;
        }
    }
}
