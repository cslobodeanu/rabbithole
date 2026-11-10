// The seam between "what the hands are doing" and "what that means".
//
// Everything above this line (GestureRecognizer, GridInteraction, KeepBoard)
// only ever sees HandState: a pointing ray, a pinch point and a pinch flag.
// That is exactly what a Quest hand gives you (Meta Interaction SDK's pointer
// pose + pinch, or XR Hands' aim pose + pinch), so moving to the headset means
// writing one more HandSource - nothing downstream changes.
//
// On PC, MouseHandSource fakes it. The fake is deliberately honest: the mouse
// drives a hand that sits at arm's length along the cursor ray, and the wheel
// moves that hand toward / away from you. Gestures are recognised from hand
// travel in metres, so thresholds tuned on PC carry over to the headset.

using UnityEngine;

namespace TimelineVR.Interaction
{
    public enum Hand { Left = 0, Right = 1 }

    public struct HandState
    {
        /// <summary>Hand is visible / mouse is present.</summary>
        public bool tracked;

        /// <summary>Index-thumb pinch (Quest) or left mouse button (PC).</summary>
        public bool pinching;

        /// <summary>Pointing ray. Quest: the hand's pointer pose. PC: camera through cursor.</summary>
        public Ray ray;

        /// <summary>Where the pinch is, in world space. Used for pull / push / two-hand spread.</summary>
        public Vector3 position;
    }

    public abstract class HandSource : MonoBehaviour
    {
        /// <summary>The viewer. Pull means "toward this".</summary>
        public abstract Transform head { get; }

        public abstract HandState GetHand(Hand hand);
    }
}
