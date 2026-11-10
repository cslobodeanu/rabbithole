// Where kept panels go: the evidence tree, which is also your trail down the
// rabbit hole.
//
//   [strasbourg 1690-1710] ── kept A
//                          ├─ kept B ── [B-subject 1695-1715] ── kept D
//                          │                                  └─ kept E
//                          └─ kept C
//
// Two kinds of node:
//   search  - a query that filled the grid (subject + years)
//   kept    - something you pulled out of that grid; a child of its search
//
// Tap a kept item: start a new search from it (pivot). The new search hangs
// under that item, and what you keep next hangs under the new search - that
// is where the tree branches. Tap a search node: run it again, i.e. go back
// up the tree and dig somewhere else.
//
// Kept items are rebuilt from data by the same factory as the grid, so they
// look the same and survive a restart (kept_tree.json in persistentDataPath).
// That file is also what a deduction gets as its grounding context later.
//
// Zero-config: put this on an empty UI object inside the grid's Canvas (NOT
// inside the container that scrolls). That object's rect is the board's
// window: the tree is clipped to it and scrolls inside it (drag to pan, it
// follows each new item on its own). Left at the 100x100 "Create Empty"
// default, it docks itself to the left side of the canvas.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SpatialForms;
using SpatialForms.Factories;
using UnityEngine;
using UnityEngine.UI;
using XrProbe;

namespace TimelineVR.Interaction
{
    [Serializable]
    public class KeptItem
    {
        public const string Search = "search";
        public const string Kept = "kept";

        public string id;
        public string parentId;           // null = a root search
        public string kind;               // Search | Kept

        // search: the query itself. kept: the query it was found by.
        public string subject;
        public int yearFrom;
        public int yearTo;

        public string keptAt;             // ISO 8601, UTC

        // Why it is linked to its parent. Empty for now; the PC model can
        // fill it ("same place, 30 years later", "mentioned in").
        public string relation;

        public Result data;               // kept only

        [JsonIgnore] public bool IsSearch => kind == Search;
    }

    [Serializable]
    public class KeptTree
    {
        public const int CurrentVersion = 2;
        public int version = CurrentVersion;
        public string activeId;           // the search new keeps go under
        public List<KeptItem> items = new List<KeptItem>();
    }

    public class KeepBoard : MonoBehaviour, ILayerContent
    {
        [Tooltip("Same factory the grid uses, so kept items look like their originals. Auto-found.")]
        [SerializeField] private SFRenderElementFactory _factory;

        [Tooltip("The board's visible window, under a Canvas. Defaults to this object.")]
        [SerializeField] private RectTransform _viewport;

        [Tooltip("Board background. Fully transparent hides it.")]
        [SerializeField] private Color _background = new Color(0.04f, 0.07f, 0.11f, 0.45f);

        [Tooltip("Runs the searches. Auto-found.")]
        [SerializeField] private SFManager _search;

        [Header("Layout")]
        [SerializeField] private float _itemScale = 0.2f;
        [Tooltip("Search nodes are drawn this much smaller than kept items.")]
        [SerializeField] private float _searchScale = 0.7f;
        [Tooltip("Gap between items as a fraction of item width / height.")]
        [SerializeField] private Vector2 _gap = new Vector2(0.45f, 0.15f);
        [SerializeField] private float _activeScale = 1.12f;

        [Header("Current search")]
        [Tooltip("Border on the search now on the grid, and on the kept item it was started from.")]
        [SerializeField] private Color _currentColor = new Color(1f, 0.72f, 0.25f, 1f);
        [Tooltip("Border thickness in canvas units, as seen on the board (independent of item scale).")]
        [SerializeField] private float _currentThickness = 2f;
        [SerializeField] private float _moveSpeed = 8f;

        [Header("Pivot")]
        [Tooltip("Half-width of the year window when searching from a kept item, " +
                 "if the parent search gives no better hint.")]
        [SerializeField] private int _pivotHalfWindow = 10;

        [Header("Scrolling")]
        [Tooltip("Space kept between the tree and the window edge, in canvas units.")]
        [SerializeField] private float _padding = 12f;
        [SerializeField] private float _scrollDamping = 4f;
        [SerializeField] private float _scrollSpring = 10f;

        [Header("Zoom")]
        [SerializeField] private float _minZoom = 0.3f;
        [SerializeField] private float _maxZoom = 3f;

        [Header("Links")]
        [SerializeField] private Color _lineColor = new Color(0.55f, 0.85f, 1f, 0.85f);
        [SerializeField] private Color _searchLineColor = new Color(1f, 0.8f, 0.45f, 0.85f);
        [Tooltip("Line width as a fraction of an item's height.")]
        [SerializeField] private float _lineWidth = 0.025f;

