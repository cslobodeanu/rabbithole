// Small pictures for the wall, by the thousand.
//
// TextureCache keeps card-size textures for the session; a wall of 5000 of
// those would not fit in memory. Here every picture is asked for at a small
// standard width where the host allows it (Wikimedia), anything bigger is
// shrunk on the GPU, and nothing keeps a CPU copy. The bytes share the
// texture disk cache, so a picture is downloaded once.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace XrProbe
{
    public static class ThumbLoader
    {
        static readonly TimeSpan DiskAge = TimeSpan.FromDays(30);

        /// <summary>A texture at most maxSize on its long side, or null. The
        /// caller owns it (Destroy when done).</summary>
        public static async Task<Texture> Load(string url, int width, int maxSize, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            url = WebGate.AtWidth(url, width);
            string disk = TextureCache.DiskPath(url);

            Texture2D tex = null;
            try
            {
                if (File.Exists(disk) && DateTime.UtcNow - File.GetLastWriteTimeUtc(disk) < DiskAge)
                {
                    var bytes = await Task.Run(() => File.ReadAllBytes(disk), ct);
                    ct.ThrowIfCancellationRequested();
                    tex = ImageDecode.Decode(bytes, true);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { tex = null; }

            if (tex == null)
            {
                var req = await WebGate.GetTexture(url, ct, 20, "thumb", true);
                using (req)
                {
                    byte[] data;
                    if (req.result == UnityWebRequest.Result.DataProcessingError)
                    {
                        (tex, data) = await ImageDecode.FetchAndDecode(req.url, ct, 20, "thumb", true);   // WebP
                        if (tex == null) return null;
                    }
                    else
                    {
                        if (req.result != UnityWebRequest.Result.Success) return null;
                        try { tex = DownloadHandlerTexture.GetContent(req); } catch { return null; }
                        data = req.downloadHandler.data;
                    }
                    if (data != null && data.Length > 0)
                        _ = Task.Run(() => { try { File.WriteAllBytes(disk, data); } catch { } });
                }
            }
            if (tex == null) return null;

            int w = tex.width, h = tex.height;
            if (Mathf.Max(w, h) <= maxSize) return tex;

            // Too big (sources without sized thumbnails): shrink on the GPU.
            float k = (float)maxSize / Mathf.Max(w, h);
            var rt = new RenderTexture(Mathf.Max(1, Mathf.RoundToInt(w * k)), Mathf.Max(1, Mathf.RoundToInt(h * k)), 0,
                                       RenderTextureFormat.ARGB32) { name = tex.name, useMipMap = false };
            rt.Create();
            Graphics.Blit(tex, rt);
            UnityEngine.Object.Destroy(tex);
            return rt;
        }
    }
}
