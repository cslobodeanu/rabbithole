// Texture loading, independent of where the metadata came from.
//
// This was inside ProbeClient, which was wrong: images are fetched straight
// from the archive CDNs either way, so it has nothing to do with whether the
// rows came off the network or the local pipeline.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    public class TextureCache : MonoBehaviour
    {
        [SerializeField]
        private int timeout = 30;

        [SerializeField]
        private List<Texture2D> _textures = new List<Texture2D>();

        readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        static string ExtractNameFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                // Try filename from path
                var name = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrEmpty(name))
                    return Uri.UnescapeDataString(name);

                // Fallback to last non-empty segment
                var seg = uri.Segments.Reverse().FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                if (!string.IsNullOrEmpty(seg))
                    return Uri.UnescapeDataString(seg.Trim('/'));

                // Try query param "name" or "file"
                var q = uri.Query;
                if (!string.IsNullOrEmpty(q))
                {
                    var pairs = q.TrimStart('?').Split('&');
                    foreach (var p in pairs)
                    {
                        var kv = p.Split('=');
                        if (kv.Length == 2 && (kv[0].Equals("name", StringComparison.OrdinalIgnoreCase)
                                              || kv[0].Equals("file", StringComparison.OrdinalIgnoreCase)))
                            return Uri.UnescapeDataString(kv[1]);
                    }
                }
            }

            // Last resort: stable short hash of the URL
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant().Substring(0, 8);
            }
        }

        /// <summary>
        /// Reel mode returns 250 rows. Call this from whatever is actually
        /// visible - loading every thumbnail up front will exhaust Quest
        /// memory long before anyone scrolls that far.
        /// Requests go through WebGate (few at a time, back off on 429), the
        /// same URL asked twice is fetched once, and the bytes are kept on
        /// disk so a thumbnail is downloaded once across sessions.
        /// </summary>
        public Task<Texture2D> Load(string url)
        {
            if (string.IsNullOrEmpty(url)) return Task.FromResult<Texture2D>(null);
            url = WebGate.Standard(url);
            if (_cache.TryGetValue(url, out var hit) && hit) return Task.FromResult(hit);
            if (_pending.TryGetValue(url, out var running)) return running;
            var t = Fetch(url);
            _pending[url] = t;
            return t;
        }

        static bool _saidUndecodable;
        readonly Dictionary<string, Task<Texture2D>> _pending = new Dictionary<string, Task<Texture2D>>();

        static readonly TimeSpan DiskAge = TimeSpan.FromDays(30);
        static string _dir;
        internal static string DiskPath(string url)
        {
            if (_dir == null)
            {
                _dir = Path.Combine(Application.persistentDataPath, "texcache");
                try { Directory.CreateDirectory(_dir); } catch { }
            }
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                var sb = new StringBuilder(40);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return Path.Combine(_dir, sb.ToString());
            }
        }

        async Task<Texture2D> Fetch(string url)
        {
            try
            {
                Texture2D tex = null;
                string disk = DiskPath(url);
                try
                {
                    if (File.Exists(disk) && DateTime.UtcNow - File.GetLastWriteTimeUtc(disk) < DiskAge)
                    {
                        var bytes = await Task.Run(() => File.ReadAllBytes(disk));
                        tex = ImageDecode.Decode(bytes, false);
                    }
                }
                catch { tex = null; }

                if (tex == null)
                {
                    UnityWebRequest req;
                    try
                    {
                        req = await WebGate.GetTexture(url, CancellationToken.None, timeout, "texture", false);
                    }
                    catch (Exception e) { Debug.LogWarning($"[texture] {url}: {e.Message}"); return null; }
                    using (req)
                    {
                        byte[] data = null;
                        if (req.result == UnityWebRequest.Result.DataProcessingError)
                        {
                            // Not JPEG / PNG: WebP is decoded here when the decoder is installed.
                            (tex, data) = await ImageDecode.FetchAndDecode(req.url, CancellationToken.None, timeout, "texture", false);
                            if (tex == null)
                            {
                                if (!_saidUndecodable)
                                    Debug.Log("[texture] some pictures are in formats Unity cannot decode" +
                                              (ImageDecode.WebPAvailable ? " (GIF)" : " (WebP: add the com.netpyoung.webp package; GIF)") +
                                              $"; those cards show no picture. First: {url}");
                                _saidUndecodable = true;
                                return null;
                            }
                        }
                        else if (req.result != UnityWebRequest.Result.Success)
                        {
                            Debug.LogWarning($"[texture] {url}: {req.error}");
                            return null;
                        }
                        else
                        {
                            tex = DownloadHandlerTexture.GetContent(req);
                            data = req.downloadHandler.data;
                        }
                        if (data != null && data.Length > 0)
                            _ = Task.Run(() => { try { File.WriteAllBytes(disk, data); } catch { } });
                    }
                }

                // Give the texture a meaningful name derived from the URL so
                // debugging and profiling show helpful identifiers instead of
                // the default "Texture2D".
                try
                {
                    var name = ExtractNameFromUrl(url);
                    if (!string.IsNullOrEmpty(name)) tex.name = name;
                }
                catch { }

                if (this == null) { Destroy(tex); return null; }
                _cache[url] = tex;
                _textures.Add(tex);
                return tex;
            }
            finally { _pending.Remove(url); }
        }

        public void Clear()
        {
            foreach (var t in _cache.Values)
                if (t) UnityEngine.Object.Destroy(t);
            _cache.Clear();
        }

        public void OnDestroy()
        {
            Clear();
        }
    }
}
