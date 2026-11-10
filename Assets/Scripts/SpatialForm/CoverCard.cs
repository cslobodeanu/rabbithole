// A card that is mostly its picture: the image fills the card (a darkened,
// cropped copy of it behind, the whole picture in front), the title and a
// short line over a dark fade at the bottom, the date at the top. The full
// text is one gesture away in the detail view.
//
// Cards without a picture (articles without an image, deductions, "looking
// in..." notes) become type cards: big title, a few lines of text.
//
// Built in code (SFMiniPageFactory makes them); keeps the CardDisplayTemplate
// slots so everything that writes into cards still works:
//   header      the date / label at the top
//   description the title
//   body        the text (shown on type cards only)
//   footer      source and date basis

using SpatialForms;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class CoverCard : CardDisplayTemplate
{
    private RectTransform _face;
    private RawImage _backdrop, _picture;
    private Image _base, _accent;
    private VerticalGradient _scrim, _topScrim;
    private PlayIcon _badge;
    private TextBlock _header, _footer, _body, _title;
    private TMP_Text _headerText, _footerText, _bodyText, _titleText;
    private bool _expectPicture;
    private float _aspect = 1.5f;
    private SkeletonBar _pictureShimmer;
    private RectTransform _skeleton;
    private TMP_Text _skeletonCaption;
    private bool _skeletonOn;
    private readonly System.Collections.Generic.List<RectTransform> _skeletonBars = new System.Collections.Generic.List<RectTransform>();

    public override TextBlock header => _header;
    public override TextBlock footer => _footer;
    public override TextBlock body => _body;
    public override TextBlock description => _title;

    private static readonly Color Base = new Color(0.05f, 0.06f, 0.08f, 1f);
    private static readonly Color TypeBase = new Color(0.07f, 0.09f, 0.12f, 1f);
    private static readonly Color Accent = new Color(0.55f, 0.85f, 1f, 1f);

    /// <summary>Make one, size of the old template, text in its font.</summary>
    public static CoverCard Create(Vector2 size, TMP_FontAsset font, int layer)
    {
        var go = new GameObject("CoverCard", typeof(RectTransform));
        go.SetActive(false);
        go.layer = layer;
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = Vector2.zero;
        rt.sizeDelta = size;
        var card = go.AddComponent<CoverCard>();
        card.Build(size, font);
        PanelUtilSetLayer(go, layer);
        go.SetActive(true);
        return card;
    }

    private static void PanelUtilSetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform t in go.transform) PanelUtilSetLayer(t.gameObject, layer);
    }

    private void Build(Vector2 size, TMP_FontAsset font)
    {
        float W = size.x, H = size.y;

        _face = Rect("Face", transform);
        Stretch(_face);

        _base = Graphic<Image>(_face);
        _base.color = Base;
        _base.raycastTarget = false;

        var clip = Rect("Clip", _face);
        Stretch(clip);
        clip.gameObject.AddComponent<RectMask2D>();

        _backdrop = Graphic<RawImage>(Rect("Backdrop", clip));
        _backdrop.raycastTarget = false;
        _backdrop.color = new Color(0.32f, 0.33f, 0.36f, 1f);
        Stretch(_backdrop.rectTransform);

        // While the picture loads: a shimmering grey block where it will be.
        _pictureShimmer = Graphic<SkeletonBar>(Rect("Loading", clip));
        _pictureShimmer.raycastTarget = false;
        Stretch(_pictureShimmer.rectTransform);

        _picture = Graphic<RawImage>(Rect("Picture", clip));
        _picture.raycastTarget = false;
        var pr = _picture.rectTransform;
        pr.anchorMin = pr.anchorMax = pr.pivot = new Vector2(0.5f, 0.5f);

        _topScrim = Graphic<VerticalGradient>(Rect("TopFade", clip));
        _topScrim.top = new Color(0f, 0f, 0f, 0.65f);
        _topScrim.bottom = new Color(0f, 0f, 0f, 0f);
        _topScrim.raycastTarget = false;
        Anchor(_topScrim.rectTransform, new Vector2(0f, 0.82f), Vector2.one);

        _scrim = Graphic<VerticalGradient>(Rect("BottomFade", clip));
        _scrim.top = new Color(0f, 0f, 0f, 0f);
        _scrim.bottom = new Color(0f, 0f, 0f, 0.92f);
        _scrim.raycastTarget = false;
        Anchor(_scrim.rectTransform, Vector2.zero, new Vector2(1f, 0.45f));

        _accent = Graphic<Image>(Rect("Accent", _face));
        _accent.color = Accent;
        _accent.raycastTarget = false;

        // Drawn, not a glyph: the card font has no play symbol.
        _badge = Graphic<PlayIcon>(Rect("Play", _face));
        _badge.raycastTarget = false;
        var brt = _badge.rectTransform;
        brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0.5f, 0.5f);
        brt.sizeDelta = new Vector2(W * 0.22f, W * 0.22f);
        brt.anchoredPosition = new Vector2(0f, H * 0.06f);
        _badge.gameObject.SetActive(false);

        (_header, _headerText) = Block("Header", font, H * 0.042f, FontStyles.Bold, TextAlignmentOptions.TopLeft);
        (_title, _titleText) = Block("Title", font, H * 0.05f, FontStyles.Bold, TextAlignmentOptions.BottomLeft);
        (_body, _bodyText) = Block("Body", font, H * 0.032f, FontStyles.Normal, TextAlignmentOptions.TopLeft);
        (_footer, _footerText) = Block("Footer", font, H * 0.022f, FontStyles.Normal, TextAlignmentOptions.BottomLeft);
        _headerText.color = Color.white;
        _titleText.color = Color.white;
        _bodyText.color = new Color(0.82f, 0.86f, 0.9f, 1f);
        _footerText.color = new Color(0.7f, 0.78f, 0.86f, 0.85f);
        _titleText.textWrappingMode = TextWrappingModes.Normal;
        _bodyText.textWrappingMode = TextWrappingModes.Normal;
        _bodyText.lineSpacing = 4f;

        // A whole card still to come: grey blocks where its parts will be.
        _skeleton = Rect("Skeleton", _face);
        Stretch(_skeleton);
        for (int i = 0; i < 6; i++)
        {
            var bar = Graphic<SkeletonBar>(Rect("Bar", _skeleton));
            bar.raycastTarget = false;
            _skeletonBars.Add(bar.rectTransform);
        }
        _skeletonCaption = Text("Caption", _skeleton, font, H * 0.024f, FontStyles.Normal, TextAlignmentOptions.BottomLeft);
        _skeletonCaption.color = new Color(0.6f, 0.68f, 0.76f, 0.8f);
        _skeleton.gameObject.SetActive(false);

        Layout();
    }

    /// <summary>Show the card as a grey, shimmering outline of a card still to
    /// come, with a small caption ("Looking in Internet Archive…"); or back to
    /// its content.</summary>
    public void SetSkeleton(bool on, string caption = null)
    {
        _skeletonOn = on;
        _skeletonCaption.text = caption ?? "";
        Layout();
    }

    public bool isSkeleton => _skeletonOn;

    // ---- the "back" tab ---------------------------------------------------------

    private bool _backOn;
    private Image _edge;

    /// <summary>Turn this card into the thin "back" tab: a narrow strip in the
    /// accent colour: "back to", the subject and its years.</summary>
    public void SetBack(string subject, string years, float widthFraction = 0.34f)
    {
        _backOn = true;
        var rt = (RectTransform)transform;
        rt.sizeDelta = new Vector2(rt.sizeDelta.x * widthFraction, rt.sizeDelta.y);
        _header.ClearParagraphs(); _header.AddParagraph("back to");
        _title.ClearParagraphs(); _title.AddParagraph(subject ?? "");
        _body.ClearParagraphs(); _body.AddParagraph(years ?? "");
        _footer.ClearParagraphs();
        if (_edge == null)
        {
            _edge = Graphic<Image>(Rect("Edge", _face));
            _edge.raycastTarget = false;
            _edge.color = Accent;
        }
        Layout();
        TimelineVR.Interaction.PanelUtil.EnsureCollider(this);
    }

    private void LayoutBack(float W, float H)
    {
        float pad = W * 0.12f;
        foreach (var g in new Graphic[] { _backdrop, _picture, _scrim, _topScrim, _accent, _pictureShimmer })
            g.gameObject.SetActive(false);
        _skeleton.gameObject.SetActive(false);
        _base.color = new Color(0.07f, 0.16f, 0.22f, 1f);
        foreach (var b in new[] { _header, _title, _body, _footer }) b.gameObject.SetActive(true);

        // A bright edge on the side facing the results.
        var er = _edge.rectTransform;
        er.anchorMin = new Vector2(1f, 0f); er.anchorMax = Vector2.one; er.pivot = new Vector2(1f, 0.5f);
        er.sizeDelta = new Vector2(Mathf.Max(3f, W * 0.035f), 0f);
        er.anchoredPosition = Vector2.zero;

        _badge.gameObject.SetActive(false);

        _headerText.fontSize = H * 0.026f;
        _headerText.color = new Color(0.7f, 0.85f, 0.95f, 0.9f);
        _headerText.alignment = TextAlignmentOptions.Top;
        _titleText.fontSize = H * 0.04f;
        _titleText.alignment = TextAlignmentOptions.Top;
        _titleText.color = Color.white;
        _bodyText.fontSize = H * 0.028f;
        _bodyText.alignment = TextAlignmentOptions.Top;
        _footerText.fontSize = H * 0.022f;
        _footerText.alignment = TextAlignmentOptions.Bottom;
        _footerText.textWrappingMode = TextWrappingModes.Normal;

        Place(_header, pad, H * 0.62f, W - 2 * pad, H * 0.05f);
        Place(_title, pad, H * 0.36f, W - 2 * pad, H * 0.25f);
        Place(_body, pad, H * 0.27f, W - 2 * pad, H * 0.08f);
        Place(_footer, pad, pad, W - 2 * pad, H * 0.1f);
    }

    private (TextBlock, TMP_Text) Block(string name, TMP_FontAsset font, float size, FontStyles style, TextAlignmentOptions align)
    {
        var holder = Rect(name, _face);
        var t = Text(name + "Text", holder, font, size, style, align);
        Stretch(t.rectTransform);
        var block = holder.gameObject.AddComponent<TextBlock>();   // finds the text just made
        return (block, t);
    }

    // ---- CardDisplayTemplate ------------------------------------------------

    public override void SetImage(Texture2D texture)
    {
        _picture.texture = texture;
        _backdrop.texture = texture;
        _expectPicture = texture != null;
        if (texture != null)
        {
            _picture.color = Color.white;
            _backdrop.color = new Color(0.3f, 0.31f, 0.34f, 1f);
            _aspect = texture.width / (float)Mathf.Max(1, texture.height);
        }
        Layout();
    }

    public override void SetImageSize(int width, int height)
    {
        _expectPicture = true;
        if (width > 0 && height > 0) _aspect = width / (float)height;
        Layout();
    }

    public override void SetImageColor(Color color)
    {
        // A grey placeholder while loading: keep it dark, not grey.
        if (_picture.texture == null) { _picture.color = new Color(0f, 0f, 0f, 0f); _backdrop.color = new Color(0.12f, 0.13f, 0.16f, 1f); }
        else _picture.color = color;
    }

    public override void UpdateCardLayout() => Layout();

    /// <summary>Show the play badge (video / recording cards).</summary>
    public void SetPlayable(bool on)
    {
        _playable = on;
        _badge.gameObject.SetActive(on && !_skeletonOn);
    }

    private bool _playable;

    // ---- layout -------------------------------------------------------------

    private void Layout()
    {
        if (_face == null) return;
        var r = ((RectTransform)transform).rect;
        float W = r.width, H = r.height, pad = W * 0.06f;
        bool pic = _expectPicture;
        if (_backOn) { LayoutBack(W, H); return; }

        _skeleton.gameObject.SetActive(_skeletonOn);
        _header.gameObject.SetActive(!_skeletonOn);
        _title.gameObject.SetActive(!_skeletonOn);
        _footer.gameObject.SetActive(!_skeletonOn);
        if (_skeletonOn)
        {
            foreach (var g in new Graphic[] { _backdrop, _picture, _scrim, _topScrim, _accent, _pictureShimmer })
                g.gameObject.SetActive(false);
            _body.gameObject.SetActive(false);
            _badge.gameObject.SetActive(false);
            _base.color = Base;
            // picture block, date, two title lines, a short line
            float top = H - pad;
            SetBar(0, pad, H * 0.3f, W - 2 * pad, top - H * 0.09f - H * 0.3f);
            SetBar(1, pad, top - H * 0.05f, W * 0.3f, H * 0.04f);
            SetBar(2, pad, H * 0.21f, W * 0.78f, H * 0.045f);
            SetBar(3, pad, H * 0.15f, W * 0.52f, H * 0.045f);
            SetBar(4, pad, H * 0.09f, W * 0.34f, H * 0.026f);
            SetBar(5, 0f, 0f, 0f, 0f);
            var cr = _skeletonCaption.rectTransform;
            cr.anchorMin = cr.anchorMax = cr.pivot = Vector2.zero;
            cr.anchoredPosition = new Vector2(pad, pad * 0.5f);
            cr.sizeDelta = new Vector2(W - 2 * pad, H * 0.05f);
            return;
        }
        _pictureShimmer.gameObject.SetActive(pic && _picture.texture == null);
        _badge.gameObject.SetActive(_playable);

        _backdrop.gameObject.SetActive(pic);
        _picture.gameObject.SetActive(pic && _picture.texture != null);
        _scrim.gameObject.SetActive(pic);
        _topScrim.gameObject.SetActive(pic);
        _base.color = pic ? Base : TypeBase;
        _body.gameObject.SetActive(!pic);
        _accent.gameObject.SetActive(!pic);

        if (pic)
        {
            // Whole picture in front, fitted; behind it the same picture
            // filling the card, darkened.
            float boxW = W, boxH = H, a = Mathf.Max(0.05f, _aspect);
            float w = boxW, h = boxW / a;
            if (h > boxH) { h = boxH; w = boxH * a; }
            _picture.rectTransform.sizeDelta = new Vector2(w, h);
            _picture.rectTransform.anchoredPosition = new Vector2(0f, (boxH - h) * 0.12f);
            float cardA = W / H;
            _backdrop.uvRect = a > cardA
                ? new Rect((1f - cardA / a) * 0.5f, 0f, cardA / a, 1f)
                : new Rect(0f, (1f - a / cardA) * 0.5f, 1f, a / cardA);

            Place(_header, pad, H - pad - H * 0.08f, W - 2 * pad, H * 0.08f);
            Place(_footer, pad, pad * 0.7f, W - 2 * pad, H * 0.05f);
            Place(_title, pad, pad * 0.7f + H * 0.055f, W - 2 * pad, H * 0.2f);
            _titleText.fontSize = H * 0.048f;
            _titleText.alignment = TextAlignmentOptions.BottomLeft;
        }
        else
        {
            // Type card: date, a big title, then as much text as fits.
            Place(_header, pad, H - pad - H * 0.07f, W - 2 * pad, H * 0.07f);
            var ar = _accent.rectTransform;
            ar.anchorMin = ar.anchorMax = ar.pivot = Vector2.zero;
            ar.anchoredPosition = new Vector2(pad, H - pad - H * 0.085f);
            ar.sizeDelta = new Vector2(W * 0.18f, Mathf.Max(2f, H * 0.005f));
            float titleTop = H - pad - H * 0.1f;
            Place(_title, pad, titleTop - H * 0.27f, W - 2 * pad, H * 0.27f);
            _titleText.fontSize = H * 0.06f;
            _titleText.alignment = TextAlignmentOptions.TopLeft;
            float bodyTop = titleTop - H * 0.29f;
            Place(_body, pad, pad + H * 0.06f, W - 2 * pad, bodyTop - pad - H * 0.06f);
            Place(_footer, pad, pad * 0.7f, W - 2 * pad, H * 0.05f);
            // A short title gives its room to the text.
            _titleText.ForceMeshUpdate();
            float used = Mathf.Min(H * 0.27f, _titleText.preferredHeight + H * 0.02f);
            if (!string.IsNullOrEmpty(_titleText.text) && _titleText.gameObject.activeInHierarchy)
                Place(_body, pad, pad + H * 0.06f, W - 2 * pad, titleTop - used - H * 0.02f - pad - H * 0.06f);
        }
    }

    private void SetBar(int i, float x, float y, float w, float h)
    {
        var rt = _skeletonBars[i];
        rt.gameObject.SetActive(w > 0f && h > 0f);
        rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static void Place(TextBlock b, float x, float y, float w, float h)
    {
        var rt = (RectTransform)b.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(Mathf.Max(1f, w), Mathf.Max(1f, h));
    }

    // ---- ui helpers -----------------------------------------------------------

    // CanvasRenderer first: a graphic added to an inactive object does not get one by itself.
    private static T Graphic<T>(RectTransform rt) where T : Graphic
    {
        if (rt.GetComponent<CanvasRenderer>() == null) rt.gameObject.AddComponent<CanvasRenderer>();
        return rt.gameObject.AddComponent<T>();
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private static void Anchor(RectTransform rt, Vector2 min, Vector2 max)
    {
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private static TMP_Text Text(string name, Transform parent, TMP_FontAsset font, float size, FontStyles style, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = align;
        t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Ellipsis;
        t.richText = true;
        return t;
    }
}
