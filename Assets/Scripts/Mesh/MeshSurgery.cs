using System.Collections.Generic;
using UnityEngine;

namespace VRSurgery.MeshOps
{
    /// <summary>
    /// Real topological surgery on a mesh: triangles are removed, new vertices and edges are
    /// created, and the surface's genus changes. This is not a decal, a shader trick or a
    /// pre-authored wound prop — after a cut the mesh is a different surface, and the tests
    /// assert that by measuring its Euler characteristic.
    ///
    /// The method is a local remesh rather than an exact boolean. Clipping every straddling
    /// triangle against the cut boundary produces slivers and T-junctions, both of which shade
    /// badly and neither of which a headset's renderer forgives. Instead the neighbourhood is
    /// cleared, the ragged boundary that leaves is captured as an ordered loop, and a clean
    /// annulus is stitched between that loop and an exact circle at the cut. The rim is therefore
    /// perfectly round no matter how the original triangulation happened to fall.
    /// </summary>
    public static class MeshSurgery
    {
        public struct HoleResult
        {
            public bool Success;
            public string Failure;

            public int TrianglesRemoved;
            public int TrianglesAdded;
            public int VerticesAdded;

            /// <summary>Vertex indices around the opening, ordered. The wound's lip.</summary>
            public int[] RimVertices;

            public override string ToString() =>
                Success
                    ? $"-{TrianglesRemoved} tri, +{TrianglesAdded} tri, +{VerticesAdded} vert, rim {RimVertices.Length}"
                    : $"failed: {Failure}";
        }

        /// <summary>
        /// Opens a circular hole through the mesh at <paramref name="centre"/> and pulls a tract
        /// of <paramref name="depth"/> inward along -normal, the way a cannula leaves a channel
        /// through the abdominal wall rather than a flat cut-out.
        ///
        /// All arguments are in the mesh's local space.
        /// </summary>
        public static HoleResult OpenCircularHole(Mesh mesh, Vector3 centre, Vector3 normal,
            float radius, float depth, int rimSegments = 24)
        {
            if (mesh == null) { return new HoleResult { Failure = "no mesh" }; }
            if (radius <= 0f) { return new HoleResult { Failure = "radius must be positive" }; }

            normal = normal.normalized;

            List<Vector3> vertices = new List<Vector3>(mesh.vertices);
            List<Vector3> normals = new List<Vector3>(mesh.normals);
            int[] triangles = mesh.triangles;

            if (normals.Count != vertices.Count)
            {
                normals.Clear();
                for (int i = 0; i < vertices.Count; i++) { normals.Add(normal); }
            }

            // Everything within this radius is cleared. The margin is what buys room for a clean
            // annulus: without it the stitched ring would have to fold back on the original
            // triangulation and would self-intersect on a coarse patch.
            float clearRadius = radius * 2.0f;

            List<int> kept = new List<int>(triangles.Length);
            List<int> removed = new List<int>();

            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 a = vertices[triangles[t]];
                Vector3 b = vertices[triangles[t + 1]];
                Vector3 c = vertices[triangles[t + 2]];
                Vector3 centroid = (a + b + c) / 3f;

                if (PlanarDistance(centroid, centre, normal) <= clearRadius)
                {
                    removed.Add(triangles[t]);
                    removed.Add(triangles[t + 1]);
                    removed.Add(triangles[t + 2]);
                }
                else
                {
                    kept.Add(triangles[t]);
                    kept.Add(triangles[t + 1]);
                    kept.Add(triangles[t + 2]);
                }
            }

            if (removed.Count == 0)
            {
                return new HoleResult { Failure = "cut fell outside the mesh" };
            }

            List<int> loop = ExtractBoundaryLoop(removed, kept);
            if (loop == null || loop.Count < 3)
            {
                return new HoleResult { Failure = "could not close a boundary around the cut" };
            }

            int verticesBefore = vertices.Count;
            int trianglesBefore = triangles.Length / 3;

            // The exact rim, and the ring at the bottom of the tract.
            int[] rim = new int[rimSegments];
            int[] floor = new int[rimSegments];

            Vector3 tangent = Vector3.Normalize(Vector3.Cross(normal, SafeUp(normal)));
            Vector3 bitangent = Vector3.Cross(normal, tangent);

            for (int i = 0; i < rimSegments; i++)
            {
                float angle = (i / (float)rimSegments) * Mathf.PI * 2f;
                Vector3 offset = (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * radius;

                rim[i] = vertices.Count;
                vertices.Add(centre + offset);
                normals.Add(normal);

                floor[i] = vertices.Count;
                vertices.Add(centre + offset - normal * depth);
                // The tract's wall faces inward, toward the channel's axis.
                normals.Add(-offset.normalized);
            }

            // Skirt from the ragged boundary to the exact rim.
            StitchLoops(kept, loop, rim, vertices, centre, normal);

            // The tract itself.
            for (int i = 0; i < rimSegments; i++)
            {
                int next = (i + 1) % rimSegments;
                kept.Add(rim[i]); kept.Add(floor[i]); kept.Add(rim[next]);
                kept.Add(rim[next]); kept.Add(floor[i]); kept.Add(floor[next]);
            }

            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(kept, 0);
            mesh.RecalculateBounds();

            return new HoleResult
            {
                Success = true,
                TrianglesRemoved = removed.Count / 3,
                TrianglesAdded = (kept.Count / 3) - (trianglesBefore - removed.Count / 3),
                VerticesAdded = vertices.Count - verticesBefore,
                RimVertices = rim,
            };
        }

