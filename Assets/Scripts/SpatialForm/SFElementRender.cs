using UnityEngine;

namespace SpatialForms
{
    public abstract class SFElementRender : MonoBehaviour
    {
        public SFDocumentRender documentRenderer { get; set; }

        public SFElement element { get; set; }

        public Bounds bounds { get; set; }
    }
}