        [Header("Persistence")]
        [Tooltip("Save the tree to kept_tree.json and load it at start. Off for now: every session starts with an empty tree.")]
        [SerializeField] private bool _saveTree = false;

        public event Action<KeptItem> ItemKept;

        public IReadOnlyList<KeptItem> items => _tree.items;
        public KeptItem active => Find(_tree.activeId);
        public string SavePath => Path.Combine(Application.persistentDataPath, "kept_tree.json");

        private KeptTree _tree = new KeptTree();
        private string _pendingParent;     // set just before a pivot search starts

        private readonly Dictionary<string, SFElementRender> _renders = new Dictionary<string, SFElementRender>();
        private readonly Dictionary<SFElementRender, string> _ids = new Dictionary<SFElementRender, string>();
        private readonly Dictionary<string, Vector3> _targets = new Dictionary<string, Vector3>();
        private RectTransform _content;    // created: holds items + lines, moves to scroll
        private UILines _lines;

        private Vector2 _scroll;           // content position inside the viewport
        private Vector2 _scrollVelocity;
        private bool _dragging;
        private bool _hasFollow;
        private Vector2 _follow;           // scroll position that shows the newest item
        private Rect _treeBounds;          // content-local, from layout targets
        private readonly List<Vector2> _linePts = new List<Vector2>();

        private const int LineSegments = 16;

        // ---- lifecycle -----------------------------------------------------

        private void Awake()
        {
            if (_viewport == null) _viewport = transform as RectTransform;
            if (_factory == null) _factory = FindFirstObjectByType<SFRenderElementFactory>();
            if (_search == null) _search = FindFirstObjectByType<SFManager>();

            if (_viewport == null || _viewport.GetComponentInParent<Canvas>() == null)
                Debug.LogWarning("[keep] KeepBoard must sit on a UI object inside a Canvas.", this);
            else
                BuildViewport();

            if (_factory == null)
                Debug.LogWarning("[keep] no SFRenderElementFactory found - kept items cannot be drawn.", this);

            // Subscribe before SFManager.Start fires the first search.
            if (_search != null) _search.SearchStarted += OnSearchStarted;
        }

        private void BuildViewport()
        {
            // Untouched "Create Empty" rect: dock to the left of the canvas.
            Rect vr = _viewport.rect;
            if (vr.width <= 100.5f && vr.height <= 100.5f)
            {
                _viewport.anchorMin = new Vector2(0.08f, 0.06f);
                _viewport.anchorMax = new Vector2(0.92f, 0.94f);
                _viewport.offsetMin = _viewport.offsetMax = Vector2.zero;
                _viewport.pivot = new Vector2(0.5f, 0.5f);
                Debug.Log("[keep] KeepBoard rect was the default 100x100 - filled most of the canvas " +
                          "of the canvas. Size the KeepBoard RectTransform to place it yourself.", this);
            }

            if (_viewport.GetComponent<RectMask2D>() == null) _viewport.gameObject.AddComponent<RectMask2D>();

            // Drawn over the grid, and hit before it: the board is in front.
            _viewport.SetAsLastSibling();
            if (_viewport.GetComponent<PanelLayer>() == null) _viewport.gameObject.AddComponent<PanelLayer>();

            if (_viewport.GetComponent<Graphic>() == null && _background.a > 0f)
            {
                var bg = _viewport.gameObject.AddComponent<Image>();
                bg.color = _background;
                bg.raycastTarget = false;
            }

            // Content: a zero-size rect whose origin is the tree's origin.
            var go = new GameObject("Content", typeof(RectTransform));
            _content = (RectTransform)go.transform;
            _content.SetParent(_viewport, false);
            _content.anchorMin = _content.anchorMax = new Vector2(0f, 1f);
            _content.pivot = new Vector2(0f, 1f);
            _content.sizeDelta = Vector2.zero;
            _content.anchoredPosition = Vector2.zero;
            _scroll = _content.localPosition;

            // Links first in the hierarchy, so they draw behind the cards.
            var lg = new GameObject("Links", typeof(RectTransform), typeof(CanvasRenderer), typeof(UILines));
            var lrt = (RectTransform)lg.transform;
            lrt.SetParent(_content, false);
            lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 1f);
            lrt.pivot = Vector2.zero;
            _lines = lg.GetComponent<UILines>();
            _lines.raycastTarget = false;
        }

        private void OnDestroy()
        {
            if (_search != null) _search.SearchStarted -= OnSearchStarted;
        }

        private void Start()
        {
            if (_saveTree) Load();
            // The first search may have started before the saved tree was
            // loaded: make the search on screen the active one again.
            if (_search != null && !string.IsNullOrEmpty(_search.subject))
            {
                var node = _tree.items.LastOrDefault(i => i.IsSearch && i.yearFrom == _search.yearFrom && i.yearTo == _search.yearTo
                               && string.Equals(i.subject, _search.subject, StringComparison.OrdinalIgnoreCase))
                           ?? EnsureSearch(_search.subject, _search.yearFrom, _search.yearTo, null);
                _tree.activeId = node.id;
            }
        }

