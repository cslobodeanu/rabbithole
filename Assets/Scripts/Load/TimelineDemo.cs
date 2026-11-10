// Simplest thing that proves the pipeline end to end: search, then lay the
// rows out with the image and the passage side by side.
//
// Put this on a GameObject, assign rowPrefab, press play. No XR rig needed -
// get it working flat first.
//
// Source is chosen here, not injected: LocalProbe and ProbeClient are plain
// C# classes (Unity cannot serialise an interface field), so the enum picks
// one in Awake and nothing below this line knows the difference.

using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace XrProbe
{
    public class TimelineDemo : MonoBehaviour
    {
        public enum Source
        {
            OnDevice,     // LocalProbe - hits the archive APIs directly, no PC
            Server,       // ProbeClient - talks to probe.py --serve
        }

        [Header("Where the data comes from")]
        public Source source = Source.OnDevice;

        [Tooltip("Only used when source is Server. The PC's LAN address.")]
        public string serverUrl = "http://192.168.1.20:8765";

        [Tooltip("Wikimedia throttles placeholder agents. Put a real contact.")]
        public string contact = "you@yourdomain.com";

        [Header("Query")]
        public string subject = "strasbourg";
        public int yearFrom = 1690;
        public int yearTo = 1710;

        [Tooltip("Pull deep for scrubbing instead of top-N ranked.")]
        public bool reel = false;

        [Header("Layout")]
        public RowDisplayTemplate rowTemplate;
        public Transform container;


        [Tooltip("Rows to instantiate. Keep low until pooling exists.")]
        public int maxRows = 20;

        IProbeSource _probe;
        TextureCache _textures;

        void Awake()
        {
            rowTemplate.gameObject.SetActive(false);

            CommonsProvider.UserAgent = $"xr-investigation-probe/1.0 ({contact})";

            _probe = source == Source.OnDevice
                ? (IProbeSource)new LocalProbe()
                : new ProbeClient(serverUrl);

            _textures = new TextureCache();
        }

        async void Start()
        {
            ProbeResult result;
            try
            {
                result = await _probe.Search(subject, yearFrom, yearTo,
                                             maxRows, reel);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[probe] search failed: {e.Message}");
                return;
            }

            Debug.Log($"[probe] {result.results.Count} rows in "
                    + $"{result.Timing?["total"]}ms");

            await Layout(result);
        }

        async Task Layout(ProbeResult result)
        {
            int n = Mathf.Min(result.results.Count, maxRows);
            for (int i = 0; i < n; i++)
            {
                var row = result.results[i];
                var go = Instantiate(rowTemplate, container);
                go.gameObject.SetActive(true);
                go.name = row.label;

                go.imageCard.header.AddParagraph(row.label);

                go.imageCard.body.AddParagraph(row.text != null ? row.text.body
                                   : row.image != null ? row.image.body
                                   : "");

                var c = row.text ?? row.image;
                go.imageCard.footer.AddParagraph(c == null ? ""
                    : $"{c.title}  ·  {c.source}  ·  {row.label} ({c.DateBasis})");

                // Date confidence is the honest channel for uncertainty -
                // fade or soften rather than hiding a weakly dated row.
                //foreach (var l in labels)
                //    l.alpha = Mathf.Lerp(0.45f, 1f, row.DateConfidence);

                if (row.image != null)
                {
                    go.imageCard.SetImageColor(Color.clear);
                    var tex = await _textures.Load(row.image.ThumbUrl);
                    go.imageCard.SetImage(tex);
                    go.imageCard.SetImageColor(Color.white);
                }
                else
                {
                    go.imageCard.SetImage(null);
                }

                go.imageCard.UpdateCardLayout();
            }
        }

        void OnDestroy() => _textures?.Clear();
    }
}
