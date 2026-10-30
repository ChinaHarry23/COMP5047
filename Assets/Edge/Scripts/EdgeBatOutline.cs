using System.Collections.Generic;
using UnityEngine;

namespace Edge
{
    // "You are the bat": a visible self in both cameras.
    //  - First person: two dark wing silhouettes with a pale rim frame the bottom of the view and
    //    flap gently, so the visitor always sees bat wings, not a floating camera.
    //  - Third person: an inverted-hull rim (a slightly inflated copy of each body part with its
    //    triangles reversed) traces the bat's outline, so it reads against the dark forest.
    // Both are unlit, cast no shadows and never touch the light/dose model. Added automatically.
    public class EdgeBatOutline : MonoBehaviour
    {
        public CameraToggle cams;
        public Color rimColor = new Color(0.62f, 0.82f, 1f);
        [Tooltip("Emissive multiplier for the rim, so it shows at night exposure")]
        public float rimEmissive = 3f;
        public Color fillColor = new Color(0.015f, 0.015f, 0.02f);

        [Header("First person wings")]
        public bool firstPersonWings = true;
        [Tooltip("Flap rate (Hz) and amplitude (degrees) of the first-person wings")]
        public float flapHz = 2.2f;
        public float flapDegrees = 5f;
        [Tooltip("Rim thickness as a fraction of the wing size")]
        public float wingRim = 0.02f;
        [Tooltip("Wing size (1 = shoulder to tip 0.52 m at 0.3 m from the eye)")]
        public float wingScale = 0.6f;

        [Header("Third person outline")]
        public bool thirdPersonOutline = true;
        [Tooltip("Outline width in metres (the bat is about 0.3 m across)")]
        public float outlineWidth = 0.006f;

        Material _rim, _fill;
        Transform _wingL, _wingR;
        readonly List<Renderer> _hulls = new List<Renderer>();
        readonly List<Mesh> _meshes = new List<Mesh>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (FindAnyObjectByType<EdgeBatOutline>(FindObjectsInactive.Include)) return;
            var cams = FindAnyObjectByType<CameraToggle>(FindObjectsInactive.Include);
            if (!cams || !cams.bat) return;
            cams.bat.gameObject.AddComponent<EdgeBatOutline>().cams = cams;
        }

        void Start()
        {
            if (!cams) cams = FindAnyObjectByType<CameraToggle>(FindObjectsInactive.Include);
            _rim = Unlit("EdgeBatOutline Rim (runtime)", rimColor, rimColor * rimEmissive);
            _fill = Unlit("EdgeBatOutline Fill (runtime)", fillColor, Color.black);
            if (thirdPersonOutline) BuildHulls();
            if (firstPersonWings && cams && cams.fpv) BuildWings(cams.fpv.transform);
        }

        void LateUpdate()
        {
            // the hull would surround the first-person camera, which sits inside the head
            bool third = cams && cams.ThirdPerson;
            foreach (var r in _hulls) if (r) r.enabled = third && thirdPersonOutline;
            if (_wingL)
            {
                float a = Mathf.Sin(Time.time * flapHz * 2f * Mathf.PI) * flapDegrees;
                _wingL.localRotation = Quaternion.Euler(0f, 0f, -a);
                _wingR.localRotation = Quaternion.Euler(0f, 0f, a);
            }
        }

        void OnDestroy()
        {
            foreach (var m in _meshes) if (m) Destroy(m);
            if (_rim) Destroy(_rim);
            if (_fill) Destroy(_fill);
        }

        static Material Unlit(string name, Color c, Color emissive)
        {
            var sh = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color");
            var m = new Material(sh) { name = name };
            m.SetColor("_UnlitColor", c);
            m.SetColor("_Color", c);
            m.SetColor("_EmissiveColor", emissive);
            return m;
        }

        Renderer AddRenderer(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _meshes.Add(mesh);
            return r;
        }

