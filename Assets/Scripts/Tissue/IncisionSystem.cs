using System;
using UnityEngine;
using VRSurgery.Cutting;
using VRSurgery.Surgery;
using VRSurgery.Tools;

namespace VRSurgery.Tissue
{
    /// <summary>
    /// Coordinates a cut session on one tissue surface: takes swept blade segments, decides
    /// whether they constitute a valid incision, records them, tracks deviation from the guide,
    /// and raises the events other systems (score, audio, haptics, objectives) listen to.
    ///
    /// It never touches UI, audio or scoring directly — everything leaves through SurgeryEvents.
    /// </summary>
    [RequireComponent(typeof(TissueSurface))]
    public class IncisionSystem : MonoBehaviour
    {
        [Header("Cut gating")]
        [SerializeField] private float minimumCutSpeed = 0.02f;
        [SerializeField] private float maximumCutSpeed = 1.2f;
        [SerializeField, Range(0f, 1f)] private float cutSensitivity = 1f;

        [Header("Completion")]
        [Tooltip("Incision length in metres that counts as a completed incision. 0 = derive from the guide.")]
        [SerializeField] private float requiredLength = 0f;

        [Header("References")]
        [SerializeField] private IncisionGuide guide;
        [SerializeField] private CuttableTissue cuttableTissue;

        private TissueSurface _tissue;
        private bool _sessionActive;
        private bool _completed;
        private SurgicalTool _activeTool;

        private float _deviationSum;
        private int _deviationSamples;

        public bool IsCutting => _sessionActive;
        public bool IsCompleted => _completed;
        public TissueSurface Tissue => _tissue;
        public IncisionGuide Guide => guide;

        /// <summary>Mean deviation from the guided path across every recorded contact sample, in metres.</summary>
        public float AverageDeviation => _deviationSamples > 0 ? _deviationSum / _deviationSamples : 0f;

        /// <summary>Worst single deviation observed, in metres.</summary>
        public float WorstDeviation { get; private set; }

        /// <summary>0..1 progress toward the required incision length.</summary>
        public float Progress01 => RequiredLength <= 0f ? 0f : Mathf.Clamp01(_tissue.IncisionLength / RequiredLength);

        public float RequiredLength
        {
            get
            {
                if (requiredLength > 0f)
                {
                    return requiredLength;
                }

                return guide != null ? guide.PathLength : 0.1f;
            }
        }

        /// <summary>Raised when a cut sample lands outside the guide's failure tolerance.</summary>
        public event Action<Vector3, float> StrayCut;

        private void Awake()
        {
            _tissue = GetComponent<TissueSurface>();
            if (cuttableTissue == null)
            {
                cuttableTissue = GetComponent<CuttableTissue>();
            }
            if (guide == null)
            {
                guide = GetComponentInChildren<IncisionGuide>();
            }
        }

        /// <summary>
        /// Feeds one swept blade segment (world space) into the system.
        /// Returns true if this segment produced contact with the tissue.
        /// </summary>
        public bool ProcessBladeSegment(Vector3 worldPrevious, Vector3 worldCurrent,
            float deltaTime, SurgicalTool tool)
        {
            return ProcessBladeSegment(worldPrevious, worldCurrent, deltaTime, tool, null);
        }

        public bool ProcessBladeSegment(Vector3 worldPrevious, Vector3 worldCurrent,
            float deltaTime, SurgicalTool tool, BladeTip blade)
        {
            if (cuttableTissue != null)
            {
                return ProcessCurvedBladeSegment(worldPrevious, worldCurrent, deltaTime, tool, blade);
            }

            return ProcessPlanarBladeSegment(worldPrevious, worldCurrent, deltaTime, tool);
        }

