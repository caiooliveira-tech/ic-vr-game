using NUnit.Framework;
using UnityEngine;
using VRSurgery.Transplant;

namespace VRSurgery.Tests
{
    /// <summary>
    /// The implant: five joins, and the heart that starts once they are done.
    ///
    /// What these mostly guard is the difference between a careful join and an accident. At a
    /// stand, a first-timer's hand crosses every vessel on the way to the one they meant, and a
    /// site that completes on contact would sew all five in a single sweep.
    /// </summary>
    public class AnastomosisTests
    {
        private GameObject _host;
        private TransplantProcedure _procedure;
        private VesselAnastomosis _site;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("Implant");
            _procedure = _host.AddComponent<TransplantProcedure>();

            GameObject siteHost = new GameObject("Aorta");
            siteHost.transform.position = new Vector3(0f, 1.2f, 0.5f);
            _site = siteHost.AddComponent<VesselAnastomosis>();
            _site.Bind(VesselSite.Aorta, _procedure, 0.025f);

            // Walk the procedure to the stage where vessels can be joined.
            _procedure.Begin();
            _procedure.CompleteStage(TransplantStage.OpenChest);
            _procedure.Bypass.Attempt(BypassStep.Cannulate);
            _procedure.Bypass.Attempt(BypassStep.ClampAorta);
            _procedure.Bypass.Attempt(BypassStep.Cardioplegia);
            _procedure.CompleteStage(TransplantStage.GoOnBypass);
            _procedure.CompleteStage(TransplantStage.RemoveNativeHeart);
            _procedure.CompleteStage(TransplantStage.PlaceDonorHeart);
        }

        [TearDown]
        public void TearDown()
        {
            if (_site != null) { Object.DestroyImmediate(_site.gameObject); }
            if (_host != null) { Object.DestroyImmediate(_host); }
        }

        private Vector3 On => _site.transform.position;
        private Vector3 Off => _site.transform.position + new Vector3(0.3f, 0f, 0f);

        [Test]
        public void BrushingPastAVesselDoesNotJoinIt()
        {
            // One frame of contact, which is what a hand crossing the site looks like.
            _site.Work(On, 1f / 60f);

            Assert.IsFalse(_site.IsJoined,
                "Five careful joins must not be reachable with one sweep of the hand.");
            Assert.Less(_site.Progress01, 0.1f);
        }

        [Test]
        public void HoldingSteadyJoinsTheVessel()
        {
            for (int i = 0; i < 120; i++) { _site.Work(On, 1f / 60f); }

            Assert.IsTrue(_site.IsJoined);
            Assert.AreEqual(1f, _site.Progress01);
            Assert.AreEqual(1, _procedure.VesselsConnected, "The join has to reach the procedure.");
        }

        [Test]
        public void DriftingOffLosesGroundWithoutLosingTheJoin()
        {
            for (int i = 0; i < 40; i++) { _site.Work(On, 1f / 60f); }
            float earned = _site.Progress01;
            Assert.Greater(earned, 0f);

            // A wobble, which at a stand is everyone.
            for (int i = 0; i < 10; i++) { _site.Work(Off, 1f / 60f); }

            Assert.Less(_site.Progress01, earned, "Drifting off should cost something,");
            Assert.Greater(_site.Progress01, 0f, "but not everything — that would punish a tremor.");
        }

        [Test]
        public void AShakyHoldLeavesTheJoinBleedingInsteadOfClean()
        {
            // Alternate two points inside the radius, fast enough that consecutive samples read
            // as a hand that will not sit still, for most of the hold.
            Vector3 a = On + new Vector3(0.01f, 0f, 0f);
            Vector3 b = On - new Vector3(0.01f, 0f, 0f);

            for (int i = 0; i < 120; i++)
            {
                _site.Work(i % 2 == 0 ? a : b, 1f / 60f);
            }

            Assert.IsFalse(_site.IsJoined, "A shaky hold should not close clean.");
            Assert.IsTrue(_site.IsBleeding, "An imprecise join should leak instead.");
            Assert.AreEqual(0, _procedure.VesselsConnected,
                "A leaking join has not reached the procedure yet.");
        }

