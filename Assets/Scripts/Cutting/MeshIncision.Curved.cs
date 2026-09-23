using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRSurgery.Cutting
{
    public static partial class MeshIncision
    {
        // The authored mesh uses Unity metres. One micrometre is enough to collapse numerical
        // duplicates without welding distinct points of the incision. The area value is twice
        // the triangle area (the magnitude of the cross product), in square metres.
        private const float CutSideEpsilon3D = 1e-6f;
        private const float PositionMergeEpsilon3D = 1e-6f;
        private const float PositionMergeEpsilonSquared3D =
            PositionMergeEpsilon3D * PositionMergeEpsilon3D;
        private const float MinimumTriangleDoubleArea3D = 1e-10f;

        public readonly struct PathPoint
        {
            public readonly Vector3 Position;
            public readonly Vector3 Normal;
            public readonly float Depth01;

            public PathPoint(Vector3 position, Vector3 normal, float depth01)
            {
                Position = position;
                Normal = normal.sqrMagnitude > 1e-8f ? normal.normalized : Vector3.up;
                Depth01 = Mathf.Clamp01(depth01);
            }
        }

        public struct Options
        {
            public float OpeningWidth;
            public float MaximumDepth;
            public float InfluenceRadius;
            public int CuttableSubmeshCount;

            public static Options Default => new Options
            {
                OpeningWidth = 0.0045f,
                MaximumDepth = 0.006f,
                InfluenceRadius = 0.010f,
                CuttableSubmeshCount = 0,
            };
        }

        // Correspondence is captured during clipping, never recovered by proximity/Arc sorting.
        // Indices remain valid in this append-only mesh until ResetTissue replaces it.
        public readonly struct LipPair
        {
            public readonly int Positive, Negative, Chain, Order;
            public readonly Vector3 Across;
            public LipPair(int positive, int negative, int chain, int order, Vector3 across)
            { Positive = positive; Negative = negative; Chain = chain; Order = order; Across = across; }
        }

        public readonly struct InteriorBinding
        {
            public readonly int Upper, Opposite, Row;
            public InteriorBinding(int upper, int opposite, int row)
            { Upper = upper; Opposite = opposite; Row = row; }
        }

        public readonly struct Result
        {
            public readonly bool Success;
            public readonly string Failure;
            public readonly int PathPoints;
            public readonly int AffectedTriangles;
            public readonly int VerticesBefore;
            public readonly int VerticesAfter;
            public readonly double RebuildMilliseconds;
            public readonly int InteriorSubmesh;
            public readonly LipPair[] LipPairs;
            public readonly InteriorBinding[] InteriorBindings;

            public Result(bool success, string failure, int points, int affected, int before,
                int after, double milliseconds, int interiorSubmesh,
                LipPair[] lipPairs = null, InteriorBinding[] interiorBindings = null)
            {
                Success = success;
                Failure = failure;
                PathPoints = points;
                AffectedTriangles = affected;
                VerticesBefore = before;
                VerticesAfter = after;
                RebuildMilliseconds = milliseconds;
                InteriorSubmesh = interiorSubmesh;
                LipPairs = lipPairs;
                InteriorBindings = interiorBindings;
            }
        }

        private readonly struct CutEdge : IEquatable<CutEdge>
        {
            public readonly int A;
            public readonly int B;

            public CutEdge(int a, int b)
            {
                A = Mathf.Min(a, b);
                B = Mathf.Max(a, b);
            }

            public bool Equals(CutEdge other) => A == other.A && B == other.B;
            public override bool Equals(object obj) => obj is CutEdge other && Equals(other);
            public override int GetHashCode() => A * 73856093 ^ B * 19349663;
        }

        private readonly struct PathSegment3D
        {
            public readonly PathPoint A;
            public readonly PathPoint B;
            public readonly float Start;
            public readonly float Length;

            public PathSegment3D(PathPoint a, PathPoint b, float start)
            {
                A = a;
                B = b;
                Start = start;
                Length = Vector3.Distance(a.Position, b.Position);
            }
        }

        private readonly struct PathMetric3D
        {
            public readonly float Side;
            public readonly float Distance;
            public readonly float Arc;
            public readonly float Depth01;
            public readonly Vector3 Across;
            public readonly Vector3 Normal;

            public PathMetric3D(float side, float distance, float arc, float depth01,
                Vector3 across, Vector3 normal)
            {
                Side = side;
                Distance = distance;
                Arc = arc;
                Depth01 = depth01;
                Across = across;
                Normal = normal;
            }
        }

        private struct WorkTriangle3D
        {
            public int A;
            public int B;
            public int C;
            public int Side;

            public WorkTriangle3D(int a, int b, int c, int side)
            {
                A = a;
                B = b;
                C = c;
                Side = side;
            }
        }

        private sealed class VertexData3D
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector4> Tangents = new List<Vector4>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            public readonly List<KeyValuePair<int, int>> InteriorRows = new List<KeyValuePair<int, int>>();
            public readonly bool HasNormals;
            public readonly bool HasTangents;
            public readonly bool HasUv;

            public VertexData3D(Mesh mesh)
            {
                mesh.GetVertices(Positions);
                mesh.GetNormals(Normals);
                mesh.GetTangents(Tangents);
                mesh.GetUVs(0, Uv);
                HasNormals = Normals.Count == Positions.Count;
                HasTangents = Tangents.Count == Positions.Count;
                HasUv = Uv.Count == Positions.Count;
                if (!HasNormals) { Normals.Clear(); }
                if (!HasTangents) { Tangents.Clear(); }
                if (!HasUv) { Uv.Clear(); }
            }

            public int Interpolate(int a, int b, float t)
            {
                int index = Positions.Count;
                Positions.Add(Vector3.LerpUnclamped(Positions[a], Positions[b], t));
                if (HasNormals) { Normals.Add(Vector3.LerpUnclamped(Normals[a], Normals[b], t).normalized); }
                if (HasTangents)
                {
                    Vector4 value = Vector4.LerpUnclamped(Tangents[a], Tangents[b], t);
                    Vector3 direction = new Vector3(value.x, value.y, value.z).normalized;
                    Tangents.Add(new Vector4(direction.x, direction.y, direction.z,
                        Mathf.Abs(value.w) < 0.5f ? 1f : Mathf.Sign(value.w)));
                }
                if (HasUv) { Uv.Add(Vector2.LerpUnclamped(Uv[a], Uv[b], t)); }
                return index;
            }

            public int Duplicate(int source)
            {
                int index = Positions.Count;
                Positions.Add(Positions[source]);
                if (HasNormals) { Normals.Add(Normals[source]); }
                if (HasTangents) { Tangents.Add(Tangents[source]); }
                if (HasUv) { Uv.Add(Uv[source]); }
                return index;
            }

            public int AddInterior(Vector3 position, Vector3 normal, Vector3 tangent, Vector2 uv)
            {
                int index = Positions.Count;
                Positions.Add(position);
                if (HasNormals) { Normals.Add(normal.normalized); }
                if (HasTangents)
                {
                    tangent = tangent.sqrMagnitude > 1e-8f ? tangent.normalized : Vector3.right;
                    Tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, 1f));
                }
                if (HasUv) { Uv.Add(uv); }
                return index;
            }
        }

        /// <summary>
        /// Splits only triangles crossed by a finite 3D polyline, duplicates its two lips and
        /// writes new inner faces to a dedicated submesh. Existing vertex attributes are retained.
        /// </summary>
        public static Result Cut(Mesh mesh, IReadOnlyList<PathPoint> path, Options options)
        {
            if (mesh == null) { return Failed3D("Mesh ausente.", path != null ? path.Count : 0); }
            if (path == null || path.Count < 2)
            {
                return Failed3D("A incisão precisa de pelo menos dois pontos.", path != null ? path.Count : 0);
            }

            List<PathSegment3D> segments = BuildSegments3D(path, out float totalLength);
            if (segments.Count == 0 || totalLength < 0.001f)
            {
                return Failed3D("Trajetória curta demais.", path.Count);
            }

            options.OpeningWidth = Mathf.Max(0.0001f, options.OpeningWidth);
            options.MaximumDepth = Mathf.Max(0.0001f, options.MaximumDepth);
            options.InfluenceRadius = Mathf.Max(options.OpeningWidth * 1.5f, options.InfluenceRadius);

            Stopwatch timer = Stopwatch.StartNew();
            VertexData3D data = new VertexData3D(mesh);
            int verticesBefore = data.Positions.Count;
            int sourceSubmeshes = Mathf.Max(1, mesh.subMeshCount);
            int cuttableSubmeshes = options.CuttableSubmeshCount <= 0
                ? sourceSubmeshes
                : Mathf.Clamp(options.CuttableSubmeshCount, 1, sourceSubmeshes);
            int interiorSubmesh = cuttableSubmeshes;
            int outputSubmeshes = Mathf.Max(sourceSubmeshes, interiorSubmesh + 1);

            Bounds pathBounds = new Bounds(path[0].Position, Vector3.zero);
            for (int i = 1; i < path.Count; i++) { pathBounds.Encapsulate(path[i].Position); }
            pathBounds.Expand(Mathf.Max(options.InfluenceRadius, options.OpeningWidth * 2f) * 2f);
            bool[] evaluated = new bool[verticesBefore];
            List<PathMetric3D> metrics = new List<PathMetric3D>(verticesBefore * 2);
            for (int i = 0; i < verticesBefore; i++)
            {
                metrics.Add(new PathMetric3D(0f, float.PositiveInfinity, 0f, 0f, Vector3.zero, Vector3.up));
            }

            List<WorkTriangle3D>[] work = new List<WorkTriangle3D>[outputSubmeshes];
            for (int i = 0; i < work.Length; i++) { work[i] = new List<WorkTriangle3D>(); }

            Dictionary<CutEdge, int> intersections = new Dictionary<CutEdge, int>();
            HashSet<int> seam = new HashSet<int>();
            Dictionary<int, HashSet<int>> seamAdjacency = new Dictionary<int, HashSet<int>>();
            HashSet<int> protectedVertices = new HashSet<int>();
            int affectedTriangles = 0;

            for (int submesh = 0; submesh < sourceSubmeshes; submesh++)
            {
                int[] triangles = mesh.GetTriangles(submesh);
                bool canCut = submesh < cuttableSubmeshes;
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    int a = triangles[t];
                    int b = triangles[t + 1];
                    int c = triangles[t + 2];
                    if (!canCut)
                    {
                        work[submesh].Add(new WorkTriangle3D(a, b, c, 0));
                        protectedVertices.Add(a); protectedVertices.Add(b); protectedVertices.Add(c);
                        continue;
                    }

                    Vector3 minimum = Vector3.Min(data.Positions[a], Vector3.Min(data.Positions[b], data.Positions[c]));
                    Vector3 maximum = Vector3.Max(data.Positions[a], Vector3.Max(data.Positions[b], data.Positions[c]));
                    if (maximum.x < pathBounds.min.x || minimum.x > pathBounds.max.x
                        || maximum.y < pathBounds.min.y || minimum.y > pathBounds.max.y
                        || maximum.z < pathBounds.min.z || minimum.z > pathBounds.max.z)
                    {
                        work[submesh].Add(new WorkTriangle3D(a, b, c, 0));
                        continue;
                    }
                    if (!evaluated[a]) { metrics[a] = Evaluate3D(data.Positions[a], segments); evaluated[a] = true; }
                    if (!evaluated[b]) { metrics[b] = Evaluate3D(data.Positions[b], segments); evaluated[b] = true; }
                    if (!evaluated[c]) { metrics[c] = Evaluate3D(data.Positions[c], segments); evaluated[c] = true; }

                    float sa = metrics[a].Side;
                    float sb = metrics[b].Side;
                    float sc = metrics[c].Side;
                    bool positive = sa > CutSideEpsilon3D || sb > CutSideEpsilon3D
                        || sc > CutSideEpsilon3D;
                    bool negative = sa < -CutSideEpsilon3D || sb < -CutSideEpsilon3D
                        || sc < -CutSideEpsilon3D;
                    PathMetric3D centre = Evaluate3D(
                        (data.Positions[a] + data.Positions[b] + data.Positions[c]) / 3f, segments);
                    float nearest = Mathf.Min(centre.Distance,
                        Mathf.Min(metrics[a].Distance, Mathf.Min(metrics[b].Distance, metrics[c].Distance)));

                    if (positive && negative && nearest <= options.InfluenceRadius
                        && TryGetTriangleCutSegment3D(a, b, c, data, metrics, segments,
                            intersections, out int cutA, out int cutB))
                    {
                        seam.Add(cutA);
                        seam.Add(cutB);
                        AddSeamConnection3D(cutA, cutB, seamAdjacency);
                        EmitClipped3D(a, b, c, true, work[submesh], data, metrics, segments,
                            intersections);
                        EmitClipped3D(a, b, c, false, work[submesh], data, metrics, segments,
                            intersections);
                        affectedTriangles++;
                    }
                    else
                    {
                        work[submesh].Add(new WorkTriangle3D(a, b, c, centre.Side < 0f ? -1 : 1));
                    }
                }
            }

            if (affectedTriangles == 0 || seam.Count < 2)
            {
                timer.Stop();
                return new Result(false, "A trajetória não interceptou triângulos suficientes.",
                    path.Count, 0, verticesBefore, verticesBefore, timer.Elapsed.TotalMilliseconds, -1);
            }

            Dictionary<int, int> negativeCopy = new Dictionary<int, int>(seam.Count);
            foreach (int index in seam)
            {
                int copy = data.Duplicate(index);
                negativeCopy[index] = copy;
                metrics.Add(metrics[index]);
            }

            for (int s = 0; s < cuttableSubmeshes; s++)
            {
                for (int i = 0; i < work[s].Count; i++)
                {
                    WorkTriangle3D triangle = work[s][i];
                    if (triangle.Side < 0)
                    {
                        if (negativeCopy.TryGetValue(triangle.A, out int a)) { triangle.A = a; }
                        if (negativeCopy.TryGetValue(triangle.B, out int b)) { triangle.B = b; }
                        if (negativeCopy.TryGetValue(triangle.C, out int c)) { triangle.C = c; }
                        work[s][i] = triangle;
                    }
                }
            }

            HashSet<int> negativeIndices = new HashSet<int>(negativeCopy.Values);
            int outerVertexCount = data.Positions.Count;
            float halfOpening = options.OpeningWidth * 0.5f;
            float falloffRadius = Mathf.Max(options.InfluenceRadius, options.OpeningWidth * 2f);
            Dictionary<int, Vector3> openingDirections = BuildOpeningDirections3D(data,
                seamAdjacency, negativeCopy, metrics, totalLength, options.OpeningWidth,
                out float endFade);

            for (int i = 0; i < outerVertexCount; i++)
            {
                if (protectedVertices.Contains(i)) { continue; }
                PathMetric3D metric = metrics[i];
                bool onSeam = openingDirections.TryGetValue(i, out Vector3 openingDirection);
                if (!onSeam && metric.Distance > falloffRadius) { continue; }

                float sign;
                if (negativeIndices.Contains(i)) { sign = -1f; }
                else if (seam.Contains(i)) { sign = 1f; }
                else if (Mathf.Abs(metric.Side) > 1e-7f) { sign = Mathf.Sign(metric.Side); }
                else { continue; }

                // Recorded intersections are the cut itself, even if re-evaluating the
                // nearest path segment leaves a small signed-distance residual.
                float lateral = onSeam ? 1f : 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01(Mathf.Abs(metric.Side) / falloffRadius));
                float endpoint = endFade > 1e-6f
                    ? Mathf.SmoothStep(0f, 1f,
                        Mathf.Clamp01(Mathf.Min(metric.Arc, totalLength - metric.Arc) / endFade))
                    : 1f;
                float irregularity = 1f + 0.07f * Mathf.Sin(metric.Arc * 173f + 0.8f)
                    + 0.035f * Mathf.Sin(metric.Arc * 397f + 2.1f);
                float amount = halfOpening * metric.Depth01 * lateral * endpoint * irregularity;
                data.Positions[i] += (onSeam ? openingDirection : metric.Across) * (sign * amount)
                    - metric.Normal * (amount * 0.18f);
            }

            BuildInterior3D(data, work[interiorSubmesh], negativeCopy, seamAdjacency,
                metrics, totalLength, options.MaximumDepth);

            mesh.Clear(false);
            mesh.indexFormat = data.Positions.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(data.Positions);
            if (data.HasNormals) { mesh.SetNormals(data.Normals); }
            if (data.HasTangents) { mesh.SetTangents(data.Tangents); }
            if (data.HasUv) { mesh.SetUVs(0, data.Uv); }
            mesh.subMeshCount = outputSubmeshes;

            for (int s = 0; s < outputSubmeshes; s++)
            {
                List<int> triangles = new List<int>(work[s].Count * 3);
                for (int i = 0; i < work[s].Count; i++)
                {
                    WorkTriangle3D triangle = work[s][i];
                    triangles.Add(triangle.A); triangles.Add(triangle.B); triangles.Add(triangle.C);
                }
                mesh.SetTriangles(triangles, s, false);
            }

            if (!data.HasNormals) { mesh.RecalculateNormals(); }
            mesh.RecalculateBounds();
            var lipPairs = new List<LipPair>();
            var paired = new Dictionary<int, int>();
            List<List<int>> orderedChains = BuildSeamChains3D(seamAdjacency);
            for (int c = 0; c < orderedChains.Count; c++)
            {
                for (int i = 0; i < orderedChains[c].Count; i++)
                {
                    int positive = orderedChains[c][i];
                    int negative = negativeCopy[positive];
                    paired[positive] = negative; paired[negative] = positive;
                    lipPairs.Add(new LipPair(positive, negative, c, i, openingDirections[positive]));
                }
            }
            var bindings = new List<InteriorBinding>(data.InteriorRows.Count);
            foreach (var row in data.InteriorRows)
                bindings.Add(new InteriorBinding(row.Key, paired[row.Key], row.Value));
            timer.Stop();
            return new Result(true, null, path.Count, affectedTriangles, verticesBefore,
                data.Positions.Count, timer.Elapsed.TotalMilliseconds, interiorSubmesh,
                lipPairs.ToArray(), bindings.ToArray());
        }

        private static Result Failed3D(string failure, int points) =>
            new Result(false, failure, points, 0, 0, 0, 0d, -1);

        private static Dictionary<int, Vector3> BuildOpeningDirections3D(VertexData3D data,
            Dictionary<int, HashSet<int>> adjacency, Dictionary<int, int> negativeCopy,
            List<PathMetric3D> metrics, float totalLength, float openingWidth, out float endFade)
        {
            Dictionary<int, Vector3> directions = new Dictionary<int, Vector3>();
            List<float> spacing = new List<float>();
            List<List<int>> chains = BuildSeamChains3D(adjacency);
            foreach (List<int> chain in chains)
            {
                Vector3[] across = new Vector3[chain.Count];
                for (int i = 0; i < chain.Count; i++)
                {
                    int vertex = chain[i];
                    Vector3 point = data.Positions[vertex];
                    int previous = i - 1;
                    int next = i + 1;
                    // Ignore numerical near-duplicates only when estimating the frame;
                    // retain every recorded vertex and edge in the actual topology.
                    while (previous >= 0 && (data.Positions[chain[previous]] - point).sqrMagnitude
                        <= PositionMergeEpsilonSquared3D) { previous--; }
                    while (next < chain.Count && (data.Positions[chain[next]] - point).sqrMagnitude
                        <= PositionMergeEpsilonSquared3D) { next++; }
                    Vector3 before = previous >= 0 ? data.Positions[chain[previous]] : point;
                    Vector3 after = next < chain.Count ? data.Positions[chain[next]] : point;
                    Vector3 tangent = Vector3.ProjectOnPlane(after - before, metrics[vertex].Normal);
                    Vector3 direction = Vector3.Cross(metrics[vertex].Normal, tangent);
                    float length = direction.magnitude;
                    if (length > PositionMergeEpsilon3D)
                    {
                        direction /= length;
                        // A topological chain may be traversed in either direction. Keep
                        // the positive lip on the same side of the original blade path.
                        if (Vector3.Dot(direction, metrics[vertex].Across) < 0f) { direction = -direction; }
                    }
                    else { direction = metrics[vertex].Across; }
                    across[i] = direction;
                    if (i > 0)
                    {
                        float step = Vector3.Distance(point, data.Positions[chain[i - 1]]);
                        if (step > PositionMergeEpsilon3D) { spacing.Add(step); }
                    }
                }

                for (int i = 0; i < chain.Count; i++)
                {
                    int vertex = chain[i];
                    Vector3 direction = across[i];
                    // One mild, non-iterative average of directions, never of positions.
                    // Do not smooth across a branch or between disconnected components.
                    if (i > 0 && i + 1 < chain.Count && adjacency[vertex].Count == 2)
                    {
                        direction = across[i] * 0.5f + (across[i - 1] + across[i + 1]) * 0.25f;
                    }
                    else if (adjacency[vertex].Count > 2) { direction = metrics[vertex].Across; }
                    direction = Vector3.ProjectOnPlane(direction, metrics[vertex].Normal);
                    direction = direction.sqrMagnitude > 1e-8f
                        ? direction.normalized : metrics[vertex].Across;
                    directions[vertex] = direction;
                    if (negativeCopy.TryGetValue(vertex, out int copy)) { directions[copy] = direction; }
                }
            }

            // Reach the unchanged full width within about three typical seam segments.
            // Bound the physical cap length so tiny/sliver edges cannot dictate its size.
            spacing.Sort();
            float typicalStep = spacing.Count > 0 ? spacing[spacing.Count / 2] : openingWidth / 3f;
            endFade = Mathf.Min(totalLength * 0.12f,
                Mathf.Clamp(typicalStep * 3f, openingWidth * 0.5f, openingWidth * 1.5f));
            return directions;
        }

        private static List<PathSegment3D> BuildSegments3D(IReadOnlyList<PathPoint> path, out float total)
        {
            List<PathSegment3D> segments = new List<PathSegment3D>(path.Count - 1);
            total = 0f;
            for (int i = 0; i < path.Count - 1; i++)
            {
                PathSegment3D segment = new PathSegment3D(path[i], path[i + 1], total);
                if (segment.Length < 0.0001f) { continue; }
                segments.Add(segment);
                total += segment.Length;
            }
            return segments;
        }

        private static PathMetric3D Evaluate3D(Vector3 point, List<PathSegment3D> segments)
        {
            float bestSquared = float.MaxValue;
            int bestIndex = 0;
            float bestT = 0f;
            for (int i = 0; i < segments.Count; i++)
            {
                PathSegment3D segment = segments[i];
                Vector3 chord = segment.B.Position - segment.A.Position;
                float chordSquared = chord.sqrMagnitude;
                float t = chordSquared > 1e-10f
                    ? Mathf.Clamp01(Vector3.Dot(point - segment.A.Position, chord) / chordSquared)
                    : 0f;
                Vector3 nearest = Vector3.LerpUnclamped(segment.A.Position, segment.B.Position, t);
                Vector3 delta = point - nearest;
                float squared = delta.sqrMagnitude;
                if (squared >= bestSquared) { continue; }
                bestSquared = squared;
                bestIndex = i;
                bestT = t;
            }
            PathSegment3D closest = segments[bestIndex];
            Vector3 bestChord = closest.B.Position - closest.A.Position;
            Vector3 normal = Vector3.Slerp(closest.A.Normal, closest.B.Normal, bestT).normalized;
            Vector3 tangent = Vector3.ProjectOnPlane(bestChord, normal).normalized;
            if (tangent.sqrMagnitude < 1e-8f) { tangent = bestChord.normalized; }
            Vector3 across = Vector3.Cross(normal, tangent).normalized;
            if (across.sqrMagnitude < 1e-8f) { across = Vector3.right; }
            Vector3 offset = point - Vector3.LerpUnclamped(closest.A.Position, closest.B.Position, bestT);
            return new PathMetric3D(Vector3.Dot(offset, across), Mathf.Sqrt(bestSquared),
                closest.Start + closest.Length * bestT,
                Mathf.Lerp(closest.A.Depth01, closest.B.Depth01, bestT), across, normal);
        }

        private static bool TryGetTriangleCutSegment3D(int a, int b, int c,
            VertexData3D data, List<PathMetric3D> metrics, List<PathSegment3D> segments,
            Dictionary<CutEdge, int> cache, out int first, out int second)
        {
            first = -1;
            second = -1;
            int[] triangle = { a, b, c };
            List<int> crossings = new List<int>(3);

            for (int i = 0; i < triangle.Length; i++)
            {
                int vertex = triangle[i];
                if (Mathf.Abs(metrics[vertex].Side) <= CutSideEpsilon3D)
                {
                    AddUniquePosition3D(crossings, vertex, data);
                }
            }

            for (int i = 0; i < triangle.Length; i++)
            {
                int from = triangle[i];
                int to = triangle[(i + 1) % triangle.Length];
                float fromSide = metrics[from].Side;
                float toSide = metrics[to].Side;
                bool crosses = (fromSide > CutSideEpsilon3D && toSide < -CutSideEpsilon3D)
                    || (fromSide < -CutSideEpsilon3D && toSide > CutSideEpsilon3D);
                if (!crosses) { continue; }

                int intersection = Intersection3D(from, to, data, metrics, segments, cache);
                if (intersection >= 0)
                {
                    AddUniquePosition3D(crossings, intersection, data);
                }
            }

            if (crossings.Count != 2
                || (data.Positions[crossings[0]] - data.Positions[crossings[1]]).sqrMagnitude
                    <= PositionMergeEpsilonSquared3D)
            {
                return false;
            }

            first = crossings[0];
            second = crossings[1];
            return true;
        }

        private static int AddUniquePosition3D(List<int> vertices, int candidate,
            VertexData3D data)
        {
            Vector3 position = data.Positions[candidate];
            for (int i = 0; i < vertices.Count; i++)
            {
                int existing = vertices[i];
                if (existing == candidate
                    || (data.Positions[existing] - position).sqrMagnitude
                        <= PositionMergeEpsilonSquared3D)
                {
                    return existing;
                }
            }

            vertices.Add(candidate);
            return candidate;
        }

        private static void EmitClipped3D(int a, int b, int c, bool keepPositive,
            List<WorkTriangle3D> output, VertexData3D data, List<PathMetric3D> metrics,
            List<PathSegment3D> segments, Dictionary<CutEdge, int> cache)
        {
            int[] input = { a, b, c };
            List<int> polygon = new List<int>(4);
            for (int i = 0; i < 3; i++)
            {
                int from = input[i];
                int to = input[(i + 1) % 3];
                float fromSide = metrics[from].Side;
                float toSide = metrics[to].Side;
                bool fromInside = keepPositive
                    ? fromSide >= -CutSideEpsilon3D
                    : fromSide <= CutSideEpsilon3D;
                bool toInside = keepPositive
                    ? toSide >= -CutSideEpsilon3D
                    : toSide <= CutSideEpsilon3D;
                if (fromInside) { AddUniquePosition3D(polygon, from, data); }
                if (fromInside == toInside) { continue; }

                int intersection = Intersection3D(from, to, data, metrics, segments, cache);
                if (intersection >= 0)
                {
                    AddUniquePosition3D(polygon, intersection, data);
                }
            }

            if (polygon.Count < 3) { return; }
            int side = keepPositive ? 1 : -1;
            Vector3 sourceA = data.Positions[a];
            Vector3 sourceB = data.Positions[b];
            Vector3 sourceC = data.Positions[c];
            Vector3 sourceCross = Vector3.Cross(sourceB - sourceA, sourceC - sourceA);
            float maximumSourceEdge = Mathf.Max(Vector3.Distance(sourceA, sourceB),
                Mathf.Max(Vector3.Distance(sourceB, sourceC), Vector3.Distance(sourceC, sourceA)));
            for (int i = 1; i < polygon.Count - 1; i++)
            {
                TryAddTriangle3D(output, data, polygon[0], polygon[i], polygon[i + 1], side,
                    maximumSourceEdge + PositionMergeEpsilon3D, sourceCross);
            }
        }

        private static int Intersection3D(int a, int b, VertexData3D data,
            List<PathMetric3D> metrics, List<PathSegment3D> segments,
            Dictionary<CutEdge, int> cache)
        {
            CutEdge edge = new CutEdge(a, b);
            if (cache.TryGetValue(edge, out int existing)) { return existing; }
            float sa = metrics[a].Side;
            float sb = metrics[b].Side;
            float denominator = sa - sb;
            if (Mathf.Abs(denominator) < 1e-9f) { return -1; }
            float t = Mathf.Clamp01(sa / denominator);
            Vector3 position = Vector3.LerpUnclamped(data.Positions[a], data.Positions[b], t);
            int index;
            if ((position - data.Positions[a]).sqrMagnitude <= PositionMergeEpsilonSquared3D)
            {
                index = a;
            }
            else if ((position - data.Positions[b]).sqrMagnitude <= PositionMergeEpsilonSquared3D)
            {
                index = b;
            }
            else
            {
                index = data.Interpolate(a, b, t);
                metrics.Add(Evaluate3D(data.Positions[index], segments));
            }
            cache[edge] = index;
            return index;
        }

        private static bool TryAddTriangle3D(List<WorkTriangle3D> output, VertexData3D data,
            int a, int b, int c, int side, float maximumEdge, Vector3 expectedNormal)
        {
            if (a == b || b == c || c == a) { return false; }

            Vector3 pa = data.Positions[a];
            Vector3 pb = data.Positions[b];
            Vector3 pc = data.Positions[c];
            float abSquared = (pb - pa).sqrMagnitude;
            float bcSquared = (pc - pb).sqrMagnitude;
            float caSquared = (pa - pc).sqrMagnitude;
            if (abSquared <= PositionMergeEpsilonSquared3D
                || bcSquared <= PositionMergeEpsilonSquared3D
                || caSquared <= PositionMergeEpsilonSquared3D)
            {
                return false;
            }

            if (!float.IsInfinity(maximumEdge))
            {
                float maximumEdgeSquared = maximumEdge * maximumEdge;
                if (abSquared > maximumEdgeSquared || bcSquared > maximumEdgeSquared
                    || caSquared > maximumEdgeSquared)
                {
                    return false;
                }
            }

            Vector3 cross = Vector3.Cross(pb - pa, pc - pa);
            if (cross.magnitude <= MinimumTriangleDoubleArea3D) { return false; }
            if (expectedNormal.sqrMagnitude > 1e-20f
                && Vector3.Dot(cross, expectedNormal) <= 0f)
            {
                return false;
            }

            output.Add(new WorkTriangle3D(a, b, c, side));
            return true;
        }

        private static void AddSeamConnection3D(int a, int b,
            Dictionary<int, HashSet<int>> adjacency)
        {
            if (a == b) { return; }
            if (!adjacency.TryGetValue(a, out HashSet<int> neighboursA))
            {
                neighboursA = new HashSet<int>();
                adjacency[a] = neighboursA;
            }
            if (!adjacency.TryGetValue(b, out HashSet<int> neighboursB))
            {
                neighboursB = new HashSet<int>();
                adjacency[b] = neighboursB;
            }
            neighboursA.Add(b);
            neighboursB.Add(a);
        }

        private static List<List<int>> BuildSeamChains3D(
            Dictionary<int, HashSet<int>> adjacency)
        {
            List<List<int>> chains = new List<List<int>>();
            HashSet<CutEdge> visited = new HashSet<CutEdge>();
            List<int> starts = new List<int>();

            foreach (KeyValuePair<int, HashSet<int>> pair in adjacency)
            {
                if (pair.Value.Count != 2) { starts.Add(pair.Key); }
            }
            starts.Sort();

            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i];
                List<int> neighbours = new List<int>(adjacency[start]);
                neighbours.Sort();
                for (int n = 0; n < neighbours.Count; n++)
                {
                    if (visited.Contains(new CutEdge(start, neighbours[n]))) { continue; }
                    chains.Add(TraceSeamChain3D(start, neighbours[n], adjacency, visited));
                }
            }

            // A component without an endpoint is a real topological loop. Walk its recorded
            // edges as a separate component; no synthetic last-to-first connection is added.
            List<int> vertices = new List<int>(adjacency.Keys);
            vertices.Sort();
            for (int i = 0; i < vertices.Count; i++)
            {
                int start = vertices[i];
                List<int> neighbours = new List<int>(adjacency[start]);
                neighbours.Sort();
                for (int n = 0; n < neighbours.Count; n++)
                {
                    if (visited.Contains(new CutEdge(start, neighbours[n]))) { continue; }
                    chains.Add(TraceSeamChain3D(start, neighbours[n], adjacency, visited));
                }
            }

            return chains;
        }

        private static List<int> TraceSeamChain3D(int start, int first,
            Dictionary<int, HashSet<int>> adjacency, HashSet<CutEdge> visited)
        {
            List<int> chain = new List<int> { start };
            int previous = start;
            int current = first;

            while (true)
            {
                visited.Add(new CutEdge(previous, current));
                chain.Add(current);

                if (!adjacency.TryGetValue(current, out HashSet<int> neighbours)
                    || neighbours.Count != 2)
                {
                    break;
                }

                int next = -1;
                foreach (int candidate in neighbours)
                {
                    if (candidate == previous
                        || visited.Contains(new CutEdge(current, candidate)))
                    {
                        continue;
                    }
                    next = candidate;
                    break;
                }

                if (next < 0) { break; }
                previous = current;
                current = next;
            }

            return chain;
        }

        private static void BuildInterior3D(VertexData3D data, List<WorkTriangle3D> output,
            Dictionary<int, int> negativeCopy, Dictionary<int, HashSet<int>> seamAdjacency,
            List<PathMetric3D> metrics, float totalLength, float maximumDepth)
        {
            List<List<int>> chains = BuildSeamChains3D(seamAdjacency);
            for (int c = 0; c < chains.Count; c++)
            {
                List<int> chain = chains[c];
                // Each lip has its own rows, shared by consecutive topological segments.
                // Keep separate components (and the two sides of the floor crease) separate.
                Dictionary<int, int> interiorRows = new Dictionary<int, int>();
                int firstVertex = data.Positions.Count;
                int firstTriangle = output.Count;
                for (int i = 0; i < chain.Count - 1; i++)
                {
                    int l0 = chain[i];
                    int l1 = chain[i + 1];
                    if (!negativeCopy.TryGetValue(l0, out int r0)
                        || !negativeCopy.TryGetValue(l1, out int r1))
                    {
                        continue;
                    }

                    PathMetric3D m0 = metrics[l0];
                    PathMetric3D m1 = metrics[l1];
                    float taper0 = Mathf.Sin(Mathf.Clamp01(m0.Arc / totalLength) * Mathf.PI);
                    float taper1 = Mathf.Sin(Mathf.Clamp01(m1.Arc / totalLength) * Mathf.PI);
                    Vector3 floor0 = (data.Positions[l0] + data.Positions[r0]) * 0.5f
                        - m0.Normal * (maximumDepth * m0.Depth01 * taper0);
                    Vector3 floor1 = (data.Positions[l1] + data.Positions[r1]) * 0.5f
                        - m1.Normal * (maximumDepth * m1.Depth01 * taper1);
                    float u0 = m0.Arc / totalLength;
                    float u1 = m1.Arc / totalLength;
                    AddInteriorQuad3D(data, output, interiorRows,
                        l0, l1, floor0, floor1, u0, u1);
                    AddInteriorQuad3D(data, output, interiorRows,
                        r1, r0, floor1, floor0, u1, u0);
                }
                SmoothInteriorFrames3D(data, output, firstVertex, firstTriangle);
            }
        }

        private static void AddInteriorQuad3D(VertexData3D data, List<WorkTriangle3D> output,
            Dictionary<int, int> rows, int upper0, int upper1,
            Vector3 lower0, Vector3 lower1, float u0, float u1)
        {
            Vector3 p0 = data.Positions[upper0];
            Vector3 p1 = data.Positions[upper1];
            Vector3 tangent = p1 - p0;
            int a = GetInteriorRow3D(data, rows, upper0, lower0, tangent, u0);
            int b = GetInteriorRow3D(data, rows, upper1, lower1, tangent, u1);
            Vector3 normal = Vector3.Cross(p1 - p0, lower1 - p0)
                + Vector3.Cross(lower1 - p0, lower0 - p0);

            // Only join adjacent rows longitudinally. There are no transverse caps or
            // synthetic last-to-first faces. Adjacent quads traverse their shared edge oppositely.
            TryAddTriangle3D(output, data, a, b, b + 1, 0, float.PositiveInfinity, normal);
            TryAddTriangle3D(output, data, a, b + 1, a + 1, 0, float.PositiveInfinity, normal);
            // Retain double-sided geometry, with a separate shared strip for reverse normals.
            TryAddTriangle3D(output, data, a + 2, b + 3, b + 2, 0, float.PositiveInfinity, -normal);
            TryAddTriangle3D(output, data, a + 2, a + 3, b + 3, 0, float.PositiveInfinity, -normal);
        }

        private static int GetInteriorRow3D(VertexData3D data, Dictionary<int, int> rows,
            int upper, Vector3 lower, Vector3 tangent, float u)
        {
            if (rows.TryGetValue(upper, out int row)) { return row; }
            row = data.AddInterior(data.Positions[upper], Vector3.zero, tangent, new Vector2(u, 0f));
            data.AddInterior(lower, Vector3.zero, tangent, new Vector2(u, 1f));
            data.AddInterior(data.Positions[upper], Vector3.zero, tangent, new Vector2(u, 0f));
            data.AddInterior(lower, Vector3.zero, tangent, new Vector2(u, 1f));
            rows.Add(upper, row);
            data.InteriorRows.Add(new KeyValuePair<int, int>(upper, row));
            return row;
        }

        private static void SmoothInteriorFrames3D(VertexData3D data,
            List<WorkTriangle3D> triangles, int firstVertex, int firstTriangle)
        {
            if (!data.HasNormals && !data.HasTangents) { return; }
            int count = data.Positions.Count - firstVertex;
            Vector3[] normals = new Vector3[count];
            Vector3[] tangents = data.HasTangents ? new Vector3[count] : null;
            Vector3[] bitangents = data.HasTangents ? new Vector3[count] : null;
            for (int i = firstTriangle; i < triangles.Count; i++)
            {
                WorkTriangle3D triangle = triangles[i];
                int a = triangle.A - firstVertex;
                int b = triangle.B - firstVertex;
                int c = triangle.C - firstVertex;
                Vector3 edge1 = data.Positions[triangle.B] - data.Positions[triangle.A];
                Vector3 edge2 = data.Positions[triangle.C] - data.Positions[triangle.A];
                Vector3 normal = Vector3.Cross(edge1, edge2);
                normals[a] += normal; normals[b] += normal; normals[c] += normal;
                if (!data.HasTangents || !data.HasUv) { continue; }

                Vector2 uv1 = data.Uv[triangle.B] - data.Uv[triangle.A];
                Vector2 uv2 = data.Uv[triangle.C] - data.Uv[triangle.A];
                float determinant = uv1.x * uv2.y - uv1.y * uv2.x;
                if (Mathf.Abs(determinant) <= 1e-12f) { continue; }
                float weight = normal.magnitude;
                Vector3 tangent = (edge1 * uv2.y - edge2 * uv1.y) / determinant * weight;
                Vector3 bitangent = (edge2 * uv1.x - edge1 * uv2.x) / determinant * weight;
                tangents[a] += tangent; tangents[b] += tangent; tangents[c] += tangent;
                bitangents[a] += bitangent; bitangents[b] += bitangent; bitangents[c] += bitangent;
            }

            // Normalize only the new interior attributes; the authored skin is untouched.
            for (int i = 0; i < count; i++)
            {
                float length = normals[i].magnitude;
                if (length <= MinimumTriangleDoubleArea3D) { continue; }
                Vector3 normal = normals[i] / length;
                int index = firstVertex + i;
                if (data.HasNormals) { data.Normals[index] = normal; }
                if (!data.HasTangents) { continue; }
                Vector3 tangent = tangents[i];
                if (tangent.sqrMagnitude <= 1e-20f)
                {
                    Vector4 original = data.Tangents[index];
                    tangent = new Vector3(original.x, original.y, original.z);
                }
                tangent -= normal * Vector3.Dot(normal, tangent);
                if (tangent.sqrMagnitude <= 1e-20f)
                {
                    tangent = Vector3.Cross(normal,
                        Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right);
                }
                tangent /= tangent.magnitude;
                float handedness = Vector3.Dot(Vector3.Cross(normal, tangent), bitangents[i]) < 0f
                    ? -1f : 1f;
                data.Tangents[index] = new Vector4(tangent.x, tangent.y, tangent.z, handedness);
            }
        }
    }
}
