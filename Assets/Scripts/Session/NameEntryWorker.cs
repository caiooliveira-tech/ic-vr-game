using System.Collections.Generic;
using UnityEngine;

namespace VRSurgery.Session
{
    /// <summary>
    /// Carries a fingertip to whichever key of the name-entry keyboard it is resting on, the same
    /// "held, not tapped" way <c>AnastomosisWorker</c> carries an instrument to a vessel: a key
    /// only fires after the tip has sat inside it for a moment, so a hand passing over the panel
    /// on its way somewhere else does not spell out letters by accident.
    ///
    /// A key fires once per visit. The tip has to leave its radius before the same key can fire
    /// again, which is what turns a held dwell into a single keystroke instead of the letter
    /// repeating for as long as the hand stays put.
    /// </summary>
    public class NameEntryWorker : MonoBehaviour
    {
        [Tooltip("The working end. Defaults to this transform.")]
        [SerializeField] private Transform tip;

        [Tooltip("Keys on the panel. Filled by the scene builder.")]
        [SerializeField] private List<NameEntryKey> keys = new List<NameEntryKey>();

        [Tooltip("Where a pressed key is reported.")]
        [SerializeField] private NameEntryController controller;

        [Tooltip("How close the tip has to be to count as resting on a key, in metres.")]
        [SerializeField, Min(0.005f)] private float radius = 0.02f;

        [Tooltip("Seconds of steady contact before a key fires.")]
        [SerializeField, Min(0.05f)] private float secondsToPress = 0.35f;

        private NameEntryKey _hoveredKey;
        private float _heldSeconds;
        private bool _firedForCurrentHover;

        /// <summary>The key the tip is currently resting on, if any.</summary>
        public NameEntryKey HoveredKey => _hoveredKey;

        /// <summary>0 to 1 progress toward the current key firing. 0 while nothing is hovered.</summary>
        public float Hold01 => secondsToPress <= 0f ? 0f : Mathf.Clamp01(_heldSeconds / secondsToPress);

        private void Awake()
        {
            if (tip == null) { tip = transform; }
        }

        private void Update() => Tick(Time.deltaTime);

        /// <summary>Advances whichever key the tip is resting on. Stepped by hand in tests.</summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f || tip == null || controller == null || !controller.IsAwaitingName) { return; }

            NameEntryKey best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < keys.Count; i++)
            {
                NameEntryKey key = keys[i];
                if (key == null) { continue; }

                float distance = Vector3.Distance(tip.position, key.transform.position);
                if (distance <= radius && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = key;
                }
            }

            if (best != _hoveredKey)
            {
                _hoveredKey = best;
                _heldSeconds = 0f;
                _firedForCurrentHover = false;
            }

            if (_hoveredKey == null) { return; }

            _heldSeconds += deltaTime;

            if (!_firedForCurrentHover && _heldSeconds >= secondsToPress)
            {
                _firedForCurrentHover = true;
                _hoveredKey.Press(controller);
            }
        }

        public void Bind(Transform workingTip, IEnumerable<NameEntryKey> panelKeys, NameEntryController nameController)
        {
            tip = workingTip != null ? workingTip : transform;
            keys = new List<NameEntryKey>(panelKeys);
            controller = nameController;
        }
    }
}