        // ---- third person: inverted hull ---------------------------------------------------------
        void BuildHulls()
        {
            var fpv = cams ? cams.fpv : null;
            foreach (var mf in GetComponentsInChildren<MeshFilter>(true))
            {
                var src = mf.sharedMesh;
                if (!src || !src.isReadable || (fpv && mf.transform.IsChildOf(fpv.transform))) continue;
                var hull = Hull(src, outlineWidth / Mathf.Max(1e-4f, mf.transform.lossyScale.magnitude / 1.732f));
                _hulls.Add(AddRenderer(mf.transform, mf.name + " Outline", hull, _rim));
            }
        }

        // Each vertex moves out along its normal and away from the part's centre (so flat parts like
        // the wing membrane also grow past their edges); reversed winding leaves only the rim visible.
        static Mesh Hull(Mesh src, float w)
        {
            var v = src.vertices;
            var n = src.normals;
            Vector3 c = src.bounds.center;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 radial = v[i] - c;
                radial = radial.sqrMagnitude > 1e-8f ? radial.normalized : Vector3.zero;
                Vector3 nn = i < n.Length ? n[i] : Vector3.zero;
                v[i] += (nn + radial).normalized * w;
            }
            var m = new Mesh { name = src.name + " (outline)" };
            m.indexFormat = src.indexFormat;
            m.vertices = v;
            m.subMeshCount = 1;
            var tris = new List<int>();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var t = src.GetTriangles(s);
                for (int i = 0; i + 2 < t.Length; i += 3) { tris.Add(t[i]); tris.Add(t[i + 2]); tris.Add(t[i + 1]); }
            }
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        // ---- first person: wing silhouettes ------------------------------------------------------
        void BuildWings(Transform cam)
        {
            _wingL = Wing(cam, -1f);
            _wingR = Wing(cam, 1f);
        }

        // Camera space (camera looks down +z). The shoulder sits just below the view centre and the
        // wing sweeps out to the lower corner, with a scalloped trailing edge like the rival bats.
        Transform Wing(Transform cam, float side)
        {
            var pivot = new GameObject(side < 0 ? "FPV Wing L" : "FPV Wing R").transform;
            pivot.SetParent(cam, false);
            pivot.localPosition = new Vector3(side * 0.12f, -0.3f, 0.3f);   // shoulder, low in the view

            // outline in the pivot's x/y plane (x = out along the wing, y = up), z slightly back at the tip
            Vector2[] edge =
            {
                new Vector2(0f, 0f),          // shoulder
                new Vector2(0.22f, 0.07f),    // wrist (leading edge)
                new Vector2(0.52f, 0.02f),    // tip
                new Vector2(0.40f, -0.08f),   // scallop
                new Vector2(0.30f, -0.05f),
                new Vector2(0.20f, -0.13f),   // scallop
                new Vector2(0.10f, -0.09f),
                new Vector2(0.02f, -0.16f),   // hip
            };
            for (int i = 0; i < edge.Length; i++) edge[i] *= wingScale;
            var fill = Fan(edge, side, 0f, 1f);
            var rim = Fan(edge, side, 0.004f, 1f + wingRim);   // a touch bigger and further away
            AddRenderer(pivot, "Fill", fill, _fill);
            AddRenderer(pivot, "Rim", rim, _rim);
            return pivot;
        }

        static Mesh Fan(Vector2[] edge, float side, float back, float scale)
        {
            Vector2 c = Vector2.zero;
            foreach (var p in edge) c += p;
            c /= edge.Length;
            var v = new Vector3[edge.Length + 1];
            v[0] = new Vector3(side * c.x, c.y, back);
            for (int i = 0; i < edge.Length; i++)
            {
                Vector2 p = c + (edge[i] - c) * scale;
                v[i + 1] = new Vector3(side * p.x, p.y, back + 0.06f * edge[i].x);
            }
            var tris = new List<int>();
            for (int i = 0; i < edge.Length; i++)
            {
                int a = i + 1, b = (i + 1) % edge.Length + 1;
                tris.AddRange(new[] { 0, a, b, 0, b, a });   // both faces: the wing reads from either side
            }
            var m = new Mesh { name = "EdgeFpvWing" };
            m.vertices = v;
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}
