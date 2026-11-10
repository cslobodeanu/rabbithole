// Every request to a public service goes through here, so the app as a whole
// behaves politely however many rows, cards and thumbnails ask at once:
//
//  - a few requests at a time per host (Wikimedia answers bursts with 429),
//  - a 429 / 503 pauses that whole host for what Retry-After asks (or a
//    growing wait), then the request is tried again,
//  - the user agent with a contact, which Wikimedia requires,
//  - Wikimedia thumbnails snapped to the standard widths: other widths have
//    to be rendered on demand and are rate-limited much harder.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    public static class WebGate
    {
        /// <summary>Wikimedia's pre-rendered thumbnail widths.</summary>
        public static readonly int[] StandardWidths = { 20, 40, 60, 120, 250, 330, 500, 960, 1280, 1920, 3840 };

        /// <summary>The width cards ask for (a standard one).</summary>
        public const int CardWidth = 500;

        class Host
        {
            public SemaphoreSlim slots;
            public DateTime until;
            public int strikes;
        }

        static readonly Dictionary<string, Host> _hosts = new Dictionary<string, Host>();

        static int LimitOf(string host)
        {
            if (host == "upload.wikimedia.org") return 3;
            if (host == "query.wikidata.org") return 2;
            if (host.EndsWith("wikimedia.org") || host.EndsWith("wikipedia.org") || host.EndsWith("wikidata.org")) return 3;
            return 4;
        }

        static Host HostOf(string url)
        {
            string host = "";
            try { host = new Uri(url).Host.ToLowerInvariant(); } catch { }
            // Thumbnails are served from both; they share one budget.
            if (host == "thumb.wikimedia.org") host = "upload.wikimedia.org";
            lock (_hosts)
            {
                if (!_hosts.TryGetValue(host, out var h))
                    _hosts[host] = h = new Host { slots = new SemaphoreSlim(LimitOf(host)) };
                return h;
            }
        }

        static readonly Regex ThumbPx = new Regex(@"(/thumb/.+/(?:[a-z0-9-]*-)?)(\d+)(px-[^/]+)$", RegexOptions.IgnoreCase);
        static readonly Regex WidthParam = new Regex(@"([?&]width=)(\d+)", RegexOptions.IgnoreCase);

        static int Snap(int w)
        {
            int best = StandardWidths[0];
            foreach (var s in StandardWidths) if (s <= w) best = s;
            return best;
        }

        /// <summary>A Wikimedia thumbnail URL at the nearest standard width at
        /// or below the one asked for. Other URLs unchanged.</summary>
        public static string Standard(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            url = Direct(url);
            if (url.Contains("upload.wikimedia.org"))
            {
                var m = ThumbPx.Match(url);
                if (m.Success && int.TryParse(m.Groups[2].Value, out int w))
                    return url.Substring(0, m.Index) + m.Groups[1].Value + Snap(w) + m.Groups[3].Value;
            }
            else if (url.Contains("Special:FilePath"))
            {
                var m = WidthParam.Match(url);
                if (m.Success && int.TryParse(m.Groups[2].Value, out int w))
                    return url.Substring(0, m.Index) + m.Groups[1].Value + Snap(w) + url.Substring(m.Index + m.Length);
            }
            return url;
        }

        static readonly Regex Original = new Regex(@"^(https://upload\.wikimedia\.org/[^/]+/[^/]+/)([0-9a-f]/[0-9a-f]{2}/)([^/?#]+\.(?:jpe?g|png))$", RegexOptions.IgnoreCase);

        /// <summary>The same picture at (about) this width, where the host can
        /// do that: Wikimedia thumbnails and originals, Special:FilePath.
        /// Standard widths only. Other URLs unchanged.</summary>
        public static string AtWidth(string url, int width)
        {
            if (string.IsNullOrEmpty(url)) return url;
            int w = Snap(width);
            if (url.Contains("Special:FilePath") && !WidthParam.IsMatch(url))
                url += (url.Contains("?") ? "&" : "?") + "width=" + w;
            url = Direct(url);
            if (url.Contains("upload.wikimedia.org"))
            {
                var m = ThumbPx.Match(url);
                if (m.Success) return url.Substring(0, m.Index) + m.Groups[1].Value + w + m.Groups[3].Value;
                var o = Original.Match(url);
                if (o.Success)
                    return o.Groups[1].Value + "thumb/" + o.Groups[2].Value + o.Groups[3].Value + "/" + w + "px-" + o.Groups[3].Value;
            }
            else if (url.Contains("Special:FilePath"))
            {
                var m = WidthParam.Match(url);
                if (m.Success) return url.Substring(0, m.Index) + m.Groups[1].Value + w + url.Substring(m.Index + m.Length);
                return url + (url.Contains("?") ? "&" : "?") + "width=" + w;
            }
            return url;
        }

        // ---- Special:FilePath without the redirects --------------------------

        static readonly Regex FilePath = new Regex(@"^https?://commons\.wikimedia\.org/wiki/Special:FilePath/([^?#]+)(?:\?(.*))?$", RegexOptions.IgnoreCase);

        /// <summary>Special:FilePath (what Wikidata's pictures link to) costs
        /// three requests - two redirects through Wikimedia's uncached page
        /// servers, then the image. The image's own address follows from the
        /// file name (an MD5 of it picks the folder), so go there directly.
        /// Unknown file types are left alone.</summary>
        public static string Direct(string url)
        {
            var m = FilePath.Match(url ?? "");
            if (!m.Success) return url;
            string name = Uri.UnescapeDataString(m.Groups[1].Value).Replace(' ', '_').Trim('_');
            if (name.Length == 0) return url;
            name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            int width = 0;
            var wm = WidthParam.Match("?" + (m.Groups[2].Value ?? ""));
            if (wm.Success) int.TryParse(wm.Groups[2].Value, out width);

            string hash;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var h = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(name));
                hash = h[0].ToString("x2");
            }
            string e = Escape(name);
            string dir = $"https://upload.wikimedia.org/wikipedia/commons/";
            string folder = $"{hash[0]}/{hash}/";
            if (width <= 0) return dir + folder + e;

            string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            string w = Snap(width) + "px-";
            string thumb;
            switch (ext)
            {
                case ".jpg": case ".jpeg": case ".png": case ".gif": case ".webp": thumb = w + e; break;
                case ".svg": thumb = w + e + ".png"; break;
                case ".tif": case ".tiff": thumb = "lossy-page1-" + w + e + ".jpg"; break;
                case ".pdf": case ".djvu": thumb = "page1-" + w + e + ".jpg"; break;
                case ".webm": case ".ogv": thumb = w + "-" + e + ".jpg"; break;
                default: return url;
            }
            return dir + "thumb/" + folder + e + "/" + thumb;
        }

        // As MediaWiki writes file URLs: these stay literal.
        static string Escape(string name)
        {
            var sb = new System.Text.StringBuilder(Uri.EscapeDataString(name));
            foreach (var (code, ch) in new[] { ("%28", "("), ("%29", ")"), ("%21", "!"), ("%2C", ","), ("%3B", ";"),
                                                ("%3A", ":"), ("%40", "@"), ("%24", "$"), ("%7E", "~"), ("%2A", "*"), ("%27", "'") })
                sb.Replace(code, ch);
            return sb.ToString();
        }

        /// <summary>For a Wikimedia thumbnail that failed (a file narrower
        /// than the width asked for has no such thumbnail): the original
        /// file, if Unity can read its type. Null otherwise.</summary>
        public static string OriginalOf(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.Contains("upload.wikimedia.org") || !url.Contains("/thumb/")) return null;
            int q = url.IndexOf('?');
            if (q >= 0) url = url.Substring(0, q);
            int last = url.LastIndexOf('/');
            if (last < 0) return null;
            string orig = url.Substring(0, last).Replace("/thumb/", "/");
            string ext = System.IO.Path.GetExtension(orig).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" ? orig : null;
        }

        // ---- pictures Unity can decode -----------------------------------------

        // Unity decodes JPEG and PNG only. Wikimedia renders any thumbnail as
        // PNG when ".png" is added to its name.
        static readonly Regex Undecodable = new Regex(@"\.(webp|gif)$", RegexOptions.IgnoreCase);
        static readonly Regex AnyOriginal = new Regex(@"^(https://upload\.wikimedia\.org/[^/]+/[^/]+/)([0-9a-f]/[0-9a-f]{2}/)([^/]+)$", RegexOptions.IgnoreCase);

        /// <summary>URLs to try, in order, for a picture Unity can decode.</summary>
        public static List<string> Decodable(string url)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(url)) return list;
            url = Standard(url);
            int q = url.IndexOf('?');
            string path = q >= 0 ? url.Substring(0, q) : url;
            if (!path.Contains("upload.wikimedia.org") && !path.Contains("thumb.wikimedia.org") || !Undecodable.IsMatch(path))
            {
                list.Add(url);
                return list;
            }
            path = path.Replace("://thumb.wikimedia.org/", "://upload.wikimedia.org/");
            if (path.Contains("/thumb/")) { list.Add(path + ".png"); return list; }
            var m = AnyOriginal.Match(path);
            if (!m.Success) { list.Add(url); return list; }
            // An original: its size is unknown, so ask for smaller and smaller thumbnails.
            string name = m.Groups[3].Value;
            foreach (int w in new[] { CardWidth, 330, 250, 120 })
                list.Add(m.Groups[1].Value + "thumb/" + m.Groups[2].Value + name + "/" + w + "px-" + name + ".png");
            return list;
        }

        /// <summary>GET a picture as a texture: in a format Unity can decode,
        /// and the original file when a thumbnail that wide does not exist.
        /// The caller disposes the result (which may have failed).</summary>
        public static async Task<UnityWebRequest> GetTexture(string url, CancellationToken ct, int timeout, string tag, bool nonReadable)
        {
            UnityWebRequest req = null;
            foreach (var u in Decodable(url))
            {
                req?.Dispose();
                req = await Send(u, ct, timeout, tag, x => UnityWebRequestTexture.GetTexture(x, nonReadable));
                if (req.result == UnityWebRequest.Result.Success || req.responseCode == 429) return req;
                string orig = OriginalOf(req.url);
                if (orig != null)
                {
                    req.Dispose();
                    req = await Send(orig, ct, timeout, tag, x => UnityWebRequestTexture.GetTexture(x, nonReadable));
                    if (req.result == UnityWebRequest.Result.Success || req.responseCode == 429) return req;
                }
            }
            return req;
        }

        /// <summary>Send a GET politely. The caller disposes the result, which
        /// may have failed (check result / responseCode). Throws when cancelled.</summary>
        public static async Task<UnityWebRequest> Send(string url, CancellationToken ct, int timeout = 20,
                                                       string tag = "web", Func<string, UnityWebRequest> make = null,
                                                       string accept = null, int retries = 3)
        {
            url = Standard(url);
            var h = HostOf(url);
            for (int attempt = 0; ; attempt++)
            {
                // The host asked everyone to wait.
                var wait = h.until - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

                await h.slots.WaitAsync(ct);
                UnityWebRequest req;
                try
                {
                    // Another request may have been told to wait meanwhile.
                    wait = h.until - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

                    req = make != null ? make(url) : UnityWebRequest.Get(url);
                    if (!string.IsNullOrEmpty(CommonsProvider.UserAgent))
                        req.SetRequestHeader("User-Agent", CommonsProvider.UserAgent);
                    if (accept != null) req.SetRequestHeader("Accept", accept);
                    req.timeout = timeout;
                    var op = req.SendWebRequest();
                    using (ct.Register(() => { try { req.Abort(); } catch { } }))
                        await op;
                }
                finally { h.slots.Release(); }

                if (ct.IsCancellationRequested) { req.Dispose(); ct.ThrowIfCancellationRequested(); }

                long code = req.responseCode;
                if (code != 429 && code != 503)
                {
                    if (req.result == UnityWebRequest.Result.Success) h.strikes = 0;
                    return req;
                }

                // Too many requests: pause the whole host, then try again.
                int.TryParse(req.GetResponseHeader("Retry-After") ?? "", out int ra);
                h.strikes = Math.Min(h.strikes + 1, 6);
                int secs = ra > 0 ? Math.Min(ra, 120) : Math.Min(60, 2 << h.strikes);   // 4, 8, 16, 32, 60
                var until = DateTime.UtcNow.AddSeconds(secs);
                if (until > h.until)
                {
                    h.until = until;
                    Debug.LogWarning($"[{tag}] {new Uri(url).Host} says too many requests ({code}); pausing it {secs}s");
                }
                if (attempt >= retries) return req;
                req.Dispose();
            }
        }
    }
}
