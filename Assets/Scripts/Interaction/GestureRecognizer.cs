// Turns raw hands into the handful of gestures the app understands.
//
//   pinch empty glass, drag       -> pan (content sticks under the ray, glass-like)
//   pinch a panel, drag sideways  -> pan as well (panels cover most of the glass)
//   pinch a panel, pull to you    -> Pulling(+) ... Kept   (pull it out of the glass)
//   pinch a panel, push away      -> Pulling(-) ... Dismissed
//   quick pinch on a panel        -> Tapped (after the double-tap window)
//   two quick pinches on a panel  -> DoubleTapped
//   two hands pinching            -> zoom by their spread
//
// A pinch on a panel is ambiguous for the first few centimetres; whichever
// of lateral or depth travel crosses its threshold first decides. Thresholds
// are hand travel in metres - the same on PC (MouseHandSource) and on Quest.
//
// Kept deliberately small. Minority Report gestures look great and tire the
// arm in minutes: every gesture here works with the elbow resting (ray-pinch),
// none needs the arm raised to the panel.

using System;
using System.Collections.Generic;
using SpatialForms;
using UnityEngine;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(-50)]
    public class GestureRecognizer : MonoBehaviour
    {
        [SerializeField] private HandSource _hands;

        [Tooltip("Defines the glass plane (its forward is the plane normal). " +
                 "Usually the grid container.")]
        [SerializeField] private Transform _glass;

        [SerializeField] private LayerMask _panelMask = ~0;

        [Header("Thresholds - metres of hand travel")]
        [SerializeField] private float _panStart = 0.015f;
        [SerializeField] private float _pullStart = 0.025f;
        [SerializeField] private float _keepDistance = 0.12f;
        [SerializeField] private float _dismissDistance = 0.12f;

        [Tooltip("A pinch shorter than this, that did not turn into anything else, is a tap.")]
        [SerializeField] private float _tapMaxSeconds = 0.35f;

        [Tooltip("Second tap on the same panel within this time is a double tap. A single " +
                 "tap is reported only once this window has passed, so the panel does not " +
                 "start moving away between the two taps.")]
        [SerializeField] private float _doubleTapSeconds = 0.3f;

        [Header("Optional")]
        [Tooltip("Small marker drawn where the ray meets the glass / a panel.")]
        [SerializeField] private Transform _cursor;

        [Tooltip("Cursor size as a fraction of its distance from the head.")]
        [SerializeField] private float _cursorSize = 0.008f;

        // ---- events --------------------------------------------------------

        public event Action<SFElementRender> HoverChanged;

        public event Action<Vector3> PanBegan;          // world point under the ray
        public event Action<Vector3> Panned;            // world delta in the glass plane
        public event Action<Vector3> PanEnded;          // world velocity in the glass plane

        public event Action<SFElementRender> Tapped;
        public event Action<SFElementRender> DoubleTapped;
        /// <summary>A quick pinch on empty glass (no panel), with the point on the glass.</summary>
        public event Action<Vector3> TappedEmpty;

        /// <summary>Signed hand travel in metres: + toward the viewer, - away.
        /// Normalised progress is travel / KeepDistance (or DismissDistance).</summary>
        public event Action<SFElementRender, float> Pulling;
        public event Action<SFElementRender> PullCancelled;
        public event Action<SFElementRender> Kept;
        public event Action<SFElementRender> Dismissed;

        public event Action<Vector3> ZoomBegan;         // pivot on the glass
        public event Action<float> Zoomed;              // spread ratio since ZoomBegan
        public event Action ZoomEnded;

        public float KeepDistance => _keepDistance;
        public float DoubleTapSeconds => _doubleTapSeconds;
        public float DismissDistance => _dismissDistance;
        public HandSource hands => _hands;
        public Transform glass => _glass;
        public Transform cursor => _cursor;

        public SFElementRender hovered { get; private set; }

        // ---- state ---------------------------------------------------------

        private enum Phase { Idle, Pending, Panning, Pulling, Zooming, Consumed }

        private Phase _phase;
        private Hand _primary = Hand.Right;
        private bool _wasPinching;
        private bool _wasTwoHand;

        private SFElementRender _target;
        private Vector3 _pinchStart;       // hand position when the pinch began
        private Vector3 _towardHead;       // unit vector, fixed at pinch start
        private float _pinchTime;

        private Vector3 _lastPlanePoint;
        private Vector3 _velocity;

        private float _zoomStartSpread;

        private float _maxLateral;        // furthest sideways hand travel during this pinch
        private SFElementRender _pendingTap;   // first tap, waiting to see if a second follows
        private float _pendingTapTime;

        public Plane GlassPlane =>
            new Plane(_glass.forward, _glass.position);

        private void Awake()
        {
            // A primitive sphere as cursor comes with a collider, which would
            // sit on the ray in front of every panel and swallow the hit.
            if (_cursor != null)
                foreach (var c in _cursor.GetComponentsInChildren<Collider>(true))
                    Destroy(c);
        }

        private void Update()
        {
            if (_hands == null || _glass == null) return;

            HandState r = _hands.GetHand(Hand.Right);
            HandState l = _hands.GetHand(Hand.Left);

            // The primary hand is whichever started pinching first.
            bool rp = r.tracked && r.pinching;
            bool lp = l.tracked && l.pinching;
            if (!_wasPinching)
            {
                if (rp) _primary = Hand.Right;
                else if (lp) _primary = Hand.Left;
            }

            HandState h = _primary == Hand.Right ? r : l;
            bool pinching = h.tracked && h.pinching;
            bool twoHand = rp && lp;

            if (twoHand && !_wasTwoHand) BeginZoom(r, l);
            else if (twoHand) Zoomed?.Invoke(Spread(r, l) / Mathf.Max(1e-4f, _zoomStartSpread));
            else if (_wasTwoHand) EndZoom();
            else if (pinching && !_wasPinching) BeginPinch(h);
            else if (pinching) UpdatePinch(h);
            else if (_wasPinching) EndPinch();
            else UpdateHover(h);

            _wasPinching = pinching || twoHand;
            _wasTwoHand = twoHand;

            // A single tap only counts once no second tap can follow.
            if (_pendingTap != null && Time.time - _pendingTapTime > _doubleTapSeconds)
            {
                var t = _pendingTap;
                _pendingTap = null;
                Tapped?.Invoke(t);
            }

            UpdateCursor(h);
        }

        // ---- single hand ---------------------------------------------------

        private void BeginPinch(HandState h)
        {
            _pinchStart = h.position;
            _pinchTime = Time.time;
            _maxLateral = 0f;
            _velocity = Vector3.zero;

            Transform head = _hands.head;
            _towardHead = head != null ? (head.position - h.position).normalized : -_glass.forward;

            // Standard UI (a ScrollRect, a button, a text field) handles its own
            // input through the EventSystem: leave that pinch to it.
            if (OverEventSystemUI(h.ray))
            {
                _phase = Phase.Consumed;
                return;
            }

            _target = RaycastPanel(h.ray, out _);

            if (!RayToGlass(h.ray, out _lastPlanePoint))
            {
                _phase = Phase.Consumed;     // pointing away from the glass
                return;
            }

            if (_target != null)
            {
                _phase = Phase.Pending;
            }
            else
            {
                _phase = Phase.Panning;
                PanBegan?.Invoke(_lastPlanePoint);
            }
        }

        private void UpdatePinch(HandState h)
        {
            Vector3 d = h.position - _pinchStart;
            float depth = Vector3.Dot(d, _towardHead);
            float lateral = (d - _towardHead * depth).magnitude;
            _maxLateral = Mathf.Max(_maxLateral, lateral);

            switch (_phase)
            {
                case Phase.Pending:
                    if (Mathf.Abs(depth) > _pullStart && Mathf.Abs(depth) > lateral)
                    {
                        _phase = Phase.Pulling;
                        Pulling?.Invoke(_target, depth);
                    }
                    else if (lateral > _panStart)
                    {
                        _phase = Phase.Panning;
                        PanBegan?.Invoke(_lastPlanePoint);
                        TrackPan(h);
                    }
                    break;

                case Phase.Panning:
                    TrackPan(h);
                    break;

                case Phase.Pulling:
                    if (_target == null) { _phase = Phase.Consumed; break; }
                    Pulling?.Invoke(_target, depth);
                    if (depth >= _keepDistance)
                    {
                        Kept?.Invoke(_target);
                        _phase = Phase.Consumed;
                    }
                    else if (depth <= -_dismissDistance)
                    {
                        Dismissed?.Invoke(_target);
                        _phase = Phase.Consumed;
                    }
                    break;
            }
        }

        private void TrackPan(HandState h)
        {
            if (!RayToGlass(h.ray, out Vector3 p)) return;
            Vector3 delta = p - _lastPlanePoint;
            _lastPlanePoint = p;

            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            // Light smoothing so the release velocity is not one jittery frame.
            _velocity = Vector3.Lerp(_velocity, delta / dt, 0.5f);

            if (delta.sqrMagnitude > 0f) Panned?.Invoke(delta);
        }

        private void EndPinch()
        {
            switch (_phase)
            {
                case Phase.Pending:
                    if (_target != null && Time.time - _pinchTime <= _tapMaxSeconds)
                    {
                        if (_pendingTap == _target && Time.time - _pendingTapTime <= _doubleTapSeconds)
                        {
                            _pendingTap = null;
                            DoubleTapped?.Invoke(_target);
                        }
                        else
                        {
                            // A tap on another panel settles the previous one first.
                            if (_pendingTap != null) Tapped?.Invoke(_pendingTap);
                            _pendingTap = _target;
                            _pendingTapTime = Time.time;
                        }
                    }
                    break;

                case Phase.Panning:
                    PanEnded?.Invoke(_velocity);
                    if (_target == null && _maxLateral < _panStart && Time.time - _pinchTime <= _tapMaxSeconds)
                        TappedEmpty?.Invoke(_lastPlanePoint);
                    break;

                case Phase.Pulling:
                    if (_target != null) PullCancelled?.Invoke(_target);
                    break;
            }

            _phase = Phase.Idle;
            _target = null;
        }

        // ---- two hands -----------------------------------------------------

        private void BeginZoom(HandState r, HandState l)
        {
            // A second hand joining cancels whatever the first one was doing.
            if (_phase == Phase.Panning) PanEnded?.Invoke(Vector3.zero);
            if (_phase == Phase.Pulling && _target != null) PullCancelled?.Invoke(_target);
            _target = null;

            _phase = Phase.Zooming;
            _zoomStartSpread = Spread(r, l);

            Ray mid = new Ray((r.ray.origin + l.ray.origin) * 0.5f,
                              (r.ray.direction + l.ray.direction).normalized);
            if (!RayToGlass(mid, out Vector3 pivot)) pivot = _glass.position;
            ZoomBegan?.Invoke(pivot);
        }

        private void EndZoom()
        {
            ZoomEnded?.Invoke();
            // The remaining hand must be released before it can start anything,
            // otherwise lifting one hand off a zoom turns into a sudden pan.
            _phase = Phase.Consumed;
        }

        private static float Spread(HandState r, HandState l) =>
            Vector3.Distance(r.position, l.position);

        // ---- hover / cursor ------------------------------------------------

        private void UpdateHover(HandState h)
        {
            if (_phase == Phase.Consumed) _phase = Phase.Idle;

            SFElementRender over = h.tracked ? RaycastPanel(h.ray, out _) : null;
            if (over != hovered)
            {
                hovered = over;
                HoverChanged?.Invoke(over);
            }
        }

        private void UpdateCursor(HandState h)
        {
            if (_cursor == null) return;

            _cursor.gameObject.SetActive(h.tracked);
            if (!h.tracked) return;

            Vector3 p;
            if (RaycastPanel(h.ray, out RaycastHit hit) != null) p = hit.point;
            else if (!RayToGlass(h.ray, out p)) p = h.ray.GetPoint(1f);

            float dist = Vector3.Distance(h.ray.origin, p);
            // Pulled slightly toward the viewer so it never sinks into a panel.
            _cursor.position = p - h.ray.direction * dist * 0.01f;
            _cursor.rotation = Quaternion.LookRotation(h.ray.direction);
            float size = dist * _cursorSize * (h.pinching ? 0.6f : 1f);
            _cursor.localScale = Vector3.one * size;
        }

        // ---- queries -------------------------------------------------------

        private readonly RaycastHit[] _hits = new RaycastHit[32];

        // Panels share the glass plane, so nearest-hit is not enough: the
        // highest PanelLayer wins, and a layer's area hides the ones below it.
        public SFElementRender RaycastPanel(Ray ray, out RaycastHit hit)
        {
            hit = default;
            int n = Physics.RaycastNonAlloc(ray, _hits, Mathf.Infinity, _panelMask, QueryTriggerInteraction.Collide);

            SFElementRender best = null;
            int bestPriority = int.MinValue;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var panel = _hits[i].collider.GetComponentInParent<SFElementRender>();
                if (panel == null) continue;
                int prio = PanelLayer.PriorityOf(panel);
                if (PanelLayer.IsHidden(prio, _hits[i].point)) continue;
                if (prio > bestPriority || (prio == bestPriority && _hits[i].distance < bestDistance))
                {
                    best = panel;
                    bestPriority = prio;
                    bestDistance = _hits[i].distance;
                    hit = _hits[i];
                }
            }
            return best;
        }

        private static readonly List<UnityEngine.EventSystems.RaycastResult> _uiHits = new List<UnityEngine.EventSystems.RaycastResult>();

        /// <summary>Is there UI under this ray that takes input itself
        /// (scroll, drag, click, select)? Asked of the EventSystem, at the
        /// point where the ray meets the glass, seen by the panel camera.</summary>
        public bool OverEventSystemUI(Ray ray)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null || !RayToGlass(ray, out Vector3 p)) return false;
            var canvas = _glass.GetComponentInParent<Canvas>();
            var cam = canvas != null ? canvas.rootCanvas.worldCamera : null;
            if (cam == null) cam = Camera.main;
            if (cam == null) return false;
            Vector3 sp = cam.WorldToScreenPoint(p);
            var data = new UnityEngine.EventSystems.PointerEventData(es) { position = sp };
            _uiHits.Clear();
            es.RaycastAll(data, _uiHits);
            foreach (var hit in _uiHits)
            {
                var go = hit.gameObject;
                if (go == null) continue;
                if (UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IScrollHandler>(go) != null ||
                    UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IBeginDragHandler>(go) != null ||
                    UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(go) != null ||
                    UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.ISelectHandler>(go) != null)
                    return true;
            }
            return false;
        }

        public bool RayToGlass(Ray ray, out Vector3 point)
        {
            if (GlassPlane.Raycast(ray, out float t) && t > 0f)
            {
                point = ray.GetPoint(t);
                return true;
            }
            point = default;
            return false;
        }
    }
}
