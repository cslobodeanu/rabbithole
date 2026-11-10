using UnityEngine;
using TMPro;

[DefaultExecutionOrder(-3000)]
public class TextBlock : MonoBehaviour
{
    [SerializeField]
    private bool autoGrow = false;

    private TMP_Text[] _texts;
    private int _currentTextIndex = 0;

    private void Awake()
    {
        if (_texts == null || _texts.Length == 0)
        {
            _texts = gameObject.GetComponentsInChildren<TMP_Text>(true);
        }
        Clear();
    }

    private void Clear()
    {
        foreach (var text in _texts)
        {
            text.text = string.Empty;
            text.gameObject.SetActive(false);
        }

        _currentTextIndex = 0;
    }

    public void AddParagraph(string text)
    {
        if (_currentTextIndex < _texts.Length)
        {
            _texts[_currentTextIndex].gameObject.SetActive(true);
            _texts[_currentTextIndex].text = text;
            _currentTextIndex++;
        }
    }

    public void ClearParagraphs()
    {
        Clear();
    }

    public bool IsEmpty()
    {
        return _currentTextIndex == 0;
    }
}
