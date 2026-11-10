// Deductions: what the trail suggests but no source says. A local model on
// the PC reads the path you came down (search -> card -> card...) and writes
// one guess at a time; the card shows it as it is written, in the cloudy
// precog look (CloudyPanel), so it can never be mistaken for a source.
//
// Any OpenAI-compatible server works (see LocalModel): Ollama by default.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using XrProbe;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(4)]
    public class Deducer : MonoBehaviour
    {
        [Tooltip("OpenAI-compatible endpoint. Ollama: http://localhost:11434/v1 - LM Studio: http://localhost:1234/v1. " +
                 "On the headset use the PC's LAN address.")]
        [SerializeField] private string _endpoint = "http://localhost:11434/v1";
        [SerializeField] private string _model = "llama3.2";
        [Tooltip("Only for servers that want one.")]
        [SerializeField] private string _apiKey = "";
        [SerializeField] private int _maxTokens = 260;
        [SerializeField, Range(0f, 1.5f)] private float _temperature = 0.8f;
        [Tooltip("After a failed call, rows stop asking for this long (D still tries).")]
        [SerializeField] private float _pauseAfterFailure = 45f;

        public class Deduction
        {
            public string title = "", year = "", text = "";
            public bool done;
            public string error;
        }

        private readonly LocalModel _llm = new LocalModel();
        private readonly SemaphoreSlim _one = new SemaphoreSlim(1, 1);   // one generation at a time
        private float _downUntil = -1f;

        /// <summary>False for a while after the model failed to answer.</summary>
        public bool Available => Time.time >= _downUntil;
        public string Model => _model;
        public string Endpoint => _endpoint;

        static readonly string[] Angles =
        {
            "a hidden connection between two of the cards on the trail",
            "what most likely happened next",
            "a cause that none of the cards states",
            "who else was probably involved, and how",
            "what a researcher should look for next, and where to find it",
            "what the people involved probably intended or felt",
            "a consequence that came years later",
        };

        const string System =
            "You are the precog of an investigation interface. The user moves through real, sourced information: " +
            "a trail of cards, from their search to the card they are on. You add deductions - things that are NOT " +
            "stated in the cards but plausibly follow from them. You may be wrong; say how sure you are. Never present " +
            "a guess as fact, never invent quotes or sources. Answer exactly in this format and nothing else:\n" +
            "TITLE: <at most 8 words>\nYEAR: <a year or a range like 1956-1958, or unknown>\n" +
            "TEXT: <2 to 4 sentences, ending with (confidence: low), (confidence: medium) or (confidence: high)>";

        /// <param name="trail">From the search down to the card deduced from (last).</param>
        /// <param name="angle">Which kind of deduction (rotates).</param>
        /// <param name="avoid">Titles already deduced here.</param>
        public async Task<Deduction> Deduce(IList<Result> trail, string subject, int angle, IEnumerable<string> avoid,
                                            Action<Deduction> onUpdate, CancellationToken ct)
        {
            var d = new Deduction();
            _llm.baseUrl = _endpoint;
            _llm.model = _model;
            _llm.apiKey = _apiKey;

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(subject)) sb.Append("Search: ").Append(subject).Append("\n\n");
            sb.Append("Trail, from the search to here:\n");
            for (int i = 0; i < trail.Count; i++)
            {
                var r = trail[i];
                var c = r.text ?? r.image;
                string body = Clip(Regex.Replace(c?.body ?? "", @"\s+", " "), i == trail.Count - 1 ? 700 : 300);
                sb.Append(i + 1).Append(". [").Append(r.label).Append("] ").Append(c?.title).Append(" (")
                  .Append(c?.source).Append("): ").Append(body).Append('\n');
            }
            sb.Append("\nDeduce ").Append(Angles[((angle % Angles.Length) + Angles.Length) % Angles.Length])
              .Append(", about the last card.");
            var done = avoid?.Where(a => !string.IsNullOrEmpty(a)).ToList();
            if (done != null && done.Count > 0) sb.Append("\nAlready deduced here, do not repeat: ").Append(string.Join("; ", done)).Append('.');

            d.text = "waiting for the model…";
            onUpdate?.Invoke(d);
            await _one.WaitAsync(ct);
            try
            {
                d.text = "";
                onUpdate?.Invoke(d);
                string raw = await _llm.Stream(System, sb.ToString(), t => { Parse(t, d); onUpdate?.Invoke(d); },
                                               ct, _maxTokens, _temperature);
                Parse(raw, d);
                if (string.IsNullOrWhiteSpace(d.text) && string.IsNullOrWhiteSpace(d.title))
                    throw new Exception("empty answer");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _downUntil = Time.time + _pauseAfterFailure;
                d.error = $"No local model answered at {_endpoint} ({e.Message}).\n\n" +
                          $"Start Ollama (\"ollama serve\", then \"ollama pull {_model}\") or LM Studio, " +
                          "or change the address on the Deducer component.";
                Debug.LogWarning($"[deduce] {e.Message}");
            }
            finally { _one.Release(); }
            d.done = true;
            onUpdate?.Invoke(d);
            return d;
        }

        static readonly Regex Tag = new Regex(@"(?im)^\s*\**\s*(TITLE|YEAR|TEXT)\s*\**\s*:\s*");

        // The answer so far, into its parts. Reasoning models' <think> blocks are hidden.
        static void Parse(string raw, Deduction d)
        {
            string s = Regex.Replace(raw ?? "", @"(?s)<think>.*?</think>", "");
            int open = s.IndexOf("<think>", StringComparison.Ordinal);
            if (open >= 0) s = s.Substring(0, open);

            var tags = Tag.Matches(s);
            if (tags.Count == 0) { d.text = s.Trim(); return; }
            for (int i = 0; i < tags.Count; i++)
            {
                int start = tags[i].Index + tags[i].Length;
                int end = i + 1 < tags.Count ? tags[i + 1].Index : s.Length;
                string v = s.Substring(start, end - start).Trim();
                switch (tags[i].Groups[1].Value.ToUpperInvariant())
                {
                    case "TITLE": d.title = v.Trim('*', '"', ' '); break;
                    case "YEAR": d.year = v.Trim('*', ' '); break;
                    case "TEXT": d.text = v; break;
                }
            }
        }

        static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
