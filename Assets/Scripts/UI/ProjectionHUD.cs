using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using VRSurgery.Session;
using VRSurgery.Tissue;

namespace VRSurgery.Surgery
{
    /// <summary>
    /// The screen the crowd watches. Everything the concept asks the audience to see lives here:
    /// a clock big enough to read across a stand, a bleeding bar, colour that rises with the risk,
    /// the day's best time while nobody is playing, and the table between visitors.
    ///
    /// It owns no state of its own — every value is read from the session, the wound and the
    /// table each frame. That is deliberate: a stand screen that tracks the round separately is a
    /// screen that will eventually disagree with the round, in front of an audience.
    ///
    /// It draws to the projector's display through a Screen Space Overlay canvas rather than as
    /// geometry in the room, so none of it can ever appear inside the headset.
    /// </summary>
    public class ProjectionHUD : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private EventSessionController session;
        [SerializeField] private BleedingSystem bleeding;
        [SerializeField] private Leaderboard leaderboard;

        /// <summary>
        /// Alternative source for the bleed bar, for a scene with no BleedingSystem of its own —
        /// the transplant stand's risk is spread across several vessel sites, not one wound, so it
        /// hands in a function instead of a component. Takes priority over bleeding when both are
        /// set, which never happens outside a test.
        /// </summary>
        private Func<float> _bleedIntensity;

        /// <summary>
        /// The three lines that name what the round is about, not just how it is going. Defaults
        /// match what this HUD always said, back when it only ever ran on SurgeryMVP; a scene
        /// with a different first gesture (no scalpel to grab, say) overrides them via BindCopy
        /// instead of this file growing a second hardcoded truth per scene.
        /// </summary>
        [SerializeField] private string attractHeadline = "CONTROLE A HEMORRAGIA";
        [SerializeField] private string attractFallbackSubline = "Coloque o headset para começar";
        [SerializeField] private string briefingHeadline = "PEGUE O BISTURI";

        [Header("Always visible")]
        [SerializeField] private Text clockText;
        [SerializeField] private Image bleedFill;
        [SerializeField] private Image urgencyVignette;

        [Header("Message block")]
        [SerializeField] private Text headlineText;
        [SerializeField] private Text sublineText;

        [Header("Panels")]
        [SerializeField] private GameObject clockGroup;
        [SerializeField] private GameObject scoreboardGroup;
        [SerializeField] private Text scoreboardText;

        [Header("Colours")]
        [SerializeField] private Color bleedCalm = new Color(0.85f, 0.25f, 0.25f);
        [SerializeField] private Color bleedHeavy = new Color(1f, 0.15f, 0.12f);
        [SerializeField] private Color clockCalm = Color.white;
        [SerializeField] private Color clockUrgent = new Color(1f, 0.45f, 0.40f);

        [Tooltip("How opaque the red edge gets at a fully spent clock.")]
        [SerializeField, Range(0f, 1f)] private float vignettePeakAlpha = 0.38f;

        /// <summary>What the clock currently reads. Exposed so tests can assert on it.</summary>
        public string ClockLabel { get; private set; } = string.Empty;

        public string Headline { get; private set; } = string.Empty;

        /// <summary>Last whole second put on the clock; -1 forces the first frame to draw.</summary>
        private int _shownSecond = -1;

        private void OnEnable()
        {
            if (session != null)
            {
                session.StateChanged += HandleStateChanged;
            }

            if (leaderboard != null)
            {
                leaderboard.Changed += RefreshScoreboard;
            }

            RefreshScoreboard();
            ApplyState(session != null ? session.State : SessionState.Attract);
        }

        private void OnDisable()
        {
            if (session != null)
            {
                session.StateChanged -= HandleStateChanged;
            }

            if (leaderboard != null)
            {
                leaderboard.Changed -= RefreshScoreboard;
            }
        }

        private void Update()
        {
            if (session == null)
            {
                return;
            }

            // The state event covers the panel swap; these three move every frame within a state.
            UpdateClock();
            UpdateBleedBar();
            UpdateUrgency();
        }

        private void HandleStateChanged(SessionState state) => ApplyState(state);

        private void UpdateClock()
        {
            float remaining = session.IsRunning ? session.RemainingSeconds : session.RoundSeconds;

            // Ceil, not round: a clock that shows 0 while the round is still winnable is a lie the
            // audience can see, and one that shows 1 for half a second at the end is not.
            int whole = Mathf.CeilToInt(remaining);

            // Only rebuild the string when the digits actually change. Formatting every frame
            // allocated a new string ~90 times a second for a label that changes once.
            if (whole != _shownSecond)
            {
                _shownSecond = whole;
                ClockLabel = $"{whole / 60:0}:{whole % 60:00}";

                if (clockText != null)
                {
                    clockText.text = ClockLabel;
                }
            }

            if (clockText != null)
            {
                clockText.color = Color.Lerp(clockCalm, clockUrgent, session.Urgency01);
            }
        }

