using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using VRSurgery.Interaction;
using VRSurgery.Surgery;
using VRSurgery.Transplant;

namespace VRSurgery.Tests
{
    /// <summary>
    /// The join between the rig's hands and the project's grab contract.
    ///
    /// This is the test that should have existed already. The scene shipped with no
    /// XRHandInteractor on it at all — the adapter that turns XRI's selectEntered/selectExited
    /// into SurgicalInteractable.OnGrabbed/OnReleased — and nothing caught it, because every
    /// other test either drives the procedure's stages directly or checks layout. The result was
    /// a transplant that could not be completed with hands: IsHeld never became true, so the
    /// native heart's Released event never fired and RemoveNativeHeart was a dead end, while the
    /// donor heart counted as implanted the moment it drifted near the seat, still in the fist
    /// holding it.
    ///
    /// So these drive the real scene through XRI's own events rather than calling the contract
    /// directly. Calling TryGrab would pass with the adapter deleted, which is precisely the
    /// failure being guarded against.
    /// </summary>
    public class HandGrabWiringTests
    {
        private const string SceneName = "TransplanteCardiaco";

        private TransplantProcedure _procedure;
        private GameObject _nativeHeart;
        private GameObject _donorHeart;
        private Transform _seat;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return HeadlessScene.Load(SceneName);

            _procedure = Object.FindFirstObjectByType<TransplantProcedure>();
            Assert.IsNotNull(_procedure, "Sem TransplantProcedure na cena.");

            // Inativos incluídos de propósito: o SternotomyController desliga o coração nativo no
            // Awake, porque ele só aparece quando o tórax abre. GameObject.Find não enxerga
            // objeto inativo, e procurar por nome aqui faria o teste falhar pelo motivo errado.
            foreach (GrabbableOrgan organ in
                     Object.FindObjectsByType<GrabbableOrgan>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (organ.name == "Heart") { _nativeHeart = organ.gameObject; }
                if (organ.name == "DonorHeart") { _donorHeart = organ.gameObject; }
            }

            _seat = GameObject.Find("PericardialSeat").transform;

            Assert.IsNotNull(_nativeHeart, "Sem coração nativo na cena.");
            Assert.IsNotNull(_donorHeart, "Sem coração doador na cena.");

            yield return null;
        }

        [TearDown]
        public void TearDown() => SurgeryEvents.ResetAll();

        /// <summary>
        /// The rig's grab interactors, which is where the adapter has to live: it requires an
        /// XRBaseInteractor on its own GameObject.
        ///
        /// Inativos incluídos, e não por comodidade: o XRInputModalityManager do rig desliga os
        /// controles quando não detecta dispositivo XR, que é sempre o caso numa suíte headless.
        /// Procurar só entre ativos devolve zero e acusa a cena de não ter o componente que ela
        /// tem — foi exatamente esse o falso negativo que este teste deu na primeira execução.
        /// </summary>
        private static XRHandInteractor[] Hands() =>
            Object.FindObjectsByType<XRHandInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // NOTA: houve aqui um PowerUpHands() que ligava os GameObjects dos controles para que o
        // OnEnable do adaptador assinasse os eventos da XRI. Ele TRAVA a suíte inteira: ativar o
        // Near-Far Interactor sem dispositivo XR presente deixa a inicialização da toolkit
        // esperando indefinidamente, e o test runner para em
        // BothHandsCarryTheProjectsGrabContract sem nunca terminar.
        //
        // Removido porque uma suíte que trava é pior que uma que falha: a que falha ainda diz o
        // que está errado e deixa o resto rodar. Consequência assumida — os três testes de
        // comportamento abaixo falham em ambiente headless, porque os eventos são levantados e
        // não há ninguém inscrito. O que continua verificado de verdade é
        // BothHandsCarryTheProjectsGrabContract, que prova que o adaptador está na cena.
        //
        // Verificar o comportamento de agarre exige dispositivo XR presente, o que significa
        // rodar no óculos. Isso está registrado no relatório como não verificado.

        /// <summary>
        /// Raises XRI's own select event on the interactor, exactly as the toolkit does when a
        /// real hand closes on something. Going through the event is the whole point — it is the
        /// wiring under test, not the contract underneath it.
        /// </summary>
        private static void RaiseSelectEntered(XRHandInteractor hand, XRGrabInteractable target)
        {
            XRBaseInteractor interactor = hand.GetComponent<XRBaseInteractor>();
            interactor.selectEntered.Invoke(new SelectEnterEventArgs
            {
                interactorObject = interactor as IXRSelectInteractor,
                interactableObject = target,
            });
        }

        private static void RaiseSelectExited(XRHandInteractor hand, XRGrabInteractable target)
        {
            XRBaseInteractor interactor = hand.GetComponent<XRBaseInteractor>();
            interactor.selectExited.Invoke(new SelectExitEventArgs
            {
                interactorObject = interactor as IXRSelectInteractor,
                interactableObject = target,
            });
        }

