// Keeps gathering material on the subject in the background, well beyond the
// cards on screen, so there is a pile to show (HarvestWall) and to steer.
//
// It starts with each search and crawls outward:
//
//   hop 0  the subject: its article's links and backlinks, its Wikidata
//          statements, Wikipedia search, Commons pictures and videos,
//          Internet Archive, Europeana, Openverse - every page of them
//   hop 1  what those found: each item's own links and statements, a page
//          of Commons pictures of it
//   hop 2  what those found: kept, not crawled further
//
// plus everything the timeline itself shows. Each request returns up to 50
// items, so a polite pace (WebGate keeps it to a few at a time per host)
// still brings in thousands a minute. Everything lands in the disk cache,
// which also makes the rows you open later instant.
//
// Items without a date of their own take the year of the item they were
// found from (inferred = true), so everything has a place in time.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SpatialForms;
using UnityEngine;
using UnityEngine.Networking;
using XrProbe;

namespace TimelineVR.Interaction
{
    public class HarvestItem
    {
        public string key;
        public Result result;
        public int year;             // where it sits in time
        public bool inferred;        // year taken from what it was found from
        public int hop;              // 0 = the timeline itself, 1 = found from the subject, 2 = one step further
        public string source;        // which feed found it
        public string thumb;         // picture, or null
        public string title;
        public int order;            // arrival order
    }

    [DefaultExecutionOrder(7)]
    public class Harvester : MonoBehaviour
    {
        [SerializeField] private SFManager _search;
        [Tooltip("Stop gathering after this many items.")]
        [SerializeField] private int _maxItems = 1000;
        [Tooltip("Requests in flight at once from the harvester (WebGate still limits per host).")]
        [SerializeField, Range(1, 6)] private int _workers = 3;
        [Tooltip("Pause after each request per worker, seconds: keeps the foreground rows quick.")]
        [SerializeField] private float _pause = 0.15f;
        [Tooltip("How far it crawls: 1 = only what the subject links to, 2 = one step further.")]
        [SerializeField, Range(1, 2)] private int _maxHop = 2;
        [Tooltip("Items found from the subject that get crawled themselves (hop 1 nodes).")]
        [SerializeField] private int _maxNodes = 400;

        public string subject { get; private set; }
        public IReadOnlyList<HarvestItem> items => _items;
        public int count => _items.Count;
        public int sourceCount => _sources.Count;
        public bool running { get; private set; }
        public string status { get; private set; } = "";
        public int yearFrom { get; private set; }
        public int yearTo { get; private set; }
        public event Action<HarvestItem> Added;
        public event Action Reset;

        private readonly List<HarvestItem> _items = new List<HarvestItem>();
        private readonly HashSet<string> _keys = new HashSet<string>();
        private readonly HashSet<string> _sources = new HashSet<string>();
        private readonly HashSet<string> _expanded = new HashSet<string>();
        private readonly List<Job> _jobs = new List<Job>();
        private readonly WikidataRelated _wikidata = new WikidataRelated();
        private CancellationTokenSource _cts;
        private SFDocument _doc;
        private int _nodes, _busy, _seq;

        private class Job
        {
            public Pager pager;
            public int hop;          // hop of what it finds
            public int year;         // year to give undated finds
            public float priority;   // lower first
            public bool again;       // re-queue for its next page
            public int pages;
        }

        private void Awake()
        {
            if (_search == null) _search = FindFirstObjectByType<SFManager>();
            if (_search != null) _search.SearchStarted += OnSearchStarted;
        }

        private void OnDestroy()
        {
            if (_search != null) _search.SearchStarted -= OnSearchStarted;
            if (_doc != null) _doc.ElementAdded -= OnTimelineElement;
            _cts?.Cancel();
        }

