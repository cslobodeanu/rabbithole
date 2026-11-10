// Where minimised layers go. Put a Dock on any RectTransform - its rect is
// the dock's area - and choose how slots are laid out in it. A layer whose
// pose is "docked" shrinks into a slot; tap the slot to bring it back.
//
// Layout is one virtual method (SlotCenter), so other shapes - e.g. a ring
// in the middle of the view, summoned with a key - are a subclass or one
// more enum case.
//
// With no Dock in the scene, LayerManager makes one along the bottom right
// of the overlay. A Dock placed on the content canvas is moved to the overlay
// at start so its slots stay sharp (keeping its anchors).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TimelineVR.Interaction
{
    public enum DockLayout { Row, Column }

    public class Dock : MonoBehaviour
    {
        [SerializeField] private DockLayout _layout = DockLayout.Row;

        [Tooltip("Slot size in canvas units.")]
        [SerializeField] private Vector2 _slotSize = new Vector2(150f, 100f);
        [SerializeField] private float _spacing = 10f;

        [Tooltip("Where the slots sit along the dock: 0 = start (left / top), 0.5 = centre, 1 = end.")]
        [SerializeField, Range(0f, 1f)] private float _align = 1f;

        [Tooltip("Draw a faint frame behind each docked layer.")]
        [SerializeField] private bool _slotBackground = true;
        [SerializeField] private Color _slotColor = new Color(0.55f, 0.85f, 1f, 0.12f);

        private RectTransform _area;
        private readonly List<Layer> _docked = new List<Layer>();
        private readonly Dictionary<Layer, RectTransform> _slots = new Dictionary<Layer, RectTransform>();
        private readonly Dictionary<Layer, float> _weights = new Dictionary<Layer, float>();

        public RectTransform area => _area != null ? _area : (_area = transform as RectTransform);

        /// <summary>Called by LayerManager each frame with how docked a layer is (0..1).</summary>
        public void SetWeight(Layer layer, float w)
        {
            _weights[layer] = w;
            bool docked = w > 0.001f;
            if (docked && !_docked.Contains(layer))
            {
                _docked.Add(layer);
                _slots[layer] = MakeSlot(layer.id);
            }
            else if (!docked && _docked.Contains(layer))
            {
                _docked.Remove(layer);
                if (_slots.TryGetValue(layer, out var s) && s != null) Destroy(s.gameObject);
                _slots.Remove(layer);
            }
        }

        public bool TryGetSlot(Layer layer, out Vector3 worldCenter, out Vector2 worldSize)
        {
            worldCenter = default;
            worldSize = default;
            int i = _docked.IndexOf(layer);
            if (i < 0 || area == null) return false;
            Vector2 c = SlotCenter(i, _docked.Count, area.rect);
            worldCenter = area.TransformPoint(c);
            Vector3 s = area.lossyScale;
            worldSize = new Vector2(_slotSize.x * Mathf.Abs(s.x), _slotSize.y * Mathf.Abs(s.y));
            return true;
        }

        /// <summary>The docked layer whose slot contains a world point on the glass.</summary>
        public Layer LayerAt(Vector3 world, float minWeight = 0.5f)
        {
            foreach (var kv in _slots)
            {
                if (kv.Value == null) continue;
                if (_weights.TryGetValue(kv.Key, out float w) && w < minWeight) continue;
                if (kv.Value.rect.Contains((Vector2)kv.Value.InverseTransformPoint(world))) return kv.Key;
            }
            return null;
        }

        /// <summary>Slot centre in the dock's local space. Override for other shapes.</summary>
        protected virtual Vector2 SlotCenter(int i, int n, Rect r)
        {
            if (_layout == DockLayout.Row)
            {
                float total = n * _slotSize.x + (n - 1) * _spacing;
                float x0 = r.xMin + (r.width - total) * _align + _slotSize.x * 0.5f;
                return new Vector2(x0 + i * (_slotSize.x + _spacing), r.center.y);
            }
            else
            {
                float total = n * _slotSize.y + (n - 1) * _spacing;
                float y0 = r.yMax - (r.height - total) * _align - _slotSize.y * 0.5f;
                return new Vector2(r.center.x, y0 - i * (_slotSize.y + _spacing));
            }
        }

        private void LateUpdate()
        {
            if (area == null) return;
            Rect r = area.rect;
            for (int i = 0; i < _docked.Count; i++)
            {
                var layer = _docked[i];
                if (!_slots.TryGetValue(layer, out var slot) || slot == null) continue;
                Vector2 c = SlotCenter(i, _docked.Count, r);
                slot.localPosition = new Vector3(c.x, c.y, 0f);
                slot.sizeDelta = _slotSize;
                float w = _weights.TryGetValue(layer, out float ww) ? ww : 0f;
                var img = slot.GetComponent<Image>();
                if (img != null) img.color = new Color(_slotColor.r, _slotColor.g, _slotColor.b, _slotColor.a * w);
                // A docked slot hides the grid panels under it, so a tap reaches the slot.
                var blocker = slot.GetComponent<PanelLayer>();
                if (blocker != null) blocker.enabled = w > 0.5f;
            }
        }

        private RectTransform MakeSlot(string id)
        {
            var go = new GameObject($"Slot {id}", typeof(RectTransform));
            go.layer = gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(area, false);
            rt.SetAsFirstSibling();                 // behind anything else in the dock
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = _slotSize;
            if (_slotBackground)
            {
                go.AddComponent<CanvasRenderer>();
                var img = go.AddComponent<Image>();
                img.color = new Color(_slotColor.r, _slotColor.g, _slotColor.b, 0f);
                img.raycastTarget = false;
            }
            var blocker = go.AddComponent<PanelLayer>();
            blocker.priority = 50;
            blocker.enabled = false;
            return rt;
        }
    }
}
