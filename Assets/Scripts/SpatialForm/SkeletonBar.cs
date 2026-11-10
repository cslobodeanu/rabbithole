using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A grey placeholder block with a soft light sweeping across it - where a
/// picture or text is still loading. All bars sweep in step (same clock), so
/// a card full of them shimmers as one.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class SkeletonBar : MaskableGraphic
{
    public Color baseColor = new Color(0.16f, 0.18f, 0.22f, 1f);
    public Color shine = new Color(0.26f, 0.29f, 0.34f, 1f);
    [Tooltip("Seconds per sweep.")]
    public float period = 1.4f;
    [Tooltip("Width of the light band, as a fraction of the bar.")]
    public float band = 0.45f;

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        var r = GetPixelAdjustedRect();
        Color32 b = baseColor * color;
        Quad(vh, r.xMin, r.xMax, r.yMin, r.yMax, b, b);

        // The band runs from fully left of the bar to fully right of it.
        float t = Mathf.Repeat(Time.unscaledTime / Mathf.Max(0.1f, period), 1f);
        float bw = r.width * band;
        float centre = Mathf.Lerp(r.xMin - bw, r.xMax + bw, t);
        Color s = shine * color;
        Color clear = s; clear.a = 0f;
        Half(vh, r, centre - bw * 0.5f, centre, clear, s);
        Half(vh, r, centre, centre + bw * 0.5f, s, clear);
    }

    // One half of the band, clipped to the bar, colours interpolated at the cut.
    private static void Half(VertexHelper vh, Rect r, float x0, float x1, Color c0, Color c1)
    {
        float a = Mathf.Max(x0, r.xMin), b = Mathf.Min(x1, r.xMax);
        if (b <= a) return;
        float w = Mathf.Max(1e-4f, x1 - x0);
        Color ca = Color.Lerp(c0, c1, (a - x0) / w), cb = Color.Lerp(c0, c1, (b - x0) / w);
        Quad(vh, a, b, r.yMin, r.yMax, ca, cb);
    }

    private static void Quad(VertexHelper vh, float x0, float x1, float y0, float y1, Color32 left, Color32 right)
    {
        int i = vh.currentVertCount;
        vh.AddVert(new Vector3(x0, y0), left, Vector2.zero);
        vh.AddVert(new Vector3(x0, y1), left, Vector2.up);
        vh.AddVert(new Vector3(x1, y1), right, Vector2.one);
        vh.AddVert(new Vector3(x1, y0), right, Vector2.right);
        vh.AddTriangle(i, i + 1, i + 2);
        vh.AddTriangle(i + 2, i + 3, i);
    }

    private void Update()
    {
        if (canvasRenderer.cull) return;
        SetVerticesDirty();
    }
}
