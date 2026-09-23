using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRSurgery.Cutting
{
    /// <summary>
    /// Reduced, cut-aware elastic membrane for gameplay, not calibrated human biomechanics.
    /// Control nodes and visual weights follow surface connectivity, never bridge a wound.
    /// Positional grips can be retained; removing them lets the skin recover with damping.
    /// No volume FEM, sutures, organs or general self-collision are implemented.
    /// </summary>
    [DisallowMultipleComponent, RequireComponent(typeof(CuttableTissue))]
    public sealed class SkinDeformation : MonoBehaviour
    {
        [SerializeField, Range(0.003f, 0.012f)] private float nodeSpacing = 0.007f;
        [SerializeField, Range(0.015f, 0.06f)] private float maximumRetraction = 0.035f;
        [SerializeField, Range(0.001f, 0.008f)] private float skinThickness = 0.003f;
        [SerializeField, Range(8f, 45f)] private float responseRate = 24f;
        [SerializeField, Range(0f, 0.15f)] private float foundationStiffness = 0.012f;

        private struct Influence { public int Node; public float Weight; }
        private struct Link { public int A, B; public float Weight; }
        private struct HeapEntry { public int Vertex; public float Distance; }
        private sealed class Grip { public int Vertex; public Vector3 Target; public bool Held; }

        private readonly List<HeapEntry> _heap = new List<HeapEntry>();
        private readonly List<Grip> _grips = new List<Grip>();
        private readonly List<Link> _links = new List<Link>();
        private readonly List<int> _seeds = new List<int>();
        private readonly HashSet<int> _sourceBoundary = new HashSet<int>();
        private CuttableTissue _tissue;
        private Mesh _mesh;
        private int _revision = -1;
        private Vector3[] _rest, _positions, _lastValid, _restNormals, _normals, _restNormalSum, _normalSum;
        private Vector4[] _restTangents, _tangents;
        private int[] _triangles, _allTriangles, _owner;
        private bool[] _surface, _nodePinned;
        private List<int>[] _adjacency, _nodeLinks;
        private List<Influence>[] _bindings;
        private float[] _distance, _nearest, _rimWeight;
        private Vector3[] _offset, _goal, _velocity, _nodeNormals;
        private Grip _drag;
        private bool _dirtyGoal, _moving;
        private float _colliderClock;

        public bool IsDragging => _drag != null;
        public int HeldGripCount { get { int n = 0; foreach (Grip g in _grips) { if (g.Held) { n++; } } return n; } }
        public bool IsDeforming => _moving || _grips.Count > 0;
        public int ActiveVertexCount { get; private set; }
        public int ConstraintCount => _links.Count;
        public int PhysicsNodeCount => _seeds.Count;
        public float MaximumDisplacement { get; private set; }
        public float AcceptedMotionFraction { get; private set; } = 1f;
        public Vector3 DragOffset => _drag == null ? Vector3.zero : _positions[_drag.Vertex] - _rest[_drag.Vertex];

        private void Awake()
        {
            _tissue = GetComponent<CuttableTissue>();
            Mesh initial = GetComponent<MeshFilter>().sharedMesh;
            var counts = new Dictionary<long, int>();
            int[] triangles = initial.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
                for (int j = 0; j < 3; j++)
                {
                    long key = Key(triangles[i + j], triangles[i + (j + 1) % 3]);
                    counts.TryGetValue(key, out int count); counts[key] = count + 1;
                }
            foreach (var edge in counts)
                if (edge.Value == 1)
                { _sourceBoundary.Add((int)(edge.Key >> 32)); _sourceBoundary.Add((int)(edge.Key & 0xffffffff)); }
            _tissue.BeforeTopologyChange += BeforeCut;
        }

        private static long Key(int a, int b) => ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
        private void BeforeCut(CuttableTissue tissue) => RestoreRestPose();
        private void LateUpdate() => AdvanceSimulation(Time.deltaTime);
        private void OnDisable() => RestoreRestPose();
        private void OnDestroy() { if (_tissue != null) { _tissue.BeforeTopologyChange -= BeforeCut; } }

        private bool EnsureModel()
        {
            if (_tissue == null || _tissue.IsRecording || _tissue.IsOpening) { return false; }
            Mesh mesh = GetComponent<MeshFilter>().sharedMesh;
            if (_mesh == mesh && _revision == _tissue.TopologyRevision) { return true; }
            _mesh = mesh; _revision = _tissue.TopologyRevision;
            _grips.Clear(); _drag = null; _seeds.Clear(); _links.Clear();
            _rest = mesh.vertices; int n = _rest.Length;
            _positions = (Vector3[])_rest.Clone(); _lastValid = (Vector3[])_rest.Clone();
            _restNormals = mesh.normals; _normals = (Vector3[])_restNormals.Clone();
            _restTangents = mesh.tangents; _tangents = (Vector4[])_restTangents.Clone();
            _restNormalSum = new Vector3[n]; _normalSum = new Vector3[n];
            _surface = new bool[n]; _owner = new int[n]; _distance = new float[n];
            _nearest = new float[n]; _rimWeight = new float[n];
            _adjacency = new List<int>[n]; _bindings = new List<Influence>[n];
            _allTriangles = mesh.triangles;
            var surfaceTriangles = new List<int>();
            for (int s = 0; s < _tissue.SurfaceSubmeshCount; s++) { surfaceTriangles.AddRange(mesh.GetTriangles(s)); }
            _triangles = surfaceTriangles.ToArray();
            var edges = new HashSet<long>();
            for (int i = 0; i < _triangles.Length; i += 3)
                for (int j = 0; j < 3; j++)
                {
                    int a = _triangles[i + j], b = _triangles[i + (j + 1) % 3];
                    _surface[a] = _surface[b] = true;
                    if (a == b || !edges.Add(Key(a, b))) { continue; }
                    if (_adjacency[a] == null) { _adjacency[a] = new List<int>(); }
                    if (_adjacency[b] == null) { _adjacency[b] = new List<int>(); }
                    _adjacency[a].Add(b); _adjacency[b].Add(a);
                }
            for (int i = 0; i < n; i++) { _nearest[i] = float.PositiveInfinity; _owner[i] = -1; }
            // Microscopic clipping edges do not become independently simulated springs.
            for (int i = 0; i < n; i++)
            {
                if (!_surface[i] || _nearest[i] < nodeSpacing) { continue; }
                int node = _seeds.Count; _seeds.Add(i);
                Walk(i, nodeSpacing * 1.2f, (v, d) =>
                { if (d < _nearest[v]) { _nearest[v] = d; _owner[v] = node; } });
            }
            int nodes = _seeds.Count;
            _offset = new Vector3[nodes]; _goal = new Vector3[nodes]; _velocity = new Vector3[nodes];
            _nodeNormals = new Vector3[nodes]; _nodePinned = new bool[nodes]; _nodeLinks = new List<int>[nodes];
            for (int i = 0; i < nodes; i++)
            { _nodeNormals[i] = _restNormals[_seeds[i]]; _nodeLinks[i] = new List<int>(); }
            var coarseEdges = new HashSet<long>();
            foreach (long edge in edges)
            {
                int a = _owner[(int)(edge >> 32)], b = _owner[(int)(edge & 0xffffffff)];
                if (a < 0 || b < 0 || a == b || !coarseEdges.Add(Key(a, b))) { continue; }
                float length = Vector3.Distance(_rest[_seeds[a]], _rest[_seeds[b]]);
                int index = _links.Count;
                _links.Add(new Link { A = a, B = b, Weight = Mathf.Clamp(nodeSpacing / Mathf.Max(length, nodeSpacing * 0.5f), 0.25f, 2f) });
                _nodeLinks[a].Add(index); _nodeLinks[b].Add(index);
            }

            ClearWalk();
            foreach (int v in _sourceBoundary)
                if (v < n && _surface[v]) { _distance[v] = 0f; Push(v, 0f); }
            WalkQueued(nodeSpacing * 2f, null);
            for (int i = 0; i < n; i++)
                _rimWeight[i] = _surface[i] ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_distance[i] / (nodeSpacing * 1.5f))) : 0f;
            for (int i = 0; i < nodes; i++) { _nodePinned[i] = _distance[_seeds[i]] < nodeSpacing * 0.6f; }

            float support = nodeSpacing * 2.5f;
            for (int i = 0; i < nodes; i++)
            {
                int node = i;
                Walk(_seeds[i], support, (v, d) =>
                {
                    float q = Mathf.Max(0f, 1f - d * d / (support * support));
                    if (_bindings[v] == null) { _bindings[v] = new List<Influence>(); }
                    _bindings[v].Add(new Influence { Node = node, Weight = q * q * q });
                });
            }
            for (int v = 0; v < n; v++)
            {
                if (_bindings[v] == null) { continue; }
                float sum = 0f; foreach (Influence influence in _bindings[v]) { sum += influence.Weight; }
                for (int j = 0; j < _bindings[v].Count; j++)
                {
                    Influence influence = _bindings[v][j];
                    influence.Weight *= _rimWeight[v] / Mathf.Max(sum, 1e-12f);
                    _bindings[v][j] = influence;
                }
            }
            // Keep the accepted shared strip indices/winding. Each lip owns its wall:
            // the old common V-shaped floor must not mechanically seal a through-skin cut.
            foreach (var binding in _tissue.InteriorBindings)
            {
                int u = binding.Upper, r = binding.Row;
                _rest[r] = _rest[r + 2] = _rest[u];
                _rest[r + 1] = _rest[r + 3] = _rest[u] - _restNormals[u] * skinThickness;
            }
            AccumulateNormals(_rest, _restNormalSum);
            foreach (var binding in _tissue.InteriorBindings)
                for (int i = 0; i < 4; i++)
                {
                    int v = binding.Row + i;
                    if (_restNormalSum[v].sqrMagnitude > 1e-20f) { _restNormals[v] = _restNormalSum[v].normalized; }
                }
            Array.Copy(_rest, _positions, n); Array.Copy(_rest, _lastValid, n);
            Array.Copy(_restNormals, _normals, n);
            _mesh.vertices = _rest; _mesh.normals = _restNormals; _mesh.RecalculateBounds();
            _tissue.RefreshDeformedCollider();
            _moving = false; _dirtyGoal = false; MaximumDisplacement = 0f; ActiveVertexCount = 0;
            return true;
        }

        public bool BeginDrag(RaycastHit hit)
        {
            if (!EnsureModel() || hit.collider != GetComponent<MeshCollider>()) { return false; }
            int triangle = hit.triangleIndex * 3;
            if (triangle < 0 || triangle + 2 >= _triangles.Length) { return false; }
            Vector3 b = hit.barycentricCoordinate;
            int corner = b.x >= b.y && b.x >= b.z ? 0 : b.y >= b.z ? 1 : 2;
            return BeginDragVertex(_triangles[triangle + corner]);
        }

        // Mesh-local IDs are valid only for the current tissue topology revision.
        public bool BeginDragVertex(int vertex)
        {
            if (!EnsureModel() || vertex < 0 || vertex >= _rest.Length || !_surface[vertex]
                || _rimWeight[vertex] < 0.2f || _tissue.LipPairs.Count == 0) { return false; }
            EndDrag();
            _drag = new Grip { Vertex = vertex, Target = _positions[vertex] - _rest[vertex] };
            _grips.Add(_drag); _dirtyGoal = _moving = true;
            return true;
        }

        public void SetDragOffset(Vector3 localOffset)
        {
            if (_drag == null) { return; }
            _drag.Target = Vector3.ClampMagnitude(localOffset, maximumRetraction);
            _dirtyGoal = _moving = true;
        }

        public void HoldCurrentDrag()
        {
            if (_drag == null) { return; }
            _drag.Held = true; _drag.Target = DragOffset; _dirtyGoal = true;
        }

        public void EndDrag()
        {
            if (_drag == null) { return; }
            if (!_drag.Held) { _grips.Remove(_drag); }
            _drag = null; _dirtyGoal = _moving = true;
            _tissue.RefreshDeformedCollider();
        }

        public void ReleaseRetraction()
        { _grips.Clear(); _drag = null; _dirtyGoal = _moving = true; }

        public void RestoreRestPose()
        {
            _grips.Clear(); _drag = null;
            if (_mesh != null && _mesh == GetComponent<MeshFilter>().sharedMesh && _revision == _tissue.TopologyRevision)
            {
                Array.Copy(_rest, _positions, _rest.Length); Array.Copy(_rest, _lastValid, _rest.Length);
                _mesh.vertices = _rest; _mesh.normals = _restNormals;
                if (_restTangents.Length == _rest.Length) { _mesh.tangents = _restTangents; }
                _mesh.RecalculateBounds(); _tissue.RefreshDeformedCollider();
            }
            if (_offset != null)
            { Array.Clear(_offset, 0, _offset.Length); Array.Clear(_goal, 0, _goal.Length); Array.Clear(_velocity, 0, _velocity.Length); }
            _moving = _dirtyGoal = false; MaximumDisplacement = 0f; ActiveVertexCount = 0;
        }

        private Vector3 Blend(int vertex, Vector3[] offsets)
        {
            Vector3 value = Vector3.zero;
            if (_bindings[vertex] != null)
                foreach (Influence influence in _bindings[vertex]) { value += offsets[influence.Node] * influence.Weight; }
            return value;
        }

        private void SolveGoal()
        {
            if (_grips.Count == 0) { Array.Clear(_goal, 0, _goal.Length); _dirtyGoal = false; return; }
            // Quasi-static elastic graph followed by a critically damped response.
            for (int iteration = 0; iteration < 48; iteration++)
            {
                for (int node = 0; node < _goal.Length; node++)
                {
                    if (_nodePinned[node]) { _goal[node] = Vector3.zero; continue; }
                    Vector3 sum = Vector3.zero; float weights = 0f;
                    foreach (int index in _nodeLinks[node])
                    {
                        Link link = _links[index]; int other = link.A == node ? link.B : link.A;
                        sum += _goal[other] * link.Weight; weights += link.Weight;
                    }
                    if (weights > 0f) { _goal[node] = sum / (weights * (1f + foundationStiffness)); }
                }
                foreach (Grip grip in _grips)
                {
                    Vector3 error = grip.Target - Blend(grip.Vertex, _goal); float denominator = 0f;
                    foreach (Influence influence in _bindings[grip.Vertex])
                        if (!_nodePinned[influence.Node]) { denominator += influence.Weight * influence.Weight; }
                    if (denominator < 1e-8f) { continue; }
                    foreach (Influence influence in _bindings[grip.Vertex])
                        if (!_nodePinned[influence.Node]) { _goal[influence.Node] += error * (influence.Weight / denominator); }
                }
                for (int node = 0; node < _goal.Length; node++)
                {
                    _goal[node] = Vector3.ClampMagnitude(_goal[node], maximumRetraction * 1.5f);
                    float depth = Vector3.Dot(_goal[node], _nodeNormals[node]);
                    if (depth < -0.001f) { _goal[node] += _nodeNormals[node] * (-0.001f - depth); }
                }
            }
            _dirtyGoal = false;
        }

        public void AdvanceSimulation(float deltaTime)
        {
            if (!EnsureModel() || deltaTime <= 0f || (!_moving && !_dirtyGoal)) { return; }
            if (_dirtyGoal) { SolveGoal(); }
            float dt = Mathf.Min(deltaTime, 0.05f);
            float decay = Mathf.Exp(-responseRate * dt);
            for (int node = 0; node < _offset.Length; node++)
            {
                Vector3 error = _offset[node] - _goal[node];
                Vector3 step = (_velocity[node] + error * responseRate) * dt;
                _offset[node] = _goal[node] + (error + step) * decay;
                _velocity[node] = (_velocity[node] - step * responseRate) * decay;
            }
            for (int v = 0; v < _rest.Length; v++)
                if (_surface[v]) { _positions[v] = _rest[v] + Blend(v, _offset); }
            AcceptedMotionFraction = 1f;
            while (!SurfaceValid() && AcceptedMotionFraction > 0.0001f)
            {
                AcceptedMotionFraction *= 0.5f;
                for (int v = 0; v < _rest.Length; v++)
                    if (_surface[v]) { _positions[v] = Vector3.Lerp(_lastValid[v], _positions[v], 0.5f); }
            }
            if (!SurfaceValid()) { Array.Copy(_lastValid, _positions, _rest.Length); }
            MaximumDisplacement = 0f; ActiveVertexCount = 0;
            for (int v = 0; v < _rest.Length; v++)
                if (_surface[v])
                {
                    float d = Vector3.Distance(_positions[v], _rest[v]);
                    MaximumDisplacement = Mathf.Max(MaximumDisplacement, d);
                    if (d > 1e-5f) { ActiveVertexCount++; }
                }
            UpdateWalls(); UpdateFrames();
            _mesh.vertices = _positions; _mesh.normals = _normals;
            if (_tangents.Length == _positions.Length) { _mesh.tangents = _tangents; }
            _mesh.RecalculateBounds(); Array.Copy(_positions, _lastValid, _positions.Length);
            float remaining = 0f;
            for (int node = 0; node < _offset.Length; node++)
                remaining = Mathf.Max(remaining, (_offset[node] - _goal[node]).magnitude, _velocity[node].magnitude * 0.05f);
            _moving = remaining > 1e-5f;
            _colliderClock += dt;
            if (!IsDragging && (!_moving || _colliderClock >= 0.12f))
            { _tissue.RefreshDeformedCollider(); _colliderClock = 0f; }
        }

        private bool SurfaceValid()
        {
            for (int i = 0; i < _triangles.Length; i += 3)
            {
                int a = _triangles[i], b = _triangles[i + 1], c = _triangles[i + 2];
                Vector3 rest = Vector3.Cross(_rest[b] - _rest[a], _rest[c] - _rest[a]);
                Vector3 current = Vector3.Cross(_positions[b] - _positions[a], _positions[c] - _positions[a]);
                if (Vector3.Dot(rest, current) < rest.sqrMagnitude * 0.04f) { return false; }
            }
            foreach (var pair in _tissue.LipPairs)
            {
                float gap = Vector3.Dot(_positions[pair.Positive] - _positions[pair.Negative], pair.Across);
                float rest = Vector3.Dot(_rest[pair.Positive] - _rest[pair.Negative], pair.Across);
                if (gap < Mathf.Min(rest, 0.0001f) - 0.00001f) { return false; }
            }
            return true;
        }

        private void UpdateWalls()
        {
            AccumulateNormals(_positions, _normalSum);
            foreach (var binding in _tissue.InteriorBindings)
            {
                int upper = binding.Upper, row = binding.Row;
                Quaternion frame = Frame(upper);
                Vector3 normal = frame * _restNormals[upper];
                _positions[row] = _positions[row + 2] = _positions[upper];
                _positions[row + 1] = _positions[row + 3] = _positions[upper] - normal * skinThickness;
            }
        }

        private Quaternion Frame(int vertex) =>
            _restNormalSum[vertex].sqrMagnitude > 1e-20f && _normalSum[vertex].sqrMagnitude > 1e-20f
            ? Quaternion.FromToRotation(_restNormalSum[vertex], _normalSum[vertex]) : Quaternion.identity;

        private void UpdateFrames()
        {
            AccumulateNormals(_positions, _normalSum);
            for (int v = 0; v < _positions.Length; v++)
            {
                Quaternion rotation = Frame(v); _normals[v] = rotation * _restNormals[v];
                if (_tangents.Length != _positions.Length) { continue; }
                Vector4 t = _restTangents[v]; Vector3 xyz = rotation * new Vector3(t.x, t.y, t.z);
                _tangents[v] = new Vector4(xyz.x, xyz.y, xyz.z, t.w);
            }
        }

        private void AccumulateNormals(Vector3[] positions, Vector3[] sum)
        {
            Array.Clear(sum, 0, sum.Length);
            for (int i = 0; i < _allTriangles.Length; i += 3)
            {
                int a = _allTriangles[i], b = _allTriangles[i + 1], c = _allTriangles[i + 2];
                Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                sum[a] += normal; sum[b] += normal; sum[c] += normal;
            }
        }

        private void ClearWalk()
        { _heap.Clear(); for (int i = 0; i < _distance.Length; i++) { _distance[i] = float.PositiveInfinity; } }

        private void Walk(int seed, float radius, Action<int, float> visit)
        { ClearWalk(); _distance[seed] = 0f; Push(seed, 0f); WalkQueued(radius, visit); }

        private void WalkQueued(float radius, Action<int, float> visit)
        {
            while (_heap.Count > 0)
            {
                HeapEntry e = Pop();
                if (e.Distance > radius || e.Distance > _distance[e.Vertex] + 1e-8f) { continue; }
                visit?.Invoke(e.Vertex, e.Distance);
                if (_adjacency[e.Vertex] == null) { continue; }
                foreach (int next in _adjacency[e.Vertex])
                {
                    float distance = e.Distance + Vector3.Distance(_rest[e.Vertex], _rest[next]);
                    if (distance >= _distance[next] || distance > radius) { continue; }
                    _distance[next] = distance; Push(next, distance);
                }
            }
        }

        private void Push(int vertex, float distance)
        {
            var e = new HeapEntry { Vertex = vertex, Distance = distance };
            int index = _heap.Count; _heap.Add(e);
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (_heap[parent].Distance <= distance) { break; }
                _heap[index] = _heap[parent]; index = parent;
            }
            _heap[index] = e;
        }

        private HeapEntry Pop()
        {
            HeapEntry result = _heap[0], last = _heap[_heap.Count - 1];
            _heap.RemoveAt(_heap.Count - 1); int index = 0;
            while (index * 2 + 1 < _heap.Count)
            {
                int child = index * 2 + 1;
                if (child + 1 < _heap.Count && _heap[child + 1].Distance < _heap[child].Distance) { child++; }
                if (_heap[child].Distance >= last.Distance) { break; }
                _heap[index] = _heap[child]; index = child;
            }
            if (_heap.Count > 0) { _heap[index] = last; }
            return result;
        }
    }
}
