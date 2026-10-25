using UnityEngine;

namespace Edge
{
    // Riding for the cyclist pedestrian. Sits on the rider's Animator object (OnAnimatorIK only fires
    // there). Ground speed is read from how far the agent actually moved this frame, so it follows
    // EdgePedestrians without a reference to it. Both wheels roll at that speed; the feet ride a virtual
    // crank at the bottom bracket (the low-poly bike has no separate crank mesh) geared down from the
    // wheel; the hands hold the grips. Needs IK Pass on the controller's base layer (the builder sets it).
    // All positions are in the agent's local frame (+Z = travel, +X = rider's right), set by the builder.
    //
    // Rider pose (seatRider): IK alone only pins hands and feet, so whatever the base clip does with the
    // hips and spine showed through (a rider hovering or standing over the bike). Here the IK pass also
    // places the body: hip joints on the seat point, pelvis/torso leaned toward the bars, a little hip
    // rock with the stroke, head looking up the path. The seat point and lean are derived from the
    // rider's own leg/arm/torso lengths (knee slightly bent at the bottom of the stroke, elbows slightly
    // bent at the grips) unless set by hand. Feet "ankle" through the stroke, elbows point out and down,
    // and every so often the rider stops pedalling and coasts with the pedals level.
    // EdgePedestrians banks the whole agent into turns, so bike and rider lean together.
    [RequireComponent(typeof(Animator))]
    public class EdgeCyclist : MonoBehaviour
    {
        public Transform agent;                 // moving root driven by EdgePedestrians
        public Transform[] wheels;              // pivots at the hub centres
        public float wheelRadius = 0.34f;       // m
        public Vector3 crankCentre;             // bottom bracket
        public float crankRadius = 0.17f;       // m
        public float pedalHalfWidth = 0.12f;    // m from the bike's centre plane to each pedal
        public float gearRatio = 2.2f;          // wheel revolutions per crank revolution: ~70 rpm at 5.4 m/s on a 0.34 m wheel
        public Vector3 ankleAbovePedal = new Vector3(0f, 0.07f, -0.06f); // ball of the foot on the spindle
        public Vector3 gripLeft, gripRight;

        [Header("Rider pose")]
        [Tooltip("Place hips on the seat and lean the torso to the bars (off = hands/feet IK only, as before)")]
        public bool seatRider = true;
        [Tooltip("Hip-joint midpoint in agent space. Zero = derived from the crank and the rider's leg length")]
        public Vector3 seatHip;
        [Tooltip("Hip-to-ankle distance at the bottom of the stroke, as a fraction of leg length")]
        [Range(0.85f, 1f)] public float kneeReach = 0.955f;   // ~30-35 deg knee bend at the bottom, the bike-fit norm
        [Tooltip("Shoulder-to-grip distance as a fraction of arm length")]
        [Range(0.7f, 1f)] public float armReach = 0.93f;
        [Tooltip("Torso lean from vertical (deg). Negative = derived from the grip position")]
        public float torsoLeanDeg = -1f;
        [Tooltip("Hip rock with the pedal stroke (deg)")] public float hipRockDeg = 2.5f;
        [Tooltip("Foot pitch swing through the stroke (deg)")] [Range(0f, 30f)] public float anklingDeg = 16f;
        public bool lookAhead = true;
        [Tooltip("Also orient the hands palm-down on the bars. Assumes Mecanim's T-pose-normalised hand goal; " +
                 "if the wrists twist, adjust handRotation or turn this off")]
        public bool alignHands = false;
        public Vector3 handRotation = new Vector3(10f, 90f, 0f);   // x: fingers down, y: +/- per side (left +, right -)
        [Tooltip("Wrist goal offset from the grip point (agent space, mirrored for the left hand), so the palm, not the wrist, sits on the bar")]
        public Vector3 wristFromGrip = new Vector3(0f, 0.03f, -0.06f);

        [Header("Coasting")]
        [Tooltip("Seconds between coasting spells (random in range); 0,0 = always pedal")]
        public Vector2 coastEvery = new Vector2(7f, 15f);
        public Vector2 coastFor = new Vector2(1.2f, 2.8f);

        [Header("Headlight (an EdgeBeam: lights the path, dazzles the bat)")]
        public bool headlight = true;
        public float headlightCandela = 45f;
        public float headlightRange = 22f;
        public float headlightSpotAngle = 40f;
        [Tooltip("Headlight is off while 'Moonlight' is brighter than this (daylight preview)")]
        public float nightLuxMax = 1000f;

        Animator _an;
        Quaternion[] _wheelRest;
        Vector3[] _wheelAxle;   // agent +X expressed in each wheel's rest frame
        Vector3 _lastPos;
        float _wheelDeg, _crankDeg;

