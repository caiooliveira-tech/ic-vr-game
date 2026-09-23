using System;
using System.Collections.Generic;
using UnityEngine;
using VRSurgery.Tools;

namespace VRSurgery.Cutting
{
    /// <summary>
    /// Reusable runtime owner for one cuttable mesh. It records one filtered local-space path and
    /// previews topology from a stable stroke snapshot while cutting. The contact collider is
    /// committed at END CUT. Local spring relaxation animates the opening without recutting it.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public sealed class CuttableTissue : MonoBehaviour
    {
        public readonly struct Contact
        {
            public readonly bool InContact;
            public readonly bool CanCut;
            public readonly Vector3 LocalPoint;
            public readonly Vector3 LocalNormal;
            public readonly float Depth01;

            public Contact(bool inContact, bool canCut, Vector3 localPoint,
                Vector3 localNormal, float depth01)
            {
                InContact = inContact;
                CanCut = canCut;
                LocalPoint = localPoint;
                LocalNormal = localNormal;
                Depth01 = depth01;
            }
        }

        [Header("Tissue")]
        [SerializeField] private bool cuttable = true;
        [SerializeField, Min(0.001f)] private float maximumDepth = 0.007f;
        [SerializeField, Min(0.0005f)] private float openingWidth = 0.0045f;
        [SerializeField, Min(0.0005f)] private float minimumPathSpacing = 0.002f;
        [SerializeField, Range(0f, 1f)] private float resistance = 0.35f;
        [SerializeField] private Material interiorMaterial;

        [Header("Progressive opening")]
        [SerializeField, Min(0.02f)] private float previewInterval = 0.08f;
        [SerializeField, Range(8f, 60f)] private float openingSpringRate = 24f;
        [SerializeField] private bool restrictToRegion;
        [SerializeField] private Bounds localCutRegion;

        [Header("Contact")]
        [SerializeField] private Vector3 localOutward = Vector3.up;
        [SerializeField, Range(0f, 1f)] private float minimumEdgeAlignment = 0.45f;
        [SerializeField, Range(0f, 1f)] private float maximumLateralNormalDot = 0.65f;

        [Header("Debug")]
        [SerializeField] private bool logMetrics;

        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private MeshCollider _collider;
        private Mesh _sourceMesh;
        private Mesh _runtimeMesh;
        private Material[] _sourceMaterials;
        private int _surfaceSubmeshCount;
        private readonly List<MeshIncision.PathPoint> _path = new List<MeshIncision.PathPoint>(64);
        private Vector3 _filteredPoint;
        private Mesh _strokeBaseMesh;
        private Mesh _contactMesh;
        private readonly List<float> _sampleTimes = new List<float>(64);
        private bool _previewDirty;
        private float _simulationTime;
        private float _nextPreviewTime;
        private MeshIncision.Result _lastPreview;
        private Vector3[] _openPositions;
        private Vector3[] _displayPositions;
        private Vector3[] _openingOffsets;
        private float[] _openedAt;
        private readonly List<MeshIncision.LipPair> _lipPairs = new List<MeshIncision.LipPair>();
        private readonly List<MeshIncision.InteriorBinding> _interiorBindings = new List<MeshIncision.InteriorBinding>();

        public bool IsRecording { get; private set; }
        public bool IsOpening => _openPositions != null;
        public int SurfaceSubmeshCount => _surfaceSubmeshCount;
        public int TopologyRevision { get; private set; }
        public IReadOnlyList<MeshIncision.LipPair> LipPairs => _lipPairs;
        public IReadOnlyList<MeshIncision.InteriorBinding> InteriorBindings => _interiorBindings;
        public int RecordedPointCount => _path.Count;
        public int PreviewRevision { get; private set; }
        public MeshIncision.Result LastPreview => _lastPreview;

        public event Action<CuttableTissue> CutBegan;
        public event Action<CuttableTissue> BeforeTopologyChange;
        public event Action<CuttableTissue, int> CutContinued;
        public event Action<CuttableTissue, MeshIncision.Result> CutEnded;

        private void Awake() => EnsureRuntimeMesh();
        private void Update() => AdvanceSimulation(Time.deltaTime);

        private void OnDestroy()
        {
            ReleaseMesh(_runtimeMesh);
            ReleaseMesh(_strokeBaseMesh);
            ReleaseMesh(_contactMesh);
        }

        private static void ReleaseMesh(Mesh mesh)
        {
            if (mesh == null) { return; }
            if (Application.isPlaying) { Destroy(mesh); }
            else { DestroyImmediate(mesh); }
        }

        /// <summary>Limits contact and accepted paths to the supplied mesh-local thorax region.</summary>
        public void ConfigureCutRegion(Bounds region, Vector3 outward)
        {
            localCutRegion = region;
            restrictToRegion = true;
            localOutward = outward.sqrMagnitude > 1e-8f ? outward.normalized : Vector3.up;
        }

        private bool InCutRegion(Vector3 point, Vector3 normal) => !restrictToRegion
            || (localCutRegion.Contains(point) && Vector3.Dot(normal, localOutward) > 0.25f);

        public void Configure(float maxDepth, float width, float spacing, float resistance01,
            Material inside = null)
        {
            maximumDepth = Mathf.Max(0.001f, maxDepth);
            openingWidth = Mathf.Max(0.0005f, width);
            minimumPathSpacing = Mathf.Max(0.0005f, spacing);
            resistance = Mathf.Clamp01(resistance01);
            interiorMaterial = inside;
            EnsureRuntimeMesh();
        }

        /// <summary>
        /// Projects the moving tip onto this curved MeshCollider and rejects superficial contact,
        /// motion across the blade edge, and a blade face laid flat against the skin.
        /// </summary>
        public Contact SampleContact(Vector3 worldPrevious, Vector3 worldCurrent, BladeTip blade)
        {
            EnsureRuntimeMesh();
            if (!cuttable || _collider == null || _runtimeMesh == null) { return default; }

            Vector3 outward = transform.TransformDirection(localOutward).normalized;
            float probe = maximumDepth + 0.03f;
            Ray ray = new Ray(worldCurrent + outward * probe, -outward);
            if (!_collider.Raycast(ray, out RaycastHit hit, probe * 2f + maximumDepth))
            {
                return default;
            }

            Vector3 normal = hit.normal.normalized;
            if (Vector3.Dot(normal, outward) < 0f) { normal = -normal; }
            if (!InCutRegion(transform.InverseTransformPoint(hit.point),
                transform.InverseTransformDirection(normal))) { return default; }
            float penetration = -Vector3.Dot(worldCurrent - hit.point, normal);
            if (penetration < -0.0005f || penetration > maximumDepth * 1.5f)
            {
                return default;
            }

            float depth01 = Mathf.Clamp01(penetration / maximumDepth);
            Vector3 motion = Vector3.ProjectOnPlane(worldCurrent - worldPrevious, normal);
            bool oriented = motion.sqrMagnitude > 1e-8f;
            if (oriented && blade != null)
            {
                Vector3 edge = Vector3.ProjectOnPlane(blade.EdgeDirection, normal).normalized;
                float edgeAlignment = edge.sqrMagnitude > 1e-8f
                    ? Mathf.Abs(Vector3.Dot(motion.normalized, edge))
                    : 0f;
                float lateralDot = Mathf.Abs(Vector3.Dot(blade.FaceNormal, normal));
                oriented = edgeAlignment >= minimumEdgeAlignment
                    && lateralDot <= maximumLateralNormalDot;
            }

            float depthThreshold = Mathf.Lerp(0.12f, 0.42f, resistance);
            bool canCut = oriented && depth01 >= depthThreshold;
            return new Contact(true, canCut, transform.InverseTransformPoint(hit.point),
                transform.InverseTransformDirection(normal).normalized, depth01);
        }

        public bool BeginCut(Contact contact)
        {
            if (!cuttable || !contact.CanCut || IsRecording
                || !InCutRegion(contact.LocalPoint, contact.LocalNormal)) { return false; }
            EnsureRuntimeMesh();
            if (_runtimeMesh == null) { return false; }
            BeforeTopologyChange?.Invoke(this);
            FinishOpening();
            _strokeBaseMesh = Instantiate(_runtimeMesh);
            _path.Clear();
            _sampleTimes.Clear();
            _sampleTimes.Add(_simulationTime);
            _lastPreview = default;
            _previewDirty = false;
            _nextPreviewTime = _simulationTime;
            _filteredPoint = contact.LocalPoint;
            _path.Add(new MeshIncision.PathPoint(
                contact.LocalPoint, contact.LocalNormal, contact.Depth01));
            IsRecording = true;
            CutBegan?.Invoke(this);
            return true;
        }

        public bool ContinueCut(Contact contact)
        {
            if (!IsRecording || !contact.CanCut
                || !InCutRegion(contact.LocalPoint, contact.LocalNormal)) { return false; }

            // Exponential smoothing damps tracking jitter before the spacing gate. Points that do
            // not advance the cut deepen the previous sample instead of multiplying vertices.
            _filteredPoint = Vector3.Lerp(_filteredPoint, contact.LocalPoint, 0.65f);
            MeshIncision.PathPoint last = _path[_path.Count - 1];
            if (Vector3.Distance(last.Position, _filteredPoint) < minimumPathSpacing)
            {
                if (contact.Depth01 > last.Depth01)
                {
                    _path[_path.Count - 1] = new MeshIncision.PathPoint(
                        last.Position, Vector3.Slerp(last.Normal, contact.LocalNormal, 0.5f),
                        contact.Depth01);
                    _previewDirty = true;
                }
                return false;
            }

            _path.Add(new MeshIncision.PathPoint(
                _filteredPoint, contact.LocalNormal, contact.Depth01));
            _sampleTimes.Add(_simulationTime);
            _previewDirty = true;
            CutContinued?.Invoke(this, _path.Count);
            return true;
        }

        public MeshIncision.Result EndCut()
        {
            if (!IsRecording)
            {
                return new MeshIncision.Result(false, "Nenhuma incisão ativa.", 0, 0, 0, 0, 0d, -1);
            }

            if (_previewDirty) { RebuildPreview(); }
            IsRecording = false;
            MeshIncision.Result result = _lastPreview.Success ? _lastPreview
                : new MeshIncision.Result(false, "Trajetória insuficiente para abrir a pele.",
                    _path.Count, 0, _runtimeMesh.vertexCount, _runtimeMesh.vertexCount, 0d, -1);
            if (result.Success) { RecordTopology(result); CommitContactMesh(); }
            else if (_strokeBaseMesh != null)
            {
                FinishOpening();
                ReleaseMesh(_runtimeMesh);
                _runtimeMesh = Instantiate(_strokeBaseMesh);
                _runtimeMesh.MarkDynamic();
                _filter.sharedMesh = _runtimeMesh;
            }
            ReleaseMesh(_strokeBaseMesh);
            _strokeBaseMesh = null;
            _path.Clear();
            _sampleTimes.Clear();
            CutEnded?.Invoke(this, result);
            return result;
        }

        /// <summary>Deterministic tick, also used by the Editor probe; no topology work per physics step.</summary>
        public void AdvanceSimulation(float deltaTime)
        {
            _simulationTime += Mathf.Clamp(deltaTime, 0f, 0.25f);
            if (IsRecording && _previewDirty && _simulationTime >= _nextPreviewTime)
            {
                RebuildPreview();
                _nextPreviewTime = _simulationTime + previewInterval;
            }
            if (_openPositions == null) { return; }
            bool moving = false;
            for (int i = 0; i < _openPositions.Length; i++)
            {
                float age = Mathf.Max(0f, _simulationTime - _openedAt[i]);
                float t = openingSpringRate * age;
                // Exact critically damped spring response: bounded, independent of frame rate.
                float remaining = t < 12f ? (1f + t) * Mathf.Exp(-t) : 0f;
                _displayPositions[i] = _openPositions[i] - _openingOffsets[i] * remaining;
                moving |= remaining > 0f && _openingOffsets[i].sqrMagnitude > 1e-14f;
            }
            _runtimeMesh.vertices = _displayPositions;
            _runtimeMesh.RecalculateBounds();
            if (!moving) { FinishOpening(); }
        }

        private MeshIncision.Options CutOptions()
        {
            MeshIncision.Options options = MeshIncision.Options.Default;
            options.OpeningWidth = openingWidth;
            options.MaximumDepth = maximumDepth;
            options.InfluenceRadius = Mathf.Max(minimumPathSpacing * 3f, openingWidth * 2f);
            options.CuttableSubmeshCount = _surfaceSubmeshCount;
            return options;
        }

        private void RebuildPreview()
        {
            if (_strokeBaseMesh == null || _path.Count < 2) { return; }
            // Always cut the snapshot, not the preceding preview: growing a stroke must not
            // accumulate duplicate cuts, vertices or a new pointed cap at every sample.
            Mesh candidate = Instantiate(_strokeBaseMesh);
            MeshIncision.Result result = MeshIncision.Cut(candidate, _path, CutOptions());
            _lastPreview = result;
            if (!result.Success) { ReleaseMesh(candidate); return; }
            Mesh previous = _runtimeMesh;
            _runtimeMesh = candidate;
            _runtimeMesh.MarkDynamic();
            _filter.sharedMesh = candidate;
            EnsureInteriorMaterial(result.InteriorSubmesh);
            ReleaseMesh(previous);
            PrepareOpening();
            _lastPreview = result;
            _previewDirty = false;
            PreviewRevision++;
        }

        private void PrepareOpening()
        {
            _openPositions = _runtimeMesh.vertices;
            _displayPositions = new Vector3[_openPositions.Length];
            _openingOffsets = new Vector3[_openPositions.Length];
            _openedAt = new float[_openPositions.Length];
            float radius = Mathf.Max(minimumPathSpacing * 3f, openingWidth * 2f);
            for (int v = 0; v < _openPositions.Length; v++)
            {
                float best = radius * radius;
                int segment = -1;
                float fraction = 0f;
                for (int p = 0; p < _path.Count - 1; p++)
                {
                    Vector3 edge = _path[p + 1].Position - _path[p].Position;
                    float t = edge.sqrMagnitude > 1e-12f
                        ? Mathf.Clamp01(Vector3.Dot(_openPositions[v] - _path[p].Position, edge) / edge.sqrMagnitude) : 0f;
                    float squared = (_openPositions[v] - (_path[p].Position + edge * t)).sqrMagnitude;
                    if (squared >= best) { continue; }
                    best = squared; segment = p; fraction = t;
                }
                if (segment < 0) { continue; }
                Vector3 normal = Vector3.Slerp(_path[segment].Normal, _path[segment + 1].Normal, fraction);
                Vector3 across = Vector3.Cross(normal,
                    _path[segment + 1].Position - _path[segment].Position).normalized;
                Vector3 centre = Vector3.Lerp(_path[segment].Position, _path[segment + 1].Position, fraction);
                float side = Vector3.Dot(_openPositions[v] - centre, across);
                float weight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Sqrt(best) / radius);
                _openingOffsets[v] = across * (side * weight * 0.65f);
                _openedAt[v] = Mathf.Lerp(_sampleTimes[segment], _sampleTimes[segment + 1], fraction);
            }
        }

