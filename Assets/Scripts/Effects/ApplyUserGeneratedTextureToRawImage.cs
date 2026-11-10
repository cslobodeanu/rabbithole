using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RawImage))]
public class ApplyUserGeneratedTextureToRawImage : MonoBehaviour
{
    [SerializeField]
    private UserGeneratedTexture generatedTexture;

    private RawImage _rawImage;

    private void OnEnable()
    {
        Apply();
    }

    private void Apply()
    {
        if(_rawImage == null)
        {
            _rawImage = GetComponent<RawImage>();
        }
        _rawImage.texture = generatedTexture.GetTexture();
    }

    private void Update()
    {
        if(_rawImage.texture != generatedTexture.GetTexture())
        {
            _rawImage.texture = generatedTexture.GetTexture();
        }
    }
}