        Vector3 _seat;
        float _lean, _legLen, _armLen, _torsoLen;
        Transform _lThigh, _rThigh, _lArm, _rArm;
        bool _haveOffset, _ikRan;
        Vector3 _hipOffset;                     // hip-joint midpoint relative to the body (COM), body frame
        Vector3 _lastBodyPos;
        Quaternion _lastBodyRot;
        bool _coasting, _braking;
        float _coastUntil, _nextCoast, _pedal01 = 1f, _speed, _accel;
        EdgeBeam _light;

        void Start()
        {
            _an = GetComponent<Animator>();
            if (!agent) agent = transform.parent;
            int n = wheels != null ? wheels.Length : 0;
            _wheelRest = new Quaternion[n]; _wheelAxle = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                if (!wheels[i]) continue;
                _wheelRest[i] = wheels[i].localRotation;
                _wheelAxle[i] = Quaternion.Inverse(wheels[i].rotation) * agent.right;
            }
            _lastPos = agent.position;
            _crankDeg = Random.value * 360f;
            _nextCoast = Time.time + Random.Range(coastEvery.x, coastEvery.y);
            MeasureRider();
            if (headlight && !agent.GetComponentInChildren<EdgeBeam>())
            {
                Vector3 bar = 0.5f * (gripLeft + gripRight);
                _light = EdgeBeam.Create(agent, "BikeLight", EdgeBeam.Kind.Bike, bar + new Vector3(0f, -0.05f, 0.12f),
                    Quaternion.Euler(7f, 0f, 0f), headlightCandela, headlightSpotAngle, headlightRange, new Color(0.92f, 0.95f, 1f));
            }
        }

        // Limb lengths are pose-independent, so the bind pose at Start is fine.
        void MeasureRider()
        {
            if (!_an || !_an.isHuman) { seatRider = false; return; }
            _lThigh = _an.GetBoneTransform(HumanBodyBones.LeftUpperLeg); _rThigh = _an.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            _lArm = _an.GetBoneTransform(HumanBodyBones.LeftUpperArm); _rArm = _an.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var lKnee = _an.GetBoneTransform(HumanBodyBones.LeftLowerLeg); var lFoot = _an.GetBoneTransform(HumanBodyBones.LeftFoot);
            var lElbow = _an.GetBoneTransform(HumanBodyBones.LeftLowerArm); var lHand = _an.GetBoneTransform(HumanBodyBones.LeftHand);
            if (!_lThigh || !_rThigh || !_lArm || !_rArm || !lKnee || !lFoot || !lElbow || !lHand) { seatRider = false; return; }
            _legLen = Vector3.Distance(_lThigh.position, lKnee.position) + Vector3.Distance(lKnee.position, lFoot.position);
            _armLen = Vector3.Distance(_lArm.position, lElbow.position) + Vector3.Distance(lElbow.position, lHand.position);
            _torsoLen = Vector3.Distance(0.5f * (_lThigh.position + _rThigh.position), 0.5f * (_lArm.position + _rArm.position));
            _seat = seatHip != Vector3.zero ? seatHip : EstimateSeat();
            _lean = torsoLeanDeg >= 0f ? torsoLeanDeg : EstimateLean();
            Debug.Log($"[EDGE] cyclist {agent.name}: seat hip {_seat} lean {_lean:F0} deg (legs {_legLen:F2} m, arms {_armLen:F2} m, torso {_torsoLen:F2} m)");
        }

        // Hip on a 73 deg seat tube through the bottom bracket, at the height where the ankle at the
        // bottom of the stroke is kneeReach * leg length away (knee slightly bent).
        Vector3 EstimateSeat()
        {
            const float seatTube = 73f * Mathf.Deg2Rad;
            Vector3 d = new Vector3(0f, Mathf.Sin(seatTube), -Mathf.Cos(seatTube));
            // ankle at the bottom of the stroke, with the foot pitched as Foot() pitches it there
            Vector3 a = new Vector3(0f, -crankRadius, 0f) + Quaternion.Euler(6f + 0.5f * anklingDeg, 0f, 0f) * ankleAbovePedal;
            float r = kneeReach * _legLen, da = Vector3.Dot(d, a);
            float t = da + Mathf.Sqrt(Mathf.Max(0f, da * da - a.sqrMagnitude + r * r));
            return crankCentre + d * t;
        }

