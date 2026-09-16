using System.Collections.Generic;
using UnityEngine;
using VRSurgery.Interaction;

namespace VRSurgery.Transplant
{
    /// <summary>
    /// Carries the join from an instrument tip to whichever vessel site it is on.
    ///
    /// Separate from VesselAnastomosis on purpose: the site owns what it takes to be joined, this
    /// owns where the surgeon's hand is. That split is what lets the sites be tested with no rig
    /// in the scene, and what will let a needle holder, a stapler or a bare fingertip drive the
    /// same sites later without any of them knowing about each other.
    ///
    /// Only works while the instrument is actually held. An instrument lying on the tray inside a
    /// site's radius would otherwise sew the vessel by itself. A bare hand is the exception the
    /// class was always written for: there is nothing to put down, so there is nothing to hold.
    /// </summary>
    public class AnastomosisWorker : MonoBehaviour
    {
        [Tooltip("The working end. Defaults to this transform.")]
        [SerializeField] private Transform tip;

        [Tooltip("Require the instrument to be held. Off lets a bare tracked hand drive the sites.")]
        [SerializeField] private bool requireHeldInstrument = true;

        [Tooltip("Sites this instrument can join. Filled by the scene builder.")]
        [SerializeField] private List<VesselAnastomosis> sites = new List<VesselAnastomosis>();

        [Tooltip("Only joins while the procedure is at the vessel stage, so a visitor cannot sew " +
                 "a heart that is not in the chest yet.")]
        [SerializeField] private TransplantProcedure procedure;

        private SurgicalInteractable _interactable;

        /// <summary>The site currently being worked, if any. Drives the surgeon's prompt.</summary>
        public VesselAnastomosis ActiveSite { get; private set; }

        private void Awake()
        {
            _interactable = GetComponent<SurgicalInteractable>();
            if (tip == null) { tip = transform; }
        }

        private void Update() => Tick(Time.deltaTime);

        /// <summary>Advances whatever join the tip is inside. Stepped by hand in tests.</summary>
        public void Tick(float deltaTime)
        {
            ActiveSite = null;

            if (deltaTime <= 0f || tip == null) { return; }
            if (requireHeldInstrument && _interactable != null && !_interactable.IsHeld) { return; }
            if (procedure != null && procedure.Stage != TransplantStage.ConnectVessels) { return; }

            // Nearest unjoined site the tip is inside. Nearest rather than first, because the
            // vessels sit close together on a heart and the sites overlap.
            VesselAnastomosis best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < sites.Count; i++)
            {
                VesselAnastomosis site = sites[i];
                if (site == null || site.IsJoined) { continue; }

                float distance = Vector3.Distance(tip.position, site.transform.position);
                if (distance <= site.Radius && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = site;
                }
            }

            if (best == null) { return; }

            best.Work(tip.position, deltaTime);
            ActiveSite = best;
        }

        public void Bind(Transform workingTip, IEnumerable<VesselAnastomosis> vesselSites,
            TransplantProcedure transplant)
        {
            tip = workingTip != null ? workingTip : transform;
            sites = new List<VesselAnastomosis>(vesselSites);
            procedure = transplant;
        }
    }
}
