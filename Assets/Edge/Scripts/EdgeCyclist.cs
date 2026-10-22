using UnityEngine;

namespace Edge
{
    // Pedalling for the cyclist pedestrian. Sits on the rider's Animator object (OnAnimatorIK only fires
    // there). Ground speed is read from how far the agent actually moved this frame, so it follows
    // EdgePedestrians without a reference to it. Both wheels roll at that speed; the feet ride a virtual
    // crank at the bottom bracket (the low-poly bike has no separate crank mesh) geared down from the
    // wheel; the hands hold the grips. Needs IK Pass on the controller's base layer (the builder sets it).
    // All positions are in the agent's local frame (+Z = travel, +X = rider's right), set by the builder.
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

        Animator _an;
        Quaternion[] _wheelRest;
        Vector3[] _wheelAxle;   // agent +X expressed in each wheel's rest frame
        Vector3 _lastPos;
        float _wheelDeg, _crankDeg;

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
        }

        void Update()
        {
            float moved = Vector3.Dot(agent.position - _lastPos, agent.forward);
            _lastPos = agent.position;
            if (Mathf.Abs(moved) > 5f) moved = 0f; // respawned at the far end: no spin for the jump
            float rolled = moved / wheelRadius * Mathf.Rad2Deg; // positive about +X = top of the wheel moves forward
            _wheelDeg = Mathf.Repeat(_wheelDeg + rolled, 360f);
            _crankDeg = Mathf.Repeat(_crankDeg + rolled / gearRatio, 360f);
            for (int i = 0; i < _wheelRest.Length; i++)
                if (wheels[i]) wheels[i].localRotation = _wheelRest[i] * Quaternion.AngleAxis(_wheelDeg, _wheelAxle[i]);
        }

        Vector3 Pedal(float deg, float side) =>
            crankCentre + new Vector3(side * pedalHalfWidth, 0f, 0f) + Quaternion.AngleAxis(deg, Vector3.right) * new Vector3(0f, crankRadius, 0f);

        void Foot(AvatarIKGoal goal, AvatarIKHint knee, HumanBodyBones thigh, float deg, float side)
        {
            Vector3 target = agent.TransformPoint(Pedal(deg, side) + ankleAbovePedal);
            _an.SetIKPositionWeight(goal, 1f);
            _an.SetIKPosition(goal, target);
            _an.SetIKRotationWeight(goal, 1f);
            _an.SetIKRotation(goal, agent.rotation); // foot level and pointing forward
            var hip = _an.GetBoneTransform(thigh);
            if (!hip) return;
            _an.SetIKHintPositionWeight(knee, 1f); // knees bend forward over the top tube
            _an.SetIKHintPosition(knee, 0.5f * (hip.position + target) + agent.forward * 0.4f + agent.right * (side * 0.04f));
        }

        void OnAnimatorIK(int layer)
        {
            if (!_an || !agent) return;
            Foot(AvatarIKGoal.RightFoot, AvatarIKHint.RightKnee, HumanBodyBones.RightUpperLeg, _crankDeg, 1f);
            Foot(AvatarIKGoal.LeftFoot, AvatarIKHint.LeftKnee, HumanBodyBones.LeftUpperLeg, _crankDeg + 180f, -1f);
            _an.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1f);
            _an.SetIKPosition(AvatarIKGoal.LeftHand, agent.TransformPoint(gripLeft));
            _an.SetIKPositionWeight(AvatarIKGoal.RightHand, 1f);
            _an.SetIKPosition(AvatarIKGoal.RightHand, agent.TransformPoint(gripRight));
        }
    }
}
