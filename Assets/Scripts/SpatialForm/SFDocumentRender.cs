using SpatialForms.Factories;
using UnityEngine;

namespace SpatialForms
{
    public class SFDocumentRender : MonoBehaviour
    {
        [SerializeField]
        private SFRenderElementFactory renderElementsFactory;

        [SerializeReference]
        private SFRenderLayoutFactory layoutFactory;

        [SerializeField]
        private Transform container;

        [SerializeField]
        private SFDocument _document;

        private Transform _renderRoot;

        public SFDocument document 
        {
            set
            {
                // Unsubscribe previous document events
                if (_document != null)
                    _document.ElementAdded -= OnDocumentElementAdded;

                ClearDocument();

                _document = value;

                if (_document != null)
                    _document.ElementAdded += OnDocumentElementAdded;
            }
        }

        /// <summary>Create the root panels live under, if there is none yet
        /// (placeholders shown before the first result).</summary>
        public Transform EnsureRenderRoot()
        {
            if (_renderRoot == null)
            {
                GameObject _root = new GameObject("RenderRoot");
                _root.transform.SetParent(container, false);
                _renderRoot = _root.transform;
            }
            return _renderRoot;
        }

        private void OnDocumentElementAdded(SFElement element)
        {
            // Add the single element to the render immediately.
            EnsureRenderRoot();

            ProcessElement(element);

            // After adding a new element, update positions of all rendered
            // element renders to reflect the document ordering (their
            // element.index values may have changed when the element was
            // inserted).
            UpdateAllElementPositions();
        }

        /// <summary>Where the layout wants this panel, in its parent's space.
        /// Interaction offsets (pull, hover) are applied on top of this.</summary>
        public Vector3 GetLayoutPosition(SFElementRender er) => layoutFactory.GetPositionFor(er);

        /// <summary>Distance between neighbouring columns, in world units.</summary>
        public float ColumnPitchWorld(SFElementRender er)
        {
            float local = layoutFactory.ColumnPitch(er);
            return _renderRoot != null ? local * Mathf.Abs(_renderRoot.lossyScale.x) : local;
        }

        /// <summary>Root that all rendered panels of this document live under.</summary>
        public Transform renderRoot => _renderRoot;

        /// <summary>Render an element that is not part of the document - a
        /// related panel in a row below the timeline. It lives with the rest
        /// (pans, zooms, is kept / opened like any panel) and goes away with
        /// the document.</summary>
        public SFElementRender AddExtra(SFElement element)
        {
            if (_renderRoot == null) return null;
            var er = renderElementsFactory.CreateElementRender(element, this);
            er.documentRenderer = this;
            er.transform.SetParent(_renderRoot, false);
            er.transform.localPosition = layoutFactory.GetPositionFor(er);
            er.transform.localRotation = layoutFactory.GetRotationFor(er);
            TimelineVR.Interaction.PanelUtil.EnsureCollider(er);
            return er;
        }

        private void UpdateAllElementPositions()
        {
            if (_renderRoot == null) return;
            // Iterate children and reposition each SFElementRender based on
            // its associated element data.
            foreach (Transform child in _renderRoot)
            {
                var er = child.GetComponent<SFElementRender>();
                if (er == null) continue;
                er.transform.localPosition = layoutFactory.GetPositionFor(er);
                er.transform.localRotation = layoutFactory.GetRotationFor(er);
            }
        }

        private void ClearDocument()
        {
            if (_renderRoot != null)
            {
                Destroy(_renderRoot.gameObject);
                _renderRoot = null;
            }
        }

        private void ProcessElement(SFElement element)
        {
            var elementRender = renderElementsFactory.CreateElementRender(element, this);
            elementRender.documentRenderer = this;
            elementRender.transform.localPosition = layoutFactory.GetPositionFor(elementRender);
            elementRender.transform.localRotation = layoutFactory.GetRotationFor(elementRender);
            elementRender.transform.SetParent(_renderRoot, false);

            // Hands find panels with physics rays.
            TimelineVR.Interaction.PanelUtil.EnsureCollider(elementRender);

            foreach (var childElement in element.links)
            {
                ProcessElement(childElement);
            }
        }
    }
}
