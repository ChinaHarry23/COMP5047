using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Edge
{
    // GPU-instanced grass (Poly Haven grass_medium_01, CC0). No GameObject per clump:
    // instances are bucketed into 10 m cells and drawn with Graphics.RenderMeshInstanced
    // only for visible cells within drawDistance of the active camera. Density falls off
    // between fullDensityDistance and drawDistance by drawing a prefix of each (pre-shuffled)
    // cell list. No shadow casting.
    public class EdgeGrass : MonoBehaviour
    {
        public Mesh[] meshes;
        public Matrix4x4[] meshFix;      // imported node rotation/scale per mesh (base-centred)
        public Material material;
        public Vector4[] posYaw;         // x, y, z, yaw degrees
        public Vector2[] meshScale;      // mesh index, uniform scale
        public Camera[] cameras;         // FPV / TPV, whichever is active
        public float drawDistance = 35f;
        public float fullDensityDistance = 12f;
        public float cellSize = 10f;

        struct Cell { public Bounds bounds; public Matrix4x4[][] perMesh; }
        Cell[] _cells;
        readonly Plane[] _planes = new Plane[6];
        RenderParams _rp;
        public int DrawnLastFrame { get; private set; }

        void Start()
        {
            if (meshes == null || meshes.Length == 0 || material == null || posYaw == null) { enabled = false; return; }
            var map = new Dictionary<Vector2Int, List<int>>();
            for (int i = 0; i < posYaw.Length; i++)
            {
                var k = new Vector2Int(Mathf.FloorToInt(posYaw[i].x / cellSize), Mathf.FloorToInt(posYaw[i].z / cellSize));
                if (!map.TryGetValue(k, out var l)) map[k] = l = new List<int>();
                l.Add(i);
            }
            var cells = new List<Cell>();
            var rng = new System.Random(5047);
            foreach (var kv in map)
            {
                var lists = new List<Matrix4x4>[meshes.Length];
                for (int m = 0; m < meshes.Length; m++) lists[m] = new List<Matrix4x4>();
                Bounds b = new Bounds(new Vector3(posYaw[kv.Value[0]].x, posYaw[kv.Value[0]].y, posYaw[kv.Value[0]].z), Vector3.zero);
                var idx = kv.Value;
                for (int i = idx.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (idx[i], idx[j]) = (idx[j], idx[i]); } // shuffle for falloff
                foreach (int i in idx)
                {
                    var p = posYaw[i]; int mi = Mathf.Clamp((int)meshScale[i].x, 0, meshes.Length - 1);
                    var pos = new Vector3(p.x, p.y, p.z);
                    lists[mi].Add(Matrix4x4.TRS(pos, Quaternion.Euler(0f, p.w, 0f), Vector3.one * meshScale[i].y) * meshFix[mi]);
                    b.Encapsulate(pos);
                }
                b.Expand(new Vector3(1f, 1.5f, 1f));
                var c = new Cell { bounds = b, perMesh = new Matrix4x4[meshes.Length][] };
                for (int m = 0; m < meshes.Length; m++) c.perMesh[m] = lists[m].ToArray();
                cells.Add(c);
            }
            _cells = cells.ToArray();
            _rp = new RenderParams(material) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true, layer = gameObject.layer };
            Debug.Log($"[EDGE] EdgeGrass: {posYaw.Length} instances in {_cells.Length} cells, draw distance {drawDistance} m");
        }

        Camera ActiveCamera()
        {
            if (cameras != null)
                foreach (var c in cameras) if (c && c.isActiveAndEnabled) return c;
            return Camera.main;
        }

        void Update()
        {
            var cam = ActiveCamera();
            if (!cam || _cells == null) return;
            GeometryUtility.CalculateFrustumPlanes(cam, _planes);
            Vector3 cp = cam.transform.position;
            int drawn = 0;
            float range = Mathf.Max(0.01f, drawDistance - fullDensityDistance);
            for (int ci = 0; ci < _cells.Length; ci++)
            {
                ref var c = ref _cells[ci];
                float d = Mathf.Sqrt(c.bounds.SqrDistance(cp));
                if (d > drawDistance || !GeometryUtility.TestPlanesAABB(_planes, c.bounds)) continue;
                float frac = d <= fullDensityDistance ? 1f : 1f - (d - fullDensityDistance) / range;
                _rp.worldBounds = c.bounds;
                for (int m = 0; m < meshes.Length; m++)
                {
                    var arr = c.perMesh[m];
                    int n = Mathf.Min(arr.Length, Mathf.CeilToInt(arr.Length * frac * frac));
                    for (int start = 0; start < n; start += 1023)
                    {
                        int cnt = Mathf.Min(1023, n - start);
                        Graphics.RenderMeshInstanced(_rp, meshes[m], 0, arr, cnt, start);
                        drawn += cnt;
                    }
                }
            }
            DrawnLastFrame = drawn;
        }
    }
}