        private void LateUpdate()
        {
            if (_content == null) return;
            float dt = Time.deltaTime;
            float k = 1f - Mathf.Exp(-_moveSpeed * dt);

            foreach (var item in _tree.items)
            {
                if (!_renders.TryGetValue(item.id, out var r) || r == null) continue;
                if (_targets.TryGetValue(item.id, out var t))
                    r.transform.localPosition = Vector3.Lerp(r.transform.localPosition, t, k);
                float s = ScaleOf(item) * (item.id == _tree.activeId ? _activeScale : 1f);
                r.transform.localScale = Vector3.Lerp(r.transform.localScale, Vector3.one * s, k);
            }

            UpdateTreeBounds();
            UpdateScroll(dt);
            UpdateHitAreas();
            UpdateMarks();
            RebuildLines();
        }

        // ---- visibility ----------------------------------------------------

        private bool _visible = true;

        /// <summary>Whether the board takes part in interaction. Hidden, it
        /// neither blocks the grid nor answers rays. Drawing is up to whoever
        /// shows it (BoardPeek fades it).</summary>
        public bool visible
        {
            get => _visible;
            set
            {
                _visible = value;
                var layer = _viewport != null ? _viewport.GetComponent<PanelLayer>() : null;
                if (layer != null) layer.enabled = value;
            }
        }

        public RectTransform viewport => _viewport;

        // LayerManager: the board answers rays and blocks the grid only while in front.
        public void OnLayerFocus(bool focused) => visible = focused;

        // ---- current search mark -------------------------------------------

        private readonly Dictionary<SFElementRender, UIFrame> _frames = new Dictionary<SFElementRender, UIFrame>();

        // The search on the grid is the active node; if it was started from a
        // kept item (a pivot), that item is its parent. Both get the border.
        private void UpdateMarks()
        {
            var current = active;
            string origin = current != null && current.IsSearch ? current.parentId : null;

            foreach (var kv in _renders)
            {
                var r = kv.Value;
                if (r == null) continue;
                bool marked = current != null && (kv.Key == current.id || kv.Key == origin);

                _frames.TryGetValue(r, out var frame);
                if (!marked)
                {
                    if (frame != null) frame.enabled = false;
                    continue;
                }
                if (frame == null) frame = _frames[r] = MakeFrame(r);
                frame.enabled = true;
                frame.color = _currentColor;
                // Item scale shrinks the border with the card; undo that.
                float s = Mathf.Max(1e-4f, Mathf.Abs(r.transform.localScale.x) * _zoom);
                float t = _currentThickness / s;
                if (!Mathf.Approximately(frame.thickness, t)) frame.thickness = t;
                var frt = frame.rectTransform;
                frt.offsetMin = new Vector2(-t * 0.5f, -t * 0.5f);
                frt.offsetMax = new Vector2(t * 0.5f, t * 0.5f);
                frt.SetAsLastSibling();
            }
        }

