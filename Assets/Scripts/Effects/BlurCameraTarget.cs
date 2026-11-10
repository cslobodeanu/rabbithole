using Unity.VisualScripting;
using UnityEngine;

public class BlurCameraTarget : MonoBehaviour
{
    [SerializeField]
    private Shader _blurShader;

    [SerializeField]
    private float _blurSize = 1.0f;

    [SerializeField]
    private int _gridSize = 4;

    private Material _material;

    private RenderTexture _tempTexture;


    private void Awake()
    {
        if (_blurShader == null)
        {
            Debug.LogError("Blur shader is not assigned.");
            return;
        }
        _material = new Material(_blurShader);

        UpdateMaterial();
    }

    private void OnDisable()
    {
        ReleaseTempTexture();
    }

    private void ReleaseTempTexture()
    {
        if (_tempTexture != null)
        {
            RenderTexture.ReleaseTemporary(_tempTexture);
            _tempTexture = null;
        }
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (_blurSize == 0.0f)
        {
            ReleaseTempTexture();
            Graphics.Blit(source, destination);
        }
        else
        {
            if (_tempTexture == null || _tempTexture.width != source.width || _tempTexture.height != source.height)
            {
                ReleaseTempTexture();
                _tempTexture = RenderTexture.GetTemporary(source.width, source.height, 0);
            }

            Graphics.Blit(source, _tempTexture, _material, 0);
            Graphics.Blit(_tempTexture, destination, _material, 1);
        }
    }

    /// <summary>Blur strength (gaussian sigma, in texels). Can be animated:
    /// the kernel grows with it, with Grid Size as the minimum.</summary>
    public float blurSize
    {
        get => _blurSize;
        set
        {
            _blurSize = Mathf.Max(0.0f, value);
            UpdateMaterial();
        }
    }

    private void UpdateMaterial()
    {
        if (_material != null)
        {
            // ~2.5 sigma each side covers the visible part of the gaussian.
            int auto = 2 * Mathf.CeilToInt(_blurSize * 2.5f) + 1;
            int grid = Mathf.Clamp(Mathf.Max(_gridSize, auto), 1, 61);
            _material.SetFloat("_Spread", _blurSize);
            _material.SetInteger("_GridSize", grid);
        }
    }

    private void OnValidate()
    {
        if(Application.isPlaying)
        {
            _gridSize = Mathf.Max(1, _gridSize);
            _blurSize = Mathf.Max(0.0f, _blurSize);
            UpdateMaterial();
        }
    }
}
