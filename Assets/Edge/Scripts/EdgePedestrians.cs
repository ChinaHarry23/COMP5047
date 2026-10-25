using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Edge
{
    // Bidirectional footpath traffic: walkers (~1.4 m/s), runners (~3 m/s) and an occasional
    // cyclist (5-6 m/s). Agents follow the path centreline polyline (baked by the builder, because
    // PathX lives in editor code) with a keep-left offset for their direction of travel (Australian
    // convention), overtake on the right, and respawn at the far end when they leave the path.
    //
    // Locomotion, so they read as people rather than sliding props (smoothLocomotion; off = old motion):
    //  - heading follows a smoothed path tangent and the actual motion, so there is no snapping at
    //    polyline vertices and no crabbing sideways during a lane change;
    //  - an overtake starts only when no oncoming agent is close; otherwise slow to the leader's pace;
    //  - speed eases with an acceleration limit and drifts a little per agent;
    //  - foot sync: the clip's playback rate follows the real ground speed, calibrated from the clip's
    //    own root motion (AnimationClip.averageSpeed). A cruise speed the clip cannot play inside
    //    playbackRateRange is moved to the clip's speed instead, so feet do not skate and a walk never
    //    plays in fast-forward;
    //  - walkers/runners lean forward with speed and acceleration; everyone banks into turns (v*w/g);
    //  - the facing auto-correction blends in instead of snapping.
    // Some walkers carry torches (EdgeBeam): they light the path and now and then sweep up into the
    // trees, which is when the bat gets dazzled (EdgeLightEffects).
    //
    // Gameplay safety: agents have NO colliders or rigidbodies (the bat can never hit or be pushed
    // by them) and are not referenced by LampZone/DoseModel; torches are EdgeBeams, never LampZones,
    // so the dose model is unaffected. PERF: Animator (CullCompletely) / legacy Animation culled when
    // off-screen; shadows only within shadowDistance, also for the seated figures.
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

        [Header("Locomotion")]
        [Tooltip("Off = the original motion (for A/B comparison)")]
        public bool smoothLocomotion = true;
        [Tooltip("Heading smoothing (s)")] public float headingSmoothTime = 0.3f;
        [Tooltip("Lane-change smoothing (s)")] public float laneSmoothTime = 0.7f;
        [Tooltip("Slow speed drift per agent (+/- fraction of cruise)")] [Range(0f, 0.2f)] public float speedDrift = 0.06f;
        [Tooltip("Acceleration limit (m/s^2); runners x1.3, cyclists x0.8")] public float accel = 1.4f;
        public bool yieldToOncoming = true;
        [Tooltip("Extra margin (m) an overtake keeps from oncoming agents, beyond the distance they close during the pass")]
        public float oncomingClearance = 4f;

        [Header("Foot sync")]
        [Tooltip("Measure each clip's root-motion speed at runtime (AnimationClip.averageSpeed)")]
        public bool autoFootSync = true;
        [Tooltip("Allowed playback rate at cruise; outside it the cruise speed follows the clip instead")]
        public Vector2 playbackRateRange = new Vector2(0.8f, 1.25f);

        [Header("Posture")]
        [Tooltip("Extra forward lean (deg) per m/s above a stroll; mocap runs already lean, so keep it small")]
        public float leanPerMps = 1.5f;
        [Tooltip("Lean (deg) per m/s^2 of acceleration")] public float accelLean = 2f;
        public float maxLeanDeg = 8f;
        [Tooltip("Bank into turns; 1 = physical atan(v*w/g)")] [Range(0f, 1.5f)] public float bankScale = 1f;
        public float maxBankDeg = 18f;

        [Header("Torches")]
        [Tooltip("Share of walkers carrying a torch at night")] [Range(0f, 1f)] public float torchShare = 0.3f;
        public float torchCandela = 25f;
        public float torchRange = 16f;
        public float torchSpotAngle = 32f;
        [Tooltip("Seconds between a torch-carrier sweeping the beam up into the trees")]
        public Vector2 torchSweepEvery = new Vector2(6f, 14f);
        [Tooltip("Torches are off while 'Moonlight' is brighter than this (daylight preview)")]
        public float nightLuxMax = 1000f;

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

        // locomotion state
        Animator[] _an;
        AnimationState[] _legacy;
        bool[] _cyc, _measured, _passing;
        float[] _cruise, _v, _vPrev, _latVel, _heading, _headVel, _bank, _natural, _seed, _lean, _faceFix, _faceApplied;
        float[] _measureAt;
        int[] _passee;
        float[] _targetLat;
        bool[] _walking;              // runner boxed in behind a walker, using the walk cycle
        int[] _walkOk;                // 0 unknown, 1 its controller has the walk state, -1 not
        float[] _gaitHold, _gaitAt;
        string _walkState;            // a walker's state name and clip speed, borrowed for the fallback
        float _walkNatural;
        static readonly List<AnimatorClipInfo> ClipInfos = new List<AnimatorClipInfo>(2);
        Transform[] _spine;
        Quaternion[] _spineSet;
        EdgeBeam[] _torch;
        Transform[] _torchHand;
        float[] _sweepAt;
        bool _night = true;

        void Start()
        {
            if (path == null || path.Length < 2 || agents == null) { enabled = false; return; }
            _arc = new float[path.Length];
            for (int i = 1; i < path.Length; i++) _arc[i] = _arc[i - 1] + Vector3.Distance(path[i - 1], path[i]);
            _len = _arc[path.Length - 1];
            int n = agents.Length;
            _s = new float[n]; _lat = new float[n]; _renderers = new Renderer[n][]; _shadowOn = new bool[n];
            _an = new Animator[n]; _legacy = new AnimationState[n];
            _cyc = new bool[n]; _measured = new bool[n]; _passing = new bool[n];
            _cruise = new float[n]; _v = new float[n]; _vPrev = new float[n]; _latVel = new float[n];
            _heading = new float[n]; _headVel = new float[n]; _bank = new float[n]; _natural = new float[n];
            _seed = new float[n]; _lean = new float[n]; _faceFix = new float[n]; _faceApplied = new float[n];
            _measureAt = new float[n]; _passee = new int[n]; _targetLat = new float[n];
            _walking = new bool[n]; _walkOk = new int[n]; _gaitHold = new float[n]; _gaitAt = new float[n]; _spine = new Transform[n]; _spineSet = new Quaternion[n];
            _torch = new EdgeBeam[n]; _torchHand = new Transform[n]; _sweepAt = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (!agents[i]) continue;
                _s[i] = Random.Range(0.05f, 0.95f) * _len; // spread out at start
                _lat[i] = _targetLat[i] = keepLeft;
                _renderers[i] = agents[i].GetComponentsInChildren<Renderer>(true);
                _shadowOn[i] = true;
                _cyc[i] = agents[i].name.StartsWith("Cyclist_");
                _seed[i] = Random.value * 100f;
                _passee[i] = -1;
                _cruise[i] = SpeedOf(i);
                _v[i] = _vPrev[i] = _cruise[i];
                _natural[i] = clipNaturalSpeed != null && i < clipNaturalSpeed.Length && clipNaturalSpeed[i] > 0f ? clipNaturalSpeed[i] : 0f;
                float rate = _natural[i] > 0f ? _cruise[i] / _natural[i] : 1f;
                var animator = agents[i].GetComponentInChildren<Animator>();
                if (animator && animator.runtimeAnimatorController && clips != null && i < clips.Length)
                {
                    animator.applyRootMotion = false;
                    animator.cullingMode = AnimatorCullingMode.CullCompletely;
                    animator.speed = rate;
                    animator.Play(clips[i], 0, Random.value);
                    _an[i] = animator;
                    if (animator.isHuman && !_cyc[i]) _spine[i] = animator.GetBoneTransform(HumanBodyBones.Spine);
                    if (animator.isHuman) _torchHand[i] = animator.GetBoneTransform(HumanBodyBones.RightHand);
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
                    _legacy[i] = st;
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
                if (!an || !an.isHuman || _cyc[i]) { _faceDone[i] = true; continue; }
                _l[i] = an.GetBoneTransform(HumanBodyBones.LeftUpperLeg); _r[i] = an.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                _ls[i] = an.GetBoneTransform(HumanBodyBones.LeftUpperArm); _rs[i] = an.GetBoneTransform(HumanBodyBones.RightUpperArm);
                if (!_l[i] || !_r[i] || !_ls[i] || !_rs[i]) _faceDone[i] = true;
            }
            for (int i = 0; i < n; i++)
            {
                if (!agents[i]) continue;
                Vector3 fwd = Tangent(_s[i]) * directions[i];
                _heading[i] = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                // deterministic torch carriers: walkers only (a torch on a runner just strobes)
                if (torchShare > 0f && !_cyc[i] && SpeedOf(i) < 2.2f && Mathf.Repeat(i * 0.618034f + 0.31f, 1f) < torchShare)
                {
                    _torch[i] = EdgeBeam.Create(agents[i], "Torch", EdgeBeam.Kind.Torch, new Vector3(0.25f, 1.05f, 0.35f),
                        Quaternion.Euler(22f, 0f, 0f), torchCandela, torchSpotAngle, torchRange, new Color(1f, 0.93f, 0.82f));
                    _sweepAt[i] = Time.time + Random.Range(torchSweepEvery.x, torchSweepEvery.y);
                }
            }
            Debug.Log($"[EDGE] EdgePedestrians: {n} agents on a {_len:F0} m path");
        }

        float SpeedOf(int i) => speeds != null && i < speeds.Length ? speeds[i] : 1.4f;
        bool IsRunner(int i) => !_cyc[i] && _cruise[i] > 2.2f;

        void Sample(float s, out Vector3 pos, out Vector3 tangent)
        {
            s = Mathf.Clamp(s, 0f, _len);
            int lo = 0, hi = _arc.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (_arc[mid] <= s) lo = mid; else hi = mid; }
            float seg = Mathf.Max(1e-4f, _arc[hi] - _arc[lo]);
            pos = Vector3.Lerp(path[lo], path[hi], (s - _arc[lo]) / seg);
            tangent = path[hi] - path[lo]; tangent.y = 0f; tangent.Normalize();
        }

        // Central difference over +/-1.5 m: continuous across polyline vertices.
        Vector3 Tangent(float s)
        {
            Sample(s - 1.5f, out var a, out _);
            Sample(s + 1.5f, out var b, out var t);
            Vector3 d = b - a; d.y = 0f;
            return d.sqrMagnitude > 1e-6f ? d.normalized : t;
        }

        /// One line per agent: kind, clip, speeds, playback rate, torch. Used by the edge_status CLI command.
        public string Describe()
        {
            var sb = new StringBuilder();
            if (agents == null) return "no agents";
            for (int i = 0; i < agents.Length; i++)
            {
                if (!agents[i]) continue;
                string kind = _cyc != null ? (_cyc[i] ? "cyclist" : IsRunner(i) ? "runner" : "walker") : "?";
                string clip = clips != null && i < clips.Length ? clips[i] : "-";
                float rate = _an != null && _an[i] ? _an[i].speed : _legacy != null && _legacy[i] != null ? _legacy[i].speed : 1f;
                sb.Append($"{agents[i].name}: {kind} clip '{clip}' cruise {(_cruise != null ? _cruise[i] : SpeedOf(i)):F2} m/s (builder {SpeedOf(i):F2}) ");
                sb.Append($"clip speed {(_natural != null ? _natural[i] : 0f):F2} m/s rate {rate:F2}{(_torch != null && _torch[i] ? " torch" : "")}\n");
            }
            return sb.ToString();
        }

        // Clip speed from root motion, once the Animator has really entered the agent's state (until then it
        // reports the controller's default state). Culled Animators never update, so keep retrying cheaply.
        void MeasureClip(int i)
        {
            if (_measured[i] || _cyc[i] || Time.time < _measureAt[i]) return;
            _measureAt[i] = Time.time + 0.25f;
            AnimationClip clip = null;
            float scale = 1f;
            if (_an[i])
            {
                if (clips == null || i >= clips.Length || !_an[i].GetCurrentAnimatorStateInfo(0).IsName(clips[i])) return;
                _an[i].GetCurrentAnimatorClipInfo(0, ClipInfos);
                if (ClipInfos.Count == 0) return;
                clip = ClipInfos[0].clip;
                scale = (_an[i].isHuman ? _an[i].humanScale : 1f) * _an[i].transform.lossyScale.y;
            }
            else if (_legacy[i] != null) { clip = _legacy[i].clip; scale = agents[i].lossyScale.y; }
            _measured[i] = true;
            // nothing animated, or a figure meant to stand still: leave its speed exactly as built
            if ((!_an[i] && _legacy[i] == null) || _cruise[i] <= 0f) return;
            float builder = _natural[i];
            if (clip)
            {
                Vector3 av = clip.averageSpeed;
                float sp = new Vector2(av.x, av.z).magnitude * scale;
                if (sp > 0.3f && sp < 9f) _natural[i] = sp;
            }
            if (_natural[i] <= 0f) _natural[i] = IsRunner(i) ? 3.2f : 1.35f;   // in-place clip, no builder value: typical gait speeds
            // Keep the cruise speed where the clip looks right; following/slowing may still go below.
            float lo = _natural[i] * playbackRateRange.x, hi = _natural[i] * playbackRateRange.y;
            float before = _cruise[i];
            _cruise[i] = Mathf.Clamp(_cruise[i], lo, hi);
            Debug.Log($"[EDGE] foot-sync {agents[i].name} '{(clip ? clip.name : "?")}': clip {_natural[i]:F2} m/s (builder said {builder:F2}), " +
                      $"cruise {before:F2} -> {_cruise[i]:F2} m/s, rate {_cruise[i] / _natural[i]:F2}");
        }

        // After animation: posture (lean), torch aim, and the facing measurement/correction.
        void LateUpdate()
        {
            if (_faceDone == null) return;
            float dt = Time.deltaTime, t = Time.time;
            for (int i = 0; i < agents.Length; i++)
            {
                var a = agents[i];
                if (!a) continue;

                if (smoothLocomotion && _spine[i] && dt > 0f)
                {
                    // only on frames the Animator wrote the pose (a culled Animator would accumulate the lean)
                    bool fresh = Quaternion.Angle(_spine[i].localRotation, _spineSet[i]) > 0.01f;
                    float acc = (_v[i] - _vPrev[i]) / dt;
                    float want = Mathf.Clamp(Mathf.Max(0f, _v[i] - 1.3f) * leanPerMps + acc * accelLean, -3f, maxLeanDeg);
                    _lean[i] = Mathf.Lerp(_lean[i], want, 1f - Mathf.Exp(-4f * dt));
                    if (fresh)
                    {
                        _spine[i].rotation = Quaternion.AngleAxis(_lean[i], a.right) * _spine[i].rotation;
                        _spineSet[i] = _spine[i].localRotation;
                    }
                }

                if (_torch[i]) AimTorch(i, a, t);

                // blend a facing correction in at 40 deg/s instead of snapping
                if (_faceApplied[i] != _faceFix[i] && _an[i])
                {
                    float step = Mathf.MoveTowards(_faceApplied[i], _faceFix[i], 40f * dt) - _faceApplied[i];
                    _an[i].transform.RotateAround(a.position, a.up, step);
                    _faceApplied[i] += step;
                }

                if (_faceDone[i]) continue;
                bool vis = false;
                foreach (var r in _renderers[i]) if (r && r.isVisible) { vis = true; break; }
                if (!vis) continue;   // culled Animator = bind pose, would bias the measurement
                Vector3 right = (_r[i].position - _l[i].position) + (_rs[i].position - _ls[i].position);
                Vector3 f = a.InverseTransformDirection(Vector3.Cross(right, a.up)); f.y = 0f;
                if (f.sqrMagnitude > 1e-8f) { _faceSum[i] += f.normalized; _faceN[i]++; }
                if (_faceN[i] < 120) continue;
                _faceDone[i] = true;
                float err = Vector3.SignedAngle(Vector3.forward, _faceSum[i], Vector3.up);
                bool fix = autoCorrectFacing && Mathf.Abs(err) > 4f;
                Debug.Log($"[EDGE] facing {a.name} ({(clips != null && i < clips.Length ? clips[i] : "?")}): body {err:F1} deg off travel{(fix ? " -> corrected at runtime (run EDGE/Refresh Pedestrian Rigs to bake)" : "")}");
                if (fix) _faceFix[i] = -err;
            }
        }

        // Torch in the right hand, pointed at the path ahead; every so often swept up into the trees.
        void AimTorch(int i, Transform a, float t)
        {
            var tb = _torch[i];
            bool on = _night;
            if (tb.Light.enabled != on) tb.Light.enabled = on;
            if (!on) return;
            if (_torchHand[i]) tb.transform.position = _torchHand[i].position + a.forward * 0.08f;
            float pitch = 22f + 3f * (Mathf.PerlinNoise(t * 0.8f, _seed[i]) - 0.5f);
            float yaw = 12f * (Mathf.PerlinNoise(_seed[i], t * 0.3f) - 0.5f);
            float since = t - _sweepAt[i];
            if (since >= 0f)
            {
                const float dur = 2.6f;
                float k = Mathf.Sin(Mathf.Clamp01(since / dur) * Mathf.PI);   // up, look around, back down
                pitch = Mathf.Lerp(pitch, -28f, k);
                yaw += 50f * k * Mathf.Sin(since * 2.2f + _seed[i]);
                if (since > dur) _sweepAt[i] = t + Random.Range(torchSweepEvery.x, torchSweepEvery.y);
            }
            tb.transform.rotation = a.rotation * Quaternion.Euler(pitch, yaw, 0f);
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
            _night = EdgeBeam.IsNight(nightLuxMax);
            for (int i = 0; i < agents.Length; i++)
            {
                var a = agents[i];
                if (!a) continue;
                if (smoothLocomotion) Move(i, a, dt);
                else MoveSimple(i, a, dt);

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

        void Move(int i, Transform a, float dt)
        {
            if (autoFootSync) MeasureClip(i);
            int dir = directions[i];
            float t = Time.time;
            float cruise = _cruise[i] * (1f + speedDrift * (2f * Mathf.PerlinNoise(t * 0.07f + _seed[i], _seed[i]) - 1f));
            if (_measured[i] && !_cyc[i] && _natural[i] > 0f) cruise = Mathf.Min(cruise, _natural[i] * playbackRateRange.y);   // drift never outruns the clip
            float want = cruise, targetLat = keepLeft;

            float acc = accel * (_cyc[i] ? 0.8f : IsRunner(i) ? 1.3f : 1f);
            float follow = _cyc[i] ? 2.5f : 1.3f;
            float passLane = keepLeft - 0.3f;   // lateral offsets below this are in the overtaking lane

            // A pass ends once clear of the person being passed, and only where merging back neither
            // lands on someone slower ahead nor cuts off someone faster behind.
            int pe = _passee[i];
            if (_passing[i])
            {
                bool lost = pe < 0 || !agents[pe] || Mathf.Abs(_s[i] - _s[pe]) > 30f;
                if (lost || (_s[i] - _s[pe]) * dir > 1.5f + 0.4f * _v[i])
                {
                    bool ok = true;
                    for (int k = 0; k < agents.Length && ok; k++)
                    {
                        if (k == i || !agents[k] || directions[k] != dir || _lat[k] < passLane) continue;
                        float g = (_s[k] - _s[i]) * dir;
                        if (g > 0f && g < follow + _v[i] * _v[i] / (1.8f * acc) && _v[k] < _v[i] - 0.1f) ok = false;
                        if (g <= 0f && g > -(2f + 3f * Mathf.Max(0f, _v[k] - _v[i])) && k != pe) ok = false;
                    }
                    if (ok || lost) { _passing[i] = false; _passee[i] = -1; }
                }
            }

            // Overtake the nearest slower agent ahead in the keep lane, if the whole pass fits:
            // nobody already in the overtaking lane there, and no oncoming agent within closing range.
            int lead = -1; float gap = float.MaxValue;
            for (int j = 0; j < agents.Length; j++)
            {
                if (j == i || !agents[j] || directions[j] != dir || _lat[j] < passLane) continue;
                float g = (_s[j] - _s[i]) * dir;
                if (g > 0f && g < gap) { gap = g; lead = j; }
            }
            if (!_passing[i] && lead >= 0 && gap < overtakeGap + _v[i] + 2f && _v[lead] < cruise - 0.15f)
            {
                float tPass = (gap + 2f + 0.4f * _v[i]) / Mathf.Max(0.2f, cruise - _v[lead]);
                bool clear = tPass < 10f;
                for (int k = 0; k < agents.Length && clear; k++)
                {
                    if (k == i || !agents[k]) continue;
                    float g = (_s[k] - _s[i]) * dir;
                    if (directions[k] == dir) { if (_lat[k] < passLane && g > -1f && g < gap + 3f) clear = false; }
                    else if (yieldToOncoming && g > -2f && g < (_v[i] + _v[k]) * tPass + oncomingClearance) clear = false;
                }
                if (clear) { _passing[i] = true; _passee[i] = lead; }
            }
            if (_passing[i]) targetLat = keepLeft - 0.6f;
            _targetLat[i] = targetLat;

            // Lane-aware car following: anyone ahead in my lane, in the lane I am moving into, or moving
            // into mine. Safe speed v_lead + sqrt(2 a d) can always stop inside the gap.
            for (int j = 0; j < agents.Length; j++)
            {
                if (j == i || !agents[j] || directions[j] != dir || j == _passee[i]) continue;
                float g = (_s[j] - _s[i]) * dir;
                if (g <= 0f || g > 30f) continue;
                if (Mathf.Abs(_lat[j] - _lat[i]) < 0.5f || Mathf.Abs(_lat[j] - targetLat) < 0.5f || Mathf.Abs(_targetLat[j] - _lat[i]) < 0.5f)
                    want = Mathf.Min(want, _v[j] + Mathf.Sqrt(2f * 0.9f * acc * Mathf.Max(0f, g - follow)));
            }

            _vPrev[i] = _v[i];
            _v[i] = Mathf.MoveTowards(_v[i], want, acc * dt);
            _lat[i] = Mathf.SmoothDamp(_lat[i], targetLat, ref _latVel[i], laneSmoothTime, Mathf.Infinity, dt);
            _s[i] += _v[i] * dir * dt;
            bool wrapped = false;
            if (_s[i] > _len || _s[i] < 0f) { _s[i] = dir > 0 ? 0f : _len; wrapped = true; }   // respawn at the far end

            Sample(_s[i], out var p, out _);
            Vector3 fwd = Tangent(_s[i]) * dir;
            Vector3 left = new Vector3(-fwd.z, 0f, fwd.x);
            Vector3 motion = fwd * Mathf.Max(0.3f, _v[i]) + left * _latVel[i];   // face where they are going
            float wantHeading = Mathf.Atan2(motion.x, motion.z) * Mathf.Rad2Deg;
            if (wrapped)
            {
                _lat[i] = keepLeft; _latVel[i] = 0f; _passing[i] = false; _passee[i] = -1;
                _heading[i] = wantHeading; _headVel[i] = 0f; _bank[i] = 0f;
            }
            else _heading[i] = Mathf.SmoothDampAngle(_heading[i], wantHeading, ref _headVel[i], headingSmoothTime, Mathf.Infinity, dt);
            // lean into the turn: positive yaw rate turns right, so roll right
            float bankWant = Mathf.Clamp(Mathf.Atan(_v[i] * _headVel[i] * Mathf.Deg2Rad / 9.81f) * Mathf.Rad2Deg * bankScale, -maxBankDeg, maxBankDeg);
            _bank[i] = Mathf.Lerp(_bank[i], bankWant, 1f - Mathf.Exp(-8f * dt));
            a.SetPositionAndRotation(p + left * _lat[i], Quaternion.Euler(0f, _heading[i], 0f) * Quaternion.AngleAxis(-_bank[i], Vector3.forward));

            if (!_cyc[i] && _natural[i] > 0f)
            {
                bool walk = GaitWalk(i, dt);
                float rate = walk ? Mathf.Clamp(_v[i] / _walkNatural, 0.45f, 1.45f) : Mathf.Clamp(_v[i] / _natural[i], 0.45f, playbackRateRange.y);
                if (_an[i]) _an[i].speed = rate;
                else if (_legacy[i] != null) _legacy[i].speed = rate;
            }
        }

        // A runner boxed in behind a walker switches to the walk cycle instead of running in slow
        // motion, and back once it can speed up (hysteresis + minimum dwell, crossfaded).
        bool GaitWalk(int i, float dt)
        {
            if (!IsRunner(i) || !_an[i] || !FindWalkState()) return false;
            if (_walkOk[i] == 0) _walkOk[i] = _an[i].HasState(0, Animator.StringToHash(_walkState)) ? 1 : -1;
            if (_walkOk[i] < 0) return false;
            float run01 = _v[i] / _natural[i];
            bool want = _walking[i] ? run01 < 0.62f : run01 < 0.5f;
            if (want == _walking[i]) { _gaitHold[i] = 0f; return _walking[i]; }
            _gaitHold[i] += dt;
            if (_gaitHold[i] > 0.3f && Time.time - _gaitAt[i] > 0.5f)
            {
                _walking[i] = want; _gaitHold[i] = 0f; _gaitAt[i] = Time.time;
                _an[i].CrossFadeInFixedTime(want ? _walkState : clips[i], 0.25f, 0);
            }
            return _walking[i];
        }

        bool FindWalkState()
        {
            if (_walkState != null) return true;
            for (int j = 0; j < agents.Length; j++)
                if (agents[j] && !_cyc[j] && !IsRunner(j) && _an[j] && _measured[j] && _natural[j] > 0f
                    && clips != null && j < clips.Length && !string.IsNullOrEmpty(clips[j]))
                { _walkState = clips[j]; _walkNatural = _natural[j]; return true; }
            return false;   // no measured walker yet; try again later
        }

        // The original motion: segment tangent, lateral slide, constant speed.
        void MoveSimple(int i, Transform a, float dt)
        {
            int dir = directions[i];
            float targetLat = keepLeft;
            for (int j = 0; j < agents.Length; j++)
            {
                if (j == i || !agents[j] || directions[j] != dir || speeds[j] >= speeds[i]) continue;
                float gap = (_s[j] - _s[i]) * dir;
                if (gap > 0f && gap < overtakeGap + speeds[i]) { targetLat = keepLeft - 0.6f; break; }
            }
            _lat[i] = Mathf.MoveTowards(_lat[i], targetLat, 0.8f * dt);
            _vPrev[i] = _v[i] = speeds[i];
            _s[i] += speeds[i] * dir * dt;
            if (_s[i] > _len || _s[i] < 0f) _s[i] = dir > 0 ? 0f : _len; // respawn at the far end

            Sample(_s[i], out var p, out var tg);
            Vector3 fwd = tg * dir;
            Vector3 left = new Vector3(-fwd.z, 0f, fwd.x);
            a.SetPositionAndRotation(p + left * _lat[i], Quaternion.LookRotation(fwd, Vector3.up));
        }
    }
}
