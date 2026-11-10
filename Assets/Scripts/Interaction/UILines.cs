// Thick polylines drawn as a uGUI graphic, so a RectMask2D clips them along
// with the cards (a LineRenderer would ignore the mask). One graphic draws all
// the links of the board in a single mesh.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TimelineVR.Interaction
{
    [RequireComponent(typeof(CanvasRenderer))]
    public class UILines : MaskableGraphic
    {
        private struct Polyline
        {
            public int start, count;
            public float width;
            public Color32 color;
        }

        private readonly List<Vector2> _points = new List<Vector2>();
        private readonly List<Polyline> _lines = new List<Polyline>();

        public void Clear()
        {
            _points.Clear();
            _lines.Clear();
        }

        /// <summary>Add a line in this graphic's local space. Call Commit() when done.</summary>
        public void Add(IReadOnlyList<Vector2> pts, float width, Color color)
        {
            if (pts == null || pts.Count < 2) return;
            _lines.Add(new Polyline { start = _points.Count, count = pts.Count, width = width, color = color });
            for (int i = 0; i < pts.Count; i++) _points.Add(pts[i]);
        }

        public void Commit() => SetVerticesDirty();

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            foreach (var l in _lines)
            {
                float h = l.width * 0.5f;
                int baseIndex = vh.currentVertCount;
                for (int i = 0; i < l.count; i++)
                {
                    Vector2 p = _points[l.start + i];
                    Vector2 prev = _points[l.start + Mathf.Max(0, i - 1)];
                    Vector2 next = _points[l.start + Mathf.Min(l.count - 1, i + 1)];
                    Vector2 dir = (next - prev);
                    dir = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector2.right;
                    Vector2 n = new Vector2(-dir.y, dir.x) * h;

                    vh.AddVert(p + n, l.color, Vector2.zero);
                    vh.AddVert(p - n, l.color, Vector2.zero);
                }
                for (int i = 0; i < l.count - 1; i++)
                {
                    int a = baseIndex + i * 2;
                    vh.AddTriangle(a, a + 1, a + 3);
                    vh.AddTriangle(a, a + 3, a + 2);
                }
            }
        }
    }
}
