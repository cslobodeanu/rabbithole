// Data model plus the REMOTE source (talks to probe.py --serve).
//
// LocalProbe is the on-device alternative; both implement IProbeSource, so
// nothing downstream knows which one it has. Keep this one around anyway:
// the Python side is the lab where retrieval gets debugged, and pointing the
// headset at it beats redeploying an APK to test a query change.
//
// Needs Newtonsoft Json (Package Manager -> "com.unity.nuget.newtonsoft-json").
// JsonUtility cannot deserialise `int?` and will silently give you 0 for a
// null year_begin - the field the whole timeline hangs on.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    // ---- wire format: mirrors the JSON from /api/search ------------------

    [Serializable]
    public class Card
    {
        public string kind;            // "image" | "text"
        public string title;
        public string source;          // wikipedia | met | commons | europeana | news
        public string body;

        [JsonProperty("image_url")] public string ImageUrl;
        [JsonProperty("thumb_url")] public string ThumbUrl;

        [JsonProperty("year_begin")] public int? YearBegin;
        [JsonProperty("year_end")]   public int? YearEnd;

        public List<string> keywords = new List<string>();
        public string credit;
        public string license;

        [JsonProperty("source_url")]      public string SourceUrl;

        // The Wikidata item this card is about, when known (related cards).
        // Going down from a card asks about this item, not the search subject.
        [JsonProperty("wikidata")] public string Wikidata;

        // Moving pictures / sound: the file or page to play, and what it is
        // ("video", "audio", "image"). The thumbnail stays in ThumbUrl.
        [JsonProperty("media_url")]  public string MediaUrl;
        [JsonProperty("media_kind")] public string MediaKind;
        [JsonProperty("date_confidence")] public float DateConfidence;
        [JsonProperty("date_basis")]      public string DateBasis;

        // Pixel dimensions of the thumbnail, when the source reports them.
        // Knowing the shape before the bytes arrive is what stops results from
        // jumping as images land - newspaper scans are tall portraits, most
        // engravings are landscape, and a fixed quad crops both badly.
        [JsonProperty("thumb_w")] public int ThumbW;
        [JsonProperty("thumb_h")] public int ThumbH;

        /// <summary>Width / height, or 1 when the source did not say.</summary>
        [JsonIgnore]
        public float Aspect => (ThumbW > 0 && ThumbH > 0)
            ? ThumbW / (float)ThumbH : 1f;

        public float score;
        [JsonProperty("subject_score")] public float SubjectScore;
        [JsonProperty("date_score")]    public float DateScore;

        // Local-only. The server strips both from the wire format: Years is a
        // scoring aid, Haystack is all provider metadata used for matching but
        // never displayed (place names often live in fields no caption shows).
        [JsonIgnore] public List<int> Years = new List<int>();
        [JsonIgnore] public string Haystack;
    }

    [Serializable]
    public class Result
    {
        [JsonProperty("year_begin")]      public int? YearBegin;
        [JsonProperty("year_end")]        public int? YearEnd;
        public string label;                       // "1702" or "1690-1710"
        [JsonProperty("date_confidence")] public float DateConfidence;
        public Card image;                         // may be null
        public Card text;                          // may be null
    }

    [Serializable]
    public class Query
    {
        public string subject;
        [JsonProperty("year_from")] public int YearFrom;
        [JsonProperty("year_to")]   public int YearTo;
    }

    [Serializable]
    public class ProbeResult
    {
        public Query query;
        public List<Result> results = new List<Result>();
        public List<Card> images = new List<Card>();
        public List<Card> text = new List<Card>();

        [JsonProperty("timing_ms")] public Dictionary<string, int> Timing;
        public Dictionary<string, object> funnel;
    }

    // ---- remote source ---------------------------------------------------

    public class ProbeClient : IProbeSource
    {
        readonly string _baseUrl;
        readonly int _timeout;

        /// <param name="baseUrl">
        /// The PC running probe.py --serve. NOT localhost when on device -
        /// that is the headset. Use the PC's LAN address, and allow cleartext
        /// HTTP in the Android manifest, since the probe serves plain http.
        /// </param>
        public ProbeClient(string baseUrl, int timeoutSeconds = 30)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _timeout = timeoutSeconds;
        }

        public async Task<ProbeResult> Search(string subject, int from, int to,
                                              int top, bool reel)
        {
            string url = $"{_baseUrl}/api/search"
                       + $"?q={UnityWebRequest.EscapeURL(subject)}"
                       + $"&year={from}-{to}"
                       + $"&top={top}&reel={(reel ? 1 : 0)}";

            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = _timeout;
                await req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                    throw new Exception($"probe server: {req.error}");

                return JsonConvert.DeserializeObject<ProbeResult>(
                    req.downloadHandler.text);
            }
        }

        // Progressive search: fallback implementation that calls the
        // regular Search API and then emits results. This does not stream
        // while the server is working, but keeps the interface compatible.
        public async Task Search(string subject, int from, int to, int top, bool reel, Action<Result> onResult, System.Threading.CancellationToken cancellationToken = default)
        {
            var result = await Search(subject, from, to, top, reel);
            foreach (var r in result.results)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try { onResult?.Invoke(r); } catch { }
            }
        }
    }
}