        private static UIFrame MakeFrame(SFElementRender r)
        {
            var go = new GameObject("CurrentSearchFrame", typeof(RectTransform), typeof(CanvasRenderer), typeof(UIFrame));
            go.layer = r.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(r.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            var f = go.GetComponent<UIFrame>();
            f.raycastTarget = false;
            return f;
        }

        // ---- scrolling -----------------------------------------------------

        /// <summary>True if a world point on the glass is inside the board's window.</summary>
        public bool ContainsWorldPoint(Vector3 world)
        {
            if (_viewport == null || !visible) return false;
            return _viewport.rect.Contains((Vector2)_viewport.InverseTransformPoint(world));
        }

        public void BeginPan()
        {
            _dragging = true;
            _hasFollow = false;
            _scrollVelocity = Vector2.zero;
        }

        public void Pan(Vector3 worldDelta)
        {
            if (_viewport == null) return;
            Vector2 d = _viewport.InverseTransformVector(worldDelta);
            // Resist past the edges, like any scroll view.
            Vector2 over = _scroll - Clamp(_scroll);
            if (Vector2.Dot(d, over) > 0f) d *= 0.35f;
            _scroll += d;
        }

        public void EndPan(Vector3 worldVelocity)
        {
            _dragging = false;
            if (_viewport != null) _scrollVelocity = _viewport.InverseTransformVector(worldVelocity);
        }

        // ---- zoom ----------------------------------------------------------
        // Content is scaled around a pivot: the point of the tree under the
        // pivot stays under it. Content-space bounds are multiplied by _zoom
        // wherever they meet window space (Clamp, Follow).

        private float _zoom = 1f;
        private float _zoomStart;
        private Vector2 _zoomPivot, _zoomStartScroll;

        public float zoom => _zoom;

        /// <summary>Two-hand zoom begins; pivot is a world point on the glass.</summary>
        public void BeginZoom(Vector3 worldPivot)
        {
            if (_viewport == null) return;
            _dragging = true;               // no clamp-spring while the hands hold it
            _hasFollow = false;
            _scrollVelocity = Vector2.zero;
            _zoomStart = _zoom;
            _zoomStartScroll = _scroll;
            _zoomPivot = _viewport.InverseTransformPoint(worldPivot);
        }

        /// <summary>Spread ratio since BeginZoom.</summary>
        public void Zoom(float ratio)
        {
            if (_viewport == null) return;
            float z = Mathf.Clamp(_zoomStart * ratio, _minZoom, _maxZoom);
            Vector2 under = (_zoomPivot - _zoomStartScroll) / Mathf.Max(1e-4f, _zoomStart);
            _scroll = _zoomPivot - under * z;
            _zoom = z;
        }

        public void EndZoom() => _dragging = false;

        /// <summary>One-shot zoom by a factor around a world point (wheel, keys).</summary>
        public void ZoomBy(float factor, Vector3 worldPivot)
        {
            if (_viewport == null) return;
            Vector2 v = _viewport.InverseTransformPoint(worldPivot);
            float z = Mathf.Clamp(_zoom * factor, _minZoom, _maxZoom);
            Vector2 under = (v - _scroll) / Mathf.Max(1e-4f, _zoom);
            _scroll = v - under * z;
            _zoom = z;
            _hasFollow = false;
            _scrollVelocity = Vector2.zero;
        }

        private Rect Zoomed(Rect b) =>
            Rect.MinMaxRect(b.xMin * _zoom, b.yMin * _zoom, b.xMax * _zoom, b.yMax * _zoom);

        private void UpdateScroll(float dt)
        {
            if (!_dragging)
            {
                if (_hasFollow)
                {
                    _scroll = Vector2.Lerp(_scroll, Clamp(_follow), 1f - Mathf.Exp(-_scrollSpring * dt));
                    if ((_scroll - Clamp(_follow)).sqrMagnitude < 0.25f) _hasFollow = false;
                }
                else
                {
                    _scroll += _scrollVelocity * dt;
                    _scrollVelocity *= Mathf.Exp(-_scrollDamping * dt);
                    Vector2 c = Clamp(_scroll);
                    if (c != _scroll)
                    {
                        _scrollVelocity *= Mathf.Exp(-_scrollSpring * dt);
                        _scroll = Vector2.Lerp(_scroll, c, 1f - Mathf.Exp(-_scrollSpring * dt));
                    }
                }
            }
            _content.localPosition = new Vector3(_scroll.x, _scroll.y, 0f);
            _content.localScale = Vector3.one * _zoom;
        }

        // The range of content positions that keeps the tree in the window.
        // A tree smaller than the window sticks to its top-left corner.
        private Vector2 Clamp(Vector2 p)
        {
            Rect r = _viewport.rect;
            Rect b = Zoomed(_treeBounds);
            float pad = _padding;

            float xHi = r.xMin + pad - b.xMin;          // left edge at the window's left
            float xLo = r.xMax - pad - b.xMax;          // right edge at the window's right
            p.x = xLo > xHi ? xHi : Mathf.Clamp(p.x, xLo, xHi);

            float yLo = r.yMax - pad - b.yMax;          // top edge at the window's top
            float yHi = r.yMin + pad - b.yMin;          // bottom edge at the window's bottom
            p.y = yLo > yHi ? yLo : Mathf.Clamp(p.y, yLo, yHi);
            return p;
        }

        // Scroll just enough to bring an item into view.
        private void Follow(string id)
        {
            if (_viewport == null || !TryItemBounds(id, out Rect b)) return;
            b = Zoomed(b);
            Rect r = _viewport.rect;
            float pad = _padding;
            Vector2 p = _hasFollow ? _follow : _scroll;

            float xLo = r.xMax - pad - b.xMax, xHi = r.xMin + pad - b.xMin;
            p.x = xLo > xHi ? xHi : Mathf.Clamp(p.x, xLo, xHi);
            float yLo = r.yMax - pad - b.yMax, yHi = r.yMin + pad - b.yMin;
            p.y = yLo > yHi ? yLo : Mathf.Clamp(p.y, yLo, yHi);

            _follow = p;
            _hasFollow = true;
            _scrollVelocity = Vector2.zero;
        }

        // Cards scrolled out of the window are clipped but their colliders
        // are not - switch those off so a ray cannot grab an invisible card.
        private void UpdateHitAreas()
        {
            Rect r = _viewport.rect;
            foreach (var kv in _renders)
            {
                var p = kv.Value;
                if (p == null) continue;
                var col = p.GetComponent<Collider>();
                if (col == null) continue;
                Vector2 c = _viewport.InverseTransformPoint(PanelUtil.WorldCenter(p));
                col.enabled = visible && r.Contains(c);
            }
        }

        private bool TryItemBounds(string id, out Rect b)
        {
            b = default;
            if (!_targets.TryGetValue(id, out var t) || !_renders.TryGetValue(id, out var r) || r == null) return false;
            var item = Find(id);
            if (!(r.transform is RectTransform rt) || item == null) return false;
            float s = ScaleOf(item) * (id == _tree.activeId ? _activeScale : 1f);
            Rect rr = rt.rect;
            b = Rect.MinMaxRect(t.x + rr.xMin * s, t.y + rr.yMin * s, t.x + rr.xMax * s, t.y + rr.yMax * s);
            return true;
        }

        private void UpdateTreeBounds()
        {
            bool any = false;
            Rect all = default;
            foreach (var id in _targets.Keys)
            {
                if (!TryItemBounds(id, out Rect b)) continue;
                all = any ? Rect.MinMaxRect(Mathf.Min(all.xMin, b.xMin), Mathf.Min(all.yMin, b.yMin),
                                            Mathf.Max(all.xMax, b.xMax), Mathf.Max(all.yMax, b.yMax)) : b;
                any = true;
            }
            _treeBounds = any ? all : new Rect(0f, 0f, 0f, 0f);
        }

        // ---- public --------------------------------------------------------

        public bool Owns(SFElementRender panel) => panel != null && _ids.ContainsKey(panel);

        /// <summary>Keep an element under the current search.</summary>
        public KeptItem Keep(SFElement element, Component from = null)
        {
            if (element?.data == null || _factory == null || _content == null) return null;

            var search = active;
            if (search == null)
            {
                // No search node (e.g. it was deleted): one for the query on screen.
                var q = FindFirstObjectByType<SFTimelineContentGenerator>();
                search = q != null ? EnsureSearch(q.subject, q.yearFrom, q.yearTo, null)
                                   : EnsureSearch("(unknown search)", 0, 0, null);
                _tree.activeId = search.id;
            }

            // Same thing kept twice from the same search: ignore.
            string key = Key(element.data);
            var dup = _tree.items.FirstOrDefault(i => i.parentId == search.id && !i.IsSearch && Key(i.data) == key);
            if (dup != null) return dup;

            var item = new KeptItem
            {
                id = NewId(),
                parentId = search.id,
                kind = KeptItem.Kept,
                subject = search.subject,
                yearFrom = search.yearFrom,
                yearTo = search.yearTo,
                keptAt = DateTime.UtcNow.ToString("o"),
                relation = "",
                data = element.data,
            };
            _tree.items.Add(item);

            var r = Spawn(item);
            // Fly in from where it was kept.
            if (r != null && from != null)
                r.transform.position = PanelUtil.WorldCenter(from);

            Relayout();
            Follow(item.id);
            Save();
            ItemKept?.Invoke(item);
            return item;
        }

        /// <summary>A new search from a card in the grid (double click / tap):
        /// it is kept first, so the new search hangs under it in the tree -
        /// the way back.</summary>
        public void SearchFrom(SFElement element, Component from = null)
        {
            if (element?.data == null || _search == null) return;
            var item = Keep(element, from);
            if (item == null) return;
            PivotQuery(item, out string subject, out int y0, out int y1);
            _pendingParent = item.id;
            Debug.Log($"[board] new search from a card: {subject} {y0}-{y1}");
            _ = _search.Search(subject, y0, y1);
        }

        /// <summary>The next search returns to an earlier one: reuse its node
        /// (the most recent with that subject and years) instead of adding one.</summary>
        public void PrepareReturn(string subject, int y0, int y1)
        {
            var node = _tree.items.LastOrDefault(i => i.IsSearch && i.yearFrom == y0 && i.yearTo == y1
                && string.Equals(i.subject, subject, StringComparison.OrdinalIgnoreCase));
            _pendingParent = node?.parentId;
        }

        /// <summary>Back one dive: re-run the search the current one was started
        /// from (up the tree: this search, the card it came from, that card's
        /// search). Returns the card it came from, to return to; null if this
        /// search was not started from a card.</summary>
        public KeptItem Back()
        {
            var active = Find(_tree.activeId);
            if (active == null || _search == null) return null;
            KeptItem from = null;
            var up = Find(active.parentId);
            while (up != null && !up.IsSearch) { from ??= up; up = Find(up.parentId); }
            if (up == null) return null;
            _pendingParent = up.parentId;            // finds the existing node, adds nothing
            Debug.Log($"[board] back to: {up.subject} {up.yearFrom}-{up.yearTo}");
            _ = _search.Search(up.subject, up.yearFrom, up.yearTo);
            return from;
        }

        /// <summary>Tap on a board item: pivot from a kept item, or re-run a search.</summary>
        public void Select(SFElementRender panel)
        {
            if (panel == null || !_ids.TryGetValue(panel, out var id)) return;
            var item = Find(id);
            if (item == null || _search == null) return;

            if (item.IsSearch)
            {
                _pendingParent = item.parentId;
                _ = _search.Search(item.subject, item.yearFrom, item.yearTo);
            }
            else
            {
                PivotQuery(item, out string subject, out int y0, out int y1);
                _pendingParent = item.id;
                _ = _search.Search(subject, y0, y1);
            }
        }

        /// <summary>Delete one node. Its children move up to its parent, so the
        /// rest of the branch - what was found after it - survives.</summary>
        public void Remove(SFElementRender panel)
        {
            if (panel == null || !_ids.TryGetValue(panel, out var id)) return;
            var item = Find(id);
            if (item == null) return;

            foreach (var it in _tree.items)
                if (it.parentId == id) it.parentId = item.parentId;

            // New keeps went under this node: send them to the nearest search above it.
            if (_tree.activeId == id)
            {
                var up = Find(item.parentId);
                while (up != null && !up.IsSearch) up = Find(up.parentId);
                _tree.activeId = up?.id;
            }

            _tree.items.Remove(item);
            _renders.Remove(id);
            _ids.Remove(panel);
            _targets.Remove(id);
            Destroy(panel.gameObject);

            Relayout();
            Save();
        }

        [ContextMenu("Clear kept tree")]
        public void Clear()
        {
            foreach (var r in _renders.Values) if (r != null) Destroy(r.gameObject);
            _renders.Clear(); _ids.Clear(); _targets.Clear();
            _tree = new KeptTree();
            UpdateTreeBounds();
            Save();
        }

        // ---- searches ------------------------------------------------------

        private void OnSearchStarted(string subject, int y0, int y1)
        {
            var node = EnsureSearch(subject, y0, y1, _pendingParent);
            _pendingParent = null;
            _tree.activeId = node.id;
            Follow(node.id);
            Save();
        }

        private KeptItem EnsureSearch(string subject, int y0, int y1, string parentId)
        {
            var node = _tree.items.FirstOrDefault(i => i.IsSearch && i.parentId == parentId
                && string.Equals(i.subject, subject, StringComparison.OrdinalIgnoreCase)
                && i.yearFrom == y0 && i.yearTo == y1);
            if (node != null) return node;

            node = new KeptItem
            {
                id = NewId(),
                parentId = parentId,
                kind = KeptItem.Search,
                subject = subject,
                yearFrom = y0,
                yearTo = y1,
                keptAt = DateTime.UtcNow.ToString("o"),
                relation = "",
            };
            _tree.items.Add(node);

            var r = Spawn(node);
            if (r != null && parentId != null && _renders.TryGetValue(parentId, out var from) && from != null)
                r.transform.localPosition = from.transform.localPosition;

            Relayout();
            return node;
        }

        // What to search for when digging from a kept item. Deliberately
        // naive - the card title, cleaned up, around the card's own date. The
        // PC model is the better source for this later ("what to look at next").
        private void PivotQuery(KeptItem item, out string subject, out int y0, out int y1)
        {
            var c = item.data?.text ?? item.data?.image;
            string title = c?.title ?? item.data?.label ?? item.subject ?? "";
            // A Wikipedia card: its article's title is the cleanest subject.
            var t = item.data?.text;
            if (t != null && t.source == "wikipedia" && !string.IsNullOrEmpty(t.SourceUrl))
            {
                int w = t.SourceUrl.IndexOf("/wiki/", StringComparison.Ordinal);
                if (w >= 0) title = UnityEngine.Networking.UnityWebRequest.UnEscapeURL(t.SourceUrl.Substring(w + 6)).Replace('_', ' ');
                int hash = title.IndexOf('#');
                if (hash > 0) title = title.Substring(0, hash);
            }
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s*\([^)]*\)\s*$", "");   // "(film)", "(1977)"

            if (title.StartsWith("File:", StringComparison.OrdinalIgnoreCase)) title = title.Substring(5);
            int dot = title.LastIndexOf('.');
            if (dot > 0 && title.Length - dot <= 5) title = title.Substring(0, dot);   // .jpg, .tiff ...
            title = title.Replace('_', ' ').Replace(" - ", " ").Trim();
            if (title.Length > 60)
            {
                int cut = title.LastIndexOf(' ', 60);
                title = title.Substring(0, cut > 20 ? cut : 60);
            }
            subject = title.Length > 0 ? title : item.subject;

            int half = item.yearTo > item.yearFrom ? Mathf.Max(5, (item.yearTo - item.yearFrom) / 2) : _pivotHalfWindow;
            int? b = item.data?.YearBegin, e = item.data?.YearEnd ?? b;
            if (b.HasValue)
            {
                y0 = b.Value - half;
                y1 = e.Value + half;
            }
            else
            {
                y0 = item.yearFrom;
                y1 = item.yearTo;
            }
        }