        private void OnSearchStarted(string s, int y0, int y1)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _items.Clear(); _keys.Clear(); _sources.Clear(); _expanded.Clear(); _jobs.Clear();
            _nodes = 0; _busy = 0; _seq = 0;
            subject = s;
            yearFrom = Mathf.Min(y0, y1);
            yearTo = Mathf.Max(y0, y1);
            Reset?.Invoke();

            if (_doc != null) _doc.ElementAdded -= OnTimelineElement;
            _doc = _search.document;
            if (_doc != null)
            {
                _doc.ElementAdded += OnTimelineElement;
                foreach (var e in _doc.elements) OnTimelineElement(e);
            }
            _ = Run(s, _cts.Token);
        }

        // The timeline's own results are part of the pile (hop 0).
        private void OnTimelineElement(SFElement e)
        {
            if (e?.data == null || e.row != 0) return;
            var c = e.data.text ?? e.data.image;
            int year = e.data.YearBegin ?? (yearFrom + yearTo) / 2;
            Add(e.data, c?.title ?? e.data.label, year, false, 0, "timeline",
                e.data.image?.ThumbUrl ?? e.data.image?.ImageUrl, "t:" + Norm(c?.title) + "|" + (c?.SourceUrl ?? ""));
        }

        // ---- crawling ----------------------------------------------------------

        private async Task Run(string s, CancellationToken ct)
        {
            running = true;
            try
            {
                int mid = (yearFrom + yearTo) / 2;
                status = "finding " + s;
                var (title, qid) = await Resolve(s, ct);
                ct.ThrowIfCancellationRequested();

                // The subject's own feeds: every page, interleaved with the crawl.
                if (title != null)
                {
                    Queue(MoreSources.ArticleLinks(title, 30), 1, mid, 0f, true);
                    Queue(MoreSources.Backlinks(title, 12), 1, mid, 3f, true);
                }
                if (qid != null) Queue(MoreSources.Statements(qid, title ?? s), 1, mid, 0.5f, true);
                Queue(MoreSources.Commons(s, false, 20), 1, mid, 1f, true);
                Queue(MoreSources.Archive(s, mid, 12), 1, mid, 1.5f, true);
                Queue(MoreSources.Europeana(s, mid, 10), 1, mid, 2f, true);
                Queue(MoreSources.Commons(s, true, 8), 1, mid, 2.5f, true);
                Queue(MoreSources.WikiSearch(s, title, 10), 1, mid, 2f, true);
                Queue(MoreSources.Openverse(s, 6), 1, mid, 4f, true);

                var workers = Enumerable.Range(0, _workers).Select(_ => Worker(ct)).ToArray();
                await Task.WhenAll(workers);
                status = _items.Count >= _maxItems ? "full" : "done";
                Debug.Log($"[harvest] {s}: {_items.Count} items from {_sources.Count} sources");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[harvest] {e.Message}"); status = "stopped"; }
            finally { if (!ct.IsCancellationRequested) running = false; }
        }

        private void Queue(Pager p, int hop, int year, float priority, bool again) =>
            _jobs.Add(new Job { pager = p, hop = hop, year = year, priority = priority, again = again });

        private async Task Worker(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _items.Count < _maxItems)
            {
                if (_jobs.Count == 0)
                {
                    if (_busy == 0) return;                 // nothing left anywhere
                    await Task.Delay(200, ct);
                    continue;
                }
                var job = _jobs[0];
                foreach (var j in _jobs) if (j.priority < job.priority) job = j;
                _jobs.Remove(job);
                _busy++;
                try
                {
                    status = job.pager.name;
                    var found = await job.pager.Next(ct);
                    job.pages++;
                    if (found.Count > 0)
                    {
                        try { await _wikidata.FillImages(found.Where(i => i.image == null && i.article != null).ToList(), ct); }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                        foreach (var it in found) AddFound(it, job);
                    }
                    // Its next page, a little later than the crawl it fed.
                    if (job.again && !job.pager.Done) { job.priority += 6f; _jobs.Add(job); }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Debug.LogWarning($"[harvest] {job.pager.name}: {e.Message}"); }
                finally { _busy--; }
                if (_pause > 0f) await Task.Delay(TimeSpan.FromSeconds(_pause), ct);
            }
        }

