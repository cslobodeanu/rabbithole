using Unity.VisualScripting;
using UnityEngine;

public class TestDisplay : MonoBehaviour
{
    [SerializeField]
    private Vector2 size = new Vector2(4, 3);

    [SerializeField]
    private ParticleSystem particleSystem;

    [SerializeField]
    private Texture testImage;

    [SerializeField]
    [Range(0f, 1f)]
    private float fade = 0.5f;

    private Renderer _particleSystemRenderer;

    private void Start()
    {
        ApplyChanges();
    }

    private void OnValidate()
    {
        if (Application.isPlaying == false || particleSystem == null)
        {
            return;
        }

        ApplyChanges();
    }

    private void ApplyChanges()
    { 
        if(testImage != null)
        {
            size.y = size.x * testImage.height / testImage.width;
        }
        if(particleSystem == null)
        {
            return;
        }
        ParticleSystem.MainModule main = particleSystem.main;
        main.startSize3D = true;
        float vertMultiplier = 2.0f;
        main.startSizeY = new ParticleSystem.MinMaxCurve(size.y * 1.1f, size.y * 0.9f * vertMultiplier);

        ParticleSystem.ShapeModule shape = particleSystem.shape;
        Vector3 shapeScale = shape.scale;
        shapeScale.x = size.x;
        shape.scale = shapeScale;

        Material mat = particleSystem.GetComponent<Renderer>().material;
        mat.SetTexture("_SubjectTex", testImage);
        mat.SetVector("_SubjectSize", new Vector4(size.x, size.y, 0, 0));
        mat.SetFloat("_SubjectFade", fade);
    }

    public void SetTestImage(Texture image)
    {
        testImage = image;
        ApplyChanges();
    }

    private void Update()
    {
        if(_particleSystemRenderer == null)
        {
            _particleSystemRenderer = particleSystem.GetComponent<Renderer>();
        }
        Material mat = _particleSystemRenderer.material;
        mat.SetMatrix("_WorldToObject", particleSystem.transform.worldToLocalMatrix);
    }
}
