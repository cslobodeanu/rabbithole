// A layer: one group of panels that moves, fades and takes input as a unit -
// the timeline, the keep board, the detail sheet, and later deductions,
// search, video...
//
// Where a layer sits is a blend of three named poses (see LayerPose):
//
//   shown    - it is up (presence 1) and nothing covers it
//   covered  - it is up, but a layer above it is in front
//   away     - it is dismissed (presence 0): hidden, minimised to the dock...
//
//   pose = blend(away, blend(shown, covered, coverage), presence)
//
// presence is 0..1 - animated by Show()/Hide(), or read every frame from a
// source (the detail sheet follows the hands, so it supplies its own).
// coverage is how much the layers above it are present.
//
// Poses are canvas-relative for now (offsets as fractions of the canvas,
// depth in canvas units). When the canvases go world-space for Quest they
// become head-relative and the same rules place layers in real 3D - on
// Quest each layer's texture becomes a compositor layer at its pose.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace TimelineVR.Interaction
{
    /// <summary>What a layer's content does when it gains or loses input focus.</summary>
    public interface ILayerContent
    {
        void OnLayerFocus(bool focused);
    }

    [Serializable]
    public class LayerPose
    {
        public string name = "Front";

        [Tooltip("Placed and sized by the Dock; offset / depth / scale are ignored.")]
        public bool docked;

        [Tooltip("Shift of the layer's centre, as a fraction of its canvas (x right, y up).")]
        public Vector2 offset;

        [Tooltip("Depth in canvas units; + is away from the viewer.")]
        public float depth;

        [Tooltip("Euler angles. x tilts (pitch), y turns (yaw).")]
        public Vector3 rotation;

        public float scale = 1f;

        [Range(0f, 1f)] public float alpha = 1f;

        [Tooltip("Blur strength. Only layers drawn by a blurring camera (the timeline) can show it.")]
        public float blur;

        public LayerPose() { }

        public LayerPose(string name, float depth = 0f, float scale = 1f, float alpha = 1f, float blur = 0f,
                         Vector2 offset = default, Vector3 rotation = default, bool docked = false)
        {
            this.name = name; this.depth = depth; this.scale = scale; this.alpha = alpha;
            this.blur = blur; this.offset = offset; this.rotation = rotation; this.docked = docked;
        }
    }

    [Serializable]
    public class LayerSettings
    {
        public string id;

        [Tooltip("Higher is in front.")]
        public int order;

        public string shown = "Front";
        public string covered = "Back";
        public string away = "Hidden";

        [Tooltip("Whether this layer pushes the layers below it into their covered pose.")]
        public bool coversOthers = true;

        public LayerSettings() { }

        public LayerSettings(string id, int order, string shown, string covered, string away, bool coversOthers = true)
        {
            this.id = id; this.order = order; this.shown = shown; this.covered = covered;
            this.away = away; this.coversOthers = coversOthers;
        }
    }

    /// <summary>A pose resolved to numbers in the layer's parent space.</summary>
    public struct ResolvedPose
    {
        public Vector3 center;
        public Quaternion rotation;
        public float scale, alpha, blur;

        public static ResolvedPose Lerp(ResolvedPose a, ResolvedPose b, float t) => new ResolvedPose
        {
            center = Vector3.LerpUnclamped(a.center, b.center, t),
            rotation = Quaternion.SlerpUnclamped(a.rotation, b.rotation, t),
            scale = Mathf.LerpUnclamped(a.scale, b.scale, t),
            alpha = Mathf.LerpUnclamped(a.alpha, b.alpha, t),
            blur = Mathf.LerpUnclamped(a.blur, b.blur, t),
        };
    }

    public class Layer
    {
        public readonly string id;
        public readonly RectTransform root;
        public readonly CanvasGroup group;
        public LayerSettings settings;
        public ILayerContent content;

        /// <summary>If set, presence is read from here every frame instead of animated.</summary>
        public Func<float> presenceSource;

        /// <summary>Receives the pose's blur (the timeline routes it to its camera's blur).</summary>
        public Action<float> blurSink;

        public float presence { get; internal set; }
        public float presenceTarget { get; private set; }
        public bool focused { get; internal set; }

        /// <summary>Presence as used for blending: eased when animated, as-is when sourced.</summary>
        public float weight => presenceSource != null ? presence : presence * presence * (3f - 2f * presence);

        internal Vector3 basePosition, baseScale;
        internal ResolvedPose current;
        internal bool hasCurrent;
        internal float dockWeight;

        public Layer(string id, RectTransform root, LayerSettings settings)
        {
            this.id = id;
            this.root = root;
            this.settings = settings;
            group = PanelUtil.Group(root);
            basePosition = root.localPosition;
            baseScale = root.localScale;
        }

        public void Show() => presenceTarget = 1f;
        public void Hide() => presenceTarget = 0f;
        public void Toggle() => presenceTarget = presenceTarget > 0.5f ? 0f : 1f;

        /// <summary>Set presence at once, no animation (initial state).</summary>
        public void Snap(float p) { presenceTarget = p; presence = p; }

        /// <summary>The layer's own centre at its base pose, in parent space.</summary>
        internal Vector3 BaseCenter => basePosition + Vector3.Scale((Vector3)root.rect.center, baseScale);
    }
}
