using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

public class CanvasToTexture : MonoBehaviour
{
    [SerializeField]
    private float scaling = 1.0f;

    [SerializeField]
    private float border = 0.0f;

    [SerializeField]
    private RawImage targetImage;

    [SerializeField]
    private TestDisplay targetDisplay;

    private RenderTexture _renderTexture;

    private Canvas _canvas;
    private RectTransform _rectTransform;
    private Camera _camera;

    private void OnEnable()
    {
        _canvas = GetComponent<Canvas>();
        _rectTransform = GetComponent<RectTransform>();
        CreateRenderTexture();

        _camera = GetComponent<Camera>();
        if(_camera ==  null)
        {
            _camera = gameObject.AddComponent<Camera>();
        }

        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = Color.clear;
        _camera.cullingMask = 1 << gameObject.layer;
        _camera.orthographic = true;
        _camera.orthographicSize = _rectTransform.rect.height * 0.5f + border;
        _camera.farClipPlane = 1000.0f;
        _camera.nearClipPlane = -1000.0f;
        _camera.depth = -100;

        _canvas.worldCamera = _camera;

        UpdateTargets();
    }

    private void OnDisable()
    {
        DestroyRenderTexture();
    }

    private void CreateRenderTexture()
    {
        int w = Mathf.RoundToInt((_rectTransform.rect.width + border * 2) / scaling);
        int h = Mathf.RoundToInt((_rectTransform.rect.height + border * 2) / scaling);

        _renderTexture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        _renderTexture.filterMode = FilterMode.Bilinear;
        _renderTexture.wrapMode = TextureWrapMode.Clamp;
        _renderTexture.Create();

        _renderTexture.name = gameObject.name + "'s RenderTexture";
    }

    private void DestroyRenderTexture()
    {
        if (_renderTexture != null)
        {
            _renderTexture.Release();
            _renderTexture = null;
        }

        UpdateTargets();
    }

    private void OnValidate()
    {
        if(Application.isPlaying)
        {
            if(_rectTransform == null)
            {
                _rectTransform = GetComponent<RectTransform>();
            }

            _camera = GetComponent<Camera>();
            if (_camera == null)
            {
                _camera = gameObject.AddComponent<Camera>();
            }

            if (_camera != null)
            {
                _camera.targetTexture = null;
                _camera.orthographicSize = _rectTransform.rect.height * 0.5f + border;
            }
            DestroyRenderTexture();
            CreateRenderTexture();

            UpdateTargets();
        }
    }

    private void UpdateTargets()
    {
        _camera.targetTexture = _renderTexture;
        if (targetImage != null)
        {
            targetImage.texture = _renderTexture;
        }

        if (targetDisplay != null)
        {
            targetDisplay.SetTestImage(_renderTexture);
        }
    }
}