        private bool ProcessPlanarBladeSegment(Vector3 worldPrevious, Vector3 worldCurrent,
            float deltaTime, SurgicalTool tool)
        {
            if (_tissue == null)
            {
                return false;
            }

            Vector3 previousLocal = _tissue.WorldToTissueLocal(worldPrevious);
            Vector3 currentLocal = _tissue.WorldToTissueLocal(worldCurrent);

            IncisionGeometry.SweepResult sweep = IncisionGeometry.Sweep(
                previousLocal, currentLocal, _tissue.HalfExtents, _tissue.MaxPenetration);

            if (!sweep.InContact)
            {
                if (_sessionActive)
                {
                    EndSession();
                }

                return false;
            }

            float travelled = Vector3.Distance(previousLocal, currentLocal);
            float speed = deltaTime > 0f ? travelled / deltaTime : 0f;
            float speedQuality = IncisionGeometry.EvaluateCutSpeed(speed, minimumCutSpeed, maximumCutSpeed);

            if (!_sessionActive)
            {
                _sessionActive = true;
                _activeTool = tool;
                SurgeryEvents.RaiseIncisionStarted(tool);
            }

            // Speed outside the usable band still counts as contact, but cuts nothing.
            if (speedQuality <= 0f)
            {
                return true;
            }

            float effectiveDepth = sweep.Depth01 * Mathf.Clamp01(cutSensitivity) * speedQuality;
            bool pointAdded = _tissue.ApplyIncision(sweep.SurfacePoint, effectiveDepth);

            if (pointAdded)
            {
                RecordDeviation(sweep.SurfacePoint);
                SurgeryEvents.RaiseIncisionProgressed(tool, Progress01);
            }

            if (!_completed && Progress01 >= 1f)
            {
                _completed = true;
                SurgeryEvents.RaiseIncisionCompleted(tool);
            }

            return true;
        }

        private bool ProcessCurvedBladeSegment(Vector3 worldPrevious, Vector3 worldCurrent,
            float deltaTime, SurgicalTool tool, BladeTip blade)
        {
            CuttableTissue.Contact contact = cuttableTissue.SampleContact(
                worldPrevious, worldCurrent, blade);

            float travelled = Vector3.Distance(worldPrevious, worldCurrent);
            float speed = deltaTime > 0f ? travelled / deltaTime : 0f;
            float speedQuality = IncisionGeometry.EvaluateCutSpeed(
                speed, minimumCutSpeed, maximumCutSpeed);

            if (!contact.InContact || !contact.CanCut || speedQuality <= 0f)
            {
                if (_sessionActive) { EndSession(); }
                return contact.InContact;
            }

            float effectiveDepth = contact.Depth01 * Mathf.Clamp01(cutSensitivity) * speedQuality;
            CuttableTissue.Contact effective = new CuttableTissue.Contact(
                true, true, contact.LocalPoint, contact.LocalNormal, effectiveDepth);

            if (!_sessionActive)
            {
                _sessionActive = true;
                _activeTool = tool;
                SurgeryEvents.RaiseIncisionStarted(tool);
            }

            bool pointAdded = cuttableTissue.IsRecording
                ? cuttableTissue.ContinueCut(effective)
                : cuttableTissue.BeginCut(effective);

            if (pointAdded)
            {
                _tissue.ApplyIncision(contact.LocalPoint, effectiveDepth);
                RecordDeviation(contact.LocalPoint);
                SurgeryEvents.RaiseIncisionProgressed(tool, Progress01);
            }

            if (!_completed && Progress01 >= 1f)
            {
                _completed = true;
                SurgeryEvents.RaiseIncisionCompleted(tool);
            }

            return true;
        }

        private void RecordDeviation(Vector3 localPoint)
        {
            if (guide == null)
            {
                return;
            }

            float deviation = guide.DeviationAt(localPoint);
            _deviationSum += deviation;
            _deviationSamples++;

            if (deviation > WorstDeviation)
            {
                WorstDeviation = deviation;
            }

            if (guide.IsOutsideValidArea(deviation))
            {
                StrayCut?.Invoke(localPoint, deviation);
            }
        }

        private void EndSession()
        {
            if (cuttableTissue != null && cuttableTissue.IsRecording)
            {
                cuttableTissue.EndCut();
            }
            _sessionActive = false;
            _activeTool = null;
        }

        /// <summary>Called when the interactor is released or leaves this target entirely.</summary>
        public void EndBladeContact()
        {
            if (_sessionActive) { EndSession(); }
        }

        public void ResetSession()
        {
            _sessionActive = false;
            _completed = false;
            _activeTool = null;
            _deviationSum = 0f;
            _deviationSamples = 0;
            WorstDeviation = 0f;

            if (_tissue != null)
            {
                _tissue.ResetTissue();
            }

            if (cuttableTissue != null)
            {
                cuttableTissue.ResetTissue();
            }
        }

        /// <summary>0..1 accuracy across the whole incision, from mean deviation.</summary>
        public float AccuracyScore01()
        {
            if (guide == null || _deviationSamples == 0)
            {
                return 0f;
            }

            return guide.ScoreDeviation(AverageDeviation);
        }
    }
}
