using System;
using UnityEngine;

namespace VRSurgery.Session
{
    /// <summary>
    /// Lets the visitor who just finished a round type their name before it lands on the
    /// scoreboard, the way the concept describes it — the stand asks "quem é você?" the moment
    /// the bleeding is controlled, rather than filing every run as "Anônimo".
    ///
    /// Deliberately not part of <see cref="EventSessionController"/> or <see cref="Leaderboard"/>:
    /// neither of those needed to know that names exist, and both are already tested against the
    /// automatic "Success -> Scoreboard -> Attract" flow. This sits beside them instead, taking
    /// over the one thing <see cref="Leaderboard"/> used to do for itself — subscribing to
    /// <see cref="EventSessionController.RoundEnded"/> and filing the result — so a scene wires up
    /// either this controller or <c>Leaderboard.Bind(session)</c>, never both, or the same round
    /// would be filed twice.
    ///
    /// Runs on its own clock rather than the session's: the session moves on to the scoreboard a
    /// few seconds after a win regardless, because that pacing is what the rest of the stand
    /// (projection, HUD) already relies on. A visitor who is still typing when that happens is not
    /// interrupted — the entry stays open until they confirm or the entry timeout elapses, and the
    /// run is filed exactly once, whenever that happens.
    /// </summary>
    public class NameEntryController : MonoBehaviour
    {
        private const int DefaultMaxNameLength = 12;
        private const float DefaultTimeoutSeconds = 20f;

        [SerializeField] private EventSessionController session;
        [SerializeField] private Leaderboard leaderboard;

        [Tooltip("Longest name the scoreboard has room to print.")]
        [SerializeField, Min(1)] private int maxNameLength = DefaultMaxNameLength;

        [Tooltip("Seconds a visitor gets to type before the run is filed under the fallback name " +
                 "anyway. A stand cannot let one slow typist hold the queue.")]
        [SerializeField, Min(0f)] private float timeoutSeconds = DefaultTimeoutSeconds;

        [Tooltip("Filed when the visitor confirms nothing, or lets the timeout run out.")]
        [SerializeField] private string fallbackName = "Anônimo";

        private SessionResult? _pending;
        private string _typed = string.Empty;
        private float _elapsedSinceOffered;

        /// <summary>True while a just-finished run is waiting on a name.</summary>
        public bool IsAwaitingName => _pending.HasValue;

        /// <summary>What the visitor has typed so far. Empty until they press a first key.</summary>
        public string TypedName => _typed;

        /// <summary>Seconds left before the entry auto-confirms. Only meaningful while awaiting.</summary>
        public float RemainingSeconds =>
            _pending.HasValue ? Mathf.Max(0f, timeoutSeconds - _elapsedSinceOffered) : 0f;

        /// <summary>Raised when a finished run starts waiting on a name.</summary>
        public event Action NameEntryOpened;

        /// <summary>Raised once the pending run has been filed, one way or another.</summary>
        public event Action NameEntryClosed;

        private void OnEnable()
        {
            if (session != null)
            {
                session.RoundEnded += HandleRoundEnded;
            }
        }

        private void OnDisable()
        {
            if (session != null)
            {
                session.RoundEnded -= HandleRoundEnded;
            }
        }

        private void Update() => Tick(Time.deltaTime);

        /// <summary>Advances the entry timeout. Stepped by hand in tests.</summary>
        public void Tick(float deltaTime)
        {
            if (!_pending.HasValue || deltaTime <= 0f)
            {
                return;
            }

            _elapsedSinceOffered += deltaTime;

            if (timeoutSeconds > 0f && _elapsedSinceOffered >= timeoutSeconds)
            {
                Confirm();
            }
        }

        /// <summary>Appends one character, up to the name's length limit.</summary>
        public void AppendCharacter(char character)
        {
            if (!_pending.HasValue || _typed.Length >= maxNameLength)
            {
                return;
            }

            if (!char.IsLetterOrDigit(character) && character != ' ')
            {
                return;
            }

            _typed += character;
        }

        /// <summary>Removes the last typed character, if any.</summary>
        public void Backspace()
        {
            if (!_pending.HasValue || _typed.Length == 0)
            {
                return;
            }

            _typed = _typed.Substring(0, _typed.Length - 1);
        }

        /// <summary>
        /// Files the pending run under whatever was typed, or under <see cref="fallbackName"/> when
        /// nothing was. Safe to call with nothing pending — a stray Confirm key press is a no-op.
        /// </summary>
        public void Confirm()
        {
            if (!_pending.HasValue)
            {
                return;
            }

            SessionResult result = _pending.Value;
            string name = string.IsNullOrWhiteSpace(_typed) ? fallbackName : _typed.Trim();

            _pending = null;
            _typed = string.Empty;
            _elapsedSinceOffered = 0f;

            if (leaderboard != null)
            {
                leaderboard.Submit(name, result);
            }

            NameEntryClosed?.Invoke();
        }

        public void Bind(EventSessionController controller, Leaderboard board)
        {
            if (session != null)
            {
                session.RoundEnded -= HandleRoundEnded;
            }

            session = controller;
            leaderboard = board;

            if (session != null && isActiveAndEnabled)
            {
                session.RoundEnded += HandleRoundEnded;
            }
        }

        private void HandleRoundEnded(SessionResult result)
        {
            if (!result.Succeeded)
            {
                return;
            }

            _pending = result;
            _typed = string.Empty;
            _elapsedSinceOffered = 0f;

            NameEntryOpened?.Invoke();
        }
    }
}
