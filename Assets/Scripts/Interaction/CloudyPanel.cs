// The precog look of a deduced card: drifting mist over it and a soft pale
// border, denser and faster while the model is still writing. A deduction
// must never look like a source.
//
// Two layers of tileable noise scroll in opposite directions; nothing blocks
// rays or clicks, so the card stays an ordinary panel underneath.

using UnityEngine;
using UnityEngine.UI;

namespace TimelineVR.Interaction
{
    [DisallowMultipleComponent]
    public class CloudyPanel : MonoBehaviour
    {
        /// <summary>True while the deduction is being written.</summary>
        public bool thinking = true;

        [SerializeField] private Color _mist = new Color(0.78f, 0.88f, 1f, 1f);
        [SerializeField] private Color _edge = new Color(0.62f, 0.85f, 1f, 1f);

        private RawImage _a, _b;
        private UIFrame _frame;
        private float _phase;
        private float _think;     // 0..1, eases between written and writing

        private static Texture2D _noise;

        private void Start()
        {
            if (!(transform is RectTransform)) { enabled = false; return; }
            _phase = Random.value * 10f;
            _a = Layer("Mist A");
            _b = Layer("Mist B");
            var go = new GameObject("Precog Edge", typeof(RectTransform), typeof(CanvasRenderer));
            go.layer = gameObject.layer;
            go.transform.SetParent(transform, false);
            Stretch((RectTransform)go.transform);
            _frame = go.AddComponent<UIFrame>();
            _frame.raycastTarget = false;
            _frame.thickness = 5f;
            _think = thinking ? 1f : 0f;
        }

        private RawImage Layer(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.layer = gameObject.layer;
            go.transform.SetParent(transform, false);
            Stretch((RectTransform)go.transform);
            var img = go.AddComponent<RawImage>();
            img.texture = Noise();
            img.raycastTarget = false;
            return img;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            rt.SetAsLastSibling();
        }

        private void Update()
        {
            if (_a == null) return;
            float dt = Time.deltaTime;
            _think = Mathf.MoveTowards(_think, thinking ? 1f : 0f, dt * 1.5f);
            float speed = Mathf.Lerp(0.015f, 0.06f, _think);
            _phase += dt;

            _a.uvRect = new Rect(_phase * speed, _phase * speed * 0.4f, 1.3f, 1.3f);
            _b.uvRect = new Rect(-_phase * speed * 0.7f, 0.3f - _phase * speed * 0.25f, 0.8f, 0.8f);

            float breathe = 0.5f + 0.5f * Mathf.Sin(_phase * Mathf.Lerp(0.8f, 2.2f, _think));
            float density = Mathf.Lerp(0.30f, 0.55f, _think) + 0.06f * breathe;
            _a.color = new Color(_mist.r, _mist.g, _mist.b, density);
            _b.color = new Color(_mist.r, _mist.g, _mist.b, density * 0.6f);
            _frame.color = new Color(_edge.r, _edge.g, _edge.b, 0.35f + 0.35f * breathe);
        }

        // Soft tileable fractal noise in the alpha channel.
        private static Texture2D Noise()
        {
            if (_noise != null) return _noise;
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, name = "PrecogMist" };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float v = 0f, amp = 0.55f, sum = 0f;
                for (int o = 0; o < 4; o++)
                {
                    float f = 3f * (1 << o) / n;
                    v += amp * Tile(x, y, n, f, o * 17.3f);
                    sum += amp;
                    amp *= 0.5f;
                }
                v /= sum;
                v = Mathf.SmoothStep(0.25f, 0.85f, v);
                px[y * n + x] = new Color32(255, 255, 255, (byte)(v * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return _noise = tex;
        }

        // Perlin noise made periodic over n pixels by blending four shifted copies.
        private static float Tile(float x, float y, int n, float f, float seed)
        {
            float P(float a, float b) => Mathf.PerlinNoise(seed + a * f, seed * 1.7f + b * f);
            float u = x / n, w = y / n;
            return P(x, y) * (1 - u) * (1 - w) + P(x - n, y) * u * (1 - w)
                 + P(x, y - n) * (1 - u) * w + P(x - n, y - n) * u * w;
        }
    }
}
