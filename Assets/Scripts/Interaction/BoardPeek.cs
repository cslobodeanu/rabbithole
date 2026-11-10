// Hold Space to bring the keep board to the front; let go and it returns to
// wherever its layer rests (the dock, by default). Tapping its slot in the
// dock also brings it back - it then stays until the next Space press.
//
// All the placement, blur and input routing that used to live here is now
// LayerManager's; this is only the rule "this key shows that layer". On Quest
// the key becomes a gesture (e.g. turning the left palm toward you).

using UnityEngine;

namespace TimelineVR.Interaction
{
    [DefaultExecutionOrder(25)]
    public class BoardPeek : MonoBehaviour
    {
        [SerializeField] private KeyCode _key = KeyCode.Space;

        [Tooltip("Press once to show, again to hide, instead of holding.")]
        [SerializeField] private bool _toggle = false;

        [Tooltip("Which layer the key shows.")]
        [SerializeField] private string _layer = "board";

        private LayerManager _manager;

        private void Awake()
        {
            // Zero-config: bring a manager along if the scene has none.
            _manager = FindFirstObjectByType<LayerManager>();
            if (_manager == null) _manager = gameObject.AddComponent<LayerManager>();
        }

        protected virtual bool Pressed() => Input.GetKeyDown(_key) && !SearchBox.typing;
        protected virtual bool Released() => Input.GetKeyUp(_key);

        private void Update()
        {
            var layer = _manager != null ? _manager.Get(_layer) : null;
            if (layer == null) return;

            if (_toggle)
            {
                if (Pressed()) layer.Toggle();
                return;
            }
            if (Pressed()) layer.Show();
            if (Released()) layer.Hide();
        }
    }
}
