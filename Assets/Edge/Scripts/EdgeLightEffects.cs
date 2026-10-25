using System;
using System.Globalization;
using UnityEngine;

namespace Edge
{
    // What light does to the bat, per kind of light (project update, "How light affects the bat"):
    //   street lamp       steady haze, lower contrast near the lamp      weak continuous vibration
    //   torch/bike light  a glare that moves with the beam               pulsing vibration
    //   phone flash       sudden white-out + shake, fading over ~2.5 s   one strong buzz
    // The same mapping is used for light in the game (LampZone, EdgeBeam, flash photos from
    // EdgeBenchSitter) and light in the real room: the tag's classifier sends
    // "CLS <none|lamp|bike|torch|flash> [confidence]"; until it ships, a sudden jump in the tag's
    // "LUX" stream counts as a phone flash.
    //
    // Safety (risk table): nothing strobes. Overlays only fade, flashes are rate-limited and capped
    // below full white. Vibration and steering instability go to BatFlightController
    // (lightVib01 / lightInstability01, max-combined), so the DoseModel is untouched.
    // `scale` is set per bat by EdgeFeedingGame: the light-tolerant bat barely notices light.
    [DefaultExecutionOrder(1000)]   // LateUpdate after CameraToggle, so the shake sits on the final camera pose
    public class EdgeLightEffects : MonoBehaviour
    {
        public static EdgeLightEffects Instance { get; private set; }

        [Header("Scene refs (auto-found if empty)")]
        public BatFlightController flight;
        public CameraToggle cams;
        public UdpLink link;
        public LampZone[] lamps;

        [Header("Strength (EdgeFeedingGame sets this per bat)")]
        [Range(0f, 1f)] public float scale = 1f;

        [Header("Street lamp: haze")]
        public Color hazeColor = new Color(1f, 0.74f, 0.42f);
        [Range(0f, 1f)] public float hazeMaxAlpha = 0.32f;
        [Tooltip("Lux at the bat that gives ~63% of full haze")] public float hazeLux = 1.5f;
        [Range(0f, 1f)] public float lampGlareAlpha = 0.35f;
        [Range(0f, 1f)] public float lampVib = 0.22f;

        [Header("Torch / bike light: glare")]
        public Color glareColor = new Color(1f, 0.97f, 0.9f);
        [Range(0f, 1f)] public float glareMaxAlpha = 0.8f;
        [Range(0f, 1f)] public float beamVib = 0.7f;
        public float beamPulseHz = 4f;
        [Range(0f, 1f)] public float beamInstability = 0.45f;
        [Tooltip("Trees and posts block beams (one linecast per lit beam)")] public bool beamOcclusion = true;
        [Tooltip("How fast each beam's glare fades in/out (per s), so a trunk passing through a beam or a wavering classifier never strobes")]
        public float glareFadePerSec = 3f;

        [Header("Phone flash: white-out")]
        public Color flashColor = new Color(1f, 1f, 0.97f);
        [Tooltip("Peak overlay alpha; below 1 so a projector never goes fully white")]
        [Range(0f, 1f)] public float flashMaxAlpha = 0.85f;
        public float flashHold = 0.08f;
        public float flashFade = 2.5f;
        [Tooltip("Min seconds between flashes: no strobing")] public float flashMinInterval = 3f;
        public float flashBuzzSeconds = 0.3f;
        [Range(0f, 1f)] public float flashInstability = 0.8f;

        [Header("Camera shake (flash and bumps)")]
        public float flashShakeDeg = 2.2f;
        public float bumpShakeDeg = 1.2f;
        public float shakeHz = 9f;

        [Header("Real light from the tag")]
        public bool listenForClassifier = true;
        [Tooltip("Until the classifier sends CLS: a sudden LUX jump on the tag counts as a phone flash")]
        public bool luxFlashDetect = true;
        public float luxFlashMin = 150f;
        public float luxFlashRatio = 3f;
        [Tooltip("A class is dropped this long after its last CLS message (s)")] public float classTimeout = 0.6f;

        public float Haze01 { get; private set; }
        public float Beam01 { get; private set; }
        public float Flash01 { get; private set; }
        public bool InBeam => Beam01 > 0.12f;
        public bool Flashing => Flash01 > 0.1f;
        public string RealClass { get; private set; } = "none";

