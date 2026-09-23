using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRSurgery.Cutting;

namespace VRSurgery.EditorTools
{
    /// <summary>
    /// Isolated CurvedSkinTest fixture. The skin is not a primitive: it is a subdivided crop of
    /// the project's authored human thorax, with its UVs, normals, tangents and material retained.
    /// </summary>
    public static class CurvedSkinProbe
    {
        private const string SkinAsset = "Assets/Models/Patient/PATIENT_BodySkin.glb";
        private const string ScalpelAsset = "Assets/Models/Tools/SCALPEL_FromGLB.fbx";

        [MenuItem("VRSurgery/Incisão/Validar CurvedSkinTest")]
        public static void RunFromMenu() => Run("/tmp/curved-skin-test.png", false);

        public static void RunFromCommandLine()
        {
            string output = "/tmp/curved-skin-test.png";
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-cutOut") { output = args[i + 1]; }
            }

            bool success = Run(output, true);
            Debug.Log(success ? "@@CURVED_CUT_DONE" : "@@CURVED_CUT_FAILED");
            EditorApplication.Exit(success ? 0 : 1);
        }

        private static bool Run(string output, bool commandLine)
        {
            GameObject sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(SkinAsset);
            MeshFilter sourceFilter = sourceAsset != null
                ? sourceAsset.GetComponentInChildren<MeshFilter>()
                : null;
            MeshRenderer sourceRenderer = sourceFilter != null
                ? sourceFilter.GetComponent<MeshRenderer>()
                : null;
            if (sourceFilter == null || sourceFilter.sharedMesh == null)
            {
                Debug.LogError("[CurvedSkinTest] Pele humana de origem não encontrada: " + SkinAsset);
                return false;
            }

            Mesh patch = ExtractThoraxPatch(sourceFilter.sharedMesh);
            if (patch == null || patch.vertexCount < 100)
            {
                Debug.LogError("[CurvedSkinTest] A extração do tórax não produziu uma malha válida.");
                return false;
            }

            GameObject root = new GameObject("CurvedSkinTest");
            GameObject skin = new GameObject("OrganicThoraxSkin",
                typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            skin.transform.SetParent(root.transform, false);
            skin.GetComponent<MeshFilter>().sharedMesh = patch;
            MeshRenderer renderer = skin.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = ResolveSkinMaterials(sourceRenderer, patch.subMeshCount);
            skin.GetComponent<MeshCollider>().sharedMesh = patch;

            Material inside = MakeMaterial("CurvedSkinTest_Interior",
                new Color(0.46f, 0.22f, 0.20f), 0.12f);
            CuttableTissue tissue = skin.AddComponent<CuttableTissue>();
            tissue.Configure(0.0055f, 0.0036f, 0.0015f, 0.28f, inside);
            tissue.ConfigureCutRegion(new Bounds(Vector3.zero, new Vector3(0.15f, 0.12f, 0.10f)), Vector3.up);

            BuildSubdermalBacking(root.transform, patch);
            AddScalpel(root.transform);
            Physics.SyncTransforms();

            MeshIncision.Result first = CutProjectedPath(tissue, skin.GetComponent<MeshCollider>(), -0.010f, 0.2f,
                root, output);
            MeshIncision.Result second = CutProjectedPath(tissue, skin.GetComponent<MeshCollider>(), 0.020f, 2.0f);
            for (int i = 0; i < 4; i++) { tissue.AdvanceSimulation(0.25f); }
            Mesh finalMesh = skin.GetComponent<MeshFilter>().sharedMesh;
            int verticesBeforeRejectedCut = finalMesh.vertexCount;
            MeshIncision.PathPoint[] outside = {
                new MeshIncision.PathPoint(new Vector3(-0.01f, 0f, 0.09f), Vector3.up, 1f),
                new MeshIncision.PathPoint(new Vector3(0.01f, 0f, 0.09f), Vector3.up, 1f),
            };
            bool regionRejected = !tissue.ApplyIncision(outside).Success
                && finalMesh.vertexCount == verticesBeforeRejectedCut;
            bool attributesValid = finalMesh.normals.Length == finalMesh.vertexCount
                && finalMesh.uv.Length == finalMesh.vertexCount
                && (patch.tangents.Length == 0 || finalMesh.tangents.Length == finalMesh.vertexCount);
            bool valid = first.Success && second.Success
                && finalMesh.subMeshCount > patch.subMeshCount && regionRejected && attributesValid;
            Debug.Log($"@@THORAX_PROGRESSIVE revisions={tissue.PreviewRevision} regionRejected={regionRejected} " +
                $"attributes={attributesValid} points={first.PathPoints + second.PathPoints}");

            Debug.Log($"@@CURVED_CUT first={first.Success} second={second.Success} " +
                $"points={first.PathPoints + second.PathPoints} " +
                $"affected={first.AffectedTriangles + second.AffectedTriangles} " +
                $"verts={first.VerticesBefore}->{second.VerticesAfter} " +
                $"ms={(first.RebuildMilliseconds + second.RebuildMilliseconds):F2} " +
                $"source=PATIENT_BodySkin.glb organic=true");

            if (valid)
            {
                Render(root, output);
                Debug.Log("[CurvedSkinTest] Captura: " + output);
            }
            else
            {
                Debug.LogError($"[CurvedSkinTest] Falha: primeira='{first.Failure}', segunda='{second.Failure}'.");
            }

            Object.DestroyImmediate(root);
            Object.DestroyImmediate(inside);
            if (commandLine) { AssetDatabase.Refresh(); }
            return valid;
        }

        private static MeshIncision.Result CutProjectedPath(CuttableTissue tissue,
            MeshCollider collider, float zOffset, float phase, GameObject captureRoot = null, string capturePath = null)
        {
            List<MeshIncision.PathPoint> path = new List<MeshIncision.PathPoint>(25);
            for (int i = 0; i < 25; i++)
            {
                float t = i / 24f;
                float x = Mathf.Lerp(-0.050f, 0.050f, t);
                // A controlled hand-drawn curve, not a ruler-straight seam.
                float z = zOffset + 0.0042f * Mathf.Sin(t * Mathf.PI + phase)
                    + 0.0012f * Mathf.Sin(t * Mathf.PI * 3f + phase * 0.7f);
                Ray ray = new Ray(new Vector3(x, 0.10f, z), Vector3.down);
                if (!collider.Raycast(ray, out RaycastHit hit, 0.20f)) { continue; }
                float depth = 0.78f + 0.12f * Mathf.Sin(t * Mathf.PI);
                path.Add(new MeshIncision.PathPoint(hit.point, hit.normal, depth));
            }
            Mesh contactSnapshot = collider.sharedMesh;
            bool stableContact = true;
            int before = tissue.PreviewRevision;
            double worstRebuild = 0d;
            double worstTick = 0d;
            for (int i = 0; i < path.Count; i++)
            {
                MeshIncision.PathPoint point = path[i];
                CuttableTissue.Contact contact = new CuttableTissue.Contact(true, true,
                    point.Position, point.Normal, point.Depth01);
                if (i == 0) { tissue.BeginCut(contact); }
                else { tissue.ContinueCut(contact); }
                var timer = System.Diagnostics.Stopwatch.StartNew();
                tissue.AdvanceSimulation(0.08f);
                timer.Stop();
                worstTick = System.Math.Max(worstTick, timer.Elapsed.TotalMilliseconds);
                worstRebuild = System.Math.Max(worstRebuild, tissue.LastPreview.RebuildMilliseconds);
                stableContact &= collider.sharedMesh == contactSnapshot;
                if (captureRoot != null && (i == 8 || i == 16))
                {
                    string stage = Path.Combine(Path.GetDirectoryName(capturePath) ?? "/tmp",
                        Path.GetFileNameWithoutExtension(capturePath) + $"-stage-{i:D2}.png");
                    Render(captureRoot, stage);
                    Debug.Log($"@@THORAX_STAGE sample={i} recording={tissue.IsRecording} " +
                        $"revision={tissue.PreviewRevision} capture={stage}");
                }
            }
            bool grewBeforeEnd = tissue.IsRecording && tissue.PreviewRevision >= before + 2;
            MeshIncision.Result result = tissue.EndCut();
            Debug.Log($"@@THORAX_STROKE grewBeforeEnd={grewBeforeEnd} stableContact={stableContact} " +
                $"colliderCommitted={collider.sharedMesh != contactSnapshot} worstRebuildMs={worstRebuild:F2} " +
                $"worstTickMs={worstTick:F2}");
            if (!grewBeforeEnd || !stableContact)
            {
                return new MeshIncision.Result(false, "Falha na progressão ou estabilidade do contato.",
                    path.Count, 0, 0, 0, 0d, -1);
            }
            return result;
        }

        private static Mesh ExtractThoraxPatch(Mesh source)
        {
            Vector3[] sourceVertices = source.vertices;
            Vector3[] sourceNormals = source.normals;
            Vector4[] sourceTangents = source.tangents;
            Vector2[] sourceUv = source.uv;
            bool hasNormals = sourceNormals.Length == sourceVertices.Length;
            bool hasTangents = sourceTangents.Length == sourceVertices.Length;
            bool hasUv = sourceUv.Length == sourceVertices.Length;
            Bounds bounds = source.bounds;
            float centreX = bounds.center.x;
            float centreY = Mathf.Lerp(bounds.min.y, bounds.max.y, 0.741f);

            float surfaceZ = float.MinValue;
            for (int i = 0; i < sourceVertices.Length; i++)
            {
                Vector3 v = sourceVertices[i];
                if (Mathf.Abs(v.x - centreX) < 0.025f && Mathf.Abs(v.y - centreY) < 0.025f)
                {
                    surfaceZ = Mathf.Max(surfaceZ, v.z);
                }
            }
            if (surfaceZ == float.MinValue) { surfaceZ = bounds.max.z; }

            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector4> tangents = new List<Vector4>();
            List<Vector2> uv = new List<Vector2>();
            List<int>[] triangles = new List<int>[Mathf.Max(1, source.subMeshCount)];
            for (int s = 0; s < triangles.Length; s++) { triangles[s] = new List<int>(); }
            Dictionary<int, int> remap = new Dictionary<int, int>();

            int Map(int sourceIndex)
            {
                if (remap.TryGetValue(sourceIndex, out int mapped)) { return mapped; }
                Vector3 p = sourceVertices[sourceIndex];
                mapped = vertices.Count;
                // Proper rotation: source +Z (chest front) becomes local +Y, source +Y becomes -Z.
                vertices.Add(new Vector3(p.x - centreX, p.z - surfaceZ, -(p.y - centreY)));
                if (hasNormals)
                {
                    Vector3 n = sourceNormals[sourceIndex];
                    normals.Add(new Vector3(n.x, n.z, -n.y).normalized);
                }
                if (hasTangents)
                {
                    Vector4 t = sourceTangents[sourceIndex];
                    tangents.Add(new Vector4(t.x, t.z, -t.y, t.w));
                }
                if (hasUv) { uv.Add(sourceUv[sourceIndex]); }
                remap[sourceIndex] = mapped;
                return mapped;
            }

            for (int s = 0; s < triangles.Length; s++)
            {
                int[] input = source.GetTriangles(s);
                for (int i = 0; i < input.Length; i += 3)
                {
                    Vector3 centre = (sourceVertices[input[i]] + sourceVertices[input[i + 1]]
                        + sourceVertices[input[i + 2]]) / 3f;
                    if (centre.z <= bounds.center.z) { continue; }
                    float nx = (centre.x - centreX) / 0.105f;
                    float ny = (centre.y - centreY) / 0.078f;
                    float angle = Mathf.Atan2(ny, nx);
                    float organicRim = 1f + 0.045f * Mathf.Sin(angle * 3f + 0.7f)
                        + 0.025f * Mathf.Sin(angle * 7f - 0.4f);
                    if (nx * nx + ny * ny > organicRim * organicRim) { continue; }
                    triangles[s].Add(Map(input[i]));
                    triangles[s].Add(Map(input[i + 1]));
                    triangles[s].Add(Map(input[i + 2]));
                }
            }

            // The authored body is intentionally Quest-friendly and coarse. Uniform subdivision
            // is safe here because this is an isolated patch: there is no untouched neighbour in
            // which it could create T-junctions, and the interpolated authored normals stay smooth.
            for (int pass = 0; pass < 3; pass++)
            {
                SubdivideOnce(vertices, normals, tangents, uv, triangles,
                    hasNormals, hasTangents, hasUv);
            }
            Mesh result = new Mesh { name = "CurvedSkinTest_OrganicThorax" };
            result.indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            result.SetVertices(vertices);
            if (hasNormals) { result.SetNormals(normals); }
            if (hasTangents) { result.SetTangents(tangents); }
            if (hasUv) { result.SetUVs(0, uv); }
            result.subMeshCount = triangles.Length;
            for (int s = 0; s < triangles.Length; s++) { result.SetTriangles(triangles[s], s); }
            if (!hasNormals) { result.RecalculateNormals(); }
            result.RecalculateBounds();
            return result;
        }

        private static void SubdivideOnce(List<Vector3> vertices, List<Vector3> normals,
            List<Vector4> tangents, List<Vector2> uv, List<int>[] submeshes,
            bool hasNormals, bool hasTangents, bool hasUv)
        {
            Dictionary<long, int> cache = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = (long)Mathf.Min(a, b) << 32 | (uint)Mathf.Max(a, b);
                if (cache.TryGetValue(key, out int existing)) { return existing; }
                int index = vertices.Count;
                vertices.Add((vertices[a] + vertices[b]) * 0.5f);
                if (hasNormals) { normals.Add((normals[a] + normals[b]).normalized); }
                if (hasTangents) { tangents.Add(Vector4.Lerp(tangents[a], tangents[b], 0.5f)); }
                if (hasUv) { uv.Add((uv[a] + uv[b]) * 0.5f); }
                cache[key] = index;
                return index;
            }

            for (int s = 0; s < submeshes.Length; s++)
            {
                List<int> input = submeshes[s];
                List<int> output = new List<int>(input.Count * 4);
                for (int i = 0; i < input.Count; i += 3)
                {
                    int a = input[i], b = input[i + 1], c = input[i + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    output.Add(a); output.Add(ab); output.Add(ca);
                    output.Add(ab); output.Add(b); output.Add(bc);
                    output.Add(ca); output.Add(bc); output.Add(c);
                    output.Add(ab); output.Add(bc); output.Add(ca);
                }
                submeshes[s] = output;
            }
        }

        private static Material[] ResolveSkinMaterials(MeshRenderer source, int count)
        {
            // glTFast may keep embedded textures reachable through the imported material without
            // exposing them as ordinary subassets. Read that binding first, then use subassets as
            // a fallback, and rebind to a plain URP/Lit material for deterministic batch renders.
            Texture2D albedo = null;
            Texture2D normal = null;
            if (source != null)
            {
                foreach (Material imported in source.sharedMaterials)
                {
                    if (imported == null) { continue; }
                    foreach (string property in imported.GetTexturePropertyNames())
                    {
                        Texture texture = imported.GetTexture(property);
                        if (texture == null) { continue; }
                        string identity = (property + " " + texture.name).ToLowerInvariant();
                        if (albedo == null && (identity.Contains("base")
                            || identity.Contains("albedo") || texture.name == "Image_0"))
                        {
                            albedo = texture as Texture2D;
                        }
                        if (normal == null && (identity.Contains("normal")
                            || texture.name == "Image_2"))
                        {
                            normal = texture as Texture2D;
                        }
                    }
                }
            }

            foreach (Object subAsset in AssetDatabase.LoadAllAssetsAtPath(SkinAsset))
            {
                if (subAsset is not Texture2D texture) { continue; }
                if (albedo == null && texture.name == "Image_0") { albedo = texture; }
                else if (normal == null && texture.name == "Image_2") { normal = texture; }
            }

            Material fallback = MakeMaterial(
                "CurvedSkinTest_Skin", Color.white, 0.20f);
            if (albedo != null) { fallback.SetTexture("_BaseMap", albedo); }
            if (normal != null)
            {
                fallback.SetTexture("_BumpMap", normal);
                fallback.EnableKeyword("_NORMALMAP");
                fallback.SetFloat("_BumpScale", 0.55f);
            }
            Debug.Log($"[CurvedSkinTest] material orgânico: albedo=" +
                $"{(albedo != null ? albedo.name : "ausente")}, normal=" +
                $"{(normal != null ? normal.name : "ausente")}");
            Material[] result = new Material[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = fallback;
            }
            return result;
        }

        private static void BuildSubdermalBacking(Transform parent, Mesh surface)
        {
            Mesh backing = Object.Instantiate(surface);
            backing.name = "CurvedSkinTest_SubdermalBacking";
            Vector3[] vertices = backing.vertices;
            Vector3[] normals = backing.normals;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 normal = normals.Length == vertices.Length ? normals[i] : Vector3.up;
                vertices[i] -= normal * 0.0075f;
            }
            backing.vertices = vertices;
            backing.RecalculateBounds();

            GameObject go = new GameObject("SubdermalBacking", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = backing;
            go.GetComponent<MeshRenderer>().sharedMaterial = MakeMaterial(
                "CurvedSkinTest_Subdermal", new Color(0.55f, 0.29f, 0.24f), 0.16f);
        }

        private static void AddScalpel(Transform parent)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(ScalpelAsset);
            if (asset == null) { return; }
            GameObject scalpel = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
            scalpel.name = "Scalpel_VisualReference";
            scalpel.transform.localPosition = new Vector3(0.060f, 0.030f, -0.040f);
            scalpel.transform.localRotation = Quaternion.Euler(18f, -35f, 82f);
        }

