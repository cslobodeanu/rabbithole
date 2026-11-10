// The vertical axis of the grid, as a "snake": from the card you rest on, a
// row of related cards opens below; that row is a small timeline of its own
// (scroll it sideways); rest on one of its cards and the next row opens below
// that, and so on, as deep as you like.
//
//   [1954] [1956] [1957] [1960]                 <- the search's timeline (row 0)
//                 |
//     [Graceland] [Priscilla] [Aladdin] ...     <- from the 1967 wedding card
//                 |
//       [1945] [1967] [1973] [1985] ...         <- from Priscilla: her own things
//                        |
//                   [Dallas] [Naked Gun] ...    <- from 1985 ... and on
//
//   down    move to the row below (it opens as a preview while you rest)
//   up      back, one step along the way you came
//   sideways   along the row you are on; the rows below it were for the
//              previous card, so they close and the new card's row opens
//
// The rows you came through stay above, with everything except your path
// dimmed - the snake is visible.
//
// A row from a card holds, left to right: what that card mentions (its
// Wikipedia paragraph, or for a related card the lead of its own article),
// then things linked to it on Wikidata near its year, in date order. The
// mentions arrive fast (Wikipedia); the Wikidata part is added when it comes
// (the query service may be rate-limited), without moving what you look at.
//
// Rows never end: scrolling towards the end of a row fetches the next page
// from more sources in turn (the item's own statements, the article's other
// links, Commons pictures and videos, Internet Archive, other years on
// Wikidata, Europeana, Wikipedia search, Openverse, links here), skipping
// anything already on the path. Every few pages, and when real sources run
// out, the local model adds a deduction - a cloudy card written as you watch.
// D deduces from the card in focus, into the row below it.
//
// They are ordinary panels: keep them, open them, search from them.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SpatialForms;
using UnityEngine;
using XrProbe;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(5)]
    public class RelatedColumn : MonoBehaviour
    {
        [SerializeField] private GridInteraction _grid;
        [SerializeField] private SFDocumentRender _document;

        [Tooltip("Most 'mentioned' cards at the start of a row.")]
        [SerializeField] private int _maxMentions = 8;

        [Tooltip("Most time-ordered cards after them.")]
        [SerializeField] private int _maxAround = 24;

        [Tooltip("How far from the card's year a related item may be dated.")]
        [SerializeField] private int _halfWindow = 15;

        [Tooltip("Rest on a card this long before the row below it opens.")]
        [SerializeField] private float _dwell = 0.35f;

        [Tooltip("Cards off your path, in the rows above you.")]
        [SerializeField, Range(0f, 1f)] private float _offPathAlpha = 0.35f;

        [Header("Row scrolling")]
        [SerializeField] private float _damping = 4f;
        [Tooltip("Below this speed (cards per second) a row settles on a card.")]
        [SerializeField] private float _settleBelow = 0.8f;
        [SerializeField] private float _settleStiffness = 10f;

        [Header("Endless rows")]
        [Tooltip("Fetch the next page when the focus is this many cards from the end of a row.")]
        [SerializeField] private int _loadAhead = 6;
        [Tooltip("A deduction after every this many pages of real results (0: only when they run out).")]
        [SerializeField] private int _deduceEvery = 3;
        [Tooltip("Deduce from the card in focus, into the row below it.")]
        [SerializeField] private KeyCode _deduceKey = KeyCode.D;
        [Tooltip("Europeana API key; the public demo key is fine for prototyping.")]
        [SerializeField] private string _europeanaKey = "api2demo";

        private Deducer _deducer;
        private SFElementRender _prefetched;

        private SFManager _search;
        private SFTimelineContentGenerator _query;
        private readonly WikidataRelated _wikidata = new WikidataRelated();
        private readonly WikipediaMentions _mentions = new WikipediaMentions();

        // One step of the snake: a row opened from a card of the row above.
        private class Level
        {
            public int row;
            public SFElementRender parent;      // the card it was opened from
            public SFElement column;            // timeline element all rows line up under
            public readonly List<SFElementRender> cards = new List<SFElementRender>();
            public SFElementRender placeholder;
            public int mentionCount;
            public float shift, velocity;       // in columns; card i sits at offset i + shift
            public bool dragging, touched;
            public float? goal;                 // where a key step glides to
            public int generation;

            public int year;
            public CancellationTokenSource cts; // cancelled when the row closes
            public List<Pager> feeds;           // where the next pages come from, in turn
            public int feedIndex, realPages;
            public bool firstDone, loading, exhausted;
            public SFElementRender tail;        // "looking in ..." after the last card
            public readonly List<SFElementRender> ghosts = new List<SFElementRender>();   // grey cards after a loading one
            public readonly HashSet<string> seen = new HashSet<string>();
            public readonly List<string> deduced = new List<string>();
        }

        private readonly List<Level> _levels = new List<Level>();      // [0] is row 1
        private SFElementRender _candidate;
        private float _candidateSince;
        private int _generation;
        private CancellationTokenSource _searchCts = new CancellationTokenSource();

        public bool HasRow(int row) => LevelAt(row) is Level l && l.cards.Count > 0;

        private void Awake()
        {
            if (_grid == null) _grid = FindFirstObjectByType<GridInteraction>();
            if (_document == null) _document = FindFirstObjectByType<SFDocumentRender>();
            _search = FindFirstObjectByType<SFManager>();
            _query = FindFirstObjectByType<SFTimelineContentGenerator>();
            if (_search != null) _search.SearchStarted += OnSearchStarted;
            WikidataRelated.Throttled += OnThrottled;
            MoreSources.EuropeanaKey = _europeanaKey;

            _deducer = FindFirstObjectByType<Deducer>();
            if (_deducer == null) _deducer = gameObject.AddComponent<Deducer>();
            if (FindFirstObjectByType<TimelineExtender>() == null) gameObject.AddComponent<TimelineExtender>();
            if (FindFirstObjectByType<Harvester>() == null) gameObject.AddComponent<Harvester>();
            if (FindFirstObjectByType<HarvestWall>() == null) gameObject.AddComponent<HarvestWall>();
            if (FindFirstObjectByType<TimelineSkeleton>() == null) gameObject.AddComponent<TimelineSkeleton>();
            if (FindFirstObjectByType<SearchHistory>() == null) gameObject.AddComponent<SearchHistory>();
            if (FindFirstObjectByType<SearchBox>() == null) gameObject.AddComponent<SearchBox>();
        }

        private void OnDestroy()
        {
            if (_search != null) _search.SearchStarted -= OnSearchStarted;
            WikidataRelated.Throttled -= OnThrottled;
            _searchCts.Cancel();
        }

        private void OnSearchStarted(string subject, int y0, int y1)
        {
            _searchCts.Cancel();
            _searchCts = new CancellationTokenSource();
            RemoveFrom(1);
            _candidate = null;
            _prefetched = null;
        }

        // ---- following the focus ---------------------------------------------

        private void Update()
        {
            if (_grid == null || _document == null) return;
            UpdateRows(Time.deltaTime);
            UpdatePath();
            if (Input.GetKeyDown(_deduceKey) && !SearchBox.typing) DeduceHere();
            Prefetch(_grid.FocusPanel());
            foreach (var l in _levels.ToList()) MaybeLoadMore(l);

            var card = _grid.SettledPanel();
            if (card == null || card.element?.data == null || IsPlaceholder(card)) { _candidate = null; return; }

            int row = card.element.row;
            var below = LevelAt(row + 1);
            if (below != null && below.parent == card) { _candidate = null; return; }   // already open

            // The rows below were opened from another card: they go now.
            if (below != null) RemoveFrom(row + 1);

            if (_candidate != card)
            {
                _candidate = card;
                _candidateSince = Time.time;
                return;
            }
            if (Time.time - _candidateSince < _dwell) return;
            _candidate = null;
            _ = Open(row + 1, card);
        }

        // Dim the cards off the path in the rows above the one you are on.
        private void UpdatePath()
        {
            var focus = _grid.FocusPanel();
            int here = focus != null ? focus.element.row : 0;
            foreach (Transform t in _document.renderRoot != null ? _document.renderRoot : transform)
            {
                var p = t.GetComponent<SFElementRender>();
                if (p == null || p.element == null) continue;
                int r = p.element.row;
                bool onPath = r >= here || IsOnPath(p);
                var g = PanelUtil.Group(p);
                float a = onPath ? 1f : _offPathAlpha;
                if (!Mathf.Approximately(g.alpha, a) && !_grid.IsDismissed(p.element)) g.alpha = a;
            }
        }

        // A card is on the path if a row below was opened from it.
        private bool IsOnPath(SFElementRender p) => _levels.Any(l => l.parent == p);

        // ---- row scrolling (called by GridInteraction) --------------------

        public void BeginRow(int row)
        {
            var l = LevelAt(row);
            if (l == null) return;
            l.dragging = true;
            l.touched = true;
            l.velocity = 0f;
            l.goal = null;
        }

        /// <summary>Scroll a row by a world-space distance along the timeline.</summary>
        public void ScrollRow(int row, float worldDistance)
        {
            var l = LevelAt(row);
            if (l == null || l.cards.Count == 0) return;
            float pitch = Pitch(l);
            if (pitch <= 0f) return;
            float d = worldDistance / pitch;
            float over = l.shift - Clamp(l, l.shift);
            if (over * d > 0f) d *= 0.35f;             // resist past the ends
            l.shift += d;
        }

        public void EndRow(int row, float worldVelocity)
        {
            var l = LevelAt(row);
            if (l == null) return;
            l.dragging = false;
            float pitch = Pitch(l);
            l.velocity = pitch > 0f ? worldVelocity / pitch : 0f;
        }

        /// <summary>One card left (-1) or right (+1): keys.</summary>
        public void StepRow(int row, int dir)
        {
            var l = LevelAt(row);
            if (l == null || l.cards.Count == 0) return;
            l.touched = true;
            l.velocity = 0f;
            // Glide there, like the timeline; repeated presses stack up.
            float from = l.goal ?? Mathf.Round(l.shift);
            l.goal = Clamp(l, from - dir);
        }

        private void UpdateRows(float dt)
        {
            foreach (var l in _levels)
            {
                if (l.cards.Count > 0 && !l.dragging)
                {
                    if (Mathf.Abs(l.velocity) > _settleBelow)
                    {
                        l.shift += l.velocity * dt;
                        l.velocity *= Mathf.Exp(-_damping * dt);
                    }
                    else
                    {
                        l.velocity = 0f;
                        float target = l.goal ?? Clamp(l, Mathf.Round(l.shift));
                        l.shift = Mathf.Lerp(l.shift, target, 1f - Mathf.Exp(-_settleStiffness * dt));
                        if (Mathf.Abs(l.shift - target) < 0.0005f) { l.shift = target; l.goal = null; }
                    }
                }
                for (int i = 0; i < l.cards.Count; i++) Place(l.cards[i], i + l.shift);
                if (l.placeholder != null) Place(l.placeholder, l.cards.Count + l.shift);
                if (l.tail != null) Place(l.tail, l.cards.Count + l.shift);
                SyncGhosts(l);
                for (int i = 0; i < l.ghosts.Count; i++) Place(l.ghosts[i], l.cards.Count + 1 + i + l.shift);
            }
        }

        private void Place(SFElementRender c, float shift)
        {
            if (c == null) return;
            c.element.shift = shift;
            c.transform.localPosition = _document.GetLayoutPosition(c);
        }

        // Card i is under the focus column when shift = -i.
        private static float Clamp(Level l, float shift) => Mathf.Clamp(shift, -(l.cards.Count - 1), 0f);

        private float Pitch(Level l)
        {
            var c = l.cards.FirstOrDefault(x => x != null);
            return c != null ? _document.ColumnPitchWorld(c) : 0f;
        }

        // ---- opening a row ---------------------------------------------------

        private System.Threading.Tasks.Task Open(int row, SFElementRender parent) => Fill(StartLevel(row, parent));

        private Level StartLevel(int row, SFElementRender parent)
        {
            RemoveFrom(row);
            var data = parent.element.data;
            var level = new Level
            {
                row = row,
                parent = parent,
                column = parent.element.row == 0 ? parent.element : parent.element.column,
                generation = ++_generation,
                year = YearOf(data),
                cts = CancellationTokenSource.CreateLinkedTokenSource(_searchCts.Token),
            };
            // The card itself does not show up again below it.
            var c = data.text ?? data.image;
            if (!string.IsNullOrEmpty(c?.Wikidata)) level.seen.Add("id:" + c.Wikidata);
            if (!string.IsNullOrEmpty(c?.title)) level.seen.Add("t:" + Norm(c.title));
            _levels.Add(level);
            return level;
        }

        private async System.Threading.Tasks.Task Fill(Level level)
        {
            var parent = level.parent;
            int row = level.row, year = level.year;
            var data = parent.element.data;
            var ct = level.cts.Token;
            bool Stale() => !_levels.Contains(level) || ct.IsCancellationRequested;

            // Not a Wikipedia / Wikidata card (a picture from an archive, a
            // deduction...): no mentions or linked items, straight to the other sources.
            var pc = data.text ?? data.image;
            bool deduction = pc?.source == "deduction";
            bool wiki = !deduction && (row == 1 || ArticleOf(data.text) != null || (pc?.Wikidata?.StartsWith("Q") ?? false));
            if (!wiki)
            {
                try
                {
                    SetTail(level, "Looking for more about this…");
                    level.feeds = await Feeds(level, data, ct);
                    if (Stale()) return;
                    level.firstDone = true;
                    await LoadMore(level);
                }
                catch (System.OperationCanceledException) { }
                catch (System.Exception e) { Debug.LogWarning($"[related] {e.Message}"); }
                return;
            }

            level.placeholder = AddInfo(level, $"Looking for what this mentions, and what else is linked to it around {year}…", 0f);

            try
            {
                // Both start at once; mentions (Wikipedia) usually arrive first.
                var mentionJob = _mentions.For(data, year, ct);
                var aroundJob = Around(data, year, row, ct);

                List<RelatedItem> mentions;
                try { mentions = Fresh((await mentionJob ?? new List<RelatedItem>()).Take(_maxMentions).ToList()); }
                catch (System.OperationCanceledException) { throw; }
                catch (System.Exception e) { Debug.LogWarning($"[related] mentions: {e.Message}"); mentions = new List<RelatedItem>(); }
                if (Stale()) return;

                if (mentions.Count > 0)
                {
                    Append(level, mentions);
                    level.mentionCount = mentions.Count;
                }

                // Wikidata can be slow (rate limit): after a few seconds the
                // other sources start anyway; its items join when they come.
                var feedsJob = Feeds(level, data, ct);
                await System.Threading.Tasks.Task.WhenAny(aroundJob, System.Threading.Tasks.Task.Delay(4000, ct));
                if (Stale()) return;
                if (!aroundJob.IsCompleted)
                {
                    level.feeds = await feedsJob;
                    if (Stale()) return;
                    RemovePlaceholder(level);
                    level.firstDone = true;
                }

                var (around, label, answered) = await aroundJob;
                if (Stale()) return;
                around = Fresh(around);

                Debug.Log($"[related] row {row} from \"{parent.element.data?.text?.title ?? parent.element.data?.label}\" ({label}): " +
                          $"{mentions.Count} mentioned, {around.Count} around {year}" + (answered ? "" : " (wikidata: no answer)"));

                if (around.Count > 0)
                {
                    try { await _wikidata.FillImages(around, ct); }
                    catch (System.OperationCanceledException) { throw; }
                    catch (System.Exception e) { Debug.LogWarning($"[related] images: {e.Message}"); }
                    if (Stale()) return;

                    int first = level.cards.Count;
                    Append(level, around);
                    // Untouched: start on the item nearest this card's year.
                    if (!level.touched && around.Count > 0)
                    {
                        int nearest = around.Select((it, i) => (d: Mathf.Abs(it.dateYear - year), i)).OrderBy(x => x.d).First().i;
                        level.shift = -(first + nearest);
                    }
                }

                // From here on the row grows from the other sources as you scroll.
                if (!level.firstDone)
                {
                    level.feeds = await feedsJob;
                    if (Stale()) return;
                    RemovePlaceholder(level);
                    level.firstDone = true;
                }
            }
            catch (System.OperationCanceledException) { }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[related] {e.Message}");
                if (!Stale()) SetPlaceholder(level, "Couldn't reach Wikipedia / Wikidata.");
            }
        }

        // Things linked to the card's item on Wikidata, near its year, in date order.
        private async System.Threading.Tasks.Task<(List<RelatedItem> items, string label, bool answered)> Around(
            Result data, int year, int row, CancellationToken ct)
        {
            string subject = _query != null ? _query.subject : null;
            var (qid, label) = await _wikidata.Anchor(data, subject, ct);
            if (string.IsNullOrEmpty(qid)) return (new List<RelatedItem>(), subject, true);

            // From the timeline, the search's own window (one cached request per
            // search); deeper down, a 60-year window aligned to decades, so
            // nearby cards about the same item share one request.
            var (y0, y1) = BaseWindow(row, data, year);
            var items = await _wikidata.Window(qid, label, y0, y1, ct);
            if (items == null) return (new List<RelatedItem>(), label, false);
            return (Pick(items, year), label, true);
        }

        private (int y0, int y1) BaseWindow(int row, Result data, int year)
        {
            bool fromTimeline = row == 1 && data.text?.Wikidata == null && _query != null;
            if (fromTimeline) return (_query.yearFrom - _halfWindow, _query.yearTo + _halfWindow);
            int y0 = Mathf.FloorToInt((year - 30) / 10f) * 10;
            return (y0, y0 + 60);
        }

        // ---- more pages --------------------------------------------------------

        // Where a row's next pages come from, in the order they take turns.
        private async System.Threading.Tasks.Task<List<Pager>> Feeds(Level l, Result data, CancellationToken ct)
        {
            var c = data.text ?? data.image;
            string article = ArticleOf(data.text);
            bool deduction = c?.source == "deduction";
            string term = MoreSources.TermOf(c);
            if (string.IsNullOrEmpty(term)) term = _query != null ? _query.subject : "";

            string qid = null, label = term;
            if (!deduction && (l.row == 1 || article != null || (c?.Wikidata?.StartsWith("Q") ?? false)))
            {
                try { (qid, label) = await _wikidata.Anchor(data, _query != null ? _query.subject : null, ct); }
                catch (System.OperationCanceledException) { throw; }
                catch (System.Exception e) { Debug.LogWarning($"[related] anchor: {e.Message}"); }
            }

            var f = new List<Pager>();
            if (qid != null) f.Add(MoreSources.Statements(qid, label ?? term));
            f.Add(MoreSources.Commons(term, false));
            if (article != null) f.Add(MoreSources.ArticleLinks(article));
            f.Add(MoreSources.Archive(term, l.year));
            if (qid != null) f.Add(OtherYears(l, qid, label ?? term, data));
            f.Add(MoreSources.Europeana(term, l.year));
            f.Add(MoreSources.Commons(term, true));
            f.Add(MoreSources.WikiSearch(term, article));
            f.Add(MoreSources.Openverse(term));
            if (article != null) f.Add(MoreSources.Backlinks(article));
            return f;
        }

        // Wikidata items linked to the card's item in the 60-year windows
        // beyond the first one: later, earlier, later again...
        private Pager OtherYears(Level l, string qid, string label, Result data)
        {
            var (b0, b1) = BaseWindow(l.row, data, l.year);
            return new Pager("Wikidata, other years", 6, async (page, ct) =>
            {
                int k = page / 2 + 1;
                bool later = page % 2 == 0;
                int y0 = later ? b1 + 60 * (k - 1) : b0 - 60 * k;
                if (!later && y0 + 60 < -3000) return (new List<RelatedItem>(), page < 5);
                var items = await _wikidata.Window(qid, label, y0, y0 + 60, ct) ?? new List<RelatedItem>();
                var pick = Mix(items.OrderBy(i => Mathf.Abs(i.dateYear - l.year))).Take(_maxAround)
                           .OrderBy(i => i.dateYear).ToList();
                return (pick, true);
            });
        }

        private bool DeduceTurn(Level l) =>
            _deducer != null && _deducer.Available && l.feeds != null &&
            (l.feeds.All(f => f.Done) || (_deduceEvery > 0 && l.realPages >= _deduceEvery));

        private void MaybeLoadMore(Level l)
        {
            if (!l.firstDone || l.loading || l.exhausted || l.feeds == null) return;
            bool deduce = DeduceTurn(l);
            // Deductions cost the model's time: only for the row you are on, at its very end.
            if (deduce)
            {
                var f = _grid.FocusPanel();
                if (f == null || f.element == null || f.element.row != l.row) return;
            }
            int ahead = deduce ? 1 : _loadAhead;
            int at = Mathf.RoundToInt(-l.shift);
            if (l.cards.Count == 0 || at >= l.cards.Count - 1 - ahead) _ = LoadMore(l);
        }

        private Pager NextFeed(Level l)
        {
            int n = l.feeds?.Count ?? 0;
            for (int k = 0; k < n; k++)
            {
                int i = (l.feedIndex + k) % n;
                if (l.feeds[i].Done) continue;
                l.feedIndex = (i + 1) % n;
                return l.feeds[i];
            }
            return null;
        }

        private async System.Threading.Tasks.Task LoadMore(Level l)
        {
            if (l.loading) return;
            l.loading = true;
            var ct = l.cts.Token;
            bool Stale() => !_levels.Contains(l) || ct.IsCancellationRequested;
            try
            {
                for (int guard = 0; guard < 40 && !Stale(); guard++)
                {
                    if (DeduceTurn(l))
                    {
                        l.realPages = 0;
                        RemoveTail(l);
                        await DeduceInto(l, l.cards.Count);
                        return;
                    }
                    var feed = NextFeed(l);
                    if (feed == null)
                    {
                        l.exhausted = true;
                        SetTail(l, _deducer != null && !_deducer.Available
                            ? $"That's everything found here. Deductions are paused: no local model answered at {_deducer.Endpoint}. Press {_deduceKey} on a card to try again."
                            : "That's everything found here.");
                        return;
                    }
                    SetTail(l, $"Looking in {feed.name}…");
                    var items = Fresh(await feed.Next(ct));
                    if (Stale()) return;
                    if (items.Count == 0) continue;
                    try { await _wikidata.FillImages(items, ct); }
                    catch (System.OperationCanceledException) { throw; }
                    catch (System.Exception e) { Debug.LogWarning($"[related] images: {e.Message}"); }
                    if (Stale()) return;
                    RemoveTail(l);
                    Append(l, items);
                    l.realPages++;
                    Debug.Log($"[related] row {l.row}: +{items.Count} from {feed.name}");
                    return;
                }
            }
            catch (System.OperationCanceledException) { }
            catch (System.Exception e) { Debug.LogWarning($"[related] more: {e.Message}"); }
            finally { l.loading = false; }
        }

        // Only what no open row shows yet (the path, its rows, this row).
        private List<RelatedItem> Fresh(List<RelatedItem> items)
        {
            var batch = new HashSet<string>();
            var result = new List<RelatedItem>();
            foreach (var it in items ?? new List<RelatedItem>())
            {
                string id = "id:" + it.qid, t = "t:" + Norm(it.label);
                if (t == "t:") continue;
                if (_levels.Any(l => l.seen.Contains(id) || l.seen.Contains(t))) continue;
                if (!batch.Add(id) || !batch.Add(t)) continue;
                result.Add(it);
            }
            return result;
        }

        private static string Norm(string s) =>
            new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static string ArticleOf(Card c)
        {
            if (c == null || c.source != "wikipedia" || string.IsNullOrEmpty(c.SourceUrl)) return null;
            int i = c.SourceUrl.IndexOf("/wiki/", System.StringComparison.Ordinal);
            return i < 0 ? null : UnityEngine.Networking.UnityWebRequest.UnEscapeURL(c.SourceUrl.Substring(i + 6)).Replace('_', ' ');
        }

        // ---- coming in from the wall ---------------------------------------------

        /// <summary>Put a result into the snake: under the timeline card
        /// nearest its year (or that card itself), and glide to it. Resting on
        /// it then opens its own row, as usual.</summary>
        public void Bring(Result r, int year)
        {
            if (r == null || _document == null || _grid == null) return;
            SFElementRender best = null;
            int bestD = int.MaxValue;
            foreach (Transform t in _document.renderRoot != null ? _document.renderRoot : transform)
            {
                var p = t.GetComponent<SFElementRender>();
                if (p == null || p.element?.data == null || p.element.row != 0 || IsPlaceholder(p)) continue;
                if (p.element.data == r) { _grid.GlideTo(p); return; }
                int d = Mathf.Abs(YearOf(p.element.data) - year);
                if (d < bestD) { bestD = d; best = p; }
            }
            if (best == null) return;

            var l = LevelAt(1);
            if (l == null || l.parent != best)
            {
                l = StartLevel(1, best);
                _ = Fill(l);
            }
            l.touched = true;
            l.velocity = 0f;
            l.goal = null;
            int at = Mathf.Clamp(Mathf.RoundToInt(-l.shift), 0, l.cards.Count);
            var c = r.text ?? r.image;
            if (!string.IsNullOrEmpty(c?.Wikidata)) l.seen.Add("id:" + c.Wikidata);
            if (!string.IsNullOrEmpty(c?.title)) l.seen.Add("t:" + Norm(c.title));
            var card = AddCard(l, r, at + l.shift);
            if (card == null) return;
            l.cards.Insert(at, card);
            l.shift = -at;
            Place(card, 0f);
            _candidate = null;
            _grid.GlideTo(card);
        }

        // ---- deductions --------------------------------------------------------

        // D: a deduction from the card in focus, into the row below it, right
        // under it (the row opens if it is not open yet).
        private void DeduceHere()
        {
            if (_deducer == null) return;
            var card = _grid.FocusPanel();
            if (card == null || card.element?.data == null || IsPlaceholder(card)) return;
            int row = card.element.row;
            var below = LevelAt(row + 1);
            if (below == null || below.parent != card)
            {
                below = StartLevel(row + 1, card);
                _candidate = null;
                _ = Fill(below);
            }
            below.touched = true;
            int at = Mathf.Clamp(Mathf.RoundToInt(-below.shift), 0, below.cards.Count);
            _ = DeduceInto(below, at);
        }

        private async System.Threading.Tasks.Task DeduceInto(Level l, int index)
        {
            var ct = l.cts.Token;
            var r = new Result
            {
                label = "deduction · thinking…",
                text = new Card
                {
                    kind = "text", source = "deduction", title = "Deducing…", body = "", DateBasis = "deduced",
                    credit = "local model " + _deducer.Model, license = "generated - not a source", SourceUrl = "",
                },
            };
            index = Mathf.Clamp(index, 0, l.cards.Count);
            var card = AddCard(l, r, index + l.shift);
            if (card == null) return;
            l.cards.Insert(index, card);
            var cloud = card.gameObject.AddComponent<CloudyPanel>();
            try
            {
                var d = await _deducer.Deduce(Trail(l.parent), _query != null ? _query.subject : null,
                                              l.deduced.Count + l.row, l.deduced, x => ShowDeduction(card, r, x), ct);
                if (!string.IsNullOrEmpty(d.title)) l.deduced.Add(d.title);
            }
            catch (System.OperationCanceledException) { }
            catch (System.Exception e) { Debug.LogWarning($"[deduce] {e.Message}"); }
            finally { if (cloud != null) cloud.thinking = false; }
        }

        private void ShowDeduction(SFElementRender card, Result r, Deducer.Deduction d)
        {
            if (card == null) return;
            string title = !string.IsNullOrEmpty(d.title) ? d.title : d.error != null ? "No deduction" : "Deducing…";
            var p = DateParser.Parse(d.year ?? "");
            r.YearBegin = r.text.YearBegin = p.Begin;
            r.YearEnd = r.text.YearEnd = p.End;
            string when = !p.Begin.HasValue ? null
                        : p.End.HasValue && p.End != p.Begin ? $"{p.Begin}–{p.End}" : p.Begin.ToString();
            r.label = "deduction" + (when != null ? " · " + when : "") + (d.done ? "" : " · thinking…");
            r.text.title = title;
            r.text.body = d.error ?? d.text;
            card.name = r.label;
            if (card is CardDisplayTemplate t)
            {
                t.header.ClearParagraphs();
                t.header.AddParagraph(r.label);
                t.body.ClearParagraphs();
                if (t is CoverCard)
                {
                    t.description.ClearParagraphs();
                    t.description.AddParagraph(title);
                    t.body.AddParagraph(r.text.body + (d.done ? "" : " |"));
                }
                else t.body.AddParagraph(title + "\n\n" + r.text.body + (d.done ? "" : " |"));
                t.footer.ClearParagraphs();
                t.footer.AddParagraph($"deduced by {_deducer.Model}  ·  not a source");
                t.UpdateCardLayout();
            }
        }

        // From the timeline card down to `from`, along the path.
        private List<Result> Trail(SFElementRender from)
        {
            var trail = new List<Result>();
            var card = from;
            for (int guard = 0; card != null && guard < 64; guard++)
            {
                if (card.element?.data != null) trail.Add(card.element.data);
                int row = card.element?.row ?? 0;
                if (row <= 0) break;
                card = LevelAt(row)?.parent;
            }
            trail.Reverse();
            return trail;
        }

        // ---- prefetch ------------------------------------------------------------

        // The card in focus gets its Wikipedia lookups started at once, so its
        // row is ready (or nearly) when you rest on it. The rate-limited query
        // service is left until the row really opens.
        private void Prefetch(SFElementRender card)
        {
            if (card == null || card == _prefetched || card.element?.data == null || IsPlaceholder(card)) return;
            _prefetched = card;
            var data = card.element.data;
            if ((data.text ?? data.image)?.source == "deduction") return;
            var ct = _searchCts.Token;
            _ = Quiet(_mentions.For(data, YearOf(data), ct));
            if (ArticleOf(data.text) != null) _ = Quiet(_wikidata.Anchor(data, _query != null ? _query.subject : null, ct));
        }

        private static async System.Threading.Tasks.Task Quiet(System.Threading.Tasks.Task t)
        {
            try { await t; } catch { }
        }

        // Near the card's year, a mix of kinds, places / things before people
        // when there are many, then in date order.
        private List<RelatedItem> Pick(IEnumerable<RelatedItem> pool, int year)
        {
            float Score(RelatedItem it) =>
                Mathf.Abs(it.dateYear - year) - (it.image != null ? 4f : 0f) - (it.article != null ? 3f : 0f);

            var near = pool.Where(i => i.Near(year, _halfWindow)).ToList();
            var things = Mix(near.Where(i => !i.human).OrderBy(Score));
            var people = near.Where(i => i.human).OrderBy(Score).ToList();

            int nThings = Mathf.Min(things.Count, Mathf.CeilToInt(_maxAround / 2f));
            var result = things.Take(nThings).ToList();
            result.AddRange(people.Take(_maxAround - result.Count));
            if (result.Count < _maxAround) result.AddRange(things.Skip(nThings).Take(_maxAround - result.Count));
            return result.OrderBy(i => i.dateYear).ToList();
        }

        // Round-robin across kinds of link, so one prolific kind (Elvis: 300
        // songs "performed by") does not bury the rest (his films, buildings).
        private static List<RelatedItem> Mix(IEnumerable<RelatedItem> ordered)
        {
            var groups = ordered.GroupBy(i => i.relation ?? "").Select(g => new Queue<RelatedItem>(g)).ToList();
            var result = new List<RelatedItem>();
            while (groups.Count > 0)
            {
                for (int g = groups.Count - 1; g >= 0; g--)
                {
                    result.Add(groups[g].Dequeue());
                    if (groups[g].Count == 0) groups.RemoveAt(g);
                }
            }
            return result;
        }

        // ---- cards -------------------------------------------------------------

        private void Append(Level level, List<RelatedItem> items)
        {
            RemovePlaceholder(level);
            foreach (var it in items)
            {
                level.seen.Add("id:" + it.qid);
                level.seen.Add("t:" + Norm(it.label));
                var card = AddCard(level, WikidataRelated.ToResult(it), level.cards.Count + level.shift);
                if (card != null) level.cards.Add(card);
            }
        }

        private SFElementRender AddCard(Level level, Result r, float shift)
        {
            var e = new SFElement { index = 0, links = new List<SFElement>(), data = r,
                                    row = level.row, column = level.column, shift = shift };
            return _document.AddExtra(e);
        }

        private Level LevelAt(int row) => _levels.FirstOrDefault(l => l.row == row);

        // Close the rows from `row` down.
        private void RemoveFrom(int row)
        {
            foreach (var l in _levels.Where(l => l.row >= row).ToList())
            {
                foreach (var c in l.cards) if (c != null) Destroy(c.gameObject);
                if (l.placeholder != null) Destroy(l.placeholder.gameObject);
                if (l.tail != null) Destroy(l.tail.gameObject);
                foreach (var g in l.ghosts) if (g != null) Destroy(g.gameObject);
                l.ghosts.Clear();
                try { l.cts?.Cancel(); } catch { }
                _levels.Remove(l);
            }
        }

        // ---- placeholders ------------------------------------------------------

        private static Result Info(string msg) => new Result
        {
            label = "related",
            text = new Card { kind = "text", title = "", source = "wikidata", body = msg, DateBasis = "" },
        };

        private bool IsPlaceholder(SFElementRender r) =>
            r != null && ((r is CoverCard c && c.isSkeleton) || SearchHistory.IsBackCard(r) || _levels.Any(l => l.placeholder == r || l.tail == r || l.ghosts.Contains(r)));

        // A note that something is on its way shows as grey, shimmering
        // cards (the note as a small caption); others ("that's everything")
        // as text.
        private static bool Loading(string msg) =>
            msg != null && (msg.StartsWith("Looking") || msg.Contains("busy"));

        private SFElementRender AddInfo(Level l, string msg, float shift)
        {
            var card = AddCard(l, Info(msg), shift);
            SetText(card, msg);
            return card;
        }

        private void SetTail(Level l, string msg)
        {
            if (l.tail == null) l.tail = AddInfo(l, msg, l.cards.Count + l.shift);
            else SetText(l.tail, msg);
        }

        // Two more grey cards behind a loading one, so a row that is filling
        // looks like a row, not a lone note.
        private void SyncGhosts(Level l)
        {
            bool loading = (l.tail is CoverCard t && t.isSkeleton) || (l.placeholder is CoverCard p && p.isSkeleton);
            int want = loading ? 2 : 0;
            while (l.ghosts.Count > want)
            {
                var g = l.ghosts[l.ghosts.Count - 1];
                if (g != null) Destroy(g.gameObject);
                l.ghosts.RemoveAt(l.ghosts.Count - 1);
            }
            while (l.ghosts.Count < want)
            {
                var g = AddCard(l, Info(""), l.cards.Count + 1 + l.ghosts.Count + l.shift);
                if (g == null) break;
                if (g is CoverCard c) c.SetSkeleton(true, "");
                l.ghosts.Add(g);
            }
        }

        private static void RemoveTail(Level l)
        {
            if (l.tail != null) Destroy(l.tail.gameObject);
            l.tail = null;
        }

        private static void SetText(SFElementRender r, string msg)
        {
            if (r is CoverCard cover) cover.SetSkeleton(Loading(msg), msg);
            if (r is CardDisplayTemplate card)
            {
                card.body.ClearParagraphs();
                card.body.AddParagraph(msg);
                card.UpdateCardLayout();
            }
        }

        private static void RemovePlaceholder(Level l)
        {
            if (l.placeholder != null) Destroy(l.placeholder.gameObject);
            l.placeholder = null;
        }

        private static void SetPlaceholder(Level l, string msg)
        {
            if (l.placeholder != null) SetText(l.placeholder, msg);
        }

        private void OnThrottled(int seconds)
        {
            foreach (var l in _levels)
                if (l.cards.Count == 0) SetPlaceholder(l, $"Wikidata is busy - asking again in {seconds} s…");
        }

        private int YearOf(Result r)
        {
            if (r.YearBegin.HasValue)
                return r.YearEnd.HasValue ? (r.YearBegin.Value + r.YearEnd.Value) / 2 : r.YearBegin.Value;
            var c = r.image ?? r.text;
            if (c?.YearBegin != null) return c.YearBegin.Value;
            return _query != null ? (_query.yearFrom + _query.yearTo) / 2 : 0;
        }
    }
}
