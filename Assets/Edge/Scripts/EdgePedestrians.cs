using UnityEngine;
using UnityEngine.Rendering;

namespace Edge
{
    // Bidirectional footpath traffic: walkers (~1.4 m/s), runners (~3 m/s) and an occasional
    // cyclist (5-6 m/s). Agents follow the path centreline polyline (baked by the builder, because
    // PathX lives in editor code) with a keep-left offset for their direction of travel (Australian
    // convention), slow/overtake on the right, and respawn at the far end when they leave the path.
    //
    // Gameplay safety: agents have NO colliders or rigidbodies (the bat can never hit or be pushed
    // by them), cast no light and are not referenced by LampZone/DoseModel, so the dose model is
    // unaffected. PERF: Animator (CullCompletely) / legacy Animation culled when off-screen;
    // shadows only within shadowDistance, also for the seated figures.
    public class EdgePedestrians : MonoBehaviour
    {
        public Vector3[] path;            // centreline points on the path surface, ordered -z -> +z
        public Transform[] agents;        // roots (one per agent), children carry the visuals
        public float[] speeds;            // m/s
        public int[] directions;          // +1 = towards +z, -1 = towards -z
        public string[] clips;            // Animator state (Humanoid controller) or legacy clip to loop
        public Transform[] seated;        // static figures (people on benches): only shadow-distance managed
        public float[] clipNaturalSpeed;  // m/s the clip was authored at (for foot-sync)
        public Camera[] cameras;
        public float keepLeft = 0.45f;    // m left of the centreline
        public float shadowDistance = 20f;
        public float overtakeGap = 3f;
        [Tooltip("Play mode: measure each walker/runner's mean body facing over ~2 s of visible animation, log it, and yaw the body onto the path tangent if it is off by more than 4 deg.")]
        public bool autoCorrectFacing = true;

        float[] _arc;          // cumulative length at each path point
        float[] _s;            // agent arc position
        float[] _lat;          // current lateral offset (smoothed)
        Renderer[][] _renderers;
        bool[] _shadowOn;
        Renderer[][] _seatRenderers;
        bool[] _seatShadow;
        float _len;
        Vector3[] _faceSum; int[] _faceN; bool[] _faceDone;
        Transform[] _l, _r, _ls, _rs;

        void Start()
        {
            if (path == null || path.Length < 2 || agents == null) { enabled = false; return; }
            _arc = new float[path.Length];
            for (int i = 1; i < path.Length; i++) _arc[i] = _arc[i - 1] + Vector3.Distance(path[i - 1], path[i]);
            _len = _arc[path.Length - 1];
            int n = agents.Length;
            _s = new float[n]; _lat = new float[n]; _renderers = new Renderer[n][]; _shadowOn = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (!agents[i]) continue;
                _s[i] = Random.Range(0.05f, 0.95f) * _len; // spread out at start
                _lat[i] = keepLeft;
                _renderers[i] = agents[i].GetComponentsInChildren<Renderer>(true);
                _shadowOn[i] = true;
                float rate = clipNaturalSpeed != null && i < clipNaturalSpeed.Length && clipNaturalSpeed[i] > 0f ? speeds[i] / clipNaturalSpeed[i] : 1f;
                var animator = agents[i].GetComponentInChildren<Animator>();
                if (animator && animator.runtimeAnimatorController && clips != null && i < clips.Length)
                {
                    animator.applyRootMotion = false;
                    animator.cullingMode = AnimatorCullingMode.CullCompletely;
                    animator.speed = rate;
                    animator.Play(clips[i], 0, Random.value);
                    continue;
                }
                var anim = agents[i].GetComponentInChildren<Animation>();
                if (anim && clips != null && i < clips.Length && anim[clips[i]] != null)
                {
                    anim.cullingType = AnimationCullingType.BasedOnRenderers;
                    var st = anim[clips[i]];
                    st.wrapMode = WrapMode.Loop;
                    st.speed = rate;
                    st.time = Random.Range(0f, st.length);
                    anim.Play(clips[i]);
                }
            }
            if (seated != null)
            {
                _seatRenderers = new Renderer[seated.Length][]; _seatShadow = new bool[seated.Length];
                for (int k = 0; k < seated.Length; k++)
                {
                    if (!seated[k]) continue;
                    _seatRenderers[k] = seated[k].GetComponentsInChildren<Renderer>(true); _seatShadow[k] = true;
                    var sa = seated[k].GetComponentInChildren<Animator>();
                    if (sa && sa.enabled && sa.runtimeAnimatorController) { sa.applyRootMotion = false; sa.cullingMode = AnimatorCullingMode.CullCompletely; sa.Play("Sit", 0, Random.value); }
                }
            }
            _faceSum = new Vector3[n]; _faceN = new int[n]; _faceDone = new bool[n];
            _l = new Transform[n]; _r = new Transform[n]; _ls = new Transform[n]; _rs = new Transform[n];
            for (int i = 0; i < n; i++)
            {
                var an = agents[i] ? agents[i].GetComponentInChildren<Animator>() : null;
                bool cyc = agents[i] && agents[i].name.StartsWith("Cyclist_");
                if (!an || !an.isHuman || cyc) { _faceDone[i] = true; continue; }
                _l[i] = an.GetBoneTransform(HumanBodyBones.LeftUpperLeg); _r[i] = an.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                _ls[i] = an.GetBoneTransform(HumanBodyBones.LeftUpperArm); _rs[i] = an.GetBoneTransform(HumanBodyBones.RightUpperArm);
                if (!_l[i] || !_r[i] || !_ls[i] || !_rs[i]) _faceDone[i] = true;
            }
            Debug.Log($"[EDGE] EdgePedestrians: {n} agents on a {_len:F0} m path");
        }

