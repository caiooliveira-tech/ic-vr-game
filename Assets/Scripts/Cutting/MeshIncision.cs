using System.Collections.Generic;
using UnityEngine;

namespace VRSurgery.Cutting
{
    /// <summary>
    /// A real incision: the mesh's topology is changed, not decorated.
    ///
    /// The project's existing wound was cosmetic — WoundRenderer drew geometry along the blade's
    /// path while the skin mesh underneath stayed whole, so nothing was ever actually severed.
    /// This splits the surface: triangles crossing the cut are subdivided, the vertices sitting on
    /// the cut line are duplicated into a left and a right copy, each side's triangles are
    /// reassigned to its own copy, and the two lips are then free to move apart. Once that is
    /// done the sheet genuinely has two edges where it had one, which is what lets it open.
    ///
    /// Works on a surface patch in its own local space, cutting along a polyline on the XZ plane.
    /// That is the shape of every cut this project needs — skin, pericardium, a vessel wall — and
    /// it keeps the arithmetic in two dimensions, which is what makes it affordable on a Quest.
    /// </summary>
    public static partial class MeshIncision
    {
        /// <summary>How far apart the two lips are pulled, per unit of cut depth, in metres.</summary>
        public const float DefaultRetraction = 0.004f;

        private struct Edge
        {
            public int A, B;
            public Edge(int a, int b) { A = Mathf.Min(a, b); B = Mathf.Max(a, b); }
            public override int GetHashCode() => A * 73856093 ^ B * 19349663;
            public override bool Equals(object o) => o is Edge e && e.A == A && e.B == B;
        }