        private void FinishOpening()
        {
            if (_openPositions != null && _runtimeMesh != null)
            {
                _runtimeMesh.vertices = _openPositions;
                _runtimeMesh.RecalculateBounds();
            }
            _openPositions = null; _displayPositions = null; _openingOffsets = null; _openedAt = null;
        }

        private void CommitContactMesh()
        {
            Mesh previous = _contactMesh;
            _contactMesh = Instantiate(_runtimeMesh);
            if (_openPositions != null) { _contactMesh.vertices = _openPositions; }
            _collider.sharedMesh = null;
            _collider.sharedMesh = _contactMesh;
            ReleaseMesh(previous);
        }

        // Collider cooking is deliberately deferred until manipulation release/settling.
        public void RefreshDeformedCollider()
        {
            if (!IsRecording && !IsOpening && _runtimeMesh != null) { CommitContactMesh(); }
        }

        private void RecordTopology(MeshIncision.Result result)
        {
            if (result.LipPairs != null) { _lipPairs.AddRange(result.LipPairs); }
            if (result.InteriorBindings != null) { _interiorBindings.AddRange(result.InteriorBindings); }
            TopologyRevision++;
        }

        /// <summary>The shared commit API used by both VR input and the Editor probe.</summary>
        public MeshIncision.Result ApplyIncision(IReadOnlyList<MeshIncision.PathPoint> localPath)
        {
            EnsureRuntimeMesh();
            if (IsRecording)
            {
                return new MeshIncision.Result(false, "Encerre a incisão ativa primeiro.", 0, 0, 0, 0, 0d, -1);
            }
            if (_runtimeMesh == null)
            {
                return new MeshIncision.Result(false, "MeshFilter sem mesh.", 0, 0, 0, 0, 0d, -1);
            }

            if (localPath != null)
            {
                for (int i = 0; i < localPath.Count; i++)
                {
                    if (!InCutRegion(localPath[i].Position, localPath[i].Normal))
                    {
                        return new MeshIncision.Result(false, "Trajetória fora da região de corte do tórax.",
                            localPath.Count, 0, _runtimeMesh.vertexCount, _runtimeMesh.vertexCount, 0d, -1);
                    }
                }
            }
            BeforeTopologyChange?.Invoke(this);
            FinishOpening();
            MeshIncision.Result result = MeshIncision.Cut(_runtimeMesh, localPath, CutOptions());

            if (result.Success)
            {
                RecordTopology(result);
                EnsureInteriorMaterial(result.InteriorSubmesh);
                // Deliberately once per completed path: assigning sharedMesh cooks the collider.
                CommitContactMesh();
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (logMetrics)
            {
                Debug.Log($"[CuttableTissue] pontos={result.PathPoints} " +
                    $"triângulos={result.AffectedTriangles} vértices={result.VerticesBefore}" +
                    $"->{result.VerticesAfter} rebuild={result.RebuildMilliseconds:F2}ms " +
                    $"ok={result.Success}", this);
            }
#endif
            return result;
        }

        public void ResetTissue()
        {
            _lipPairs.Clear();
            _interiorBindings.Clear();
            TopologyRevision++;
            IsRecording = false;
            _path.Clear();
            _sampleTimes.Clear();
            _previewDirty = false;
            FinishOpening();
            ReleaseMesh(_strokeBaseMesh); _strokeBaseMesh = null;
            if (_sourceMesh == null) { return; }
            if (_runtimeMesh != null)
            {
                if (Application.isPlaying) { Destroy(_runtimeMesh); }
                else { DestroyImmediate(_runtimeMesh); }
            }
            _runtimeMesh = Instantiate(_sourceMesh);
            _runtimeMesh.name = _sourceMesh.name + " (Cuttable Runtime)";
            _runtimeMesh.MarkDynamic();
            _filter.sharedMesh = _runtimeMesh;
            CommitContactMesh();
            if (_sourceMaterials != null) { _renderer.sharedMaterials = _sourceMaterials; }
        }

        private void EnsureRuntimeMesh()
        {
            if (_runtimeMesh != null) { return; }
            _filter = GetComponent<MeshFilter>();
            _renderer = GetComponent<MeshRenderer>();
            _collider = GetComponent<MeshCollider>();
            if (_filter == null || _filter.sharedMesh == null) { return; }

            _sourceMesh = _filter.sharedMesh;
            _sourceMaterials = _renderer.sharedMaterials;
            _surfaceSubmeshCount = Mathf.Max(1, _sourceMesh.subMeshCount);
            _runtimeMesh = Instantiate(_sourceMesh);
            _runtimeMesh.name = _sourceMesh.name + " (Cuttable Runtime)";
            _runtimeMesh.MarkDynamic();
            _filter.sharedMesh = _runtimeMesh;
            CommitContactMesh();
        }

        private void EnsureInteriorMaterial(int submesh)
        {
            Material[] current = _renderer.sharedMaterials;
            if (current.Length > submesh && current[submesh] != null) { return; }

            int count = Mathf.Max(submesh + 1, current.Length);
            Material[] expanded = new Material[count];
            for (int i = 0; i < current.Length; i++) { expanded[i] = current[i]; }
            Material fallback = current.Length > 0 ? current[current.Length - 1] : null;
            for (int i = current.Length; i < expanded.Length; i++) { expanded[i] = fallback; }
            expanded[submesh] = interiorMaterial != null ? interiorMaterial : fallback;
            _renderer.sharedMaterials = expanded;
        }
    }
}
