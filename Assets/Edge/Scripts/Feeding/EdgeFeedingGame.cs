using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Edge.Feeding
{
    // Mini insect-feeding game with two variants (light-tolerant / light-averse bat).
    // Moths: pooled structs, dark HDRP/Lit, drawn with Graphics.RenderMeshInstanced; no GameObjects,
    // no Rigidbodies. Lamps are found from LampZone components (fallback: Light components).
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

        [Header("Moth look")]
        [Tooltip("Optional; if empty a dark HDRP/Lit material is made at runtime")]
        public Material insectMaterial;
        public Color mothColor = new Color(0.06f, 0.045f, 0.035f);
        [Tooltip("Wingspan (m). Real small moths ~0.02-0.04; larger for gameplay")]
        public float insectSize = 0.07f;
        public float flapHz = 22f;
        [Header("Echolocation highlight")]
        public Color echoColor = new Color(0.55f, 0.8f, 1f);
        public float echoEmissive = 30f;
        public Key echoKey = Key.E;

        [Header("Feedback")]
        public bool playSound = true;
        [Tooltip("VIB/LED burst sent to the tag on each eat")] [Range(0f, 1f)] public float eatVibLevel = 0.8f;
        public float eatVibSeconds = 0.12f;
        public Key debugHudKey = Key.H;
        public float hintFadeSeconds = 3f;

        // Hooks (e.g. EdgeNarrator). Optional; nothing subscribes by default.
        public event Action<int> InsectEaten;
        public event Action<Variant, int, string> RoundEnded;
        public event Action<Variant> RoundStarted;

        public State CurrentState { get; private set; }
        public int Score { get; private set; }
        public float Hunger01 { get; private set; }
        public float TimeLeft { get; private set; }
        public float Budget01 { get; private set; } = 1f;
        public bool InLight { get; private set; }
        public float LuxAtBat { get; private set; }
        public string EndReason { get; private set; } = "";
        public bool DebugHud { get; private set; }
        public EdgeFeedingPreset Preset => variant == Variant.LightTolerant ? tolerantPreset : aversePreset;

        struct Insect
        {
            public Vector3 pos, anchor, vel, axis;
            public int lamp;        // -1 = dark
            public float phase, respawnAt, orbitR, orbitW, nextDart;
            public bool alive;
        }

        struct Pop { public Vector3 pos; public float t; }

        Insect[] _ins = new Insect[0];
        readonly List<Vector3> _lampHeads = new List<Vector3>();
        readonly List<Pop> _pops = new List<Pop>();
        readonly Matrix4x4[] _mats = new Matrix4x4[1023];
        readonly Matrix4x4[] _echoMats = new Matrix4x4[1023];
        Mesh _mesh;
        Material _mat, _echoMat;
        AudioSource _audio;
        AudioClip _crunch;
        Vector3 _startPos, _pathMin, _pathMax;
        float _startHeading, _budgetFmm, _vibUntil, _bestTolerant, _bestAverse, _playStart, _lastPing = -100f;
        readonly System.Random _rng = new System.Random(5047);

        void Awake()
        {
            if (!tolerantPreset) tolerantPreset = EdgeFeedingPreset.CreateTolerant();
            if (!aversePreset) aversePreset = EdgeFeedingPreset.CreateAverse();
            if (!flight) flight = FindAnyObjectByType<BatFlightController>();
            if (!link && flight) link = flight.link;
            if (lamps == null || lamps.Length == 0) lamps = FindObjectsByType<LampZone>();
            if (!exhibitHud) exhibitHud = FindAnyObjectByType<EdgeHud>();
        }

        void Start()
        {
            if (flight) { _startPos = flight.transform.position; _startHeading = flight.transform.eulerAngles.y; }
            FindLamps();
            BuildVisuals();
            BuildAudio();
            if (showStartScreen) { CurrentState = State.StartScreen; ApplyFlightMods(false); SpawnAll(); ApplyHud(); }
            else Restart();
        }

        void OnEnable() { if (flight) flight.Bumped += OnBumped; }

        void OnBumped(Vector3 point, Vector3 normal, Collider c)
        {
            if (CurrentState != State.Playing) return;
            Hunger01 = Mathf.Max(0f, Hunger01 - Preset.bumpHungerCost);
        }

        void OnDisable()
        {
            if (flight) flight.Bumped -= OnBumped;
            ApplyFlightMods(false, restoreDefaults: true);
            if (exhibitHud) { exhibitHud.compact = false; exhibitHud.hideBudget = false; }
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

            _mat = insectMaterial;
            if (!_mat)
            {
                var sh = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
                _mat = new Material(sh) { name = "EdgeMoth Dark (runtime)" };
                _mat.SetColor("_BaseColor", mothColor);
                _mat.SetColor("_Color", mothColor);
                _mat.SetFloat("_Smoothness", 0.15f);
                _mat.SetFloat("_Metallic", 0f);
                _mat.SetFloat("_DoubleSidedEnable", 1f);
                _mat.SetFloat("_CullMode", 0f);
                _mat.SetFloat("_CullModeForward", 0f);
                _mat.EnableKeyword("_DOUBLESIDED_ON");
            }
            _mat.enableInstancing = true;

            var ush = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color");
            _echoMat = new Material(ush) { name = "EdgeMoth Echo (runtime)", enableInstancing = true };
            _echoMat.SetColor("_UnlitColor", echoColor * 0.4f);
            _echoMat.SetColor("_Color", echoColor);
            _echoMat.SetColor("_EmissiveColor", echoColor * echoEmissive);
        }

        void BuildAudio()
        {
            if (!playSound) return;
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false; _audio.spatialBlend = 0f; _audio.volume = 0.5f;
            const int sr = 22050, n = sr / 14;
            var d = new float[n];
            var r = new System.Random(7);
            for (int i = 0; i < n; i++)
            {
                float env = Mathf.Exp(-i / (n * 0.18f));
                float click = (i / 40) % 2 == 0 ? 1f : -1f;
                d[i] = env * (0.6f * ((float)r.NextDouble() * 2f - 1f) + 0.25f * click);
            }
            _crunch = AudioClip.Create("EdgeCrunch", n, 1, sr, false);
            _crunch.SetData(d, 0);
        }

        float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);
        Vector3 InUnitSphere() { Vector3 v; do v = new Vector3(Rand(-1, 1), Rand(-1, 1), Rand(-1, 1)); while (v.sqrMagnitude > 1f); return v; }

        void SpawnAll()
        {
            var p = Preset;
            _ins = new Insect[Mathf.Clamp(p.insectCount, 1, 300)];
            for (int i = 0; i < _ins.Length; i++) Respawn(ref _ins[i], p);
        }

        void Respawn(ref Insect s, EdgeFeedingPreset p)
        {
            bool lampIns = _lampHeads.Count > 0 && _rng.NextDouble() < p.lampShare;
            if (lampIns)
            {
                s.lamp = _rng.Next(_lampHeads.Count);
                s.anchor = _lampHeads[s.lamp];
                s.orbitR = Rand(p.lampOrbitRadius.x, p.lampOrbitRadius.y);
                s.orbitW = Rand(1.5f, 4f) * (_rng.NextDouble() < 0.5 ? -1f : 1f);  // rad/s, either direction
                s.axis = (Vector3.up + InUnitSphere() * 0.8f).normalized;           // tilted orbit plane
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
                float groundY = Physics.Raycast(new Vector3(x, 200f, z), Vector3.down, out var hit, 400f) ? hit.point.y : _startPos.y - 1f;
                s.anchor = new Vector3(x, groundY + Rand(p.darkHeight.x, p.darkHeight.y), z);
                s.pos = s.anchor + InUnitSphere();
            }
            s.vel = Vector3.zero;
            s.phase = Rand(0f, 100f);
            s.nextDart = Time.time + Rand(0f, 2f);
            s.alive = true;
        }

        // ---- flow --------------------------------------------------------------
        public void SetVariant(Variant v) { variant = v; Restart(); }

        public void Restart()
        {
            var p = Preset;
            Score = 0;
            Hunger01 = p.startHunger;
            TimeLeft = p.roundSeconds;
            _budgetFmm = p.budgetFmm;
            Budget01 = 1f;
            EndReason = "";
            _pops.Clear();
            SpawnAll();
            if (flight) flight.ResetPose(_startPos, _startHeading);
            CurrentState = State.Playing;
            _playStart = Time.unscaledTime;
            ApplyFlightMods(true);
            ApplyHud();
            RoundStarted?.Invoke(variant);
        }

        void End(string reason)
        {
            CurrentState = State.Ended;
            EndReason = reason;
            if (variant == Variant.LightTolerant) _bestTolerant = Mathf.Max(_bestTolerant, Score);
            else _bestAverse = Mathf.Max(_bestAverse, Score);
            ApplyFlightMods(false);
            ApplyHud();
            RoundEnded?.Invoke(variant, Score, reason);
        }

        void ApplyHud()
        {
            if (!exhibitHud) return;
            exhibitHud.compact = !DebugHud;
            exhibitHud.hideBudget = !DebugHud && variant == Variant.LightTolerant;
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
                flight.ignoreDoseInstability = false;
                return;
            }
            flight.speedMultiplier = p.speedMultiplier;
            flight.ignoreDoseInstability = p.ignoreDoseInstability;
        }

        // ---- update ------------------------------------------------------------
        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.digit1Key.wasPressedThisFrame) SetVariant(Variant.LightTolerant);
                if (kb.digit2Key.wasPressedThisFrame) SetVariant(Variant.LightAverse);
                if (kb.rKey.wasPressedThisFrame) Restart();
                if (CurrentState != State.Playing && (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame)) Restart();
                if (kb[debugHudKey].wasPressedThisFrame) { DebugHud = !DebugHud; ApplyHud(); }
                if (kb[echoKey].wasPressedThisFrame && Preset.EchoEnabled) _lastPing = Time.time;
            }
            var gp = Gamepad.current;
            if (gp != null && CurrentState != State.Playing && gp.startButton.wasPressedThisFrame) Restart();

            var pr = Preset;
            if (CurrentState == State.Playing && pr.EchoEnabled && pr.echoInterval > 0f && Time.time - _lastPing >= pr.echoInterval)
                _lastPing = Time.time;

            float dt = Time.deltaTime;
            Simulate(dt);
            if (CurrentState == State.Playing) Tick(dt);
            Draw();
        }

        void Tick(float dt)
        {
            var p = Preset;
            Vector3 bat = flight ? flight.transform.position : transform.position;

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
            LuxAtBat = lux;
            InLight = lux >= p.litLuxThreshold;

            float vib = 0f;
            if (p.lightHurts)
            {
                float fmmPerSec = lux * EdgeConfig.SpAmber / EdgeConfig.FmmScotLux * (EdgeConfig.TimeCompression / 60f);
                _budgetFmm -= fmmPerSec * p.doseMultiplier * dt;
                Budget01 = Mathf.Clamp01(_budgetFmm / Mathf.Max(0.01f, p.budgetFmm));
                if (flight)
                {
                    flight.speedMultiplier = p.speedMultiplier * (InLight ? p.litSpeedMultiplier : 1f);
                    flight.extraInstability01 = Mathf.Max(InLight ? p.litInstability : 0f, (1f - Budget01) * 0.6f);
                }
                if (InLight) Hunger01 -= p.litHungerDrainPerSec * dt;
                vib = InLight ? Mathf.Clamp01(0.25f + lux * 0.1f) : 0f;
                if (_budgetFmm <= 0f) { End("Light budget spent — forced to retreat to the roost."); return; }
            }
            else if (flight) { flight.extraInstability01 = 0f; }

            float r2 = p.eatRadius * p.eatRadius;
            for (int i = 0; i < _ins.Length; i++)
            {
                ref var s = ref _ins[i];
                if (!s.alive || (s.pos - bat).sqrMagnitude > r2) continue;
                s.alive = false;
                s.respawnAt = Time.time + p.respawnSeconds;
                Score++;
                Hunger01 = Mathf.Min(1f, Hunger01 + p.hungerPerInsect);
                _pops.Add(new Pop { pos = s.pos, t = 0f });
                if (_audio && _crunch) { _audio.pitch = Rand(0.85f, 1.25f); _audio.PlayOneShot(_crunch); }
                _vibUntil = Time.time + eatVibSeconds;
                InsectEaten?.Invoke(Score);
            }
            if (Time.time < _vibUntil) vib = Mathf.Max(vib, eatVibLevel);
            if (flight) flight.extraVib01 = vib;

            Hunger01 -= p.hungerDrainPerSec * dt;
            TimeLeft -= dt;
            if (Hunger01 <= 0f) { Hunger01 = 0f; End("Starved — not enough insects."); }
            else if (TimeLeft <= 0f) { TimeLeft = 0f; End("Night's over."); }
        }

        void Simulate(float dt)
        {
            if (_ins.Length == 0) return;
            var p = Preset;
            float t = Time.time;
            for (int i = 0; i < _ins.Length; i++)
            {
                ref var s = ref _ins[i];
                if (!s.alive)
                {
                    if (t >= s.respawnAt) Respawn(ref s, p);
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
                    Vector3 n = new Vector3(Mathf.PerlinNoise(ph, 0.1f) - 0.5f, Mathf.PerlinNoise(0.3f, ph) - 0.5f, Mathf.PerlinNoise(ph, 0.7f) - 0.5f);
                    target = s.anchor + n * 2.5f;
                    stiffness = 3f;
                }
                Vector3 acc = (target - s.pos) * stiffness - s.vel * 2.2f;
                s.vel += acc * dt;
                s.pos += s.vel * dt;
            }
            for (int i = _pops.Count - 1; i >= 0; i--)
            {
                var pp = _pops[i]; pp.t += dt;
                if (pp.t > 0.15f) _pops.RemoveAt(i); else _pops[i] = pp;
            }
        }

        void Draw()
        {
            if (!_mat || !_mesh) return;
            var p = Preset;
            int c = 0, e = 0;
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
                    _echoMats[e++] = Matrix4x4.TRS(s.pos, rot, scale * (1.2f + 0.8f * echoK));
            }
            foreach (var pp in _pops)
            {
                if (c >= _mats.Length) break;
                float k = pp.t / 0.15f;
                _mats[c++] = Matrix4x4.TRS(pp.pos, Quaternion.Euler(0, k * 90f, 0), Vector3.one * insectSize * (1f + 1.5f * k) * (1f - k));
            }
            var bounds = new Bounds(transform.position, Vector3.one * 2000f);
            if (c > 0)
                Graphics.RenderMeshInstanced(new RenderParams(_mat) { worldBounds = bounds, receiveShadows = true,
                    shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off }, _mesh, 0, _mats, c);
            if (e > 0 && _echoMat)
                Graphics.RenderMeshInstanced(new RenderParams(_echoMat) { worldBounds = bounds, receiveShadows = false,
                    shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off }, _mesh, 0, _echoMats, e);
        }

        // ---- HUD ---------------------------------------------------------------
        GUIStyle _big, _wrap, _small;

        void OnGUI()
        {
            if (_big == null)
            {
                _big = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.UpperCenter, fontSize = 15 };
                _small = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleRight };
            }
            var p = Preset;
            Texture2D white = Texture2D.whiteTexture;

            // compact cluster, top-right
            const float w = 180f;
            float x = Screen.width - w - 16f, y = 14f;
            GUI.Label(new Rect(x, y, w, 20f), $"{Score}  ·  {Mathf.CeilToInt(TimeLeft)}s", _small);
            y += 20f;
            Bar(new Rect(x, y, w, 6f), Hunger01, new Color(0.45f, 0.9f, 0.45f), white);
            if (p.lightHurts)
            {
                y += 9f;
                Bar(new Rect(x, y, w, 6f), Budget01, InLight ? new Color(1f, 0.4f, 0.2f) : new Color(1f, 0.7f, 0.25f), white);
            }

            if (DebugHud)
                GUI.Label(new Rect(x - 260f, y + 12f, w + 260f, 60f),
                    $"{p.displayName} | lux {LuxAtBat:F2} {(InLight ? "LIT" : "dark")} | hunger {Hunger01 * 100f:F0}% budget {Budget01 * 100f:F0}%\n[1][2] variant  [R] restart  [E] ping  [H] HUD", _small);

            if (CurrentState == State.Playing)
            {
                float age = Time.unscaledTime - _playStart;
                if (age < hintFadeSeconds)
                {
                    GUI.color = new Color(1f, 1f, 1f, 1f - age / hintFadeSeconds);
                    GUI.Label(new Rect(0f, Screen.height - 60f, Screen.width, 24f),
                        $"{p.displayName} — fly through moths to eat" + (p.EchoEnabled ? "   ·   E: echolocate" : ""), _wrap);
                    GUI.color = Color.white;
                }
                return;
            }

            Rect panel = new Rect(Screen.width * 0.5f - 300f, Screen.height * 0.5f - 110f, 600f, 220f);
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(panel, white);
            GUI.color = Color.white;
            if (CurrentState == State.StartScreen)
            {
                GUI.Label(new Rect(panel.x, panel.y + 15f, panel.width, 36f), "Night Feeding", _big);
                GUI.Label(new Rect(panel.x + 30f, panel.y + 60f, panel.width - 60f, 150f),
                    $"{p.speciesBlurb}\n\nFly through moths to eat before hunger runs out.\n" +
                    "[1] light-tolerant   [2] light-averse   [R] restart   [E] echolocate   [H] HUD\n\n" +
                    "SPACE / ENTER to start", _wrap);
            }
            else
            {
                GUI.Label(new Rect(panel.x, panel.y + 30f, panel.width, 40f), $"{Score} insects", _big);
                GUI.Label(new Rect(panel.x + 30f, panel.y + 85f, panel.width - 60f, 120f),
                    $"{p.endMessage}\n\nSPACE replay   ·   1 / 2 switch bat", _wrap);
            }
        }

        static void Bar(Rect r, float v, Color c, Texture2D white)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.5f); GUI.DrawTexture(r, white);
            GUI.color = c; GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(v), r.height), white);
            GUI.color = Color.white;
        }
    }
}
