// Bytes -> texture, for the formats Unity's own decoder does not handle.
//
// Unity decodes JPEG and PNG. WebP needs the unity.webp package
// (com.netpyoung.webp, via OpenUPM); it is found at run time, so the project
// builds and runs without it - WebP pictures are just skipped then.

using System;
using System.Reflection;
using UnityEngine;

namespace XrProbe
{
    public static class ImageDecode
    {
        static bool _looked;
        static MethodInfo _webp;

        /// <summary>The WebP decoder is installed.</summary>
        public static bool WebPAvailable
        {
            get
            {
                if (!_looked)
                {
                    _looked = true;
                    var t = Type.GetType("WebP.Texture2DExt, unity.webp");
                    if (t == null)
                        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                            if ((t = a.GetType("WebP.Texture2DExt")) != null) break;
                    _webp = t?.GetMethod("CreateTexture2DFromWebP", BindingFlags.Public | BindingFlags.Static);
                }
                return _webp != null;
            }
        }

        /// <summary>"RIFF....WEBP"</summary>
        public static bool IsWebP(byte[] d) =>
            d != null && d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F'
                      && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P';

        /// <summary>A texture from image bytes (JPEG, PNG, and WebP when the
        /// decoder is installed), or null.</summary>
        public static Texture2D Decode(byte[] data, bool nonReadable)
        {
            if (data == null || data.Length == 0) return null;
            if (IsWebP(data)) return DecodeWebP(data, nonReadable);
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (t.LoadImage(data, nonReadable)) return t;
            UnityEngine.Object.Destroy(t);
            return null;
        }

        /// <summary>For a picture Unity's texture download could not decode:
        /// the same URL as plain bytes, decoded here (WebP). (null, null) when
        /// it cannot be.</summary>
        public static async System.Threading.Tasks.Task<(Texture2D tex, byte[] data)> FetchAndDecode(
            string url, System.Threading.CancellationToken ct, int timeout, string tag, bool nonReadable)
        {
            if (!WebPAvailable || string.IsNullOrEmpty(url)) return (null, null);
            using (var req = await WebGate.Send(url, ct, timeout, tag))
            {
                if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success) return (null, null);
                var d = req.downloadHandler.data;
                var tex = Decode(d, nonReadable);
                return tex != null ? (tex, d) : (null, null);
            }
        }

        static Texture2D DecodeWebP(byte[] data, bool nonReadable)
        {
            if (!WebPAvailable) return null;
            try
            {
                // (byte[] data, bool mipmaps, bool linear, out Error error, ScalingFunction scaling, bool makeNoLongerReadable)
                var args = new object[] { data, false, false, null, null, nonReadable };
                var tex = _webp.Invoke(null, args) as Texture2D;
                if (args[3] != null && args[3].ToString() != "Success")
                {
                    if (tex != null) UnityEngine.Object.Destroy(tex);
                    return null;
                }
                return tex;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[webp] {e.InnerException?.Message ?? e.Message}");
                return null;
            }
        }
    }
}
