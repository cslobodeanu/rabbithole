using System;
using UnityEngine;

[RequireComponent(typeof(Camera))]
public class CameraTargetTexture : UserGeneratedTexture
{
    [SerializeField]
    private int width = 1024;

    [SerializeField]
    private int height = 768;

    [SerializeField]
    private int depthBits = 16;

    [Tooltip("Size the texture to the window (width / height ignored). The picture is " +
             "shown full-window, so Unity UI input (EventSystem, ScrollRect) then lines up exactly.")]
    [SerializeField]
    private bool matchScreen = true;

    private RenderTexture _renderTexture;

    private Camera _camera;


    private void OnEnable()
    {
        _camera = GetComponent<Camera>();
        CreateTexture();
    }

    private void OnDisable()
    {
        DestroyTexture();
    }

    private void CreateTexture()
    {
        if (_camera == null)
        {
            return;
        }

        int w = matchScreen ? Mathf.Max(16, Screen.width) : width;
        int h = matchScreen ? Mathf.Max(16, Screen.height) : height;
        _renderTexture = new RenderTexture(w, h, depthBits, RenderTextureFormat.ARGB32);
        _renderTexture.name = name + "'s RenderTexture";
        _renderTexture.wrapMode = TextureWrapMode.Clamp;
        _renderTexture.filterMode = FilterMode.Bilinear;

        _renderTexture.Create();

        _camera.targetTexture = _renderTexture;
    }

    private void DestroyTexture()
    {
        if (_renderTexture)
        {
            if (_camera)
            {
                _camera.targetTexture = null;
            }
            Destroy(_renderTexture);
            _renderTexture = null;
        }
    }

    private void Update()
    {
        // Follow the window.
        if (matchScreen && _renderTexture != null &&
            (_renderTexture.width != Screen.width || _renderTexture.height != Screen.height) &&
            Screen.width >= 16 && Screen.height >= 16)
        {
            DestroyTexture();
            CreateTexture();
        }
    }

    private void OnValidate()
    {
        if(Application.isPlaying)
        {
            DestroyTexture();
            CreateTexture();
        }
    }

    public override Texture GetTexture()
    {
        return _renderTexture;
    }
}
