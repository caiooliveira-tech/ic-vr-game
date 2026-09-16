using NUnit.Framework;
using UnityEngine;
using VRSurgery.Interaction;
using VRSurgery.Session;
using VRSurgery.Surgery;
using VRSurgery.Transplant;

namespace VRSurgery.Tests
{
    /// <summary>
    /// The loop the stand actually runs: a visitor arrives, cuts, operates, wins or runs out of
    /// clock, and the patient goes back to the start for the next person in the queue.
    ///
    /// Every rule these check was already implemented and already tested in isolation — and none
    /// of it was connected, so the scene held a complete transplant that could not be started,
    /// could not be won, and never reset between visitors. These are the joins, which is exactly
    /// the class of defect a unit test on either side of the join cannot see.
    ///
    /// Time is stepped by hand rather than waited on, so a 150-second round costs the suite
    /// nothing.
    /// </summary>
    public class TransplantRoundTests
    {
        private const float Round = 60f;
        private const float AttractHold = 5f;
        private const float SternalHold = 3f;
        private const float SternalRadius = 0.08f;

        private static readonly Vector3 SternalSite = new Vector3(0f, 1.2f, 0.5f);

        private GameObject _host;
        private GameObject _sternum;
        private GameObject _tip;

        private EventSessionDefinition _definition;
        private EventSessionController _session;
        private TransplantProcedure _procedure;
        private SternotomyController _sternotomy;
        private SternotomyWorker _opening;
        private TransplantRoundBridge _bridge;

        [SetUp]
        public void SetUp()
        {
            SurgeryEvents.ResetAll();

            _definition = EventSessionDefinition.Create(
                Round, briefingTimeout: 0f, resultHold: 4f, scoreboardHold: 6f,
                startOnGrab: false, pointsPerSecond: 100f);

            _tip = new GameObject("Tip");
            _tip.transform.position = Vector3.zero;

            _sternum = new GameObject("Sternum");
            _sternum.transform.position = SternalSite;
            _sternotomy = _sternum.AddComponent<SternotomyController>();
            _sternotomy.Bind(_sternum.transform, new GameObject[0]);

            GameObject site = new GameObject("SternalMidline");
            site.transform.position = SternalSite;

            // Built inactive so everything is bound before the first OnEnable subscribes.
            _host = new GameObject("Systems");
            _host.SetActive(false);

            _procedure = _host.AddComponent<TransplantProcedure>();
            _session = _host.AddComponent<EventSessionController>();
            _session.Bind(_definition, null);

            _opening = _host.AddComponent<SternotomyWorker>();
            _opening.Bind(_tip.transform, site.transform, _sternotomy, _procedure,
                SternalRadius, SternalHold);

            _bridge = _host.AddComponent<TransplantRoundBridge>();
            _bridge.Bind(_session, _procedure, _sternotomy, _opening,
                new GrabbableOrgan[0], new VesselAnastomosis[0], null);

            _host.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) { Object.DestroyImmediate(_host); }
            if (_sternum != null) { Object.DestroyImmediate(_sternum); }
            if (_tip != null) { Object.DestroyImmediate(_tip); }
            if (_definition != null) { Object.DestroyImmediate(_definition); }

            SurgeryEvents.ResetAll();
        }

        /// <summary>Puts the hand on the sternum and holds it there long enough to cut.</summary>
        private void CutTheSternum()
        {
            _tip.transform.position = SternalSite;

            for (int i = 0; i < 10; i++)
            {
                _opening.Tick(SternalHold * 0.2f);
            }
        }

        /// <summary>Runs the sternum's animation out, which is what clears the first stage.</summary>
        private void LetTheChestFinishOpening()
        {
            for (int i = 0; i < 60; i++)
            {
                _sternotomy.Tick(0.1f);
            }
        }

        [Test]
        public void AnUnattendedBoothStartsTheNextVisitorByItself()
        {
            Assert.AreEqual(SessionState.Attract, _session.State);

            _bridge.Tick(AttractHold + 1f);

            Assert.AreEqual(SessionState.Briefing, _session.State,
                "Nobody is standing at the stand to press start; the booth has to offer the turn.");
        }

