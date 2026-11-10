using UnityEngine;

namespace SpatialForms.Factories
{
    public abstract class SFRenderElementFactory : MonoBehaviour
    {
        public SFElementRender CreateElementRender(SFElement element, SFDocumentRender documentRenderer)
        {
            SFElementRender elem = CreateElementRenderInternal(element, documentRenderer);
            elem.element = element;
            elem.name = element.data.label;
            return elem;
        }
        protected abstract SFElementRender CreateElementRenderInternal(SFElement element, SFDocumentRender documentRenderer);
    }
}