        private void UpdateBleedBar()
        {
            if (bleedFill == null)
            {
                return;
            }

            float amount = _bleedIntensity != null
                ? _bleedIntensity()
                : (bleeding != null ? bleeding.Intensity01 : 0f);
            bleedFill.fillAmount = amount;
            bleedFill.color = Color.Lerp(bleedCalm, bleedHeavy, amount);
        }

        private void UpdateUrgency()
        {
            if (urgencyVignette == null)
            {
                return;
            }

            float alpha = session.IsRunning ? session.Urgency01 * vignettePeakAlpha : 0f;
            Color colour = urgencyVignette.color;
            urgencyVignette.color = new Color(colour.r, colour.g, colour.b, alpha);
        }

        private void ApplyState(SessionState state)
        {
            bool showClock = state is SessionState.Briefing or SessionState.Running;
            bool showScoreboard = state == SessionState.Scoreboard;

            if (clockGroup != null) { clockGroup.SetActive(showClock); }
            if (scoreboardGroup != null) { scoreboardGroup.SetActive(showScoreboard); }

            switch (state)
            {
                case SessionState.Attract:
                    LeaderboardEntry? best = leaderboard != null ? leaderboard.Best : null;
                    SetMessage(
                        best.HasValue ? $"MELHOR TEMPO: {best.Value.Seconds:F1}s" : attractHeadline,
                        best.HasValue ? $"por {best.Value.Name} — consegue superar?" : attractFallbackSubline);
                    break;

                case SessionState.Briefing:
                    SetMessage(briefingHeadline, session.EducationalFact);
                    break;

                case SessionState.Running:
                    SetMessage(string.Empty, string.Empty);
                    break;

                case SessionState.Success:
                    SetMessage(
                        $"CONTROLADO EM {session.LastResult.ElapsedSeconds:F1}s",
                        $"{session.LastResult.Score} pontos");
                    break;

                case SessionState.Failure:
                    SetMessage("TEMPO ESGOTADO", session.EducationalFact);
                    break;

                case SessionState.Scoreboard:
                    SetMessage("MELHORES TEMPOS", "Próximo da fila!");
                    break;
            }
        }

        private void SetMessage(string headline, string subline)
        {
            Headline = headline;

            if (headlineText != null)
            {
                headlineText.text = headline;
                headlineText.gameObject.SetActive(!string.IsNullOrEmpty(headline));
            }

            if (sublineText != null)
            {
                sublineText.text = subline;
                sublineText.gameObject.SetActive(!string.IsNullOrEmpty(subline));
            }
        }

        private void RefreshScoreboard()
        {
            if (scoreboardText == null)
            {
                return;
            }

            if (leaderboard == null || !leaderboard.HasAny)
            {
                scoreboardText.text = "Seja o primeiro do dia.";
                return;
            }

            StringBuilder table = new StringBuilder();
            for (int i = 0; i < leaderboard.Entries.Count; i++)
            {
                LeaderboardEntry entry = leaderboard.Entries[i];
                table.AppendLine($"{i + 1}.  {entry.Name,-14} {entry.Seconds,5:F1}s");
            }

            scoreboardText.text = table.ToString();
        }

        public void Bind(EventSessionController controller, BleedingSystem wound, Leaderboard table)
        {
            session = controller;
            bleeding = wound;
            leaderboard = table;
        }

        /// <summary>Binds a bleed signal that is not a BleedingSystem, e.g. several vessel sites
        /// reduced to one severity number. Pass null to go back to reading BleedingSystem.</summary>
        public void BindBleedSource(Func<float> intensity01)
        {
            _bleedIntensity = intensity01;
        }

        /// <summary>Overrides the attract/briefing copy for a scene whose first gesture, or
        /// framing, is not "pegue o bisturi". Pass null for any line to keep its default.</summary>
        public void BindCopy(string attractHeadline = null, string attractFallbackSubline = null,
            string briefingHeadline = null)
        {
            if (attractHeadline != null) { this.attractHeadline = attractHeadline; }
            if (attractFallbackSubline != null) { this.attractFallbackSubline = attractFallbackSubline; }
            if (briefingHeadline != null) { this.briefingHeadline = briefingHeadline; }
        }

        public void BindWidgets(Text clock, Image bleed, Image vignette, Text headline, Text subline,
            GameObject clockPanel, GameObject scorePanel, Text scoreTable)
        {
            clockText = clock;
            bleedFill = bleed;
            urgencyVignette = vignette;
            headlineText = headline;
            sublineText = subline;
            clockGroup = clockPanel;
            scoreboardGroup = scorePanel;
            scoreboardText = scoreTable;
        }
    }
}
