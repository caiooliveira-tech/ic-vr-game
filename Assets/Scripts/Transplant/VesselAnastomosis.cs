using System;
using UnityEngine;
using VRSurgery.Surgery;

namespace VRSurgery.Transplant
{
    /// <summary>
    /// Which vessel a connection point stands for. Named because the audience screen and the
    /// surgeon's prompt both say them out loud, and because the order they are sewn in is not
    /// arbitrary — the left atrium goes first, deep and hardest to reach, and the great arteries
    /// last.
    /// </summary>
    public enum VesselSite
    {
        LeftAtrium,
        InferiorVenaCava,
        SuperiorVenaCava,
        Aorta,
        PulmonaryArtery,
    }

    /// <summary>
    /// One anastomosis: a place on the implanted heart that has to be joined to the recipient.
    ///
    /// Held, not tapped. The instrument has to stay inside the site for a moment, the same way
    /// the gauze has to stay on a bleeding port, because a connection that completes the instant
    /// something brushes past it turns five careful joins into five accidents. Progress is exposed
    /// so the projection can show a vessel filling rather than blinking.
    ///
    /// A hold that wanders too much while it is happening is not a careful suture, it is a hand
    /// that will not sit still — and in this game that is what an imprecise anastomosis looks
    /// like. Held steady for the whole time: it closes clean. Held for long enough but shaky for
    /// too much of it: it finishes joined but leaking, and the same "encostar e segurar" the join
    /// itself used is what stops the bleeding, this time held against the leak instead of the
    /// vessel wall.
    ///
    /// It knows nothing about hands or XR. Something else tells it what is touching it, which is
    /// what lets a test drive it with no rig in the scene.
    /// </summary>
    public class VesselAnastomosis : MonoBehaviour
    {
        [SerializeField] private VesselSite site = VesselSite.LeftAtrium;

        [Tooltip("How close the instrument has to be to count as working on this join, in metres.")]
        [SerializeField, Min(0.005f)] private float radius = 0.025f;

        [Tooltip("Seconds of steady contact to complete the join.")]
        [SerializeField, Min(0.1f)] private float secondsToJoin = 1.4f;

        [Header("Precision")]
        [Tooltip("Tip speed, in metres/second, still read as a calm hand. A visitor's natural " +
                 "tremor sits well under this; a hand actively working the site does not.")]
        [SerializeField, Min(0.01f)] private float maxSteadySpeed = 0.12f;

        [Tooltip("Fraction of the hold that is allowed to be unsteady before the join is judged " +
                 "imprecise rather than clean. Some wobble is normal at a stand; a shaky hand for " +
                 "most of the hold is a bad suture, not a tremor.")]
        [SerializeField, Range(0f, 1f)] private float allowedUnstableFraction = 0.35f;

        [Tooltip("Seconds of steady pressure needed to stop the bleeding from an imprecise join.")]
        [SerializeField, Min(0.1f)] private float secondsToControlBleeding = 1.5f;

        [SerializeField] private TransplantProcedure procedure;

        public VesselSite Site => site;
        public float Radius => radius;

        /// <summary>0 until started, 1 when joined. What the audience's readout should follow.</summary>
        public float Progress01 { get; private set; }

        public bool IsJoined { get; private set; }

        /// <summary>True while a badly-held join is leaking and waiting for pressure.</summary>
        public bool IsBleeding { get; private set; }

        /// <summary>0..1 progress toward stopping a leak. 0 whenever the site is not bleeding.</summary>
        public float BleedingControl01 { get; private set; }

        /// <summary>True once resolved if the join that produced it was imprecise, false if it closed clean.</summary>
        public bool BledDuringJoin { get; private set; }

        /// <summary>Raised the moment an imprecise join starts leaking.</summary>
        public event Action<VesselAnastomosis> BleedingStarted;

        /// <summary>Raised once the site is fully resolved: joined, whether or not it bled first.</summary>
        public event Action<VesselAnastomosis> Joined;

        private float _held;
        private float _unstableHeld;
        private float _pressureHeld;

        private Vector3 _lastPoint;
        private bool _lastPointValid;

