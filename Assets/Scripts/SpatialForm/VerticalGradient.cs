using UnityEngine;
using UnityEngine.UI;

/// <summary>A rect filled with a vertical gradient (scrims under text on pictures).</summary>
[RequireComponent(typeof(CanvasRenderer))]
public class VerticalGradient : MaskableGraphic
{
    public Color top = new Color(0f, 0f, 0f, 0f);
    public Color bottom = new Color(0f, 0f, 0f, 0.85f);

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        var r = GetPixelAdjustedRect();
        Color32 t = top * color, b = bottom * color;
        vh.AddVert(new Vector3(r.xMin, r.yMin), b, Vector2.zero);
        vh.AddVert(new Vector3(r.xMin, r.yMax), t, Vector2.up);
        vh.AddVert(new Vector3(r.xMax, r.yMax), t, Vector2.one);
        vh.AddVert(new Vector3(r.xMax, r.yMin), b, Vector2.right);
        vh.AddTriangle(0, 1, 2);
        vh.AddTriangle(2, 3, 0);
    }
}
