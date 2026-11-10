// An endless timeline: when the focus comes near either end, the same search
// runs for the next period beyond it and the results join the timeline in
// date order. Results inserted before the card you look at shift the grid by
// as many columns, so nothing appears to move.
//
// A period that brings nothing new doubles the next step (sparse eras are
// skipped quickly); one that does resets it. Stops at the present, and at
// year 1 going back.

using System;
using SpatialForms;
using UnityEngine;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(6)]
    public class TimelineExtender : MonoBehaviour
    {
        [SerializeField] private GridInteraction _grid;
        [SerializeField] private SFManager _search;

        [Tooltip("Start extending when the focus is this many cards from an end.")]
        [SerializeField] private int _ahead = 3;
        [Tooltip("Shortest period asked for at a time, in years.")]
        [SerializeField] private int _minSpan = 10;
        [Tooltip("Longest jump over an empty stretch, in years.")]
        [SerializeField] private int _maxSpan = 320;
        [SerializeField] private int _earliestYear = 1;

        private SFDocument _doc;
        private int _from, _to, _baseSpan, _spanEarlier, _spanLater;
        private bool _busy;
        private SFElement _focus;

        private void Awake()
        {
            if (_grid == null) _grid = FindFirstObjectByType<GridInteraction>();
            if (_search == null) _search = FindFirstObjectByType<SFManager>();
            if (_search != null) _search.SearchStarted += OnSearchStarted;
        }

        private void OnDestroy()
        {
            if (_search != null) _search.SearchStarted -= OnSearchStarted;
            if (_doc != null) _doc.ElementAdded -= OnAdded;
        }

        private void OnSearchStarted(string subject, int y0, int y1)
        {
            if (_doc != null) _doc.ElementAdded -= OnAdded;
            _doc = _search.document;
            if (_doc != null) _doc.ElementAdded += OnAdded;
            _from = Mathf.Min(y0, y1);
            _to = Mathf.Max(y0, y1);
            _baseSpan = _spanEarlier = _spanLater = Mathf.Max(_minSpan, _to - _from + 1);
            _busy = false;
            _focus = null;
        }

        private void Update()
        {
            if (_grid == null || _search == null || _doc == null || _doc != _search.document) return;

            var f = _grid.FocusPanel();
            if (f != null && f.element != null && f.element.row == 0) _focus = f.element;
            if (_busy || _search.searching || _focus == null) return;

            int n = _doc.elements.Count;
            if (n == 0) return;
            int i = _focus.index;
            if (i >= n - 1 - _ahead && _to < DateTime.Now.Year) _ = Later();
            else if (i <= _ahead && _from > _earliestYear) _ = Earlier();
        }

        private async System.Threading.Tasks.Task Later()
        {
            _busy = true;
            var doc = _doc;
            int y0 = _to + 1, y1 = Mathf.Min(DateTime.Now.Year, _to + _spanLater);
            int added = await _search.Extend(y0, y1);
            if (doc != _doc) return;            // a new search replaced it
            _busy = false;
            if (added < 0) { enabled = false; return; }
            _to = y1;
            _spanLater = added == 0 ? Mathf.Min(_maxSpan, _spanLater * 2) : _baseSpan;
            Debug.Log($"[timeline] {y0}–{y1}: {added} new");
        }

        private async System.Threading.Tasks.Task Earlier()
        {
            _busy = true;
            var doc = _doc;
            int y1 = _from - 1, y0 = Mathf.Max(_earliestYear, _from - _spanEarlier);
            int added = await _search.Extend(y0, y1);
            if (doc != _doc) return;
            _busy = false;
            if (added < 0) { enabled = false; return; }
            _from = y0;
            _spanEarlier = added == 0 ? Mathf.Min(_maxSpan, _spanEarlier * 2) : _baseSpan;
            Debug.Log($"[timeline] {y0}–{y1}: {added} new");
        }

        // Inserted before the card in focus: everything after it moved one
        // column right; move the grid back so the view stays put.
        private void OnAdded(SFElement e)
        {
            if (_search.searching || _focus == null || e == _focus || e.row != 0) return;
            if (e.index <= _focus.index) _grid.ShiftColumns(1);
        }
    }
}