        [Test]
        public void TheBriefingDealsTheVisitorAFreshOperation()
        {
            _session.BeginSession();

            Assert.AreEqual(TransplantStage.OpenChest, _procedure.Stage,
                "The operation has to be waiting at its first stage when the visitor looks up.");
            Assert.AreEqual(1, _bridge.ResetCount);
        }

        [Test]
        public void TheClockDoesNotStartUntilTheVisitorCuts()
        {
            _session.BeginSession();

            Assert.AreEqual(SessionState.Briefing, _session.State);
            Assert.AreEqual(Round, _session.RemainingSeconds, 0.001f,
                "Reading the briefing must not cost the visitor any of their round.");

            CutTheSternum();

            Assert.AreEqual(SessionState.Running, _session.State,
                "There is no instrument to pick up in this scene, so the cut is what starts the clock.");
        }

        [Test]
        public void OpeningTheChestClearsTheFirstStage()
        {
            _session.BeginSession();
            CutTheSternum();

            Assert.AreEqual(TransplantStage.OpenChest, _procedure.Stage,
                "The stage must not advance while the sternum is still visibly swinging apart.");

            LetTheChestFinishOpening();

            Assert.IsTrue(_sternotomy.IsOpen);
            Assert.AreEqual(TransplantStage.GoOnBypass, _procedure.Stage);
        }

        [Test]
        public void AHandThatNeverReachesTheSternumOpensNothing()
        {
            _session.BeginSession();

            _tip.transform.position = SternalSite + new Vector3(0.5f, 0f, 0f);
            for (int i = 0; i < 20; i++) { _opening.Tick(0.2f); }

            Assert.AreEqual(0f, _opening.Progress01, 0.001f);
            Assert.AreEqual(SessionState.Briefing, _session.State,
                "A hand waving across the room is not a sternotomy.");
        }

        [Test]
        public void DriftingOffTheSternumCostsGroundRatherThanLosingIt()
        {
            _session.BeginSession();

            _tip.transform.position = SternalSite;
            _opening.Tick(SternalHold * 0.6f);
            float banked = _opening.Progress01;

            _tip.transform.position = SternalSite + new Vector3(0.5f, 0f, 0f);
            _opening.Tick(SternalHold * 0.2f);

            Assert.Less(_opening.Progress01, banked, "Drifting off has to cost something.");
            Assert.Greater(_opening.Progress01, 0f,
                "A tremor must not wipe the gesture; at a stand a tremor is the norm.");
        }

        [Test]
        public void FinishingTheOperationWinsTheRound()
        {
            _session.BeginSession();
            CutTheSternum();
            LetTheChestFinishOpening();

            _procedure.Bypass.Attempt(BypassStep.Cannulate);
            _procedure.Bypass.Attempt(BypassStep.ClampAorta);
            _procedure.Bypass.Attempt(BypassStep.Cardioplegia);
            _procedure.CompleteStage(TransplantStage.GoOnBypass);
            _procedure.CompleteStage(TransplantStage.RemoveNativeHeart);
            _procedure.CompleteStage(TransplantStage.PlaceDonorHeart);

            for (int i = 0; i < _procedure.VesselCount; i++) { _procedure.ConnectVessel(); }

            _procedure.Bypass.Attempt(BypassStep.Unclamp, true);
            _procedure.Bypass.Attempt(BypassStep.DeAir, true);
            _procedure.Bypass.Attempt(BypassStep.Wean, true);
            _procedure.CompleteStage(TransplantStage.Restart);

            Assert.IsTrue(_procedure.IsComplete);
            Assert.AreEqual(SessionState.Success, _session.State,
                "A transplant performed correctly has to win the round it was performed in.");
            Assert.IsTrue(_session.LastResult.Succeeded);
            Assert.Greater(_session.LastResult.Score, 0);
        }

        [Test]
        public void RunningOutOfClockLosesTheRound()
        {
            _session.BeginSession();
            CutTheSternum();

            _session.Tick(Round + 1f);

            Assert.AreEqual(SessionState.Failure, _session.State);
            Assert.IsFalse(_session.LastResult.Succeeded);
        }