        // Smallest forward lean that brings the shoulders within armReach * arm length of the grips.
        float EstimateLean()
        {
            Vector3 g = 0.5f * (gripLeft + gripRight);
            float reach = armReach * _armLen, bestDeg = 30f, bestD = float.MaxValue;
            for (int deg = 5; deg <= 60; deg++)
            {
                float rad = deg * Mathf.Deg2Rad;
                Vector3 sh = _seat + new Vector3(0f, Mathf.Cos(rad), Mathf.Sin(rad)) * _torsoLen;
                Vector3 dg = g - sh; dg.x = 0f;
                float d = dg.magnitude;
                if (d <= reach) return deg;
                if (d < bestD) { bestD = d; bestDeg = deg; }
            }
            return bestDeg;   // grips out of reach: lean as close as possible, the arms straighten
        }

        void Update()
        {
            float moved = Vector3.Dot(agent.position - _lastPos, agent.forward);
            _lastPos = agent.position;
            bool jumped = Mathf.Abs(moved) > 5f;
            if (jumped) moved = 0f; // respawned at the far end: no spin for the jump
            float rolled = moved / wheelRadius * Mathf.Rad2Deg; // positive about +X = top of the wheel moves forward
            _wheelDeg = Mathf.Repeat(_wheelDeg + rolled, 360f);

            // ground speed, low-passed (tau 0.25 s) before differentiating; the respawn jump is skipped
            float t = Time.time, dt = Time.deltaTime;
            if (dt > 0f && !jumped)
            {
                float k = 1f - Mathf.Exp(-dt / 0.25f);
                float sp = Mathf.Lerp(_speed, moved / dt, k);
                _accel = Mathf.Lerp(_accel, (sp - _speed) / dt, k);
                _speed = sp;
            }
            // coast now and then with the pedals level; always while braking (slowing for someone ahead),
            // never while picking up speed
            if (_braking && _accel > -0.1f) { _braking = false; _coasting = false; _nextCoast = t + Random.Range(coastEvery.x, coastEvery.y); }
            if (_coasting && !_braking && (t >= _coastUntil || _accel > 0.3f)) { _coasting = false; _nextCoast = t + Random.Range(coastEvery.x, coastEvery.y); }
            bool level = Mathf.Abs(Mathf.Repeat(_crankDeg, 180f) - 90f) < 12f;
            if (!_coasting && level && _accel < -0.4f) { _coasting = true; _braking = true; }
            else if (!_coasting && level && coastEvery.y > 0f && t >= _nextCoast && _accel <= 0.1f)
            { _coasting = true; _coastUntil = t + Random.Range(coastFor.x, coastFor.y); }
            if (!_coasting) _crankDeg = Mathf.Repeat(_crankDeg + rolled / gearRatio, 360f);
            _pedal01 = Mathf.MoveTowards(_pedal01, _coasting ? 0f : 1f, 3f * Time.deltaTime);

            for (int i = 0; i < _wheelRest.Length; i++)
                if (wheels[i]) wheels[i].localRotation = _wheelRest[i] * Quaternion.AngleAxis(_wheelDeg, _wheelAxle[i]);

            if (_light)
            {
                bool night = EdgeBeam.IsNight(nightLuxMax);
                if (_light.Light.enabled != night) _light.Light.enabled = night;
            }
        }

        // After the IK pass: how the hip joints sit relative to the body, to seat them next frame.
        void LateUpdate()
        {
            // only on frames the IK pass ran (a culled Animator skips it, and _lastBody* would be stale/unset)
            if (!_ikRan || !seatRider || !_lThigh || !_rThigh) return;
            _ikRan = false;
            Vector3 hip = 0.5f * (_lThigh.position + _rThigh.position);
            Vector3 off = Quaternion.Inverse(_lastBodyRot) * (hip - _lastBodyPos);
            _hipOffset = _haveOffset ? Vector3.Lerp(_hipOffset, off, 0.2f) : off;
            _haveOffset = true;
        }

        Vector3 Pedal(float deg, float side) =>
            crankCentre + new Vector3(side * pedalHalfWidth, 0f, 0f) + Quaternion.AngleAxis(deg, Vector3.right) * new Vector3(0f, crankRadius, 0f);

        void Foot(AvatarIKGoal goal, AvatarIKHint knee, HumanBodyBones thigh, float deg, float side)
        {
            // ankling: toes drop most on the upstroke (crank at the back), least on the downstroke
            float toesDown = 6f + anklingDeg * 0.5f * (1f + Mathf.Sin((deg - 180f) * Mathf.Deg2Rad)) * _pedal01;
            var pitch = Quaternion.Euler(toesDown, 0f, 0f);
            Vector3 target = agent.TransformPoint(Pedal(deg, side) + pitch * ankleAbovePedal);
            _an.SetIKPositionWeight(goal, 1f);
            _an.SetIKPosition(goal, target);
            _an.SetIKRotationWeight(goal, 1f);
            _an.SetIKRotation(goal, agent.rotation * pitch); // pointing forward, pitched by the ankling
            var hip = _an.GetBoneTransform(thigh);
            if (!hip) return;
            _an.SetIKHintPositionWeight(knee, 1f); // knees bend forward over the top tube
            _an.SetIKHintPosition(knee, 0.5f * (hip.position + target) + agent.forward * 0.4f + agent.right * (side * 0.04f));
        }

