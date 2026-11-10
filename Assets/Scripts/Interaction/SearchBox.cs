// The search box: a "special" layer, like the history - outside the zoom of
// harvest / main / detail.
//
//   open     "/" , or the Search button at the top left (a pinch on Quest)
//   type     a subject, with years if you like:
//              ufo              apollo 1969            ufo 1950-1980
//              strasbourg 1700s (a century)            beatles 1960s (a decade)
//            Without years the window comes from the subject's own dates on
//            Wikidata (born-died, founded-dissolved, start-end...), else 1900-now.
//   suggest  Wikipedia titles appear as you type, each with its picture and a
//            line about it; Up / Down or pointing picks one (so "Mercury" means
//            the one you meant)
//   search   Enter (or click a suggestion): a new search - a new root in the
//            history; the back card leads to where you were
//   close    Esc
//
// Standard uGUI (TMP_InputField, Buttons): the EventSystem drives it - the
// keyboard and mouse on PC, the Quest input module and system keyboard there.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SpatialForms;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
using XrProbe;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(31)]
    public class SearchBox : MonoBehaviour, ILayerContent
    {
        [SerializeField] private KeyCode _key = KeyCode.Slash;
        [SerializeField] private int _maxSuggestions = 6;
        [SerializeField] private Color _panel = new Color(0.04f, 0.06f, 0.09f, 0.96f);
        [SerializeField] private Color _accent = new Color(0.55f, 0.85f, 1f, 1f);
        [SerializeField] private Color _text = new Color(0.9f, 0.94f, 0.98f, 1f);

        /// <summary>True while the box has the keyboard: other keys (arrows, D,
        /// Space, + / -) must leave it alone.</summary>
        public static bool typing { get; private set; }

        private class Suggestion
        {
            public string title, description, qid, thumb;
        }

        private Layer _layer;
        private RectTransform _root, _box, _list;
        private TMP_InputField _input;
        private TMP_Text _hint;
        private GameObject _button;
        private readonly List<(GameObject go, Image bg, RawImage img, TMP_Text title, TMP_Text desc)> _rows =
            new List<(GameObject, Image, RawImage, TMP_Text, TMP_Text)>();
        private List<Suggestion> _suggestions = new List<Suggestion>();
        private int _selected = -1;
        private string _asked = "";
        private float _askAt = -1f;
        private CancellationTokenSource _cts;
        private SFManager _search;
        private TextureCache _textures;
        private bool _focused;
        private float _openedAt = -1f;

        public bool shown => _layer != null ? _layer.presenceTarget > 0.5f : _root != null && _root.gameObject.activeSelf;

        public void OnLayerFocus(bool focused) => _focused = focused;

        // ---- setup -----------------------------------------------------------------

        private void Start()
        {
            _search = FindFirstObjectByType<SFManager>();
            _textures = FindFirstObjectByType<TextureCache>();
            var manager = LayerManager.Instance;
            Canvas canvas = manager != null ? manager.overlayCanvas : null;
            if (canvas == null) canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null) { enabled = false; return; }
            var c = (RectTransform)canvas.rootCanvas.transform;
            Build(c);
            BuildButton(c);
            if (manager != null)
            {
                _layer = manager.Register("search", _root, this);
                _layer.Snap(0f);
            }
            _root.gameObject.SetActive(false);
        }

        private void Build(RectTransform canvas)
        {
            int layer = canvas.gameObject.layer;
            float H = canvas.rect.height, W = canvas.rect.width;

            _root = Rect("SearchLayer", canvas);
            _root.anchorMin = Vector2.zero; _root.anchorMax = Vector2.one; _root.offsetMin = _root.offsetMax = Vector2.zero;
            _root.SetAsLastSibling();

            // The box: top centre.
            _box = Rect("Box", _root);
            _box.anchorMin = _box.anchorMax = _box.pivot = new Vector2(0.5f, 1f);
            _box.anchoredPosition = new Vector2(0f, -H * 0.08f);
            _box.sizeDelta = new Vector2(Mathf.Min(W * 0.62f, H * 1.3f), H * 0.09f);
            var bg = Graphic<Image>(_box);
            bg.color = _panel;
            var edge = Graphic<Image>(Rect("Edge", _box));
            edge.color = _accent;
            edge.raycastTarget = false;
            var er = edge.rectTransform;
            er.anchorMin = Vector2.zero; er.anchorMax = new Vector2(1f, 0f); er.pivot = new Vector2(0.5f, 0f);
            er.sizeDelta = new Vector2(0f, Mathf.Max(2f, H * 0.003f));

            // Input field, built by hand (no prefab): area, placeholder, text.
            var field = Rect("Input", _box);
            field.gameObject.SetActive(false);
            field.anchorMin = Vector2.zero; field.anchorMax = Vector2.one;
            field.offsetMin = new Vector2(H * 0.025f, 0f); field.offsetMax = new Vector2(-H * 0.025f, 0f);
            var area = Rect("Text Area", field);
            area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one; area.offsetMin = area.offsetMax = Vector2.zero;
            area.gameObject.AddComponent<RectMask2D>();
            var placeholder = Text("Placeholder", area, H * 0.036f, new Color(_text.r, _text.g, _text.b, 0.4f), TextAlignmentOptions.MidlineLeft);
            placeholder.text = "Search a subject - and years if you like:  ufo 1950-1980";
            placeholder.fontStyle = FontStyles.Normal;
            var text = Text("Text", area, H * 0.036f, _text, TextAlignmentOptions.MidlineLeft);
            foreach (var t in new[] { placeholder.rectTransform, text.rectTransform })
            {
                t.anchorMin = Vector2.zero; t.anchorMax = Vector2.one; t.offsetMin = t.offsetMax = Vector2.zero;
            }
            _input = field.gameObject.AddComponent<TMP_InputField>();
            _input.textViewport = area;
            _input.textComponent = text;
            _input.placeholder = placeholder;
            _input.lineType = TMP_InputField.LineType.SingleLine;
            _input.caretColor = _accent;
            _input.customCaretColor = true;
            _input.selectionColor = new Color(_accent.r, _accent.g, _accent.b, 0.35f);
            _input.onFocusSelectAll = true;
            _input.onValueChanged.AddListener(v =>
            {
                // The "/" that opened the box is not part of the search.
                if (Time.unscaledTime - _openedAt < 0.3f && v.StartsWith("/")) { _input.text = v.Substring(1); return; }
                _askAt = Time.unscaledTime + 0.25f;
            });
            _input.onSubmit.AddListener(_ => Submit());
            field.gameObject.SetActive(true);

            // Suggestions under the box.
            _list = Rect("Suggestions", _root);
            _list.anchorMin = _list.anchorMax = _list.pivot = new Vector2(0.5f, 1f);
            _list.sizeDelta = new Vector2(_box.sizeDelta.x, 0f);
            _list.anchoredPosition = _box.anchoredPosition - new Vector2(0f, _box.sizeDelta.y + H * 0.01f);
            float rowH = H * 0.075f;
            for (int i = 0; i < _maxSuggestions; i++)
            {
                int k = i;
                var row = Rect("Suggestion", _list);
                row.anchorMin = new Vector2(0f, 1f); row.anchorMax = new Vector2(1f, 1f); row.pivot = new Vector2(0.5f, 1f);
                row.sizeDelta = new Vector2(0f, rowH);
                row.anchoredPosition = new Vector2(0f, -i * (rowH + H * 0.004f));
                var rbg = Graphic<Image>(row);
                rbg.color = new Color(_panel.r, _panel.g, _panel.b, 0.9f);
                var btn = row.gameObject.AddComponent<Button>();
                btn.targetGraphic = rbg;
                var colors = btn.colors;
                colors.highlightedColor = new Color(1.6f, 1.6f, 1.8f, 1f);
                btn.colors = colors;
                btn.onClick.AddListener(() => Choose(k));
                var img = Graphic<RawImage>(Rect("Picture", row));
                img.raycastTarget = false;
                var ir = img.rectTransform;
                ir.anchorMin = new Vector2(0f, 0.5f); ir.anchorMax = new Vector2(0f, 0.5f); ir.pivot = new Vector2(0f, 0.5f);
                ir.sizeDelta = new Vector2(rowH * 0.84f, rowH * 0.84f);
                ir.anchoredPosition = new Vector2(rowH * 0.08f, 0f);
                var title = Text("Title", row, H * 0.03f, _text, TextAlignmentOptions.BottomLeft);
                title.fontStyle = FontStyles.Bold;
                Span(title.rectTransform, rowH * 1.05f, 0.5f, 1f);
                var desc = Text("Description", row, H * 0.022f, new Color(0.65f, 0.75f, 0.85f, 0.9f), TextAlignmentOptions.TopLeft);
                Span(desc.rectTransform, rowH * 1.05f, 0f, 0.5f);
                row.gameObject.SetActive(false);
                _rows.Add((row.gameObject, rbg, img, title, desc));
            }

            _hint = Text("Hint", _root, H * 0.02f, new Color(0.65f, 0.75f, 0.85f, 0.8f), TextAlignmentOptions.TopRight);
            var hr = _hint.rectTransform;
            hr.anchorMin = hr.anchorMax = hr.pivot = new Vector2(0.5f, 1f);
            hr.sizeDelta = new Vector2(_box.sizeDelta.x, H * 0.03f);
            hr.anchoredPosition = _box.anchoredPosition + new Vector2(0f, H * 0.035f);
            _hint.text = "Enter: search   ·   Up / Down: pick a suggestion   ·   Esc: close";

            PanelUtil.SetLayer(_root.gameObject, layer);
        }

        // A small "Search" button, top left: the way in without a keyboard.
        private void BuildButton(RectTransform canvas)
        {
            float H = canvas.rect.height;
            var r = Rect("SearchButton", canvas);
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(H * 0.03f, -H * 0.025f);
            r.sizeDelta = new Vector2(H * 0.16f, H * 0.05f);
            var bg = Graphic<Image>(r);
            bg.color = new Color(_panel.r, _panel.g, _panel.b, 0.75f);
            var b = r.gameObject.AddComponent<Button>();
            b.targetGraphic = bg;
            b.onClick.AddListener(Open);
            var t = Text("Label", r, H * 0.024f, _accent, TextAlignmentOptions.Center);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;
            t.text = "Search  /";
            PanelUtil.SetLayer(r.gameObject, canvas.gameObject.layer);
            _button = r.gameObject;
        }

        // ---- open / close -------------------------------------------------------

        public void Open()
        {
            _root.gameObject.SetActive(true);
            _openedAt = Time.unscaledTime;
            _layer?.Show();
            _input.text = "";
            Show(new List<Suggestion>());
            _input.Select();
            _input.ActivateInputField();
        }

        public void Close()
        {
            _layer?.Hide();
            _cts?.Cancel();
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == _input.gameObject)
                EventSystem.current.SetSelectedGameObject(null);
            _input.DeactivateInputField();
            typing = false;
        }

        // ---- per frame ----------------------------------------------------------

        private void Update()
        {
            if (_root == null) return;
            typing = shown && _input.isFocused;
            if (_button != null) _button.SetActive(!shown);

            if (!shown)
            {
                if (_layer != null && _layer.presence < 0.01f && _root.gameObject.activeSelf) _root.gameObject.SetActive(false);
                // Not while something else has the keyboard (another input field).
                var sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                bool otherField = sel != null && (sel.GetComponent<TMP_InputField>() != null || sel.GetComponent<InputField>() != null);
                if (Input.GetKeyDown(_key) && !otherField) Open();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            if (Input.GetKeyDown(KeyCode.DownArrow)) Select(Mathf.Min(_selected + 1, _suggestions.Count - 1));
            if (Input.GetKeyDown(KeyCode.UpArrow)) Select(Mathf.Max(_selected - 1, -1));
            if (!_input.isFocused && _focused && Input.anyKeyDown && !Input.GetMouseButtonDown(0)) _input.ActivateInputField();

            if (_askAt >= 0f && Time.unscaledTime >= _askAt)
            {
                _askAt = -1f;
                string q = Parse(_input.text).subject;
                if (q != _asked) { _asked = q; _ = Suggest(q); }
            }
        }

        // ---- suggestions ------------------------------------------------------------

        private async Task Suggest(string q)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            if (q.Length < 2) { Show(new List<Suggestion>()); return; }
            string url = "https://en.wikipedia.org/w/api.php?action=query&format=json&redirects=1"
                       + $"&generator=prefixsearch&gpslimit={_maxSuggestions}&gpssearch={UnityWebRequest.EscapeURL(q)}"
                       + $"&prop=pageimages|description|pageprops&piprop=thumbnail&pithumbsize=120&pilicense=any&ppprop=wikibase_item";
            JToken j;
            try { j = await WebCache.GetJson(url, ct, 10, "search", TimeSpan.FromDays(1)); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            var pages = (j?["query"]?["pages"] as JObject)?.Properties().Select(p => p.Value)
                        .OrderBy(p => (int?)p["index"] ?? 0).ToList() ?? new List<JToken>();
            Show(pages.Select(p => new Suggestion
            {
                title = (string)p["title"],
                description = (string)p["description"] ?? "",
                qid = (string)p["pageprops"]?["wikibase_item"],
                thumb = (string)p["thumbnail"]?["source"],
            }).Where(s => !string.IsNullOrEmpty(s.title)).ToList());
        }

        private void Show(List<Suggestion> list)
        {
            _suggestions = list;
            _selected = -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool on = i < list.Count;
                row.go.SetActive(on);
                if (!on) continue;
                var s = list[i];
                row.title.text = s.title;
                row.desc.text = s.description;
                row.img.texture = null;
                row.img.color = new Color(0.16f, 0.18f, 0.22f, 1f);
                if (s.thumb != null && _textures != null) _ = Picture(row.img, s.thumb);
            }
            Select(-1);
        }

        private async Task Picture(RawImage img, string url)
        {
            var tex = await _textures.Load(url);
            if (img == null || tex == null) return;
            img.texture = tex;
            img.color = Color.white;
            float a = tex.width / (float)Mathf.Max(1, tex.height);
            img.uvRect = a >= 1f ? new Rect((1f - 1f / a) * 0.5f, 0f, 1f / a, 1f) : new Rect(0f, (1f - a) * 0.5f, 1f, a);
        }

        private void Select(int i)
        {
            _selected = i;
            for (int k = 0; k < _rows.Count; k++)
                _rows[k].bg.color = k == i ? new Color(0.1f, 0.2f, 0.28f, 0.98f) : new Color(_panel.r, _panel.g, _panel.b, 0.9f);
        }

        private void Choose(int i)
        {
            if (i < 0 || i >= _suggestions.Count) return;
            _ = Run(_suggestions[i].title, _suggestions[i].qid, Parse(_input.text));
        }

        private void Submit()
        {
            if (!shown) return;
            var parsed = Parse(_input.text);
            if (_selected >= 0 && _selected < _suggestions.Count)
                _ = Run(_suggestions[_selected].title, _suggestions[_selected].qid, parsed);
            else if (!string.IsNullOrWhiteSpace(parsed.subject))
                _ = Run(parsed.subject, null, parsed);
        }

        // ---- the search itself ---------------------------------------------------------

        private async Task Run(string subject, string qid, (string subject, int? y0, int? y1) parsed)
        {
            Close();
            if (_search == null || string.IsNullOrWhiteSpace(subject)) return;
            int y0, y1;
            if (parsed.y0.HasValue) { y0 = parsed.y0.Value; y1 = parsed.y1 ?? y0; }
            else (y0, y1) = await Window(subject, qid);
            Debug.Log($"[search] {subject} {y0}-{y1}");
            _ = _search.Search(subject, y0, y1);
        }

        // Years typed with the subject: "1950-1980", "1700s" (century), "1960s"
        // (decade), "1969" (around it).
        public static (string subject, int? y0, int? y1) Parse(string s)
        {
            s = s ?? "";
            var range = Regex.Match(s, @"\b(\d{3,4})\s*(?:-|–|to)\s*(\d{3,4})\b");
            if (range.Success)
            {
                int a = int.Parse(range.Groups[1].Value), b = int.Parse(range.Groups[2].Value);
                return (Rest(s, range), Mathf.Min(a, b), Mathf.Max(a, b));
            }
            var decade = Regex.Match(s, @"\b(\d{2,3}0)s\b");
            if (decade.Success)
            {
                int y = int.Parse(decade.Groups[1].Value);
                return (Rest(s, decade), y, y % 100 == 0 ? y + 99 : y + 9);
            }
            var year = Regex.Match(s, @"\b(\d{4})\b");      // four digits: "boeing 747" is not a year
            if (year.Success)
            {
                int y = int.Parse(year.Groups[1].Value);
                return (Rest(s, year), y - 10, Mathf.Min(DateTime.Now.Year, y + 10));
            }
            return (s.Trim(), null, null);
        }

        private static string Rest(string s, Match m) =>
            Regex.Replace(s.Remove(m.Index, m.Length), @"\s{2,}", " ").Trim();

        // No years typed: the subject's own dates on Wikidata, else 1900-now.
        private static readonly string[][] DatePairs =
        {
            new[] { "P569", "P570" },   // born - died
            new[] { "P571", "P576" },   // founded / made - dissolved
            new[] { "P580", "P582" },   // start - end
            new[] { "P585", null },     // point in time
            new[] { "P577", null },     // published
            new[] { "P1619", null },    // opened
        };

        private static async Task<(int, int)> Window(string subject, string qid)
        {
            int now = DateTime.Now.Year;
            try
            {
                if (string.IsNullOrEmpty(qid))
                {
                    var p = await WebCache.GetJson("https://en.wikipedia.org/w/api.php?action=query&format=json&redirects=1&prop=pageprops"
                                                   + "&ppprop=wikibase_item&titles=" + UnityWebRequest.EscapeURL(subject), CancellationToken.None, 10, "search");
                    qid = (string)(p?["query"]?["pages"] as JObject)?.Properties().FirstOrDefault()?.Value?["pageprops"]?["wikibase_item"];
                }
                if (!string.IsNullOrEmpty(qid))
                {
                    var j = await WebCache.GetJson($"https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=claims&ids={qid}",
                                                   CancellationToken.None, 15, "search");
                    var claims = j?["entities"]?[qid]?["claims"] as JObject;
                    foreach (var pair in DatePairs)
                    {
                        int? a = YearOf(claims?[pair[0]]), b = pair[1] != null ? YearOf(claims?[pair[1]]) : null;
                        if (!a.HasValue) continue;
                        if (b.HasValue && b.Value >= a.Value) return (a.Value - 5, Mathf.Min(now, b.Value + 5));
                        return (a.Value - 15, Mathf.Min(now, a.Value + 25));
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[search] dates: {e.Message}"); }
            return (1900, now);
        }

        private static int? YearOf(JToken statements)
        {
            var time = (string)statements?.FirstOrDefault()?["mainsnak"]?["datavalue"]?["value"]?["time"];
            if (string.IsNullOrEmpty(time)) return null;
            var m = Regex.Match(time, @"^([+-])0*(\d+)-");
            if (!m.Success || !int.TryParse(m.Groups[2].Value, out int y)) return null;
            return m.Groups[1].Value == "-" ? -y : y;
        }

        // ---- ui helpers --------------------------------------------------------------

        private static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static T Graphic<T>(RectTransform rt) where T : Graphic
        {
            if (rt.GetComponent<CanvasRenderer>() == null) rt.gameObject.AddComponent<CanvasRenderer>();
            return rt.gameObject.AddComponent<T>();
        }

        private static void Span(RectTransform rt, float left, float yMin, float yMax)
        {
            rt.anchorMin = new Vector2(0f, yMin); rt.anchorMax = new Vector2(1f, yMax);
            rt.offsetMin = new Vector2(left, 0f); rt.offsetMax = new Vector2(-left * 0.15f, 0f);
        }

        private static TMP_Text Text(string name, Transform parent, float size, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.richText = false;
            return t;
        }
    }
}
