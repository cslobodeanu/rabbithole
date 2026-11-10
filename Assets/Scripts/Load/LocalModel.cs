// A language model running on the PC, streamed token by token.
//
// Speaks the OpenAI-compatible chat API, which Ollama, LM Studio, llama.cpp's
// server, vLLM and others all serve:
//   Ollama     http://localhost:11434/v1   (ollama pull llama3.2)
//   LM Studio  http://localhost:1234/v1
// From the headset, use the PC's LAN address instead of localhost (and allow
// cleartext HTTP in the Android manifest).

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    public class LocalModel
    {
        public string baseUrl = "http://localhost:11434/v1";
        public string model = "llama3.2";
        public string apiKey = "";

        /// <summary>Stream a chat answer; onText gets the whole text so far,
        /// on the main thread. Returns the full answer. Throws when no model
        /// answers.</summary>
        public async Task<string> Stream(string system, string user, Action<string> onText,
                                         CancellationToken ct, int maxTokens = 260, float temperature = 0.8f)
        {
            var body = new JObject
            {
                ["model"] = model,
                ["stream"] = true,
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = system },
                    new JObject { ["role"] = "user", ["content"] = user },
                },
            };

            var sse = new SseHandler(onText);
            using (var req = new UnityWebRequest(baseUrl.TrimEnd('/') + "/chat/completions", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
                req.downloadHandler = sse;
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "text/event-stream");
                if (!string.IsNullOrEmpty(apiKey)) req.SetRequestHeader("Authorization", "Bearer " + apiKey);
                req.timeout = 180;

                var op = req.SendWebRequest();
                using (ct.Register(() => { try { req.Abort(); } catch { } }))
                    await op;
                ct.ThrowIfCancellationRequested();

                if (req.result != UnityWebRequest.Result.Success && sse.Text.Length == 0)
                    throw new Exception($"{req.responseCode} {req.error} {sse.ErrorBody}".Trim());
                return sse.Text.ToString();
            }
        }

        // Server-sent events: "data: {json}\n\n" lines, "data: [DONE]" at the end.
        class SseHandler : DownloadHandlerScript
        {
            readonly Action<string> _onText;
            readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
            readonly StringBuilder _line = new StringBuilder();
            public readonly StringBuilder Text = new StringBuilder();
            readonly StringBuilder _raw = new StringBuilder();

            public SseHandler(Action<string> onText) : base(new byte[4096]) { _onText = onText; }

            public string ErrorBody => _raw.Length > 300 ? _raw.ToString(0, 300) : _raw.ToString();

            protected override bool ReceiveData(byte[] data, int length)
            {
                if (data == null || length == 0) return true;
                var chars = new char[_utf8.GetCharCount(data, 0, length)];
                _utf8.GetChars(data, 0, length, chars, 0);
                bool changed = false;
                foreach (char ch in chars)
                {
                    if (ch != '\n') { _line.Append(ch); continue; }
                    changed |= Line(_line.ToString().TrimEnd('\r'));
                    _line.Clear();
                }
                if (changed) { try { _onText?.Invoke(Text.ToString()); } catch (Exception e) { Debug.LogException(e); } }
                return true;
            }

            bool Line(string line)
            {
                if (_raw.Length < 2000) _raw.Append(line).Append('\n');
                if (!line.StartsWith("data:")) return false;
                string payload = line.Substring(5).Trim();
                if (payload.Length == 0 || payload == "[DONE]") return false;
                try
                {
                    var j = JObject.Parse(payload);
                    string piece = (string)j["choices"]?[0]?["delta"]?["content"]
                                ?? (string)j["choices"]?[0]?["text"];
                    if (string.IsNullOrEmpty(piece)) return false;
                    Text.Append(piece);
                    return true;
                }
                catch { return false; }
            }
        }
    }
}
