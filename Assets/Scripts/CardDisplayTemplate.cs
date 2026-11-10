using SpatialForms;
using UnityEngine;

public abstract class CardDisplayTemplate : SFElementRender
{
    public XrProbe.Card source { get; set; }

    public abstract void SetImage(Texture2D texture);

    public abstract void SetImageSize(int width, int height);

    public abstract void SetImageColor(Color color);

    public abstract TextBlock header { get; }

    public abstract TextBlock footer { get; }

    public abstract TextBlock body { get; }

    public abstract TextBlock description { get; }

    public void ClearParagraphs()
    {
        header.ClearParagraphs();
        footer.ClearParagraphs();
        body.ClearParagraphs();
        description.ClearParagraphs();
    }

    public virtual void UpdateCardLayout()
    {
    }
}
