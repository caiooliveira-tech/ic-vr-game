using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using VRSurgery.Data;
using VRSurgery.Interaction;
using VRSurgery.Session;
using VRSurgery.Surgery;
using VRSurgery.Tools;
using VRSurgery.Transplant;
using VRSurgery.VR;

namespace VRSurgery.EditorTools
{
    /// <summary>
    /// Builds the heart transplant scene: patient supine on the table, thorax assembled, sternum
    /// wired to open, heart grabbable.
    ///
    /// Nothing is placed by typed coordinates. The four anatomical models were generated
    /// separately and only the heart carries a measurement anyone trusts, so every other position
    /// is derived at build time from the meshes themselves — the table's top surface, the body's
    /// own bounds, the height of the thorax as a fraction of stature. That way re-exporting any
    /// one model moves what depends on it instead of silently leaving it floating.
    /// </summary>
    public static class TransplanteSceneBuilder
    {
        private const string SourceScene = "Assets/Scenes/SampleScene.unity";
        private const string TargetScene = "Assets/Scenes/TransplanteCardiaco.unity";

        private const string TableGlb = "Assets/Models/Environment/PROP_OperatingTable.glb";
        private const string BodyGlb = "Assets/Models/Patient/PATIENT_BodySkin.glb";
        private const string RibcageGlb = "Assets/Models/Patient/PATIENT_Ribcage.glb";
        private const string SternumGlb = "Assets/Models/Patient/PATIENT_Sternum.glb";
        private const string HeartGlb = "Assets/Models/Patient/PATIENT_Heart.glb";

        private const float TableTopY = 0.95f;

        /// <summary>
        /// The room monitor, in metres. Sized to be read from the far side of the patient — about
        /// 1.2m from the surgeon's eye — not to fill the view.
        /// </summary>
        private const float MonitorWidth = 0.56f;

        private const float MonitorHeight = 0.34f;

        /// <summary>0-based; index 0 is "Display 1", index 1 is "Display 2".</summary>
        private const int SpectatorDisplayIndex = 0;
        private const int ProjectionDisplayIndex = 1;

        /// <summary>Layer the rig and its UI are moved to, so the audience's projector never
        /// shows the operator's own controllers or teleport gizmo.</summary>
        private const string OperatorLayer = "OperatorOnly";

        /// <summary>
        /// Level the scene is built for. The bypass sites and the round length both follow from
        /// it, so switching this and rebuilding is the whole change — there is no second place
        /// holding a duration that has to be kept in step.
        /// </summary>
        private static SurgicalDifficulty _difficulty = SurgicalDifficulty.Medio;

        [MenuItem("VRSurgery/Transplante — nível Fácil")]
        public static void BuildEasy() { _difficulty = SurgicalDifficulty.Facil; Build(); }

        [MenuItem("VRSurgery/Transplante — nível Médio")]
        public static void BuildMedium() { _difficulty = SurgicalDifficulty.Medio; Build(); }

        [MenuItem("VRSurgery/Transplante — nível Difícil")]
        public static void BuildHard() { _difficulty = SurgicalDifficulty.Dificil; Build(); }

        /// <summary>
        /// Where the thorax sits, as a fraction of stature measured from the soles. The heart
        /// centre lands a little below the mid-thorax and to the patient's left, which is where
        /// a heart actually is.
        /// </summary>
        private const float ThoraxCentreOfHeight = 0.735f;

        /// <summary>
        /// No correction. The first ribcage came out flat — 20.0 x 10.8 x 32.0cm against a real
        /// cage's 28 x 20 x 30 — and had to be stretched per axis just to let an 8.7cm heart sit
        /// inside without touching ribs front and back. The regenerated model is barrel-shaped:
        /// its depth-to-width ratio measures 0.67 against a real 0.71, where the old one was 0.54.
        /// That scales uniformly to 23.8 x 16.0 x 30.0cm and needs no distortion at all.
        /// </summary>
        private static readonly Vector3 RibcageProportionFix = Vector3.one;

        [MenuItem("VRSurgery/Build 'Transplante Cardíaco'")]
        public static void BuildFromMenu()
        {
            Build();
            EditorUtility.DisplayDialog("Transplante Cardíaco",
                "Cena construída. Veja o Console para as medidas e o veredito de proporção.", "OK");
        }

        public static void Build()
        {
            EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);

            GameObject area = GameObject.Find("Teleport Area");
            if (area != null) { Object.DestroyImmediate(area); }

            RenameRig();
            WireHands();

            GameObject table = BuildTable();
            GameObject patient = BuildPatient(out Bounds bodyBounds);
            GameObject systems = BuildSystems();

            Vector3 thorax = ThoraxCentre(bodyBounds);
            GameObject ribcage = BuildRibcage(patient, thorax);
            GameObject heart = BuildHeart(patient, thorax);
            GameObject sternum = BuildSternum(patient, thorax, heart, WorldBounds(ribcage));

            GameObject donor = BuildDonorHeart(thorax);
            VesselAnastomosis[] vessels = BuildVessels(systems, thorax);
            BypassPlan plan = BypassPlan.For(_difficulty);
            List<BypassSite> bypass = BuildBypassSites(systems, thorax, plan);
            GameObject monitor = BuildMonitor(thorax);
            WireProcedure(systems, sternum, heart, donor, vessels, bypass, plan, monitor);
            PlaceAnchor(thorax);
            EnsureMainCamera();
            ApplyStaticAndShadowFlags();

