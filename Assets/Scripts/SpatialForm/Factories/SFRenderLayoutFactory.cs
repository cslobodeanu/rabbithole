using UnityEngine;

namespace SpatialForms.Factories
{
    public abstract class SFRenderLayoutFactory : MonoBehaviour
    {
        public abstract Vector3 GetPositionFor(SFElementRender e);
        public abstract Quaternion GetRotationFor(SFElementRender e);

        /// <summary>Distance between neighbouring columns, in the layout's own space.</summary>
        public virtual float ColumnPitch(SFElementRender e) => e.bounds.size.x;
    }
}
