using UnityEngine;
using UnityEngine.UI;

/// <summary>A play badge drawn as shapes (a dark disc with a triangle), so it
/// does not depend on the font having a ▶ glyph.</summary>
[RequireComponent(typeof(CanvasRenderer))]
public class PlayIcon : MaskableGraphic
{
    public Color disc = new Color(0f, 0f, 0f, 0.55f);
    public Color triangle = new Color(1f, 1f, 1f, 0.92f);
    [Range(8, 64)] public int segments = 40;

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        var r = GetPixelAdjustedRect();
        Vector2 c = r.center;
        float rad = Mathf.Min(r.width, r.height) * 0.5f;

        // disc
        Color32 d = disc * color;
        vh.AddVert(c, d, Vector2.zero);
        for (int i = 0; i <= segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            vh.AddVert(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad, d, Vector2.zero);
            if (i > 0) vh.AddTriangle(0, i, i + 1);
        }

        // triangle, nudged right so it looks centred
        Color32 t = triangle * color;
        float s = rad * 0.5f;
        int k = vh.currentVertCount;
        vh.AddVert(c + new Vector2(-s * 0.7f, s), t, Vector2.zero);
        vh.AddVert(c + new Vector2(s * 1.05f, 0f), t, Vector2.zero);
        vh.AddVert(c + new Vector2(-s * 0.7f, -s), t, Vector2.zero);
        vh.AddTriangle(k, k + 1, k + 2);
    }
}
