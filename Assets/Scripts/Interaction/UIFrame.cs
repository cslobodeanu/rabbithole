// A rectangular border drawn as a uGUI graphic - no sprite needed, clipped
// by masks like any other UI. Stretch it over the thing to outline.

using UnityEngine;
using UnityEngine.UI;

namespace TimelineVR.Interaction
{
    [RequireComponent(typeof(CanvasRenderer))]
    public class UIFrame : MaskableGraphic
    {
        [SerializeField] private float _thickness = 6f;

        public float thickness
        {
            get => _thickness;
            set { _thickness = Mathf.Max(0f, value); SetVerticesDirty(); }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            float t = Mathf.Min(_thickness, Mathf.Min(r.width, r.height) * 0.5f);
            if (t <= 0f) return;

            // Outer and inner corners, then four quads between them.
            Vector2 o0 = new Vector2(r.xMin, r.yMin), o1 = new Vector2(r.xMax, r.yMin),
                    o2 = new Vector2(r.xMax, r.yMax), o3 = new Vector2(r.xMin, r.yMax);
            Vector2 i0 = o0 + new Vector2(t, t), i1 = o1 + new Vector2(-t, t),
                    i2 = o2 + new Vector2(-t, -t), i3 = o3 + new Vector2(t, -t);

            Color32 c = color;
            foreach (var p in new[] { o0, o1, o2, o3, i0, i1, i2, i3 }) vh.AddVert(p, c, Vector2.zero);
            // indices: outer 0..3, inner 4..7
            Quad(vh, 0, 1, 5, 4);   // bottom
            Quad(vh, 1, 2, 6, 5);   // right
            Quad(vh, 2, 3, 7, 6);   // top
            Quad(vh, 3, 0, 4, 7);   // left
        }

        private static void Quad(VertexHelper vh, int a, int b, int c, int d)
        {
            vh.AddTriangle(a, b, c);
            vh.AddTriangle(a, c, d);
        }
    }
}
