// Owns the layers: where each one sits, how it looks, and which one gets
// input. Layers are blended between named poses (Layer.cs explains the
// rule); everything here is configuration you can change in the Inspector -
// the pose library and, per layer, which pose it takes when shown, covered
// by another layer, or away.
//
// At start it sets up what the layers need, so nothing has to be wired:
//
//   timeline  the grid container is wrapped in a "TimelineLayer" so the
//             whole timeline can move without fighting the grid's own
//             pan / zoom. Its blur goes to the canvas camera's
//             BlurCameraTarget (the project's existing blur chain).
//   overlay   a sharp canvas for every layer above the timeline: a twin of
//             the content canvas drawn by "OverlayCamera" - a child of the
//             canvas camera, same pose and lens, so panels stay exactly where
//             they were in the world and rays still hit them - composited over
//             the blurred timeline in FinalComposition. The hand cursor is
//             moved onto it so it is never blurred.
//   board     the KeepBoard window is moved onto the overlay.
//   dock      the scene's Dock (moved onto the overlay), or a default one.
//
// Other layers register themselves (DetailView does) with Register().
//
// Focus: the front-most layer with presence >= 0.5 gets input; the others
// are told they lost it (ILayerContent).

using System;
using System.Collections.Generic;
using SpatialForms;
using UnityEngine;
using UnityEngine.UI;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(20)]
    public class LayerManager : MonoBehaviour
    {
        public static LayerManager Instance { get; private set; }

        [Tooltip("Named poses layers can take. Add your own; refer to them by name below.")]
        [SerializeField] private List<LayerPose> _poses = DefaultPoses();

        [Tooltip("Per layer: order and which pose it takes when shown / covered / away.")]
        [SerializeField] private List<LayerSettings> _layers = DefaultLayers();

        [Tooltip("How fast Show / Hide animate, 1/s.")]
        [SerializeField] private float _speed = 10f;

        [Tooltip("Smoothing of the final placement (softens dock reflow), 1/s.")]
        [SerializeField] private float _smoothing = 20f;

        [Header("Dock")]
        [Tooltip("Optional; found or created.")]
        [SerializeField] private Dock _dock;

        [Header("Sharp overlay for layers above the timeline")]
        [SerializeField] private bool _sharpOverlay = true;
        [Tooltip("Optional; found by name if empty. Assign it for builds.")]
        [SerializeField] private Shader _compositeShader;
        [SerializeField] private string _overlayLayer = "UI";

        /// <summary>Canvas for layers above the timeline (sharp if it could be separated).</summary>
        public Canvas overlayCanvas { get; private set; }
        public Dock dock => _dock;
        public IReadOnlyList<Layer> layers => _live;

        private readonly List<Layer> _live = new List<Layer>();
        private readonly List<Layer> _sorted = new List<Layer>();
        private Canvas _content;
        private GestureRecognizer _gestures;

        private BlurCameraTarget _blur;
        private float _restBlur;
        private bool _blurWasEnabled;
        private RenderTexture _overlayTexture;
        private Camera _overlayCamera, _mainCamera;
        private RawImage _overlayImage;

        // ---- defaults ------------------------------------------------------

        private static List<LayerPose> DefaultPoses() => new List<LayerPose>
        {
            new LayerPose("Front"),
            // What "goes to the back" looked like before: sink, shrink, dim, blur.
            new LayerPose("Back", depth: 140f, scale: 0.92f, alpha: 0.3f, blur: 6f),
            // A layer behind the front one but above the timeline.
            new LayerPose("Behind", depth: 70f, scale: 0.92f, alpha: 0.2f),
            new LayerPose("Hidden", depth: 84f, scale: 0.94f, alpha: 0f),
            new LayerPose("Minimized", docked: true),
            new LayerPose("SideLeft", depth: 40f, scale: 0.55f, alpha: 0.95f,
                          offset: new Vector2(-0.36f, 0f), rotation: new Vector3(0f, -35f, 0f)),
            new LayerPose("SideRight", depth: 40f, scale: 0.55f, alpha: 0.95f,
                          offset: new Vector2(0.36f, 0f), rotation: new Vector3(0f, 35f, 0f)),
            new LayerPose("Up", depth: 40f, scale: 0.5f, alpha: 0.95f,
                          offset: new Vector2(0f, 0.34f), rotation: new Vector3(20f, 0f, 0f)),
        };

        private static List<LayerSettings> DefaultLayers() => new List<LayerSettings>
        {
            new LayerSettings("timeline", 0, shown: "Front", covered: "Back", away: "Front"),
            new LayerSettings("board", 10, shown: "Front", covered: "Behind", away: "Minimized"),
            // The detail sheet animates itself; its layer stays put for now.
            new LayerSettings("detail", 20, shown: "Front", covered: "Front", away: "Front"),
        };

        // ---- setup ---------------------------------------------------------

        private void Awake()
        {
            Instance = this;
            _gestures = FindFirstObjectByType<GestureRecognizer>();

            // Zero-config: the detail layer comes with the manager.
            if (FindFirstObjectByType<DetailView>() == null) gameObject.AddComponent<DetailView>();
            // ...and so does the grid's vertical axis.
            if (FindFirstObjectByType<RelatedColumn>() == null) gameObject.AddComponent<RelatedColumn>();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_gestures != null) _gestures.TappedEmpty -= OnTappedEmpty;
            if (_overlayTexture != null) { _overlayTexture.Release(); Destroy(_overlayTexture); }
        }

        private void Start()
        {
            var grid = FindFirstObjectByType<GridInteraction>();
            var board = FindFirstObjectByType<KeepBoard>();

            if (grid != null && grid.container != null) _content = grid.container.GetComponentInParent<Canvas>()?.rootCanvas;
            if (_content == null && board != null && board.viewport != null) _content = board.viewport.GetComponentInParent<Canvas>()?.rootCanvas;

            if (_content != null && _sharpOverlay) overlayCanvas = CreateOverlay(_content);
            if (overlayCanvas == null) overlayCanvas = _content;

            if (grid != null && grid.container != null)
            {
                var root = WrapTimeline(grid.container);
                var t = Register("timeline", root, grid);
                t.Snap(1f);
                t.blurSink = ApplyBlur;
            }

            if (board != null && board.viewport != null)
            {
                MoveToOverlay(board.viewport);
                var b = Register("board", board.viewport, board);
                b.Snap(0f);
            }

            SetUpDock();
            if (_gestures != null) _gestures.TappedEmpty += OnTappedEmpty;
        }

        /// <summary>Make a layer of a RectTransform. Its settings come from the list by id.</summary>
        public Layer Register(string id, RectTransform root, ILayerContent content, Func<float> presence = null)
        {
            var settings = _layers.Find(s => s.id == id);
            if (settings == null)
            {
                settings = new LayerSettings(id, (_layers.Count + 1) * 10, "Front", "Back", "Hidden");
                _layers.Add(settings);
            }
            var layer = new Layer(id, root, settings) { content = content, presenceSource = presence };
            _live.Add(layer);
            // Start unfocused, and say so: content may default to taking input.
            layer.focused = false;
            content?.OnLayerFocus(false);
            return layer;
        }

        public Layer Get(string id) => _live.Find(l => l.id == id);

        /// <summary>Move a RectTransform onto the overlay canvas, keeping its anchors and layer.</summary>
        public void MoveToOverlay(RectTransform rt)
        {
            if (rt == null || overlayCanvas == null || rt.IsChildOf(overlayCanvas.transform)) return;
            rt.SetParent(overlayCanvas.transform, false);
            PanelUtil.SetLayer(rt.gameObject, overlayCanvas.gameObject.layer);
        }

        private RectTransform WrapTimeline(Transform container)
        {
            var parent = container.parent;
            var go = new GameObject("TimelineLayer", typeof(RectTransform));
            go.layer = container.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.SetSiblingIndex(container.GetSiblingIndex());
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            container.SetParent(rt, true);
            return rt;
        }

        private void SetUpDock()
        {
            if (_dock == null) _dock = FindFirstObjectByType<Dock>();
            if (_dock == null && overlayCanvas != null)
            {
                var go = new GameObject("Dock", typeof(RectTransform));
                var rt = (RectTransform)go.transform;
                rt.SetParent(overlayCanvas.transform, false);
                rt.anchorMin = new Vector2(0.55f, 0.02f);
                rt.anchorMax = new Vector2(0.98f, 0.2f);
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                _dock = go.AddComponent<Dock>();
            }
            if (_dock != null && _dock.area != null)
            {
                MoveToOverlay(_dock.area);
                _dock.area.SetAsLastSibling();
            }
        }

        // The overlay twin of the content canvas. See the header.
        private Canvas CreateOverlay(Canvas content)
        {
            Camera main = content.worldCamera;
            if (content.renderMode != RenderMode.ScreenSpaceCamera || main == null || main.targetTexture == null)
                return Fail("content canvas is not Screen Space - Camera into a texture");

            _blur = main.GetComponent<BlurCameraTarget>();
            var apply = FindFirstObjectByType<ApplyUserGeneratedTextureToRawImage>();
            var composite = apply != null ? apply.GetComponent<RawImage>() : null;
            if (composite == null) return Fail("no FinalComposition RawImage to layer over");

            Shader shader = _compositeShader != null ? _compositeShader : Shader.Find("TimelineVR/UICompositeLayer");
            if (shader == null) return Fail("composite shader not found");

            int layer = LayerMask.NameToLayer(_overlayLayer);
            if (layer < 0) layer = 5;

            RenderTexture mainTex = main.targetTexture;
            _overlayTexture = new RenderTexture(mainTex.width, mainTex.height, 16, RenderTextureFormat.ARGB32)
            {
                name = "OverlayCamera's RenderTexture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _overlayTexture.Create();

            var camGo = new GameObject("OverlayCamera");
            camGo.transform.SetParent(main.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.CopyFrom(main);
            camGo.transform.localPosition = Vector3.zero;
            camGo.transform.localRotation = Quaternion.identity;
            cam.targetTexture = _overlayTexture;
            _overlayCamera = cam;
            _mainCamera = main;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << layer;
            cam.depth = main.depth + 1f;
            main.cullingMask &= ~(1 << layer);

            var cgo = new GameObject("OverlayCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            cgo.layer = layer;
            var oc = cgo.GetComponent<Canvas>();
            oc.renderMode = RenderMode.ScreenSpaceCamera;
            oc.worldCamera = cam;
            oc.planeDistance = content.planeDistance;
            oc.sortingOrder = content.sortingOrder + 1;
            var src = content.GetComponent<CanvasScaler>();
            var dst = cgo.GetComponent<CanvasScaler>();
            if (src != null)
            {
                dst.uiScaleMode = src.uiScaleMode;
                dst.referenceResolution = src.referenceResolution;
                dst.screenMatchMode = src.screenMatchMode;
                dst.matchWidthOrHeight = src.matchWidthOrHeight;
                dst.referencePixelsPerUnit = src.referencePixelsPerUnit;
                dst.scaleFactor = src.scaleFactor;
                dst.physicalUnit = src.physicalUnit;
                dst.dynamicPixelsPerUnit = src.dynamicPixelsPerUnit;
            }

            if (_gestures != null && _gestures.cursor != null) PanelUtil.SetLayer(_gestures.cursor.gameObject, layer);

            var ig = new GameObject("OverlayLayer", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            ig.layer = composite.gameObject.layer;
            var irt = (RectTransform)ig.transform;
            var crt = composite.rectTransform;
            irt.SetParent(crt.parent, false);
            irt.SetSiblingIndex(crt.GetSiblingIndex() + 1);
            irt.anchorMin = crt.anchorMin;
            irt.anchorMax = crt.anchorMax;
            irt.pivot = crt.pivot;
            irt.sizeDelta = crt.sizeDelta;
            irt.anchoredPosition3D = crt.anchoredPosition3D;
            irt.localRotation = crt.localRotation;
            irt.localScale = crt.localScale;
            var img = ig.GetComponent<RawImage>();
            img.texture = _overlayTexture;
            _overlayImage = img;
            img.uvRect = composite.uvRect;
            img.raycastTarget = false;
            img.material = new Material(shader);

            if (_blur != null)
            {
                // A blur switched off in the scene stays off at rest and only
                // comes on while a pose asks for it.
                _blurWasEnabled = _blur.enabled;
                _restBlur = _blurWasEnabled ? _blur.blurSize : 0f;
            }
            return oc;
        }

        private Canvas Fail(string why)
        {
            Debug.LogWarning($"[layers] no sharp overlay ({why}) - layers share the content canvas and blur is off.", this);
            _blur = null;
            return null;
        }

        private void ApplyBlur(float b)
        {
            if (_blur == null) return;
            _blur.blurSize = _restBlur + b;
            _blur.enabled = _blurWasEnabled || b > 0.05f;
        }

        // ---- input ---------------------------------------------------------

        // Tap a docked slot: bring that layer back.
        private void OnTappedEmpty(Vector3 point)
        {
            var layer = _dock != null ? _dock.LayerAt(point) : null;
            if (layer != null) layer.Show();
        }

        // ---- per frame -----------------------------------------------------

        // The content camera's texture follows the window (CameraTargetTexture);
        // the overlay's must stay the same size.
        private void FollowMainTexture()
        {
            if (_overlayCamera == null || _mainCamera == null || _overlayTexture == null) return;
            var m = _mainCamera.targetTexture;
            if (m == null || (m.width == _overlayTexture.width && m.height == _overlayTexture.height)) return;
            var old = _overlayTexture;
            _overlayTexture = new RenderTexture(m.width, m.height, 16, RenderTextureFormat.ARGB32)
            {
                name = old.name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            _overlayTexture.Create();
            _overlayCamera.targetTexture = _overlayTexture;
            if (_overlayImage != null) _overlayImage.texture = _overlayTexture;
            old.Release();
            Destroy(old);
        }

        private void LateUpdate()
        {
            FollowMainTexture();
            float dt = Time.deltaTime;
            float k = 1f - Mathf.Exp(-_speed * dt);

            _sorted.Clear();
            foreach (var l in _live)
            {
                if (l.root == null) continue;
                if (l.presenceSource != null) l.presence = Mathf.Clamp01(l.presenceSource());
                else
                {
                    l.presence = Mathf.Lerp(l.presence, l.presenceTarget, k);
                    if (Mathf.Abs(l.presence - l.presenceTarget) < 0.001f) l.presence = l.presenceTarget;
                }
                _sorted.Add(l);
            }
            _sorted.Sort((a, b) => a.settings.order.CompareTo(b.settings.order));

            // Focus: the front-most layer that is mostly there.
            Layer focus = null;
            for (int i = _sorted.Count - 1; i >= 0 && focus == null; i--)
                if (_sorted[i].presence >= 0.5f) focus = _sorted[i];
            foreach (var l in _sorted)
            {
                bool f = l == focus;
                if (f != l.focused)
                {
                    l.focused = f;
                    l.content?.OnLayerFocus(f);
                }
            }

            float ks = 1f - Mathf.Exp(-_smoothing * dt);
            for (int i = 0; i < _sorted.Count; i++)
            {
                var l = _sorted[i];

                float cover = 0f;
                for (int j = i + 1; j < _sorted.Count; j++)
                    if (_sorted[j].settings.coversOthers) cover = Mathf.Max(cover, _sorted[j].weight);

                LayerPose shown = Pose(l.settings.shown), covered = Pose(l.settings.covered), away = Pose(l.settings.away);
                float p = l.weight;

                // How docked the layer is, so the dock can hold a slot for it.
                float dw = (1f - p) * (away.docked ? 1f : 0f)
                         + p * ((1f - cover) * (shown.docked ? 1f : 0f) + cover * (covered.docked ? 1f : 0f));
                if (_dock != null) _dock.SetWeight(l, dw);
                l.dockWeight = dw;

                var up = ResolvedPose.Lerp(Resolve(shown, l), Resolve(covered, l), cover);
                var target = ResolvedPose.Lerp(Resolve(away, l), up, p);

                l.current = l.hasCurrent ? ResolvedPose.Lerp(l.current, target, ks) : target;
                l.hasCurrent = true;
                Apply(l, l.current);
            }
        }

        private LayerPose Pose(string name)
        {
            var p = _poses.Find(x => x.name == name);
            return p ?? _poses[0];
        }

        private ResolvedPose Resolve(LayerPose pose, Layer l)
        {
            var parent = l.root.parent;
            Rect canvas = overlayCanvas != null && l.root.IsChildOf(overlayCanvas.transform)
                ? ((RectTransform)overlayCanvas.transform).rect
                : (_content != null ? ((RectTransform)_content.transform).rect : l.root.rect);

            if (pose.docked && _dock != null && parent != null && _dock.TryGetSlot(l, out Vector3 wc, out Vector2 ws))
            {
                Vector3 ps = parent.lossyScale;
                Vector2 slot = new Vector2(ws.x / Mathf.Max(1e-6f, Mathf.Abs(ps.x)), ws.y / Mathf.Max(1e-6f, Mathf.Abs(ps.y)));
                Rect r = l.root.rect;
                float s = Mathf.Min(slot.x / Mathf.Max(1f, r.width * l.baseScale.x),
                                    slot.y / Mathf.Max(1f, r.height * l.baseScale.y)) * 0.92f;
                return new ResolvedPose
                {
                    center = parent.InverseTransformPoint(wc),
                    rotation = Quaternion.identity,
                    scale = s,
                    alpha = 1f,
                    blur = 0f,
                };
            }

            return new ResolvedPose
            {
                center = l.BaseCenter + new Vector3(pose.offset.x * canvas.width, pose.offset.y * canvas.height, pose.depth),
                rotation = Quaternion.Euler(pose.rotation),
                scale = pose.scale,
                alpha = pose.docked ? 0f : pose.alpha,      // docked with no dock: just fade
                blur = pose.blur,
            };
        }

        private static void Apply(Layer l, ResolvedPose p)
        {
            var rt = l.root;
            Vector3 c = Vector3.Scale((Vector3)rt.rect.center, l.baseScale) * p.scale;
            rt.localScale = l.baseScale * p.scale;
            rt.localRotation = p.rotation;
            rt.localPosition = p.center - p.rotation * c;
            l.group.alpha = Mathf.Clamp01(p.alpha);
            l.blurSink?.Invoke(Mathf.Max(0f, p.blur));
        }
    }
}
