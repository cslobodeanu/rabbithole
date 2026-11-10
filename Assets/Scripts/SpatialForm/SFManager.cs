using System;
using System.Threading;
using System.Threading.Tasks;
using SpatialForms.Factories;
using UnityEngine;

namespace SpatialForms
{
    public class SFManager : MonoBehaviour
    {
        [SerializeField]
        private SFContentGeneratorFactory content;

        [SerializeField]
        private SFDocumentRender currentDocumentRenderer;

        /// <summary>A new search replaced the grid: subject, yearFrom, yearTo.</summary>
        public event Action<string, int, int> SearchStarted;

        private CancellationTokenSource _cts;

        /// <summary>The document on screen, and the search that made it.</summary>
        public SFDocument document { get; private set; }
        public string subject { get; private set; }
        public int yearFrom { get; private set; }
        public int yearTo { get; private set; }

        /// <summary>True while the search itself is still arriving.</summary>
        public bool searching { get; private set; }

        /// <summary>Add results for another period to the current timeline (the
        /// ends of an endless timeline). Returns how many were new; -1 when the
        /// content source cannot do it.</summary>
        public async Task<int> Extend(int y0, int y1)
        {
            if (!(content is SFTimelineContentGenerator t) || document == null || subject == null || _cts == null) return -1;
            var doc = document;
            try { return await t.AppendAsync(doc, subject, y0, y1, _cts.Token); }
            catch (OperationCanceledException) { return 0; }
            catch (Exception e)
            {
                Debug.LogWarning($"[SFManager] extending the timeline failed: {e.Message}");
                return 0;
            }
        }

        private async void Start()
        {
            if (content is SFTimelineContentGenerator t)
                await Search(t.subject, t.yearFrom, t.yearTo);
            else
                await Run(null, 0, 0);
        }

        /// <summary>Replace the grid with a new search. Cancels the one in flight.</summary>
        public Task Search(string subject, int yearFrom, int yearTo)
        {
            if (content is SFTimelineContentGenerator t)
            {
                t.subject = subject;
                t.yearFrom = yearFrom;
                t.yearTo = yearTo;
            }
            return Run(subject, yearFrom, yearTo);
        }

        private async Task Run(string subject, int yearFrom, int yearTo)
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();

            // Create a document and let the generator populate it incrementally.
            var doc = new SFDocument();
            content.TargetDocument = doc;
            currentDocumentRenderer.document = doc;
            document = doc;
            this.subject = subject;
            this.yearFrom = yearFrom;
            this.yearTo = yearTo;
            searching = true;

            if (subject != null) SearchStarted?.Invoke(subject, yearFrom, yearTo);

            try
            {
                await content.GenerateContentAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer search
            }
            catch (Exception e)
            {
                Debug.LogError($"[SFManager] content generation failed: {e.Message}");
            }
            finally
            {
                if (content.TargetDocument == doc) content.TargetDocument = null;
                if (document == doc) searching = false;
            }
        }

        private void OnDestroy() => _cts?.Cancel();
    }
}