        /// "lamp", "torch", "bike" or "flash", once at the onset of each exposure (narration, stats).
        public event Action<string> LightHit;

        struct Glow { public Vector2 screen; public float k; }
        readonly Glow[] _glows = new Glow[16];
        int _glowN;
        Texture2D _glowTex, _streakTex;
        float _flashAt = -100f, _flashK, _buzzUntil = -100f, _bumpAt = -100f;
        float _luxBase = -1f, _lastLux, _realAt = -100f, _realConf, _realBeam;
        string _beamKind = "torch";
        bool _wasBeam, _wasLamp;
        Transform _shaken;
        Quaternion _shakeRest;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            // Scenes built before this component existed still get the effects.
            var existing = FindAnyObjectByType<EdgeLightEffects>(FindObjectsInactive.Include);
            if (existing)
            {
                if (!existing.isActiveAndEnabled) Debug.LogWarning($"[EDGE] EdgeLightEffects on '{existing.name}' is inactive: light effects are off", existing);
                return;
            }
            if (!FindAnyObjectByType<BatFlightController>()) return;
            new GameObject("EdgeLightEffects (auto)").AddComponent<EdgeLightEffects>();
        }

        void Awake()
        {
            Instance = this;
            if (!flight) flight = FindAnyObjectByType<BatFlightController>();
            if (!cams) cams = FindAnyObjectByType<CameraToggle>();
            if (!link) link = flight && flight.link ? flight.link : FindAnyObjectByType<UdpLink>();
            if (lamps == null || lamps.Length == 0) lamps = FindObjectsByType<LampZone>();
            _glowTex = MakeGlow(128, 1f);
            _streakTex = MakeGlow(128, 0.12f);
        }

        void OnEnable()
        {
            if (link) link.OnMessage += Handle;
            if (flight) flight.Bumped += OnBumped;
        }

        void OnDisable()
        {
            if (link) link.OnMessage -= Handle;
            if (flight) { flight.Bumped -= OnBumped; flight.lightVib01 = 0f; flight.lightInstability01 = 0f; }
            Unshake();
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void OnBumped(Vector3 p, Vector3 n, Collider c) => _bumpAt = Time.time;

        /// A phone flash: white-out, shake, one strong buzz. strength 0..1 (distance etc.).
        public void Flash(float strength = 1f)
        {
            if (Time.time - _flashAt < flashMinInterval || strength <= 0.02f) return;
            _flashAt = Time.time;
            _flashK = Mathf.Clamp01(strength);
            _buzzUntil = Time.time + flashBuzzSeconds;
            LightHit?.Invoke("flash");
        }

        /// Real-room light class, as the tag's classifier reports it: none | lamp | bike | torch | flash.
        /// holdSeconds > 0 keeps it without further messages (previewing from the CLI / inspector).
        public void SetRealClass(string cls, float conf = 1f, float holdSeconds = 0f)
        {
            cls = (cls ?? "none").ToLowerInvariant();
            if (cls == "phone") cls = "flash";
            if (cls == "street" || cls == "streetlamp") cls = "lamp";
            if (cls == "ambient") cls = "none";
            if (cls == "flash" && RealClass != "flash") Flash(conf);
            RealClass = cls; _realConf = Mathf.Clamp01(conf);
            _realAt = Time.time + Mathf.Max(0f, holdSeconds);
        }

        // ---- real light from the tag ---------------------------------------------------------
        void Handle(string msg)
        {
            var ci = CultureInfo.InvariantCulture;
            if (listenForClassifier && msg.StartsWith("CLS "))
            {
                var p = msg.Split(' ');
                string cls = p.Length > 1 ? p[1].ToLowerInvariant() : "none";
                float conf = p.Length > 2 && float.TryParse(p[2], NumberStyles.Float, ci, out float c) ? Mathf.Clamp01(c) : 1f;
                SetRealClass(cls, conf);
                return;
            }
            if (luxFlashDetect && msg.StartsWith("LUX ") &&
                float.TryParse(msg.Substring(4), NumberStyles.Float, ci, out float lux))
            {
                if (_luxBase < 0f) _luxBase = lux;
                float thresh = Mathf.Max(luxFlashMin, _luxBase * luxFlashRatio);
                if (lux >= thresh && _lastLux < thresh) Flash(Mathf.Clamp01(lux / (thresh * 4f)) * 0.5f + 0.5f);
                // ~3 s at 10 Hz below the threshold; slowly above it too, so a room that stays brighter re-arms
                _luxBase = Mathf.Lerp(_luxBase, lux, lux < thresh ? 0.03f : 0.005f);
                _lastLux = lux;
            }
        }

        // ---- per frame -----------------------------------------------------------------------
        Camera ActiveCamera()
        {
            if (cams)
            {
                var c = cams.ThirdPerson ? cams.tpv : cams.fpv;
                if (c && c.isActiveAndEnabled) return c;
            }
            return Camera.main;
        }

        void AddGlow(Camera cam, Vector3 world, float k)
        {
            if (_glowN >= _glows.Length || k <= 0.01f || !cam) return;
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0.1f) return;
            _glows[_glowN++] = new Glow { screen = new Vector2(sp.x, Screen.height - sp.y), k = k };
        }

