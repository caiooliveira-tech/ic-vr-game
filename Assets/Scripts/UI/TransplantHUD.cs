using System.Text;
using UnityEngine;
using VRSurgery.Session;
using VRSurgery.Transplant;

namespace VRSurgery.Surgery
{
    /// <summary>
    /// The monitor in the operating room, for the person wearing the headset.
    ///
    /// Everything on it was already being computed and thrown away. TransplantProcedure knew what
    /// the visitor should be doing, BypassProcedure knew exactly why each wrong move was wrong,
    /// and the session knew how much clock was left — and none of it reached a surface anyone
    /// could read. A refusal that fails silently teaches nothing, which is the one thing this
    /// project says it is for.
    ///
    /// A screen in the room, never a panel welded to the player's face: the same rule the rest of
    /// the UI follows, and the reason this reads from TextMesh in world space rather than an
    /// overlay canvas.
    ///
    /// It owns no state beyond how long a refusal has been up. Every other value is read from the
    /// procedure and the session each frame, because a monitor that tracks the operation
    /// separately is a monitor that will eventually disagree with it, in front of an audience.
    /// </summary>
    public class TransplantHUD : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private TransplantProcedure procedure;
        [SerializeField] private EventSessionController session;
        [SerializeField] private BypassWorker bypassWorker;
        [SerializeField] private SternotomyWorker sternotomyWorker;
        [SerializeField] private AnastomosisWorker anastomosisWorker;

        [Tooltip("Optional. While set and awaiting a name, the monitor prompts for it instead of " +
                 "just announcing the win.")]
        [SerializeField] private NameEntryController nameEntry;

        [Header("Screen")]
        [SerializeField] private TextMesh instructionText;
        [SerializeField] private TextMesh clockText;
        [SerializeField] private TextMesh reasonText;

        [Header("Behaviour")]
        [Tooltip("How long a refused step stays on the screen. Long enough to read a sentence " +
                 "aloud, short enough that it is gone before the next mistake.")]
        [SerializeField, Min(0.5f)] private float reasonHoldSeconds = 5f;

        [Tooltip("Characters per line before the text is wrapped. TextMesh does not wrap on its " +
                 "own: a refusal is a full sentence, and unwrapped it runs off both edges of the " +
                 "panel and off the screen behind it. Sized to the narrowest line on the monitor.")]
        [SerializeField, Min(8)] private int maxLineCharacters = 38;

        [Tooltip("Clock turns this colour as the round runs out.")]
        [SerializeField] private Color clockCalm = new Color(0.82f, 0.88f, 0.95f);

        [SerializeField] private Color clockUrgent = new Color(1f, 0.45f, 0.40f);

        [SerializeField] private Color reasonColour = new Color(1f, 0.62f, 0.35f);

        private float _reasonElapsed;

        /// <summary>What the instruction line reads. Exposed so tests can assert on it.</summary>
        public string Instruction { get; private set; } = string.Empty;

        /// <summary>What the clock reads, as shown. Empty when the round is not running.</summary>
        public string ClockLabel { get; private set; } = string.Empty;

        /// <summary>The refusal currently on screen, or empty.</summary>
        public string Reason { get; private set; } = string.Empty;

        private void OnEnable()
        {
            if (bypassWorker != null) { bypassWorker.StepRefused += ShowReason; }
            if (bypassWorker != null) { bypassWorker.StepPerformed += ClearReason; }

            Refresh(0f);
        }

        private void OnDisable()
        {
            if (bypassWorker != null) { bypassWorker.StepRefused -= ShowReason; }
            if (bypassWorker != null) { bypassWorker.StepPerformed -= ClearReason; }
        }

        private void Update() => Refresh(Time.deltaTime);

        /// <summary>Redraws the monitor. Stepped by hand in tests.</summary>
        public void Refresh(float deltaTime)
        {
            AgeReason(deltaTime);

            Instruction = BuildInstruction();
            ClockLabel = BuildClock();

            SetText(instructionText, Wrap(Instruction));
            SetText(clockText, ClockLabel);
            SetText(reasonText, Wrap(Reason));

            if (clockText != null)
            {
                clockText.color = session != null
                    ? Color.Lerp(clockCalm, clockUrgent, session.Urgency01)
                    : clockCalm;
            }

            if (reasonText != null) { reasonText.color = reasonColour; }
        }

