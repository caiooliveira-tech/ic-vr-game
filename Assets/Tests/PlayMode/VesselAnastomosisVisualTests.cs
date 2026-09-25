using NUnit.Framework;
using UnityEngine;
using VRSurgery.Transplant;

namespace VRSurgery.Tests
{
    /// <summary>
    /// The ring's colour is the only feedback the physical mannequin's projector can show — no
    /// text reaches it — so a bleeding site has to actually look different, not just report
    /// IsBleeding to something that only the headset ever reads.
    /// </summary>
    public class VesselAnastomosisVisualTests
    {
        private GameObject _host;
        private TransplantProcedure _procedure;
        private VesselAnastomosis _site;
        private VesselAnastomosisVisual _visual;
        private readonly Color _resting = new Color(0.85f, 0.22f, 0.20f, 0.65f);

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("Implant");
            _procedure = _host.AddComponent<TransplantProcedure>();

            GameObject siteHost = new GameObject("Aorta");
            siteHost.transform.position = new Vector3(0f, 1.2f, 0.5f);
            _site = siteHost.AddComponent<VesselAnastomosis>();
            _site.Bind(VesselSite.Aorta, _procedure, 0.025f);

            GameObject ringHost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ringHost.transform.SetParent(siteHost.transform, false);
            Renderer ring = ringHost.GetComponent<Renderer>();
            ring.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));

            _visual = siteHost.AddComponent<VesselAnastomosisVisual>();
            _visual.Bind(_site, ring, _resting);

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

        [Test]
        public void AnUntouchedSiteShowsItsRestingColour()
        {
            _visual.Tick(1f / 60f);

            Assert.AreEqual(_resting, _visual.CurrentColor);
        }

        [Test]
        public void ABleedingSiteTurnsFullyOpaqueRed()
        {
            Vector3 a = On + new Vector3(0.01f, 0f, 0f);
            Vector3 b = On - new Vector3(0.01f, 0f, 0f);
            for (int i = 0; i < 120; i++) { _site.Work(i % 2 == 0 ? a : b, 1f / 60f); }
            Assert.IsTrue(_site.IsBleeding, "test setup: the shaky hold should have caused a leak.");

            _visual.Tick(1f / 60f);

            Assert.AreEqual(1f, _visual.CurrentColor.a,
                "A bleeding ring reads at full opacity, not the resting marker's 0.65 alpha.");
            Assert.AreNotEqual(_resting, _visual.CurrentColor,
                "Bleeding has to look different from the calm marker, or it is invisible to the crowd.");
        }

        [Test]
        public void AJoinThatFinishesBleedingSettlesToAHealedColour()
        {
            Vector3 a = On + new Vector3(0.01f, 0f, 0f);
            Vector3 b = On - new Vector3(0.01f, 0f, 0f);
            for (int i = 0; i < 120; i++) { _site.Work(i % 2 == 0 ? a : b, 1f / 60f); }
            for (int i = 0; i < 120; i++) { _site.Work(On, 1f / 60f); }
            Assert.IsTrue(_site.IsJoined, "test setup: sustained pressure should have closed the join.");

            // Past the healed flash's own fade window (1.2s default).
            for (int i = 0; i < 90; i++) { _visual.Tick(1f / 60f); }

            Assert.IsFalse(_visual.CurrentColor.Equals(_resting),
                "A finished join should not still read as the untouched marker.");
            // 0.55 is healedColor's own alpha (its default): once the flash has fully settled,
            // the ring should hold at that quieter opacity, not the flash's full-opacity moment.
            Assert.AreEqual(0.55f, _visual.CurrentColor.a, 0.05f,
                "The healed colour should have settled to its own alpha, not stayed at the flash's full opacity.");
        }

        [Test]
        public void ACleanJoinNeverTurnsRed()
        {
            for (int i = 0; i < 120; i++) { _site.Work(On, 1f / 60f); }
            Assert.IsTrue(_site.IsJoined);
            Assert.IsFalse(_site.BledDuringJoin, "test setup: a steady hold should close clean.");

            _visual.Tick(1f / 60f);

            Assert.IsFalse(_visual.CurrentColor.Equals(new Color(0.95f, 0.05f, 0.05f, 1f)),
                "A clean join was never bleeding, so it should never have flashed alarm red.");
        }
    }
}