        // ---- building ------------------------------------------------------

        private SFElementRender Spawn(KeptItem item)
        {
            if (_factory == null || _content == null) return null;

            var data = item.IsSearch ? SearchCard(item) : item.data;
            var el = new SFElement { index = 0, links = new List<SFElement>(), data = data };
            var r = _factory.CreateElementRender(el, null);
            // Kept deductions stay cloudy: a guess must not turn into a source.
            if (data?.text?.source == "deduction") r.gameObject.AddComponent<CloudyPanel>().thinking = false;
            r.transform.SetParent(_content, false);
            r.transform.localRotation = Quaternion.identity;
            r.transform.localScale = Vector3.one * ScaleOf(item);
            PanelUtil.EnsureCollider(r);
            // The board may live on its own canvas / camera layer (BoardPeek).
            PanelUtil.SetLayer(r.gameObject, _viewport.gameObject.layer);

            _renders[item.id] = r;
            _ids[r] = item.id;
            return r;
        }

        // A search drawn with the same card template: years as the header,
        // the subject as the body.
        private static Result SearchCard(KeptItem s)
        {
            string years = s.yearFrom == 0 && s.yearTo == 0 ? "" : $"{s.yearFrom}–{s.yearTo}";
            return new Result
            {
                label = years,
                text = new Card
                {
                    kind = "text",
                    title = "search",
                    source = "query",
                    body = $"Search: {s.subject}",
                    DateBasis = years,
                },
            };
        }

