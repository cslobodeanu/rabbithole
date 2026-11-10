// Back through searches, like a browser: every new search remembers the one
// before it (and the card it was started from, if any). Going back re-runs
// that search and returns to the card.
//
// A "back" card stands at the start of the timeline row, before the earliest
// result: "← UFO 1947–1980". Double click it (or Up on the timeline row, or
// pull the timeline down past its top) to go back.
//
// The keep board's tree is kept in step (the search node is reused, not
// duplicated), but the history does not depend on it.

using System.Collections.Generic;
using SpatialForms;
using UnityEngine;
using XrProbe;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(9)]
    public class SearchHistory : MonoBehaviour
    {
        public const string BackSource = "back";

        private class Entry
        {
            public string subject;
            public int y0, y1;
            public Result from;      // the card the next search was started from
        }

        [SerializeField] private SFManager _search;
        [SerializeField] private SFDocumentRender _render;

        private readonly Stack<Entry> _back = new Stack<Entry>();
        private Entry _current;
        private Result _divingFrom;
        private bool _returning;
        private SFElementRender _card;
        private SFDocument _cardDoc;
        private KeepBoard _board;
        private GridInteraction _grid;

        public bool canGoBack => _back.Count > 0;

        private void Awake()
        {
            if (_search == null) _search = FindFirstObjectByType<SFManager>();
            if (_render == null) _render = FindFirstObjectByType<SFDocumentRender>();
            _board = FindFirstObjectByType<KeepBoard>();
            _grid = FindFirstObjectByType<GridInteraction>();
            if (_search != null) _search.SearchStarted += OnSearchStarted;
        }

        private void OnDestroy()
        {
            if (_search != null) _search.SearchStarted -= OnSearchStarted;
        }

        /// <summary>The next search is started from this card.</summary>
        public void NoteDive(Result from) => _divingFrom = from;

        private void OnSearchStarted(string subject, int y0, int y1)
        {
            if (!_returning && _current != null &&
                !(string.Equals(_current.subject, subject, System.StringComparison.OrdinalIgnoreCase) && _current.y0 == y0 && _current.y1 == y1))
            {
                _current.from = _divingFrom;
                _back.Push(_current);
            }
            _returning = false;
            _divingFrom = null;
            _current = new Entry { subject = subject, y0 = y0, y1 = y1 };
            _card = null;            // the old document took it with it
        }

        public void GoBack()
        {
            if (_back.Count == 0 || _search == null)
            {
                Debug.Log("[history] nothing to go back to");
                return;
            }
            var e = _back.Pop();
            Debug.Log($"[history] back to {e.subject} {e.y0}-{e.y1}");
            _returning = true;
            _board?.PrepareReturn(e.subject, e.y0, e.y1);
            _grid?.ReturnTo(e.from);
            _ = _search.Search(e.subject, e.y0, e.y1);
        }

        public static bool IsBackCard(SFElementRender p) =>
            p != null && (p.element?.data?.text?.source == BackSource);

        // ---- the back card -------------------------------------------------------

        private void LateUpdate()
        {
            if (_render == null || _search == null) return;
            var doc = _search.document;
            bool want = _back.Count > 0 && doc != null;
            if (_card != null && (!want || _cardDoc != doc))
            {
                Destroy(_card.gameObject);
                _card = null;
            }
            if (!want) return;

            if (_card == null)
            {
                var prev = _back.Peek();
                var data = new Result
                {
                    label = "back",
                    text = new Card
                    {
                        kind = "text", source = BackSource, title = prev.subject,
                        body = $"{prev.y0}–{prev.y1}",
                        DateBasis = "", credit = "", license = "", SourceUrl = "",
                    },
                };
                _render.EnsureRenderRoot();
                var e = new SFElement { index = -1, row = 0, links = new List<SFElement>(), data = data };
                _card = _render.AddExtra(e);
                _cardDoc = doc;
                if (_card != null) _card.name = "Back";
            }
            if (_card != null)
            {
                // A thin tab snug against the earliest result, a small gap between.
                // Expressed as the element's own layout (column -1 plus a shift),
                // so everything that re-places panels (hover, pull) agrees.
                float pitch = _card.bounds.size.x;
                float w = ((RectTransform)_card.transform).rect.width;
                _card.element.index = -1;
                _card.element.shift = pitch > 0f ? 1f - (w + pitch * 0.04f) / pitch : 0f;
                _card.transform.localPosition = _render.GetLayoutPosition(_card);
            }
        }
    }
}
