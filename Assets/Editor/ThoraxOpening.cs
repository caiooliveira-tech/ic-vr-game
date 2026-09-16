using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRSurgery.EditorTools
{
    /// <summary>
    /// Bakes the open thorax: a window removed from the chest skin over the sternum, and a lined
    /// cavity underneath so the opening shows tissue instead of the operating table.
    ///
    /// Runs ONCE, in the editor, and writes mesh assets. The rule against runtime mesh deformation
    /// stands — at play time these are ordinary static meshes. The source .glb is never modified;
    /// everything lands in Generated/, so deleting that folder undoes all of it.
    ///
    /// -------------------------------------------------------------------------------------
    /// WHY NOT MeshIncision, WHICH THE PROJECT ALREADY HAS
    ///
    /// The first version of this used it, and the result was unusable. Three separate failures,
    /// all measured and photographed:
    ///
    ///  1. MeshIncision.Cut ends with mesh.RecalculateNormals(), which replaces the body's
    ///     authored smooth normals with computed ones. On a shell-soup mesh — and this body is
    ///     one — that shatters the shading of the ENTIRE model, arms and legs included, nowhere
    ///     near the cut.
    ///  2. Parting the lips 3cm on a mesh with ~8mm vertex spacing, with the retraction falling
    ///     off over open*6 = 18cm, sheared the whole chest: the body's bounds grew from
    ///     56.6 x 34.6cm to 60.9 x 37.3cm. The patient inflated.
    ///  3. The wound walls stitched between rim pairs came out as spikes radiating from the cut.
    ///
    /// None of that is a defect in MeshIncision. It is built for a 4mm incision on a tessellated
    /// patch, and 3cm on coarse body geometry is seven times outside its design range.
    ///
    /// So this removes triangles instead of moving them. Nothing in the skin's vertex or normal
    /// arrays is touched — the hole is made purely by dropping triangles from the index buffer,
    /// which is why the shading survives intact.
    /// -------------------------------------------------------------------------------------
    /// </summary>
    public static class ThoraxOpening
    {
        private const string SkinGlb = "Assets/Models/Patient/PATIENT_BodySkin.glb";
        private const string OutputFolder = "Assets/Models/Patient/Generated";
        private const string OpenSkinAsset = OutputFolder + "/PATIENT_BodySkin_Aberto.asset";
        private const string CavityAsset = OutputFolder + "/PATIENT_Cavidade.asset";

        /// <summary>Middle of the opening, as a fraction of stature measured from the soles.</summary>
        private const float WindowCentreOfStature = 0.741f;

        /// <summary>Half-width of the opening across the chest, in metres.</summary>
        private const float WindowHalfWidth = 0.055f;

        /// <summary>Half-height of the opening along the body, in metres. A sternotomy is long.</summary>
        private const float WindowHalfHeight = 0.085f;

        /// <summary>How deep the cavity floor sits below the skin, in metres.</summary>
        private const float CavityDepth = 0.045f;

        /// <summary>
        /// How many times the triangles around the window are split before the hole is cut.
        ///
        /// DESLIGADO (0). A ideia era suavizar a borda: o corpo tem um vértice a cada 8mm, e
        /// remover triângulos inteiros dessa malha deixa a borda ziguezagueando de triângulo em
        /// triângulo.
        ///
        /// Mas esta subdivisão é ADAPTATIVA — só quarteia os triângulos perto da janela — e isso
        /// cria T-junctions em todo o anel onde o trecho subdividido encontra o resto da pele: o
        /// vizinho não subdividido não conhece o vértice que apareceu no meio da aresta dele.
        /// Numa malha de casca solta como esta, o resultado são fendas e costuras visíveis
        /// espalhadas pelo peito — pior que a borda serrilhada que eu queria corrigir.
        ///
        /// Suavizar a borda sem isso é possível projetando os vértices do laço de borda sobre a
        /// elipse, o que não cria vértice nenhum e portanto não cria T-junction. Não está feito
        /// porque não consegui verificar visualmente; fica registrado como o próximo passo.
        /// </summary>
        private const int WindowSubdivisions = 0;

        [MenuItem("VRSurgery/Tórax — gerar abertura")]
        public static void Generate()
        {
            Mesh source = LoadSourceMesh();
            if (source == null) { return; }

            if (!AssetDatabase.IsValidFolder(OutputFolder))
            {
                AssetDatabase.CreateFolder("Assets/Models/Patient", "Generated");
            }

            Bounds local = source.bounds;
            float centreY = Mathf.Lerp(local.min.y, local.max.y, WindowCentreOfStature);

            List<Vector3> vertexList = new List<Vector3>(source.vertices);
            List<Vector3> normalList = new List<Vector3>(source.normals);
            List<Vector2> uvList = new List<Vector2>(source.uv);
            List<int> triangleList = new List<int>(source.triangles);

            int before = triangleList.Count / 3;
            Subdivide(vertexList, normalList, uvList, triangleList, local, centreY);

            Vector3[] vertices = vertexList.ToArray();
            Vector3[] normals = normalList.ToArray();
            int[] triangles = triangleList.ToArray();

            Debug.Log($"[Tórax] subdivisão: {before} → {triangles.Length / 3} triângulos " +
                      $"({WindowSubdivisions} passes ao redor da janela)");

            // ---- which triangles fall in the window ----------------------------------------
            List<int> kept = new List<int>(triangles.Length);
            List<int> removed = new List<int>();

            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                Vector3 centroid = (vertices[a] + vertices[b] + vertices[c]) / 3f;

                if (InWindow(centroid, local, centreY))
                {
                    removed.Add(a); removed.Add(b); removed.Add(c);
                }
                else
                {
                    kept.Add(a); kept.Add(b); kept.Add(c);
                }
            }

            if (removed.Count == 0)
            {
                Debug.LogError("[Tórax] a janela não pegou triângulo nenhum — conferir " +
                               "WindowCentreOfStature e as meias-medidas.");
                return;
            }

            // ---- the skin, minus the window --------------------------------------------------
            // Normals are carried over rather than recalculated. That is the whole reason this
            // shades like the original body: the first attempt recalculated them and shattered
            // the look of the entire model, arms and legs included.
            Mesh openSkin = new Mesh { name = "PATIENT_BodySkin_Aberto" };
            openSkin.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            openSkin.SetVertices(vertexList);
            openSkin.SetNormals(normalList);
            if (uvList.Count == vertexList.Count) { openSkin.SetUVs(0, uvList); }
            openSkin.SetTriangles(kept, 0);
            openSkin.RecalculateBounds();

            Mesh cavity = BuildCavity(vertices, normals, removed, local, centreY);

            Write(openSkin, OpenSkinAsset);
            Write(cavity, CavityAsset);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Tórax] janela de {WindowHalfWidth * 200f:F1} x {WindowHalfHeight * 200f:F1}cm: " +
                      $"{removed.Count / 3} triângulos removidos da pele, {kept.Count / 3} mantidos. " +
                      $"Normais originais preservadas.");
        }

        /// <summary>
        /// Quarters the triangles around the window so the hole's rim can follow a curve instead
        /// of the body's original tessellation.
        ///
        /// Only triangles with a vertex inside a generously expanded window are split, and the
        /// split is by edge midpoint with position, normal and UV interpolated — so the surface
        /// keeps its shape and its texture, it simply gains resolution where the cut needs it.
        /// Midpoints are cached per edge, otherwise neighbouring triangles each create their own
        /// copy and the patch comes apart along every shared edge.
        /// </summary>
        private static void Subdivide(List<Vector3> vertices, List<Vector3> normals,
            List<Vector2> uvs, List<int> triangles, Bounds local, float centreY)
        {
            bool hasNormals = normals.Count == vertices.Count;
            bool hasUvs = uvs.Count == vertices.Count;

            for (int pass = 0; pass < WindowSubdivisions; pass++)
            {
                Dictionary<long, int> midpoints = new Dictionary<long, int>();
                List<int> next = new List<int>(triangles.Count * 2);

                int Mid(int a, int b)
                {
                    long key = (long)Mathf.Min(a, b) * 1000000L + Mathf.Max(a, b);
                    if (midpoints.TryGetValue(key, out int cached)) { return cached; }

                    int index = vertices.Count;
                    vertices.Add((vertices[a] + vertices[b]) * 0.5f);
                    if (hasNormals) { normals.Add((normals[a] + normals[b]).normalized); }
                    if (hasUvs) { uvs.Add((uvs[a] + uvs[b]) * 0.5f); }

                    midpoints[key] = index;
                    return index;
                }

                for (int t = 0; t < triangles.Count; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];

                    if (!NearWindow(vertices[a], local, centreY) &&
                        !NearWindow(vertices[b], local, centreY) &&
                        !NearWindow(vertices[c], local, centreY))
                    {
                        next.Add(a); next.Add(b); next.Add(c);
                        continue;
                    }

                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);

                    next.Add(a); next.Add(ab); next.Add(ca);
                    next.Add(ab); next.Add(b); next.Add(bc);
                    next.Add(ca); next.Add(bc); next.Add(c);
                    next.Add(ab); next.Add(bc); next.Add(ca);
                }

                triangles.Clear();
                triangles.AddRange(next);
            }
        }

        /// <summary>
        /// A generous margin around the window. Wider than the hole on purpose: the triangles that
        /// straddle the boundary are exactly the ones whose resolution decides how ragged the rim
        /// looks, and they have vertices outside it.
        /// </summary>
        private static bool NearWindow(Vector3 point, Bounds local, float centreY)
        {
            if (point.z <= local.center.z) { return false; }

            float x = (point.x - local.center.x) / (WindowHalfWidth * 1.8f);
            float y = (point.y - centreY) / (WindowHalfHeight * 1.8f);
            return x * x + y * y <= 1f;
        }

        /// <summary>
        /// An ellipse over the sternum, on the front of the body only.
        ///
        /// Elliptical rather than rectangular because a rectangle leaves four corners where the
        /// skin ends in a right angle, and nothing on a body does that.
        /// </summary>
        private static bool InWindow(Vector3 point, Bounds local, float centreY)
        {
            // Front half only: the same ellipse on the back would open a second hole under the
            // patient, through the table.
            if (point.z <= local.center.z) { return false; }

            float x = (point.x - local.center.x) / WindowHalfWidth;
            float y = (point.y - centreY) / WindowHalfHeight;
            return x * x + y * y <= 1f;
        }

        /// <summary>
        /// What is seen through the opening: the removed triangles themselves, pushed inward, plus
        /// a wall joining them to the rim of the hole so the skin reads as having thickness.
        ///
        /// Derived from the removed geometry rather than from the whole body, which is what stops
        /// it poking out through the shoulder and the flank — the failure the first version had.
        /// It exists only under the hole, so it cannot appear anywhere else.
        ///
        /// It is not anatomy. There is no muscle, no fascia, no mediastinum. What it does is stop
        /// the opening being a hole straight through the patient to the table.
        /// </summary>
        private static Mesh BuildCavity(Vector3[] vertices, Vector3[] normals, List<int> removed,
            Bounds local, float centreY)
        {
            Dictionary<int, int> floor = new Dictionary<int, int>();
            List<Vector3> outVertices = new List<Vector3>();
            List<int> outTriangles = new List<int>();

            int Sink(int index)
            {
                if (floor.TryGetValue(index, out int mapped)) { return mapped; }

                Vector3 inward = normals != null && normals.Length == vertices.Length
                    ? -normals[index].normalized
                    : (new Vector3(local.center.x, centreY, local.center.z) - vertices[index]).normalized;

                mapped = outVertices.Count;
                outVertices.Add(vertices[index] + inward * CavityDepth);
                floor[index] = mapped;
                return mapped;
            }

            for (int t = 0; t < removed.Count; t += 3)
            {
                outTriangles.Add(Sink(removed[t]));
                outTriangles.Add(Sink(removed[t + 1]));
                outTriangles.Add(Sink(removed[t + 2]));
            }

            // ---- the rim: edges used by exactly one removed triangle --------------------------
            Dictionary<long, int> edgeUse = new Dictionary<long, int>();
            for (int t = 0; t < removed.Count; t += 3)
            {
                Count(removed[t], removed[t + 1]);
                Count(removed[t + 1], removed[t + 2]);
                Count(removed[t + 2], removed[t]);
            }

            void Count(int a, int b)
            {
                long key = (long)Mathf.Min(a, b) * 1000000L + Mathf.Max(a, b);
                edgeUse[key] = edgeUse.TryGetValue(key, out int n) ? n + 1 : 1;
            }

            // The wall carries its own vertices, shared with nothing.
            //
            // The previous version reused the floor's vertices and emitted both windings on top of
            // them. RecalculateNormals then averaged two opposing faces at every vertex, the
            // normals cancelled, and the rim rendered as bright shards standing up out of the
            // chest. Separate vertices per face means each one gets the normal it actually has.
            int walls = 0;
            foreach (KeyValuePair<long, int> edge in edgeUse)
            {
                if (edge.Value != 1) { continue; }

                int a = (int)(edge.Key / 1000000L);
                int b = (int)(edge.Key % 1000000L);

                Vector3 topA = vertices[a], topB = vertices[b];
                Vector3 lowA = outVertices[Sink(a)], lowB = outVertices[Sink(b)];

                // Both facings, each on its own four vertices, because the rim is looked at from
                // wherever the surgeon's head happens to be and a one-sided wall disappears when
                // they lean across the patient.
                Quad(topA, lowA, lowB, topB);
                Quad(topB, lowB, lowA, topA);
                walls++;
            }

            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int start = outVertices.Count;
                outVertices.Add(p0); outVertices.Add(p1); outVertices.Add(p2); outVertices.Add(p3);
                outTriangles.Add(start); outTriangles.Add(start + 1); outTriangles.Add(start + 2);
                outTriangles.Add(start); outTriangles.Add(start + 2); outTriangles.Add(start + 3);
            }

            Mesh cavity = new Mesh { name = "PATIENT_Cavidade" };
            cavity.SetVertices(outVertices);
            cavity.SetTriangles(outTriangles, 0);
            cavity.RecalculateNormals();
            cavity.RecalculateBounds();

            Debug.Log($"[Tórax] cavidade: {outVertices.Count} vértices, " +
                      $"{outTriangles.Count / 3} triângulos ({walls} arestas de borda na parede), " +
                      $"fundo a {CavityDepth * 100f:F1}cm da pele");
            return cavity;
        }

        private static Mesh LoadSourceMesh()
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(SkinGlb);
            if (asset == null)
            {
                Debug.LogError("[Tórax] modelo de pele ausente: " + SkinGlb);
                return null;
            }

            MeshFilter filter = asset.GetComponentInChildren<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                Debug.LogError("[Tórax] o modelo de pele não tem malha.");
                return null;
            }

            return filter.sharedMesh;
        }

        private static void Write(Mesh mesh, string path)
        {
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return;
            }

            // Overwrite in place so anything already referencing the asset keeps its link.
            existing.Clear();
            existing.vertices = mesh.vertices;
            existing.normals = mesh.normals;
            existing.uv = mesh.uv;
            existing.triangles = mesh.triangles;
            existing.RecalculateBounds();
            EditorUtility.SetDirty(existing);
        }
    }
}