        /// <summary>
        /// What the room is telling the visitor. The session's own states take precedence over the
        /// procedure's, because between visitors there is no surgeon to instruct — there is a
        /// queue to invite.
        /// </summary>
        private string BuildInstruction()
        {
            if (session != null)
            {
                switch (session.State)
                {
                    case SessionState.Attract:
                        return "TRANSPLANTE DE CORAÇÃO\nColoque o visor para começar";

                    case SessionState.Briefing:
                        return procedure != null
                            ? procedure.Briefing + "\n\nAbra o tórax para começar"
                            : "Abra o tórax para começar";

                    case SessionState.Success:
                        if (nameEntry != null && nameEntry.IsAwaitingName)
                        {
                            string typed = nameEntry.TypedName;
                            string shown = string.IsNullOrEmpty(typed) ? "_" : typed;
                            return "CORAÇÃO BATENDO\nDigite seu nome no teclado:\n" + shown;
                        }

                        return "CORAÇÃO BATENDO\nTransplante concluído";

                    case SessionState.Failure:
                        return "TEMPO ESGOTADO";

                    case SessionState.Scoreboard:
                        return "MELHORES TEMPOS";
                }
            }

            // A join held too unsteady is not a refusal — nothing was rejected, the vessel is
            // simply leaking now. It still needs its own line, or the visitor sees the vessel
            // count stop climbing with no idea why.
            VesselAnastomosis activeSite = anastomosisWorker != null ? anastomosisWorker.ActiveSite : null;
            if (activeSite != null && activeSite.IsBleeding)
            {
                return $"SANGRAMENTO NA {activeSite.DisplayName.ToUpperInvariant()}\n" +
                       "Mantenha o instrumento no ponto para estancar";
            }

            return procedure != null ? procedure.CurrentInstruction : "Aguardando";
        }

        /// <summary>
        /// Minutes and seconds. Only while the round is running: a clock frozen at its full value
        /// during the briefing reads as a clock already counting, and rushes people who still have
        /// the headset in their hands.
        /// </summary>
        private string BuildClock()
        {
            if (session == null || session.State != SessionState.Running)
            {
                return string.Empty;
            }

            int total = Mathf.CeilToInt(Mathf.Max(0f, session.RemainingSeconds));
            return $"{total / 60:0}:{total % 60:00}";
        }

        private void ShowReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) { return; }

            Reason = reason;
            _reasonElapsed = 0f;
        }

        private void ClearReason(BypassStep step)
        {
            Reason = string.Empty;
            _reasonElapsed = 0f;
        }

        private void AgeReason(float deltaTime)
        {
            if (string.IsNullOrEmpty(Reason) || deltaTime <= 0f) { return; }

            _reasonElapsed += deltaTime;
            if (_reasonElapsed >= reasonHoldSeconds)
            {
                Reason = string.Empty;
                _reasonElapsed = 0f;
            }
        }

        private static void SetText(TextMesh target, string value)
        {
            if (target != null && target.text != value) { target.text = value; }
        }

        /// <summary>
        /// Breaks a line so it fits the panel.
        ///
        /// TextMesh does no wrapping of its own, and the refusals are full sentences: on screen
        /// they ran off both edges of the monitor and out into the room behind it. The properties
        /// stay unwrapped, so what the HUD reports is the sentence and not its layout.
        ///
        /// Line breaks already in the text are kept — the briefing chooses its own.
        /// </summary>
        private string Wrap(string value)
        {
            if (string.IsNullOrEmpty(value) || maxLineCharacters <= 0) { return value; }

            StringBuilder builder = new StringBuilder(value.Length + 16);
            string[] paragraphs = value.Split('\n');

            for (int p = 0; p < paragraphs.Length; p++)
            {
                if (p > 0) { builder.Append('\n'); }

                int lineLength = 0;
                string[] words = paragraphs[p].Split(' ');

                for (int w = 0; w < words.Length; w++)
                {
                    string word = words[w];
                    if (word.Length == 0) { continue; }

                    if (lineLength > 0 && lineLength + 1 + word.Length > maxLineCharacters)
                    {
                        builder.Append('\n');
                        lineLength = 0;
                    }
                    else if (lineLength > 0)
                    {
                        builder.Append(' ');
                        lineLength++;
                    }

                    builder.Append(word);
                    lineLength += word.Length;
                }
            }

            return builder.ToString();
        }

        public void Bind(
            TransplantProcedure transplant,
            EventSessionController controller,
            BypassWorker worker,
            SternotomyWorker sternotomy,
            TextMesh instruction,
            TextMesh clock,
            TextMesh reason,
            AnastomosisWorker anastomosis = null,
            NameEntryController nameEntryController = null)
        {
            if (bypassWorker != null)
            {
                bypassWorker.StepRefused -= ShowReason;
                bypassWorker.StepPerformed -= ClearReason;
            }

            procedure = transplant;
            session = controller;
            bypassWorker = worker;
            sternotomyWorker = sternotomy;
            anastomosisWorker = anastomosis;
            nameEntry = nameEntryController;
            instructionText = instruction;
            clockText = clock;
            reasonText = reason;

            if (bypassWorker != null && isActiveAndEnabled)
            {
                bypassWorker.StepRefused += ShowReason;
                bypassWorker.StepPerformed += ClearReason;
            }
        }
    }
}