        private float ScaleOf(KeptItem item) => _itemScale * (item.IsSearch ? _searchScale : 1f);

        // Tree layout: depth is the column; leaves take consecutive rows and a
        // parent sits on its first child's row. Siblings in date order, so a
        // search's kept items read like a small timeline.
        private void Relayout()
        {
            Vector2 size = ItemSize();
            float dx = size.x * (1f + _gap.x);
            float dy = size.y * (1f + _gap.y);

            var known = new HashSet<string>(_tree.items.Select(i => i.id));
            var children = new Dictionary<string, List<KeptItem>>();
            var roots = new List<KeptItem>();
            foreach (var it in _tree.items)
            {
                if (string.IsNullOrEmpty(it.parentId) || !known.Contains(it.parentId)) { roots.Add(it); continue; }
                if (!children.TryGetValue(it.parentId, out var list)) children[it.parentId] = list = new List<KeptItem>();
                list.Add(it);
            }
            foreach (var list in children.Values) list.Sort(SiblingOrder);

            int row = 0;
            int Place(KeptItem it, int depth)
            {
                int myRow;
                if (children.TryGetValue(it.id, out var kids) && kids.Count > 0)
                {
                    myRow = Place(kids[0], depth + 1);
                    for (int i = 1; i < kids.Count; i++) Place(kids[i], depth + 1);
                }
                else
                {
                    myRow = row++;
                }
                _targets[it.id] = new Vector3(depth * dx, -myRow * dy, 0f);
                return myRow;
            }

            foreach (var root in roots) Place(root, 0);
        }

