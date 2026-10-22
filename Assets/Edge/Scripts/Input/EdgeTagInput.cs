using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Edge.Input
{
    public enum TagAxis { Roll, Pitch, Yaw, GyroX, GyroY, GyroZ }

    // Turns "ORI ..." UDP packets (UdpLink thread -> main thread) into EdgeTag
    // input-state events. Calibration, remap, deadzone, filtering and packet-loss
    // handling all live here so every consumer sees clean -1..1 steer/climb.
    //   ORI <roll> <pitch> <yaw> [gx gy gz btn seq]   degrees, deg/s; ~100 Hz
    [DefaultExecutionOrder(-50)]
    public class EdgeTagInput : MonoBehaviour
    {
        public UdpLink link;

        [Header("Axis remap (MPU mounting is unknown: pick which tag axis drives what)")]
        public TagAxis steerSource = TagAxis.Roll;
        public bool invertSteer = false;
        public TagAxis climbSource = TagAxis.Pitch;
        public bool invertClimb = false;

        [Header("Range / feel")]
        [Tooltip("Tag tilt (deg) that gives full steer")] public float steerFullScaleDeg = 35f;
        [Tooltip("Tag tilt (deg) that gives full climb")] public float climbFullScaleDeg = 30f;
        [Range(0f, 0.5f)] public float deadzone = 0.06f;
        [Range(0.2f, 3f)] public float sensitivity = 1f;
        [Tooltip(">1 = finer near centre, 1 = linear")] [Range(1f, 3f)] public float responseCurve = 1.3f;

        [Header("One Euro filter (low = smoother at rest, beta = less lag when moving)")]
        public float minCutoffHz = 2.0f;
        public float beta = 0.08f;

        [Header("Packet loss")]
        [Tooltip("Hold last value this long after the last packet (s)")] public float holdTime = 0.15f;
        [Tooltip("Then decay to neutral over this long (s)")] public float decayTime = 0.35f;
        [Tooltip("Tag counts as connected while packets arrived within this window (s)")] public float activeWindow = 0.5f;

        [Header("Calibration")]
        public Key zeroKey = Key.Z;   // C is the camera toggle
        public bool zeroOnFirstPacket = true;

        public float NeutralSteerDeg { get; private set; }
        public float NeutralClimbDeg { get; private set; }
        public float PacketRateHz { get; private set; }
        public int PacketsTotal { get; private set; }
        public int SeqDrops { get; private set; }
        public bool Connected => Time.unscaledTime - _lastPacket < activeWindow;
        public float LastSteer { get; private set; }
        public float LastClimb { get; private set; }
        public string Status => Paused ? "TAG paused (press D2 to stream)" : PacketsTotal == 0 ? "TAG: no packets on UDP " + (link ? link.listenPort : 0)
            : $"TAG {(Connected ? "OK" : "LOST")} {PacketRateHz:F0} Hz drops {SeqDrops}  steer {LastSteer:+0.00;-0.00} climb {LastClimb:+0.00;-0.00}";

        EdgeTag _device;
        readonly OneEuroFilter _fs = new OneEuroFilter(), _fc = new OneEuroFilter();
        float[] _raw = new float[6];
        uint _btn;
        bool _wasConnected, _btnPrev, _haveData, _gotNew;
        long _lastSeq = -1;
        float _lastPacket = -10f, _rateWin, _rateT;
        int _rateCount;
        float _heldSteer, _heldClimb;

        void OnEnable()
        {
            _device = EdgeTag.AddOrGet();
            if (!link) link = FindAnyObjectByType<UdpLink>();
            if (link) link.OnMessage += Handle;
        }

        void OnDisable()
        {
            if (link) link.OnMessage -= Handle;
            if (_device != null && _device.added) InputSystem.RemoveDevice(_device);
            _device = null;
        }

        /// Latest BH1750 reading from "LUX <lux>" (~10 Hz), or -1 if none yet. Display only.
        public float LatestLux { get; private set; } = -1f;
        public bool LuxFresh => Time.unscaledTime - _lastLux < 1f;
        /// Tag is alive but paused (button D2): sending "HELLO ..." 1 Hz instead of ORI.
        public bool Paused => Time.unscaledTime - _lastHello < 2.5f && !Connected;
        float _lastLux = -10f, _lastHello = -10f;

        void Handle(string msg)
        {
            if (msg.StartsWith("LUX "))
            {
                if (float.TryParse(msg.Substring(4), NumberStyles.Float, CultureInfo.InvariantCulture, out float lux))
                { LatestLux = lux; _lastLux = Time.unscaledTime; }
                return;
            }
            if (msg.StartsWith("HELLO")) { _lastHello = Time.unscaledTime; return; }
            if (!msg.StartsWith("ORI ")) return;
            var p = msg.Split(' ');
            if (p.Length < 4) return;
            var ci = CultureInfo.InvariantCulture;
            for (int i = 0; i < 6; i++)
            {
                if (i + 1 >= p.Length) { if (i >= 3) _raw[i] = 0f; continue; }
                if (!float.TryParse(p[i + 1], NumberStyles.Float, ci, out float v)) { if (i < 3) return; v = 0f; }
                _raw[i] = v;
            }
            _btn = p.Length > 7 && p[7] == "1" ? 1u : 0u;
            if (p.Length > 8 && long.TryParse(p[8], NumberStyles.Integer, ci, out long seq))
            {
                if (_lastSeq >= 0 && seq > _lastSeq + 1 && seq - _lastSeq < 1000) SeqDrops += (int)(seq - _lastSeq - 1);
                _lastSeq = seq;
            }
            if (!_haveData && zeroOnFirstPacket) { _haveData = true; Zero(); }
            _haveData = true;
            _gotNew = true;
            _lastPacket = Time.unscaledTime;
            PacketsTotal++;
            _rateCount++;
        }

        /// Capture the current pose as neutral (key Z, tag button, or call from code).
        public void Zero()
        {
            NeutralSteerDeg = _raw[(int)steerSource];
            NeutralClimbDeg = _raw[(int)climbSource];
            _fs.Reset(0f); _fc.Reset(0f);
            Debug.Log($"[EDGE] tag zeroed: steer neutral {NeutralSteerDeg:F1} deg, climb neutral {NeutralClimbDeg:F1} deg");
        }

        static float Wrap(float d) { d %= 360f; if (d > 180f) d -= 360f; if (d < -180f) d += 360f; return d; }

        float Shape(float deg, float fullScale, bool invert)
        {
            float v = Mathf.Clamp(deg / Mathf.Max(1f, fullScale) * sensitivity, -1f, 1f);
            float a = Mathf.Abs(v);
            a = a <= deadzone ? 0f : (a - deadzone) / (1f - deadzone);
            a = Mathf.Pow(a, responseCurve);
            v = Mathf.Sign(v) * a;
            return invert ? -v : v;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            var kb = Keyboard.current;
            bool btn = _btn != 0;
            if ((kb != null && kb[zeroKey].wasPressedThisFrame) || (btn && !_btnPrev)) Zero();
            _btnPrev = btn;

            _rateT += dt;
            if (_rateT >= 0.5f) { PacketRateHz = _rateCount / _rateT; _rateCount = 0; _rateT = 0f; }

            bool c = Connected;
            if (c != _wasConnected) { _wasConnected = c; Debug.Log(c ? $"[EDGE] tag connected ({link?.listenPort})" : "[EDGE] tag LOST - holding then decaying to neutral; keyboard still works"); }

            if (!_haveData || _device == null) return;

            float age = Time.unscaledTime - _lastPacket;
            float steer, climb;
            if (age <= holdTime)
            {
                float sDeg = Wrap(_raw[(int)steerSource] - NeutralSteerDeg);
                float cDeg = Wrap(_raw[(int)climbSource] - NeutralClimbDeg);
                bool sIsRate = steerSource >= TagAxis.GyroX, cIsRate = climbSource >= TagAxis.GyroX;
                // gyro axes: deg/s, full scale = 6x the tilt scale (≈ 200 deg/s)
                steer = _fs.Filter(Shape(sDeg, steerFullScaleDeg * (sIsRate ? 6f : 1f), invertSteer), dt);
                climb = _fc.Filter(Shape(cDeg, climbFullScaleDeg * (cIsRate ? 6f : 1f), invertClimb), dt);
                _fs.minCutoff = _fc.minCutoff = minCutoffHz; _fs.beta = _fc.beta = beta;
                _heldSteer = steer; _heldClimb = climb;
            }
            else
            {
                // packet loss: hold, then decay to neutral
                float k = decayTime <= 0f ? 0f : Mathf.Clamp01(1f - (age - holdTime) / decayTime);
                steer = _heldSteer * k; climb = _heldClimb * k;
                if (k <= 0f && !_gotNew) { _fs.Reset(0f); _fc.Reset(0f); }
            }
            _gotNew = false;
            LastSteer = steer; LastClimb = climb;

            InputSystem.QueueStateEvent(_device, new EdgeTagState
            {
                steer = steer, climb = climb,
                roll = _raw[0], pitch = _raw[1], yaw = _raw[2],
                gyro = new Vector3(_raw[3], _raw[4], _raw[5]),
                buttons = _btn,
            });
        }
    }
}
