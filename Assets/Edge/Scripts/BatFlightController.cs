using System.Globalization;
using Edge.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Edge
{
    // Flies the bat from the tag's IMU (EdgeTag Input System device, fed by
    // EdgeTagInput from UdpLink) with keyboard/gamepad on the same actions.
    // Disorientation is dose-driven (see DoseModel) and is also sent back to
    // the tag as vibration, ~50 Hz.
    //
    // Flight model: steer -> target bank angle (critically damped, rate-limited)
    // -> coordinated-turn yaw rate (g*tan(bank)/v) -> heading. Climb -> target
    // pitch (damped) -> vertical speed.
    public class BatFlightController : MonoBehaviour
    {
        public enum InputSource { InputActions, LegacyUdpDirect }

        public UdpLink link;
        public DoseModel dose;
        public float cruiseSpeed = EdgeConfig.CruiseSpeed;
        public float minAltitude = 0.6f;   // m; EdgeSceneBuilder sets 1 m
        public float maxAltitude = 30f;    // m; EdgeSceneBuilder sets 10 m (N. gouldi foraging height)

        [Header("Input")]
        [Tooltip("InputActions = EdgeTag + keyboard/gamepad via EdgeControls. LegacyUdpDirect = the old raw ORI path (fallback switch).")]
        public InputSource inputSource = InputSource.InputActions;
        [Tooltip("Assets/Edge/Input/EdgeControls.inputactions; if empty, identical bindings are built in code")]
        public InputActionAsset actions;
        public EdgeTagInput tagInput;

        [Header("Turning")]
        [Tooltip("Bank angle at full steer (deg)")] public float maxBankDeg = 45f;
        [Tooltip("Bank smoothing when flying the tag (s); tag is already One-Euro filtered")] public float tagBankResponse = 0.06f;
        [Tooltip("Bank smoothing on keyboard/gamepad (s); ramps the digital keys")] public float keyBankResponse = 0.16f;
        [Tooltip("Max roll rate (deg/s)")] public float maxRollRate = 240f;
        [Tooltip("Scales the physical coordinated-turn rate g*tan(bank)/v")] public float turnGain = 0.75f;
        [Tooltip("Yaw-rate cap (deg/s)")] public float maxYawRate = 100f;

        [Header("Climb")]
        public float maxPitchDeg = 30f;
        public float pitchResponse = 0.12f;
        [Tooltip("Vertical speed = sin(pitch) * cruise * gain")] public float climbGain = 1.2f;
        [Range(0f, 1f)] public float visualPitchScale = 0.5f;

        [Header("External modifiers (EdgeFeedingGame); defaults = original behaviour")]
        [Tooltip("Multiplies cruise speed")] public float speedMultiplier = 1f;
        [Tooltip("Added to dose instability (0..1) for heading noise")] [Range(0f, 1f)] public float extraInstability01 = 0f;
        [Tooltip("Ignore DoseModel instability (light-tolerant variant)")] public bool ignoreDoseInstability = false;
        [Tooltip("Extra vibration/LED level sent to the tag, max-combined with instability")] [Range(0f, 1f)] public float extraVib01 = 0f;

        [Header("Collision (sphere-cast against obstacles; ground/path included)")]
        public bool collisions = true;
        [Tooltip("Bat collision radius (m). N. gouldi wingspan ~0.25-0.3 m; a bit smaller feels fair")]
        public float batRadius = 0.12f;
        [Tooltip("Layers that block the bat. Empty = EdgeObstacle + Default")] public LayerMask collisionMask;
        [Tooltip("Push-off speed after a hit (m/s)")] public float bounceSpeed = 1.5f;
        [Tooltip("Heading turned away from the surface on a hit (deg)")] public float bumpTurnDeg = 35f;
        [Tooltip("Stun after a hit (s): adds instability, slows")] public float stunSeconds = 0.45f;
        [Range(0f, 1f)] public float stunInstability = 0.5f;
        [Range(0.2f, 1f)] public float stunSpeedMultiplier = 0.6f;
        [Range(0f, 1f)] public float bumpVib = 1f;
        public float bumpVibSeconds = 0.18f;
        public bool bumpSound = true;
        [Tooltip("Min time between bump events (s)")] public float bumpCooldown = 0.3f;
        [Tooltip("Keep this far above the ground surface (m)")] public float groundClearance = 0.15f;

        /// Fired on a hard hit: point, normal, collider. EdgeFeedingGame uses it for the hunger cost.
        public event System.Action<Vector3, Vector3, Collider> Bumped;
        public bool InFoliage { get; private set; }
        public bool Stunned => Time.time < _stunUntil;

        float _stunUntil = -10f, _bumpVibUntil = -10f, _lastBump = -10f;
        Vector3 _bounce;
        SphereCollider _probe;
        AudioSource _thudSrc;
        AudioClip _thud;
        readonly Collider[] _overlap = new Collider[16];
        readonly RaycastHit[] _hits = new RaycastHit[8];

        // legacy raw path
        float _tagRoll, _tagPitch, _lastOriTime = -10f;

        float _heading, _bank, _bankVel, _pitch, _pitchVel;
        float _vibTimer, _noiseT, _lastVib = -1f;
        InputAction _steer, _climb;

        public float YawRateDegPerSec { get; private set; }
        public float BankDeg => _bank;

        public bool TagActive => inputSource == InputSource.LegacyUdpDirect
            ? Time.time - _lastOriTime < 0.5f
            : tagInput && tagInput.Connected;

        void OnValidate()
        {
#if UNITY_EDITOR
            if (!actions)
                actions = UnityEditor.AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/Edge/Input/EdgeControls.inputactions");
#endif
        }

        void Awake()
        {
            if (!tagInput) tagInput = FindAnyObjectByType<EdgeTagInput>();
            if (!tagInput)
            {
                tagInput = gameObject.AddComponent<EdgeTagInput>();
                tagInput.link = link;
            }
            if (!actions) actions = BuildDefaultActions();
            SetupCollision();
            var map = actions.FindActionMap("Fly", true);
            _steer = map.FindAction("Steer", true);
            _climb = map.FindAction("Climb", true);
            // "Zero" (Z / tag button / gamepad Select) is handled by EdgeTagInput itself.
        }

        void SetupCollision()
        {
            if (collisionMask.value == 0)
            {
                int l = LayerMask.NameToLayer("EdgeObstacle");
                collisionMask = (1 << 0) | (l >= 0 ? 1 << l : 0);
            }
            var go = new GameObject("BatCollisionProbe") { layer = 2 };   // Ignore Raycast: never hit by our own casts
            go.transform.SetParent(transform, false);
            _probe = go.AddComponent<SphereCollider>();
            _probe.isTrigger = true;
            _probe.radius = batRadius;
            collisionMask &= ~(1 << 2);
            if (bumpSound)
            {
                _thudSrc = gameObject.AddComponent<AudioSource>();
                _thudSrc.playOnAwake = false; _thudSrc.spatialBlend = 0f; _thudSrc.volume = 0.6f;
                const int sr = 22050, n = sr / 8;
                var d = new float[n];
                var r = new System.Random(3);
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)sr, env = Mathf.Exp(-t * 28f);
                    d[i] = env * (0.8f * Mathf.Sin(2f * Mathf.PI * (90f - 40f * t * 8f) * t) + 0.25f * ((float)r.NextDouble() * 2f - 1f));
                }
                _thud = AudioClip.Create("EdgeThud", n, 1, sr, false);
                _thud.SetData(d, 0);
            }
        }

        // Moves from 'from' toward 'to' without passing through obstacles: sphere-cast, slide along
        // the hit surface (2 iterations), then depenetrate and keep ground clearance.
        Vector3 ResolveMove(Vector3 from, Vector3 to, ref Vector3 fwd)
        {
            Vector3 delta = to - from;
            Vector3 pos = from;
            const float skin = 0.02f;
            for (int iter = 0; iter < 3 && delta.sqrMagnitude > 1e-8f; iter++)
            {
                float dist = delta.magnitude;
                Vector3 dir = delta / dist;
                int n = Physics.SphereCastNonAlloc(pos, batRadius, dir, _hits, dist + skin, collisionMask, QueryTriggerInteraction.Ignore);
                int best = -1; float bestD = float.MaxValue;
                for (int i = 0; i < n; i++)
                    if (_hits[i].distance < bestD && _hits[i].collider != _probe && _hits[i].distance > 0f) { bestD = _hits[i].distance; best = i; }
                if (best < 0) { pos += delta; break; }
                var h = _hits[best];
                float move = Mathf.Max(0f, h.distance - skin);
                pos += dir * move;
                Vector3 rem = delta - dir * move;
                delta = Vector3.ProjectOnPlane(rem, h.normal);   // slide
                OnBump(h.point, h.normal, h.collider, ref fwd);
            }
            // depenetration safety
            if (_probe)
            {
                int n = Physics.OverlapSphereNonAlloc(pos, batRadius, _overlap, collisionMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var c = _overlap[i];
                    if (c == _probe) continue;
                    if (Physics.ComputePenetration(_probe, pos, Quaternion.identity, c, c.transform.position, c.transform.rotation, out Vector3 pd, out float pdist))
                        pos += pd * (pdist + 0.005f);
                }
            }
            // ground: never below the surface
            // (short ray from just above the bat so lamp heads/branches overhead are never treated as ground)
            if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out var g, 3f, collisionMask, QueryTriggerInteraction.Ignore))
                pos.y = Mathf.Max(pos.y, g.point.y + batRadius + groundClearance);
            return pos;
        }

        void OnBump(Vector3 point, Vector3 normal, Collider col, ref Vector3 fwd)
        {
            // Ground grazes (normal mostly up) just slide - no stun, no turn.
            if (normal.y > 0.7f) return;
            _bounce = normal * bounceSpeed;
            if (Time.time - _lastBump < bumpCooldown) return;
            _lastBump = Time.time;
            Vector3 nh = Vector3.ProjectOnPlane(normal, Vector3.up);
            if (nh.sqrMagnitude > 1e-4f)
            {
                Vector3 refl = Vector3.Reflect(Vector3.ProjectOnPlane(fwd, Vector3.up), nh.normalized);
                float want = Mathf.Atan2(refl.x, refl.z) * Mathf.Rad2Deg;
                _heading = Mathf.MoveTowardsAngle(_heading, want, bumpTurnDeg);
                fwd = Quaternion.Euler(0f, _heading, 0f) * Vector3.forward;
                _bankVel += Mathf.Sign(Mathf.DeltaAngle(_heading, want)) * 120f;   // little wobble
            }
            _stunUntil = Time.time + stunSeconds;
            _bumpVibUntil = Time.time + bumpVibSeconds;
            if (_thudSrc && _thud) { _thudSrc.pitch = Random.Range(0.85f, 1.15f); _thudSrc.PlayOneShot(_thud); }
            Bumped?.Invoke(point, normal, col);
        }

        float FoliageCheck(Vector3 pos, out float speedMul)
        {
            speedMul = 1f;
            InFoliage = false;
            int n = Physics.OverlapSphereNonAlloc(pos, batRadius, _overlap, ~(1 << 2), QueryTriggerInteraction.Collide);
            float inst = 0f;
            for (int i = 0; i < n; i++)
            {
                if (!_overlap[i].isTrigger) continue;
                var f = _overlap[i].GetComponent<EdgeFoliage>();
                if (!f) continue;
                InFoliage = true;
                speedMul = Mathf.Min(speedMul, f.speedMultiplier);
                inst = Mathf.Max(inst, f.instability);
            }
            return inst;
        }

        void OnEnable() { actions?.FindActionMap("Fly")?.Enable(); }
        void OnDisable() { actions?.FindActionMap("Fly")?.Disable(); }

        void Start()
        {
            _heading = transform.eulerAngles.y;
            if (link) link.OnMessage += Handle;
        }

        void OnDestroy()
        {
            if (link) link.OnMessage -= Handle;
        }

        /// Teleport and level the bat (used by EdgeFeedingGame on restart).
        public void ResetPose(Vector3 position, float headingDeg)
        {
            transform.position = position;
            _heading = headingDeg;
            _bank = _bankVel = _pitch = _pitchVel = 0f;
            transform.rotation = Quaternion.Euler(0f, _heading, 0f);
        }

        static InputActionAsset BuildDefaultActions()
        {
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            var map = asset.AddActionMap("Fly");
            var steer = map.AddAction("Steer", InputActionType.Value, expectedControlLayout: "Axis");
            steer.AddBinding("<EdgeTag>/steer");
            steer.AddBinding("<Gamepad>/leftStick/x");
            steer.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/a").With("Positive", "<Keyboard>/d");
            steer.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/leftArrow").With("Positive", "<Keyboard>/rightArrow");
            var climb = map.AddAction("Climb", InputActionType.Value, expectedControlLayout: "Axis");
            climb.AddBinding("<EdgeTag>/climb");
            climb.AddBinding("<Gamepad>/leftStick/y");
            climb.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/s").With("Positive", "<Keyboard>/w");
            climb.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/downArrow").With("Positive", "<Keyboard>/upArrow");
            var zero = map.AddAction("Zero", InputActionType.Button);
            zero.AddBinding("<EdgeTag>/zero");
            zero.AddBinding("<Keyboard>/z");
            zero.AddBinding("<Gamepad>/select");
            return asset;
        }

        void Handle(string msg)
        {
            // legacy path only: ORI <roll> <pitch> <yaw> [...] degrees, ~100 Hz
            if (inputSource != InputSource.LegacyUdpDirect) return;
            var p = msg.Split(' ');
            if (p.Length >= 4 && p[0] == "ORI" &&
                float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float pt))
            {
                _tagRoll = r;
                _tagPitch = pt;
                _lastOriTime = Time.time;
            }
        }

        void ReadInput(out float steer, out float climb)
        {
            if (inputSource == InputSource.LegacyUdpDirect)
            {
                if (Time.time - _lastOriTime < 0.5f)
                {
                    steer = Mathf.Clamp(_tagRoll / 35f, -1f, 1f);
                    climb = Mathf.Clamp(_tagPitch / 45f, -1f, 1f);
                    return;
                }
                KeyboardFallback(out steer, out climb);
                return;
            }
            steer = Mathf.Clamp(_steer.ReadValue<float>(), -1f, 1f);
            climb = Mathf.Clamp(_climb.ReadValue<float>(), -1f, 1f);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            ReadInput(out float steer, out float climb);

            // bank: critically damped toward target, roll-rate limited
            float bankResponse = TagActive ? tagBankResponse : keyBankResponse;
            _bank = Mathf.SmoothDamp(_bank, steer * maxBankDeg, ref _bankVel, bankResponse, maxRollRate, dt);

            // coordinated turn: yaw rate follows bank
            float v = Mathf.Max(1f, cruiseSpeed);
            float yawRate = Mathf.Rad2Deg * (9.81f * Mathf.Tan(_bank * Mathf.Deg2Rad) / v) * turnGain;
            YawRateDegPerSec = Mathf.Clamp(yawRate, -maxYawRate, maxYawRate);
            _heading += YawRateDegPerSec * dt;

            // Disorientation: amplitude is the stated function of dose
            // (FINAL_PLAN section 3.2). Perlin is only the carrier waveform.
            float inst = (dose && !ignoreDoseInstability ? dose.Instability01 : 0f);
            float folSpeed = 1f;
            if (collisions) inst += FoliageCheck(transform.position, out folSpeed);
            float stunK = Stunned ? Mathf.Clamp01((_stunUntil - Time.time) / Mathf.Max(0.01f, stunSeconds)) : 0f;
            inst += stunK * stunInstability;
            inst = Mathf.Clamp01(inst + extraInstability01);
            float vibOut = Mathf.Max(inst, extraVib01);
            if (Time.time < _bumpVibUntil) vibOut = Mathf.Max(vibOut, bumpVib);
            _noiseT += dt;
            float n = Mathf.PerlinNoise(_noiseT * 2f, 0.5f) * 2f - 1f;
            _heading += n * inst * EdgeConfig.YawNoiseMaxDegPerSec * dt;

            _pitch = Mathf.SmoothDamp(_pitch, climb * maxPitchDeg, ref _pitchVel, pitchResponse, Mathf.Infinity, dt);
            Vector3 fwd = Quaternion.Euler(0f, _heading, 0f) * Vector3.forward;
            float vy = Mathf.Sin(_pitch * Mathf.Deg2Rad) * climbGain;
            float spd = cruiseSpeed * speedMultiplier * folSpeed * Mathf.Lerp(1f, stunSpeedMultiplier, stunK);
            Vector3 pos = transform.position + (fwd + Vector3.up * vy) * (spd * dt) + _bounce * dt;
            _bounce *= Mathf.Exp(-6f * dt);
            pos.y = Mathf.Clamp(pos.y, minAltitude, maxAltitude);
            pos.x = Mathf.Clamp(pos.x, -100f, 100f);
            pos.z = Mathf.Clamp(pos.z, -100f, 100f);
            if (collisions) pos = ResolveMove(transform.position, pos, ref fwd);
            transform.position = pos;
            transform.rotation = Quaternion.Euler(-_pitch * visualPitchScale, _heading, -_bank);

            // instability back to the tag as vibration, ~50 Hz (BUILD_SPEC 8.1)
            _vibTimer += dt;
            // PERF: only send when the value changed (or 1 Hz keep-alive) - avoids 50 string allocs/s.
            if (link && _vibTimer >= 0.02f)
            {
                if (Mathf.Abs(vibOut - _lastVib) > 0.0005f || _vibTimer >= 1f)
                {
                    _vibTimer = 0f;
                    _lastVib = vibOut;
                    link.Send($"VIB {vibOut.ToString("F3", CultureInfo.InvariantCulture)}");
                }
            }
        }

        static void KeyboardFallback(out float steer, out float climb)
        {
            steer = 0f;
            climb = 0f;
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.leftArrowKey.isPressed || kb.aKey.isPressed) steer -= 1f;
            if (kb.rightArrowKey.isPressed || kb.dKey.isPressed) steer += 1f;
            if (kb.upArrowKey.isPressed || kb.wKey.isPressed) climb += 1f;
            if (kb.downArrowKey.isPressed || kb.sKey.isPressed) climb -= 1f;
        }
    }
}