        [Test]
        public void TheNextVisitorGetsAClosedChestAndASickHeart()
        {
            _session.BeginSession();
            CutTheSternum();
            LetTheChestFinishOpening();

            Assert.IsTrue(_sternotomy.IsOpen);
            Assert.AreEqual(TransplantStage.GoOnBypass, _procedure.Stage);

            // The queue moves on: the round is ended, the result and the scoreboard are read out,
            // and only then is the next turn offered. One Tick per screen, because the session
            // advances a single state per call.
            _session.AbortRound();
            _session.Tick(20f);
            _session.Tick(20f);

            Assert.AreEqual(SessionState.Attract, _session.State);

            _bridge.Tick(AttractHold + 1f);

            Assert.AreEqual(SessionState.Briefing, _session.State);
            Assert.AreEqual(0f, _sternotomy.Openness01, 0.001f,
                "The next visitor has to be handed a chest that is still closed.");
            Assert.AreEqual(TransplantStage.OpenChest, _procedure.Stage);
            Assert.AreEqual(BypassStep.NotStarted, _procedure.Bypass.Step,
                "A patient cannot arrive already cannulated by the person before them.");
            Assert.AreEqual(0f, _opening.Progress01, 0.001f);
        }
    }

    /// <summary>
    /// The shared working point, which is what lets either hand perform any step.
    ///
    /// The ergonomics rule this project already enforces for instruments — reachable from both
    /// shoulders, because left-handers and short-armed people exist in the queue — means nothing
    /// if the gestures themselves only answer to one hand.
    /// </summary>
    public class SurgeonHandTipTests
    {
        private GameObject _tip;
        private GameObject _left;
        private GameObject _right;
        private GameObject _work;
        private SurgeonHandTip _handTip;

        [SetUp]
        public void SetUp()
        {
            _left = new GameObject("Left") { transform = { position = new Vector3(-0.4f, 1.2f, 0.5f) } };
            _right = new GameObject("Right") { transform = { position = new Vector3(0.4f, 1.2f, 0.5f) } };
            _work = new GameObject("Work") { transform = { position = new Vector3(0f, 1.2f, 0.5f) } };

            _tip = new GameObject("Tip");
            _handTip = _tip.AddComponent<SurgeonHandTip>();
            _handTip.Bind(new[] { _left.transform, _right.transform }, new[] { _work.transform });
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in new[] { _tip, _left, _right, _work })
            {
                if (go != null) { Object.DestroyImmediate(go); }
            }
        }

        [Test]
        public void TheTipGoesToWhicheverHandIsOnTheWork()
        {
            _left.transform.position = _work.transform.position + new Vector3(0.01f, 0f, 0f);
            _handTip.Follow();

            Assert.AreSame(_left.transform, _handTip.ActiveHand);
            Assert.AreEqual(_left.transform.position, _tip.transform.position);

            _right.transform.position = _work.transform.position;
            _handTip.Follow();

            Assert.AreSame(_right.transform, _handTip.ActiveHand,
                "Either hand has to be able to take over the work at any moment.");
            Assert.AreEqual(_right.transform.position, _tip.transform.position);
        }

        [Test]
        public void AHandOnTheWorkBeatsAHandRestingOnTheDrapes()
        {
            _left.transform.position = _work.transform.position + new Vector3(0.015f, 0f, 0f);
            _right.transform.position = new Vector3(0.9f, 0.9f, 0f);

            _handTip.Follow();

            Assert.AreSame(_left.transform, _handTip.ActiveHand);
            Assert.Less(_handTip.DistanceToWork, 0.02f);
        }
    }

    /// <summary>
    /// The monitor, which is the only surface the clinical reasons ever reach.
    ///
    /// BypassProcedure has always carried a sentence explaining each refusal, and the project
    /// states plainly that the reason is the teaching. Until there was a screen to put it on, a
    /// wrong move was a gesture that silently did nothing.
    /// </summary>
    public class TransplantHUDTests
    {
        private GameObject _host;
        private GameObject _cannulate;
        private GameObject _clamp;
        private TransplantProcedure _procedure;
        private BypassWorker _worker;
        private TransplantHUD _hud;
        private TextMesh _instruction;
        private TextMesh _clock;
        private TextMesh _reason;

        [SetUp]
        public void SetUp()
        {
            SurgeryEvents.ResetAll();

            _cannulate = new GameObject("Cannulate") { transform = { position = new Vector3(0f, 1.2f, 0f) } };
            _clamp = new GameObject("Clamp") { transform = { position = new Vector3(0.5f, 1.2f, 0f) } };

            _instruction = new GameObject("InstructionText").AddComponent<TextMesh>();
            _clock = new GameObject("ClockText").AddComponent<TextMesh>();
            _reason = new GameObject("ReasonText").AddComponent<TextMesh>();

            _host = new GameObject("Systems");
            _host.SetActive(false);

            _procedure = _host.AddComponent<TransplantProcedure>();

            // The tip sits on the cross-clamp, which is the wrong site: the patient has not been
            // cannulated, so clamping would cut their circulation off entirely.
            _host.transform.position = _clamp.transform.position;

            _worker = _host.AddComponent<BypassWorker>();
            _worker.Bind(_host.transform, new System.Collections.Generic.List<BypassSite>
            {
                new BypassSite { Step = BypassStep.Cannulate, Point = _cannulate.transform, Radius = 0.03f, Seconds = 1f },
                new BypassSite { Step = BypassStep.ClampAorta, Point = _clamp.transform, Radius = 0.03f, Seconds = 1f },
            }, _procedure);

            _hud = _host.AddComponent<TransplantHUD>();
            _hud.Bind(_procedure, null, _worker, null, _instruction, _clock, _reason);

            _host.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in new[]
            {
                _host, _cannulate, _clamp,
                _instruction != null ? _instruction.gameObject : null,
                _clock != null ? _clock.gameObject : null,
                _reason != null ? _reason.gameObject : null,
            })
            {
                if (go != null) { Object.DestroyImmediate(go); }
            }

            SurgeryEvents.ResetAll();
        }

        /// <summary>
        /// What the screen says, with its wrapping undone. The HUD breaks lines to fit the panel,
        /// which is layout; these tests are about the words.
        /// </summary>
        private static string Unwrapped(TextMesh text) => text.text.Replace("\n", " ");

        [Test]
        public void TheMonitorTellsTheSurgeonWhatToDoNow()
        {
            _procedure.Begin();
            _hud.Refresh(0f);

            Assert.IsNotEmpty(_hud.Instruction);
            Assert.AreEqual(_procedure.CurrentInstruction, Unwrapped(_instruction),
                "The screen has to read what the operation is actually asking for.");
        }

        [Test]
        public void AWrongStepIsExplainedRatherThanIgnored()
        {
            _procedure.Begin();
            _procedure.CompleteStage(TransplantStage.OpenChest);

            // One frame with the hand on the cross-clamp, which is refused before any time banks.
            _worker.Tick(1f / 60f);

            Assert.IsNotEmpty(_worker.LastRefusal);

            _hud.Refresh(0f);

            Assert.AreEqual(_worker.LastRefusal, Unwrapped(_reason),
                "The clinical reason is the teaching; a gesture that merely fails teaches nothing.");
            StringAssert.Contains("circulação", Unwrapped(_reason));
        }

        /// <summary>
        /// A sentence that overruns the panel is a sentence nobody reads. TextMesh does not wrap,
        /// and the first build of this monitor rendered the refusals off both edges of the screen
        /// and out into the room — which every programmatic check passed.
        /// </summary>
        [Test]
        public void ALongExplanationIsBrokenToFitTheScreen()
        {
            _procedure.Begin();
            _procedure.CompleteStage(TransplantStage.OpenChest);
            _worker.Tick(1f / 60f);
            _hud.Refresh(0f);

            Assert.Greater(_hud.Reason.Length, 40, "This test needs a refusal long enough to wrap.");
            StringAssert.Contains("\n", _reason.text, "A full sentence has to be broken up.");

            foreach (string line in _reason.text.Split('\n'))
            {
                Assert.LessOrEqual(line.Length, 38,
                    $"'{line}' is wider than the panel it is drawn on.");
            }
        }

        [Test]
        public void TheExplanationClearsItselfBeforeTheNextMistake()
        {
            _procedure.Begin();
            _procedure.CompleteStage(TransplantStage.OpenChest);
            _worker.Tick(1f / 60f);
            _hud.Refresh(0f);

            Assert.IsNotEmpty(_hud.Reason);

            _hud.Refresh(30f);

            Assert.IsEmpty(_hud.Reason, "A stale reason next to a new mistake is worse than none.");
        }
    }
}
