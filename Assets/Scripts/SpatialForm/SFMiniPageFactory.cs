using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEngine;
using XrProbe;
using TimelineVR.Interaction;

namespace SpatialForms.Factories
{
    public class SFMiniPageFactory : SFRenderElementFactory
    {
        [SerializeField]
        private SFElementRender miniPageTemplate;

        [SerializeField]
        private TextureCache _textureCache;

        [Tooltip("Picture-first cards (CoverCard). Off: the old page template.")]
        [SerializeField]
        private bool _cover = true;

        private Vector2 _pageSize;
        private TMPro.TMP_FontAsset _font;


        private void Awake()
        {
            RectTransform rt = miniPageTemplate.GetComponent<RectTransform>();
            _pageSize = rt.rect.size;
            var anyText = miniPageTemplate.GetComponentInChildren<TMPro.TMP_Text>(true);
            _font = anyText != null ? anyText.font : null;
            miniPageTemplate.gameObject.SetActive(false);
        }

        protected override SFElementRender CreateElementRenderInternal(SFElement element, SFDocumentRender documentRenderer)
        {
            if (_cover) return CreateCover(element);

            SFElementRender elem = Instantiate(miniPageTemplate);
            elem.gameObject.SetActive(true);

            CardDisplayTemplate cardDisplay = elem as CardDisplayTemplate;


            cardDisplay.header.AddParagraph(element.data.label);

            cardDisplay.body.AddParagraph(element.data.text != null ? element.data.text.body
                               : element.data.image != null ? element.data.image.body
                               : "");

            var c = element.data.image ?? element.data.text;
            cardDisplay.footer.AddParagraph(c == null ? ""
                : $"{c.title}  ·  {c.source}  ·  {element.data.label} ({c.DateBasis})");

            // Date confidence is the honest channel for uncertainty -
            // fade or soften rather than hiding a weakly dated row.
            //foreach (var l in labels)
            //    l.alpha = Mathf.Lerp(0.45f, 1f, row.DateConfidence);

            if (element.data.image != null)
            {
                cardDisplay.SetImage(null);
                cardDisplay.SetImageColor(Color.gray);
                cardDisplay.SetImageSize(c.ThumbW, c.ThumbH);
                // Start loading the image asynchronously and apply it when ready.
                _ = LoadAndApplyImageAsync(cardDisplay, c.ThumbUrl);
            }
            else
            {
                cardDisplay.SetImage(null);
            }

            cardDisplay.UpdateCardLayout();


            elem.bounds = new Bounds(Vector3.zero, new Vector3(_pageSize.x, _pageSize.y, 0.0f));


            return elem;
        }

        // Picture first: date at the top, title and one line at the bottom;
        // the text itself only on cards without a picture.
        private SFElementRender CreateCover(SFElement element)
        {
            var card = CoverCard.Create(_pageSize, _font, miniPageTemplate.gameObject.layer);
            var d = element.data;
            if (d.text?.source == SearchHistory.BackSource)
            {
                card.SetBack(d.text.title, d.text.body);
                card.bounds = new Bounds(Vector3.zero, new Vector3(_pageSize.x, _pageSize.y, 0.0f));
                return card;
            }
            var c = d.image ?? d.text;
            card.header.AddParagraph(d.label ?? "");
            card.description.AddParagraph(CleanTitle(d.text?.title ?? c?.title));
            card.body.AddParagraph(d.text != null ? d.text.body : d.image != null ? d.image.body : "");
            if (c != null && d.text?.source != SearchHistory.BackSource)
                card.footer.AddParagraph(string.IsNullOrEmpty(c.credit) || c.credit.Length > 40
                    ? c.source : $"{c.source}  ·  {Strip(c.credit)}");
            card.SetPlayable((d.text?.MediaKind ?? d.image?.MediaKind) is string k && (k == "video" || k == "audio"));

            if (d.image != null && !string.IsNullOrEmpty(d.image.ThumbUrl ?? d.image.ImageUrl))
            {
                card.SetImage(null);
                card.SetImageSize(d.image.ThumbW, d.image.ThumbH);
                card.SetImageColor(Color.gray);
                _ = LoadAndApplyImageAsync(card, d.image.ThumbUrl ?? d.image.ImageUrl);
            }
            else
            {
                // No picture of its own: look for one (article lead image,
                // Wikidata picture, Commons search). Shimmer while looking.
                var find = PictureFinder.Find(d, true);
                card.SetImage(null);
                if (!find.IsCompleted || find.Result != null)
                {
                    card.SetImageSize(0, 0);
                    card.SetImageColor(Color.gray);
                    _ = UsePicture(card, d, find);
                }
            }
            card.UpdateCardLayout();
            card.bounds = new Bounds(Vector3.zero, new Vector3(_pageSize.x, _pageSize.y, 0.0f));
            return card;
        }

        private async Task UsePicture(CoverCard card, Result d, Task<Picture> find)
        {
            Picture pic = null;
            try { pic = await find; } catch { }
            if (card == null) return;
            if (pic == null || string.IsNullOrEmpty(pic.url))
            {
                card.SetImage(null);
                card.UpdateCardLayout();
                return;
            }
            PictureFinder.Attach(d, pic);
            if (pic.width > 0 && pic.height > 0) card.SetImageSize(pic.width, pic.height);
            await LoadAndApplyImageAsync(card, pic.url);
        }

        // "File:Frank_Moore_-_Receives_9000_letters ... (page 1).jpg" -> words.
        private static string CleanTitle(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            t = System.Text.RegularExpressions.Regex.Replace(t, @"^File:", "");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\.(jpe?g|png|tiff?|gif|svg|webm|ogv|ogg|mp4|pdf|djvu|webp)$", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\b[0-9a-f]{16,}\b", "");      // hashes in archive names
            t = t.Replace('_', ' ');
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+-\s+(DPLA|NARA)\b.*$", "");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s{2,}", " ").Trim(' ', '-', '·');
            return t;
        }

        private static string Strip(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]+>", "").Trim();

        private async Task LoadAndApplyImageAsync(CardDisplayTemplate cardDisplay, string url)
        {
            try
            {
                var tex = await _textureCache.Load(url);
                if (cardDisplay == null) return;
                if (tex != null)
                {
                    cardDisplay.SetImage(tex);
                    cardDisplay.SetImageColor(Color.white);
                    cardDisplay.UpdateCardLayout();
                }
                else if (cardDisplay is CoverCard)
                {
                    // No picture after all: a type card.
                    cardDisplay.SetImage(null);
                    cardDisplay.UpdateCardLayout();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SFMiniPageFactory] image load failed: {e.Message}");
            }
        }
    }
}
