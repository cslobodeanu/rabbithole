// Panels are uGUI RectTransforms. Hands (and the mouse stand-in) find them
// with physics rays, so each one gets a BoxCollider matching its rect. A
// trigger, so it never pushes anything around.

using SpatialForms;
using UnityEngine;

namespace TimelineVR.Interaction
{
    public static class PanelUtil
    {
        public static void EnsureCollider(SFElementRender panel)
        {
            if (panel == null) return;
            var rt = panel.transform as RectTransform;
            var box = panel.GetComponent<BoxCollider>();
            if (box == null) box = panel.gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;

            if (rt != null)
            {
                Rect r = rt.rect;
                box.center = new Vector3(r.center.x, r.center.y, 0f);
                box.size = new Vector3(r.width, r.height, 1f);
            }
            else
            {
                box.center = panel.bounds.center;
                box.size = new Vector3(panel.bounds.size.x, panel.bounds.size.y, 1f);
            }
        }

        /// <summary>Visual centre of the panel in world space.</summary>
        public static Vector3 WorldCenter(Component panel)
        {
            var rt = panel.transform as RectTransform;
            return rt != null ? rt.TransformPoint(rt.rect.center) : panel.transform.position;
        }

        /// <summary>World-space width and height of the panel.</summary>
        public static Vector2 WorldSize(Component panel)
        {
            var rt = panel.transform as RectTransform;
            if (rt == null) return Vector2.one;
            Vector3 s = rt.lossyScale;
            return new Vector2(rt.rect.width * Mathf.Abs(s.x), rt.rect.height * Mathf.Abs(s.y));
        }

        public static void SetLayer(GameObject go, int layer)
        {
            if (go == null) return;
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayer(t.gameObject, layer);
        }

        public static CanvasGroup Group(Component panel)
        {
            var g = panel.GetComponent<CanvasGroup>();
            return g != null ? g : panel.gameObject.AddComponent<CanvasGroup>();
        }
    }
}
