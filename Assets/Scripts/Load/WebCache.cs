// One place for "GET this URL as JSON": the user agent, cancellation, the
// query service's 429 hint, and a small disk cache.
//
// The cache is what makes going back cheap and keeps the rate-limited
// Wikidata query service out of the way: an answer fetched once (a row, an
// article's links, a page of archive results) is read from disk for a week,
// across sessions. Only successful answers are kept.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    public static class WebCache
    {
        public static TimeSpan MaxAge = TimeSpan.FromDays(7);

        static string _dir;
        static string Dir
        {
            get
            {
                if (_dir == null)
                {
                    _dir = Path.Combine(Application.persistentDataPath, "webcache");
                    try { Directory.CreateDirectory(_dir); } catch { }
                }
                return _dir;
            }
        }

        static string PathOf(string url)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                var sb = new StringBuilder(40);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return Path.Combine(Dir, sb + ".json");
            }
        }

        /// <summary>The cached body for this URL, or null if none / too old.</summary>
        public static string Read(string url, TimeSpan? maxAge = null)
        {
            try
            {
                string p = PathOf(url);
                if (!File.Exists(p)) return null;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(p) > (maxAge ?? MaxAge)) return null;
                return File.ReadAllText(p);
            }
            catch { return null; }
        }

        public static void Write(string url, string body)
        {
            if (string.IsNullOrEmpty(body)) return;
            string p;
            try { p = PathOf(url); } catch { return; }
            Task.Run(() => { try { File.WriteAllText(p, body); } catch { } });
        }

        /// <summary>Newtonsoft turns ISO dates into locale strings unless told
        /// not to; every year would be lost. Keep strings as strings.</summary>
        public static JToken Parse(string text)
        {
            using (var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(text))
                   { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                return JToken.ReadFrom(reader);
        }

        /// <summary>GET a URL as JSON (cached). Null on any failure.</summary>
        public static async Task<JToken> GetJson(string url, CancellationToken ct, int timeout = 20,
                                                 string tag = "web", TimeSpan? maxAge = null)
        {
            string cached = Read(url, maxAge);
            if (cached != null)
            {
                try { return Parse(cached); } catch { }
            }

            using (var req = await WebGate.Send(url, ct, timeout, tag, accept: "application/json"))
            {
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[{tag}] {req.responseCode} {req.error}");
                    return null;
                }
                string body = req.downloadHandler.text;
                try
                {
                    var j = Parse(body);
                    Write(url, body);
                    return j;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{tag}] unreadable answer: {e.Message}");
                    return null;
                }
            }
        }
    }
}
