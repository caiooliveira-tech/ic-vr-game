using UnityEngine;

namespace VRSurgery.Interaction
{
    /// <summary>
    /// One working point that follows whichever hand is nearest the work.
    ///
    /// The bypass sites and the anastomoses are each driven by a worker that carries a single tip,
    /// but a stand has to serve left-handers and right-handers out of the same scene — the
    /// ergonomics rule the rest of the project follows is that any site is reachable with either
    /// hand. Giving each hand its own worker does not achieve that: a worker decays every site its
    /// own tip is not touching, so the idle hand would erase the working hand's progress exactly
    /// as fast as it was made, and both hands together would hold at zero forever.
    ///
    /// So there is one tip, and it goes to whichever hand is closest to something worth working.
    /// Distance to the work rather than to the patient, because a hand resting on the drapes is
    /// not the hand the surgeon is operating with.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class SurgeonHandTip : MonoBehaviour
    {
        [Tooltip("The surgeon's hands, in no particular order. Usually the two poke points of the rig.")]
        [SerializeField] private Transform[] hands = new Transform[0];

        [Tooltip("Every point a hand could be working: bypass sites, anastomoses, the sternum.")]
        [SerializeField] private Transform[] targets = new Transform[0];

        /// <summary>The hand the tip is currently standing in for, or null while none is known.</summary>
        public Transform ActiveHand { get; private set; }

        /// <summary>
        /// How far the active hand is from the nearest piece of work, in metres. Infinite until
        /// there is both a hand and a target to measure between.
        /// </summary>
        public float DistanceToWork { get; private set; } = float.PositiveInfinity;

        // Ahead of the workers in the frame, via DefaultExecutionOrder: they read this position in
        // their own Update, and a tip resolved afterwards would always be one frame behind the
        // hand it is meant to be.
        private void Update() => Follow();

        /// <summary>Moves the tip onto the nearest hand. Stepped by hand in tests.</summary>
        public void Follow()
        {
            Transform nearest = null;
            float best = float.PositiveInfinity;

            for (int h = 0; h < hands.Length; h++)
            {
                Transform hand = hands[h];
                if (hand == null) { continue; }

                for (int t = 0; t < targets.Length; t++)
                {
                    Transform target = targets[t];
                    if (target == null) { continue; }

                    float distance = Vector3.Distance(hand.position, target.position);
                    if (distance < best)
                    {
                        best = distance;
                        nearest = hand;
                    }
                }
            }

            // With no targets to judge against, any tracked hand is better than leaving the tip
            // parked at the origin, where it would sit inside nothing and read as "no hands".
            if (nearest == null)
            {
                for (int h = 0; h < hands.Length; h++)
                {
                    if (hands[h] != null) { nearest = hands[h]; break; }
                }
            }

            ActiveHand = nearest;
            DistanceToWork = best;

            if (nearest != null)
            {
                transform.position = nearest.position;
                transform.rotation = nearest.rotation;
            }
        }

        public void Bind(Transform[] handTransforms, Transform[] workPoints)
        {
            hands = handTransforms ?? new Transform[0];
            targets = workPoints ?? new Transform[0];
        }
    }
}
