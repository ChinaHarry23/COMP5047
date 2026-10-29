using System;
using System.Collections.Generic;
using UnityEngine;
using Edge.UI;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;

namespace Edge.Feeding
{
    // Mini insect-feeding game with two variants (light-tolerant / light-averse bat). The point is the
    // unfairness: the same lamps that pull insects out of the bush feed light-tolerant bats and starve
    // light-averse ones. The tolerant bat just flies into the swarms; the averse bat is hurt by light
    // and has to hunt the scattered leftovers in the dark.
    //  - Moths: pooled structs, HDRP/Lit (pale wings that catch lamplight, unseen in the dark), drawn
    //    with Graphics.RenderMeshInstanced; no GameObjects, no Rigidbodies.
    //  - Lamps pull dark insects into their swarm over the round (vacuum-cleaner effect, preset.lampPull),
    //    and light-tolerant rival bats hawk the swarms (preset.RivalCount), so the dark gets poorer.
    //  - Sound: EdgeFeedingAudio (crunch, streak, feeding buzz). Light that hits the bat (lamps, torches,
    //    bike lights, flashes) is felt through EdgeLightEffects, scaled per bat.
    //  - The end screen compares both bats side by side (moths, time in light, dark moths left, moths
    //    the rivals took at the lamps) with one line from the national guidelines.
    // Lamps are found from LampZone components (fallback: Light components).
    // Keys: Space/Enter start, 1/2 variant, R restart, E echolocation ping, H full debug HUD.
    public class EdgeFeedingGame : MonoBehaviour
    {
        public enum Variant { LightTolerant, LightAverse }
        public enum State { StartScreen, Playing, Ended }

        public Variant variant = Variant.LightAverse;
        public EdgeFeedingPreset tolerantPreset;
        public EdgeFeedingPreset aversePreset;
        public bool showStartScreen = true;

        [Header("Scene refs (auto-found if empty)")]
        public BatFlightController flight;
        public UdpLink link;
        public LampZone[] lamps;
        public EdgeHud exhibitHud;
        public CameraToggle cams;

        [Header("Moth look")]
        [Tooltip("Optional; if empty an HDRP/Lit material is made at runtime")]
        public Material insectMaterial;
        [Tooltip("Pale, dusty wing scales: bright specks in lamplight, unseen in the dark (echolocation finds those)")]
        public Color mothColor = new Color(0.36f, 0.31f, 0.24f);
        [Tooltip("Wingspan (m). Real small moths ~0.02-0.04; larger for gameplay")]
        public float insectSize = 0.07f;
        public float flapHz = 22f;
        [Header("Echolocation highlight")]
        public Color echoColor = new Color(0.55f, 0.8f, 1f);
        public float echoEmissive = 30f;
        [Tooltip("Highlight size relative to the moth at the moment of the ping; shrinks to half as it fades")]
        public float echoScale = 4f;
        public Key echoKey = Key.E;

        [Header("Rival light-tolerant bats")]
        [Tooltip("Wingspan (m); Gould's wattled bat is ~0.3 m")] public float rivalSize = 0.32f;
        public Color rivalColor = new Color(0.05f, 0.04f, 0.035f);
        public float rivalSpeed = 5f;
        public float rivalFlapHz = 9f;

        [Header("Feedback")]
        public bool playSound = true;
        [Tooltip("Optional recorded eat sounds; empty = synthesised crunch variants (EdgeFeedingAudio)")]
        public AudioClip[] eatClips;
        [Range(0f, 1f)] public float eatVolume = 0.55f;
        [Tooltip("Feeding buzz starts when a moth ahead is closer than this (m)")]
        public float approachRadius = 5f;
        [Tooltip("Wing-scale burst and a rising +1 where a moth is eaten")]
        public bool eatBurst = true;
        public Color burstColor = new Color(1f, 0.86f, 0.62f);
        [Tooltip("VIB/LED burst sent to the tag on each eat")] [Range(0f, 1f)] public float eatVibLevel = 0.8f;
        public float eatVibSeconds = 0.12f;
        public Key debugHudKey = Key.H;
        public float hintFadeSeconds = 3f;
        [Tooltip("When a bat is chosen, it waits this long at the start while it introduces itself (0 = off)")]
        public float introSeconds = 3f;
        [Tooltip("Results screen idle this long (s) = the next visitor: back to the start screen, two-bat comparison cleared (0 = never)")]
        public float newVisitAfterSeconds = 45f;

        [Header("Screens")]
        [Tooltip("Bat-themed uGUI screens (EdgeUiView). Off = the plain IMGUI text screens")]
        public bool themedUi = true;

        // Hooks (e.g. EdgeNarrator). Optional; nothing subscribes by default.
        public event Action<int> InsectEaten;
        public event Action<Variant, int, string> RoundEnded;
        public event Action<Variant> RoundStarted;
        /// A chosen bat's round begins with an intro (bat held still, timer and light paused).
        public event Action<Variant> IntroStarted;

        public State CurrentState { get; private set; }
        public int Score { get; private set; }
        public float Hunger01 { get; private set; }
        public float TimeLeft { get; private set; }
        public float Budget01 { get; private set; } = 1f;
        public bool InLight { get; private set; }
        public float LuxAtBat { get; private set; }
        public string EndReason { get; private set; } = "";
        public bool DebugHud { get; private set; }
        /// Seconds this round with light on the bat (lamp, beam or flash).
        public float LightSeconds { get; private set; }
        /// Share of live insects that are circling lamps right now.
        public float LampShare01 { get; private set; }
        /// Moths eaten by the rival bats this round.
        public int RivalScore { get; private set; }
        /// Moths living in the dark right now (alive or about to respawn there).
        public int DarkInsects { get { int n = 0; foreach (var s in _ins) if (s.lamp < 0) n++; return n; } }
        public EdgeFeedingPreset Preset => variant == Variant.LightTolerant ? tolerantPreset : aversePreset;

        struct Insect
        {
            public Vector3 pos, anchor, vel, axis;
            public int lamp;        // -1 = dark
            public float phase, respawnAt, orbitR, orbitW, nextDart;
            public bool alive;
        }

        struct Rival
        {
            public Vector3 pos, vel;
            public int lamp;
            public float ang, radius, height, angW, phase, nextBite;
        }

        struct Pop { public Vector3 pos; public float t; public int seed; }

        struct RoundResult { public bool played; public int moths, darkStart, darkEnd, rivals; public float lightSeconds; public string reason; }

        static readonly Color LegacyMothColor = new Color(0.06f, 0.045f, 0.035f);
        const float PopLife = 0.4f, FloatLife = 0.8f;
        const int RivalFrames = 4;