        void Update()
        {
            Unshake();   // before CameraToggle.LateUpdate reads the camera pose
            if (RealClass != "none" && Time.time - _realAt > classTimeout) RealClass = "none";
        }

        void LateUpdate()
        {
            float t = Time.time, dt = Time.deltaTime;
            Vector3 bat = flight ? flight.transform.position : transform.position;
            var cam = ActiveCamera();

            // flash envelope (hold, then ease out) and the shake first, so glows are projected
            // through the camera as it will render
            float ft = t - _flashAt;
            Flash01 = ft < flashHold ? _flashK : _flashK * Mathf.Pow(Mathf.Clamp01(1f - (ft - flashHold) / Mathf.Max(0.05f, flashFade)), 2f);
            Shake(cam, t);
            _glowN = 0;

            // street lamps: same inverse-square law as LampZone / the feeding game
            float lux = 0f;
            if (lamps != null)
                foreach (var l in lamps)
                {
                    if (!l) continue;
                    Vector3 o = l.measureFrom ? l.measureFrom.position : l.transform.position;
                    float dist = Vector3.Distance(bat, o);
                    if (dist >= l.radius) continue;
                    float dd = Mathf.Max(dist, 0.5f);
                    lux += EdgeConfig.LampPhotLuxAtRef * (EdgeConfig.LampRefDistanceM * EdgeConfig.LampRefDistanceM) / (dd * dd);
                    AddGlow(cam, o, lampGlareAlpha * (1f - dist / l.radius));
                }
            float haze = 1f - Mathf.Exp(-lux / Mathf.Max(0.01f, hazeLux));
            if (RealClass == "lamp") haze = Mathf.Max(haze, 0.6f * _realConf);
            Haze01 = Mathf.MoveTowards(Haze01, haze, 2f * dt);   // eyes adapt, no pops

            // torches and bike lights: each beam's glare eases in and out, never pops
            float beam = 0f;
            foreach (var b in EdgeBeam.All)
            {
                float e = b.Exposure(bat);
                if (e > 0.01f && beamOcclusion && Physics.Linecast(b.transform.position, bat, out var hit, ~(1 << 2), QueryTriggerInteraction.Ignore)
                    && (!flight || !hit.transform.IsChildOf(flight.transform))) e = 0f;
                b.glare = Mathf.MoveTowards(b.glare, e, glareFadePerSec * dt);
                if (b.glare <= 0.01f) continue;
                if (b.glare > beam) { beam = b.glare; _beamKind = b.kind == EdgeBeam.Kind.Bike ? "bike" : "torch"; }
                AddGlow(cam, b.transform.position, b.glare);
            }
            // real beam: no position in the scene, so it sweeps across the view; eased like the others
            bool realBeam = RealClass == "bike" || RealClass == "torch";
            _realBeam = Mathf.MoveTowards(_realBeam, realBeam ? _realConf : 0f, 2f * dt);
            if (_realBeam > 0.01f)
            {
                beam = Mathf.Max(beam, _realBeam);
                if (realBeam) _beamKind = RealClass;
                if (cam && _glowN < _glows.Length)
                    _glows[_glowN++] = new Glow { screen = new Vector2((0.5f + 0.42f * Mathf.Sin(t * 1.3f)) * Screen.width, Screen.height * 0.42f), k = _realBeam };
            }
            Beam01 = Mathf.MoveTowards(Beam01, beam, 4f * dt);

            // onset events
            if (InBeam && !_wasBeam) LightHit?.Invoke(_beamKind);
            _wasBeam = InBeam;
            bool lampNow = Haze01 > 0.25f;
            if (lampNow && !_wasLamp) LightHit?.Invoke("lamp");
            _wasLamp = lampNow;

            // hand: lamp = weak continuous, beam = pulsing, flash = one strong buzz
            float vib = Haze01 > 0.05f ? lampVib * (0.4f + 0.6f * Haze01) : 0f;
            if (Beam01 > 0.05f) vib = Mathf.Max(vib, beamVib * Beam01 * (Mathf.Repeat(t * beamPulseHz, 1f) < 0.5f ? 1f : 0.2f));
            if (t < _buzzUntil) vib = 1f;
            if (flight)
            {
                flight.lightVib01 = vib * scale;
                flight.lightInstability01 = Mathf.Max(Beam01 * beamInstability, Flash01 * flashInstability) * scale;
            }
        }

