using UnityEngine;

namespace VRSurgery.Transplant
{
    /// <summary>
    /// The ring's colour, driven by the join it marks.
    ///
    /// Before this, IsBleeding only ever reached a line of text on the surgeon's own monitor. The
    /// projector aimed at the physical mannequin has no text to show, only the 3D scene itself —
    /// so an anastomosis could be actively leaking with nothing visible anywhere outside the
    /// headset. This is the "efeito físico" the projector exists for: the ring reddens and pulses
    /// while its site bleeds, and settles to a calm colour once the join is finished.
    ///
    /// Reads VesselAnastomosis every frame rather than subscribing to its events, the same way
    /// TransplantHUD reads the procedure — a visual that tracked bleeding state on its own would
    /// eventually disagree with the join it is supposed to be showing.
    /// </summary>
    public class VesselAnastomosisVisual : MonoBehaviour
    {
        [SerializeField] private VesselAnastomosis site;
        [SerializeField] private Renderer ring;

        [Header("Colours")]
        [Tooltip("Set at Bind time: arterial red or venous blue, the marker's original look.")]
        [SerializeField] private Color restingColor = Color.white;

        [SerializeField] private Color bleedingColor = new Color(0.95f, 0.05f, 0.05f, 1f);
        [SerializeField] private Color healedColor = new Color(0.25f, 0.85f, 0.35f, 0.55f);

        [Tooltip("How fast the bleeding pulse cycles, in hertz. Fast enough to read as alarm from " +
                 "across the stand, slow enough not to look like a rendering glitch.")]
        [SerializeField, Min(0.1f)] private float pulseHz = 1.6f;

        [Tooltip("Seconds the healed flash holds near-full brightness before settling to " +
                 "healedColor's own, quieter alpha.")]
        [SerializeField, Min(0.1f)] private float healedFadeSeconds = 1.2f;

        private bool _wasJoined;
        private float _healedElapsed;
        private float _pulseClock;

        /// <summary>What the ring is currently drawn as. Exposed so tests can assert on it without
        /// reading material state back off a renderer.</summary>
        public Color CurrentColor { get; private set; }

        private void Update() => Tick(Time.deltaTime);

        /// <summary>Recomputes and applies the ring colour. Stepped by hand in tests.</summary>
        public void Tick(float deltaTime)
        {
            if (site == null) { return; }

            if (site.IsJoined && !_wasJoined)
            {
                _wasJoined = true;
                _healedElapsed = 0f;
            }

            Color next;

            if (site.IsBleeding)
            {
                // Own clock, advanced only by deltaTime, rather than reading Time.time directly:
                // a test steps this with fixed increments and expects the same result every run,
                // which a wall-clock read would not give it.
                _pulseClock += deltaTime;

                // A sine pulse rather than a blink: a hard on/off read as a UI glitch at a stand,
                // a smooth pulse reads as "alarm" the way a real monitor's does.
                float pulse = 0.5f + 0.5f * Mathf.Sin(_pulseClock * pulseHz * Mathf.PI * 2f);
                next = Color.Lerp(bleedingColor * new Color(0.55f, 0.55f, 0.55f, 1f), bleedingColor, pulse);
                next.a = 1f;
            }
            else if (_wasJoined)
            {
                _pulseClock = 0f;
                _healedElapsed += deltaTime;
                float t = healedFadeSeconds <= 0f ? 1f : Mathf.Clamp01(_healedElapsed / healedFadeSeconds);
                Color flash = new Color(healedColor.r, healedColor.g, healedColor.b, 1f);
                next = Color.Lerp(flash, healedColor, t);
            }
            else
            {
                _pulseClock = 0f;
                next = restingColor;
            }

            CurrentColor = next;
            Apply(next);
        }

        private void Apply(Color colour)
        {
            if (ring != null && ring.sharedMaterial != null)
            {
                ring.sharedMaterial.color = colour;
            }
        }

        public void Bind(VesselAnastomosis vessel, Renderer cuffRenderer, Color rest)
        {
            site = vessel;
            ring = cuffRenderer;
            restingColor = rest;
            CurrentColor = rest;
        }
    }
}
