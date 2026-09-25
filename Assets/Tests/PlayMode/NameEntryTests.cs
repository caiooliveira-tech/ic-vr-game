using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VRSurgery.Session;
using VRSurgery.Surgery;

namespace VRSurgery.Tests
{
    /// <summary>
    /// The name-entry keyboard: a win should ask "quem é você?" instead of filing the run under
    /// "Anônimo" straight away, and the visitor typing must not be able to double-file a round or
    /// bypass the stand's own pacing.
    /// </summary>
    public class NameEntryTests
    {
        private const float Round = 60f;
        private const float ResultHold = 4f;
        private const float ScoreboardHold = 6f;

        private GameObject _host;
        private EventSessionController _session;
        private EventSessionDefinition _definition;
        private Leaderboard _leaderboard;
        private NameEntryController _nameEntry;

        [SetUp]
        public void SetUp()
        {
            SurgeryEvents.ResetAll();

            _definition = EventSessionDefinition.Create(Round, 20f, ResultHold, ScoreboardHold, true, 100f);

            _host = new GameObject("EventSession");
            _host.SetActive(false);
            _session = _host.AddComponent<EventSessionController>();
            _session.Bind(_definition, null);
            _leaderboard = _host.AddComponent<Leaderboard>();
            _nameEntry = _host.AddComponent<NameEntryController>();
            _nameEntry.Bind(_session, _leaderboard);
            _host.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) { Object.DestroyImmediate(_host); }
            if (_definition != null) { Object.DestroyImmediate(_definition); }
            SurgeryEvents.ResetAll();
        }

        private void WinRound(float elapsedSeconds)
        {
            _session.BeginSession();
            _session.StartRound();
            Step(elapsedSeconds);
            SurgeryEvents.RaiseSurgeryCompleted();
        }

        [Test]
        public void WinningARoundOpensNameEntryInsteadOfFilingAnonymously()
        {
            WinRound(20f);

            Assert.IsTrue(_nameEntry.IsAwaitingName,
                "A win must wait for a name instead of filing the run by itself.");
            Assert.IsFalse(_leaderboard.HasAny,
                "Nothing should reach the scoreboard before the visitor confirms a name.");
        }

        [Test]
        public void LosingARoundNeverOpensNameEntry()
        {
            _session.BeginSession();
            _session.StartRound();
            Step(Round + 0.5f);

            Assert.IsFalse(_nameEntry.IsAwaitingName,
                "A loss has no time to rank, so there is nothing to name.");
            Assert.IsFalse(_leaderboard.HasAny);
        }

        [Test]
        public void TypingAndConfirmingFilesTheTypedName()
        {
            WinRound(20f);

            foreach (char c in "ANA")
            {
                _nameEntry.AppendCharacter(c);
            }
            _nameEntry.Confirm();

            Assert.IsFalse(_nameEntry.IsAwaitingName);
            Assert.IsTrue(_leaderboard.HasAny);
            Assert.AreEqual("ANA", _leaderboard.Entries[0].Name);
        }

        [Test]
        public void BackspaceRemovesTheLastTypedCharacter()
        {
            WinRound(20f);

            _nameEntry.AppendCharacter('A');
            _nameEntry.AppendCharacter('N');
            _nameEntry.AppendCharacter('X');
            _nameEntry.Backspace();
            _nameEntry.AppendCharacter('A');
            _nameEntry.Confirm();

            Assert.AreEqual("ANA", _leaderboard.Entries[0].Name);
        }

        [Test]
        public void ConfirmingWithNothingTypedFallsBackToAnonimo()
        {
            WinRound(20f);

            _nameEntry.Confirm();

            Assert.AreEqual("Anônimo", _leaderboard.Entries[0].Name);
        }

        [Test]
        public void ARoundThatFinishesTheSessionIsStillFiledExactlyOnce()
        {
            WinRound(20f);

            // The session moves on to the scoreboard and back to attract on its own clock while
            // the visitor is still typing; that pacing must not file the round a second time.
            Step(ResultHold + ScoreboardHold + 1f);

            _nameEntry.AppendCharacter('A');
            _nameEntry.Confirm();

            Assert.AreEqual(1, _leaderboard.Entries.Count,
                "The session's own pacing must not file the round a second time.");
        }

        [Test]
        public void ASlowTypistIsAutoConfirmedByTheEntryTimeout()
        {
            WinRound(20f);

            _nameEntry.AppendCharacter('A');
            _nameEntry.AppendCharacter('N');

            // Longer than the controller's own entry timeout (20s default), stepped through the
            // controller itself rather than the session, since the two clocks are independent.
            for (int i = 0; i < 100; i++) { _nameEntry.Tick(0.25f); }

            Assert.IsFalse(_nameEntry.IsAwaitingName,
                "A stand cannot let one slow typist hold the queue forever.");
            Assert.AreEqual("AN", _leaderboard.Entries[0].Name);
        }

        [Test]
        public void TheKeyboardTypesByHoldingAKeyLikeEveryOtherSiteInTheGame()
        {
            WinRound(20f);

            GameObject tipHost = new GameObject("Tip");
            GameObject keyHost = new GameObject("KeyA");
            keyHost.transform.position = new Vector3(0.1f, 0f, 0f);
            NameEntryKey key = keyHost.AddComponent<NameEntryKey>();
            key.Bind(NameEntryKey.KeyAction.Character, 'A');

            NameEntryWorker worker = tipHost.AddComponent<NameEntryWorker>();
            worker.Bind(tipHost.transform, new List<NameEntryKey> { key }, _nameEntry);

            tipHost.transform.position = keyHost.transform.position;

            // A single frame of contact must not type a letter — the same "held, not tapped"
            // rule every other site in the game follows.
            worker.Tick(1f / 60f);
            Assert.AreEqual(string.Empty, _nameEntry.TypedName);

            for (int i = 0; i < 60; i++) { worker.Tick(1f / 60f); }
            Assert.AreEqual("A", _nameEntry.TypedName,
                "Holding the tip on the key long enough must type exactly one letter.");

            // Still resting on the same key: it must not repeat like a held button would.
            for (int i = 0; i < 60; i++) { worker.Tick(1f / 60f); }
            Assert.AreEqual("A", _nameEntry.TypedName,
                "A key must fire once per visit, not once per frame it stays held.");

            // Leaving and coming back arms it again.
            tipHost.transform.position += new Vector3(0.3f, 0f, 0f);
            worker.Tick(1f / 60f);
            tipHost.transform.position = keyHost.transform.position;
            for (int i = 0; i < 60; i++) { worker.Tick(1f / 60f); }
            Assert.AreEqual("AA", _nameEntry.TypedName);

            Object.DestroyImmediate(tipHost);
            Object.DestroyImmediate(keyHost);
        }

        /// <summary>Feeds the session fixed steps. 0.25 is exact in binary, so the clock does not drift.</summary>
        private void Step(float seconds)
        {
            const float stepSize = 0.25f;
            int steps = Mathf.CeilToInt(seconds / stepSize);
            for (int i = 0; i < steps; i++)
            {
                _session.Tick(stepSize);
                _nameEntry.Tick(stepSize);
            }
        }
    }
}