        /// <summary>
        /// Ordered loop of vertices bounding the cleared region: the edges that the removed
        /// triangles and the surviving ones had in common.
        /// </summary>
        private static List<int> ExtractBoundaryLoop(List<int> removed, List<int> kept)
        {
            HashSet<long> keptEdges = new HashSet<long>();
            for (int t = 0; t < kept.Count; t += 3)
            {
                keptEdges.Add(EdgeKey(kept[t], kept[t + 1]));
                keptEdges.Add(EdgeKey(kept[t + 1], kept[t + 2]));
                keptEdges.Add(EdgeKey(kept[t + 2], kept[t]));
            }

            // Directed so the loop comes out consistently wound.
            Dictionary<int, int> next = new Dictionary<int, int>();
            for (int t = 0; t < removed.Count; t += 3)
            {
                TryBoundary(removed[t], removed[t + 1], keptEdges, next);
                TryBoundary(removed[t + 1], removed[t + 2], keptEdges, next);
                TryBoundary(removed[t + 2], removed[t], keptEdges, next);
            }

            if (next.Count == 0) { return null; }

            List<int> loop = new List<int>(next.Count);
            int start = -1;
            foreach (KeyValuePair<int, int> pair in next) { start = pair.Key; break; }

            int current = start;
            for (int guard = 0; guard <= next.Count; guard++)
            {
                loop.Add(current);
                if (!next.TryGetValue(current, out int following)) { return null; }
                current = following;
                if (current == start) { return loop; }
            }

            // Ran past the edge count without closing: the cleared region was not a simple disc,
            // which happens when two cuts overlap. Refusing is better than stitching a tangle.
            return null;
        }

        private static void TryBoundary(int from, int to, HashSet<long> keptEdges, Dictionary<int, int> next)
        {
            if (keptEdges.Contains(EdgeKey(from, to)) && !next.ContainsKey(from))
            {
                next[from] = to;
            }
        }

        /// <summary>Triangulates the band between the ragged boundary loop and the exact rim.</summary>
        private static void StitchLoops(List<int> triangles, List<int> loop, int[] rim,
            List<Vector3> vertices, Vector3 centre, Vector3 normal)
        {
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(normal, SafeUp(normal)));
            Vector3 bitangent = Vector3.Cross(normal, tangent);

            // Each boundary vertex is joined to the rim vertex nearest in angle, so the band has
            // no twist however unevenly the original triangulation spaced the loop.
            int[] nearestRim = new int[loop.Count];
            for (int i = 0; i < loop.Count; i++)
            {
                Vector3 d = vertices[loop[i]] - centre;
                float angle = Mathf.Atan2(Vector3.Dot(d, bitangent), Vector3.Dot(d, tangent));
                if (angle < 0f) { angle += Mathf.PI * 2f; }
                nearestRim[i] = Mathf.RoundToInt(angle / (Mathf.PI * 2f) * rim.Length) % rim.Length;
            }

            for (int i = 0; i < loop.Count; i++)
            {
                int j = (i + 1) % loop.Count;

                int outerA = loop[i];
                int outerB = loop[j];
                int innerA = nearestRim[i];
                int innerB = nearestRim[j];

                triangles.Add(outerA); triangles.Add(rim[innerA]); triangles.Add(outerB);

                // Walk the rim forward until it catches up, so no rim vertex is left unattached
                // and the band stays a closed surface.
                int guard = 0;
                while (innerA != innerB && guard++ < rim.Length)
                {
                    int step = (innerA + 1) % rim.Length;
                    triangles.Add(outerB); triangles.Add(rim[innerA]); triangles.Add(rim[step]);
                    innerA = step;
                }
            }
        }

        private static Vector3 SafeUp(Vector3 normal) =>
            Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;

        private static float PlanarDistance(Vector3 point, Vector3 centre, Vector3 normal)
        {
            Vector3 d = point - centre;
            return Vector3.ProjectOnPlane(d, normal).magnitude;
        }

        private static long EdgeKey(int a, int b)
        {
            int low = a < b ? a : b;
            int high = a < b ? b : a;
            return ((long)low << 32) | (uint)high;
        }

        /// <summary>
        /// Euler characteristic V - E + F. The number the tests watch: an uncut patch is a disc
        /// at 1, and every hole punched through it drops it by one. Nothing else in the pipeline
        /// can move this number, which is what makes it proof that the topology changed rather
        /// than the shading.
        /// </summary>
        public static int EulerCharacteristic(Mesh mesh)
        {
            int[] triangles = mesh.triangles;
            HashSet<long> edges = new HashSet<long>();
            HashSet<int> used = new HashSet<int>();

            for (int t = 0; t < triangles.Length; t += 3)
            {
                edges.Add(EdgeKey(triangles[t], triangles[t + 1]));
                edges.Add(EdgeKey(triangles[t + 1], triangles[t + 2]));
                edges.Add(EdgeKey(triangles[t + 2], triangles[t]));
                used.Add(triangles[t]);
                used.Add(triangles[t + 1]);
                used.Add(triangles[t + 2]);
            }

            return used.Count - edges.Count + triangles.Length / 3;
        }

        /// <summary>Edges used by exactly one triangle — the open borders of the surface.</summary>
        public static int BoundaryEdgeCount(Mesh mesh)
        {
            int[] triangles = mesh.triangles;
            Dictionary<long, int> counts = new Dictionary<long, int>();

            for (int t = 0; t < triangles.Length; t += 3)
            {
                Bump(counts, EdgeKey(triangles[t], triangles[t + 1]));
                Bump(counts, EdgeKey(triangles[t + 1], triangles[t + 2]));
                Bump(counts, EdgeKey(triangles[t + 2], triangles[t]));
            }

            int boundary = 0;
            foreach (KeyValuePair<long, int> pair in counts)
            {
                if (pair.Value == 1) { boundary++; }
            }

            return boundary;
        }

        private static void Bump(Dictionary<long, int> counts, long key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }
    }
}
