// What gestures do to the timeline grid.
//
//   pan              grid slides under the ray 1:1, keeps its momentum on release,
//                    then settles with the nearest panel at the focus point
//   tap a panel      that panel glides to the focus point
//   double tap       a new search from it (the board, now the search history,
//                    records it: search -> card -> new search)
//                    - on a board entry (board in front): search from it again
//   up / pull down   on the timeline row: back to the previous search
//   right click      on a board entry: remove it; also Delete
//   push a panel     it sinks and fades; past the threshold it is Dismissed
//                    (pulling toward you does nothing now - nothing is kept by hand)
//   two-hand spread  zooms the grid around the point between your hands
//   arrow keys       step to the neighbouring panel (PC convenience)
//
// Replaces KeyboardInputController - disable that one, both move the container.
//
// Every distance here is relative (panels per second, fraction of viewing
// distance), because the PC scene is a Screen Space - Camera canvas a hundred
// units away and the Quest scene will be a world-space canvas a metre away.

using System.Collections.Generic;
using SpatialForms;
using UnityEngine;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(10)]
    public class GridInteraction : MonoBehaviour, ILayerContent
    {
        [SerializeField] private GestureRecognizer _gestures;
        [SerializeField] private SFDocumentRender _document;

        [Tooltip("What gets moved and scaled - the same container KeyboardInputController moved.")]
        [SerializeField] private Transform _container;

        /// <summary>The grid container (what pans and zooms).</summary>
        public Transform container => _container;

        /// <summary>False while another layer (the keep board) is in front:
        /// gestures on the grid are ignored, board gestures still go through.</summary>
        [System.NonSerialized] public bool interactable = true;

        // LayerManager: the timeline takes input only while it is the front layer.
        public void OnLayerFocus(bool focused) => interactable = focused;

        [Tooltip("Optional. Where kept panels go.")]
        [SerializeField] private KeepBoard _keepBoard;

        [Tooltip("Optional. The third layer: one panel opened up. Auto-found.")]
        [SerializeField] private DetailView _detail;

        [Header("Momentum & snap")]
        [Tooltip("How quickly a flung grid slows down, 1/s.")]
        [SerializeField] private float _damping = 4f;

        [Tooltip("Below this speed (panels per second) the grid settles on a panel.")]
        [SerializeField] private float _settleBelow = 0.8f;

        [Tooltip("How fast it settles, 1/s.")]
        [SerializeField] private float _settleStiffness = 9f;

        [Header("Zoom")]
        [SerializeField] private float _minZoom = 0.35f;
        [SerializeField] private float _maxZoom = 2.5f;

        [Header("Panel feedback")]
        [Header("Focus")]
        [Tooltip("Border on the card at the focus point (the one the grid settles on).")]
        [SerializeField] private Color _focusColor = new Color(0.55f, 0.85f, 1f, 0.95f);
        [Tooltip("Border thickness in canvas units, as seen (independent of zoom).")]
        [SerializeField] private float _focusThickness = 2.5f;

        [Tooltip("At the keep threshold the panel has come this fraction of the way toward you.")]
        [SerializeField] private float _pullTravel = 0.25f;

        [SerializeField] private float _pullScale = 1.08f;
        [SerializeField] private float _dismissedAlpha = 0.25f;
        [SerializeField] private float _feedbackSpeed = 14f;

        [Header("PC convenience")]
        [SerializeField] private bool _arrowKeys = true;

        [Tooltip("Keep board zoom per wheel notch / key press.")]
        [SerializeField] private float _boardZoomStep = 1.15f;

        // ---- state ---------------------------------------------------------

        private Vector3 _focus;          // world point on the glass panels settle at
        private Vector3 _velocity;
        private bool _dragging;
        private bool _zooming;
        private bool _settled;
        private Vector3 _settleTarget;   // container position to settle at, in its parent's space
                                         // (so a parent layer can move - BoardPeek - without a fight)

        private Vector3 _zoomPivot, _zoomStartPos, _zoomStartScale;

        private class Fx
        {
            public Vector3 baseScale;
            public float pull, pullTarget;     // signed progress, -1 (dismiss) .. 1 (keep)
            public float hover, hoverTarget;
        }

        private readonly Dictionary<SFElementRender, Fx> _fx = new Dictionary<SFElementRender, Fx>();
        private readonly HashSet<SFElement> _dismissed = new HashSet<SFElement>();
        private readonly List<SFElementRender> _done = new List<SFElementRender>();

        public bool IsDismissed(SFElement e) => _dismissed.Contains(e);

        // ---- lifecycle -----------------------------------------------------

        private void OnEnable()
        {
            if (_gestures == null) return;
            _gestures.PanBegan += OnPanBegan;
            _gestures.Panned += OnPanned;
            _gestures.PanEnded += OnPanEnded;
            _gestures.Tapped += OnTapped;
            _gestures.DoubleTapped += OnDoubleTapped;
            _gestures.TappedEmpty += OnTappedEmpty;
            _gestures.Pulling += OnPulling;
            _gestures.PullCancelled += OnPullCancelled;
            _gestures.Kept += OnKept;
            _gestures.Dismissed += OnDismissed;
            _gestures.HoverChanged += OnHoverChanged;
            _gestures.ZoomBegan += OnZoomBegan;
            _gestures.Zoomed += OnZoomed;
            _gestures.ZoomEnded += OnZoomEnded;
        }

        private void OnDisable()
        {
            if (_gestures == null) return;
            _gestures.PanBegan -= OnPanBegan;
            _gestures.Panned -= OnPanned;
            _gestures.PanEnded -= OnPanEnded;
            _gestures.Tapped -= OnTapped;
            _gestures.DoubleTapped -= OnDoubleTapped;
            _gestures.TappedEmpty -= OnTappedEmpty;
            _gestures.Pulling -= OnPulling;
            _gestures.PullCancelled -= OnPullCancelled;
            _gestures.Kept -= OnKept;
            _gestures.Dismissed -= OnDismissed;
            _gestures.HoverChanged -= OnHoverChanged;
            _gestures.ZoomBegan -= OnZoomBegan;
            _gestures.Zoomed -= OnZoomed;
            _gestures.ZoomEnded -= OnZoomEnded;
        }

        private SFManager _search;
        private Vector3 _homePosition, _homeScale;

        private void Awake()
        {
            if (_keepBoard == null) _keepBoard = FindFirstObjectByType<KeepBoard>();
            if (_detail == null) _detail = FindFirstObjectByType<DetailView>();
            _search = FindFirstObjectByType<SFManager>();
            if (_search != null) _search.SearchStarted += OnSearchStarted;
            _homePosition = _container.localPosition;
            _homeScale = _container.localScale;
        }

        private void OnDestroy()
        {
            if (_search != null) _search.SearchStarted -= OnSearchStarted;
        }

        // A new search replaced the grid: back to the start, nothing in flight.
        private void OnSearchStarted(string subject, int y0, int y1)
        {
            _container.localPosition = _homePosition;
            _container.localScale = _homeScale;
            _axis = Axis.Horizontal;
            _lockedColumn = -1;
            _velocity = Vector3.zero;
            _settled = false;
            _dragging = _zooming = false;
            _fx.Clear();
            _dismissed.Clear();
        }

        private void Start()
        {
            // May have been added by LayerManager after our Awake.
            if (_detail == null) _detail = FindFirstObjectByType<DetailView>();
            _related = FindFirstObjectByType<RelatedColumn>();

            _baseScale = Mathf.Max(1e-4f, _container.localScale.x);

            // Focus = where the viewer looks straight ahead onto the glass.
            Transform head = _gestures != null && _gestures.hands != null ? _gestures.hands.head : null;
            if (head == null || !_gestures.RayToGlass(new Ray(head.position, head.forward), out _focus))
                _focus = _container.position;
        }

        // ---- motion --------------------------------------------------------

        private void Update()
        {
            if (_container == null) return;

            bool keys = _arrowKeys && !SearchBox.typing;     // the search box has the keyboard
            if (keys && interactable) HandleKeys();
            ReturnToCard();
            if (keys) HandleBoardZoomKeys();
            if (keys) HandleDetailKeys();
            if (keys) HandleKeepClick();

            float dt = Time.deltaTime;

            // Horizontal motion happens on the timeline row: if it started
            // down in a related column, glide back up while moving.
            if ((_dragging || _velocity != Vector3.zero) && _axis == Axis.Horizontal) ReturnToRow(dt);

            if (_dragging || _zooming) return;

            float cell = CellWidth();

            if (_velocity.magnitude > _settleBelow * cell)
            {
                _container.position += _velocity * dt;
                _velocity *= Mathf.Exp(-_damping * dt);
                _settled = false;
                return;
            }

            _velocity = Vector3.zero;
            if (!_settled)
            {
                var nearest = NearestOnAxis(_focus);
                if (nearest == null) return;
                SettleOn(nearest);
            }

            _container.localPosition = Vector3.Lerp(_container.localPosition, _settleTarget,
                                               1f - Mathf.Exp(-_settleStiffness * dt));
        }

        private void SettleOn(SFElementRender panel)
        {
            // The panel's centre is measured where the content is now, so the
            // offset applies to the container's current position. Settling
            // after vertical motion only moves vertically: the related grid is
            // wider than one card, and its side cards must not pull the
            // timeline sideways.
            Vector3 offset = Flatten(_focus - PanelUtil.WorldCenter(panel));
            if (_axis == Axis.Vertical) offset = _container.up * Vector3.Dot(offset, _container.up);
            _settleTarget = ToLocal(_container.position + offset);
            _settled = true;
            _velocity = Vector3.zero;
        }

        private void HandleKeys()
        {
            Vector2 dir = Vector2.zero;
            if (Input.GetKeyDown(KeyCode.LeftArrow)) dir = Vector2.left;
            if (Input.GetKeyDown(KeyCode.RightArrow)) dir = Vector2.right;
            if (Input.GetKeyDown(KeyCode.UpArrow)) dir = Vector2.up;
            if (Input.GetKeyDown(KeyCode.DownArrow)) dir = Vector2.down;
            if (dir == Vector2.zero) return;

            // Content moves opposite to where we look: "right" = next panel to the right.
            Vector3 world = _container.right * dir.x + _container.up * dir.y;

            // Where the focus will be in content space once the current glide ends.
            Vector3 pending = _settled ? ToWorld(_settleTarget) - _container.position : Vector3.zero;
            Vector3 from = _focus - pending;

            float cell = CellWidth();
            var here = Nearest(from);
            int column = here != null ? here.element.columnIndex : -1;

            // Up on the timeline row: back to the search this one was dived from.
            if (dir.y > 0f && (here == null || here.element.row == 0)) { GoBack(); return; }

            // Left / right while on a related row: step that row, the timeline stays.
            if (dir.x != 0f && here != null && here.element.row > 0 && _related != null && _related.HasRow(here.element.row))
            {
                _related.StepRow(here.element.row, dir.x > 0f ? 1 : -1);
                return;
            }
            _axis = dir.x != 0f ? Axis.Horizontal : Axis.Vertical;
            _lockedColumn = column;

            SFElementRender best = null;
            float bestScore = float.MaxValue;
            foreach (var p in Panels())
            {
                // Left / right walk the timeline; up / down stay in this column.
                if (dir.x != 0f && p.element.row != 0) continue;
                if (dir.y != 0f && p.element.columnIndex != column) continue;
                Vector3 v = Flatten(PanelUtil.WorldCenter(p) - from);
                float along = Vector3.Dot(v, world);
                if (along < cell * 0.25f) continue;
                float score = along + 2f * (v - world * along).magnitude;
                if (score < bestScore) { bestScore = score; best = p; }
            }
            if (best == null) return;

            Vector3 step = Flatten(from - PanelUtil.WorldCenter(best));
            if (dir.y != 0f) step = _container.up * Vector3.Dot(step, _container.up);   // up / down: no sideways
            _settleTarget = ToLocal(_container.position + pending + step);
            _settled = true;
            _velocity = Vector3.zero;
        }

        // ---- gesture handlers ----------------------------------------------

        // A drag that starts inside the keep board's window scrolls the board;
        // anywhere else it moves the grid.
        private bool _panningBoard;
        private bool _ignoringPan;

        private bool _panningDetail;

        private void OnPanBegan(Vector3 start)
        {
            _panningDetail = _detail != null && _detail.isOpen;
            if (_panningDetail) return;      // the text scrolls itself (ScrollRect)
            _panningBoard = _keepBoard != null && _keepBoard.ContainsWorldPoint(start);
            if (_panningBoard)
            {
                _keepBoard.BeginPan();
                return;
            }
            if (!interactable) { _ignoringPan = true; return; }
            _ignoringPan = false;
            _dragging = true;
            _settled = false;
            _velocity = Vector3.zero;
            _axis = Axis.None;
            _panAccum = Vector3.zero;
            _startColumn = ColumnAt(start);
        }

        private void OnPanned(Vector3 delta)
        {
            if (_panningDetail) return;      // the text's ScrollRect handles its own drags
            else if (_panningBoard) _keepBoard.Pan(Flatten(delta));
            else if (!_ignoringPan) PanGrid(Flatten(delta));
        }

        // ---- axis lock ------------------------------------------------------
        // A drag moves the grid along one axis only, chosen by its first few
        // pixels. Vertical only within the related column of the panel in
        // focus (and only if it has one); everything else is horizontal, on
        // the timeline row.

        // Horizontal moves the timeline; Row scrolls one related row on its own.
        private enum Axis { None, Horizontal, Vertical, Row }
        private int _scrollRow = -1;
        private RelatedColumn _related;
        private Axis _axis = Axis.Horizontal;
        private Vector3 _panAccum;
        private int _lockedColumn = -1;
        private int _startColumn = -1;     // column the drag started in, if it is the focused one

        private void PanGrid(Vector3 d)
        {
            Vector3 right = _container.right, up = _container.up;

            if (_axis == Axis.None)
            {
                _panAccum += d;
                if (_panAccum.magnitude < CellWidth() * 0.02f) return;     // not decided yet

                float h = Vector3.Dot(_panAccum, right), v = Vector3.Dot(_panAccum, up);
                var here = Nearest(_focus);
                int column = here != null ? here.element.columnIndex : -1;
                // Vertical only when grabbed on the focused card or its column.
                bool onTop = here != null && here.element.row == 0;
                bool canScroll = column >= 0 && _startColumn == column && (HasColumn(column) || onTop);
                _axis = Mathf.Abs(v) > Mathf.Abs(h) && canScroll ? Axis.Vertical : Axis.Horizontal;
                _lockedColumn = column;

                // Sideways while down on a related row: scroll that row, not the timeline.
                int row = here != null ? here.element.row : 0;
                if (_axis == Axis.Horizontal && row > 0 && _related != null && _related.HasRow(row))
                {
                    _axis = Axis.Row;
                    _scrollRow = row;
                    _related.BeginRow(row);
                    _settled = true;             // the grid itself stays where it is
                }
                d = _panAccum;                                              // catch up
            }

            if (_axis == Axis.Row)
            {
                _related.ScrollRow(_scrollRow, Vector3.Dot(d, right));
                return;
            }
            if (_axis == Axis.Vertical)
            {
                // Above the timeline row there is nothing: it stretches (pulled
                // far enough and let go, it goes back a search).
                float dv = Vector3.Dot(d, up);
                if (dv < 0f && AboveTop() > 0f) dv *= 0.4f;
                _container.position += up * dv;
            }
            else _container.position += right * Vector3.Dot(d, right);
        }

        // How far the focus is above the timeline row, in card heights (0 if not).
        private float AboveTop()
        {
            SFElementRender top = null;
            foreach (var p in Panels())
                if (p.element.row == 0 && p.element.columnIndex == _lockedColumn) { top = p; break; }
            if (top == null) return 0f;
            float h = top.bounds.size.y * Mathf.Abs(top.transform.lossyScale.y);
            if (h <= 0f) return 0f;
            return Mathf.Max(0f, Vector3.Dot(_focus - PanelUtil.WorldCenter(top), _container.up) / h);
        }

        // ---- back ------------------------------------------------------------

        private XrProbe.Result _returnTo;
        private float _returnSince;

        private SearchHistory _history;

        /// <summary>Back to the previous search (SearchHistory), then to the card dived from.</summary>
        public void GoBack()
        {
            if (_history == null) _history = FindFirstObjectByType<SearchHistory>();
            _history?.GoBack();
        }

        /// <summary>After the next search arrives, return to this card.</summary>
        public void ReturnTo(XrProbe.Result card)
        {
            _returnTo = card;
            _returnSince = Time.time;
        }

        // Once the earlier search shows the card you dived from, glide to it;
        // if it was not a timeline card, bring it into the row below when the
        // search is done.
        private void ReturnToCard()
        {
            if (_returnTo == null || _dragging) return;
            if (Time.time - _returnSince > 30f) { _returnTo = null; return; }
            foreach (var p in Panels())
            {
                if (p.element?.data == null || p.element.row != 0) continue;
                if (SameCard(p.element.data, _returnTo)) { _returnTo = null; GlideTo(p); return; }
            }
            if (_search != null && !_search.searching && Time.time - _returnSince > 1f && _related != null)
            {
                var r = _returnTo;
                _returnTo = null;
                _related.Bring(r, r.YearBegin ?? (_search.yearFrom + _search.yearTo) / 2);
            }
        }

        private static bool SameCard(XrProbe.Result a, XrProbe.Result b)
        {
            var x = a.text ?? a.image; var y = b.text ?? b.image;
            if (x == null || y == null) return false;
            if (!string.IsNullOrEmpty(x.SourceUrl) && x.SourceUrl == y.SourceUrl && x.title == y.title) return true;
            var xi = a.image?.ImageUrl; var yi = b.image?.ImageUrl;
            return !string.IsNullOrEmpty(xi) && xi == yi;
        }

        // The focused card's column index if a world point lies on the focused
        // card or across the related grid below it; -1 elsewhere.
        private int ColumnAt(Vector3 world)
        {
            var here = Nearest(_focus);
            if (here == null) return -1;
            int column = here.element.columnIndex;
            Vector3 right = _container.right;
            foreach (var p in Panels())
            {
                if (p.element.columnIndex != column) continue;
                float half = PanelUtil.WorldSize(p).x * 0.5f;
                float dx = Vector3.Dot(world - PanelUtil.WorldCenter(p), right);
                if (Mathf.Abs(dx) <= half) return column;
            }
            return -1;
        }

        private bool HasColumn(int column)
        {
            if (column < 0) return false;
            foreach (var p in Panels())
                if (p.element.row > 0 && p.element.columnIndex == column) return true;
            return false;
        }

        // Nudge the grid vertically so the timeline row is at the focus.
        private void ReturnToRow(float dt)
        {
            foreach (var p in Panels())
            {
                if (p.element.row != 0) continue;
                Vector3 up = _container.up;
                float off = Vector3.Dot(_focus - PanelUtil.WorldCenter(p), up);
                _container.position += up * off * (1f - Mathf.Exp(-12f * dt));
                return;
            }
        }

        // Where a released drag / fling settles: on the timeline row after
        // horizontal motion, in the locked column after vertical motion.
        private SFElementRender NearestOnAxis(Vector3 world)
        {
            SFElementRender best = null;
            float bestD = float.MaxValue;
            foreach (var p in Panels())
            {
                if (_axis == Axis.Horizontal && p.element.row != 0) continue;
                if (_axis == Axis.Vertical && p.element.columnIndex != _lockedColumn) continue;
                float d = Flatten(PanelUtil.WorldCenter(p) - world).sqrMagnitude;
                if (d < bestD) { bestD = d; best = p; }
            }
            return best ?? Nearest(world);
        }

        private void OnPanEnded(Vector3 velocity)
        {
            if (_panningDetail) { _panningDetail = false; return; }
            if (_panningBoard)
            {
                _panningBoard = false;
                _keepBoard.EndPan(Flatten(velocity));
                return;
            }
            if (_ignoringPan) { _ignoringPan = false; return; }
            _dragging = false;
            if (_axis == Axis.Row)
            {
                _related.EndRow(_scrollRow, Vector3.Dot(Flatten(velocity), _container.right));
                _velocity = Vector3.zero;
                _settled = true;
                return;
            }
            if (_axis == Axis.Vertical && AboveTop() > 0.35f) { _velocity = Vector3.zero; _settled = false; GoBack(); return; }
            if (_axis == Axis.None) _axis = Axis.Horizontal;   // a drag too small to decide
            Vector3 v = Flatten(velocity), axis = _axis == Axis.Vertical ? _container.up : _container.right;
            _velocity = axis * Vector3.Dot(v, axis);
        }

        private void OnTapped(SFElementRender panel)
        {
            if (Owns(panel))
            {
                if (!interactable) return;
                _settled = false;
                SettleOn(panel);
            }
            // On the board a single tap does nothing (yet): searching from a
            // kept item is a double tap, so it never happens by accident.
        }

        // Double tap = open the panel in the detail layer - grid panels, and
        // kept items while the board is in front.
        // Double tap: on the grid, open the panel in the detail layer; on the
        // board, search from that kept item (or re-run that search node).
        private void OnDoubleTapped(SFElementRender panel)
        {
            if (panel == null) return;
            if (Owns(panel) && interactable)
            {
                // A new search from it (the detail view: Enter, or spread past the maximum zoom).
                if (SearchHistory.IsBackCard(panel)) { GoBack(); return; }
                bool placeholder = panel is CoverCard c && c.isSkeleton
                                || string.IsNullOrEmpty((panel.element.data.text ?? panel.element.data.image)?.title);
                if (placeholder) return;
                if (_history == null) _history = FindFirstObjectByType<SearchHistory>();
                _history?.NoteDive(panel.element.data);
                if (_keepBoard != null) _keepBoard.SearchFrom(panel.element, panel);
                else _detail?.Open(panel);
            }
            else if (_keepBoard != null && _keepBoard.Owns(panel) && _keepBoard.visible)
            {
                _keepBoard.Select(panel);
            }
        }

        // Keep without pulling: the panel pops toward the viewer and settles
        // back as its copy flies to the board.
        private void KeepWithPop(SFElementRender panel)
        {
            if (!interactable || !Owns(panel)) return;
            _dismissed.Remove(panel.element);
            PanelUtil.Group(panel).alpha = 1f;
            _keepBoard?.Keep(panel.element, panel);
            var fx = FxFor(panel);
            fx.pull = 0.6f;
            fx.pullTarget = 0f;
        }

        // PC convenience: right click a grid panel to keep it. On Quest,
        // keep is pulling the panel out of the glass.
        // PC convenience: right click a grid panel to keep it; right click (or
        // Delete) a kept item on the board to remove it. On Quest: pull a
        // panel out to keep it.
        private void HandleKeepClick()
        {
            bool right = Input.GetMouseButtonDown(1);
            bool del = Input.GetKeyDown(KeyCode.Delete);
            if ((!right && !del) || _gestures == null) return;
            var hand = _gestures.hands != null ? _gestures.hands.GetHand(Hand.Right) : default;
            var panel = _gestures.RaycastPanel(hand.ray, out _);
            if (panel == null) return;

            // The board is the history now: nothing is kept by hand. A right
            // click / Delete on the board still removes an entry.
            if (_keepBoard != null && _keepBoard.visible && _keepBoard.Owns(panel)) _keepBoard.Remove(panel);
        }

        private void OnPulling(SFElementRender panel, float travel)
        {
            if (!interactable) return;
            if (!Owns(panel)) return;
            // Pulling toward you no longer keeps anything: only pushing away
            // (dismiss) gives feedback.
            if (travel >= 0f) { FxFor(panel).pullTarget = 0f; return; }
            float progress = travel / Mathf.Max(1e-4f, _gestures.DismissDistance);
            FxFor(panel).pullTarget = Mathf.Clamp(progress, -1f, 1f);
        }

        private void OnPullCancelled(SFElementRender panel)
        {
            if (_fx.TryGetValue(panel, out var fx)) fx.pullTarget = 0f;
        }

        private void OnKept(SFElementRender panel)
        {
            // Keeping by hand is gone (the board is the search history); the
            // pulled card just settles back.
            if (_fx.TryGetValue(panel, out var fx)) fx.pullTarget = 0f;
        }

        private void OnDismissed(SFElementRender panel)
        {
            if (!interactable) return;
            if (!Owns(panel)) return;
            _dismissed.Add(panel.element);
            FxFor(panel).pullTarget = 0f;
        }

        private void OnHoverChanged(SFElementRender panel)
        {
            foreach (var kv in _fx) kv.Value.hoverTarget = 0f;
            if (interactable && panel != null && Owns(panel)) FxFor(panel).hoverTarget = 1f;
        }

        // While the keep board is in front, two-hand zoom zooms the board.
        private bool _zoomingBoard;

        // Two-hand zoom on an open detail closes (pinch in) or reopens it.
        private bool _zoomingDetail;
        // A grid zoom pushed past its maximum is opening a detail.
        private bool _openingDetail;
        private SFElementRender _zoomPanel;
        private Vector3 _zoomPivotLocal;

        /// <summary>A two-hand pinch went on past the smallest zoom.</summary>
        public event System.Action ZoomedOutPastMin;
        private bool _zoomedOutFired;

        private void OnZoomBegan(Vector3 pivot)
        {
            if (_detail != null && _detail.isOpen)
            {
                _zoomingDetail = true;
                _detail.BeginPinch();
                return;
            }
            if (_keepBoard != null && _keepBoard.visible)
            {
                _zoomingBoard = true;
                _keepBoard.BeginZoom(pivot);
                return;
            }
            if (!interactable) return;
            _zooming = true;
            _zoomedOutFired = false;
            _settled = false;
            _velocity = Vector3.zero;
            _zoomPivot = pivot;
            // Parent space, so a parent layer moving (BoardPeek) does not fight it.
            _zoomPivotLocal = ToLocal(pivot);
            _zoomStartPos = _container.localPosition;
            _zoomStartScale = _container.localScale;
            _zoomPanel = Nearest(pivot);
            _openingDetail = false;
        }

        private void OnZoomed(float ratio)
        {
            if (_zoomingDetail) { _detail.Pinch(ratio); return; }
            if (_zoomingBoard) { _keepBoard.Zoom(ratio); return; }
            if (!_zooming) return;
            float start = Mathf.Max(1e-4f, _zoomStartScale.x);
            float max = _maxZoom * BaseScale();
            float raw = start * ratio;

            // Keep spreading past the maximum: the panel under the hands
            // opens up into the detail layer, following the hands.
            if (_detail != null && _zoomPanel != null)
            {
                float overflow = raw / max;
                if (!_openingDetail && overflow > 1.02f)
                {
                    _openingDetail = true;
                    _detail.BeginGestureOpen(_zoomPanel);
                }
                if (_openingDetail) _detail.Drive(_detail.ProgressForOverflow(overflow));
            }

            // Keep pinching in past the minimum: the whole harvest (the wall).
            if (!_zoomedOutFired && raw < _minZoom * BaseScale() * 0.8f)
            {
                _zoomedOutFired = true;
                ZoomedOutPastMin?.Invoke();
            }

            // Scale relative to the zoom-start, clamped in absolute terms.
            float target = Mathf.Clamp(raw, _minZoom * BaseScale(), max);
            float k = target / start;
            _container.localScale = _zoomStartScale * k;
            _container.localPosition = _zoomPivotLocal + (_zoomStartPos - _zoomPivotLocal) * k;
        }

        private void OnZoomEnded()
        {
            if (_zoomingDetail)
            {
                _zoomingDetail = false;
                _detail.EndGesture();
                return;
            }
            if (_openingDetail)
            {
                _openingDetail = false;
                _detail.EndGesture();
            }
            if (_zoomingBoard)
            {
                _zoomingBoard = false;
                _keepBoard.EndZoom();
                return;
            }
            _zooming = false;
        }

        // An open detail closes with a tap outside the sheet, or a double tap
        // anywhere - the same gesture that opened it. (With the sheet open no
        // panel can be hit, so every tap arrives here as an empty one.)
        private float _lastEmptyTap = -1f;

        private void OnTappedEmpty(Vector3 point)
        {
            if (_detail == null || !_detail.isOpen) { _lastEmptyTap = -1f; return; }

            bool second = _lastEmptyTap >= 0f && Time.time - _lastEmptyTap <= _gestures.DoubleTapSeconds;
            _lastEmptyTap = second ? -1f : Time.time;

            if (second || !_detail.SheetContains(point)) _detail.Close();
        }

        // PC convenience: Enter opens the panel under the cursor (grid or
        // board) or the one in the middle; Enter, Esc or Backspace close; the
        // wheel scrolls the open text. On Quest: zoom past max, pinch in.
        // Detail, main and harvest are one zoom: + (or Enter) goes in, - goes
        // out, Esc goes back to main - the same keys on every one of them (the
        // wall handles its own while it is in front). Two hands: spread / pinch.
        private HarvestWall _wall;

        private void HandleDetailKeys()
        {
            if (_detail == null) return;
            if (_wall == null) _wall = FindFirstObjectByType<HarvestWall>();
            if (_wall != null && _wall.shown) return;
            if (_keepBoard != null && _keepBoard.visible) return;      // the board's + / - zoom it
            bool zoomIn = HarvestWall.ZoomInKey(), zoomOut = HarvestWall.ZoomOutKey();

            if (_detail.isOpen)
            {
                if (zoomOut || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)
                    || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) _detail.Close();
                // The wheel reaches the text's ScrollRect through the EventSystem.
                return;
            }

            if (zoomOut && interactable) { _wall?.Open(); return; }
            if (!zoomIn) return;
            var target = _gestures != null ? _gestures.hovered : null;
            if (target == null && interactable) target = Nearest(_focus);
            if (target != null) _detail.Open(target);
        }

        // PC convenience: with the board in front, the wheel (and + / -) zoom
        // it around the cursor. Pull / push mean nothing on the board, so the
        // wheel is free there. On Quest this is the two-hand spread only.
        private void HandleBoardZoomKeys()
        {
            if (_keepBoard == null || !_keepBoard.visible || _gestures == null || _gestures.hands == null) return;

            float steps = MouseHandSource.WheelIsDepth ? 0f : Input.mouseScrollDelta.y;
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus)) steps += 1f;
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus)) steps -= 1f;
            if (steps == 0f) return;

            HandState h = _gestures.hands.GetHand(Hand.Right);
            if (!_gestures.RayToGlass(h.ray, out Vector3 p) || !_keepBoard.ContainsWorldPoint(p))
                p = _keepBoard.viewport.TransformPoint(_keepBoard.viewport.rect.center);
            _keepBoard.ZoomBy(Mathf.Pow(_boardZoomStep, steps), p);
        }

        // ---- panel feedback ------------------------------------------------

        private void LateUpdate()
        {
            if (_document == null) return;

            Transform head = _gestures != null && _gestures.hands != null ? _gestures.hands.head : null;
            float k = 1f - Mathf.Exp(-_feedbackSpeed * Time.deltaTime);
            _done.Clear();

            foreach (var kv in _fx)
            {
                var panel = kv.Key;
                var fx = kv.Value;
                if (panel == null) { _done.Add(panel); continue; }

                fx.pull = Mathf.Lerp(fx.pull, fx.pullTarget, k);
                fx.hover = Mathf.Lerp(fx.hover, fx.hoverTarget, k);

                Vector3 rest = _document.GetLayoutPosition(panel);
                Vector3 offset = Vector3.zero;
                if (head != null)
                {
                    Vector3 restWorld = panel.transform.parent != null
                        ? panel.transform.parent.TransformPoint(rest) : rest;
                    Vector3 toHead = head.position - restWorld;
                    Vector3 world = toHead * (_pullTravel * fx.pull);
                    offset = panel.transform.parent != null
                        ? panel.transform.parent.InverseTransformVector(world) : world;
                }
                panel.transform.localPosition = rest + offset;

                // No hover scale: the card in focus gets a border instead.
                float s = 1f;
                s *= 1f + (_pullScale - 1f) * Mathf.Max(fx.pull, 0f);
                panel.transform.localScale = fx.baseScale * s;

                float alpha = _dismissed.Contains(panel.element) ? _dismissedAlpha : 1f;
                alpha = Mathf.Lerp(alpha, _dismissedAlpha, Mathf.Max(-fx.pull, 0f));
                PanelUtil.Group(panel).alpha = alpha;

                bool idle = fx.pullTarget == 0f && fx.hoverTarget == 0f
                         && Mathf.Abs(fx.pull) < 0.002f && fx.hover < 0.002f;
                if (idle)
                {
                    panel.transform.localPosition = rest;
                    panel.transform.localScale = fx.baseScale;
                    _done.Add(panel);
                }
            }

            foreach (var p in _done) _fx.Remove(p);

            UpdateFocusFrame();
        }

        // ---- focus border -----------------------------------------------------

        private UIFrame _focusFrame;

        // One frame, moved to whichever card is nearest the focus point. Hidden
        // while another layer (board, detail) is in front.
        private void UpdateFocusFrame()
        {
            var panel = interactable ? Nearest(_focus) : null;
            if (panel == null)
            {
                if (_focusFrame != null) _focusFrame.enabled = false;
                return;
            }

            if (_focusFrame == null)
            {
                var go = new GameObject("FocusFrame", typeof(RectTransform), typeof(CanvasRenderer), typeof(UIFrame));
                _focusFrame = go.GetComponent<UIFrame>();
                _focusFrame.raycastTarget = false;
            }
            var rt = _focusFrame.rectTransform;
            if (rt.parent != panel.transform)
            {
                rt.SetParent(panel.transform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.pivot = new Vector2(0.5f, 0.5f);
                _focusFrame.gameObject.layer = panel.gameObject.layer;
            }
            rt.SetAsLastSibling();

            // Thickness in the card's own units, so it looks the same at any zoom.
            float scale = Mathf.Max(1e-4f, Mathf.Abs(panel.transform.localScale.x) * Mathf.Abs(_container.localScale.x) / Mathf.Max(1e-4f, _baseScale));
            float t = _focusThickness / scale;
            if (!Mathf.Approximately(_focusFrame.thickness, t)) _focusFrame.thickness = t;
            rt.offsetMin = new Vector2(-t * 0.5f, -t * 0.5f);
            rt.offsetMax = new Vector2(t * 0.5f, t * 0.5f);
            _focusFrame.color = _focusColor;
            _focusFrame.enabled = true;
        }

        private Fx FxFor(SFElementRender panel)
        {
            if (!_fx.TryGetValue(panel, out var fx))
            {
                fx = new Fx { baseScale = panel.transform.localScale };
                _fx[panel] = fx;
            }
            return fx;
        }

        // ---- helpers -------------------------------------------------------

        private bool Owns(SFElementRender panel) =>
            panel != null && _document != null && _document.renderRoot != null
            && panel.transform.IsChildOf(_document.renderRoot);

        private IEnumerable<SFElementRender> Panels()
        {
            if (_document == null || _document.renderRoot == null) yield break;
            foreach (Transform child in _document.renderRoot)
            {
                var p = child.GetComponent<SFElementRender>();
                if (p != null && child.gameObject.activeInHierarchy) yield return p;
            }
        }

        private SFElementRender Nearest(Vector3 world)
        {
            SFElementRender best = null;
            float bestD = float.MaxValue;
            foreach (var p in Panels())
            {
                float d = Flatten(PanelUtil.WorldCenter(p) - world).sqrMagnitude;
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        private float CellWidth()
        {
            foreach (var p in Panels()) return Mathf.Max(1e-4f, PanelUtil.WorldSize(p).x);
            return 1f;
        }

        private float _baseScale = 1f;
        private float BaseScale() => _baseScale;

        /// <summary>Move the content sideways by whole columns without anything
        /// appearing to move: used when results are inserted before the one in
        /// focus (the timeline growing at its start).</summary>
        public void ShiftColumns(int columns)
        {
            if (_container == null || _document == null || columns == 0) return;
            SFElementRender any = null;
            foreach (var p in Panels()) { any = p; break; }
            if (any == null) return;
            Vector3 d = -_container.right * (_document.ColumnPitchWorld(any) * columns);
            Vector3 before = ToLocal(_container.position);
            _container.position += d;
            _settleTarget += ToLocal(_container.position) - before;
        }

        /// <summary>Glide until this panel is at the focus point.</summary>
        public void GlideTo(SFElementRender p)
        {
            if (p == null || _container == null) return;
            _dragging = _zooming = false;
            _velocity = Vector3.zero;
            Vector3 pending = _settled ? ToWorld(_settleTarget) - _container.position : Vector3.zero;
            Vector3 step = Flatten(_focus - pending - PanelUtil.WorldCenter(p));
            _settleTarget = ToLocal(_container.position + pending + step);
            _settled = true;
        }

        /// <summary>The focus point (world), where the card you are on sits.</summary>
        public Vector3 focusPoint => _focus;

        /// <summary>The panel nearest the focus point right now (moving or not).</summary>
        public SFElementRender FocusPanel() => Nearest(_focus);

        /// <summary>The panel at the focus point once the grid has come to rest
        /// on it (no drag, zoom, fling or glide in progress); null while moving.</summary>
        public SFElementRender SettledPanel()
        {
            if (_dragging || _zooming || !_settled || _velocity != Vector3.zero || _container == null) return null;
            if ((ToWorld(_settleTarget) - _container.position).magnitude > CellWidth() * 0.05f) return null;
            return Nearest(_focus);
        }

        private Vector3 ToLocal(Vector3 world) =>
            _container.parent != null ? _container.parent.InverseTransformPoint(world) : world;

        private Vector3 ToWorld(Vector3 local) =>
            _container.parent != null ? _container.parent.TransformPoint(local) : local;

        /// <summary>Removes the component along the glass normal.</summary>
        private Vector3 Flatten(Vector3 v)
        {
            Vector3 n = _container.forward;
            return v - n * Vector3.Dot(v, n);
        }
    }
}
