using System;
using UnityEngine;
using VRSurgery.Interaction;

namespace VRSurgery.Transplant
{
    /// <summary>
    /// Opens the chest when the surgeon works the sternal midline.
    ///
    /// SternotomyController already knew how to open a sternum and TransplantProcedure already
    /// knew that opening it clears the first stage; nothing in the scene connected the two, so the
    /// operation dead-ended on its first step with the room telling the visitor to open a chest
    /// that could not be opened. This is that connection.
    ///
    /// Same terms as every other gesture in the procedure: the hand has to be on the site, and it
    /// has to stay there. Drifting off costs ground rather than losing it, because at a stand a
    /// tremor is the norm and punishing it punishes everyone.
    ///
    /// The site is a band the length of the sternum, not a 2cm point — a sternotomy runs from the
    /// manubrium to the xiphoid, and asking a first-timer to hold a fingertip inside a coin while
    /// the audience watches is a different skill than the one being taught.
    /// </summary>
    public class SternotomyWorker : MonoBehaviour
    {
        [Tooltip("The working hand. Usually the shared SurgeonHandTip.")]
        [SerializeField] private Transform tip;

        [Tooltip("Middle of the sternum. The gesture is judged against this point.")]
        [SerializeField] private Transform site;

        [Tooltip("How close the hand has to be, in metres. Sized from the sternum, not guessed.")]
        [SerializeField, Min(0.01f)] private float radius = 0.09f;

        [Tooltip("Seconds of steady contact to open the chest. A sternotomy is the longest single " +
                 "gesture in the operation; it is also the one the audience most clearly reads.")]
        [SerializeField, Min(0.1f)] private float seconds = 3f;

        [SerializeField] private SternotomyController sternotomy;
        [SerializeField] private TransplantProcedure procedure;

        private bool _announcedStart;
        private bool _requestedOpen;

        /// <summary>Seconds of contact banked so far.</summary>
        public float Held { get; private set; }

        /// <summary>0..1 toward an open chest, for the room to draw.</summary>
        public float Progress01 => seconds <= 0f ? 0f : Mathf.Clamp01(Held / seconds);

        /// <summary>True while the hand is on the site and the stage is still open to it.</summary>
        public bool IsWorking { get; private set; }

        /// <summary>
        /// First contact of the round. The booth starts its clock on this: in this scene there is
        /// no instrument to pick up, so the grab that starts the round everywhere else never
        /// happens, and cutting the sternum is the first thing the visitor actually does.
        /// </summary>
        public event Action Started;

        private void OnEnable()
        {
            if (sternotomy != null) { sternotomy.Opened += HandleOpened; }
        }

        private void OnDisable()
        {
            if (sternotomy != null) { sternotomy.Opened -= HandleOpened; }
        }

        private void Update() => Tick(Time.deltaTime);

        /// <summary>Advances the gesture. Stepped by hand in tests.</summary>
        public void Tick(float deltaTime)
        {
            IsWorking = false;

            if (deltaTime <= 0f || tip == null || site == null) { return; }

            // Only while the operation is actually asking for it. Without this the chest could be
            // opened during the scoreboard, or re-opened under a heart already sewn in.
            if (procedure != null && procedure.Stage != TransplantStage.OpenChest) { return; }
            if (sternotomy != null && (sternotomy.IsOpen || sternotomy.IsMoving)) { return; }

            if (Vector3.Distance(tip.position, site.position) > radius)
            {
                Held = Mathf.Max(0f, Held - deltaTime);
                return;
            }

            IsWorking = true;

            if (!_announcedStart)
            {
                _announcedStart = true;
                Started?.Invoke();
            }

            Held += deltaTime;
            if (Held < seconds || _requestedOpen) { return; }

            _requestedOpen = true;
            if (sternotomy != null) { sternotomy.Open(); }
        }

        /// <summary>
        /// The chest finished opening. The stage is cleared here rather than the moment the
        /// gesture completes, so the procedure does not move on while the sternum is still visibly
        /// swinging apart in front of the visitor.
        /// </summary>
        private void HandleOpened()
        {
            if (procedure != null)
            {
                procedure.CompleteStage(TransplantStage.OpenChest);
            }
        }

        /// <summary>Puts the gesture back in play for the next visitor.</summary>
        public void ResetGesture()
        {
            Held = 0f;
            IsWorking = false;
            _announcedStart = false;
            _requestedOpen = false;
        }

        public void Bind(Transform workingTip, Transform sternalSite, SternotomyController controller,
            TransplantProcedure transplant, float siteRadius, float holdSeconds)
        {
            if (sternotomy != null) { sternotomy.Opened -= HandleOpened; }

            tip = workingTip;
            site = sternalSite;
            sternotomy = controller;
            procedure = transplant;
            radius = Mathf.Max(0.01f, siteRadius);
            seconds = Mathf.Max(0.1f, holdSeconds);

            if (sternotomy != null && isActiveAndEnabled) { sternotomy.Opened += HandleOpened; }
        }
    }
}
