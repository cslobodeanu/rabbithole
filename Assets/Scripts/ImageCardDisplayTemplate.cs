using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(-2999)]
public class ImageCardDisplayTemplate : CardDisplayTemplate
{
    [SerializeField]
    private UnityEngine.UI.RawImage image;

    [SerializeField]
    private TextBlock _header;
    public override TextBlock header => _header;

    [SerializeField]
    private TextBlock _footer;
    public override TextBlock footer => _footer;

    [SerializeField]
    private TextBlock _body;
    public override TextBlock body => _body;

    [SerializeField]
    private TextBlock _description;
    public override TextBlock description => _description;

    [SerializeField]
    private GameObject _imageAndDescriptionBlock;

    private float _initialImageHeight;

    private void Awake()
    {
        _initialImageHeight = image.rectTransform.sizeDelta.y;
        ClearParagraphs(); 
    }

    public override void SetImage(Texture2D texture)
    {
        image.texture = texture;

        if (texture == null)
        {
            image.gameObject.SetActive(false);
        }
        else
        {
            image.gameObject.SetActive(true);
            image.color = Color.white;

            SetImageSize(texture.width, texture.height);
        }
    }

    public override void SetImageSize(int width, int height)
    {
        image.gameObject.SetActive(true);

        Vector3[] corners = new Vector3[4];
        RectTransform rt = GetComponent<RectTransform>();
        rt.GetLocalCorners(corners);
        float maxWidth = Mathf.Abs(corners[2].x - corners[0].x) * 0.65f;

        float aspect = (float)Mathf.Max(1, width) / Mathf.Max(1, height);

        Vector2 sz = image.rectTransform.sizeDelta;
        if (_initialImageHeight * aspect > maxWidth)
        {
            sz.Set(maxWidth, maxWidth / aspect);
        }
        else
        {
            sz.Set(_initialImageHeight * aspect, _initialImageHeight);
        }

        image.rectTransform.sizeDelta = sz;
    }

    public override void SetImageColor(Color color)
    {
        image.color = color;
    }

    public override void UpdateCardLayout()
    {
        if (description.IsEmpty() && image.gameObject.activeSelf == false)
        {
            _imageAndDescriptionBlock.SetActive(false);

        }
        else
        {
            _imageAndDescriptionBlock.SetActive(true);
        }
    }
}