        /// <summary>Walks the operation to the stage where the native heart may come out.</summary>
        private void AdvanceToExplant()
        {
            _procedure.Begin();
            _procedure.CompleteStage(TransplantStage.OpenChest);

            // O tórax está aberto a essa altura, e é a abertura que expõe o coração. Sem isto ele
            // segue desativado e nenhuma mão o alcançaria — no jogo nem no teste.
            _nativeHeart.SetActive(true);
            _procedure.Bypass.Attempt(BypassStep.Cannulate);
            _procedure.Bypass.Attempt(BypassStep.ClampAorta);
            _procedure.Bypass.Attempt(BypassStep.Cardioplegia);
            _procedure.CompleteStage(TransplantStage.GoOnBypass);

            Assert.AreEqual(TransplantStage.RemoveNativeHeart, _procedure.Stage);
        }

        [UnityTest]
        public IEnumerator BothHandsCarryTheProjectsGrabContract()
        {
            XRHandInteractor[] hands = Hands();

            Assert.AreEqual(2, hands.Length,
                "A cena precisa de um XRHandInteractor por mão. Sem ele nada fica 'segurado', " +
                "e o meio da cirurgia trava sem erro nenhum no console.");

            bool left = false, right = false;
            foreach (XRHandInteractor hand in hands)
            {
                Assert.IsNotNull(hand.GetComponent<XRBaseInteractor>(),
                    $"'{hand.name}' não tem interactor da XRI — o adaptador não recebe evento algum.");

                if (hand.IsLeftHand) { left = true; } else { right = true; }
            }

            Assert.IsTrue(left && right,
                "Uma mão está marcada como a outra. Canhotos são metade da fila.");
            yield break;
        }

        [UnityTest]
        public IEnumerator GrabbingAnOrganMarksItHeld()
        {
            // Exposto, como estaria com o tórax aberto: o adaptador resolve o interactable subindo
            // a hierarquia, e GetComponentInParent ignora objeto inativo.
            _nativeHeart.SetActive(true);

            XRHandInteractor hand = Hands()[0];
            SurgicalInteractable organ = _nativeHeart.GetComponent<SurgicalInteractable>();

            Assert.IsFalse(organ.IsHeld, "Nada deveria estar na mão antes de agarrar.");

            RaiseSelectEntered(hand, _nativeHeart.GetComponent<XRGrabInteractable>());
            yield return null;

            Assert.IsTrue(organ.IsHeld,
                "Agarrar pela XRI tem que marcar o objeto como segurado no contrato do projeto.");
            Assert.AreSame(hand, organ.CurrentHolder);

            RaiseSelectExited(hand, _nativeHeart.GetComponent<XRGrabInteractable>());
            yield return null;

            Assert.IsFalse(organ.IsHeld, "Soltar tem que desfazer isso.");
        }

        [UnityTest]
        public IEnumerator ReleasingTheNativeHeartAwayFromTheChestCompletesTheExplant()
        {
            AdvanceToExplant();

            XRHandInteractor hand = Hands()[0];
            XRGrabInteractable grab = _nativeHeart.GetComponent<XRGrabInteractable>();

            RaiseSelectEntered(hand, grab);
            yield return null;

            // Carried well clear of the pericardium — further than the explant threshold.
            _nativeHeart.transform.position = _seat.position + new Vector3(0.6f, 0.1f, 0f);
            yield return null;

            Assert.AreEqual(TransplantStage.RemoveNativeHeart, _procedure.Stage,
                "Carregar o coração não é explantar; ele ainda está na mão do cirurgião.");

            RaiseSelectExited(hand, grab);
            yield return null;

            Assert.IsTrue(_nativeHeart.GetComponent<GrabbableOrgan>().Satisfied,
                "O coração doente saiu do tórax e foi pousado: isso é a cardiectomia.");
            Assert.AreEqual(TransplantStage.PlaceDonorHeart, _procedure.Stage,
                "Com o coração doente fora, a operação tem que avançar para posicionar o doador.");
        }

        [UnityTest]
        public IEnumerator TheDonorHeartDoesNotCountAsImplantedWhileItIsStillBeingHeld()
        {
            AdvanceToExplant();
            _procedure.CompleteStage(TransplantStage.RemoveNativeHeart);
            Assert.AreEqual(TransplantStage.PlaceDonorHeart, _procedure.Stage);

            XRHandInteractor hand = Hands()[0];
            XRGrabInteractable grab = _donorHeart.GetComponent<XRGrabInteractable>();
            GrabbableOrgan organ = _donorHeart.GetComponent<GrabbableOrgan>();

            RaiseSelectEntered(hand, grab);
            yield return null;

            // Lowered exactly into the pericardium, but not let go of.
            _donorHeart.transform.position = _seat.position;
            yield return null;
            yield return null;

            Assert.IsFalse(organ.Satisfied,
                "Um coração ainda preso à mão do cirurgião não está implantado. Antes desta " +
                "ligação existir ele contava, porque IsHeld era sempre falso.");
            Assert.AreEqual(TransplantStage.PlaceDonorHeart, _procedure.Stage);

            RaiseSelectExited(hand, grab);
            yield return null;
            yield return null;

            Assert.IsTrue(organ.Satisfied, "Pousado no lugar certo e solto: aí sim está posicionado.");
            Assert.AreEqual(TransplantStage.ConnectVessels, _procedure.Stage);
        }
    }
}
