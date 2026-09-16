using UnityEngine;
using VRSurgery.Session;
using VRSurgery.Surgery;

namespace VRSurgery.Transplant
{
    /// <summary>
    /// Joins the booth's round to the transplant, and keeps the queue moving.
    ///
    /// Neither side knows about the other by design: the session owns the clock, the states and
    /// the scoreboard, and the procedure owns what "transplanted" means. This is the only place
    /// that says finishing the operation wins the round, that a new visitor gets a closed chest
    /// and a sick heart back in it, and that the stand starts the next turn by itself.
    ///
    /// That last part matters more here than it sounds. Without it the session sat in Attract for
    /// the whole event: BeginSession was never called by anything in the scene, so the clock never
    /// started, the patient was never reset between visitors, and the operation could not be won
    /// even when it was performed correctly. An unattended stand has nobody to press start.
    /// </summary>
    public class TransplantRoundBridge : MonoBehaviour
    {
        [SerializeField] private EventSessionController session;
        [SerializeField] private TransplantProcedure procedure;

        [Header("Reset between visitors")]
        [SerializeField] private SternotomyController sternotomy;
        [SerializeField] private SternotomyWorker sternotomyWorker;
        [SerializeField] private GrabbableOrgan[] organs = new GrabbableOrgan[0];
        [SerializeField] private VesselAnastomosis[] vessels = new VesselAnastomosis[0];
        [SerializeField] private Heartbeat donorHeart;

        [Header("Booth loop")]
        [Tooltip("How long the attract screen holds before the stand offers the next turn. Zero " +
                 "starts the next visitor immediately, which reads as a stand that never rests.")]
        [SerializeField, Min(0f)] private float attractSeconds = 6f;

        [Tooltip("Off hands the loop to an operator, who calls BeginSession themselves.")]
        [SerializeField] private bool autoAdvance = true;

        private Vector3[] _organHomePositions;
        private Quaternion[] _organHomeRotations;
        private float _attractElapsed;

        /// <summary>How many times a visitor's patient has been put back to the start.</summary>
        public int ResetCount { get; private set; }

        private void Awake() => CaptureOrganHomePoses();

        private void OnEnable() => Subscribe();

        private void OnDisable() => Unsubscribe();

        private void Subscribe()
        {
            if (procedure != null) { procedure.ProcedureCompleted += HandleProcedureCompleted; }
            if (session != null) { session.StateChanged += HandleSessionState; }
            if (sternotomyWorker != null) { sternotomyWorker.Started += HandleFirstGesture; }
        }

        private void Unsubscribe()
        {
            if (procedure != null) { procedure.ProcedureCompleted -= HandleProcedureCompleted; }
            if (session != null) { session.StateChanged -= HandleSessionState; }
            if (sternotomyWorker != null) { sternotomyWorker.Started -= HandleFirstGesture; }
        }

        private void Update() => Tick(Time.deltaTime);

        /// <summary>
        /// Advances the part of the booth loop the session cannot advance on its own. Stepped by
        /// hand in tests, so a whole queue can be verified without sitting through it.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!autoAdvance || deltaTime <= 0f || session == null) { return; }

            if (session.State != SessionState.Attract)
            {
                _attractElapsed = 0f;
                return;
            }

            _attractElapsed += deltaTime;
            if (_attractElapsed >= attractSeconds)
            {
                _attractElapsed = 0f;
                session.BeginSession();
            }
        }

        /// <summary>
        /// The operation is finished. Raised on the shared bus rather than called on the session
        /// directly, because that is the signal the session already listens for to end a round as
        /// a win — and it keeps this class from having to know how winning is implemented.
        /// </summary>
        private void HandleProcedureCompleted() => SurgeryEvents.RaiseSurgeryCompleted();

        /// <summary>
        /// The visitor has started cutting. Everywhere else in this project the round starts when
        /// an instrument is picked up, but the transplant is worked with the hands and there is no
        /// instrument to pick up — so the sternotomy is the first real gesture and the clock
        /// starts on it. Starting it any earlier would charge the visitor for the briefing.
        /// </summary>
        private void HandleFirstGesture()
        {
            if (session != null) { session.StartRound(); }
        }

        private void HandleSessionState(SessionState state)
        {
            // Wiped at the briefing, not at the end of the previous round: the result and the
            // scoreboard are still up then, and resetting underneath them would close the chest
            // in front of an audience still being told what just happened in it.
            if (state == SessionState.Briefing)
            {
                ResetForNextVisitor();
            }
        }

        /// <summary>
        /// Puts the patient back to the top of the operation: chest closed, sick heart in its
        /// seat, donor heart cold on the stand, no vessel sewn, pump untouched.
        /// </summary>
        public void ResetForNextVisitor()
        {
            ResetCount++;

            if (donorHeart != null) { donorHeart.StopBeating(); }
            if (sternotomy != null) { sternotomy.ResetClosed(); }
            if (sternotomyWorker != null) { sternotomyWorker.ResetGesture(); }

            for (int i = 0; i < vessels.Length; i++)
            {
                if (vessels[i] != null) { vessels[i].ResetJoin(); }
            }

            ReturnOrgansHome();

            if (procedure != null)
            {
                // Reset then Begin, in that order: Begin replays whatever the team did before the
                // visitor arrived through the pump's own rules, and replaying it onto a pump that
                // still holds the last visitor's state would be refused.
                procedure.ResetProcedure();
                procedure.Begin();
            }
        }

        private void CaptureOrganHomePoses()
        {
            _organHomePositions = new Vector3[organs.Length];
            _organHomeRotations = new Quaternion[organs.Length];

            for (int i = 0; i < organs.Length; i++)
            {
                if (organs[i] == null) { continue; }
                _organHomePositions[i] = organs[i].transform.position;
                _organHomeRotations[i] = organs[i].transform.rotation;
            }
        }

        private void ReturnOrgansHome()
        {
            if (_organHomePositions == null || _organHomePositions.Length != organs.Length)
            {
                CaptureOrganHomePoses();
                return;
            }

            for (int i = 0; i < organs.Length; i++)
            {
                GrabbableOrgan organ = organs[i];
                if (organ == null) { continue; }

                organ.ResetOrgan();

                // Velocity as well as pose. A heart let go mid-throw and then teleported home
                // keeps its momentum and sails straight back out of the chest.
                Rigidbody body = organ.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }

                organ.transform.SetPositionAndRotation(_organHomePositions[i], _organHomeRotations[i]);
            }
        }

        public void Bind(
            EventSessionController controller,
            TransplantProcedure transplant,
            SternotomyController sternotomyController,
            SternotomyWorker worker,
            GrabbableOrgan[] resettableOrgans,
            VesselAnastomosis[] resettableVessels,
            Heartbeat donor)
        {
            Unsubscribe();

            session = controller;
            procedure = transplant;
            sternotomy = sternotomyController;
            sternotomyWorker = worker;
            organs = resettableOrgans ?? new GrabbableOrgan[0];
            vessels = resettableVessels ?? new VesselAnastomosis[0];
            donorHeart = donor;

            CaptureOrganHomePoses();

            if (isActiveAndEnabled) { Subscribe(); }
        }
    }
}
