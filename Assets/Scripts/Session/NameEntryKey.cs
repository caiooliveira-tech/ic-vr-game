using UnityEngine;

namespace VRSurgery.Session
{
    /// <summary>
    /// One physical key on the stand's name-entry keyboard: a letter, digit, backspace or
    /// confirm. The scene builder places one of these per key on the panel; what the key does is
    /// entirely described here, so <see cref="NameEntryWorker"/> does not need a switch statement
    /// per key — it just tells whichever key the tip is holding to act.
    /// </summary>
    public class NameEntryKey : MonoBehaviour
    {
        public enum KeyAction
        {
            /// <summary>Appends <see cref="Character"/> to the name being typed.</summary>
            Character,

            /// <summary>Removes the last typed character.</summary>
            Backspace,

            /// <summary>Files the name as typed so far.</summary>
            Confirm,
        }

        [SerializeField] private KeyAction action = KeyAction.Character;

        [Tooltip("Only used when Action is Character.")]
        [SerializeField] private char character = 'A';

        public KeyAction Action => action;

        public char Character => character;

        /// <summary>Sends this key's action to the controller. Confirm/Backspace ignore the char.</summary>
        public void Press(NameEntryController controller)
        {
            if (controller == null) { return; }

            switch (action)
            {
                case KeyAction.Character:
                    controller.AppendCharacter(character);
                    break;
                case KeyAction.Backspace:
                    controller.Backspace();
                    break;
                case KeyAction.Confirm:
                    controller.Confirm();
                    break;
            }
        }

        public void Bind(KeyAction keyAction, char keyCharacter = '\0')
        {
            action = keyAction;
            character = keyCharacter;
        }
    }
}
