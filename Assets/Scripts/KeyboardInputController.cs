using UnityEngine;
using UnityEngine.UI;

public class KeyboardInputController : BaseInputController
{
    [SerializeField]
    private float _animationDuration = 0.5f;

    private Vector3 _targetPos;
    private int _currentTween;

    private void Update()
    {
        Vector3 dir = Vector3.zero;
        if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            dir = Vector3.left;
        }

        if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            dir = Vector3.right;
        }

        if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            dir = Vector3.up;
        }

        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            dir = Vector3.down;
        }

        if(dir.sqrMagnitude > 0)
        {
            Vector3 nextPos = GetNextPos(dir, _currentTween >= 0);

            if (_currentTween >= 0)
            {
                LeanTween.cancel(_currentTween);
            }

            _currentTween = LeanTween.value(gameObject, container.localPosition, nextPos, _animationDuration).setEase(LeanTweenType.easeOutBack).setOnUpdateVector3((Vector3 v) =>
            {
                container.localPosition = v;
            }).setOnComplete(() => {
                _currentTween = -1;
            }
            ).id;
        }
    }

    private Vector3 GetNextPos(Vector3 dir, bool animating)
    {
        float offset = -100.0f;
        if (animating)
        {
            _targetPos += dir * offset;
        }
        else
        {
            _targetPos = container.localPosition + dir * offset;
        }
        return _targetPos;
    }

}