        Insect[] _ins = new Insect[0];
        Rival[] _rivals = new Rival[0];
        readonly List<Vector3> _lampHeads = new List<Vector3>();
        readonly List<Pop> _pops = new List<Pop>();
        readonly List<Pop> _floaters = new List<Pop>();
        readonly Matrix4x4[] _mats = new Matrix4x4[1023];
        readonly Matrix4x4[] _echoMats = new Matrix4x4[1023];
        readonly Matrix4x4[] _burstMats = new Matrix4x4[256];
        readonly Matrix4x4[][] _rivalMats = new Matrix4x4[RivalFrames][];
        readonly int[] _rivalN = new int[RivalFrames];
        readonly RoundResult[] _results = new RoundResult[2];   // this visit
        readonly int[] _best = new int[2];                      // all day
        Mesh _mesh;
        readonly Mesh[] _batMesh = new Mesh[RivalFrames];
        Material _mat, _echoMat, _burstMat, _rivalMat;
        EdgeFeedingAudio _sfx;
        EdgeLightEffects _fx;
        Vector3 _startPos, _pathMin, _pathMax;
        float _startHeading, _budgetFmm, _vibUntil, _playStart, _lastPing = -100f, _scorePulse = -100f, _endedAt;
        float _introUntil = -1f;
        Variant _roundVariant;
        int _darkAtStart;
        static readonly RaycastHit[] GroundHits = new RaycastHit[16];
        System.Random _rng = new System.Random(5047);

        EdgeLightEffects Fx => _fx ? _fx : (_fx = EdgeLightEffects.Instance);

        // How the street lamps look for the current bat (EdgeFeedingPreset.LampBrightness / LampGlow).
        // Visual only: the lux at the bat comes from EdgeConfig, never from the Light component.
        // The blinding halo is the head's bloom plus the spot light in the volumetric fog, so glow has its own factor.
        struct LampLook
        {
            public Light light; public float intensity;
            public HDAdditionalLightData hd; public float volumetricDimmer;
            public Renderer[] heads; public Color[] emissive;
        }
        static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
        LampLook[] _lampLooks;
        float _lampK = 1f, _lampGlow = 1f;
        MaterialPropertyBlock _lampMpb;

        bool InLightGrace => CurrentState == State.Playing && Time.unscaledTime - _playStart < Preset.lightGraceSeconds;
        public bool InIntro => CurrentState == State.Playing && Time.unscaledTime < _introUntil;

        void Awake()
        {
            if (!tolerantPreset) tolerantPreset = EdgeFeedingPreset.CreateTolerant();
            if (!aversePreset) aversePreset = EdgeFeedingPreset.CreateAverse();
            if (!flight) flight = FindAnyObjectByType<BatFlightController>();
            if (!link && flight) link = flight.link;
            if (lamps == null || lamps.Length == 0) lamps = FindObjectsByType<LampZone>();
            if (!exhibitHud) exhibitHud = FindAnyObjectByType<EdgeHud>();
            if (!cams) cams = FindAnyObjectByType<CameraToggle>();
            // Scenes saved with the old near-black default get the pale moths; a custom colour is kept.
            if (Mathf.Abs(mothColor.r - LegacyMothColor.r) + Mathf.Abs(mothColor.g - LegacyMothColor.g) + Mathf.Abs(mothColor.b - LegacyMothColor.b) < 0.01f)
                mothColor = new Color(0.36f, 0.31f, 0.24f);
        }

        void Start()
        {
            if (flight) { _startPos = flight.transform.position; _startHeading = flight.transform.eulerAngles.y; }
            FindLamps();
            CaptureLampLooks();
            BuildVisuals();
            BuildAudio();
            if (themedUi) _ui = EdgeUiView.Create();
            if (showStartScreen) { CurrentState = State.StartScreen; ApplyFlightMods(false); SpawnAll(); ApplyHud(); }
            else Restart();
        }

        void OnEnable()
        {
            if (flight) flight.Bumped += OnBumped;
            if (_mesh) { ApplyFlightMods(CurrentState == State.Playing); ApplyHud(); }   // re-enabled after Start: take the bat back
            if (_ui) _ui.gameObject.SetActive(true);
        }

        void OnDestroy() { if (_ui) Destroy(_ui.gameObject); }

        void OnBumped(Vector3 point, Vector3 normal, Collider c)
        {
            if (CurrentState != State.Playing) return;
            Hunger01 = Mathf.Max(0f, Hunger01 - Preset.bumpHungerCost);
        }

        void OnDisable()
        {
            if (flight) flight.Bumped -= OnBumped;
            ApplyFlightMods(false, restoreDefaults: true);
            if (flight && flight.dose) flight.dose.ResetVirtual();   // the exhibit resumes without the game's lamp dose
            if (exhibitHud) { exhibitHud.compact = false; exhibitHud.hideBudget = false; }
            if (Fx) Fx.scale = 1f;
            SetLampLook(1f, 1f);
            if (_sfx) _sfx.SetApproach(0f, 0f);
            if (_ui) _ui.gameObject.SetActive(false);
        }

        // ---- setup -------------------------------------------------------------
        void FindLamps()
        {
            _lampHeads.Clear();
            foreach (var l in lamps)
                if (l) _lampHeads.Add(l.measureFrom ? l.measureFrom.position : l.transform.position);
            if (_lampHeads.Count == 0)
                foreach (var li in FindObjectsByType<Light>())
                    if (li.type != LightType.Directional && li.name.IndexOf("lamp", StringComparison.OrdinalIgnoreCase) >= 0)
                        _lampHeads.Add(li.transform.position);
            _pathMin = _pathMax = flight ? flight.transform.position : transform.position;
            foreach (var h in _lampHeads) { _pathMin = Vector3.Min(_pathMin, h); _pathMax = Vector3.Max(_pathMax, h); }
            Debug.Log($"[EDGE] Feeding game: {_lampHeads.Count} lamps found");
        }

        void CaptureLampLooks()
        {
            var looks = new List<LampLook>();
            foreach (var l in lamps)
            {
                if (!l) continue;
                var look = new LampLook { light = l.GetComponentInChildren<Light>(true) };
                if (look.light)
                {
                    look.intensity = look.light.intensity;
                    look.hd = look.light.GetComponent<HDAdditionalLightData>();
                    if (look.hd) look.volumetricDimmer = look.hd.volumetricDimmer;
                }
                var heads = new List<Renderer>();
                var emissive = new List<Color>();
                foreach (var r in l.GetComponentsInChildren<Renderer>(true))
                {
                    var m = r.sharedMaterial;
                    if (!m || !m.HasProperty(EmissiveColorId)) continue;
                    Color e = m.GetColor(EmissiveColorId);
                    if (e.maxColorComponent <= 1f) continue;   // only the glowing lamp head
                    heads.Add(r);
                    emissive.Add(e);
                }
                look.heads = heads.ToArray();
                look.emissive = emissive.ToArray();
                looks.Add(look);
            }
            _lampLooks = looks.ToArray();
        }

        void SetLampLook(float k, float glow)
        {
            if (_lampLooks == null || (Mathf.Approximately(k, _lampK) && Mathf.Approximately(glow, _lampGlow))) return;
            _lampK = k;
            _lampGlow = glow;
            if (_lampMpb == null) _lampMpb = new MaterialPropertyBlock();
            foreach (var look in _lampLooks)
            {
                if (look.light) look.light.intensity = look.intensity * k;
                if (look.hd) look.hd.volumetricDimmer = look.volumetricDimmer * glow;
                for (int i = 0; i < look.heads.Length; i++)
                {
                    var r = look.heads[i];
                    if (!r) continue;
                    if (Mathf.Approximately(k * glow, 1f)) { r.SetPropertyBlock(null); continue; }
                    _lampMpb.Clear();
                    _lampMpb.SetColor(EmissiveColorId, look.emissive[i] * (k * glow));
                    r.SetPropertyBlock(_lampMpb);
                }
            }
        }

