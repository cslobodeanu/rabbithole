using SpatialForms.Factories;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using XrProbe;

namespace SpatialForms
{
    public class SFTimelineContentGenerator : SFContentGeneratorFactory
    {
        [Tooltip("Wikimedia throttles placeholder agents. Put a real contact.")]
        public string contact = "you@yourdomain.com";

        [Header("Query")]
        public string subject = "strasbourg";
        public int yearFrom = 1690;
        public int yearTo = 1710;

        [Tooltip("Pull deep for scrubbing instead of top-N ranked.")]
        public bool reel = false;


        [Tooltip("Results to instantiate. Keep low until pooling exists.")]
        public int maxResults = 20;

        IProbeSource _probe;

        void EnsureProbe()
        {
            if (_probe == null)
            {
                CommonsProvider.UserAgent = $"xr-investigation-probe/1.0 ({contact})";
                _probe = new LocalProbe();
            }
        }

        static string Key(Result r) =>
            (r.image?.ImageUrl ?? r.image?.ThumbUrl ?? "") + "|" + (r.text?.SourceUrl ?? "") + "|" + (r.text?.body?.GetHashCode() ?? 0);

        /// <summary>More of the same search for another period, added to an
        /// existing document in date order. Only results dated inside the
        /// period and not already there. Returns how many were added.</summary>
        public async Task<int> AppendAsync(SFDocument doc, string subject, int y0, int y1, CancellationToken ct)
        {
            EnsureProbe();
            var queue = new System.Collections.Concurrent.ConcurrentQueue<Result>();
            var task = _probe.Search(subject, y0, y1, maxResults, reel, onRow: r => queue.Enqueue(r), ct);
            int added = 0;
            void Drain()
            {
                while (queue.TryDequeue(out var r))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!r.YearBegin.HasValue || r.YearBegin.Value > y1 || (r.YearEnd ?? r.YearBegin.Value) < y0) continue;
                    if (!doc.keys.Add(Key(r))) continue;
                    doc.AddElement(new SFElement { index = doc.elements.Count, links = new List<SFElement>(), data = r });
                    added++;
                }
            }
            while (!task.IsCompleted) { Drain(); await Task.Yield(); }
            Drain();
            await task;
            return added;
        }

        public override async Task<SFDocument> GenerateContentAsync(CancellationToken cancellationToken = default)
        {
            EnsureProbe();

            // Use a target document if provided so callers can show partial
            // results as they arrive. Otherwise create and populate a new
            // document to return when finished.
            var doc = TargetDocument ?? new SFDocument();

            var queue = new System.Collections.Concurrent.ConcurrentQueue<Result>();

            // Start the progressive search which will enqueue results as they
            // become available. The provider may invoke the callback from a
            // background thread, so we queue results and process them on the
            // main thread below.
            var searchTask = _probe.Search(subject, yearFrom, yearTo, maxResults, reel,
                onRow: r => { queue.Enqueue(r); }, cancellationToken);

            var started = Time.realtimeSinceStartup;
            int i = 0;

            try
            {
                // While the search is running, drain the queue on the main
                // thread so elements appear immediately.
                while (!searchTask.IsCompleted)
                {
                    while (queue.TryDequeue(out var result))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        doc.keys.Add(Key(result));
                        var el = new SFElement { index = i++, links = new List<SFElement>(), data = result };
                        doc.AddElement(el);
                    }
                    // Yield to allow the frame to render and avoid starving the main thread.
                    await Task.Yield();
                }

                // Drain any remaining items
                while (queue.TryDequeue(out var result))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    doc.keys.Add(Key(result));
                    var el = new SFElement { index = i++, links = new List<SFElement>(), data = result };
                    doc.AddElement(el);
                }

                // Observe any exception from the searchTask
                await searchTask;
            }
            catch (System.OperationCanceledException)
            {
                // A newer search replaced this one; its results go nowhere.
                return doc;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[probe] search failed: {e.Message}");
                return doc;
            }

            Debug.Log($"[probe] {i} results in " + $"{Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f)}ms");

            return doc;
        }
    }
}
