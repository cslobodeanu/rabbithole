using UnityEngine;
using System.Collections.Generic;

namespace SpatialForms
{
    public class SFDocument
    {
        private List<SFElement> _elements = new List<SFElement>();
        // Expose the backing list for compatibility but prefer AddElement
        // so listeners can be notified when new elements arrive.
        public List<SFElement> elements => _elements;

        public event System.Action<SFElement> ElementAdded;

        /// <summary>Keys of the results already in the document, so extending
        /// the timeline never adds one twice.</summary>
        public readonly HashSet<string> keys = new HashSet<string>();

        public void AddElement(SFElement element)
        {
            // Insert element into the list keeping chronological order by
            // YearBegin then YearEnd. Undated items go to the end.
            int GetKey(SFElement e)
            {
                if (e == null || e.data == null) return int.MaxValue;
                return e.data.YearBegin ?? int.MaxValue;
            }

            int GetEnd(SFElement e)
            {
                if (e == null || e.data == null) return int.MaxValue;
                return e.data.YearEnd ?? (e.data.YearBegin ?? int.MaxValue);
            }

            int key = GetKey(element);
            int insert = _elements.FindIndex(e =>
                GetKey(e) > key || (GetKey(e) == key && GetEnd(e) > GetEnd(element)));

            if (insert >= 0) _elements.Insert(insert, element);
            else _elements.Add(element);

            // Re-index elements to reflect list order
            for (int i = 0; i < _elements.Count; i++) _elements[i].index = i;

            ElementAdded?.Invoke(element);
        }
    }
}