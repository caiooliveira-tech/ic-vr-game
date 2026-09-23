using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRSurgery.Cutting;
using VRSurgery.Data;
using VRSurgery.Tissue;
using VRSurgery.Tools;
using Object = UnityEngine.Object;

namespace VRSurgery.EditorTools
{
    /// <summary>Installs only the cutting exercise into an already saved, separate scene copy.</summary>
    public static class ThoraxPlayableSceneSetup
    {
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

        public static string Install()
        {
            var scene = SceneManager.GetActiveScene();
            if (EditorApplication.isPlaying || !scene.path.StartsWith("Assets/Scenes/ThoraxCuttingPlayable"))
                throw new InvalidOperationException("Open the preserved ThoraxCuttingPlayable copy in Edit mode.");
            if (GameObject.Find("ThoraxCuttingSession") != null)
                throw new InvalidOperationException("This copy already contains the cutting exercise.");

            GameObject body = GameObject.Find("Patient/Body_Skin");
            if (body == null) { throw new InvalidOperationException("Patient skin not found."); }
            Mesh source = body.GetComponent<MeshFilter>().sharedMesh;
            if (source == null || !source.isReadable) { throw new InvalidOperationException("Skin mesh unreadable."); }
            string folder = AssetDatabase.GenerateUniqueAssetPath("Assets/ThoraxPlayable");
            AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(folder));

            // Reuse the validated authored-thorax extraction without touching cutting topology.
            Mesh patch = (Mesh)typeof(CurvedSkinProbe).GetMethod("ExtractThoraxPatch", PrivateStatic)
                .Invoke(null, new object[] { source });
            patch.name = "Patient_ThoraxCuttable";
            AssetDatabase.CreateAsset(patch, folder + "/ThoraxSkin.asset");

            Vector3[] vertices = source.vertices;
            Bounds bounds = source.bounds;
            float cx = bounds.center.x;
            float cy = Mathf.Lerp(bounds.min.y, bounds.max.y, 0.741f);
            float front = float.MinValue;
            foreach (Vector3 p in vertices)
                if (Mathf.Abs(p.x - cx) < 0.025f && Mathf.Abs(p.y - cy) < 0.025f)
                    front = Mathf.Max(front, p.z);
            if (front == float.MinValue) { front = bounds.max.z; }

            // Exact inverse selection of the fixture. No intact surface beneath the active skin;
            // all source vertex channels and every untouched submesh retain their original data.
            Mesh remainder = Object.Instantiate(source);
            remainder.name = "Patient_SkinOutsideThorax";
            int removed = 0;
            for (int s = 0; s < source.subMeshCount; s++)
            {
                int[] input = source.GetTriangles(s);
                var keep = new List<int>(input.Length);
                for (int i = 0; i < input.Length; i += 3)
                {
                    Vector3 p = (vertices[input[i]] + vertices[input[i + 1]] + vertices[input[i + 2]]) / 3f;
                    float nx = (p.x - cx) / 0.105f, ny = (p.y - cy) / 0.078f;
                    float angle = Mathf.Atan2(ny, nx);
                    float rim = 1f + 0.045f * Mathf.Sin(angle * 3f + 0.7f) + 0.025f * Mathf.Sin(angle * 7f - 0.4f);
                    if (p.z > bounds.center.z && nx * nx + ny * ny <= rim * rim) { removed++; continue; }
                    keep.Add(input[i]); keep.Add(input[i + 1]); keep.Add(input[i + 2]);
                }
                remainder.SetTriangles(keep, s);
            }
            AssetDatabase.CreateAsset(remainder, folder + "/BodySkinRemainder.asset");
            body.GetComponent<MeshFilter>().sharedMesh = remainder;