        private static int SiblingOrder(KeptItem a, KeptItem b)
        {
            int ya = a.data?.YearBegin ?? (a.IsSearch ? a.yearFrom : int.MaxValue);
            int yb = b.data?.YearBegin ?? (b.IsSearch ? b.yearFrom : int.MaxValue);
            int c = ya.CompareTo(yb);
            return c != 0 ? c : string.CompareOrdinal(a.keptAt, b.keptAt);
        }

        private Vector2 ItemSize()
        {
            foreach (var r in _renders.Values)
            {
                if (r != null && r.transform is RectTransform rt)
                    return rt.rect.size * _itemScale;
            }
            return Vector2.one * 100f * _itemScale;
        }

        // All links redrawn every frame from where the cards are now (they
        // animate), in content space, as one clipped UI mesh.
        private void RebuildLines()
        {
            if (_lines == null) return;

            var lrt = _lines.rectTransform;
            // The graphic's rect must overlap the window or RectMask2D culls
            // it whole, so stretch it over the tree (with slack for animation).
            Rect area = _treeBounds;
            float slack = Mathf.Max(area.width, area.height) * 0.25f + 50f;
            Vector2 origin = area.min - Vector2.one * slack;
            lrt.anchoredPosition = origin;
            lrt.sizeDelta = area.size + Vector2.one * slack * 2f;

            _lines.Clear();
            foreach (var item in _tree.items)
            {
                if (string.IsNullOrEmpty(item.parentId)) continue;
                if (!_renders.TryGetValue(item.id, out var child) || child == null) continue;
                if (!_renders.TryGetValue(item.parentId, out var parent) || parent == null) continue;
                if (!LiveRect(parent, out Rect pr) || !LiveRect(child, out Rect cr)) continue;

                // Right edge of the parent to left edge of the child, as an S-curve.
                Vector2 a = new Vector2(pr.xMax, pr.center.y) - origin;
                Vector2 b = new Vector2(cr.xMin, cr.center.y) - origin;
                float handle = Mathf.Max((b.x - a.x) * 0.5f, cr.width * 0.15f);
                Vector2 c1 = a + Vector2.right * handle;
                Vector2 c2 = b - Vector2.right * handle;

                _linePts.Clear();
                for (int i = 0; i < LineSegments; i++)
                {
                    float t = i / (float)(LineSegments - 1);
                    float u = 1f - t;
                    _linePts.Add(u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b);
                }
                // Amber into a search (a pivot), blue into a kept item.
                _lines.Add(_linePts, Mathf.Max(1f, Mathf.Min(pr.height, cr.height) * _lineWidth),
                           item.IsSearch ? _searchLineColor : _lineColor);
            }
            _lines.Commit();
        }