            ReportFit(ribcage, heart);

            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), TargetScene, true);
            RegisterSceneInBuildSettings();
            Debug.Log("[Transplante] salvo em " + TargetScene);
        }

        // ---------------------------------------------------------------- sala

        private static GameObject BuildTable()
        {
            GameObject root = new GameObject("OperatingTable");
            GameObject model = Instantiate(TableGlb, root.transform);
            model.name = "TableModel";
            ConvertGltfMaterials(model);

            BoxCollider slab = model.AddComponent<BoxCollider>();
            slab.center = new Vector3(0f, TableTopY - 0.03f, 0f);
            slab.size = new Vector3(0.575f, 0.06f, 2.002f);

            Debug.Log($"[Transplante] mesa na origem, tampo em Y={TableTopY:F3}");
            return root;
        }

        /// <summary>
        /// The patient, supine on the table with the head toward +Z.
        ///
        /// The body model stands, so it is pitched a quarter turn to lie down; that also turns its
        /// front to face up, which is what supine means. It is then dropped so its back rests on
        /// the tabletop rather than being pushed through it.
        /// </summary>
        private static GameObject BuildPatient(out Bounds bodyBounds)
        {
            GameObject root = new GameObject("Patient");

            GameObject skin = Instantiate(BodyGlb, root.transform);
            skin.name = "Body_Skin";
            ConvertGltfMaterials(skin);
            ApplyOpenThorax(skin);

            // Lays the standing figure down with the head toward +Z and the chest facing up.
            //
            // A plain quarter turn about X is what was here, and it produced a patient face down
            // on the table — an operation performed on someone's back. The body model's front is
            // its local +Z (measured, not assumed: at ankle height the toes reach 17.3mm further
            // along +Z than the heels do along -Z, and at hip height the buttocks reach 17.3mm
            // along -Z against the belly's 11.7mm). Euler(90,0,0) sends that +Z to world -Y,
            // which is straight down.
            //
            // This is the same rotation the ribcage already uses when its spine comes out on the
            // far side: head to +Z, front to +Y.
            root.transform.rotation = Quaternion.Euler(-90f, 180f, 0f);

            bodyBounds = WorldBounds(skin);
            float drop = TableTopY - bodyBounds.min.y;
            root.transform.position += new Vector3(0f, drop, 0f);

            bodyBounds = WorldBounds(skin);

            Debug.Log($"[Transplante] paciente supino: {bodyBounds.size.x * 100f:F1} x " +
                      $"{bodyBounds.size.y * 100f:F1} x {bodyBounds.size.z * 100f:F1} cm, " +
                      $"costas em Y={bodyBounds.min.y:F3}, cabeça em Z={bodyBounds.max.z:F2}");
            return root;
        }

        /// <summary>
        /// Swaps the closed chest for the parted one, and puts tissue under the opening.
        ///
        /// Both meshes come from ThoraxOpening, which derives them from this same body — so the
        /// cut skin sits in exactly the local space the closed one did and the swap needs no
        /// repositioning of the heart, the vessel cuffs or the bypass sites. That is the whole
        /// reason for generating them from the patient instead of importing an open thorax:
        /// an imported one would bring its own proportions and origin, and everything placed
        /// against the body's bounds would have to be re-derived.
        ///
        /// Silently skipped when the assets are absent, so the scene still builds on a clean
        /// checkout where nobody has run the generator yet. The chest is closed then, which is
        /// the state the project was in before any of this.
        /// </summary>
        /// <summary>
        /// Desligado. A abertura gerada ainda não passou por verificação visual.
        ///
        /// O que existe em Generated/ abre uma janela no tórax e mostra tecido por baixo, mas as
        /// duas últimas correções — subdivisão da borda e normais das paredes — nunca foram vistas
        /// renderizadas, porque a ponte com o Editor caiu antes. O estado que EU VI tinha a borda
        /// serrilhada em estrela e lascas saindo da parede.
        ///
        /// Um paciente com a pele intacta é pior conteúdo e melhor projeto do que um paciente
        /// quebrado. Ligar de volta é trocar este false por true, reconstruir a cena e OLHAR.
        /// </summary>
        private const bool UseOpenThorax = false;

        private static void ApplyOpenThorax(GameObject skin)
        {
            if (!UseOpenThorax)
            {
                return;
            }

            Mesh parted = AssetDatabase.LoadAssetAtPath<Mesh>(
                "Assets/Models/Patient/Generated/PATIENT_BodySkin_Aberto.asset");

            if (parted == null)
            {
                Debug.LogWarning("[Transplante] tórax aberto não encontrado — a pele fica fechada. " +
                                 "Rodar 'VRSurgery/Tórax — gerar abertura' primeiro.");
                return;
            }

            MeshFilter filter = skin.GetComponentInChildren<MeshFilter>();
            if (filter == null)
            {
                Debug.LogError("[Transplante] a pele não tem MeshFilter; abertura não aplicada.");
                return;
            }

            filter.sharedMesh = parted;

            Mesh cavityMesh = AssetDatabase.LoadAssetAtPath<Mesh>(
                "Assets/Models/Patient/Generated/PATIENT_Cavidade.asset");

            if (cavityMesh == null)
            {
                Debug.LogWarning("[Transplante] pele aberta sem cavidade — a incisão fica vazada.");
                return;
            }

            // Parented to whatever holds the skin's own mesh, carrying the same local transform,
            // because the cavity's vertices are in that mesh's space and nothing else.
            GameObject cavity = new GameObject("Cavidade", typeof(MeshFilter), typeof(MeshRenderer));
            cavity.transform.SetParent(filter.transform.parent, false);
            cavity.transform.localPosition = filter.transform.localPosition;
            cavity.transform.localRotation = filter.transform.localRotation;
            cavity.transform.localScale = filter.transform.localScale;

            cavity.GetComponent<MeshFilter>().sharedMesh = cavityMesh;

            MeshRenderer renderer = cavity.GetComponent<MeshRenderer>();
            // Subcutaneous rather than muscle red: what shows immediately under a skin incision is
            // fat and fascia, and a bright red would read as the heart before the chest is open.
            renderer.sharedMaterial = MakeMaterial(new Color(0.52f, 0.29f, 0.26f), 0f, 0.18f);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            Debug.Log($"[Transplante] tórax aberto aplicado: pele com {parted.triangles.Length / 3} " +
                      $"triângulos, cavidade com {cavityMesh.triangles.Length / 3}");
        }

        /// <summary>
        /// Middle of the thorax, derived from the body rather than typed. With the patient supine
        /// the long axis is Z, so stature runs from the soles at min.z to the crown at max.z.
        /// </summary>
        private static Vector3 ThoraxCentre(Bounds body)
        {
            float stature = body.size.z;
            float z = body.min.z + stature * ThoraxCentreOfHeight;

            // Mid-depth of the chest, not the surface: the organs sit inside.
            float y = Mathf.Lerp(body.min.y, body.max.y, 0.45f);

            return new Vector3(body.center.x, y, z);
        }

        // ---------------------------------------------------------------- tórax

        private static GameObject BuildRibcage(GameObject patient, Vector3 thorax)
        {
            GameObject root = new GameObject("Ribcage");
            root.transform.SetParent(patient.transform, true);

            GameObject model = Instantiate(RibcageGlb, root.transform);
            model.name = "RibcageModel";
            ConvertGltfMaterials(model);

            // Laid down with the spine against the table, which is the whole point of supine and
            // is not something to guess at: the first attempt reused the body's rotation and put
            // the cage on its side, spine out sideways. Which face carries the spine is measured
            // from the mesh, because a generated model gives no guarantee about its axes.
            root.transform.rotation = SupineRotationFor(model);
            root.transform.localScale = RibcageProportionFix;
            root.transform.position = thorax;

            Bounds bounds = WorldBounds(model);
            Debug.Log($"[Transplante] gradil: {bounds.size.x * 100f:F1} x {bounds.size.y * 100f:F1} x " +
                      $"{bounds.size.z * 100f:F1} cm" +
                      (RibcageProportionFix == Vector3.one ? " (escala uniforme, sem distorção)"
                                                           : $" (CORRIGIDO {RibcageProportionFix})"));
            return root;
        }

        /// <summary>
        /// Works out how to lay a thoracic model down so the spine ends up underneath.
        ///
        /// The spine is a dense column of vertices hugging the midline; the ribs sweep away from
        /// it and are sparse there. So the discriminator is how much geometry sits near x = 0 on
        /// each side of the model's depth axis, not how wide each side is — the ribs reach the
        /// same span front and back, and a first attempt comparing widths read 19.9cm against
        /// 20.0cm and decided nothing.
        /// </summary>
        private static Quaternion SupineRotationFor(GameObject model)
        {
            // The band has to be a fraction of the model's width, not a fixed distance. At 3cm
            // absolute it covered 1.9% of the source model and 12.6% of the same model once
            // scaled to anatomical size — tight enough to isolate the spine in one case, wide
            // enough to sweep in ribs in the other, which turned a 9957-to-0 signal into 1.4:1.
            Bounds extent = LocalBounds(model, model.transform);
            float midlineBand = Mathf.Max(0.004f, extent.size.x * 0.06f);

            int frontMidline = 0, backMidline = 0;

            // Vertices are brought into the model root's space first. Reading them raw from the
            // mesh measures whatever space the importer happened to nest them in, while the
            // rotation is applied to the root — two different frames, which is how this returned
            // a limp 1.4:1 where the same count in the authoring tool was 9957 against zero.
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) { continue; }
                foreach (Vector3 raw in filter.sharedMesh.vertices)
                {
                    Vector3 v = model.transform.InverseTransformPoint(filter.transform.TransformPoint(raw));
                    if (Mathf.Abs(v.x - extent.center.x) > midlineBand) { continue; }
                    if (v.z >= 0f) { frontMidline++; } else { backMidline++; }
                }
            }

            bool spineAtPositiveZ = frontMidline > backMidline;

            Debug.Log($"[Transplante] coluna detectada em {(spineAtPositiveZ ? "+Z" : "-Z")} do modelo " +
                      $"(faixa ±{midlineBand * 100f:F1}cm: +Z={frontMidline}, -Z={backMidline}, " +
                      $"razão {Mathf.Max(frontMidline, backMidline) / (float)Mathf.Max(1, Mathf.Min(frontMidline, backMidline)):F1}:1)");

            // Head toward +Z either way; the roll is what puts the spine down against the table.
            return spineAtPositiveZ
                ? Quaternion.Euler(90f, 0f, 0f)
                : Quaternion.Euler(-90f, 180f, 0f);
        }

        private static GameObject BuildHeart(GameObject patient, Vector3 thorax)
        {
            GameObject root = new GameObject("Heart");
            root.transform.SetParent(patient.transform, true);

            GameObject model = Instantiate(HeartGlb, root.transform);
            model.name = "HeartModel";
            ConvertGltfMaterials(model);

            // Left of the midline and slightly toward the feet, with the apex pointing down-left
            // and forward — where a heart sits, not centred in the chest like a textbook diagram.
            root.transform.rotation = Quaternion.Euler(90f, 0f, 22f);
            root.transform.position = thorax + new Vector3(-0.025f, 0.01f, -0.02f);

            DressAsGrabbable(root, model);

            Bounds bounds = WorldBounds(model);
            Debug.Log($"[Transplante] coração: {bounds.size.x * 100f:F1} x {bounds.size.y * 100f:F1} x " +
                      $"{bounds.size.z * 100f:F1} cm em {root.transform.position}");
            return root;
        }

        private static GameObject BuildSternum(GameObject patient, Vector3 thorax, GameObject heart,
            Bounds ribcage)
        {
            GameObject root = new GameObject("Sternum");
            root.transform.SetParent(patient.transform, true);

            GameObject model = Instantiate(SternumGlb, root.transform);
            model.name = "SternumModel";
            ConvertGltfMaterials(model);

            root.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            // Seated on the front of the cage rather than at a fixed offset from the thorax
            // centre, which left it hovering above the ribs with daylight underneath. On a supine
            // patient the front of the chest is the top, so it rides the ribcage's upper surface,
            // sunk slightly so the bone meets cartilage instead of resting on it.
            Bounds plate = WorldBounds(root);
            float seatY = ribcage.max.y - plate.size.y * 0.35f;
            root.transform.position = new Vector3(thorax.x, seatY, thorax.z + 0.015f);

            SternotomyController sternotomy = root.AddComponent<SternotomyController>();
            sternotomy.Bind(root.transform, new[] { heart });

            Debug.Log($"[Transplante] esterno em {root.transform.position}, " +
                      "SternotomyController revelando o coração");
            return root;
        }

        /// <summary>
        /// Gives the heart the project's grab contract. Same components the instruments use, and
        /// deliberately no SurgicalTool — that base class starts the booth's round on first grab.
        /// </summary>
        private static void DressAsGrabbable(GameObject organ, GameObject model)
        {
            Bounds local = LocalBounds(model, organ.transform);

            BoxCollider box = organ.AddComponent<BoxCollider>();
            box.center = local.center;
            box.size = local.size;

            Rigidbody body = organ.AddComponent<Rigidbody>();
            body.mass = 0.3f;
            body.useGravity = false;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            GameObject grip = new GameObject("GripPoint");
            grip.transform.SetParent(organ.transform, false);
            grip.transform.localPosition = local.center;

            ToolDefinition definition = ToolDefinition.Create(
                "heart", "Coração", ToolType.Retractor, ToolCapability.None);

            SurgicalInteractable interactable = organ.AddComponent<SurgicalInteractable>();
            SetPrivateField(interactable, "toolDefinition", definition);
            SetPrivateField(interactable, "gripPoint", grip.transform);

            XRGrabInteractable grab = organ.AddComponent<XRGrabInteractable>();
            grab.attachTransform = grip.transform;
            grab.useDynamicAttach = false;
            grab.throwOnDetach = false;
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;

            organ.AddComponent<ToolReleasePhysics>();
            organ.AddComponent<GrabbableOrgan>();
        }

        // ---------------------------------------------------------------- sistemas

        private static GameObject BuildSystems()
        {
            GameObject systems = new GameObject("Systems");
            systems.AddComponent<SurgeryTelemetry>();
            return systems;
        }

        /// <summary>
        /// The replacement organ, waiting on its own stand beside the table.
        ///
        /// It starts outside the patient because that is where a donor heart is: brought in cold,
        /// in a basin, and lifted into a chest that is already empty. Putting it in the thorax
        /// from the start would make the middle of the operation meaningless.
        /// </summary>
        private static GameObject BuildDonorHeart(Vector3 thorax)
        {
            GameObject stand = new GameObject("DonorStand");
            stand.transform.position = new Vector3(thorax.x + 0.50f, 0f, thorax.z - 0.35f);

            GameObject pedestal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pedestal.name = "StandColumn";
            pedestal.transform.SetParent(stand.transform, false);
            pedestal.transform.localScale = new Vector3(0.26f, 0.90f, 0.26f);
            pedestal.transform.localPosition = new Vector3(0f, 0.45f, 0f);
            pedestal.GetComponent<MeshRenderer>().sharedMaterial =
                MakeMaterial(new Color(0.55f, 0.58f, 0.62f), 0f, 0.3f);

            GameObject basin = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            basin.name = "Basin";
            basin.transform.SetParent(stand.transform, false);
            basin.transform.localScale = new Vector3(0.24f, 0.03f, 0.24f);
            basin.transform.localPosition = new Vector3(0f, 0.92f, 0f);
            Object.DestroyImmediate(basin.GetComponent<Collider>());
            basin.GetComponent<MeshRenderer>().sharedMaterial =
                MakeMaterial(new Color(0.72f, 0.75f, 0.78f), 0.8f, 0.6f);

            GameObject organ = new GameObject("DonorHeart");
            organ.transform.SetParent(stand.transform, true);
            organ.transform.position = stand.transform.position + new Vector3(0f, 1.0f, 0f);
            organ.transform.rotation = Quaternion.Euler(0f, 0f, 12f);

            GameObject model = Instantiate(HeartGlb, organ.transform);
            model.name = "DonorHeartModel";
            ConvertGltfMaterials(model);

            DressAsGrabbable(organ, model);
            organ.AddComponent<Heartbeat>().Bind(model.transform);

            Debug.Log($"[Transplante] coração doador na bacia em {organ.transform.position}");
            return organ;
        }

        /// <summary>
        /// The five joins, placed around the recipient's seat in roughly the order they are sewn:
        /// left atrium first and deepest, the great arteries last and most exposed.
        /// </summary>
        private static VesselAnastomosis[] BuildVessels(GameObject systems, Vector3 thorax)
        {
            (VesselSite site, Vector3 offset)[] layout =
            {
                (VesselSite.LeftAtrium,       new Vector3(-0.030f, -0.020f,  0.005f)),
                (VesselSite.InferiorVenaCava, new Vector3( 0.028f, -0.015f, -0.045f)),
                (VesselSite.SuperiorVenaCava, new Vector3( 0.032f,  0.015f,  0.050f)),
                (VesselSite.Aorta,            new Vector3(-0.008f,  0.030f,  0.055f)),
                (VesselSite.PulmonaryArtery,  new Vector3(-0.032f,  0.028f,  0.040f)),
            };

            GameObject root = new GameObject("Anastomoses");
            root.transform.SetParent(systems.transform, true);

            VesselAnastomosis[] built = new VesselAnastomosis[layout.Length];
            for (int i = 0; i < layout.Length; i++)
            {
                GameObject site = new GameObject("Vessel_" + layout[i].site);
                site.transform.SetParent(root.transform, true);
                site.transform.position = thorax + layout[i].offset;

                built[i] = site.AddComponent<VesselAnastomosis>();
                built[i].Bind(layout[i].site, null, 0.022f);

                BuildVesselMarker(site.transform, layout[i].site, 0.022f);
            }

            Debug.Log($"[Transplante] {built.Length} anastomoses posicionadas ao redor do assento");
            return built;
        }

        /// <summary>
        /// Where the pump is worked: the cannulation sites on the great vessels, the cross-clamp
        /// on the ascending aorta, and the cardioplegia cannula in the aortic root.
        ///
        /// Entry and exit share their sites, because that is true of the real thing — the clamp
        /// comes off the same aorta it went onto. The six holds come to roughly forty seconds,
        /// which is the share of a four-minute operation bypass is allowed before it stops being
        /// a procedure and becomes a minigame.
        /// </summary>
        private static List<BypassSite> BuildBypassSites(GameObject systems, Vector3 thorax,
            BypassPlan plan)
        {
            GameObject root = new GameObject("BypassSites");
            root.transform.SetParent(systems.transform, true);

            // Anatomy, not layout: where each step is performed on a real patient. A gesture that
            // carries several steps is placed at the last of them, which is the one the surgeon's
            // hands finish on.
            Dictionary<BypassStep, Vector3> where = new Dictionary<BypassStep, Vector3>
            {
                { BypassStep.Cannulate,    new Vector3( 0.035f,  0.020f,  0.030f) },
                { BypassStep.ClampAorta,   new Vector3(-0.010f,  0.035f,  0.060f) },
                { BypassStep.Cardioplegia, new Vector3(-0.005f,  0.030f,  0.048f) },
                { BypassStep.Unclamp,      new Vector3(-0.010f,  0.035f,  0.060f) },
                { BypassStep.DeAir,        new Vector3(-0.028f,  0.032f,  0.035f) },
                { BypassStep.Wean,         new Vector3( 0.060f,  0.010f, -0.060f) },
            };

            List<BypassSite> sites = new List<BypassSite>();

            foreach (BypassGesture gesture in plan.Gestures)
            {
                GameObject point = new GameObject("Bypass_" + gesture.Anchor);
                point.transform.SetParent(root.transform, true);
                point.transform.position = thorax +
                    (where.TryGetValue(gesture.Anchor, out Vector3 offset) ? offset : Vector3.zero);

                sites.Add(new BypassSite
                {
                    Step = gesture.Anchor,
                    Point = point.transform,
                    Radius = 0.028f,
                    Seconds = gesture.Seconds,
                    Chain = gesture.Steps,
                });
            }

            Debug.Log($"[Transplante] nível {plan.Difficulty}: {sites.Count} ponto(s) de CEC, " +
                      $"{plan.GestureSeconds:F0}s de gestos, {plan.DoneByTeam.Count} etapa(s) " +
                      $"já feitas pela equipe, rodada de {plan.RoundSeconds:F0}s");
            return sites;
        }

        /// <summary>
        /// A visible cuff at each join.
        ///
        /// The sites were invisible transforms, so the visitor was asked to sew five vessels with
        /// nothing to aim at. This is a ring rather than a vessel model: the generated vessel set
        /// could not be segmented into its five pieces by any means tried — loose parts gave 350
        /// shells, spatial clustering bridged the aortic arch into the pulmonary trunk, and the
        /// texture distinguishes arterial from venous rather than one vessel from another — and
        /// the heart's own vessels are the same shell soup, 776 boundary loops with no five tube
        /// ends among them. Importing 1.9M triangles that overlap vessels the heart already
        /// carries would have bought nothing the mechanic can use.
        ///
        /// Arterial red and venous blue, the one convention the generated texture did carry.
        /// </summary>
        private static void BuildVesselMarker(Transform site, VesselSite vessel, float radius)
        {
            bool arterial = vessel == VesselSite.Aorta || vessel == VesselSite.PulmonaryArtery;

            GameObject cuff = new GameObject("Cuff", typeof(MeshFilter), typeof(MeshRenderer));
            cuff.transform.SetParent(site, false);
            cuff.GetComponent<MeshFilter>().sharedMesh = MakeRing(radius * 0.55f, radius, 28);

            MeshRenderer renderer = cuff.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = MakeUnlit(arterial
                ? new Color(0.85f, 0.22f, 0.20f, 0.65f)
                : new Color(0.30f, 0.42f, 0.72f, 0.65f));
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>
        /// The target on the chest, so the visitor can see where the sternotomy goes.
        ///
        /// Without it the first instruction of the operation — open the chest — pointed at nothing:
        /// the site was an invisible transform and the only way to find it was to sweep a hand
        /// across the patient until something happened. The five anastomoses already had cuffs for
        /// exactly this reason; the sternum was the one working site with no mark on it.
        ///
        /// Same ring the vessels use, at the gesture's own radius, so what is drawn is the
        /// tolerance the code actually checks rather than a decoration near it.
        ///
        /// It sits on the skin rather than on the bone. The site itself is the middle of the
        /// sternum, 5.7cm under the surface, and a marker left there is inside the patient where
        /// nobody can see it. The height is sampled from the skin mesh directly above the site
        /// instead of typed, so re-exporting the body moves the mark with the chest.
        /// </summary>
        private static void BuildSternalMarker(Transform site, float radius)
        {
            GameObject skin = GameObject.Find("Body_Skin");
            float surfaceY = site.position.y;

            if (skin != null)
            {
                MeshFilter filter = skin.GetComponentInChildren<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    float highest = float.NegativeInfinity;
                    foreach (Vector3 raw in filter.sharedMesh.vertices)
                    {
                        Vector3 world = filter.transform.TransformPoint(raw);
                        if (Mathf.Abs(world.x - site.position.x) > 0.03f) { continue; }
                        if (Mathf.Abs(world.z - site.position.z) > 0.03f) { continue; }
                        if (world.y > highest) { highest = world.y; }
                    }

                    if (!float.IsNegativeInfinity(highest)) { surfaceY = highest; }
                }
            }

            GameObject mark = new GameObject("SternalMark", typeof(MeshFilter), typeof(MeshRenderer));
            mark.transform.SetParent(site, true);
            // A couple of millimetres clear of the skin, or the two surfaces fight for the pixel.
            mark.transform.position = new Vector3(site.position.x, surfaceY + 0.003f, site.position.z);
            mark.transform.rotation = Quaternion.identity;

            mark.GetComponent<MeshFilter>().sharedMesh = MakeRing(radius * 0.55f, radius, 28);

            MeshRenderer renderer = mark.GetComponent<MeshRenderer>();
            // Neither arterial nor venous: this is a guide mark, not a vessel, and it should not
            // read as one of the five joins the visitor has to sew later.
            renderer.sharedMaterial = MakeUnlit(new Color(0.95f, 0.78f, 0.35f, 0.55f));
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            Debug.Log($"[Transplante] marca do esterno em Y={mark.transform.position.y:F3} " +
                      $"(pele em {surfaceY:F3}, sítio do gesto {site.position.y:F3}), raio {radius * 100f:F1}cm");
        }

        /// <summary>A flat annulus on the XZ plane — the open mouth of a vessel, seen end on.</summary>
        private static Mesh MakeRing(float inner, float outer, int segments)
        {
            Vector3[] vertices = new Vector3[segments * 2];
            int[] triangles = new int[segments * 12];

            for (int i = 0; i < segments; i++)
            {
                float angle = (i / (float)segments) * Mathf.PI * 2f;
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                vertices[i * 2] = new Vector3(cos * inner, 0f, sin * inner);
                vertices[i * 2 + 1] = new Vector3(cos * outer, 0f, sin * outer);
            }

            int t = 0;
            for (int i = 0; i < segments; i++)
            {
                int n = (i + 1) % segments;
                int a = i * 2, b = i * 2 + 1, c = n * 2, d = n * 2 + 1;

                // Both windings: a cuff is seen from wherever the surgeon's head happens to be.
                triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                triangles[t++] = c; triangles[t++] = d; triangles[t++] = b;
                triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
                triangles[t++] = c; triangles[t++] = b; triangles[t++] = d;
            }

            Mesh mesh = new Mesh { name = "VesselCuff" };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material MakeUnlit(Color colour)
        {
            Material m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = colour };
            m.SetFloat("_Surface", 1f);
            m.renderQueue = 3000;
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            return m;
        }

        /// <summary>
        /// The monitor the surgeon reads, across the table from where they stand.
        ///
        /// On the far side of the patient on purpose: it is the only place in the room a screen
        /// can sit where looking at it does not mean looking away from the chest. Its position
        /// comes from the thorax and the stance rather than being typed, so re-exporting the body
        /// moves the screen with the patient instead of leaving it behind the visitor's head.
        /// </summary>
        private static GameObject BuildMonitor(Vector3 thorax)
        {
            Vector3 stance = Stance(thorax);

            // Beyond the patient, a little past the far edge of the table, at standing eye height.
            Vector3 position = new Vector3(thorax.x - 0.78f, 1.45f, thorax.z + 0.10f);

            GameObject monitor = new GameObject("SurgeonMonitor");
            monitor.transform.position = position;

            // Square on to the surgeon. Yaw only: tilting to chase eye height keystones the text
            // for no readability gain.
            Vector3 toSurgeon = new Vector3(stance.x - position.x, 0f, stance.z - position.z);
            monitor.transform.rotation = toSurgeon.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(toSurgeon.normalized, Vector3.up)
                : Quaternion.identity;

            GameObject screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screen.name = "Screen";
            screen.transform.SetParent(monitor.transform, false);
            screen.transform.localScale = new Vector3(MonitorWidth, MonitorHeight, 1f);
            // A Unity quad faces -Z, so it is turned to look back along the parent's +Z.
            screen.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            Object.DestroyImmediate(screen.GetComponent<Collider>());
            screen.GetComponent<MeshRenderer>().sharedMaterial =
                MakeMaterial(new Color(0.06f, 0.08f, 0.11f), 0f, 0.1f);

            // +Z is the surgeon's side of the panel, so the text sits in front of the quad rather
            // than behind it, or the panel occludes every word.
            BuildScreenText(monitor.transform, "InstructionText", new Vector3(0f, 0.075f, 0.004f), 0.034f);
            BuildScreenText(monitor.transform, "ClockText", new Vector3(0f, -0.005f, 0.004f), 0.052f);
            BuildScreenText(monitor.transform, "ReasonText", new Vector3(0f, -0.105f, 0.004f), 0.022f);

            monitor.AddComponent<TransplantHUD>();

            Debug.Log($"[Transplante] monitor em {position}, virado para o cirurgião em {stance}");
            return monitor;
        }

        /// <summary>
        /// A line of text on the monitor. TextMesh rather than TextMeshPro on purpose: the HUD
        /// component already takes TextMesh, and a legacy mesh costs the Quest less than a
        /// world-space canvas for what is a handful of short lines.
        /// </summary>
        private static TextMesh BuildScreenText(Transform parent, string name, Vector3 localPosition,
            float height)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            TextMesh text = go.AddComponent<TextMesh>();
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = new Color(0.82f, 0.88f, 0.95f);

            // A TextMesh with no font renders nothing and logs no error, which is the single
            // easiest way to ship a monitor that is silently blank.
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
            {
                Debug.LogError("[Transplante] fonte interna ausente; o monitor ficará vazio.");
            }
            else
            {
                text.font = font;
                go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }

            // fontSize is the raster resolution and characterSize is the world scale; a glyph ends
            // up (fontSize * characterSize / 10) units tall. Rastering large and scaling down is
            // what keeps world-space text from looking chewed at reading distance.
            text.fontSize = 96;
            text.characterSize = height * 10f / text.fontSize;

            return text;
        }

        /// <summary>
        /// A small keypad the visitor types their own name on, set within arm's reach of the
        /// stance and facing it the same way the monitor faces it.
        ///
        /// Its position is a first placement, not a measured one — like the anastomosis offsets,
        /// it has not been checked against a real chest and a real headset, and needs the same
        /// advisor pass before the stand opens.
        /// </summary>
        private static List<NameEntryKey> BuildNameEntryKeyboard(GameObject sternum, GameObject systems)
        {
            Vector3 thoraxCenter = WorldBounds(sternum).center;
            Vector3 stance = Stance(thoraxCenter);

            // Between the stance and the sternum, off to the side of the vessels being sewn
            // rather than on top of them.
            Vector3 center = new Vector3(
                Mathf.Lerp(stance.x, thoraxCenter.x, 0.55f),
                thoraxCenter.y,
                thoraxCenter.z + 0.28f);

            GameObject panel = new GameObject("NameEntryKeyboard");
            panel.transform.SetParent(systems.transform, true);
            panel.transform.position = center;

            // Same yaw-only, face-the-stance convention as BuildMonitor, so BuildScreenText's
            // "+Z is the surgeon's side" trick keeps working on this panel too.
            Vector3 toSurgeon = new Vector3(stance.x - center.x, 0f, stance.z - center.z);
            panel.transform.rotation = toSurgeon.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(toSurgeon.normalized, Vector3.up)
                : Quaternion.identity;

            string[] rows = { "ABCDEF", "GHIJKL", "MNOPQR", "STUVWX", "YZ" };
            const float spacing = 0.05f;

            List<NameEntryKey> keys = new List<NameEntryKey>();

            for (int r = 0; r < rows.Length; r++)
            {
                string row = rows[r];
                for (int c = 0; c < row.Length; c++)
                {
                    char letter = row[c];
                    Vector3 local = new Vector3(c * spacing, -r * spacing, 0f);
                    keys.Add(BuildNameEntryKey(panel.transform, letter.ToString(), local,
                        NameEntryKey.KeyAction.Character, letter));
                }
            }

            // The three special keys share the letters' last row, one column after "YZ".
            keys.Add(BuildNameEntryKey(panel.transform, "SPC", new Vector3(2 * spacing, -4 * spacing, 0f),
                NameEntryKey.KeyAction.Character, ' '));
            keys.Add(BuildNameEntryKey(panel.transform, "DEL", new Vector3(3 * spacing, -4 * spacing, 0f),
                NameEntryKey.KeyAction.Backspace, '\0'));
            keys.Add(BuildNameEntryKey(panel.transform, "OK", new Vector3(4 * spacing, -4 * spacing, 0f),
                NameEntryKey.KeyAction.Confirm, '\0'));

            Debug.Log($"[Transplante] teclado do placar em {center}, {keys.Count} tecla(s)");

            return keys;
        }

        /// <summary>One keycap: a small block with a printed label, holding the NameEntryKey.</summary>
        private static NameEntryKey BuildNameEntryKey(Transform parent, string label, Vector3 localPosition,
            NameEntryKey.KeyAction action, char character)
        {
            GameObject cap = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cap.name = $"Key_{label}";
            cap.transform.SetParent(parent, false);
            cap.transform.localPosition = localPosition;
            cap.transform.localRotation = Quaternion.identity;
            cap.transform.localScale = new Vector3(0.035f, 0.035f, 0.01f);
            Object.DestroyImmediate(cap.GetComponent<Collider>());
            cap.GetComponent<MeshRenderer>().sharedMaterial =
                MakeMaterial(new Color(0.18f, 0.22f, 0.28f), 0f, 0.25f);

            NameEntryKey key = cap.AddComponent<NameEntryKey>();
            key.Bind(action, character);

            BuildScreenText(cap.transform, "Label", new Vector3(0f, 0f, 0.008f), 0.016f).text = label;

            return key;
        }

        /// <summary>
        /// Puts the project's hand adapter on the rig's grab interactors.
        ///
        /// Without this the scene had no XRHandInteractor at all, and that one omission broke the
        /// middle of the operation: the adapter is the only thing that turns XRI's selectEntered /
        /// selectExited into SurgicalInteractable.OnGrabbed / OnReleased, so IsHeld stayed false
        /// forever. The native heart is judged on its Released event and therefore could never be
        /// reported as explanted, and the donor heart — judged on "near the seat AND not held" —
        /// counted as implanted while still in the visitor's hand.
        ///
        /// It goes on the Near-Far Interactor because that is the one that performs grabs. Poke is
        /// for pressing, Teleport for locomotion this scene does not use, and Gaze is not a hand.
        /// The adapter requires an XRBaseInteractor on its own GameObject, so it has to live on
        /// the interactor rather than on the controller root.
        /// </summary>
        private static void WireHands()
        {
            GameObject rig = GameObject.Find("XR Origin");
            if (rig == null)
            {
                Debug.LogError("[Transplante] sem XR Origin: nada poderá ser agarrado.");
                return;
            }

            int wired = 0;

            foreach (string side in new[] { "Left", "Right" })
            {
                Transform interactor = rig.transform.Find($"Camera Offset/{side} Controller/Near-Far Interactor");
                if (interactor == null)
                {
                    Debug.LogError($"[Transplante] '{side} Controller' sem Near-Far Interactor; " +
                                   "essa mão não conseguirá agarrar nada.");
                    continue;
                }

                XRHandInteractor hand = interactor.GetComponent<XRHandInteractor>();
                if (hand == null) { hand = interactor.gameObject.AddComponent<XRHandInteractor>(); }

                SetPrivateField(hand, "isLeftHand", side == "Left");
                wired++;
            }

            Debug.Log($"[Transplante] {wired} mão(s) ligadas ao contrato de agarre do projeto");
        }

        /// <summary>
        /// The surgeon's two hands, as the rig exposes them.
        ///
        /// The poke point is the fingertip the template already positions and draws, so it is what
        /// the visitor sees themselves touching the patient with. Falling back to the controller
        /// root keeps the scene buildable against a rig that has been stripped of its poke
        /// interactors, at the cost of a tip a few centimetres back from the fingertip.
        /// </summary>
        private static Transform[] FindHands()
        {
            GameObject rig = GameObject.Find("XR Origin");
            if (rig == null)
            {
                Debug.LogWarning("[Transplante] sem XR Origin: nenhum gesto poderá ser executado.");
                return new Transform[0];
            }

            List<Transform> hands = new List<Transform>();

            foreach (string side in new[] { "Left", "Right" })
            {
                Transform controller = rig.transform.Find($"Camera Offset/{side} Controller");
                if (controller == null)
                {
                    Debug.LogWarning($"[Transplante] rig sem '{side} Controller'.");
                    continue;
                }

                Transform poke = controller.Find("Poke Interactor/Poke Point");
                hands.Add(poke != null ? poke : controller);

                if (poke == null)
                {
                    Debug.LogWarning($"[Transplante] '{side} Controller' sem Poke Point; " +
                                     "usando a raiz do controle como ponta.");
                }
            }

            Debug.Log($"[Transplante] {hands.Count} mão(s) encontradas no rig");
            return hands.ToArray();
        }

        private static void WireProcedure(GameObject systems, GameObject sternum, GameObject heart,
            GameObject donor, VesselAnastomosis[] vessels, List<BypassSite> bypass, BypassPlan plan,
            GameObject monitor)
        {
            SurgeryTelemetry telemetry = systems.GetComponent<SurgeryTelemetry>();

            TransplantProcedure procedure = systems.AddComponent<TransplantProcedure>();
            procedure.SetDifficulty(plan.Difficulty);

            EventSessionDefinition definition = EventSessionDefinition.Create(
                // From the level, not from a constant. Fácil is ninety seconds because there is
                // a third less to do, not because ninety was typed somewhere — so the booth can
                // run a fast queue or a faithful operation without the two numbers drifting apart.
                round: plan.RoundSeconds, briefingTimeout: 45f, resultHold: 6f, scoreboardHold: 8f,
                // The round starts on the sternotomy, not on a grab: this scene has no instrument
                // to pick up, so the grab that starts the round in every other scene never happens.
                startOnGrab: false, pointsPerSecond: 100f,
                fact: "Entre parar o coração doente e fazer o novo bater, quem mantém o paciente " +
                      "vivo é a circulação extracorpórea: uma bomba assume o trabalho do coração " +
                      "e dos pulmões durante toda a troca.");

            EventSessionController session = systems.AddComponent<EventSessionController>();
            session.Bind(definition, null, telemetry);

            // Not leaderboard.Bind(session): the concept has the visitor typing a name before the
            // run is filed, so NameEntryController takes over the RoundEnded subscription instead
            // of the leaderboard filing every win under "Anônimo" by itself.
            Leaderboard leaderboard = systems.AddComponent<Leaderboard>();

            NameEntryController nameEntry = systems.AddComponent<NameEntryController>();
            nameEntry.Bind(session, leaderboard);

            // The seat is where the heart starts: the donor organ has to come back to it, and the
            // native one has to be carried away from it.
            GameObject seat = new GameObject("PericardialSeat");
            seat.transform.SetParent(systems.transform, true);
            seat.transform.position = heart.transform.position;

            heart.GetComponent<GrabbableOrgan>().Bind(OrganRole.Native, seat.transform, procedure);
            donor.GetComponent<GrabbableOrgan>().Bind(OrganRole.Donor, seat.transform, procedure);

            foreach (VesselAnastomosis vessel in vessels)
            {
                vessel.Bind(vessel.Site, procedure, 0.022f);
            }

            // The payoff: the donor heart starts once the operation is finished. Wired here rather
            // than inside Heartbeat so the organ knows nothing about the procedure that installed
            // it, and can be reused wherever a beating heart is wanted.
            // The beat waits for the pump, not for the last stage. Coming off bypass is what
            // hands the circulation back; declaring the operation finished is bookkeeping that
            // happens afterwards.
            Heartbeat beat = donor.GetComponent<Heartbeat>();
            procedure.Bypass.WeanedOff += () => beat.StartBeating();

            // Everything in this operation is worked with the hands: there is no instrument to
            // pick up, so the workers need a tip that tracks the surgeon rather than one attached
            // to a tool. A single shared tip, following whichever hand is nearest the work, so
            // either hand can perform any step — and so two workers cannot cancel each other's
            // progress, which is what a tip per hand would do.
            List<Transform> work = new List<Transform>();
            foreach (BypassSite site in bypass) { if (site.Point != null) { work.Add(site.Point); } }
            foreach (VesselAnastomosis vessel in vessels) { work.Add(vessel.transform); }

            GameObject sternalSite = new GameObject("SternalMidline");
            sternalSite.transform.SetParent(systems.transform, true);
            sternalSite.transform.position = WorldBounds(sternum).center;
            work.Add(sternalSite.transform);

            GameObject tipObject = new GameObject("SurgeonHandTip");
            tipObject.transform.SetParent(systems.transform, true);
            SurgeonHandTip tip = tipObject.AddComponent<SurgeonHandTip>();
            tip.Bind(FindHands(), work.ToArray());

            BypassWorker worker = systems.AddComponent<BypassWorker>();
            worker.Bind(tipObject.transform, bypass, procedure);
            SetPrivateField(worker, "requireHeldInstrument", false);

            // The vessels had no worker at all, so the five joins could never be sewn and the
            // operation dead-ended at its longest stage.
            AnastomosisWorker sewing = systems.AddComponent<AnastomosisWorker>();
            sewing.Bind(tipObject.transform, vessels, procedure);
            SetPrivateField(sewing, "requireHeldInstrument", false);

            // The visitor's own hand types the name too, on a small keypad set within reach of
            // the stance rather than on the monitor across the table — the monitor sits beyond
            // the patient on purpose (see BuildMonitor), which puts it out of arm's reach.
            List<NameEntryKey> nameKeys = BuildNameEntryKeyboard(sternum, systems);
            NameEntryWorker keyboard = systems.AddComponent<NameEntryWorker>();
            keyboard.Bind(tipObject.transform, nameKeys, nameEntry);

            // Half the sternum's length, so the gesture covers the bone the incision runs along
            // rather than a coin in the middle of it.
            float sternalReach = Mathf.Max(0.05f, WorldBounds(sternum).size.z * 0.5f);

            BuildSternalMarker(sternalSite.transform, sternalReach);

            SternotomyWorker opening = systems.AddComponent<SternotomyWorker>();
            opening.Bind(tipObject.transform, sternalSite.transform,
                sternum.GetComponent<SternotomyController>(), procedure, sternalReach, 3f);

            // The booth loop. Without this the session never left Attract: the clock never
            // started, the patient was never reset between visitors, and finishing the operation
            // did not win the round.
            TransplantRoundBridge bridge = systems.AddComponent<TransplantRoundBridge>();
            bridge.Bind(session, procedure, sternum.GetComponent<SternotomyController>(), opening,
                new[] { heart.GetComponent<GrabbableOrgan>(), donor.GetComponent<GrabbableOrgan>() },
                vessels, beat);

            monitor.GetComponent<TransplantHUD>().Bind(
                procedure, session, worker, opening,
                monitor.transform.Find("InstructionText").GetComponent<TextMesh>(),
                monitor.transform.Find("ClockText").GetComponent<TextMesh>(),
                monitor.transform.Find("ReasonText").GetComponent<TextMesh>(),
                sewing, nameEntry);

            Debug.Log($"[Transplante] procedimento ligado: {procedure.VesselCount} vasos, " +
                      $"rodada {definition.RoundSeconds:F0}s, assento pericárdico em {seat.transform.position}, " +
                      $"esternotomia num raio de {sternalReach * 100f:F1}cm, " +
                      $"{work.Count} ponto(s) de trabalho para as mãos");

            // The public side of the stand: what the docx calls a projeção enquanto o visitante
            // opera — a big clock, the risk colour, the day's best time, and the table between
            // visitors, all on a screen nobody wearing the headset ever sees.
            BuildProjectionHUD(systems, session, leaderboard, vessels);
            WireUrgencyTint(systems);
            HideOperatorVisualsFromProjection();
            BuildSpectatorCamera();
            BuildProjectionCamera(WorldBounds(sternum).center);
        }

        /// <summary>
        /// The audience's screen: clock, risk colour, the day's best time, and the table between
        /// visitors. Ported from SurgeryMvpSceneBuilder's ProjectionHUD wiring — the component
        /// itself already had no dependency on that scene beyond an optional BleedingSystem, which
        /// this scene has no equivalent of. In its place, the bleed bar reads the fraction of
        /// vessels currently leaking, the same risk signal the concept describes for the trocar.
        /// </summary>
        private static GameObject BuildProjectionHUD(
            GameObject systems, EventSessionController session, Leaderboard leaderboard,
            VesselAnastomosis[] vessels)
        {
            GameObject root = new GameObject("ProjectionHUD");

            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = ProjectionDisplayIndex;

            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            Image vignette = BuildHudImage(root.transform, "UrgencyVignette",
                new Vector2(0f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero,
                new Color(0.75f, 0.05f, 0.05f, 0f));

            GameObject clockGroup = new GameObject("ClockGroup", typeof(RectTransform));
            clockGroup.transform.SetParent(root.transform, false);
            StretchFull(clockGroup.GetComponent<RectTransform>());

            Text clock = BuildHudText(clockGroup.transform, "Clock", font, 220, TextAnchor.UpperCenter,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(900f, 260f));

            // "Risco de sangramento" rather than one wound's bleed bar: how many of the five
            // anastomoses are currently leaking, which is the number the concept's bar generalises to.
            BuildHudImage(clockGroup.transform, "BleedTrack",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -330f), new Vector2(1100f, 46f),
                new Color(0.12f, 0.12f, 0.14f, 0.85f));

            Image bleedFill = BuildHudImage(clockGroup.transform, "BleedFill",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -330f), new Vector2(1100f, 46f),
                new Color(0.85f, 0.25f, 0.25f, 1f));
            bleedFill.type = Image.Type.Filled;
            bleedFill.fillMethod = Image.FillMethod.Horizontal;
            bleedFill.fillAmount = 0f;

            Text headline = BuildHudText(root.transform, "Headline", font, 110, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 60f), new Vector2(1600f, 180f));

            Text subline = BuildHudText(root.transform, "Subline", font, 52, TextAnchor.UpperCenter,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -80f), new Vector2(1500f, 220f));

            GameObject scoreGroup = new GameObject("ScoreboardGroup", typeof(RectTransform));
            scoreGroup.transform.SetParent(root.transform, false);
            StretchFull(scoreGroup.GetComponent<RectTransform>());

            Text scoreTable = BuildHudText(scoreGroup.transform, "ScoreTable", font, 64, TextAnchor.UpperCenter,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -140f), new Vector2(1100f, 460f));
            scoreTable.lineSpacing = 1.35f;

            ProjectionHUD hud = root.AddComponent<ProjectionHUD>();
            hud.Bind(session, null, leaderboard);
            hud.BindBleedSource(() =>
            {
                if (vessels == null || vessels.Length == 0) { return 0f; }

                int bleeding = 0;
                for (int i = 0; i < vessels.Length; i++)
                {
                    if (vessels[i] != null && vessels[i].IsBleeding) { bleeding++; }
                }

                return (float)bleeding / vessels.Length;
            });
            hud.BindWidgets(clock, bleedFill, vignette, headline, subline,
                clockGroup, scoreGroup, scoreTable);

            Debug.Log($"[Transplante] projeção -> Display {ProjectionDisplayIndex + 1} " +
                      "(canvas overlay; invisível ao headset)");
            return root;
        }

        /// <summary>Hooks the room's light to the clock, the same way SurgeryMVP does.</summary>
        private static void WireUrgencyTint(GameObject systems)
        {
            Light key = null;
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional)
                {
                    key = light;
                    break;
                }
            }

            if (key == null)
            {
                Debug.LogWarning("[Transplante] Nenhuma luz direcional encontrada; a sala não avermelhará com o relógio.");
            }

            SceneUrgencyTint tint = systems.AddComponent<SceneUrgencyTint>();
            tint.Bind(systems.GetComponent<EventSessionController>(), key);

            Debug.Log($"[Transplante] tom de urgência ligado a '{(key != null ? key.name : "nada")}'");
        }

        /// <summary>Moves the rig's own visuals off the layer the projection camera reads, so the
        /// audience sees the patient and not the operator's controllers or teleport gizmo.</summary>
        private static void HideOperatorVisualsFromProjection()
        {
            int layer = EnsureLayer(OperatorLayer);
            if (layer < 0) { return; }

            int moved = 0;
            int skipped = 0;

            foreach (string rootName in new[] { "XR Origin", "Teleport Anchor" })
            {
                GameObject root = GameObject.Find(rootName);
                if (root == null) { continue; }

                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    bool draws = t.GetComponent<Renderer>() != null
                              || t.GetComponent<Canvas>() != null
                              || t.GetComponent<CanvasRenderer>() != null;
                    if (!draws) { continue; }

                    if (t.GetComponent<Collider>() != null) { skipped++; continue; }

                    t.gameObject.layer = layer;
                    moved++;
                }
            }

            Debug.Log($"[Transplante] {moved} elemento(s) do operador movidos para '{OperatorLayer}' " +
                      $"({skipped} preservados por terem collider)");
        }

        /// <summary>Finds a layer by name, claiming the first free user slot if it does not exist.</summary>
        private static int EnsureLayer(string layerName)
        {
            int existing = LayerMask.NameToLayer(layerName);
            if (existing >= 0) { return existing; }

            SerializedObject tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");

            for (int i = 8; i < layers.arraySize; i++)
            {
                SerializedProperty slot = layers.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(slot.stringValue)) { continue; }

                slot.stringValue = layerName;
                tagManager.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                Debug.Log($"[Transplante] camada '{layerName}' criada no índice {i}");
                return i;
            }

            Debug.LogError($"[Transplante] Sem camada de usuário livre para '{layerName}'.");
            return -1;
        }

        /// <summary>
        /// Flat camera that mirrors the headset, for the spectator screen on Display 1.
        /// </summary>
        private static void BuildSpectatorCamera()
        {
            GameObject go = new GameObject("SpectatorCamera");

            Camera cam = go.AddComponent<Camera>();
            cam.targetDisplay = SpectatorDisplayIndex;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 60f;

            UnityEngine.Rendering.Universal.UniversalAdditionalCameraData data =
                go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            data.renderPostProcessing = false;
            data.allowXRRendering = false;

            HeadsetFollowCamera follow = go.AddComponent<HeadsetFollowCamera>();
            Camera head = Camera.main;
            if (head != null)
            {
                follow.Headset = head.transform;
                go.transform.SetPositionAndRotation(head.transform.position, head.transform.rotation);
            }
            else
            {
                Debug.LogWarning("[Transplante] Sem MainCamera para espelhar; a visão do espectador ficará parada.");
            }

            Debug.Log($"[Transplante] câmera de espectador -> Display {SpectatorDisplayIndex + 1}, " +
                      $"seguindo {(head != null ? head.name : "nada")}");
        }

        /// <summary>
        /// Overhead camera feeding the projector on Display 2, framed on the patient, the table
        /// and the donor stand, so spectators see the operative field without a headset.
        ///
        /// Position and framing carried over from SurgeryMVP's validated projection camera, not
        /// re-measured against this scene's own patient — like the anastomosis offsets, this
        /// needs to be checked against the real room before the stand opens.
        /// </summary>
        private static void BuildProjectionCamera(Vector3 thoraxCenter)
        {
            GameObject go = new GameObject("ProjectionCamera");
            go.transform.position = new Vector3(0f, 2.62f, thoraxCenter.z);
            go.transform.rotation = Quaternion.Euler(90f, 90f, 0f);

            Camera cam = go.AddComponent<Camera>();
            cam.targetDisplay = ProjectionDisplayIndex;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;

            int operatorLayer = LayerMask.NameToLayer(OperatorLayer);
            if (operatorLayer >= 0) { cam.cullingMask &= ~(1 << operatorLayer); }

            cam.orthographic = true;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 12f;
            cam.orthographicSize = ProjectionSizeFor(cam);

            UnityEngine.Rendering.Universal.UniversalAdditionalCameraData data =
                go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            data.renderPostProcessing = false;
            data.allowXRRendering = false;

            go.AddComponent<ProjectionDisplay>();

            Debug.Log($"[Transplante] câmera de projeção em {go.transform.position} " +
                      $"-> Display {ProjectionDisplayIndex + 1}, tamanho ortográfico {cam.orthographicSize:F3}");
        }

        /// <summary>Frames the patient, the table and the donor stand in the projection camera's view.</summary>
        private static float ProjectionSizeFor(Camera cam)
        {
            Bounds subject = default;
            bool any = false;

            foreach (string name in new[] { "Patient", "OperatingTable", "DonorStand" })
            {
                GameObject go = GameObject.Find(name);
                if (go == null) { continue; }

                foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
                {
                    if (!any) { subject = r.bounds; any = true; } else { subject.Encapsulate(r.bounds); }
                }
            }

            if (!any)
            {
                Debug.LogWarning("[Transplante] Nada para enquadrar; usando 1m de tamanho ortográfico.");
                return 1f;
            }

            float halfWidth = 0f;
            float halfHeight = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    (i & 1) == 0 ? subject.min.x : subject.max.x,
                    (i & 2) == 0 ? subject.min.y : subject.max.y,
                    (i & 4) == 0 ? subject.min.z : subject.max.z);

                Vector3 local = cam.transform.InverseTransformPoint(corner);
                halfWidth = Mathf.Max(halfWidth, Mathf.Abs(local.x));
                halfHeight = Mathf.Max(halfHeight, Mathf.Abs(local.y));
            }

            const float margin = 1.06f;
            float size = Mathf.Max(halfHeight, halfWidth) * margin;

            Debug.Log($"[Transplante] enquadrando sujeito {subject.size} -> meia-largura {halfWidth:F3}, " +
                      $"meia-altura {halfHeight:F3}, tamanho ortográfico {size:F3}");
            return size;
        }

        private static Image BuildHudImage(Transform parent, string name, Vector2 anchorMin,
            Vector2 anchorMax, Vector2 position, Vector2 size, Color colour)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;

            if (anchorMin == Vector2.zero && anchorMax == Vector2.one)
            {
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            else
            {
                rect.anchoredPosition = position;
                rect.sizeDelta = size;
            }

            Image image = go.AddComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        private static void StretchFull(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static Text BuildHudText(Transform parent, string name, Font font, int size,
            TextAnchor anchor, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 sizeDelta)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.anchoredPosition = position;
            rect.sizeDelta = sizeDelta;

            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            if (font == null)
            {
                Debug.LogError("[Transplante] Fonte interna ausente; a projeção ficará em branco.");
            }

            return text;
        }

        /// <summary>
        /// Marks what never moves as static, and stops what nobody can see from casting shadows.
        ///
        /// Measured before this existed: 46 of 53 renderers were dynamic, carrying 292.024 of the
        /// scene's 296.408 triangles, and 45 renderers were casting shadows from the room's single
        /// directional light — a second pass over almost the whole scene.
        ///
        /// Listed by name rather than inferred. A heuristic like "has no animating component" would
        /// quietly mark the sternum static the day someone renames SternotomyController, and a
        /// static object that moves renders in the wrong place with no error.
        /// </summary>
        private static void ApplyStaticAndShadowFlags()
        {
            // Never moves for the whole session.
            string[] immovable =
            {
                "TableModel", "Body_Skin", "RibcageModel", "StandColumn", "Basin", "Screen",
            };

            // Deliberately absent: SternumModel (the sternotomy animates it), Heart and DonorHeart
            // (the visitor carries them), and the monitor's TextMesh lines (their mesh is rebuilt
            // whenever the text changes, which static batching cannot follow).

            // Inside the patient or flat against a screen. Neither casts a shadow anyone can see,
            // and both were paying for one every frame.
            string[] noShadow =
            {
                "RibcageModel", "SternumModel", "Screen", "InstructionText", "ClockText", "ReasonText",
            };

            int marked = 0, unshadowed = 0;

            foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (System.Array.IndexOf(immovable, renderer.gameObject.name) >= 0)
                {
                    GameObjectUtility.SetStaticEditorFlags(renderer.gameObject,
                        StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI |
                        StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
                    marked++;
                }

                if (System.Array.IndexOf(noShadow, renderer.gameObject.name) >= 0 &&
                    renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                {
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    unshadowed++;
                }
            }

            // The room itself came from the template scene and never moves either.
            GameObject environment = GameObject.Find("Environment");
            if (environment != null)
            {
                foreach (Renderer renderer in environment.GetComponentsInChildren<Renderer>(true))
                {
                    GameObjectUtility.SetStaticEditorFlags(renderer.gameObject,
                        StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI |
                        StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
                    marked++;
                }
            }

            Debug.Log($"[Transplante] {marked} renderer(s) marcados static, " +
                      $"{unshadowed} deixaram de lançar sombra");
        }

        /// <summary>
        /// Says plainly whether the heart fits inside the cage, because the four models were
        /// generated apart and nothing guarantees they belong to the same body.
        /// </summary>
        private static void ReportFit(GameObject ribcage, GameObject heart)
        {
            Bounds cage = WorldBounds(ribcage);
            Bounds organ = WorldBounds(heart);

            bool inside = cage.Contains(organ.min) && cage.Contains(organ.max);
            float folgaX = (cage.size.x - organ.size.x) * 100f;
            float folgaY = (cage.size.y - organ.size.y) * 100f;
            float folgaZ = (cage.size.z - organ.size.z) * 100f;

            string verdict = inside
                ? $"[Transplante] AJUSTE OK: coração dentro do gradil. Folga X={folgaX:F1}cm " +
                  $"Y={folgaY:F1}cm Z={folgaZ:F1}cm"
                : $"[Transplante] AJUSTE RUIM: coração ultrapassa o gradil. Folga X={folgaX:F1}cm " +
                  $"Y={folgaY:F1}cm Z={folgaZ:F1}cm — regerar o gradil";

            if (inside) { Debug.Log(verdict); } else { Debug.LogWarning(verdict); }
        }

        // ---------------------------------------------------------------- helpers

        private static Bounds WorldBounds(GameObject go)
        {
            bool any = false;
            Bounds bounds = new Bounds();
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
            {
                if (!any) { bounds = r.bounds; any = true; } else { bounds.Encapsulate(r.bounds); }
            }

            return bounds;
        }

        private static Bounds LocalBounds(GameObject go, Transform space)
        {
            bool any = false;
            Bounds bounds = new Bounds();
            foreach (MeshFilter f in go.GetComponentsInChildren<MeshFilter>())
            {
                if (f.sharedMesh == null) { continue; }
                foreach (Vector3 v in f.sharedMesh.vertices)
                {
                    Vector3 p = space.InverseTransformPoint(f.transform.TransformPoint(v));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else { bounds.Encapsulate(p); }
                }
            }

            return bounds;
        }

        /// <summary>
        /// Moves glTF materials onto URP/Lit. gltFast imports on its own Shader Graph, which costs
        /// a Quest more and renders black when its variant is not compiled. The metallic-roughness
        /// map is left behind on purpose: glTF packs roughness in green and metallic in blue while
        /// URP reads metallic from red and smoothness from alpha, so copying it across would look
        /// right and be wrong in every channel.
        /// </summary>
        private static void ConvertGltfMaterials(GameObject model)
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) { return; }

            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
            {
                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material source = materials[i];
                    if (source == null || !source.shader.name.Contains("glTF")) { continue; }

                    Material target = new Material(lit) { name = source.name + "_URP" };

                    if (source.HasProperty("baseColorTexture"))
                    {
                        Texture albedo = source.GetTexture("baseColorTexture");
                        if (albedo != null) { target.SetTexture("_BaseMap", albedo); }
                    }

                    if (source.HasProperty("normalTexture"))
                    {
                        Texture normal = source.GetTexture("normalTexture");
                        if (normal != null)
                        {
                            target.SetTexture("_BumpMap", normal);
                            target.EnableKeyword("_NORMALMAP");
                        }
                    }

                    if (source.HasProperty("baseColorFactor"))
                    {
                        target.SetColor("_BaseColor", source.GetColor("baseColorFactor"));
                    }

                    target.SetFloat("_Metallic", 0.05f);
                    target.SetFloat("_Smoothness", 0.35f);
                    materials[i] = target;
                }

                renderer.sharedMaterials = materials;
            }
        }

        private static Material MakeMaterial(Color colour, float metallic, float smoothness)
        {
            Material m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = colour };
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        private static GameObject Instantiate(string path, Transform parent)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) { throw new System.IO.FileNotFoundException("Modelo ausente: " + path); }
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            return instance;
        }

        private static void RenameRig()
        {
            if (GameObject.Find("XR Origin") != null) { return; }
            foreach (GameObject go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (go.transform.parent == null && go.name.StartsWith("XR Origin"))
                {
                    go.name = "XR Origin";
                    return;
                }
            }
        }

        /// <summary>
        /// Where the surgeon stands: beside the chest, on the patient's left. Derived once and
        /// shared, because the monitor has to face this spot and a second copy of the number is a
        /// second chance for the two to drift apart.
        /// </summary>
        private static Vector3 Stance(Vector3 thorax) => new Vector3(thorax.x + 0.45f, 0f, thorax.z);

        /// <summary>Puts the surgeon beside the chest, on the patient's left, facing the thorax.</summary>
        private static void PlaceAnchor(Vector3 thorax)
        {
            Vector3 stance = Stance(thorax);
            Vector3 toWork = new Vector3(thorax.x - stance.x, 0f, thorax.z - stance.z);
            Quaternion facing = toWork.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(toWork.normalized, Vector3.up)
                : Quaternion.identity;

            GameObject anchor = GameObject.Find("Teleport Anchor");
            if (anchor != null) { anchor.transform.SetPositionAndRotation(stance, facing); }

            GameObject rig = GameObject.Find("XR Origin");
            if (rig != null) { rig.transform.SetPositionAndRotation(stance, facing); }

            Debug.Log($"[Transplante] cirurgião em {stance}, virado para o tórax");
        }

        private static void EnsureMainCamera()
        {
            foreach (Camera c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (c.CompareTag("MainCamera")) { return; }
            }

            foreach (Camera c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (c.name.Contains("Main")) { c.tag = "MainCamera"; return; }
            }
        }

        private static void RegisterSceneInBuildSettings()
        {
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (s.path == TargetScene) { return; }
            }

            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.Add(new EditorBuildSettingsScene(TargetScene, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        private static void SetPrivateField(object target, string field, object value)
        {
            System.Reflection.FieldInfo info = target.GetType().GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (info == null) { Debug.LogError($"[Transplante] sem campo '{field}'"); return; }
            info.SetValue(target, value);
        }
    }
}