            var skin = new GameObject("Thorax_CuttableSkin", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            skin.transform.SetParent(body.transform, false);
            skin.transform.localPosition = new Vector3(cx, cy, front);
            skin.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            skin.GetComponent<MeshFilter>().sharedMesh = patch;
            skin.GetComponent<MeshCollider>().sharedMesh = patch;
            skin.GetComponent<MeshRenderer>().sharedMaterials = body.GetComponent<MeshRenderer>().sharedMaterials;

            var inside = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                { name = "Thorax_Interior", color = new Color(0.46f, 0.22f, 0.20f) };
            inside.SetFloat("_Smoothness", 0.12f);
            AssetDatabase.CreateAsset(inside, folder + "/Interior.mat");
            var tissue = skin.AddComponent<CuttableTissue>();
            Set(tissue, "maximumDepth", 0.0055f);
            Set(tissue, "openingWidth", 0.0036f);
            Set(tissue, "minimumPathSpacing", 0.0015f);
            Set(tissue, "resistance", 0.28f);
            Set(tissue, "interiorMaterial", inside);
            tissue.ConfigureCutRegion(new Bounds(Vector3.zero, new Vector3(0.14f, 0.12f, 0.088f)), Vector3.up);
            skin.AddComponent<SkinDeformation>();
            skin.AddComponent<TissueSurface>();
            var incision = skin.AddComponent<IncisionSystem>();
            Set(incision, "cuttableTissue", tissue);

            var root = new GameObject("ThoraxCuttingSession");
            var scalpel = new GameObject("DesktopScalpel");
            scalpel.transform.SetParent(root.transform, false);
            var model = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Tools/SCALPEL_FromGLB.fbx"), scalpel.transform);
            typeof(SurgeryMvpSceneBuilder).GetMethod("AlignToolForward", PrivateStatic)
                .Invoke(null, new object[] { model, "filo" });
            var metal = (Material)typeof(SurgeryMvpSceneBuilder).GetMethod("MakeScalpelMaterial", PrivateStatic)
                .Invoke(null, null);
            AssetDatabase.CreateAsset(metal, folder + "/Scalpel.mat");
            Vector3 tipPosition = Vector3.zero;
            float furthest = float.MinValue;
            foreach (MeshRenderer r in model.GetComponentsInChildren<MeshRenderer>()) { r.sharedMaterial = metal; }
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.name != "filo") { continue; }
                foreach (Vector3 v in filter.sharedMesh.vertices)
                {
                    Vector3 p = scalpel.transform.InverseTransformPoint(filter.transform.TransformPoint(v));
                    if (p.z > furthest) { furthest = p.z; tipPosition = p; }
                }
            }
            var tipObject = new GameObject("BladeTip");
            tipObject.transform.SetParent(scalpel.transform, false);
            tipObject.transform.localPosition = tipPosition;
            var blade = tipObject.AddComponent<BladeTip>();
            var tool = scalpel.AddComponent<ScalpelTool>();
            tool.SetToolDefinition(AssetDatabase.LoadAssetAtPath<ToolDefinition>("Assets/Data/Tool_Scalpel.asset"));
            var rb = scalpel.GetComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true;
            var cutter = scalpel.AddComponent<CuttingInteractor>();
            Set(cutter, "bladeTip", blade);
            Set(cutter, "requireHeld", false);
            Set(tool, "bladeTip", blade);
            Set(tool, "cuttingInteractor", cutter);
            cutter.enabled = false;
            scalpel.transform.position = skin.transform.position + skin.transform.up * 0.04f - skin.transform.right * 0.09f;

            // These objects are retained, merely inactive in this desktop-only scene copy.
            // No transplant progression, organs, XR setup, HUD or source scene is rewritten.
            foreach (GameObject go in scene.GetRootGameObjects())
                if (go.name == "XR Origin" || go.name == "Hands Permissions Manager"
                    || go.name == "Systems" || go.name == "SurgeonMonitor") { go.SetActive(false); }

            var cameraObject = new GameObject("ThoraxDesktopCamera", typeof(Camera), typeof(AudioListener));
            cameraObject.transform.SetParent(root.transform, false);
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.fieldOfView = 42f;
            camera.nearClipPlane = 0.015f;
            camera.farClipPlane = 30f;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            Vector3 focus = skin.transform.position;
            camera.transform.position = focus + skin.transform.up * 0.54f + skin.transform.forward * 0.23f;
            camera.transform.rotation = Quaternion.LookRotation(focus - camera.transform.position, -skin.transform.forward);
            var lampObject = new GameObject("ThoraxExaminationLight", typeof(Light));
            lampObject.transform.SetParent(root.transform, false);
            lampObject.transform.position = focus + skin.transform.up * 0.6f - skin.transform.right * 0.2f;
            var lamp = lampObject.GetComponent<Light>();
            lamp.type = LightType.Directional;
            lamp.color = new Color(1f, 0.91f, 0.82f);
            lamp.intensity = 1.2f;
            lamp.range = 2f;
            lamp.shadows = LightShadows.None;
            lamp.transform.rotation = Quaternion.LookRotation(focus - lamp.transform.position, Vector3.forward);
            // The project's performance URP asset renders only the designated main light.
            RenderSettings.sun = lamp;
            var driver = root.AddComponent<ThoraxDesktopController>();
            Set(driver, "viewCamera", camera);
            Set(driver, "tissue", tissue);
            Set(driver, "incision", incision);
            Set(driver, "cutter", cutter);
            Set(driver, "blade", blade);
            Set(driver, "scalpel", scalpel.transform);

            EditorSceneManager.MarkSceneDirty(scene);
            AssetDatabase.SaveAssets();
            if (!EditorSceneManager.SaveScene(scene)) { throw new InvalidOperationException("Could not save playable scene."); }
            Selection.activeGameObject = skin;
            return $"Saved {scene.path}; thorax vertices={patch.vertexCount}; removed original triangles={removed}; assets={folder}";
        }

        private static void Set(Object target, string name, object value)
        {
            var so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(name);
            if (p == null) { throw new MissingFieldException(target.GetType().Name, name); }
            if (value is Object o) { p.objectReferenceValue = o; }
            else if (value is bool b) { p.boolValue = b; }
            else if (value is float f) { p.floatValue = f; }
            else { throw new ArgumentException("Unsupported serialized type"); }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
