using UnityEngine;
using TMPro;

[RequireComponent(typeof(TMP_Text))]
[DefaultExecutionOrder(-100)] // Ensure this runs before other scripts that might modify the text
public class FontDefaults : MonoBehaviour
{
    private TMPro.TMP_Text _text;
    private float _fontSize;
    private Color _textColor;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Awake()
    {
        _text = GetComponent<TMP_Text>();
        SaveDefaults();
    }

    private void SaveDefaults()
    {
        _fontSize = _text.fontSize;
        _textColor = _text.color;
    }

    public void RestoreDefaults()
    {
        _text.fontSize = _fontSize;
        _text.color = _textColor;
    }
}
