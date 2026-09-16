using System.Collections;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VRSurgery.Diagnostics;
using VRSurgery.Interaction;
using VRSurgery.Surgery;
using VRSurgery.Transplant;

namespace VRSurgery.Tests
{
    /// <summary>
    /// Ergonomics of the transplant workstation.
    ///
    /// WorkspaceErgonomicsTests already measures this for the MVP scene, and only for things
    /// carrying a SurgicalInteractable. Neither covers the scene actually in development, and
    /// neither covers what the transplant is mostly made of: bare interaction points — four
    /// bypass sites, five anastomoses and the sternal midline — that the visitor works with their
    /// hands and that no instrument component ever touches.
    ///
    /// Zero locomotion is a design pillar, so layout is a correctness problem rather than an
    /// aesthetic one: anything the procedure requires that falls outside the arm envelope, or
    /// behind the shoulder line, is a defect.
    ///
    /// Measured from both shoulders on purpose. Left-handers and people with shorter arms are
    /// roughly half the queue, and a layout that only answers to the dominant hand fails them
    /// silently — they just find it hard and assume that is how VR feels.
    /// </summary>
    public class TransplantErgonomicsTests
    {
        private const string SceneName = "TransplanteCardiaco";

        /// <summary>
        /// The limit the project already enforces on the MVP scene, for the same reason: past
        /// ninety degrees the visitor is turning their torso, not their eyes, and anything
        /// essential out there is found by accident or not at all.
        /// </summary>
        private const float GazeLimitDegrees = 90f;

        private Camera _camera;
        private Vector3 _eye;
        private Quaternion _head;
        private Vector3 _leftShoulder;
        private Vector3 _rightShoulder;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return HeadlessScene.Load(SceneName);

            // The player's head specifically. Measuring from any other camera in the room produces
            // confident and entirely meaningless numbers.
            _camera = Camera.main;
            Assert.IsNotNull(_camera,
                "Sem câmera marcada MainCamera — não dá para localizar a cabeça do jogador.");

            _eye = _camera.transform.position;
            _head = _camera.transform.rotation;
            _leftShoulder = ReachEnvelope.ShoulderPosition(_eye, _head, true);
            _rightShoulder = ReachEnvelope.ShoulderPosition(_eye, _head, false);
        }

        [TearDown]
        public void TearDown() => SurgeryEvents.ResetAll();

        /// <summary>
        /// Everything the visitor has to put a hand on, instruments and bare sites alike.
        ///
        /// Collected by component where a component exists and by hierarchy where one does not —
        /// the bypass sites live in a private list inside the worker, and reaching into it would
        /// couple this test to that class's internals rather than to the scene's layout.
        /// </summary>
        private List<(string name, Vector3 point)> InteractionPoints()
        {
            List<(string, Vector3)> points = new List<(string, Vector3)>();

            // Inativos incluídos: o coração nativo só é ligado quando o tórax abre, e um órgão que
            // o visitante terá que alcançar não deixa de contar por ainda estar escondido.
            foreach (SurgicalInteractable tool in
                     Object.FindObjectsByType<SurgicalInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                points.Add((tool.name,
                    tool.GripPoint != null ? tool.GripPoint.position : tool.transform.position));
            }

            foreach (VesselAnastomosis vessel in
                     Object.FindObjectsByType<VesselAnastomosis>(FindObjectsSortMode.None))
            {
                points.Add((vessel.name, vessel.transform.position));
            }

            GameObject sites = GameObject.Find("BypassSites");
            if (sites != null)
            {
                foreach (Transform site in sites.transform)
                {
                    points.Add((site.name, site.position));
                }
            }

            GameObject sternal = GameObject.Find("SternalMidline");
            if (sternal != null)
            {
                points.Add((sternal.name, sternal.transform.position));
            }

            return points;
        }

        [UnityTest]
        public IEnumerator EveryInteractionPoint_IsReachableWithEitherHand()
        {
            List<(string name, Vector3 point)> points = InteractionPoints();
            Assert.Greater(points.Count, 0, "Nenhum ponto de interação na cena.");

            StringBuilder report = new StringBuilder();
            List<string> failures = new List<string>();

            foreach ((string name, Vector3 point) in points)
            {
                float fromLeft = Vector3.Distance(_leftShoulder, point);
                float fromRight = Vector3.Distance(_rightShoulder, point);

                report.AppendLine($"  {name}: esquerda {fromLeft:F3} m [{ReachEnvelope.Classify(fromLeft)}], " +
                                  $"direita {fromRight:F3} m [{ReachEnvelope.Classify(fromRight)}]");

                foreach ((float distance, string hand) in new[] { (fromLeft, "esquerda"), (fromRight, "direita") })
                {
                    ReachClass reach = ReachEnvelope.Classify(distance);

                    if (reach == ReachClass.OutOfReach)
                    {
                        failures.Add($"'{name}' está a {distance:F3} m do ombro {hand} — " +
                                     "o visitante teria que dar um passo, o que o design proíbe.");
                    }
                    else if (reach == ReachClass.Strained)
                    {
                        failures.Add($"'{name}' está a {distance:F3} m do ombro {hand}, na faixa " +
                                     "de esforço. Quem for canhoto ou tiver braço mais curto " +
                                     "trabalharia em extensão máxima a sessão inteira.");
                    }
                }
            }

            Debug.Log("[Ergonomia] alcance pelos dois ombros:\n" + report);

            // Todas as falhas de uma vez: corrigir um layout uma medida por execução custa uma
            // rodada de testes por ponto.
            Assert.IsEmpty(failures, string.Join("\n", failures));
            yield break;
        }

        [UnityTest]
        public IEnumerator NothingEssential_SitsBeyondNinetyDegreesOfGaze()
        {
            List<(string name, Vector3 point)> points = InteractionPoints();
            Assert.Greater(points.Count, 0, "Nenhum ponto de interação na cena.");

            StringBuilder report = new StringBuilder();
            List<string> failures = new List<string>();

            foreach ((string name, Vector3 point) in points)
            {
                float yaw = Mathf.Abs(ReachEnvelope.GazeYawDegrees(_eye, _head, point));
                report.AppendLine($"  {name}: {yaw:F0}° fora do eixo");

                if (yaw >= GazeLimitDegrees)
                {
                    failures.Add($"'{name}' está a {yaw:F0}° do eixo do olhar, além do limite de " +
                                 $"{GazeLimitDegrees:F0}°. Passado isso o visitante gira o tronco, " +
                                 "não os olhos, e um leigo de primeira viagem simplesmente não " +
                                 "encontra o que está lá.");
                }
            }

            Debug.Log("[Ergonomia] desvio do eixo do olhar:\n" + report);

            Assert.IsEmpty(failures, string.Join("\n", failures));
            yield break;
        }
    }
}
