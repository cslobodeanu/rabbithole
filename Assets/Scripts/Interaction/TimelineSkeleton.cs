// Grey, shimmering cards on the timeline while a search is still arriving:
// from the moment it starts (before the first result there is nothing at
// all), and after the last result while more are coming. They are not part
// of the document - nothing counts, extends from or opens them - and they go
// as soon as the search is done.

using System.Collections.Generic;
using SpatialForms;
using UnityEngine;
using XrProbe;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(8)]
    public class TimelineSkeleton : MonoBehaviour
    {
        [SerializeField] private SFManager _search;
        [SerializeField] private SFDocumentRender _render;
        [Tooltip("Grey cards after the last result while the search runs.")]
        [SerializeField] private int _count = 3;

        private readonly List<SFElementRender> _cards = new List<SFElementRender>();
        private string _caption;
        private bool _empty = true;

        private void Awake()
        {
            if (_search == null) _search = FindFirstObjectByType<SFManager>();
            if (_render == null) _render = FindFirstObjectByType<SFDocumentRender>();
            if (_search != null) _search.SearchStarted += OnSearchStarted;
        }

        private void OnDestroy()
        {
            if (_search != null) _search.SearchStarted -= OnSearchStarted;
        }

        private void OnSearchStarted(string subject, int y0, int y1) =>
            _caption = $"Searching {subject}, {Mathf.Min(y0, y1)}–{Mathf.Max(y0, y1)}…";

        private void LateUpdate()
        {
            if (_search == null || _render == null) return;
            _cards.RemoveAll(c => c == null);          // a new document took the old root with it

            bool want = _search.searching && _search.document != null;
            if (!want)
            {
                foreach (var c in _cards) Destroy(c.gameObject);
                _cards.Clear();
                _empty = true;
                return;
            }

            _render.EnsureRenderRoot();
            int n = _search.document.elements.Count;
            while (_cards.Count < _count)
            {
                var e = new SFElement
                {
                    index = n + _cards.Count, row = 0, links = new List<SFElement>(),
                    data = new Result { label = "", text = new Card { kind = "text", title = "", body = "", source = "", DateBasis = "" } },
                };
                var card = _render.AddExtra(e);
                if (card == null) return;
                if (card is CoverCard cover) cover.SetSkeleton(true, _cards.Count == 0 && n == 0 ? _caption : "");
                card.name = "Loading";
                _cards.Add(card);
            }

            // Always just after the last real result (they move as results arrive).
            for (int i = 0; i < _cards.Count; i++)
            {
                var c = _cards[i];
                c.element.index = n + i;
                c.transform.localPosition = _render.GetLayoutPosition(c);
                if (i == 0 && (n == 0) != _empty && c is CoverCard cover) cover.SetSkeleton(true, n == 0 ? _caption : "");
            }
            _empty = n == 0;
        }
    }
}
