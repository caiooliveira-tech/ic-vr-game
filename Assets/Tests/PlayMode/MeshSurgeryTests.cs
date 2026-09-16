using NUnit.Framework;
using UnityEngine;
using VRSurgery.MeshOps;
using VRSurgery.Ports;

namespace VRSurgery.Tests
{
    /// <summary>
    /// Proof that the wall is actually cut, not merely painted.
    ///
    /// The measure is the Euler characteristic, V - E + F. A patch is a disc and reads 1; every
    /// hole punched through it drops it to 0, -1, -2. No decal, shader, normal map or pre-authored
    /// wound prop can move that number — only removing triangles and creating new edges can. That
    /// is what makes it the right assertion for "the topology changed".
    /// </summary>
    public class MeshSurgeryTests
    {
        private GameObject _host;
        private AbdominalWallPatch _wall;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("Wall");
            _host.AddComponent<MeshFilter>();
            _host.AddComponent<MeshRenderer>();
            _wall = _host.AddComponent<AbdominalWallPatch>();
            _wall.Configure(0.055f, 28, 0.022f);
            _wall.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) { Object.DestroyImmediate(_host); }
        }

        private Mesh Live => _host.GetComponent<MeshFilter>().sharedMesh;

        [Test]
        public void AnUncutPatchIsADisc()
        {
            Assert.AreEqual(1, MeshSurgery.EulerCharacteristic(Live),
                "A flat grid with one outer border is a disc: V - E + F = 1.");
            Assert.AreEqual(0, _wall.HoleCount);
        }

        [Test]
        public void PiercingChangesTheTopologyNotJustTheShading()
        {
            int before = MeshSurgery.EulerCharacteristic(Live);
            int trianglesBefore = Live.triangles.Length / 3;
            int verticesBefore = Live.vertexCount;

            Assert.IsTrue(_wall.Pierce(_host.transform.position, 0.0055f),
                "A cut at the centre of the patch must succeed.");

            Assert.AreEqual(before - 1, MeshSurgery.EulerCharacteristic(Live),
                "One opening through a surface drops its Euler characteristic by exactly one. " +
                "If this still read 1 the mesh would be unchanged and the wound a decal.");

            Assert.AreNotEqual(trianglesBefore, Live.triangles.Length / 3,
                "Triangles have to have been removed and others created.");
            Assert.Greater(Live.vertexCount, verticesBefore,
                "The rim and the tract are new geometry.");
            Assert.AreEqual(1, _wall.HoleCount);
        }

        [Test]
        public void EachFurtherPortOpensTheSurfaceAgain()
        {
            // Four ports, as the procedure places them, well clear of each other.
            Vector3 origin = _host.transform.position;
            Vector3[] sites =
            {
                origin + new Vector3(-0.028f, 0f, -0.028f),
                origin + new Vector3(0.028f, 0f, -0.028f),
                origin + new Vector3(-0.028f, 0f, 0.028f),
                origin + new Vector3(0.028f, 0f, 0.028f),
            };

            for (int i = 0; i < sites.Length; i++)
            {
                Assert.IsTrue(_wall.Pierce(sites[i], 0.0055f), $"cut {i} should succeed");
                Assert.AreEqual(1 - (i + 1), MeshSurgery.EulerCharacteristic(Live),
                    $"After {i + 1} openings the characteristic should read {1 - (i + 1)}.");
            }

            Assert.AreEqual(4, _wall.HoleCount);
        }

        [Test]
        public void EveryOpeningAddsABorderToTheSurface()
        {
            int bordersBefore = MeshSurgery.BoundaryEdgeCount(Live);
            _wall.Pierce(_host.transform.position, 0.0055f);

            Assert.Greater(MeshSurgery.BoundaryEdgeCount(Live), bordersBefore,
                "The tract's far end is an open border that did not exist before the cut.");
        }

        [Test]
        public void TheOpeningIsRoundRegardlessOfHowTheGridFell()
        {
            const float Radius = 0.0055f;
            MeshSurgery.HoleResult result = MeshSurgery.OpenCircularHole(
                Live, Vector3.zero, Vector3.up, Radius, 0.02f);

            Assert.IsTrue(result.Success, result.Failure);

            // Every rim vertex sits on the circle, not on whatever grid line was nearby.
            Vector3[] vertices = Live.vertices;
            foreach (int index in result.RimVertices)
            {
                Vector3 v = vertices[index];
                float r = new Vector2(v.x, v.z).magnitude;
                Assert.AreEqual(Radius, r, 1e-4f,
                    "The rim is generated at the exact radius, so an 11 mm cannula leaves an " +
                    "11 mm hole and not a staircase of grid cells.");
            }
        }

        [Test]
        public void TheTractHasDepthSoTheOpeningIsAChannelNotACutout()
        {
            _wall.Pierce(_host.transform.position, 0.0055f);

            float lowest = 0f;
            foreach (Vector3 v in Live.vertices)
            {
                if (v.y < lowest) { lowest = v.y; }
            }

            Assert.Less(lowest, -0.01f,
                "Geometry has to extend behind the surface: a port is a channel through the wall, " +
                "and a flat hole would show the inside of the patient through a paper edge.");
        }

        [Test]
        public void ACutOffThePatchIsRefusedRatherThanStitchedIntoNonsense()
        {
            Vector3 wayOff = _host.transform.position + new Vector3(0.5f, 0f, 0.5f);
            Assert.IsFalse(_wall.Pierce(wayOff, 0.0055f));
            Assert.AreEqual(1, MeshSurgery.EulerCharacteristic(Live), "The patch is untouched.");
        }

        [Test]
        public void RebuildingRestoresAnUnbrokenWallForTheNextVisitor()
        {
            _wall.Pierce(_host.transform.position, 0.0055f);
            Assert.AreEqual(0, MeshSurgery.EulerCharacteristic(Live));

            _wall.Rebuild();

            Assert.AreEqual(1, MeshSurgery.EulerCharacteristic(
                _host.GetComponent<MeshFilter>().sharedMesh),
                "Between visitors the wall has to come back whole.");
            Assert.AreEqual(0, _wall.HoleCount);
        }
    }
}
