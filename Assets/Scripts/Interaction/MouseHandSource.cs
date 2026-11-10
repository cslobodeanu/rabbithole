// PC stand-in for two tracked hands.
//
//   move mouse            hand ray follows the cursor
//   left button           pinch (right hand), held
//   Ctrl + wheel          push / pull: pinches by itself and moves the hand
//                         toward you (wheel down = pull) / away (wheel up =
//                         push), over the panel under the cursor. The plain
//                         wheel stays free (scrolling, zooming the board / wall).
//                         "Wheel Moves Hand" on: the plain wheel does it too.
//   Alt + left button     both hands pinch, mirrored around where you clicked;
//                         moving the mouse spreads / closes them (zoom)
//
// The hand is placed at arm's length along the cursor ray, so the recogniser
// sees hand travel in metres, the same unit a real hand reports.

using UnityEngine;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(-100)]
    public class MouseHandSource : HandSource
    {
        [Tooltip("Camera that renders the panels. For a Screen Space - Camera canvas, " +
                 "this is the canvas' Render Camera. Falls back to Camera.main.")]
        [SerializeField] private Camera _camera;

        [Tooltip("Where the emulated hand rests in front of the head, in metres.")]
        [SerializeField] private float _restDepth = 0.45f;

        [Tooltip("Let the wheel move the hand toward / away from you (pull to keep, push " +
                 "to dismiss). Off: the wheel does nothing to the hand.")]
        [SerializeField] private bool _wheelMovesHand = false;

        [Tooltip("Hand travel per wheel notch, in metres. Default keep / dismiss " +
                 "threshold is 0.12 m, i.e. 4 notches.")]
        [SerializeField] private float _depthPerNotch = 0.03f;

        [Tooltip("Optional key that pinches while held, as an alternative to the left " +
                 "mouse button. None by default: Space shows the keep board.")]
        [SerializeField] private KeyCode _holdPinchKey = KeyCode.None;

        [Tooltip("Scrolling without holding anything pinches on its own; the pinch " +
                 "lets go this long after the wheel stops.")]
        [SerializeField] private float _wheelRelease = 0.5f;

        [SerializeField] private KeyCode _twoHandModifier = KeyCode.LeftAlt;

        [Tooltip("Held with the wheel: push / pull (hand depth). Either Ctrl key.")]
        [SerializeField] private bool _ctrlWheelMovesHand = true;

        /// <summary>The wheel is being used as hand depth right now (others leave it alone).</summary>
        public static bool WheelIsDepth =>
            Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        [Tooltip("Initial gap between the two emulated hands, as a fraction of screen height.")]
        [SerializeField] private float _twoHandStartSpread = 0.08f;

        private float _depth;
        private bool _twoHand;
        private bool _wheelPinch;
        private float _lastScroll;
        private float _pendingScroll;
        private Vector2 _anchor;   // viewport point where the two-hand pinch started

        private HandState _left, _right;

        private Camera cam => _camera != null ? _camera : Camera.main;

        public override Transform head => cam != null ? cam.transform : null;

        public override HandState GetHand(Hand hand) => hand == Hand.Left ? _left : _right;

        private void Awake()
        {
            _depth = _restDepth;
        }

        // Runs before the recogniser (which has a later execution order) so it
        // always sees this frame's hands.
        private void Update()
        {
            var c = cam;
            if (c == null)
            {
                _left = _right = default;
                return;
            }

            bool held = Input.GetMouseButton(0) || (_holdPinchKey != KeyCode.None && Input.GetKey(_holdPinchKey));
            // Off by default: on PC, keep is a double click and the wheel is
            // left to the board (zoom) and the detail sheet (scroll).
            bool depthWheel = _wheelMovesHand || (_ctrlWheelMovesHand && WheelIsDepth);
            float scroll = depthWheel ? Input.mouseScrollDelta.y : 0f;

            if (Input.GetMouseButtonDown(0))
            {
                _twoHand = Input.GetKey(_twoHandModifier);
                _anchor = Viewport(c, Input.mousePosition);
            }
            if (!Input.GetMouseButton(0)) _twoHand = false;

            // Wheel without holding anything: the wheel itself pinches. The
            // pinch starts this frame at rest depth and the scroll is applied
            // from the next frame, so the recogniser sees a pinch, then travel.
            bool wheelStarted = false;
            if (!held && scroll != 0f && !_wheelPinch)
            {
                _wheelPinch = true;
                wheelStarted = true;
                _depth = _restDepth;
            }
            if (held) _wheelPinch = false;
            if (scroll != 0f) _lastScroll = Time.time;
            if (_wheelPinch && Time.time - _lastScroll > _wheelRelease) _wheelPinch = false;

            bool pinch = held || _wheelPinch;

            // Wheel down = hand toward you (pull), wheel up = away (push).
            // Hand drifts back to rest when not pinching, like a relaxed arm.
            if (pinch && !wheelStarted)
                _depth = Mathf.Clamp(_depth + (scroll + _pendingScroll) * _depthPerNotch, 0.1f, 0.9f);
            else if (!pinch)
                _depth = _restDepth;
            _pendingScroll = wheelStarted ? scroll : 0f;

            Vector2 vp = Viewport(c, Input.mousePosition);
            bool inside = vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;

            if (pinch && _twoHand)
            {
                // Right hand follows the cursor, left hand mirrors it around the anchor.
                float s = _twoHandStartSpread * 0.5f;
                Vector2 off = new Vector2(s / Mathf.Max(0.01f, c.aspect), 0f);
                Vector2 right = vp + off;
                Vector2 left = 2f * _anchor - vp - off;
                _right = Make(c, right, true, true);
                _left = Make(c, left, true, true);
            }
            else
            {
                _right = Make(c, vp, inside, pinch);
                _left = default;
            }
        }

        private HandState Make(Camera c, Vector2 viewport, bool tracked, bool pinching)
        {
            Ray ray = c.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
            // Start the ray at the eye, not the near plane, so ray and hand agree.
            ray.origin = c.transform.position;
            return new HandState
            {
                tracked = tracked,
                pinching = pinching && tracked,
                ray = ray,
                position = c.transform.position + ray.direction * _depth,
            };
        }

        // Viewport rather than screen coordinates: the panel camera may render
        // into a texture at a different resolution than the window.
        private static Vector2 Viewport(Camera c, Vector3 mouse)
        {
            return new Vector2(mouse.x / Mathf.Max(1, Screen.width),
                               mouse.y / Mathf.Max(1, Screen.height));
        }
    }
}
