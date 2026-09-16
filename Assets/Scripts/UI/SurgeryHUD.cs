using UnityEngine;

namespace VRSurgery.Surgery
{
    /// <summary>
    /// Drives the in-room objective monitor. Deliberately minimal and diegetic: the text lives
    /// on a screen in the operating room, never floating in front of the player's eyes.
    /// </summary>
    public class SurgeryHUD : MonoBehaviour
    {
        [SerializeField] private SurgeryObjectiveSystem objectiveSystem;
        [SerializeField] private TextMesh objectiveText;

        public string CurrentText { get; private set; } = string.Empty;

        private void OnEnable()
        {
            if (objectiveSystem != null)
            {
                objectiveSystem.ObjectiveActivated += HandleObjectiveChanged;
                objectiveSystem.ObjectiveCompleted += HandleObjectiveChanged;
            }

            SurgeryEvents.OnSurgeryCompleted += HandleSurgeryCompleted;
            Refresh();
        }

        private void OnDisable()
        {
            if (objectiveSystem != null)
            {
                objectiveSystem.ObjectiveActivated -= HandleObjectiveChanged;
                objectiveSystem.ObjectiveCompleted -= HandleObjectiveChanged;
            }

            SurgeryEvents.OnSurgeryCompleted -= HandleSurgeryCompleted;
        }

        public void Bind(SurgeryObjectiveSystem system, TextMesh text)
        {
            objectiveSystem = system;
            objectiveText = text;
        }

        private void HandleObjectiveChanged(SurgeryObjectiveSystem.ObjectiveRuntime objective) => Refresh();

        private void HandleSurgeryCompleted() => SetText("PROCEDIMENTO CONCLUÍDO");

        private void Refresh()
        {
            if (objectiveSystem == null)
            {
                SetText("AGUARDANDO");
                return;
            }

            SurgeryObjectiveSystem.ObjectiveRuntime active = objectiveSystem.ActiveObjective;
            if (active == null)
            {
                // Eram "PROCEDURE COMPLETE" e "STANDBY": as duas únicas frases em inglês que
                // chegavam ao monitor da sala. O estande é no Brasil e o público é leigo.
                // "PROCEDIMENTO CONCLUÍDO" repete a linha 47 de propósito — os dois caminhos
                // dizem a mesma coisa ao jogador e devem ler igual.
                SetText(objectiveSystem.IsSurgeryComplete ? "PROCEDIMENTO CONCLUÍDO" : "AGUARDANDO");
                return;
            }

            int step = objectiveSystem.CompletedCount + 1;
            int total = objectiveSystem.Objectives.Count;
            SetText($"ETAPA {step}/{total}\n\n{active.Description}");
        }

        private void SetText(string value)
        {
            CurrentText = value;
            if (objectiveText != null)
            {
                objectiveText.text = value;
            }
        }
    }
}