        // A card's rect in content space, as it is this frame.
        private static bool LiveRect(SFElementRender r, out Rect rect)
        {
            rect = default;
            if (!(r.transform is RectTransform rt)) return false;
            Vector3 p = rt.localPosition;
            Vector3 s = rt.localScale;
            Rect rr = rt.rect;
            rect = Rect.MinMaxRect(p.x + rr.xMin * s.x, p.y + rr.yMin * s.y,
                                   p.x + rr.xMax * s.x, p.y + rr.yMax * s.y);
            return true;
        }

        // ---- persistence ---------------------------------------------------

        private void Save()
        {
            if (!_saveTree) return;
            try
            {
                File.WriteAllText(SavePath, JsonConvert.SerializeObject(_tree, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[keep] save failed: {e.Message}");
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(SavePath)) return;
                var tree = JsonConvert.DeserializeObject<KeptTree>(File.ReadAllText(SavePath));
                if (tree?.items == null) return;

                if (tree.version < KeptTree.CurrentVersion)
                {
                    // v1 was a plain chain with no search nodes. Set it aside.
                    string old = Path.Combine(Application.persistentDataPath, $"kept_tree.v{tree.version}.json");
                    File.Copy(SavePath, old, true);
                    Debug.Log($"[keep] old tree format moved to {old}; starting a new tree");
                    return;
                }

                // Merge rather than replace: the first search may already have
                // added its node before Start ran.
                string current = _tree.activeId;
                var fresh = _tree.items.ToList();
                foreach (var r in _renders.Values) if (r != null) Destroy(r.gameObject);
                _renders.Clear(); _ids.Clear(); _targets.Clear();

                _tree = tree;
                foreach (var f in fresh)
                {
                    var same = _tree.items.FirstOrDefault(i => i.IsSearch && f.IsSearch && i.parentId == f.parentId
                        && string.Equals(i.subject, f.subject, StringComparison.OrdinalIgnoreCase)
                        && i.yearFrom == f.yearFrom && i.yearTo == f.yearTo);
                    if (same != null) { if (current == f.id) current = same.id; }
                    else _tree.items.Add(f);
                }
                if (current != null) _tree.activeId = current;

                foreach (var it in _tree.items)
                {
                    var r = Spawn(it);
                    if (r != null) r.transform.localScale = Vector3.zero;   // grow in
                }
                Relayout();
                foreach (var kv in _targets)
                    if (_renders.TryGetValue(kv.Key, out var r)) r.transform.localPosition = kv.Value;
                Save();
                Debug.Log($"[keep] loaded {_tree.items.Count} nodes from {SavePath}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[keep] load failed: {e.Message}");
            }
        }

        private KeptItem Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var it in _tree.items) if (it.id == id) return it;
            return null;
        }

        private static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        private static string Key(Result r)
        {
            if (r == null) return "";
            return (r.image?.ImageUrl ?? r.image?.ThumbUrl ?? "") + "|"
                 // Wikipedia passages share one SourceUrl, so add the start of the
                 // passage. (Not GetHashCode: it is not stable across runs.)
                 + (r.text?.SourceUrl ?? "") + "|" + Head(r.text?.body)
                 + "|" + (r.label ?? "");
        }

        private static string Head(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Substring(0, Mathf.Min(64, s.Length));
    }
}
