// The third layer: one panel, opened up.
//
// A sheet grows out of the panel it was opened from - following the hands
// when opened by zooming (keep spreading past the grid's maximum zoom), or on
// its own when opened with Enter - until it fills the view. On the way the
// timeline sinks and blurs behind it (BoardPeek reads `amount`).
//
// It shows what the card already has at once, then asks the source for the
// rest (DetailProvider) and lets it stream in: the full description or the
// whole Wikipedia section, the facts the card had no room for, and a large
// rendition of the image. While that runs, a status line pulses - the same
// "you can see it arrive" honesty the timeline has.
//
// Closing: pinch in (two hands closing), tap outside the sheet, or Esc.
// Release a gesture past halfway and it snaps open, before halfway it folds
// back into its panel.
//
// The UI is built in code - no prefab to wire. Fonts are taken from the panel
// it opens from, so it matches the cards.

using System;
using System.Text;
using System.Threading;
using SpatialForms;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using XrProbe;

namespace TimelineVR.Interaction
{
    /// <summary>Calls back on a double click / double tap (EventSystem).</summary>
    public class DoubleClick : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
    {
        public System.Action onDouble;

        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData e)
        {
            if (e.clickCount >= 2 && !e.dragging) onDouble?.Invoke();
        }
    }

    [DefaultExecutionOrder(30)]
    public class DetailView : MonoBehaviour
    {
        [Header("Look")]
        [SerializeField] private Color _sheetColor = new Color(0.03f, 0.05f, 0.08f, 0.92f);
        [SerializeField] private Color _titleColor = new Color(0.95f, 0.97f, 1f, 1f);
        [SerializeField] private Color _textColor = new Color(0.82f, 0.86f, 0.9f, 1f);
        [SerializeField] private Color _metaColor = new Color(0.55f, 0.85f, 1f, 0.9f);
        [Tooltip("Gap around the open sheet, as a fraction of the view.")]
        [SerializeField] private float _margin = 0.04f;
        [SerializeField] private float _titleSize = 24f;
        [SerializeField] private float _bodySize = 13f;
        [SerializeField] private float _metaSize = 10f;

        [Header("Motion")]
        [SerializeField] private float _speed = 9f;
        [Tooltip("Released past this, a gesture snaps the sheet open; below, it closes.")]
        [SerializeField, Range(0.05f, 0.95f)] private float _snap = 0.4f;
        [Tooltip("Hand spread (ratio) that takes the sheet from closed to fully open.")]
        [SerializeField] private float _spreadToOpen = 1.5f;

        /// <summary>0 = closed, 1 = full screen. Eased; drives the other layers.</summary>
        public float amount { get; private set; }
        public bool isOpen => _target > 0f || _s > 0.001f;
        public Result current => _result;

        private RectTransform _layer, _sheet, _imageBox, _bodyViewport, _bodyRect;
        private RawImage _image;
        private TMP_Text _title, _meta, _body, _status;
        private CanvasGroup _layerGroup, _contentGroup;
        private PanelLayer _blocker;

        private float _s, _target;         // raw progress and where it is heading
        private bool _driven;              // a gesture holds it
        private float _gestureStart;       // _s when a closing pinch began
        private ScrollRect _scroll;
        private SkeletonBar _imageShimmer;
        private RectTransform _textSkeleton;
        private static readonly float[] LineWidths = { 1f, 0.96f, 0.9f, 1f, 0.72f, 0.94f, 0.58f };
        private string _bodyRaw = "";     // body as plain text, before markup

        private SFElementRender _source;
        private Rect _sourceRect;          // in layer space, refreshed while the panel lives
        private Result _result;
        private Texture _texture;
        private float _textureAspect = 1f;
        private bool _fetching;
        private CancellationTokenSource _cts;

        private TextureCache _textures;
        private readonly DetailProvider _provider = new DetailProvider();

        // ---- setup ---------------------------------------------------------

        private void Start()
        {
            _textures = FindFirstObjectByType<TextureCache>();
            var manager = LayerManager.Instance;
            Canvas canvas = manager != null ? manager.overlayCanvas : null;
            if (canvas == null)
            {
                var anyPanelCanvas = FindFirstObjectByType<SFDocumentRender>();
                canvas = anyPanelCanvas != null ? anyPanelCanvas.GetComponentInParent<Canvas>() : null;
            }
            if (canvas == null) canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                Debug.LogWarning("[detail] no canvas to draw on", this);
                enabled = false;
                return;
            }
            Build(canvas.rootCanvas.transform as RectTransform);
            SetVisible(false);

            // Its own layer: present as much as the sheet is open, so the
            // layers below sink as it grows.
            manager?.Register("detail", _layer, null, () => amount);
        }

        private void OnDestroy() => _cts?.Cancel();

        private void Build(RectTransform canvas)
        {
            int layer = canvas.gameObject.layer;

            _layer = NewRect("DetailLayer", canvas);
            Stretch(_layer);
            _layer.SetAsLastSibling();
            _layerGroup = null;   // created on the sheet below; the root's belongs to LayerManager
            _blocker = _layer.gameObject.AddComponent<PanelLayer>();
            _blocker.priority = 100;      // hides every panel while open

            _sheet = NewRect("Sheet", _layer);
            _sheet.anchorMin = _sheet.anchorMax = _sheet.pivot = new Vector2(0.5f, 0.5f);
            var bg = _sheet.gameObject.AddComponent<Image>();
            bg.color = _sheetColor;
            bg.raycastTarget = false;
            _layerGroup = _sheet.gameObject.AddComponent<CanvasGroup>();
            _layerGroup.blocksRaycasts = false;
            _sheet.gameObject.AddComponent<RectMask2D>();

            var content = NewRect("Content", _sheet);
            Stretch(content, 18f);
            _contentGroup = content.gameObject.AddComponent<CanvasGroup>();

            _imageBox = NewRect("ImageBox", content);
            // While the picture loads: a shimmering block where it will be.
            var shimmer = NewRect("ImageLoading", _imageBox);
            shimmer.gameObject.AddComponent<CanvasRenderer>();
            _imageShimmer = shimmer.gameObject.AddComponent<SkeletonBar>();
            _imageShimmer.raycastTarget = false;
            shimmer.anchorMin = shimmer.anchorMax = new Vector2(0.5f, 0.5f);
            _image = NewRect("Image", _imageBox).gameObject.AddComponent<RawImage>();
            _image.raycastTarget = false;
            _image.rectTransform.anchorMin = _image.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);

            var column = NewRect("Column", content);
            _title = Text("Title", column, _titleSize, _titleColor, FontStyles.Bold);
            _meta = Text("Meta", column, _metaSize, _metaColor, FontStyles.Normal);
            _status = Text("Status", column, _metaSize, _metaColor, FontStyles.Italic);

            _bodyViewport = NewRect("BodyViewport", column);
            _bodyViewport.gameObject.AddComponent<RectMask2D>();
            // Something to hit, so drags and the wheel reach the ScrollRect.
            _bodyViewport.gameObject.AddComponent<CanvasRenderer>();
            var hit = _bodyViewport.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            hit.raycastTarget = true;
            _body = Text("Body", _bodyViewport, _bodySize, _textColor, FontStyles.Normal);
            _bodyRect = _body.rectTransform;
            _bodyRect.anchorMin = new Vector2(0f, 1f);
            _bodyRect.anchorMax = new Vector2(1f, 1f);
            _bodyRect.pivot = new Vector2(0.5f, 1f);
            // While the text is fetched: grey lines under what is already there.
            _textSkeleton = NewRect("TextLoading", _bodyRect);
            _textSkeleton.anchorMin = _textSkeleton.anchorMax = _textSkeleton.pivot = new Vector2(0f, 1f);
            foreach (var _ in LineWidths)
            {
                var line = NewRect("Line", _textSkeleton);
                line.gameObject.AddComponent<CanvasRenderer>();
                line.gameObject.AddComponent<SkeletonBar>().raycastTarget = false;
                line.anchorMin = line.anchorMax = line.pivot = new Vector2(0f, 1f);
            }

            // Double click on the text closes, as it does everywhere else on the sheet
            // (the text's clicks go to the EventSystem, not the gesture recogniser).
            _bodyViewport.gameObject.AddComponent<DoubleClick>().onDouble = Close;

            _scroll = _bodyViewport.gameObject.AddComponent<ScrollRect>();
            _scroll.viewport = _bodyViewport;
            _scroll.content = _bodyRect;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Elastic;
            _scroll.elasticity = 0.12f;
            _scroll.inertia = true;
            _scroll.decelerationRate = 0.14f;
            _scroll.scrollSensitivity = 24f;
            _body.overflowMode = TextOverflowModes.Overflow;

            PanelUtil.SetLayer(_layer.gameObject, layer);
        }

        // ---- opening / closing ---------------------------------------------

        /// <summary>Open fully, animated (keyboard, or any one-shot trigger).</summary>
        public void Open(SFElementRender source)
        {
            if (source == null || source.element?.data == null) return;
            Bind(source);
            _driven = false;
            _target = 1f;
        }

        /// <summary>A zoom gesture starts opening this panel; Drive() follows the hands.</summary>
        public void BeginGestureOpen(SFElementRender source)
        {
            if (source == null || source.element?.data == null) return;
            Bind(source);
            _driven = true;
            _target = 0f;
        }

        /// <summary>Gesture position, 0..1.</summary>
        public void Drive(float s)
        {
            _driven = true;
            _target = Mathf.Clamp01(s);
        }

        /// <summary>Two hands start pinching on the open sheet.</summary>
        public void BeginPinch()
        {
            _driven = true;
            _gestureStart = _target = _s;
        }

        /// <summary>Spread ratio since BeginPinch: below 1 closes, above 1 reopens.</summary>
        public void Pinch(float ratio)
        {
            float delta = (ratio - 1f) / Mathf.Max(0.01f, _spreadToOpen - 1f);
            Drive(_gestureStart + delta);
        }

        /// <summary>Hands let go: snap open or closed.</summary>
        public void EndGesture()
        {
            _driven = false;
            _target = _target >= _snap ? 1f : 0f;
        }

        public void Close()
        {
            _driven = false;
            _target = 0f;
        }

        /// <summary>Progress for an opening zoom whose spread overshoots by `overflow` (1 = none).</summary>
        public float ProgressForOverflow(float overflow) =>
            Mathf.Clamp01((overflow - 1f) / Mathf.Max(0.01f, _spreadToOpen - 1f));

        public bool SheetContains(Vector3 world)
        {
            if (_sheet == null) return false;
            return _sheet.rect.Contains((Vector2)_sheet.InverseTransformPoint(world));
        }

        // ---- scrolling -----------------------------------------------------

        // The body is a standard ScrollRect (elastic, with inertia); the
        // EventSystem drives it - the mouse on PC, the hand ray through the
        // Quest input module on the headset.

        /// <summary>Kept for callers; scrolling is the ScrollRect's.</summary>
        public void ScrollBy(float notches)
        {
            if (_scroll == null || _scroll.content == null) return;
            var e = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                    { scrollDelta = new Vector2(0f, notches) };
            _scroll.OnScroll(e);
        }

        // ---- content -------------------------------------------------------

        private void Bind(SFElementRender source)
        {
            if (_source == source && _result == source.element.data && isOpen) return;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            _source = source;
            _result = source.element.data;
            _sourceRect = SourceRect();
            if (_scroll != null) { _scroll.StopMovement(); _bodyRect.anchoredPosition = Vector2.zero; }
            if (_s <= 0.001f) _s = 0f;

            MatchFonts(source);
            SetVisible(true);

            // What the card already has.
            var img = _result.image;
            var txt = _result.text;
            var main = txt ?? img;
            _title.text = Plain(main?.title ?? _result.label ?? "");
            _meta.text = Plain(MetaLine(_result));
            _bodyRaw = Join(txt?.body, img != null && txt != null ? img.body : (txt == null ? img?.body : null));
            _body.text = Plain(_bodyRaw);
            SetTexture(null, img != null && img.ThumbW > 0 ? img.ThumbW / (float)Mathf.Max(1, img.ThumbH) : 1f);
            if (img != null && !string.IsNullOrEmpty(img.ThumbUrl)) _ = LoadImage(img.ThumbUrl, _cts.Token, true);

            // ...and then the rest.
            _ = FetchMore(_result, _cts.Token);
        }

        private async System.Threading.Tasks.Task FetchMore(Result r, CancellationToken ct)
        {
            _fetching = true;
            _status.text = $"fetching more from {Sources(r)}…";
            try
            {
                var d = await _provider.Fetch(r, ct);
                if (ct.IsCancellationRequested) return;

                if (!string.IsNullOrEmpty(d.title)) _title.text = Plain(d.title);
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(d.text)) _bodyRaw = d.text;
                sb.Append(Plain(_bodyRaw));
                if (d.facts.Count > 0 || !string.IsNullOrEmpty(d.sourceUrl))
                {
                    sb.Append("\n\n");
                    foreach (var f in d.facts)
                        sb.Append("\n<color=#").Append(ColorUtility.ToHtmlStringRGB(_metaColor)).Append(">")
                          .Append(f.Key).Append("</color>   ").Append(Plain(f.Value));
                    if (!string.IsNullOrEmpty(d.sourceUrl))
                        sb.Append("\n<color=#").Append(ColorUtility.ToHtmlStringRGB(_metaColor)).Append(">Source</color>   ")
                          .Append(Plain(d.sourceUrl));
                }
                _body.text = sb.ToString();
                _status.text = "";

                if (!string.IsNullOrEmpty(d.imageUrl))
                {
                    if (_texture == null && d.imageW > 0) _textureAspect = d.imageW / (float)Mathf.Max(1, d.imageH);
                    await LoadImage(d.imageUrl, ct, false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogWarning($"[detail] {e.Message}");
                if (!ct.IsCancellationRequested) _status.text = "couldn't fetch more - showing what the card had";
            }
            finally
            {
                if (!ct.IsCancellationRequested) _fetching = false;
            }
        }

        private async System.Threading.Tasks.Task LoadImage(string url, CancellationToken ct, bool onlyIfEmpty)
        {
            if (_textures == null) return;
            try
            {
                var tex = await _textures.Load(url);
                if (ct.IsCancellationRequested || tex == null) return;
                if (onlyIfEmpty && _texture != null) return;
                SetTexture(tex, tex.width / (float)Mathf.Max(1, tex.height));
            }
            catch (Exception e) { Debug.LogWarning($"[detail] image: {e.Message}"); }
        }

        private void SetTexture(Texture t, float aspect)
        {
            _texture = t;
            _textureAspect = aspect > 0f ? aspect : 1f;
            _image.texture = t;
            _image.color = t != null ? Color.white : new Color(1f, 1f, 1f, 0.06f);
        }

        // ---- per frame -----------------------------------------------------

        private void LateUpdate()
        {
            if (_layer == null) return;

            float k = 1f - Mathf.Exp(-(_driven ? _speed * 2.5f : _speed) * Time.deltaTime);
            _s = Mathf.Lerp(_s, _target, k);
            if (Mathf.Abs(_s - _target) < 0.001f) _s = _target;
            amount = _s * _s * (3f - 2f * _s);

            if (_s <= 0f && _target <= 0f && !_driven)
            {
                if (_layer.gameObject.activeSelf) Finish();
                return;
            }

            if (_source != null) _sourceRect = SourceRect();
            Layout();
        }

        private void Finish()
        {
            _cts?.Cancel();
            _source = null;
            _result = null;
            SetVisible(false);
        }

        private void SetVisible(bool v)
        {
            _layer.gameObject.SetActive(v);
            _blocker.enabled = v;
        }

        private void Layout()
        {
            Rect view = _layer.rect;
            Rect full = Rect.MinMaxRect(view.xMin + view.width * _margin, view.yMin + view.height * _margin,
                                        view.xMax - view.width * _margin, view.yMax - view.height * _margin);
            Rect r = Lerp(_sourceRect, full, amount);
            _sheet.localPosition = new Vector3(r.center.x, r.center.y, 0f);
            _sheet.sizeDelta = r.size;
            _layerGroup.alpha = Mathf.Clamp01(amount * 4f);
            _layerGroup.blocksRaycasts = amount > 0.5f;     // UI input (the text's ScrollRect) only when open

            // Content arrives once there is room for it.
            _contentGroup.alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.85f, amount));

            // Columns: image left, text right; text only when there is no image.
            bool hasImage = _result?.image != null || _texture != null;
            float split = hasImage ? (_textureAspect >= 1.2f ? 0.58f : 0.46f) : 0f;
            SetAnchors(_imageBox, new Vector2(0f, 0f), new Vector2(split, 1f), 0f, hasImage ? 12f : 0f);
            _imageBox.gameObject.SetActive(hasImage);
            var column = (RectTransform)_title.transform.parent;
            SetAnchors(column, new Vector2(split, 0f), new Vector2(1f, 1f), hasImage ? 12f : 0f, 0f);

            // Image: fit inside its box, keep aspect.
            if (hasImage)
            {
                Rect box = _imageBox.rect;
                float w = box.width, h = w / _textureAspect;
                if (h > box.height) { h = box.height; w = h * _textureAspect; }
                _image.rectTransform.sizeDelta = new Vector2(w, h);
                _imageShimmer.rectTransform.sizeDelta = new Vector2(w, h);
                _image.enabled = _texture != null;
            }
            _imageShimmer.gameObject.SetActive(hasImage && _texture == null);

            // Text column: title, meta, status stacked from the top; body fills the rest.
            float y = 0f, cw = column.rect.width;
            y = Stack(_title, y, cw, 4f);
            y = Stack(_meta, y, cw, 2f);
            if (_status.text.Length > 0)
            {
                y = Stack(_status, y, cw, 6f);
                _status.alpha = _fetching ? 0.45f + 0.45f * Mathf.Sin(Time.time * 4f) : 1f;
            }
            _status.gameObject.SetActive(_status.text.Length > 0);
            y += 8f;

            _bodyViewport.anchorMin = new Vector2(0f, 0f);
            _bodyViewport.anchorMax = new Vector2(1f, 1f);
            _bodyViewport.offsetMin = Vector2.zero;
            _bodyViewport.offsetMax = new Vector2(0f, -y);

            float bodyH = _body.GetPreferredValues(_body.text, Mathf.Max(10f, cw), 0f).y;
            // Grey lines below the text while more is on its way.
            bool more = _fetching;
            _textSkeleton.gameObject.SetActive(more);
            if (more)
            {
                float line = _bodySize * 0.95f, gap = _bodySize * 0.75f, top = bodyH > 1f ? bodyH + gap * 1.5f : 0f;
                _textSkeleton.anchoredPosition = new Vector2(0f, -top);
                int i = 0;
                foreach (RectTransform l in _textSkeleton)
                {
                    l.anchoredPosition = new Vector2(0f, -i * (line + gap));
                    l.sizeDelta = new Vector2(cw * LineWidths[i % LineWidths.Length], line);
                    i++;
                }
                bodyH = top + i * (line + gap);
            }
            _bodyRect.sizeDelta = new Vector2(0f, bodyH);

        }

        // Places a top-anchored text at y (distance from the column's top);
        // returns the y below it.
        private static float Stack(TMP_Text t, float y, float width, float gap)
        {
            var rt = t.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            float h = t.text.Length == 0 ? 0f : t.GetPreferredValues(t.text, Mathf.Max(10f, width), 0f).y;
            rt.sizeDelta = new Vector2(0f, h);
            rt.anchoredPosition = new Vector2(0f, -y);
            return y + h + (h > 0f ? gap : 0f);
        }

        // ---- helpers -------------------------------------------------------

        private Rect SourceRect()
        {
            if (_source == null || !(_source.transform is RectTransform rt)) return _sourceRect;
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            Vector2 a = _layer.InverseTransformPoint(c[0]);
            Vector2 b = _layer.InverseTransformPoint(c[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        private void MatchFonts(SFElementRender source)
        {
            var texts = source.GetComponentsInChildren<TMP_Text>(true);
            if (texts.Length == 0) return;
            TMP_FontAsset font = texts[0].font, bodyFont = texts[texts.Length > 1 ? 1 : 0].font;
            foreach (var t in texts) if (t.fontSize > texts[0].fontSize) font = t.font;
            if (font != null) { _title.font = font; _meta.font = font; _status.font = font; }
            if (bodyFont != null) _body.font = bodyFont;
        }

        private static string MetaLine(Result r)
        {
            var c = r.image ?? r.text;
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(r.label)) sb.Append(r.label);
            if (r.image != null) sb.Append(sb.Length > 0 ? "  ·  " : "").Append(r.image.source);
            if (r.text != null) sb.Append(sb.Length > 0 ? "  ·  " : "").Append(r.text.source);
            if (c != null && !string.IsNullOrEmpty(c.DateBasis))
                sb.Append($"  ·  dated by {c.DateBasis} ({Mathf.RoundToInt(r.DateConfidence * 100f)}%)");
            return sb.ToString();
        }

        private static string Sources(Result r)
        {
            if (r.image != null && r.text != null) return $"{r.image.source} and {r.text.source}";
            return (r.image ?? r.text)?.source ?? "the source";
        }

        private static string Join(string a, string b) =>
            string.IsNullOrEmpty(a) ? (b ?? "") : string.IsNullOrEmpty(b) ? a : a + "\n\n" + b;

        // Source text can contain '<' - keep TMP from reading it as markup.
        private static string Plain(string s) => string.IsNullOrEmpty(s) ? "" : "<noparse>" + s + "</noparse>";

        private static Rect Lerp(Rect a, Rect b, float t) =>
            Rect.MinMaxRect(Mathf.Lerp(a.xMin, b.xMin, t), Mathf.Lerp(a.yMin, b.yMin, t),
                            Mathf.Lerp(a.xMax, b.xMax, t), Mathf.Lerp(a.yMax, b.yMax, t));

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        private static void Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        private static void SetAnchors(RectTransform rt, Vector2 min, Vector2 max, float padLeft, float padRight)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(padLeft, 0f);
            rt.offsetMax = new Vector2(-padRight, 0f);
        }

        private TMP_Text Text(string name, RectTransform parent, float size, Color color, FontStyles style)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.color = color;
            t.fontStyle = style;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Truncate;
            t.richText = true;
            t.text = "";
            return t;
        }
    }
}