        [Test]
        public void PressureStopsTheBleedingAndFinishesTheJoin()
        {
            Vector3 a = On + new Vector3(0.01f, 0f, 0f);
            Vector3 b = On - new Vector3(0.01f, 0f, 0f);

            for (int i = 0; i < 120; i++)
            {
                _site.Work(i % 2 == 0 ? a : b, 1f / 60f);
            }

            Assert.IsTrue(_site.IsBleeding);

            // Steady pressure, same as any other hold at the site.
            for (int i = 0; i < 120; i++) { _site.Work(On, 1f / 60f); }

            Assert.IsFalse(_site.IsBleeding, "Sustained pressure should stop the leak.");
            Assert.IsTrue(_site.IsJoined, "and the join should finish once it does.");
            Assert.IsTrue(_site.BledDuringJoin, "The site should remember it did not close clean.");
            Assert.AreEqual(1, _procedure.VesselsConnected,
                "The procedure only hears about the join once it is actually resolved.");
        }

        [Test]
        public void AGentleTremorStillClosesClean()
        {
            // A small, slow drift within the radius — the kind of wobble every visitor has —
            // should not be enough to count as an imprecise join.
            Vector3 origin = On;
            for (int i = 0; i < 120; i++)
            {
                float wobble = Mathf.Sin(i * 0.05f) * 0.002f;
                _site.Work(origin + new Vector3(wobble, 0f, 0f), 1f / 60f);
            }

            Assert.IsTrue(_site.IsJoined);
            Assert.IsFalse(_site.IsBleeding);
            Assert.IsFalse(_site.BledDuringJoin);
        }

        [Test]
        public void AJoinedVesselStaysJoined()
        {
            for (int i = 0; i < 120; i++) { _site.Work(On, 1f / 60f); }
            Assert.AreEqual(1, _procedure.VesselsConnected);

            // Resting the instrument on a finished join must not count twice.
            for (int i = 0; i < 120; i++) { _site.Work(On, 1f / 60f); }
            Assert.AreEqual(1, _procedure.VesselsConnected);
        }

        [Test]
        public void TheHeartDoesNotBeatUntilItIsRestarted()
        {
            GameObject organ = new GameObject("Heart");
            organ.transform.localScale = Vector3.one;
            Heartbeat beat = organ.AddComponent<Heartbeat>();

            for (int i = 0; i < 60; i++) { beat.Tick(1f / 60f); }
            Assert.IsFalse(beat.IsBeating);
            Assert.AreEqual(1f, beat.Pulse, 0.0001f);
            Assert.AreEqual(Vector3.one, organ.transform.localScale);

            beat.StartBeating();
            Assert.IsTrue(beat.IsBeating);

            Object.DestroyImmediate(organ);
        }

        [Test]
        public void TheBeatSqueezesAndRefillsRatherThanOscillating()
        {
            GameObject organ = new GameObject("Heart");
            Heartbeat beat = organ.AddComponent<Heartbeat>();
            beat.StartBeating();

            // Past the startup ramp so the amplitude is fully in.
            for (int i = 0; i < 300; i++) { beat.Tick(1f / 60f); }

            float smallest = 1f, largest = 0f;
            int contracted = 0, total = 0;
            for (int i = 0; i < 120; i++)
            {
                beat.Tick(1f / 60f);
                smallest = Mathf.Min(smallest, beat.Pulse);
                largest = Mathf.Max(largest, beat.Pulse);
                if (beat.Pulse < 0.999f) { contracted++; }
                total++;
            }

            Assert.Less(smallest, 1f, "The muscle has to actually shorten.");
            Assert.AreEqual(1f, largest, 0.001f, "and come back to rest between beats.");

            // Systole is the short part of the cycle. A sine would spend half the time contracted
            // and read as breathing rather than beating.
            Assert.Less(contracted / (float)total, 0.5f,
                "Most of a cycle is the heart filling, not squeezing.");

            Object.DestroyImmediate(organ);
        }

        [Test]
        public void RestartingIsTheLastStage()
        {
            for (int i = 0; i < 120; i++) { _site.Work(On, 1f / 60f); }

            // Four more vessels to satisfy the stage.
            for (int i = 1; i < _procedure.VesselCount; i++) { _procedure.ConnectVessel(); }

            Assert.AreEqual(TransplantStage.Restart, _procedure.Stage);

            // The restart is coming off bypass, so it cannot be declared done while the pump is
            // still carrying the patient.
            Assert.IsFalse(_procedure.CompleteStage(TransplantStage.Restart),
                "Calling the operation finished while still on bypass would leave the patient on the pump.");

            _procedure.Bypass.Attempt(BypassStep.Unclamp, true);
            _procedure.Bypass.Attempt(BypassStep.DeAir, true);
            _procedure.Bypass.Attempt(BypassStep.Wean, true);

            Assert.IsTrue(_procedure.CompleteStage(TransplantStage.Restart));
            Assert.IsTrue(_procedure.IsComplete);
        }
    }
}