        static Material LitMaterial(string name, Color c, float smoothness)
        {
            var sh = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            var m = new Material(sh) { name = name };
            m.SetColor("_BaseColor", c);
            m.SetColor("_Color", c);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_DoubleSidedEnable", 1f);
            m.SetFloat("_CullMode", 0f);
            m.SetFloat("_CullModeForward", 0f);
            m.EnableKeyword("_DOUBLESIDED_ON");
            m.enableInstancing = true;
            return m;
        }

        static Material UnlitMaterial(string name, Color c, Color emissive)
        {
            var ush = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color");
            var m = new Material(ush) { name = name, enableInstancing = true };
            m.SetColor("_UnlitColor", c);
            m.SetColor("_Color", c);
            m.SetColor("_EmissiveColor", emissive);
            return m;
        }

        void BuildVisuals()
        {
            // moth: body sliver + two wing triangles hinged at the body (hinge = local x 0)
            _mesh = new Mesh { name = "EdgeMoth" };
            _mesh.vertices = new[]
            {
                // left wing
                new Vector3(0, 0, 0.25f), new Vector3(0, 0, -0.35f), new Vector3(-0.5f, 0, -0.05f),
                // right wing
                new Vector3(0, 0, 0.25f), new Vector3(0.5f, 0, -0.05f), new Vector3(0, 0, -0.35f),
                // body
                new Vector3(0, 0.04f, 0.3f), new Vector3(0, 0.04f, -0.4f), new Vector3(0, -0.06f, 0f),
            };
            _mesh.triangles = new[] { 0, 1, 2, 0, 2, 1, 3, 4, 5, 3, 5, 4, 6, 7, 8, 6, 8, 7 };
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            _mat = insectMaterial ? insectMaterial : LitMaterial("EdgeMoth (runtime)", mothColor, 0.2f);
            _mat.enableInstancing = true;
            _echoMat = UnlitMaterial("EdgeMoth Echo (runtime)", echoColor * 0.4f, echoColor * echoEmissive);
            _burstMat = UnlitMaterial("EdgeMoth Burst (runtime)", burstColor * 0.6f, Color.black);
            _rivalMat = LitMaterial("EdgeRivalBat (runtime)", rivalColor, 0.25f);
            for (int f = 0; f < RivalFrames; f++)
            {
                _batMesh[f] = BuildBatMesh(Mathf.Lerp(38f, -34f, f / (float)(RivalFrames - 1)));
                _rivalMats[f] = new Matrix4x4[64];
            }
        }