        private void AddFound(RelatedItem it, Job job)
        {
            if (string.IsNullOrEmpty(it.label)) return;
            bool dated = it.yearMin != int.MaxValue;
            int year = dated ? it.dateYear : job.year;
            bool inferred = !dated;
            if (!dated)
            {
                // Wikipedia descriptions often carry the years: "American astronaut (1930–2012)".
                var p = DateParser.Parse(it.description ?? "");
                if (p.Begin.HasValue && p.Begin.Value > 0 && p.Begin.Value <= DateTime.Now.Year) { year = p.Begin.Value; inferred = false; }
            }
            var r = WikidataRelated.ToResult(it);
            string key = it.qid != null && !it.qid.StartsWith("wp:") ? "id:" + it.qid : "t:" + Norm(it.label);
            var h = Add(r, it.label, year, inferred, job.hop, job.pager.name, r.image?.ThumbUrl ?? r.image?.ImageUrl ?? it.image, key);
            if (h == null) return;

            // Crawl it in turn.
            if (job.hop < _maxHop && _nodes < _maxNodes)
            {
                string title = TitleOf(it.article);
                bool any = false;
                float pr = 10f + h.order * 0.02f + (it.image == null ? 2f : 0f);
                if (title != null && _expanded.Add("a:" + title)) { Queue(MoreSources.ArticleLinks(title, 1), job.hop + 1, year, pr, false); any = true; }
                if (it.qid != null && it.qid.StartsWith("Q") && _expanded.Add("q:" + it.qid)) { Queue(MoreSources.Statements(it.qid, it.label), job.hop + 1, year, pr + 0.5f, false); any = true; }
                if (it.source == null && title != null && _nodes < _maxNodes / 3) Queue(MoreSources.Commons(MoreSources.TermOf(r.text), false, 1), job.hop + 1, year, pr + 4f, false);
                if (any) _nodes++;
            }
        }

        private HarvestItem Add(Result r, string title, int year, bool inferred, int hop, string source, string thumb, string key)
        {
            if (r == null || _items.Count >= _maxItems) return null;
            string tk = "t:" + Norm(title);
            if (string.IsNullOrEmpty(key) || !_keys.Add(key)) return null;
            if (tk != "t:" && tk != key && !_keys.Add(tk)) return null;
            var h = new HarvestItem
            {
                key = key, result = r, title = title, year = year, inferred = inferred, hop = hop,
                source = source, thumb = string.IsNullOrEmpty(thumb) ? null : thumb, order = _seq++,
            };
            _items.Add(h);
            _sources.Add(source);
            Added?.Invoke(h);
            return h;
        }

        // The subject's Wikipedia article and Wikidata item, by search.
        private static async Task<(string title, string qid)> Resolve(string s, CancellationToken ct)
        {
            string url = "https://en.wikipedia.org/w/api.php?action=query&format=json&redirects=1&generator=search&gsrlimit=1"
                       + "&prop=pageprops&ppprop=wikibase_item&gsrsearch=" + UnityWebRequest.EscapeURL(s);
            var j = await WebCache.GetJson(url, ct, 20, "harvest");
            var pages = j?["query"]?["pages"] as Newtonsoft.Json.Linq.JObject;
            var pg = pages?.Properties().Select(p => p.Value).FirstOrDefault();
            if (pg == null) return (null, null);
            return ((string)pg["title"], (string)pg["pageprops"]?["wikibase_item"]);
        }

        private static string TitleOf(string article)
        {
            if (string.IsNullOrEmpty(article)) return null;
            int i = article.IndexOf("/wiki/", StringComparison.Ordinal);
            if (i < 0) return article;
            return UnityWebRequest.UnEscapeURL(article.Substring(i + 6)).Replace('_', ' ');
        }

        private static string Norm(string s) =>
            new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
