// Panels on the glass share one plane, so a ray cannot tell a board card from
// the grid card behind it by distance. A PanelLayer says "panels under me win,
// and inside my area I hide everything of lower priority" - so a pinch on the
// keep board never reaches the grid through it.

using System.Collections.Generic;
using UnityEngine;

namespace TimelineVR.Interaction
{
    public class PanelLayer : MonoBehaviour
    {
        public int priority = 10;

        [Tooltip("Area that hides lower-priority panels. Defaults to this RectTransform.")]
        public RectTransform area;

        public static readonly List<PanelLayer> Active = new List<PanelLayer>();

        private void Awake()
        {
            if (area == null) area = transform as RectTransform;
        }

        private void OnEnable() => Active.Add(this);
        private void OnDisable() => Active.Remove(this);

        public bool Covers(Vector3 world) =>
            area != null && area.rect.Contains((Vector2)area.InverseTransformPoint(world));

        public static int PriorityOf(Component panel)
        {
            var layer = panel.GetComponentInParent<PanelLayer>();
            return layer != null ? layer.priority : 0;
        }

        public static bool IsHidden(int priority, Vector3 world)
        {
            foreach (var l in Active)
                if (l.priority > priority && l.Covers(world)) return true;
            return false;
        }
    }
}