        // Low-poly bat, span 1 (scaled by rivalSize), +Z forward. Wings fold about the body axis by
        // `wingDeg`; the hand section lags a bit further for a softer stroke.
        static Mesh BuildBatMesh(float wingDeg)
        {
            var v = new List<Vector3>();
            var tri = new List<int>();
            void Tri(Vector3 a, Vector3 b, Vector3 c) { int i = v.Count; v.Add(a); v.Add(b); v.Add(c); tri.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 1 }); }
            Vector3 Wing(float side, float x, float z, float lag)
            {
                float a = wingDeg * Mathf.Deg2Rad * (x > 0.25f ? lag : 1f);
                return new Vector3(side * x * Mathf.Cos(a), x * Mathf.Sin(a), z);
            }
            // body + ears
            Vector3 nose = new Vector3(0f, 0.01f, 0.17f), tail = new Vector3(0f, 0f, -0.2f);
            Vector3 top = new Vector3(0f, 0.045f, 0f), belly = new Vector3(0f, -0.04f, 0.02f);
            Vector3 l = new Vector3(-0.045f, 0f, 0.02f), r = new Vector3(0.045f, 0f, 0.02f);
            Tri(nose, l, top); Tri(nose, top, r); Tri(nose, belly, l); Tri(nose, r, belly);
            Tri(l, tail, top); Tri(top, tail, r); Tri(belly, tail, l); Tri(r, tail, belly);
            Tri(new Vector3(-0.03f, 0.03f, 0.12f), new Vector3(-0.05f, 0.11f, 0.07f), new Vector3(-0.005f, 0.035f, 0.08f));
            Tri(new Vector3(0.03f, 0.03f, 0.12f), new Vector3(0.005f, 0.035f, 0.08f), new Vector3(0.05f, 0.11f, 0.07f));
            // wings: shoulder, wrist, tip, two scallops of the trailing edge, hip
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 sh = new Vector3(s * 0.04f, 0f, 0.06f), hip = new Vector3(s * 0.035f, 0f, -0.13f);
                Vector3 wrist = Wing(s, 0.22f, 0.05f, 1f), tip = Wing(s, 0.5f, -0.07f, 1.35f);
                Vector3 p1 = Wing(s, 0.37f, -0.15f, 1.25f), p2 = Wing(s, 0.2f, -0.12f, 1f);
                Tri(sh, wrist, p2); Tri(wrist, tip, p1); Tri(wrist, p1, p2); Tri(sh, p2, hip);
            }
            var m = new Mesh { name = "EdgeRivalBat" };
            m.SetVertices(v);
            m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        void BuildAudio()
        {
            if (!playSound) return;
            _sfx = GetComponent<EdgeFeedingAudio>();
            if (!_sfx) _sfx = gameObject.AddComponent<EdgeFeedingAudio>();
            bool any = false;
            if (eatClips != null) foreach (var c in eatClips) any |= c;
            if (any) _sfx.SetEatClips(eatClips);   // this game's clips win, also over an existing component's
            _sfx.eatVolume = eatVolume;
        }

        float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);
        Vector3 InUnitSphere() { Vector3 v; do v = new Vector3(Rand(-1, 1), Rand(-1, 1), Rand(-1, 1)); while (v.sqrMagnitude > 1f); return v; }

        void SpawnAll()
        {
            var p = Preset;
            _rng = new System.Random(5047);   // same starting field every round: both bats get the same night
            _ins = new Insect[Mathf.Clamp(p.insectCount, 1, 300)];
            for (int i = 0; i < _ins.Length; i++) Respawn(ref _ins[i], p, -1);
            _rivals = new Rival[_lampHeads.Count == 0 ? 0 : Mathf.Clamp(p.RivalCount, 0, 16)];
            for (int i = 0; i < _rivals.Length; i++)
            {
                ref var r = ref _rivals[i];
                r.lamp = (i * 7 + 3) % _lampHeads.Count;            // spread over the lamps
                r.radius = Rand(1.8f, 3.2f);
                r.height = Rand(-1.6f, 0.3f);
                r.angW = rivalSpeed / r.radius * (_rng.NextDouble() < 0.5 ? -1f : 1f);
                r.ang = Rand(0f, 6.283f);
                r.phase = Rand(0f, 100f);
                r.pos = _lampHeads[r.lamp] + new Vector3(Mathf.Cos(r.ang) * r.radius, r.height, Mathf.Sin(r.ang) * r.radius);
                r.vel = Vector3.zero;
            }
        }

        void AssignToLamp(ref Insect s, int lamp, EdgeFeedingPreset p)
        {
            s.lamp = lamp;
            s.anchor = _lampHeads[lamp];
            s.orbitR = Rand(p.lampOrbitRadius.x, p.lampOrbitRadius.y);
            s.orbitW = Rand(1.5f, 4f) * (_rng.NextDouble() < 0.5 ? -1f : 1f);  // rad/s, either direction
            s.axis = (Vector3.up + InUnitSphere() * 0.8f).normalized;           // tilted orbit plane
        }

        // where: -1 = pick by lampShare (initial spawn), 1 = at a lamp, 0 = in the dark. Eaten moths come back
        // where they lived, so only the lamps' pull moves insects between dark and light: the dark can only drain.
        void Respawn(ref Insect s, EdgeFeedingPreset p, int where)
        {
            bool lampIns = _lampHeads.Count > 0 && (where < 0 ? _rng.NextDouble() < p.lampShare : where > 0);
            if (lampIns)
            {
                AssignToLamp(ref s, _rng.Next(_lampHeads.Count), p);
                s.pos = s.anchor + InUnitSphere().normalized * s.orbitR;
            }
            else
            {
                s.lamp = -1;
                float x = Rand(_pathMin.x - p.darkSpread, _pathMax.x + p.darkSpread);
                float z = Rand(_pathMin.z - p.darkSpread, _pathMax.z + p.darkSpread);
                for (int k = 0; k < _lampHeads.Count; k++)
                {
                    Vector3 d = new Vector3(x - _lampHeads[k].x, 0, z - _lampHeads[k].z);
                    if (d.sqrMagnitude < 64f) { d = d.sqrMagnitude < 0.01f ? Vector3.right : d.normalized; x = _lampHeads[k].x + d.x * 8f; z = _lampHeads[k].z + d.z * 8f; }
                }
                s.anchor = new Vector3(x, GroundY(x, z) + Rand(p.darkHeight.x, p.darkHeight.y), z);
                if (flight)   // keep every dark moth inside the band the bat can fly (+/-1.25 m wander)
                    s.anchor.y = Mathf.Clamp(s.anchor.y, flight.minAltitude + 0.5f, Mathf.Max(flight.minAltitude + 0.5f, flight.maxAltitude - 1.5f));
                s.pos = s.anchor + InUnitSphere();
            }
            s.vel = Vector3.zero;
            s.phase = Rand(0f, 100f);
            s.nextDart = Time.time + Rand(0f, 2f);
            s.alive = true;
        }

        // Lowest solid hit under (x, z): tree-crown foliage triggers and trunk tops are not the ground.
        float GroundY(float x, float z)
        {
            int n = Physics.RaycastNonAlloc(new Vector3(x, 200f, z), Vector3.down, GroundHits, 400f, ~(1 << 2), QueryTriggerInteraction.Ignore);
            float y = float.MaxValue;
            for (int i = 0; i < n; i++) y = Mathf.Min(y, GroundHits[i].point.y);
            return n > 0 ? y : _startPos.y - 1f;
        }

        // ---- flow --------------------------------------------------------------
        public void SetVariant(Variant v) { variant = v; Restart(); }

        public void Restart()
        {
            var p = Preset;
            // a bat chosen on the start or results screen (or a switch of bat) introduces itself; R mid-round does not
            bool intro = introSeconds > 0f && (CurrentState != State.Playing || variant != _roundVariant);
            Score = 0;
            RivalScore = 0;
            LightSeconds = 0f;
            Hunger01 = p.startHunger;
            TimeLeft = p.roundSeconds;
            _budgetFmm = p.budgetFmm;
            Budget01 = 1f;
            EndReason = "";
            _pops.Clear();
            _floaters.Clear();
            SpawnAll();
            _darkAtStart = DarkInsects;
            if (flight) flight.ResetPose(_startPos, _startHeading);
            if (flight && flight.dose) flight.dose.ResetVirtual();   // the exhibit dose must not carry over between rounds
            CurrentState = State.Playing;
            _roundVariant = variant;
            _introUntil = intro ? Time.unscaledTime + introSeconds : -1f;
            _playStart = intro ? _introUntil : Time.unscaledTime;   // hint and light grace start after the intro
            if (_sfx) _sfx.ResetStreak();
            ApplyFlightMods(true);
            ApplyHud();
            RoundStarted?.Invoke(variant);
            if (intro) IntroStarted?.Invoke(variant);
        }

        /// Keep the current intro going for at least this long from its start (e.g. the length of the
        /// narrator's intro clip), so the line is never cut off.
        public void HoldIntroAtLeast(float seconds)
        {
            if (!InIntro) return;
            float until = _introUntil - introSeconds + seconds;
            if (until > _introUntil) { _introUntil = until; _playStart = until; }
        }

        /// End the running round now (e.g. from the CLI); shows the results screen.
        public void EndRound(string reason = "Round ended.") { if (CurrentState == State.Playing) End(reason); }

        void End(string reason)
        {
            CurrentState = State.Ended;
            EndReason = reason;
            _endedAt = Time.unscaledTime;
            ref var r = ref _results[(int)variant];
            r.played = true;
            r.moths = Score;
            r.lightSeconds = LightSeconds;
            r.darkStart = _darkAtStart;
            r.darkEnd = DarkInsects;
            r.rivals = RivalScore;
            r.reason = reason;
            _best[(int)variant] = Mathf.Max(_best[(int)variant], Score);
            if (_sfx) _sfx.SetApproach(0f, 0f);
            ApplyFlightMods(false);
            ApplyHud();
            RoundEnded?.Invoke(variant, Score, reason);
        }

        /// A new visitor: back to the start screen with an empty two-bat comparison (day bests are kept).
        public void NewVisit()
        {
            _results[0] = _results[1] = default;
            var p = Preset;   // the start screen must not show the last visitor's round
            Score = 0; RivalScore = 0; LightSeconds = 0f; EndReason = "";
            TimeLeft = p.roundSeconds; Hunger01 = p.startHunger; Budget01 = 1f;
            CurrentState = State.StartScreen;
            ApplyFlightMods(false);
            ApplyHud();
        }

        void ApplyHud()
        {
            if (!exhibitHud) return;
            exhibitHud.compact = !DebugHud;
            // the exhibit's budget-spent dimming belongs to the exhibit mode; each bat here has its own budget
            exhibitHud.hideBudget = !DebugHud;
        }

        void ApplyFlightMods(bool playing, bool restoreDefaults = false)
        {
            if (!flight) return;
            var p = Preset;
            if (restoreDefaults || !playing)
            {
                flight.speedMultiplier = restoreDefaults ? 1f : (CurrentState == State.Ended ? 0.4f : 1f);
                flight.extraInstability01 = 0f;
                flight.extraVib01 = 0f;
                flight.ignoreDoseInstability = !restoreDefaults;   // start/end screens never wobble or buzz from the exhibit dose
                return;
            }
            flight.speedMultiplier = p.speedMultiplier;
            flight.ignoreDoseInstability = p.ignoreDoseInstability;
        }

        // ---- update ------------------------------------------------------------
        void Update()
        {
            var kb = Keyboard.current;
            bool ping = false;
            if (kb != null)
            {
                if (kb.digit1Key.wasPressedThisFrame) SetVariant(Variant.LightTolerant);
                if (kb.digit2Key.wasPressedThisFrame) SetVariant(Variant.LightAverse);
                if (kb.rKey.wasPressedThisFrame) Restart();
                if (CurrentState != State.Playing && (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame)) Restart();
                if (kb[debugHudKey].wasPressedThisFrame) { DebugHud = !DebugHud; ApplyHud(); }
                if (kb[echoKey].wasPressedThisFrame && Preset.EchoEnabled) { _lastPing = Time.time; ping = true; }
            }
            var gp = Gamepad.current;
            if (gp != null && CurrentState != State.Playing && gp.startButton.wasPressedThisFrame) Restart();
            if (CurrentState == State.Ended && newVisitAfterSeconds > 0f && Time.unscaledTime - _endedAt > newVisitAfterSeconds) NewVisit();

            var pr = Preset;
            if (CurrentState == State.Playing && pr.EchoEnabled && pr.echoInterval > 0f && Time.time - _lastPing >= pr.echoInterval)
            { _lastPing = Time.time; ping = true; }
            if (ping && CurrentState == State.Playing && _sfx) _sfx.PlayPing();
            // during the start grace the light-averse bat still sees and feels the lamp, just faintly
            if (Fx) Fx.scale = pr.LightEffectScale * (InLightGrace ? 0.25f : 1f);
            SetLampLook(pr.LampBrightness, pr.LampGlow);

            float dt = Time.deltaTime;
            Simulate(dt);
            if (CurrentState == State.Playing)
            {
                if (InIntro) { if (flight) flight.ResetPose(_startPos, _startHeading); }   // hold still; no timer, no light
                else Tick(dt);
            }
            else if (_sfx) _sfx.SetApproach(0f, 0f);
            Draw();
            if (_ui) DrawUi();
        }

        float PanOf(Vector3 rel)
        {
            if (!flight) return 0f;
            float side = Vector3.Dot(rel, flight.transform.right);
            return Mathf.Clamp(side / Mathf.Max(0.3f, rel.magnitude), -1f, 1f);
        }

        void Tick(float dt)
        {
            var p = Preset;
            Vector3 bat = flight ? flight.transform.position : transform.position;
            Vector3 fwd = flight ? flight.transform.forward : transform.forward;

            float lux = 0f;
            foreach (var l in lamps)
            {
                if (!l) continue;
                Vector3 o = l.measureFrom ? l.measureFrom.position : l.transform.position;
                float dist = Vector3.Distance(bat, o);
                if (dist >= l.radius) continue;
                float dd = Mathf.Max(dist, 0.5f);
                lux += EdgeConfig.LampPhotLuxAtRef * (EdgeConfig.LampRefDistanceM * EdgeConfig.LampRefDistanceM) / (dd * dd);
            }
            // a torch held on the real bat adds to the lamps, so it drains the budget and energy too
            if (Fx) lux += Fx.RealGameLux;
            LuxAtBat = lux;
            InLight = lux >= p.litLuxThreshold;
            bool beamed = Fx && (Fx.InBeam || Fx.Flashing);
            if (InLight || beamed) LightSeconds += dt;

            float vib = 0f;
            if (p.lightHurts && !InLightGrace)
            {
                float fmmPerSec = lux * EdgeConfig.SpAmber / EdgeConfig.FmmScotLux * (EdgeConfig.TimeCompression / 60f);
                _budgetFmm -= fmmPerSec * p.doseMultiplier * dt;
                Budget01 = Mathf.Clamp01(_budgetFmm / Mathf.Max(0.01f, p.budgetFmm));
                if (flight)
                {
                    flight.speedMultiplier = p.speedMultiplier * (InLight ? p.litSpeedMultiplier : 1f);
                    flight.extraInstability01 = Mathf.Max(InLight ? p.litInstability : 0f, (1f - Budget01) * 0.6f);
                }
                if (InLight || beamed) Hunger01 -= p.litHungerDrainPerSec * dt;
                // the lamp's weak hum comes from EdgeLightEffects (design mapping); same hum here if that is missing/off
                if (InLight && !(Fx && Fx.isActiveAndEnabled)) vib = Mathf.Max(vib, 0.22f);
                if (_budgetFmm <= 0f) { End("Night vision gone: too much light, back to the roost."); return; }
            }
            else if (flight) { flight.extraInstability01 = 0f; }

            float r2 = p.eatRadius * p.eatRadius, a2 = approachRadius * approachRadius;
            float bestD2 = float.MaxValue;
            Vector3 bestRel = Vector3.zero;
            int alive = 0, atLamps = 0;
            for (int i = 0; i < _ins.Length; i++)
            {
                ref var s = ref _ins[i];
                if (!s.alive) continue;
                alive++;
                if (s.lamp >= 0) atLamps++;
                Vector3 rel = s.pos - bat;
                float d2 = rel.sqrMagnitude;
                if (d2 > r2)
                {
                    // nearest moth ahead drives the feeding buzz
                    if (d2 < a2 && d2 < bestD2 && Vector3.Dot(rel, fwd) > 0.35f * Mathf.Sqrt(d2)) { bestD2 = d2; bestRel = rel; }
                    continue;
                }
                s.alive = false;
                s.respawnAt = Time.time + p.respawnSeconds;
                Score++;
                Hunger01 = Mathf.Min(1f, Hunger01 + p.hungerPerInsect);
                if (eatBurst) { _pops.Add(new Pop { pos = s.pos, t = 0f, seed = i * 31 + Score }); _floaters.Add(new Pop { pos = s.pos, t = 0f }); }
                if (_sfx) _sfx.PlayEat(PanOf(rel));
                _scorePulse = Time.unscaledTime;
                _vibUntil = Time.time + eatVibSeconds;
                InsectEaten?.Invoke(Score);
            }
            LampShare01 = alive > 0 ? atLamps / (float)alive : 0f;
            if (_sfx)
            {
                if (bestD2 < a2)
                {
                    float d = Mathf.Sqrt(bestD2);
                    _sfx.SetApproach(1f - Mathf.Clamp01((d - p.eatRadius) / Mathf.Max(0.1f, approachRadius - p.eatRadius)), PanOf(bestRel));
                }
                else _sfx.SetApproach(0f, 0f);
            }
            if (Time.time < _vibUntil) vib = Mathf.Max(vib, eatVibLevel);
            if (flight) flight.extraVib01 = vib;

            Hunger01 -= p.hungerDrainPerSec * dt;
            TimeLeft -= dt;
            if (Hunger01 <= 0f) { Hunger01 = 0f; End("Out of energy: not enough moths."); }
            else if (TimeLeft <= 0f) { TimeLeft = 0f; End("Night's over."); }
        }

        int NearestLamp(Vector3 p, out float horizDist)
        {
            int best = -1; float bd = float.MaxValue;
            for (int k = 0; k < _lampHeads.Count; k++)
            {
                float dx = p.x - _lampHeads[k].x, dz = p.z - _lampHeads[k].z, d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = k; }
            }
            horizDist = Mathf.Sqrt(bd);
            return best;
        }

        void Simulate(float dt)
        {
            if (_ins.Length == 0) return;
            var p = Preset;
            float t = Time.time;
            float capture = p.lampOrbitRadius.y + 2.5f;
            for (int i = 0; i < _ins.Length; i++)
            {
                ref var s = ref _ins[i];
                if (!s.alive)
                {
                    if (t >= s.respawnAt) Respawn(ref s, p, s.lamp >= 0 ? 1 : 0);
                    continue;
                }
                float ph = s.phase + t * p.insectSpeed;
                Vector3 target;
                float stiffness;
                if (s.lamp >= 0)
                {
                    // erratic orbit: tilted circle whose radius breathes, plus random darts
                    Vector3 head = _lampHeads[s.lamp];
                    Vector3 rel = s.pos - head;
                    Vector3 radial = Vector3.ProjectOnPlane(rel, s.axis);
                    if (radial.sqrMagnitude < 1e-4f) radial = Vector3.Cross(s.axis, Vector3.right);
                    radial.Normalize();
                    float rr = s.orbitR * (0.75f + 0.5f * Mathf.PerlinNoise(ph * 0.7f, 3.1f));
                    Vector3 tangent = Vector3.Cross(s.axis, radial) * Mathf.Sign(s.orbitW);
                    target = head + radial * rr + tangent * (Mathf.Abs(s.orbitW) * rr * 0.35f)
                             + s.axis * ((Mathf.PerlinNoise(ph, 0.5f) - 0.5f) * 0.6f);
                    stiffness = 9f;
                    if (t >= s.nextDart)
                    {
                        s.vel += InUnitSphere().normalized * Rand(2f, 4.5f);
                        s.nextDart = t + Rand(0.3f, 2f) / Mathf.Max(0.05f, p.dartRate);
                        if (_rng.NextDouble() < 0.3) s.orbitW = -s.orbitW;   // reverse direction sometimes
                    }
                    // never fly into the lamp head itself
                    if (rel.sqrMagnitude < 0.09f) s.vel += rel.normalized * 3f;
                }
                else
                {
                    // vacuum-cleaner effect: drift towards the nearest lamp, then get caught in its swarm
                    if (p.lampPull > 0f && _lampHeads.Count > 0)
                    {
                        int k = NearestLamp(s.anchor, out float hd);
                        if (hd < p.lampPullRange)
                        {
                            float pull = p.lampPull * (1f - hd / p.lampPullRange);
                            s.anchor = Vector3.MoveTowards(s.anchor, _lampHeads[k], 0.6f * pull * dt);   // ~half the dark drained in 90 s
                            if (hd < capture) AssignToLamp(ref s, k, p);
                        }
                    }
                    Vector3 n = new Vector3(Mathf.PerlinNoise(ph, 0.1f) - 0.5f, Mathf.PerlinNoise(0.3f, ph) - 0.5f, Mathf.PerlinNoise(ph, 0.7f) - 0.5f);
                    target = s.anchor + n * 2.5f;
                    stiffness = 3f;
                }
                Vector3 acc = (target - s.pos) * stiffness - s.vel * 2.2f;
                s.vel += acc * dt;
                s.pos += s.vel * dt;
            }
            SimulateRivals(dt, t, p);
            for (int i = _pops.Count - 1; i >= 0; i--)
            {
                var pp = _pops[i]; pp.t += dt;
                if (pp.t > PopLife) _pops.RemoveAt(i); else _pops[i] = pp;
            }
            for (int i = _floaters.Count - 1; i >= 0; i--)
            {
                var pp = _floaters[i]; pp.t += dt;
                if (pp.t > FloatLife) _floaters.RemoveAt(i); else _floaters[i] = pp;
            }
        }

        // Light-tolerant rivals circle their lamp and hawk the nearest moth in its swarm.
        void SimulateRivals(float dt, float t, EdgeFeedingPreset p)
        {
            for (int k = 0; k < _rivals.Length; k++)
            {
                ref var r = ref _rivals[k];
                Vector3 head = _lampHeads[r.lamp];
                r.ang += r.angW * dt;
                float rad = r.radius * (0.75f + 0.5f * Mathf.PerlinNoise(t * 0.35f + r.phase, 1.7f));
                float y = r.height + 1.2f * (Mathf.PerlinNoise(t * 0.5f, r.phase) - 0.5f);
                Vector3 target = head + new Vector3(Mathf.Cos(r.ang) * rad, y, Mathf.Sin(r.ang) * rad);

                int prey = -1;
                float best = 2.5f * 2.5f;
                for (int i = 0; i < _ins.Length; i++)
                {
                    if (!_ins[i].alive || _ins[i].lamp != r.lamp) continue;
                    float d2 = (_ins[i].pos - r.pos).sqrMagnitude;
                    if (d2 < best) { best = d2; prey = i; }
                }
                if (prey >= 0 && t >= r.nextBite) target = Vector3.Lerp(target, _ins[prey].pos, 0.8f);

                Vector3 to = target - r.pos;
                Vector3 want = to.sqrMagnitude > 1e-4f ? to.normalized * rivalSpeed * Mathf.Clamp01(to.magnitude) : Vector3.zero;
                r.vel = Vector3.MoveTowards(r.vel, want, 20f * dt);
                Vector3 fromHead = r.pos - head;
                if (fromHead.sqrMagnitude < 0.64f) r.vel += fromHead.normalized * 6f * dt;   // keep off the lamp
                r.pos += r.vel * dt;

                if (prey >= 0 && t >= r.nextBite && (_ins[prey].pos - r.pos).sqrMagnitude < 0.5f * 0.5f)
                {
                    _ins[prey].alive = false;
                    _ins[prey].respawnAt = t + p.respawnSeconds;
                    r.nextBite = t + Rand(0.4f, 0.9f);
                    if (CurrentState == State.Playing) RivalScore++;
                }
            }
        }

        static Vector3 HashDir(int seed, int k)
        {
            float a = Mathf.Repeat(Mathf.Sin(seed * 12.9898f + k * 78.233f) * 43758.547f, 1f) * 6.2832f;
            float b = Mathf.Repeat(Mathf.Sin(seed * 39.346f + k * 11.135f) * 24634.633f, 1f) * 2f - 1f;
            float c = Mathf.Sqrt(1f - b * b);
            return new Vector3(c * Mathf.Cos(a), b * 0.6f + 0.25f, c * Mathf.Sin(a));
        }

        void Draw()
        {
            if (!_mat || !_mesh) return;
            var p = Preset;
            int c = 0, e = 0, bN = 0;
            float t = Time.time;
            float echoAge = t - _lastPing;
            bool echo = p.EchoEnabled && CurrentState == State.Playing && echoAge < p.echoDuration;
            float echoK = echo ? 1f - echoAge / p.echoDuration : 0f;
            float echoR2 = p.echoRadius * p.echoRadius;
            Vector3 bat = flight ? flight.transform.position : transform.position;
            for (int i = 0; i < _ins.Length && c < _mats.Length; i++)
            {
                ref var s = ref _ins[i];
                if (!s.alive) continue;
                // wing flap: x-scale flicker (wings fold) + small roll jitter
                float f = Mathf.Sin(t * flapHz * 6.283f + s.phase * 7f);
                float wing = 0.35f + 0.65f * Mathf.Abs(f);
                Quaternion rot = (s.vel.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(s.vel) : Quaternion.identity)
                                 * Quaternion.Euler(0f, 0f, f * 15f);
                var scale = new Vector3(insectSize * wing, insectSize, insectSize);
                _mats[c++] = Matrix4x4.TRS(s.pos, rot, scale);
                if (echo && e < _echoMats.Length && (s.pos - bat).sqrMagnitude < echoR2)
                    _echoMats[e++] = Matrix4x4.TRS(s.pos, rot, scale * (echoScale * (0.5f + 0.5f * echoK)));
            }
            // eat burst: wing-scale fragments thrown out and falling, shrinking as they go
            foreach (var pp in _pops)
            {
                float u = pp.t / PopLife, ease = 1f - (1f - u) * (1f - u);
                for (int k = 0; k < 7 && bN < _burstMats.Length; k++)
                {
                    Vector3 d = HashDir(pp.seed, k);
                    Vector3 pos = pp.pos + d * (0.35f * ease) + Vector3.down * (0.5f * u * u);
                    _burstMats[bN++] = Matrix4x4.TRS(pos, Quaternion.Euler(k * 50f + u * 400f, k * 80f, 0f), Vector3.one * insectSize * 0.45f * (1f - u));
                }
            }
            for (int f = 0; f < RivalFrames; f++) _rivalN[f] = 0;
            for (int k = 0; k < _rivals.Length; k++)
            {
                ref var r = ref _rivals[k];
                float flap = Mathf.Sin(t * rivalFlapHz * 6.283f + r.phase);
                int frame = Mathf.Clamp(Mathf.RoundToInt((1f - (flap * 0.5f + 0.5f)) * (RivalFrames - 1)), 0, RivalFrames - 1);
                if (_rivalN[frame] >= _rivalMats[frame].Length) continue;
                Vector3 v = r.vel.sqrMagnitude > 0.01f ? r.vel : Vector3.forward;
                float bank = Mathf.Clamp(r.angW * 12f, -40f, 40f);   // angW > 0 circles left: roll left into it
                var rot = Quaternion.LookRotation(v) * Quaternion.Euler(0f, 0f, bank);
                _rivalMats[frame][_rivalN[frame]++] = Matrix4x4.TRS(r.pos, rot, Vector3.one * rivalSize);
            }

            var bounds = new Bounds(transform.position, Vector3.one * 2000f);
            if (c > 0)
                Graphics.RenderMeshInstanced(new RenderParams(_mat) { worldBounds = bounds, receiveShadows = true,
                    shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off }, _mesh, 0, _mats, c);
            if (e > 0 && _echoMat)
                Graphics.RenderMeshInstanced(new RenderParams(_echoMat) { worldBounds = bounds, receiveShadows = false,
                    shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off }, _mesh, 0, _echoMats, e);
            if (bN > 0 && _burstMat)
                Graphics.RenderMeshInstanced(new RenderParams(_burstMat) { worldBounds = bounds, receiveShadows = false,
                    shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off }, _mesh, 0, _burstMats, bN);
            for (int f = 0; f < RivalFrames; f++)
                if (_rivalN[f] > 0 && _rivalMat)
                    Graphics.RenderMeshInstanced(new RenderParams(_rivalMat) { worldBounds = bounds, receiveShadows = true,
                        shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off }, _batMesh[f], 0, _rivalMats[f], _rivalN[f]);
        }

        // ---- HUD ---------------------------------------------------------------
        GUIStyle _big, _wrap, _small, _score, _col, _colHead, _float, _guide;

        Camera ActiveCamera()
        {
            if (cams)
            {
                var c = cams.ThirdPerson ? cams.tpv : cams.fpv;
                if (c && c.isActiveAndEnabled) return c;
            }
            return Camera.main;
        }

        string Verdict(EdgeFeedingPreset p)
        {
            var rt = _results[(int)Variant.LightTolerant];
            var ra = _results[(int)Variant.LightAverse];
            return !(rt.played && ra.played) ? p.endMessage
                : rt.moths > ra.moths
                    ? $"Same night. Unequal feast. The light-tolerant bat ate {rt.moths} moths, the light-averse bat {ra.moths}. The lamps decided who ate."
                    : $"The light-averse bat ate {ra.moths} moths in the dark this time (light-tolerant: {rt.moths}). On most lit nights the lamps take that food first.";
        }

        // ---- themed screens (uGUI) ------------------------------------------------------
        EdgeUiView _ui;
        readonly UiList _uiNodes = new UiList();
        readonly Vector3[] _floatBuf = new Vector3[32];

        BatCard Card(EdgeFeedingPreset preset, Variant v)
        {
            string blurb = preset ? preset.speciesBlurb : "";
            int colon = blurb.IndexOf(':');
            string desc = colon >= 0 ? blurb.Substring(colon + 1).Trim() : "";
            if (desc.Length > 0) desc = char.ToUpperInvariant(desc[0]) + desc.Substring(1);
            var r = _results[(int)v];
            return new BatCard
            {
                name = preset ? preset.displayName : v.ToString(),
                species = colon >= 0 ? blurb.Substring(0, colon).Trim() : blurb,
                desc = desc,
                stats = new RoundStats
                {
                    played = r.played, moths = r.moths, best = _best[(int)v], darkStart = r.darkStart, darkEnd = r.darkEnd,
                    rivals = r.rivals, lightSeconds = r.lightSeconds,
                },
            };
        }

        void DrawUi()
        {
            var p = Preset;
            float now = Time.unscaledTime, age = now - _playStart;
            var m = new HudModel
            {
                screen = CurrentState == State.Playing ? HudScreen.Playing : CurrentState == State.StartScreen ? HudScreen.Start : HudScreen.Results,
                time = now,
                tolerant = Card(tolerantPreset, Variant.LightTolerant),
                averse = Card(aversePreset, Variant.LightAverse),
                averseCurrent = variant == Variant.LightAverse,
                score = Score, timeLeft = TimeLeft, roundSeconds = p.roundSeconds, hunger01 = Hunger01, budget01 = Budget01,
                scorePulse01 = Mathf.Clamp01(1f - (now - _scorePulse) / 0.35f),
                lightHurts = p.lightHurts, inLight = InLight, echo = p.EchoEnabled,
                hint = p.displayName + ": " + (string.IsNullOrEmpty(p.playHint) ? "fly through moths to eat." : p.playHint),
                hintAlpha = CurrentState == State.Playing ? Mathf.Clamp01(hintFadeSeconds + 1f - age) : 0f,   // hold, then a 1 s fade
                endReason = EndReason, verdict = Verdict(p),
                debug = DebugHud ? $"{p.displayName} | lux {LuxAtBat:F2} (real {(Fx ? Fx.RealGameLux : 0f):F2}) {(InLight ? "LIT" : "dark")} | energy {Hunger01 * 100f:F0}% night vision {Budget01 * 100f:F0}% | at lamps {LampShare01 * 100f:F0}% rivals {RivalScore}\n[1][2] variant  [R] restart  [E] ping  [H] HUD" : null,
                subtitle = _ui.Subtitle, subtitleAlpha = _ui.SubtitleAlpha,
            };
            int n = 0;
            var cam = ActiveCamera();
            if (cam)
                foreach (var f in _floaters)
                {
                    if (n >= _floatBuf.Length) break;
                    Vector3 sp = cam.WorldToScreenPoint(f.pos);
                    if (sp.z <= 0.1f) continue;
                    Vector2 c = _ui.ScreenToCanvas(sp);
                    _floatBuf[n++] = new Vector3(c.x, c.y, f.t / FloatLife);
                }
            m.floaters = _floatBuf; m.floaterCount = n;
            _uiNodes.Clear();
            Vector2 size = _ui.Size;
            EdgeFeedingHud.Build(_uiNodes, m, size.x, size.y);
            _ui.Apply(_uiNodes.nodes);
        }

        void OnGUI()
        {
            if (_ui && _ui.isActiveAndEnabled) return;   // the themed screens are showing
            if (_big == null)
            {
                _big = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.UpperCenter, fontSize = 15 };
                _small = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleRight };
                _score = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
                _col = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, alignment = TextAnchor.UpperLeft };
                _colHead = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
                _float = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _guide = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Italic, wordWrap = true, alignment = TextAnchor.UpperCenter };
                _guide.normal.textColor = new Color(1f, 0.8f, 0.5f);
            }
            var p = Preset;
            Texture2D white = Texture2D.whiteTexture;

            // compact cluster, top-right; the score swells briefly on each catch
            const float w = 180f;
            float x = Screen.width - w - 16f, y = 12f;
            float pulse = Mathf.Clamp01(1f - (Time.unscaledTime - _scorePulse) / 0.35f);
            _score.fontSize = Mathf.RoundToInt(16f + 8f * pulse);
            GUI.Label(new Rect(x - 60f, y, w + 60f, 26f), $"{Score} moths  ·  {Mathf.CeilToInt(TimeLeft)}s", _score);
            y += 26f;
            Bar(new Rect(x, y, w, 6f), Hunger01, new Color(0.45f, 0.9f, 0.45f), white);
            if (p.lightHurts)
            {
                y += 9f;
                Bar(new Rect(x, y, w, 6f), Budget01, InLight ? new Color(1f, 0.4f, 0.2f) : new Color(1f, 0.7f, 0.25f), white);
            }

            if (DebugHud)
                GUI.Label(new Rect(x - 300f, y + 12f, w + 300f, 60f),
                    $"{p.displayName} | lux {LuxAtBat:F2} (real {(Fx ? Fx.RealGameLux : 0f):F2}) {(InLight ? "LIT" : "dark")} | energy {Hunger01 * 100f:F0}% night vision {Budget01 * 100f:F0}% | at lamps {LampShare01 * 100f:F0}% rivals {RivalScore}\n[1][2] variant  [R] restart  [E] ping  [H] HUD", _small);

            // +1 rising from each catch
            var cam = ActiveCamera();
            if (cam)
                foreach (var f in _floaters)
                {
                    Vector3 sp = cam.WorldToScreenPoint(f.pos);
                    if (sp.z <= 0.1f) continue;
                    float u = f.t / FloatLife;
                    GUI.color = new Color(1f, 0.92f, 0.7f, 1f - u * u);
                    GUI.Label(new Rect(sp.x - 40f, Screen.height - sp.y - 30f - 50f * u, 80f, 30f), "+1", _float);
                }
            GUI.color = Color.white;

            if (CurrentState == State.Playing)
            {
                float age = Time.unscaledTime - _playStart;
                if (age < hintFadeSeconds)
                {
                    GUI.color = new Color(1f, 1f, 1f, 1f - age / hintFadeSeconds);
                    string hint = string.IsNullOrEmpty(p.playHint) ? "fly through moths to eat" : p.playHint;
                    GUI.Label(new Rect(0f, Screen.height - 60f, Screen.width, 24f),
                        $"{p.displayName} — {hint}" + (p.EchoEnabled ? "   ·   E: echolocate" : ""), _wrap);
                    GUI.color = Color.white;
                }
                return;
            }

            if (CurrentState == State.StartScreen)
            {
                Rect sp = new Rect(Screen.width * 0.5f - 300f, Screen.height * 0.5f - 110f, 600f, 220f);
                Panel(sp, white);
                GUI.Label(new Rect(sp.x, sp.y + 15f, sp.width, 36f), "Night Feeding", _big);
                GUI.Label(new Rect(sp.x + 30f, sp.y + 60f, sp.width - 60f, 150f),
                    $"{p.speciesBlurb}\n\nFly through moths to eat them before your energy runs out.\n" +
                    "[1] light-tolerant   [2] light-averse   [R] restart   [E] echolocate   [H] HUD\n\n" +
                    "SPACE / ENTER to start", _wrap);
                return;
            }

            // end screen: this round, then both bats side by side
            Rect panel = new Rect(Screen.width * 0.5f - 380f, Screen.height * 0.5f - 200f, 760f, 400f);
            Panel(panel, white);
            GUI.Label(new Rect(panel.x, panel.y + 14f, panel.width, 34f), $"{Score} moths", _big);
            GUI.Label(new Rect(panel.x, panel.y + 48f, panel.width, 22f), EndReason, _wrap);
            float cy = panel.y + 82f, cw = (panel.width - 90f) * 0.5f;
            Column(new Rect(panel.x + 30f, cy, cw, 124f), tolerantPreset, Variant.LightTolerant, white);
            Column(new Rect(panel.x + 60f + cw, cy, cw, 124f), aversePreset, Variant.LightAverse, white);
            var rt = _results[(int)Variant.LightTolerant];
            var ra = _results[(int)Variant.LightAverse];
            GUI.Label(new Rect(panel.x + 30f, cy + 132f, panel.width - 60f, 64f), Verdict(p), _wrap);
            GUI.Label(new Rect(panel.x + 30f, cy + 200f, panel.width - 60f, 44f),
                "Keep corridors dark. Where light is needed, use less of it: warmer, pointed down, shielded, and on for less time. " +
                "— National Light Pollution Guidelines for Wildlife", _guide);
            GUI.Label(new Rect(panel.x, panel.yMax - 36f, panel.width, 24f), "SPACE replay   ·   1 / 2 switch bat", _wrap);
        }

        void Column(Rect r, EdgeFeedingPreset preset, Variant v, Texture2D white)
        {
            var res = _results[(int)v];
            bool current = v == variant;
            GUI.color = current ? new Color(1f, 1f, 1f, 0.12f) : new Color(1f, 1f, 1f, 0.05f);
            GUI.DrawTexture(new Rect(r.x - 10f, r.y - 6f, r.width + 20f, r.height + 12f), white);
            GUI.color = Color.white;
            string name = preset ? preset.displayName : v.ToString();
            GUI.Label(new Rect(r.x, r.y, r.width, 22f), name + (current ? "  (this round)" : ""), _colHead);
            string body = !res.played
                ? $"Not played yet: press {(v == Variant.LightTolerant ? 1 : 2)}"
                : $"Moths caught:  {res.moths}   (today's best {_best[(int)v]})\n" +
                  $"Time in light:  {res.lightSeconds:F0} s\n" +
                  $"Moths left in the dark:  {res.darkStart} -> {res.darkEnd}\n" +
                  $"Eaten at the lamps by light-tolerant bats:  {res.rivals}";
            GUI.Label(new Rect(r.x, r.y + 26f, r.width, r.height - 26f), body, _col);
        }

        static void Panel(Rect r, Texture2D white)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(r, white);
            GUI.color = Color.white;
        }

        static void Bar(Rect r, float v, Color c, Texture2D white)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.5f); GUI.DrawTexture(r, white);
            GUI.color = c; GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(v), r.height), white);
            GUI.color = Color.white;
        }
    }
}
