using UnityEngine;

namespace SpatialForms.Factories
{
    public class SFRenderGridLayout : SFRenderLayoutFactory
    {
        [SerializeField]
        private float spacing = 0.0f;

        [Tooltip("Gap between rows (timeline row and the related rows below it).")]
        [SerializeField]
        private float rowSpacing = 20.0f;

        // Columns: time, left to right. Rows: 0 is the timeline, related
        // rows go downwards from it. A related row can be scrolled sideways on
        // its own (element.shift, in columns, fractional while moving).
        public override Vector3 GetPositionFor(SFElementRender e)
        {
            float col = e.element.columnIndex + e.element.columnOffset + e.element.shift;
            float x = col * ColumnPitch(e);
            float y = -e.element.row * (e.bounds.size.y + rowSpacing);
            return new Vector3(x, y, 0f);
        }

        public override Quaternion GetRotationFor(SFElementRender e)
        {
            return Quaternion.identity;
        }

        public override float ColumnPitch(SFElementRender e) => e.bounds.size.x + spacing;
    }
}