        private static Material MakeMaterial(string name, Color color, float smoothness)
        {
            Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
            {
                name = name,
                color = color,
            };
            material.SetFloat("_Smoothness", smoothness);
            return material;
        }

        private static void Render(GameObject root, string path)
        {
            GameObject keyObject = new GameObject("Key", typeof(Light));
            keyObject.transform.SetParent(root.transform, false);
            keyObject.transform.rotation = Quaternion.Euler(48f, -28f, 0f);
            Light key = keyObject.GetComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = 1.45f;

            GameObject fillObject = new GameObject("Fill", typeof(Light));
            fillObject.transform.SetParent(root.transform, false);
            fillObject.transform.position = new Vector3(-0.10f, 0.13f, -0.05f);
            Light fill = fillObject.GetComponent<Light>();
            fill.type = LightType.Point;
            fill.range = 0.6f;
            fill.intensity = 4.5f;
            fill.color = new Color(0.72f, 0.82f, 1f);

            Camera camera = new GameObject("Camera", typeof(Camera)).GetComponent<Camera>();
            camera.transform.SetParent(root.transform, false);
            camera.transform.position = new Vector3(0.010f, 0.125f, -0.090f);
            camera.transform.rotation = Quaternion.LookRotation(
                new Vector3(0f, -0.010f, 0f) - camera.transform.position, Vector3.up);
            camera.fieldOfView = 32f;
            camera.nearClipPlane = 0.01f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.045f, 0.055f);

            RenderTexture target = new RenderTexture(1400, 900, 24) { antiAliasing = 4 };
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            Texture2D image = new Texture2D(1400, 900, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 1400, 900), 0, 0);
            image.Apply();
            RenderTexture.active = null;

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
            File.WriteAllBytes(path, image.EncodeToPNG());
            camera.targetTexture = null;
            Object.DestroyImmediate(camera.gameObject);
            Object.DestroyImmediate(keyObject);
            Object.DestroyImmediate(fillObject);
            Object.DestroyImmediate(target);
            Object.DestroyImmediate(image);
        }
    }
}
