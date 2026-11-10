// The whole harvest at once: every item the Harvester has gathered, as a
// mosaic of small pictures in time order (left = earlier), growing while it
// gathers. Dense eras take more columns, so the shape of the wall is the
// shape of the subject's history.
//
// Harvest, main and detail are one zoom (same keys and gestures on each):
//   open    "-", or keep pinching past the timeline's smallest zoom
//   look    hover a tile for what it is; drag to pan; the wheel zooms the wall
//   go in   tap a tile, spread two hands over it, or "+" / Enter at the
//           cursor: the wall closes and that item is put into the snake under
//           the timeline card nearest its year (RelatedColumn.Bring)
//   back    Esc
//
// Tiles found further from the subject (hop 2) are smaller and dimmer; tiles
// whose year was inferred from what they were found from are slightly
// dimmer too. Pictures load only while the wall is up, visible tiles first,
// at a small standard width (ThumbLoader).
//
// A counter in the corner says how much has been gathered while the wall is
// closed.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SpatialForms;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using XrProbe;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(25)]
    public class HarvestWall : MonoBehaviour, ILayerContent
    {
        [SerializeField] private Harvester _harvester;

        [Header("Look")]
        [SerializeField] private Color _background = new Color(0.015f, 0.02f, 0.035f, 0.86f);
        [SerializeField] private Color _textColor = new Color(0.85f, 0.9f, 0.95f, 1f);
        [SerializeField] private Color _accent = new Color(0.55f, 0.85f, 1f, 1f);
        [Tooltip("Smallest and largest tile, as a fraction of the wall's height.")]
        [SerializeField] private float _minTile = 0.018f, _maxTile = 0.16f;
        [SerializeField] private float _maxZoom = 8f;
        [SerializeField] private float _wheelZoom = 1.15f;

        [Header("Pictures")]
        [Tooltip("Width asked for (a standard Wikimedia width).")]
        [SerializeField] private int _thumbWidth = 120;
        [SerializeField] private int _maxTexture = 128;
        [SerializeField] private int _parallelLoads = 8;

        private class Tile
        {
            public HarvestItem item;
            public RectTransform rt;
            public RawImage img;
            public Vector2 pos, target;
            public float size, appear, shownSize = -1f, shownAlpha = -1f;
            public Vector2 shownPos = new Vector2(float.NaN, 0f);
            public bool loading, tried;
            public Texture tex;
        }

        private readonly List<Tile> _tiles = new List<Tile>();
        private readonly List<Tile> _sorted = new List<Tile>();
        private readonly List<TMP_Text> _labels = new List<TMP_Text>();
        private Layer _layer;
        private RectTransform _root, _viewport, _content, _highlight;
        private TMP_Text _header, _hint, _caption, _hud;
        private PanelLayer _blocker;
        private GestureRecognizer _gestures;
        private GridInteraction _grid;
        private RelatedColumn _related;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _focused, _dirty;
        private float _nextLayout, _nextLoad, _tile, _pitch, _contentWidth;
        private int _rows, _loads;
        private float _zoom = 1f;
        private Vector2 _pan;
        private bool _pinching;
        private float _pinchStartZoom;
        private Vector2 _pinchPivot, _pinchStartPan;
        private Tile _hover;

        public bool shown => _layer != null && _layer.presenceTarget > 0.5f;

        public void OnLayerFocus(bool focused) => _focused = focused;

        // ---- setup ---------------------------------------------------------

        private void Start()
        {
            if (_harvester == null) _harvester = FindFirstObjectByType<Harvester>();
            _gestures = FindFirstObjectByType<GestureRecognizer>();
            _grid = FindFirstObjectByType<GridInteraction>();
            _related = FindFirstObjectByType<RelatedColumn>();
            var manager = LayerManager.Instance;
            Canvas canvas = manager != null ? manager.overlayCanvas : null;
            if (canvas == null) canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null || _harvester == null)
            {
                Debug.LogWarning("[wall] no canvas or harvester", this);
                enabled = false;
                return;
            }
            Build(canvas.rootCanvas.transform as RectTransform);

            _harvester.Added += OnAdded;
            _harvester.Reset += OnReset;
            foreach (var h in _harvester.items) OnAdded(h);

            if (_gestures != null)
            {
                _gestures.TappedEmpty += OnTap;
                _gestures.Panned += OnPanned;
                _gestures.ZoomBegan += OnZoomBegan;
                _gestures.Zoomed += OnZoomed;
                _gestures.ZoomEnded += OnZoomEnded;
            }
            if (_grid != null) _grid.ZoomedOutPastMin += Open;

            if (manager != null)
            {
                _layer = manager.Register("wall", _root, this);
                _layer.Snap(0f);
            }
            else _root.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            _cts.Cancel();
            if (_harvester != null) { _harvester.Added -= OnAdded; _harvester.Reset -= OnReset; }
            if (_gestures != null)
            {
                _gestures.TappedEmpty -= OnTap;
                _gestures.Panned -= OnPanned;
                _gestures.ZoomBegan -= OnZoomBegan;
                _gestures.Zoomed -= OnZoomed;
                _gestures.ZoomEnded -= OnZoomEnded;
            }
            if (_grid != null) _grid.ZoomedOutPastMin -= Open;
            foreach (var t in _tiles) if (t.tex != null) Destroy(t.tex);
        }

        private void Build(RectTransform canvas)
        {
            int layer = canvas.gameObject.layer;

            _root = NewRect("HarvestWall", canvas);
            Stretch(_root, 0f);
            _root.SetAsLastSibling();
            var bg = _root.gameObject.AddComponent<Image>();
            bg.color = _background;
            bg.raycastTarget = false;
            _blocker = _root.gameObject.AddComponent<PanelLayer>();
            _blocker.priority = 90;
            _blocker.enabled = false;

            _header = Text("Header", _root, TextAlignmentOptions.TopLeft);
            _hint = Text("Hint", _root, TextAlignmentOptions.TopRight);
            _hint.text = "drag to pan  ·  wheel to zoom the wall  ·  tap, spread or + to go in  ·  Esc to go back";
            _caption = Text("Caption", _root, TextAlignmentOptions.BottomLeft);

            _viewport = NewRect("Viewport", _root);
            _viewport.gameObject.AddComponent<RectMask2D>();
            _content = NewRect("Content", _viewport);
            _content.anchorMin = _content.anchorMax = _content.pivot = new Vector2(0f, 1f);

            _highlight = NewRect("Highlight", _content);
            var hl = _highlight.gameObject.AddComponent<Image>();
            hl.color = _accent;
            hl.raycastTarget = false;
            _highlight.anchorMin = _highlight.anchorMax = new Vector2(0f, 1f);
            _highlight.pivot = new Vector2(0.5f, 0.5f);
            _highlight.gameObject.SetActive(false);

            // The counter lives outside the wall, so it shows while the wall is closed.
            _hud = Text("HarvestCounter", canvas, TextAlignmentOptions.TopRight);

            PanelUtil.SetLayer(_root.gameObject, layer);
            PanelUtil.SetLayer(_hud.gameObject, layer);
        }

        // ---- open / close --------------------------------------------------

        public void Open()
        {
            if (_layer == null) return;
            _layer.Show();
            _blocker.enabled = true;
            _dirty = true;
        }

        public void Close()
        {
            if (_layer == null) return;
            _layer.Hide();
            _blocker.enabled = false;
            _hover = null;
            _highlight.gameObject.SetActive(false);
        }

        // ---- items ---------------------------------------------------------

        private void OnReset()
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            foreach (var t in _tiles)
            {
                if (t.tex != null) Destroy(t.tex);
                if (t.rt != null) Destroy(t.rt.gameObject);
            }
            _tiles.Clear();
            _sorted.Clear();
            _loads = 0;
            _hover = null;
            _zoom = 1f;
            _pan = Vector2.zero;
            _dirty = true;
        }

        private void OnAdded(HarvestItem h)
        {
            var rt = NewRect("Tile", _content);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var img = rt.gameObject.AddComponent<RawImage>();
            img.raycastTarget = false;
            img.color = h.thumb != null ? new Color(0.12f, 0.14f, 0.18f, 0.9f) : ColorOf(h);
            PanelUtil.SetLayer(rt.gameObject, _content.gameObject.layer);
            _tiles.Add(new Tile { item = h, rt = rt, img = img, appear = 0f });
            _dirty = true;
        }

        private static Color ColorOf(HarvestItem h)
        {
            Color c;
            switch (h.source)
            {
                case "timeline": c = new Color(0.62f, 0.55f, 0.34f); break;
                case "statements": c = new Color(0.27f, 0.55f, 0.55f); break;
                case "links": case "links here": case "search": c = new Color(0.33f, 0.43f, 0.62f); break;
                default: c = new Color(0.47f, 0.4f, 0.58f); break;
            }
            c.a = 0.55f;
            return c;
        }

        // ---- per frame -----------------------------------------------------

        private void Update()
        {
            if (_harvester == null || _root == null) return;
            UpdateHud();

            // Fully away: stop drawing it at all.
            bool live = shown || (_layer != null && _layer.presence > 0.01f) || _layer == null;
            if (_root.gameObject.activeSelf != live) _root.gameObject.SetActive(live);
            if (!shown) return;
            if (_focused && !TypingSomewhere())
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                // Zoom in = into the main view, at the tile under the cursor.
                if (ZoomInKey()) { GoIn(CursorWorld(out var cw) ? cw : (Vector3?)null); return; }
            }

            LayoutChrome();
            if (_dirty && Time.unscaledTime >= _nextLayout) Layout();

            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f && _focused && !MouseHandSource.WheelIsDepth && CursorLocal(out var cl)) ZoomAround(cl, _zoom * Mathf.Pow(_wheelZoom, wheel));

            ApplyView();
            Animate(Time.unscaledDeltaTime);
            Hover();
            if (Time.unscaledTime >= _nextLoad) { _nextLoad = Time.unscaledTime + 0.15f; LoadVisible(); }
        }

        private static bool TypingSomewhere()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            return go != null && (go.GetComponent<TMP_InputField>() != null || go.GetComponent<InputField>() != null);
        }

        private void UpdateHud()
        {
            int n = _harvester.count;
            bool show = n > 0 && !shown;
            if (_hud.gameObject.activeSelf != show) _hud.gameObject.SetActive(show);
            string line = $"<b>{_harvester.count:N0}</b> items · {_harvester.sourceCount} sources" +
                          (_harvester.running ? $" · gathering ({_harvester.status})" : "");
            if (show)
            {
                var c = (RectTransform)_hud.transform.parent;
                float h = c.rect.height;
                _hud.fontSize = h * 0.022f;
                var rt = _hud.rectTransform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
                rt.sizeDelta = new Vector2(c.rect.width * 0.5f, h * 0.05f);
                rt.anchoredPosition = new Vector2(-h * 0.03f, h * 0.015f);
                _hud.alignment = TextAlignmentOptions.BottomRight;
                _hud.text = $"{_harvester.subject}  ·  {line}  ·  <color=#{ColorUtility.ToHtmlStringRGB(_accent)}>pinch or - to see them all</color>";
            }
            if (shown) _header.text = $"<b>{_harvester.subject}</b>   {line}";
        }

        private void LayoutChrome()
        {
            float h = _root.rect.height, w = _root.rect.width;
            float pad = h * 0.03f, top = h * 0.09f, bottom = h * 0.07f;
            _header.fontSize = h * 0.03f;
            _hint.fontSize = _caption.fontSize = h * 0.019f;
            Place(_header.rectTransform, new Vector2(0f, 1f), new Vector2(pad, -pad), new Vector2(w * 0.6f, top));
            Place(_hint.rectTransform, new Vector2(1f, 1f), new Vector2(-pad, -pad), new Vector2(w * 0.4f, top));
            Place(_caption.rectTransform, new Vector2(0f, 0f), new Vector2(pad, pad * 0.6f), new Vector2(w - 2f * pad, bottom));
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = new Vector2(pad, bottom);
            _viewport.offsetMax = new Vector2(-pad, -top);
        }

        private static void Place(RectTransform rt, Vector2 corner, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = corner;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        // Time order, column after column, tiles as big as the count allows.
        private void Layout()
        {
            _dirty = false;
            _nextLayout = Time.unscaledTime + 0.3f;
            float W = _viewport.rect.width, H = _viewport.rect.height;
            if (W <= 1f || H <= 1f) { _dirty = true; return; }
            float strip = H * 0.05f;          // year labels
            float Ht = H - strip;

            _sorted.Clear();
            _sorted.AddRange(_tiles.OrderBy(t => t.item.year).ThenBy(t => t.item.order));
            int n = Mathf.Max(1, _sorted.Count);
            float s = Mathf.Sqrt(W * Ht / n) * 0.92f;
            s = Mathf.Clamp(s, _minTile * H, _maxTile * H);
            _pitch = s;
            _tile = s * 0.9f;
            _rows = Mathf.Max(1, Mathf.FloorToInt(Ht / _pitch));
            int cols = Mathf.CeilToInt(n / (float)_rows);
            _contentWidth = cols * _pitch;
            for (int i = 0; i < _sorted.Count; i++)
            {
                var t = _sorted[i];
                int c = i / _rows, r = i % _rows;
                t.target = new Vector2(c * _pitch + _pitch * 0.5f, -strip - r * _pitch - _pitch * 0.5f);
                float k = t.item.hop >= 2 ? 0.78f : 1f;
                t.size = _tile * k;
                if (t.appear == 0f) t.pos = t.target;
            }
            Labels(strip, cols);
        }

        // A year over the first column of each new decade (or century...),
        // as long as it does not crowd the one before.
        private void Labels(float strip, int cols)
        {
            int used = 0;
            if (_sorted.Count > 0)
            {
                int span = _sorted[_sorted.Count - 1].item.year - _sorted[0].item.year;
                int step = span > 600 ? 100 : span > 150 ? 50 : span > 40 ? 10 : span > 12 ? 5 : 1;
                float minGap = strip * 3.2f / Mathf.Max(0.2f, _zoom);
                float lastX = float.NegativeInfinity;
                int lastBucket = int.MinValue;
                for (int c = 0; c < cols; c++)
                {
                    int i = c * _rows;
                    if (i >= _sorted.Count) break;
                    int y = _sorted[i].item.year;
                    int bucket = Mathf.FloorToInt(y / (float)step);
                    float x = c * _pitch;
                    if (bucket == lastBucket || x - lastX < minGap) continue;
                    lastBucket = bucket;
                    lastX = x;
                    if (used == _labels.Count)
                    {
                        var t = Text("Year", _content, TextAlignmentOptions.BottomLeft);
                        t.color = _accent;
                        _labels.Add(t);
                    }
                    var l = _labels[used++];
                    l.gameObject.SetActive(true);
                    l.text = step == 1 ? y.ToString() : (bucket * step) + (step >= 10 ? "s" : "");
                    l.fontSize = strip * 0.6f / Mathf.Max(0.5f, Mathf.Sqrt(_zoom));
                    var rt = l.rectTransform;
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                    rt.sizeDelta = new Vector2(minGap, strip);
                    rt.anchoredPosition = new Vector2(x, 0f);
                }
            }
            for (int i = used; i < _labels.Count; i++) _labels[i].gameObject.SetActive(false);
        }

        private void ApplyView()
        {
            float W = _viewport.rect.width;
            float wide = _contentWidth * _zoom;
            // Centre a wall narrower than the view; otherwise keep it in view.
            if (wide <= W) _pan.x = (W - wide) * 0.5f;
            else _pan.x = Mathf.Clamp(_pan.x, W - wide, 0f);
            float H = _viewport.rect.height;
            _pan.y = Mathf.Clamp(_pan.y, 0f, Mathf.Max(0f, H * _zoom - H));
            _content.localScale = new Vector3(_zoom, _zoom, 1f);
            _content.anchoredPosition = _pan;
        }

        private void Animate(float dt)
        {
            float k = 1f - Mathf.Exp(-8f * dt);
            // Only what changed: touching thousands of UI elements every frame
            // rebuilds the whole canvas.
            foreach (var t in _tiles)
            {
                if (t.appear < 1f) t.appear = Mathf.Min(1f, t.appear + dt * 2.5f);
                t.pos = (t.pos - t.target).sqrMagnitude < 0.01f ? t.target : Vector2.Lerp(t.pos, t.target, k);
                if (t.pos != t.shownPos) { t.rt.anchoredPosition = t.pos; t.shownPos = t.pos; }
                float s = t == _hover ? t.size * 1.5f : t.size * (0.4f + 0.6f * t.appear);
                if (s != t.shownSize) { t.rt.sizeDelta = new Vector2(s, s); t.shownSize = s; }
                float a = (t.tex != null ? 1f : t.item.thumb != null ? 0.9f : 0.55f) * t.appear;
                if (t.item.hop >= 2) a *= 0.7f;
                if (t.item.inferred) a *= 0.85f;
                if (a != t.shownAlpha) { var c = t.img.color; c.a = a; t.img.color = c; t.shownAlpha = a; }
            }
        }

        // ---- pictures ------------------------------------------------------

        private void LoadVisible()
        {
            if (_loads >= _parallelLoads) return;
            var view = _viewport.rect;
            foreach (var t in _sorted)
            {
                if (_loads >= _parallelLoads) break;
                if (t.tried || t.loading || t.tex != null) continue;
                Vector2 p = (Vector2)_viewport.InverseTransformPoint(t.rt.position);
                if (!view.Contains(p)) continue;
                _ = Load(t, _cts.Token);
            }
        }

        private async System.Threading.Tasks.Task Load(Tile t, CancellationToken ct)
        {
            t.loading = true;
            _loads++;
            try
            {
                if (t.item.thumb == null)
                {
                    // No picture of its own: the batched lookups only (no per-tile search).
                    var pic = await PictureFinder.Find(t.item.result, false, ct);
                    if (ct.IsCancellationRequested || pic == null || string.IsNullOrEmpty(pic.url)) return;
                    t.item.thumb = pic.url;
                    PictureFinder.Attach(t.item.result, pic);
                    t.img.color = new Color(0.12f, 0.14f, 0.18f, 0.9f);
                    t.shownAlpha = -1f;
                }
                var tex = await ThumbLoader.Load(t.item.thumb, _thumbWidth, _maxTexture, ct);
                if (ct.IsCancellationRequested || t.rt == null) { if (tex != null) Destroy(tex); return; }
                if (tex != null)
                {
                    t.tex = tex;
                    t.img.texture = tex;
                    t.img.color = Color.white;
                    t.shownAlpha = -1f;
                    // Fill the square: crop the long side.
                    float aspect = tex.width / (float)Mathf.Max(1, tex.height);
                    t.img.uvRect = aspect >= 1f
                        ? new Rect((1f - 1f / aspect) * 0.5f, 0f, 1f / aspect, 1f)
                        : new Rect(0f, (1f - aspect) * 0.5f, 1f, aspect);
                }
                else { t.img.color = ColorOf(t.item); t.shownAlpha = -1f; }
            }
            catch (System.OperationCanceledException) { }
            catch (System.Exception e) { Debug.LogWarning($"[wall] {e.Message}"); }
            finally
            {
                t.tried = true;
                t.loading = false;
                _loads--;
            }
        }

        // ---- input ---------------------------------------------------------

        // The wall is not on the gesture "glass" plane (it is on the overlay
        // canvas, at its own depth): rays are met on the wall's own plane, or
        // under perspective the cursor drifts more the further from the centre.
        private bool OnWall(Ray ray, out Vector3 world)
        {
            world = default;
            var plane = new Plane(_viewport.forward, _viewport.position);
            if (!plane.Raycast(ray, out float t) || t <= 0f) return false;
            world = ray.GetPoint(t);
            return true;
        }

        // A point the recognizer found on its glass, seen from the eye, on the wall.
        private Vector3 GlassToWall(Vector3 glassPoint)
        {
            Vector3 eye;
            var cam = _root.GetComponentInParent<Canvas>()?.rootCanvas.worldCamera;
            if (_gestures != null && _gestures.hands != null && _gestures.hands.head != null) eye = _gestures.hands.head.position;
            else if (cam != null) eye = cam.transform.position;
            else return glassPoint;
            return OnWall(new Ray(eye, glassPoint - eye), out var w) ? w : glassPoint;
        }

        // The cursor (right hand's ray) on the wall.
        private bool CursorWorld(out Vector3 world)
        {
            world = default;
            if (_gestures == null || _gestures.hands == null) return false;
            var h = _gestures.hands.GetHand(Hand.Right);
            return OnWall(h.ray, out world);
        }

        private bool CursorLocal(out Vector2 viewportLocal)
        {
            viewportLocal = default;
            if (!CursorWorld(out var w)) return false;
            viewportLocal = ViewportLocal(w);
            return true;
        }

        // Viewport-local, origin at the viewport's top-left (the content's anchor).
        private Vector2 ViewportLocal(Vector3 world)
        {
            Vector2 p = _viewport.InverseTransformPoint(world);
            var r = _viewport.rect;
            return new Vector2(p.x - r.xMin, p.y - r.yMax);
        }

        private Tile TileAt(Vector3 world)
        {
            if (_rows <= 0 || _pitch <= 0f) return null;
            Vector2 v = ViewportLocal(world);
            var r = _viewport.rect;
            if (v.x < 0f || v.x > r.width || v.y > 0f || v.y < -r.height) return null;
            Vector2 c = (v - _pan) / _zoom;                          // content space
            float strip = r.height * 0.05f;
            int col = Mathf.FloorToInt(c.x / _pitch);
            int row = Mathf.FloorToInt((-c.y - strip) / _pitch);
            if (col < 0 || row < 0 || row >= _rows) return null;
            int i = col * _rows + row;
            return i >= 0 && i < _sorted.Count ? _sorted[i] : null;
        }

        private void Hover()
        {
            Tile t = _focused && CursorWorld(out var w) ? TileAt(w) : null;
            if (t != _hover)
            {
                _hover = t;
                _highlight.gameObject.SetActive(t != null);
                if (t != null)
                {
                    var h = t.item;
                    string when = h.inferred ? $"c. {h.year}" : h.year.ToString();
                    string hop = h.hop == 0 ? "on the timeline" : h.hop == 1 ? "linked to the subject" : "one step further";
                    _caption.text = $"<b>{h.title}</b>   {when}   ·   {h.source}   ·   {hop}";
                }
                else _caption.text = "";
            }
            if (t != null)
            {
                _highlight.SetAsLastSibling();
                t.rt.SetAsLastSibling();
                _highlight.anchoredPosition = t.pos;
                float s = t.size * 1.6f + 4f / Mathf.Max(0.2f, _zoom);
                _highlight.sizeDelta = new Vector2(s, s);
            }
        }

        private void OnTap(Vector3 point)
        {
            if (!shown || !_focused) return;
            var t = CursorWorld(out var w) ? TileAt(w) : TileAt(GlassToWall(point));
            if (t == null) return;
            Close();
            if (_related != null) _related.Bring(t.item.result, t.item.year);
        }

        private void OnPanned(Vector3 worldDelta)
        {
            if (!shown || !_focused || _pinching) return;
            // Glass-plane delta, scaled to the wall's depth.
            Vector3 eye = _gestures != null && _gestures.hands != null && _gestures.hands.head != null
                ? _gestures.hands.head.position : _viewport.position - _viewport.forward;
            Vector3 g0 = _gestures != null && _gestures.glass != null ? _gestures.glass.position : _viewport.position;
            float k = Vector3.Distance(eye, _viewport.position) / Mathf.Max(1e-4f, Vector3.Distance(eye, g0));
            Vector2 d = _viewport.InverseTransformVector(worldDelta * k);
            _pan += d;
        }

        // Two hands work as everywhere else: spread = zoom in, which from here
        // is the main view, at the tile between the hands. Pinch: nothing, the
        // wall is as far out as it goes. (The wall's own zoom is the wheel.)
        private Vector3 _pinchWorld;

        private void OnZoomBegan(Vector3 pivot)
        {
            if (!shown || !_focused) return;
            _pinching = true;
            _pinchWorld = GlassToWall(pivot);
        }

        private void OnZoomed(float ratio)
        {
            if (!_pinching) return;
            if (ratio > 1.25f) { _pinching = false; GoIn(_pinchWorld); }
        }

        private void OnZoomEnded() => _pinching = false;

        /// <summary>Into the main view: at this point's tile if there is one.</summary>
        private void GoIn(Vector3? world)
        {
            var t = world.HasValue ? TileAt(world.Value) : null;
            Close();
            if (t != null && _related != null) _related.Bring(t.item.result, t.item.year);
        }

        public static bool ZoomInKey() =>
            Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus)
            || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);

        public static bool ZoomOutKey() =>
            Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus);

        private void ZoomAround(Vector2 viewportLocal, float zoom)
        {
            zoom = Mathf.Clamp(zoom, 1f, _maxZoom);
            Vector2 c = (viewportLocal - _pan) / _zoom;
            _zoom = zoom;
            _pan = viewportLocal - c * _zoom;
            _dirty = true;     // labels thin out / fill in with zoom
        }

        // ---- ui helpers ----------------------------------------------------

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Stretch(RectTransform rt, float inset)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        private TMP_Text Text(string name, Transform parent, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            t.color = _textColor;
            t.alignment = align;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.richText = true;
            t.text = "";
            return t;
        }
    }
}