        /// <summary>
        /// Cuts <paramref name="mesh"/> along the segment from <paramref name="from"/> to
        /// <paramref name="to"/>, both in the mesh's local XZ plane.
        ///
        /// <paramref name="depth01"/> scales how far the lips retract, so a shallow pass parts the
        /// surface barely at all and a deep one opens it properly.
        /// </summary>
        public static bool Cut(Mesh mesh, Vector2 from, Vector2 to, float depth01,
            float retraction = DefaultRetraction)
        {
            if (mesh == null || (to - from).sqrMagnitude < 1e-8f)
            {
                return false;
            }

            List<Vector3> vertices = new List<Vector3>();
            mesh.GetVertices(vertices);
            int[] triangles = mesh.triangles;

            Vector2 direction = (to - from).normalized;
            // Left of the cut, on the surface plane. Which side a vertex falls on is the sign of
            // its offset along this.
            Vector2 across = new Vector2(-direction.y, direction.x);

            float SideOf(Vector3 v)
            {
                Vector2 flat = new Vector2(v.x, v.z) - from;
                // Only the span between the two ends is cut; beyond it the sheet stays joined,
                // which is what makes this an incision rather than a slice through the whole patch.
                float along = Vector2.Dot(flat, direction);
                if (along < 0f || along > (to - from).magnitude)
                {
                    return 0f;
                }

                return Vector2.Dot(flat, across);
            }

            // ---- 1. split every edge that straddles the cut -------------------------------
            Dictionary<Edge, int> splitPoints = new Dictionary<Edge, int>();
            List<int> newTriangles = new List<int>(triangles.Length * 2);

            for (int t = 0; t < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                float s0 = SideOf(vertices[i0]), s1 = SideOf(vertices[i1]), s2 = SideOf(vertices[i2]);

                bool crosses = (s0 > 0f && (s1 < 0f || s2 < 0f)) || (s0 < 0f && (s1 > 0f || s2 > 0f))
                            || (s1 > 0f && s2 < 0f) || (s1 < 0f && s2 > 0f);

                if (!crosses)
                {
                    newTriangles.Add(i0); newTriangles.Add(i1); newTriangles.Add(i2);
                    continue;
                }

                // Split on the line itself, not at the centroid. Subdividing at the centroid left
                // the seam following whatever the original tessellation happened to be, so the
                // incision came out as sawteeth instead of a line. Cutting each crossing edge at
                // its exact intersection puts every new vertex on the cut, and the two lips then
                // part along a straight edge.
                SplitTriangle(vertices, newTriangles, splitPoints, SideOf, i0, i1, i2);
            }

            // ---- 2. duplicate the vertices on the cut, one copy per side ------------------
            Dictionary<int, int> rightCopy = new Dictionary<int, int>();
            int[] finalTriangles = new int[newTriangles.Count];

            for (int t = 0; t < newTriangles.Count; t += 3)
            {
                int a = newTriangles[t], b = newTriangles[t + 1], c = newTriangles[t + 2];

                // A triangle belongs to whichever side its centre falls on.
                Vector3 centre = (vertices[a] + vertices[b] + vertices[c]) / 3f;
                bool onRight = SideOf(centre) < 0f;

                finalTriangles[t] = onRight ? Duplicate(a) : a;
                finalTriangles[t + 1] = onRight ? Duplicate(b) : b;
                finalTriangles[t + 2] = onRight ? Duplicate(c) : c;
            }

            int Duplicate(int index)
            {
                if (rightCopy.TryGetValue(index, out int copy))
                {
                    return copy;
                }

                copy = vertices.Count;
                vertices.Add(vertices[index]);
                rightCopy[index] = copy;
                return copy;
            }

            if (rightCopy.Count == 0)
            {
                // The segment missed the sheet entirely; leave the mesh untouched rather than
                // rewriting it into an identical copy.
                return false;
            }

            // ---- 2b. record the wound's rim, before anything moves ------------------------
            // Only the vertices sitting ON the cut are the rim. rightCopy holds a duplicate for
            // every vertex touched by a right-hand triangle, which is half the sheet; stitching
            // walls between all of those pairs shreds the patch into spikes. And this has to be
            // captured now, because parting the lips below moves these vertices off the line and
            // there is no way to recognise them afterwards.
            List<(float along, int left, int right)> rim = new List<(float, int, int)>();
            float cutLength = (to - from).magnitude;

            foreach (KeyValuePair<int, int> pair in rightCopy)
            {
                Vector3 v = vertices[pair.Key];
                Vector2 flat = new Vector2(v.x, v.z) - from;
                float alongCut = Vector2.Dot(flat, direction);

                if (alongCut < 0f || alongCut > cutLength)
                {
                    continue;
                }

                if (Mathf.Abs(Vector2.Dot(flat, across)) > 1e-5f)
                {
                    continue;
                }

                rim.Add((alongCut, pair.Key, pair.Value));
            }

            rim.Sort((a, b) => a.along.CompareTo(b.along));

            // ---- 3. part the lips --------------------------------------------------------
            float open = retraction * Mathf.Clamp01(depth01);
            Vector3 acrossLocal = new Vector3(across.x, 0f, across.y);

            // Membership as a set, not Dictionary.ContainsValue: that is a linear scan, and
            // calling it once per vertex made parting the lips quadratic in the patch size.
            HashSet<int> rightCopies = new HashSet<int>(rightCopy.Values);

            for (int i = 0; i < vertices.Count; i++)
            {
                float side = SideOf(vertices[i]);
                if (Mathf.Abs(side) > open * 6f)
                {
                    // Far from the cut the sheet is undisturbed, so the opening tapers out
                    // instead of shearing the whole patch sideways.
                    continue;
                }

                float falloff = 1f - Mathf.Clamp01(Mathf.Abs(side) / Mathf.Max(1e-5f, open * 6f));
                float sign = rightCopies.Contains(i) ? -1f : (side >= 0f ? 1f : -1f);

                vertices[i] += acrossLocal * (sign * open * falloff)
                             - Vector3.up * (open * 0.5f * falloff);
            }

            // ---- 4. close the wound's sides ----------------------------------------------
            List<int> withWalls = new List<int>(finalTriangles);
            BuildCavity(vertices, withWalls, rim, open);

            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(withWalls, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return true;
        }

        /// <summary>
        /// Builds the inside of the wound.
        ///
        /// Parting the lips alone leaves a hole straight through the sheet — the surface reads as
        /// torn paper rather than cut tissue, because you can see the background through it. Skin
        /// has thickness, and what makes an incision look like one is its cross section: two walls
        /// running down from the lips to a floor.
        ///
        /// The floor sits on the cut's original line, below the surface, so the section is a V
        /// that deepens with the cut instead of a flat-bottomed slot.
        /// </summary>
        private static void BuildCavity(List<Vector3> vertices, List<int> triangles,
            List<(float along, int left, int right)> rim, float open)
        {
            if (open <= 0f || rim.Count < 2)
            {
                return;
            }

            // One floor vertex per rim pair, midway between the lips and below them.
            int[] floor = new int[rim.Count];
            for (int i = 0; i < rim.Count; i++)
            {
                Vector3 mid = (vertices[rim[i].left] + vertices[rim[i].right]) * 0.5f;

                // Tapered at the ends: a wound is deepest where the blade was buried and runs out
                // to nothing at its extremities, which is also what stops the walls from ending
                // in a vertical cliff.
                float t = rim.Count > 1 ? i / (float)(rim.Count - 1) : 0.5f;
                float taper = Mathf.Sin(t * Mathf.PI);

                floor[i] = vertices.Count;
                vertices.Add(mid - Vector3.up * (open * 1.6f * taper));
            }

            for (int i = 0; i < rim.Count - 1; i++)
            {
                int l0 = rim[i].left, l1 = rim[i + 1].left;
                int r0 = rim[i].right, r1 = rim[i + 1].right;
                int f0 = floor[i], f1 = floor[i + 1];

                // Both walls are emitted with both windings. A cut surface is a thin sheet seen
                // from whichever side the player's head happens to be on, and single-sided walls
                // are invisible from half the angles around the table — which is exactly how this
                // first read as an empty black slot instead of a wound with depth.
                AddQuad(triangles, l0, f0, f1, l1);
                AddQuad(triangles, r0, r1, f1, f0);
            }
        }

        /// <summary>Quad a-b-c-d as two triangles, facing both ways.</summary>
        private static void AddQuad(List<int> triangles, int a, int b, int c, int d)
        {
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
            triangles.Add(a); triangles.Add(c); triangles.Add(d);

            triangles.Add(a); triangles.Add(c); triangles.Add(b);
            triangles.Add(a); triangles.Add(d); triangles.Add(c);
        }

        /// <summary>
        /// Clips one triangle against the cut line, emitting sub-triangles that all lie cleanly on
        /// one side or the other.
        ///
        /// Exactly one vertex sits alone on its side of a crossed triangle, so the shape always
        /// divides the same way: a corner triangle containing that lone vertex, and a quad on the
        /// far side split into two. Intersection points are cached per edge so the triangles
        /// sharing that edge reuse the same vertex and the surface stays welded.
        /// </summary>
        private static void SplitTriangle(List<Vector3> vertices, List<int> output,
            Dictionary<Edge, int> cache, System.Func<Vector3, float> sideOf, int i0, int i1, int i2)
        {
            float s0 = sideOf(vertices[i0]), s1 = sideOf(vertices[i1]), s2 = sideOf(vertices[i2]);

            // Rotate so that 'a' is the vertex alone on its side.
            int a, b, c;
            if (s0 * s1 >= 0f) { a = i2; b = i0; c = i1; }
            else if (s1 * s2 >= 0f) { a = i0; b = i1; c = i2; }
            else { a = i1; b = i2; c = i0; }

            int ab = IntersectionOn(vertices, cache, sideOf, a, b);
            int ac = IntersectionOn(vertices, cache, sideOf, a, c);

            if (ab < 0 || ac < 0)
            {
                // The cut grazed a vertex rather than crossing an edge; leaving the triangle whole
                // is correct and avoids emitting a zero-area sliver.
                output.Add(i0); output.Add(i1); output.Add(i2);
                return;
            }

            output.Add(a); output.Add(ab); output.Add(ac);
            output.Add(ab); output.Add(b); output.Add(c);
            output.Add(ab); output.Add(c); output.Add(ac);
        }

        /// <summary>
        /// Vertex where the cut crosses edge (<paramref name="a"/>, <paramref name="b"/>), created
        /// once and shared. Returns -1 when the edge does not actually cross.
        /// </summary>
        private static int IntersectionOn(List<Vector3> vertices, Dictionary<Edge, int> cache,
            System.Func<Vector3, float> sideOf, int a, int b)
        {
            Edge edge = new Edge(a, b);
            if (cache.TryGetValue(edge, out int existing))
            {
                return existing;
            }

            float sa = sideOf(vertices[a]);
            float sb = sideOf(vertices[b]);

            float denominator = sa - sb;
            if (Mathf.Abs(denominator) < 1e-9f)
            {
                return -1;
            }

            float t = sa / denominator;
            if (t <= 0f || t >= 1f)
            {
                return -1;
            }

            int index = vertices.Count;
            vertices.Add(Vector3.Lerp(vertices[a], vertices[b], t));
            cache[edge] = index;
            return index;
        }
    }
}