        void Hand(AvatarIKGoal goal, AvatarIKHint elbow, Transform shoulder, Vector3 grip, float side)
        {
            Vector3 g = agent.TransformPoint(alignHands ? grip + new Vector3(wristFromGrip.x * side, wristFromGrip.y, wristFromGrip.z) : grip);
            _an.SetIKPositionWeight(goal, 1f);
            _an.SetIKPosition(goal, g);
            if (alignHands)
            {
                // fingers forward and a little down, palm down (Euler x applied after the per-side yaw)
                _an.SetIKRotationWeight(goal, 1f);
                _an.SetIKRotation(goal, agent.rotation * Quaternion.Euler(handRotation.x, 0f, handRotation.z) * Quaternion.Euler(0f, side < 0f ? handRotation.y : -handRotation.y, 0f));
            }
            if (!shoulder) return;
            _an.SetIKHintPositionWeight(elbow, 0.8f);   // elbows out and a little down, not flipped up
            _an.SetIKHintPosition(elbow, 0.5f * (shoulder.position + g) + agent.right * (side * 0.18f) - agent.up * 0.12f);
        }

        void OnAnimatorIK(int layer)
        {
            if (!_an || !agent) return;
            if (seatRider)
            {
                // until the first measurement, a typical hip-below-COM offset seats the rider from the first visible frame
                Vector3 hipOff = _haveOffset ? _hipOffset : new Vector3(0f, -0.05f, 0f) * _an.humanScale;
                float rock = hipRockDeg * Mathf.Sin(_crankDeg * Mathf.Deg2Rad) * _pedal01;
                float lean = _lean - 4f * (1f - _pedal01);   // sit up a little while coasting
                Quaternion rb = agent.rotation * Quaternion.Euler(lean, 0f, rock);
                _an.bodyRotation = rb;
                _an.bodyPosition = agent.TransformPoint(_seat) - rb * hipOff;
            }
            _lastBodyPos = _an.bodyPosition;
            _lastBodyRot = _an.bodyRotation;
            _ikRan = true;

            Foot(AvatarIKGoal.RightFoot, AvatarIKHint.RightKnee, HumanBodyBones.RightUpperLeg, _crankDeg, 1f);
            Foot(AvatarIKGoal.LeftFoot, AvatarIKHint.LeftKnee, HumanBodyBones.LeftUpperLeg, _crankDeg + 180f, -1f);
            Hand(AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow, _lArm, gripLeft, -1f);
            Hand(AvatarIKGoal.RightHand, AvatarIKHint.RightElbow, _rArm, gripRight, 1f);
            if (lookAhead)
            {
                Vector3 fwd = Vector3.ProjectOnPlane(agent.forward, Vector3.up).normalized;
                _an.SetLookAtWeight(1f, 0.15f, 0.7f, 1f, 0.6f);
                _an.SetLookAtPosition(agent.position + fwd * 15f + Vector3.up * 1.1f);
            }
        }

        /// Seat/lean/limb summary for the edge_status CLI command.
        public string Describe() =>
            $"{(agent ? agent.name : name)}: seat {_seat} lean {_lean:F0} deg legs {_legLen:F2} m arms {_armLen:F2} m " +
            $"seated {(seatRider && _haveOffset)} coasting {_coasting} headlight {(_light ? "on" : "none")}";

        void OnDrawGizmosSelected()
        {
            var root = agent ? agent : transform.parent;
            if (!root) return;
            Gizmos.color = new Color(0.3f, 0.9f, 1f);
            Vector3 prev = root.TransformPoint(Pedal(0f, 0f));
            for (int d = 15; d <= 360; d += 15) { Vector3 p = root.TransformPoint(Pedal(d, 0f)); Gizmos.DrawLine(prev, p); prev = p; }
            Gizmos.DrawWireSphere(root.TransformPoint(gripLeft), 0.03f);
            Gizmos.DrawWireSphere(root.TransformPoint(gripRight), 0.03f);
            Vector3 seat = seatHip != Vector3.zero ? seatHip : _seat;
            if (seat != Vector3.zero) { Gizmos.color = new Color(1f, 0.6f, 0.2f); Gizmos.DrawWireSphere(root.TransformPoint(seat), 0.05f); }
        }
    }
}
