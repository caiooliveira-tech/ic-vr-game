using UnityEngine;

namespace VRSurgery.Session
{
    /// <summary>
    /// The booth loop as data: how long a visitor's turn lasts, how long each screen is held, and
    /// the real-world fact shown next to the score. The procedure itself stays in
    /// SurgeryDefinition — this describes only the event session wrapped around it, so whoever is
    /// running the stand can retune the pacing on the day without a rebuild.
    /// </summary>
    [CreateAssetMenu(fileName = "EventSessionDefinition", menuName = "VRSurgery/Data/Event Session Definition")]
    public class EventSessionDefinition : ScriptableObject
    {
        [Header("Timing (seconds)")]
        [Tooltip("Hard limit for one turn. The round is lost the moment this reaches zero.")]
        [SerializeField, Min(5f)] private float roundSeconds = 90f;

        [Tooltip("How long the briefing waits for the visitor to pick up an instrument before the " +
                 "booth gives up and goes back to attract mode. 0 disables the timeout.")]
        [SerializeField, Min(0f)] private float briefingTimeoutSeconds = 45f;

        [Tooltip("How long the success/failure screen is held before the scoreboard replaces it.")]
        [SerializeField, Min(0f)] private float resultHoldSeconds = 5f;

        [Tooltip("How long the scoreboard stays up between visitors.")]
        [SerializeField, Min(0f)] private float scoreboardHoldSeconds = 8f;

        [Header("Flow")]
        [Tooltip("Start the clock when the visitor picks up the first instrument. Off means the " +
                 "round only starts on an explicit StartRound() call from the operator.")]
        [SerializeField] private bool startOnFirstToolGrab = true;

        [Header("Score")]
        [Tooltip("Points awarded per second left on the clock when the bleeding is controlled.")]
        [SerializeField, Min(1f)] private float pointsPerSecondRemaining = 100f;

        [Header("Player-facing text (pt-BR)")]
        [Tooltip("Shown in the briefing and again on the result screen. PLACEHOLDER: replace with " +
                 "a figure the team can source before the event.")]
        [SerializeField, TextArea(2, 4)]
        private string educationalFact =
            "Em uma hemorragia grave, cada segundo conta: fazer pressão no ponto certo é uma das " +
            "primeiras ações da equipe cirúrgica.";

        public float RoundSeconds => roundSeconds;
        public float BriefingTimeoutSeconds => briefingTimeoutSeconds;
        public float ResultHoldSeconds => resultHoldSeconds;
        public float ScoreboardHoldSeconds => scoreboardHoldSeconds;
        public bool StartOnFirstToolGrab => startOnFirstToolGrab;
        public float PointsPerSecondRemaining => pointsPerSecondRemaining;
        public string EducationalFact => educationalFact;

        /// <summary>
        /// Builds a definition in memory. Used by the scene builder and by tests.
        ///
        /// <paramref name="fact"/> defaults to null rather than to the field's text so a caller
        /// that says nothing keeps whatever the field already holds. Each scene passes its own:
        /// the fact is about the procedure being performed, and a transplant explained with a
        /// sentence about controlling haemorrhage is worse than no sentence at all.
        /// </summary>
        public static EventSessionDefinition Create(
            float round = 90f,
            float briefingTimeout = 45f,
            float resultHold = 5f,
            float scoreboardHold = 8f,
            bool startOnGrab = true,
            float pointsPerSecond = 100f,
            string fact = null)
        {
            EventSessionDefinition definition = CreateInstance<EventSessionDefinition>();
            definition.name = "EventSession";
            definition.roundSeconds = round;
            definition.briefingTimeoutSeconds = briefingTimeout;
            definition.resultHoldSeconds = resultHold;
            definition.scoreboardHoldSeconds = scoreboardHold;
            definition.startOnFirstToolGrab = startOnGrab;
            definition.pointsPerSecondRemaining = pointsPerSecond;

            if (!string.IsNullOrWhiteSpace(fact))
            {
                definition.educationalFact = fact;
            }

            return definition;
        }
    }
}