        void Sample(float s, out Vector3 pos, out Vector3 tangent)
        {
            s = Mathf.Clamp(s, 0f, _len);
            int lo = 0, hi = _arc.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (_arc[mid] <= s) lo = mid; else hi = mid; }
            float seg = Mathf.Max(1e-4f, _arc[hi] - _arc[lo]);
            pos = Vector3.Lerp(path[lo], path[hi], (s - _arc[lo]) / seg);
            tangent = path[hi] - path[lo]; tangent.y = 0f; tangent.Normalize();
        }

        // After animation: accumulate the body's facing (agent space) while visible, then log/correct once.
        void LateUpdate()
        {
            if (_faceDone == null) return;
            for (int i = 0; i < agents.Length; i++)
            {
                if (_faceDone[i] || !agents[i]) continue;
                bool vis = false;
                foreach (var r in _renderers[i]) if (r && r.isVisible) { vis = true; break; }
                if (!vis) continue;   // culled Animator = bind pose, would bias the measurement
                var a = agents[i];
                Vector3 right = (_r[i].position - _l[i].position) + (_rs[i].position - _ls[i].position);
                Vector3 f = a.InverseTransformDirection(Vector3.Cross(right, a.up)); f.y = 0f;
                if (f.sqrMagnitude > 1e-8f) { _faceSum[i] += f.normalized; _faceN[i]++; }
                if (_faceN[i] < 120) continue;
                _faceDone[i] = true;
                float err = Vector3.SignedAngle(Vector3.forward, _faceSum[i], Vector3.up);
                bool fix = autoCorrectFacing && Mathf.Abs(err) > 4f;
                Debug.Log($"[EDGE] facing {a.name} ({(clips != null && i < clips.Length ? clips[i] : "?")}): body {err:F1} deg off travel{(fix ? " -> corrected at runtime (run EDGE/Refresh Pedestrian Rigs to bake)" : "")}");
                if (fix)
                {
                    var an = a.GetComponentInChildren<Animator>();
                    if (an) an.transform.RotateAround(a.position, a.up, -err);
                }
            }
        }

        Camera ActiveCamera()
        {
            if (cameras != null) foreach (var c in cameras) if (c && c.isActiveAndEnabled) return c;
            return Camera.main;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            var cam = ActiveCamera();
            Vector3 cp = cam ? cam.transform.position : Vector3.zero;
            for (int i = 0; i < agents.Length; i++)
            {
                var a = agents[i];
                if (!a) continue;
                int dir = directions[i];
                // Overtaking: a slower agent ahead in the same direction -> move out towards the centre.
                float targetLat = keepLeft;
                for (int j = 0; j < agents.Length; j++)
                {
                    if (j == i || !agents[j] || directions[j] != dir || speeds[j] >= speeds[i]) continue;
                    float gap = (_s[j] - _s[i]) * dir;
                    if (gap > 0f && gap < overtakeGap + speeds[i]) { targetLat = keepLeft - 0.6f; break; }
                }
                _lat[i] = Mathf.MoveTowards(_lat[i], targetLat, 0.8f * dt);
                _s[i] += speeds[i] * dir * dt;
                if (_s[i] > _len || _s[i] < 0f) _s[i] = dir > 0 ? 0f : _len; // respawn at the far end

                Sample(_s[i], out var p, out var t);
                Vector3 fwd = t * dir;
                Vector3 left = new Vector3(-fwd.z, 0f, fwd.x);
                a.SetPositionAndRotation(p + left * _lat[i], Quaternion.LookRotation(fwd, Vector3.up));

                bool near = (a.position - cp).sqrMagnitude < shadowDistance * shadowDistance;
                if (near != _shadowOn[i])
                {
                    _shadowOn[i] = near;
                    foreach (var r in _renderers[i]) if (r) r.shadowCastingMode = near ? ShadowCastingMode.On : ShadowCastingMode.Off;
                }
            }
            if (_seatRenderers != null)
                for (int k = 0; k < seated.Length; k++)
                {
                    if (!seated[k] || _seatRenderers[k] == null) continue;
                    bool near = (seated[k].position - cp).sqrMagnitude < shadowDistance * shadowDistance;
                    if (near == _seatShadow[k]) continue;
                    _seatShadow[k] = near;
                    foreach (var r in _seatRenderers[k]) if (r) r.shadowCastingMode = near ? ShadowCastingMode.On : ShadowCastingMode.Off;
                }
        }
    }
}