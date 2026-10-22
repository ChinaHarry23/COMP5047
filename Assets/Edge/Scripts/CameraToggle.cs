using UnityEngine;
using UnityEngine.InputSystem;

namespace Edge
{
    // FPV shows what the bat sees; third-person shows the visitor standing in
    // the light — the point of the piece (FINAL_PLAN section 5). C toggles.
    public class CameraToggle : MonoBehaviour
    {
        public Camera fpv;
        public Camera tpv;
        public Transform bat;
        public bool startInThirdPerson = true;
        public float followDistance = 6f;  // m behind the bat; EdgeSceneBuilder sets 1.6 for a 0.3 m bat
        public float followHeight = 2.5f;  // m above
        public float lookHeight = 0.5f;    // m above the bat the camera aims at
        [Tooltip("Follow position sharpness (1/s, frame-rate independent). Higher = tighter")]
        public float positionSharpness = 9f;
        [Tooltip("Look-at rotation sharpness (1/s). Higher = camera turns with the bat sooner")]
        public float rotationSharpness = 14f;
        [Tooltip("Follow behind the heading only, so bank/pitch do not swing the camera")]
        public bool followHeadingOnly = true;
        [Tooltip("Lead the look point into the turn (s of yaw rate)")]
        public float turnLookAhead = 0.15f;
        BatFlightController _flight;

        public bool ThirdPerson { get; private set; }

        void Start()
        {
            SetMode(startInThirdPerson);
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.cKey.wasPressedThisFrame)
                SetMode(!ThirdPerson);
        }

        void LateUpdate()
        {
            if (!ThirdPerson || !tpv || !bat) return;
            float dt = Time.deltaTime;
            if (!_flight) _flight = bat.GetComponent<BatFlightController>();
            Vector3 back = bat.forward;
            if (followHeadingOnly)
            {
                back = Vector3.ProjectOnPlane(bat.forward, Vector3.up);
                if (back.sqrMagnitude < 1e-4f) back = bat.forward;
                back.Normalize();
            }
            Vector3 target = bat.position - back * followDistance + Vector3.up * followHeight;
            tpv.transform.position = Vector3.Lerp(tpv.transform.position, target, 1f - Mathf.Exp(-positionSharpness * dt));
            Vector3 lead = Vector3.zero;
            if (_flight && turnLookAhead > 0f)
                lead = Quaternion.Euler(0f, _flight.YawRateDegPerSec * turnLookAhead, 0f) * back - back;
            Vector3 look = bat.position + lead * followDistance + Vector3.up * lookHeight - tpv.transform.position;
            if (look.sqrMagnitude > 0.001f)
                tpv.transform.rotation = Quaternion.Slerp(tpv.transform.rotation,
                    Quaternion.LookRotation(look), 1f - Mathf.Exp(-rotationSharpness * dt));
        }

        void SetMode(bool third)
        {
            ThirdPerson = third;
            if (fpv) fpv.gameObject.SetActive(!third);
            if (tpv) tpv.gameObject.SetActive(third);
        }
    }
}