        void Shake(Camera cam, float t)
        {
            if (!cam) return;
            float amp = flashShakeDeg * Flash01 * scale + bumpShakeDeg * Mathf.Exp(-(t - _bumpAt) / 0.22f);
            if (amp < 0.01f) return;
            float f = t * shakeHz;
            var q = Quaternion.Euler(amp * (Mathf.PerlinNoise(f, 0.3f) * 2f - 1f),
                                     amp * (Mathf.PerlinNoise(0.7f, f) * 2f - 1f),
                                     0.5f * amp * (Mathf.PerlinNoise(f, 5.1f) * 2f - 1f));
            _shaken = cam.transform;
            _shakeRest = _shaken.localRotation;
            _shaken.localRotation = _shakeRest * q;
        }

        void Unshake()
        {
            if (_shaken) _shaken.localRotation = _shakeRest;
            _shaken = null;
        }

        // ---- overlay -------------------------------------------------------------------------
        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;   // scale 0 still shows the flash minimum below
            GUI.depth = 10;   // behind the HUDs (depth 0)
            var full = new Rect(0f, 0f, Screen.width, Screen.height);
            var white = Texture2D.whiteTexture;

            float haze = Haze01 * hazeMaxAlpha * scale;
            if (haze > 0.003f) { GUI.color = new Color(hazeColor.r, hazeColor.g, hazeColor.b, haze); GUI.DrawTexture(full, white); }

            for (int i = 0; i < _glowN; i++)
            {
                var g = _glows[i];
                float a = Mathf.Clamp01(g.k) * glareMaxAlpha * scale;
                if (a < 0.01f) continue;
                float size = Screen.height * (0.18f + 0.55f * Mathf.Clamp01(g.k));
                GUI.color = new Color(glareColor.r, glareColor.g, glareColor.b, a);
                GUI.DrawTexture(new Rect(g.screen.x - size * 0.5f, g.screen.y - size * 0.5f, size, size), _glowTex);
                GUI.color = new Color(glareColor.r, glareColor.g, glareColor.b, a * 0.6f);
                GUI.DrawTexture(new Rect(g.screen.x - size * 1.4f, g.screen.y - size * 0.07f, size * 2.8f, size * 0.14f), _streakTex);
            }
            // a beam also washes the whole view a little, even when its source is off screen
            float wash = Beam01 * 0.25f * scale;
            if (wash > 0.003f) { GUI.color = new Color(glareColor.r, glareColor.g, glareColor.b, wash); GUI.DrawTexture(full, white); }

            float fl = Flash01 * flashMaxAlpha * Mathf.Max(scale, 0.25f);
            if (fl > 0.003f) { GUI.color = new Color(flashColor.r, flashColor.g, flashColor.b, fl); GUI.DrawTexture(full, white); }
            GUI.color = Color.white;
        }

        // Soft round glow; aspect < 1 squashes it vertically into a lens streak.
        static Texture2D MakeGlow(int size, float aspect)
        {
            int h = Mathf.Max(4, Mathf.RoundToInt(size * aspect));
            var tex = new Texture2D(size, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "EdgeGlow" };
            var px = new Color32[size * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / h * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Pow(Mathf.Clamp01(1f - r), 2.4f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