        /// <summary>
        /// Feeds the join. <paramref name="worldPoint"/> is wherever the instrument tip is; pass
        /// nothing in a frame and the join pauses rather than resets.
        /// </summary>
        public void Work(Vector3 worldPoint, float deltaTime)
        {
            if (IsJoined || deltaTime <= 0f)
            {
                return;
            }

            bool inside = Vector3.Distance(worldPoint, transform.position) <= radius;

            if (IsBleeding)
            {
                WorkBleeding(inside, deltaTime);
                return;
            }

            if (!inside)
            {
                // Drifting off the site loses the moment's work but not the join. Resetting to
                // zero would punish a visitor whose hand wobbles, which at a stand is everyone.
                _held = Mathf.Max(0f, _held - deltaTime);
                Progress01 = Mathf.Clamp01(_held / secondsToJoin);
                _lastPointValid = false;
                return;
            }

            float speed = _lastPointValid ? Vector3.Distance(worldPoint, _lastPoint) / deltaTime : 0f;
            _lastPoint = worldPoint;
            _lastPointValid = true;

            if (speed > maxSteadySpeed)
            {
                _unstableHeld += deltaTime;
            }

            _held += deltaTime;
            Progress01 = Mathf.Clamp01(_held / secondsToJoin);

            if (_held >= secondsToJoin)
            {
                float unstableFraction = secondsToJoin > 0f ? _unstableHeld / secondsToJoin : 0f;

                if (unstableFraction > allowedUnstableFraction)
                {
                    BeginBleeding();
                }
                else
                {
                    Complete(bled: false);
                }
            }
        }

        /// <summary>Pressure against a leaking join. The same "hold at the site" input that joins a clean vessel, applied against the leak instead.</summary>
        private void WorkBleeding(bool inside, float deltaTime)
        {
            if (!inside)
            {
                _pressureHeld = Mathf.Max(0f, _pressureHeld - deltaTime);
                BleedingControl01 = Mathf.Clamp01(_pressureHeld / secondsToControlBleeding);
                return;
            }

            _pressureHeld += deltaTime;
            BleedingControl01 = Mathf.Clamp01(_pressureHeld / secondsToControlBleeding);

            if (_pressureHeld >= secondsToControlBleeding)
            {
                IsBleeding = false;
                SurgeryEvents.RaiseBleedingStopped();
                Complete(bled: true);
            }
        }

        private void BeginBleeding()
        {
            IsBleeding = true;
            _pressureHeld = 0f;
            BleedingControl01 = 0f;

            SurgeryEvents.RaiseError(ErrorSeverity.MinorError,
                $"Anastomose da {DisplayName} malfeita: ponto sangrando.");
            SurgeryEvents.RaiseBleedingStarted();

            BleedingStarted?.Invoke(this);
        }

        private void Complete(bool bled)
        {
            IsJoined = true;
            Progress01 = 1f;
            BledDuringJoin = bled;
            Joined?.Invoke(this);

            if (procedure != null)
            {
                procedure.ConnectVessel();
            }
        }

        public void ResetJoin()
        {
            IsJoined = false;
            IsBleeding = false;
            BledDuringJoin = false;
            Progress01 = 0f;
            BleedingControl01 = 0f;
            _held = 0f;
            _unstableHeld = 0f;
            _pressureHeld = 0f;
            _lastPointValid = false;
        }

        public void Bind(VesselSite vessel, TransplantProcedure transplant, float siteRadius)
        {
            site = vessel;
            procedure = transplant;
            radius = Mathf.Max(0.005f, siteRadius);
        }

        /// <summary>Name for the surgeon's prompt and the audience screen, in pt-BR.</summary>
        public string DisplayName => site switch
        {
            VesselSite.LeftAtrium => "átrio esquerdo",
            VesselSite.InferiorVenaCava => "veia cava inferior",
            VesselSite.SuperiorVenaCava => "veia cava superior",
            VesselSite.Aorta => "aorta",
            VesselSite.PulmonaryArtery => "artéria pulmonar",
            _ => site.ToString(),
        };
    }
}
