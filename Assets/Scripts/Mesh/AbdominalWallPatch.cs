using UnityEngine;
using VRSurgery.MeshOps;

namespace VRSurgery.Ports
{
    /// <summary>
    /// The piece of abdominal wall a port is made through, and the only geometry in the scene
    /// that gets cut.
    ///
    /// It is a separate patch rather than the patient's own body mesh for two reasons. The body
    /// is a single shared imported mesh, and cutting it would edit the asset for every instance
    /// and every scene; and a patch can carry the tessellation cutting needs — a few thousand
    /// triangles across ten centimetres — without paying for that density over an entire body
    /// the Quest also has to draw.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class AbdominalWallPatch : MonoBehaviour
    {
        [Header("Patch")]
        [Tooltip("Half-width of the patch, in metres.")]
        [SerializeField, Min(0.01f)] private float halfSize = 0.055f;

        [Tooltip("Cells per side. Cutting quality is bounded by this: the rim is exact either " +
                 "way, but a coarse patch leaves a wider band of stitched triangles around it.")]
        [SerializeField, Range(8, 64)] private int resolution = 28;

        [Tooltip("How far the tract is pulled in behind the opening, in metres. Roughly the " +
                 "thickness of the abdominal wall it stands for.")]
        [SerializeField, Min(0.002f)] private float tractDepth = 0.022f;

        private MeshFilter _filter;
        private Mesh _mesh;

        /// <summary>How many openings have been cut through this patch.</summary>
        public int HoleCount { get; private set; }

        /// <summary>Euler characteristic of the live mesh. Drops by one per opening.</summary>
        public int Euler => _mesh != null ? MeshSurgery.EulerCharacteristic(_mesh) : 0;

        private void Awake()
        {
            _filter = GetComponent<MeshFilter>();
            Rebuild();
        }

        /// <summary>Lays a fresh, unbroken patch. Called at Awake and between visitors.</summary>
        public void Rebuild()
        {
            if (_filter == null) { _filter = GetComponent<MeshFilter>(); }

            _mesh = BuildGrid(halfSize, resolution);
            _mesh.name = "AbdominalWallPatch";
            _filter.sharedMesh = _mesh;
            HoleCount = 0;
        }

        /// <summary>
        /// Puts a port through the wall at a world point. Returns false when the cut fell off the
        /// patch or would have run into one already there.
        /// </summary>
        public bool Pierce(Vector3 worldPoint, float radius)
        {
            if (_mesh == null) { return false; }

            Vector3 local = transform.InverseTransformPoint(worldPoint);
            local.y = 0f;

            MeshSurgery.HoleResult result = MeshSurgery.OpenCircularHole(
                _mesh, local, Vector3.up, radius, tractDepth);

            if (!result.Success)
            {
                Debug.LogWarning($"[Wall] pierce at {local} rejected — {result.Failure}", this);
                return false;
            }

            HoleCount++;
            _filter.sharedMesh = _mesh;
            return true;
        }

        /// <summary>Flat grid on the XZ plane, +Y outward. Dense enough to cut cleanly.</summary>
        private static Mesh BuildGrid(float half, int cells)
        {
            int side = cells + 1;
            Vector3[] vertices = new Vector3[side * side];
            Vector3[] normals = new Vector3[side * side];
            Vector2[] uv = new Vector2[side * side];
            int[] triangles = new int[cells * cells * 6];

            for (int z = 0; z < side; z++)
            {
                for (int x = 0; x < side; x++)
                {
                    int i = z * side + x;
                    float u = x / (float)cells;
                    float v = z / (float)cells;
                    vertices[i] = new Vector3(Mathf.Lerp(-half, half, u), 0f, Mathf.Lerp(-half, half, v));
                    normals[i] = Vector3.up;
                    uv[i] = new Vector2(u, v);
                }
            }

            int t = 0;
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int i = z * side + x;
                    triangles[t++] = i;
                    triangles[t++] = i + side;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + side;
                    triangles[t++] = i + side + 1;
                }
            }

            Mesh mesh = new Mesh();
            // A 28x28 patch is under the 16-bit limit, but the format is set explicitly because
            // cutting only ever adds vertices and a finer patch would silently overflow.
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        public void Configure(float newHalfSize, int newResolution, float newTractDepth)
        {
            halfSize = newHalfSize;
            resolution = Mathf.Clamp(newResolution, 8, 64);
            tractDepth = Mathf.Max(0.002f, newTractDepth);
        }
    }
}
