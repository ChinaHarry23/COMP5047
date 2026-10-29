using UnityEngine;
using UnityEngine.InputSystem;

namespace MoonlitIsland
{
    /// <summary>
    /// Drag either mouse button to orbit, scroll to zoom, R to reset, and O to toggle auto orbit.
    /// The initial composition is taken from the camera's authored transform.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IslandCameraRig : MonoBehaviour
    {
        public Vector3 focus = new Vector3(0f, 7f, 0f);

        [Tooltip("Runtime orbit radius; initialized from the camera's starting position.")]
        public float distance = 155f;
        public float yaw = 140f;

        [Range(8f, 75f)] public float pitch = 22f;
        public bool autoOrbit;

        [Min(1f)] public float minimumDistance = 65f;
        [Min(1f)] public float maximumDistance = 220f;
        [Range(0.01f, 1f)] public float dragSensitivity = 0.18f;
        [Range(0.0001f, 0.01f)] public float scrollSensitivity = 0.0015f;
        [Range(1f, 20f)] public float smoothing = 9f;
        [Range(0f, 15f)] public float autoOrbitSpeed = 2.5f;

        private float currentYaw;
        private float currentPitch;
        private float currentDistance;
        private float initialYaw;
        private float initialPitch;
        private float initialDistance;
        private bool initialAutoOrbit;
        private IslandBatPilot pilot;

        private void Awake()
        {
            pilot = GetComponent<IslandBatPilot>();
        }

        private void Start()
        {
            ResyncFromTransform();
            initialYaw = yaw;
            initialPitch = pitch;
            initialDistance = distance;
            initialAutoOrbit = autoOrbit;
            ApplyPose();
        }

        /// <summary>
        /// Recomputes the orbit pose from wherever the camera currently is.
        /// Called after a bat ride so the showcase orbit resumes without a snap.
        /// </summary>
        public void ResyncFromTransform()
        {
            Vector3 offset = transform.position - focus;
            if (offset.sqrMagnitude > 0.001f)
            {
                distance = offset.magnitude;
                yaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
                pitch = Mathf.Asin(Mathf.Clamp(offset.y / distance, -1f, 1f)) * Mathf.Rad2Deg;
            }

            minimumDistance = Mathf.Max(1f, minimumDistance);
            // A bat ride can carry the camera beyond the authored zoom range; keep its framing rather than snap back.
            maximumDistance = Mathf.Max(maximumDistance, distance);
            distance = Mathf.Clamp(distance, minimumDistance, maximumDistance);
            pitch = Mathf.Clamp(pitch, 8f, 75f);

            currentYaw = yaw;
            currentPitch = pitch;
            currentDistance = distance;
            ApplyPose();
        }

        private void LateUpdate()
        {
            if (pilot != null && pilot.Piloting)
                return;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.rKey.wasPressedThisFrame)
                {
                    yaw = initialYaw;
                    pitch = initialPitch;
                    distance = initialDistance;
                    autoOrbit = initialAutoOrbit;
                }

                if (keyboard.oKey.wasPressedThisFrame)
                    autoOrbit = !autoOrbit;
            }

            Mouse mouse = Mouse.current;
            bool dragging = mouse != null &&
                            (mouse.leftButton.isPressed || mouse.rightButton.isPressed);
            if (dragging)
            {
                Vector2 delta = mouse.delta.ReadValue();
                yaw += delta.x * dragSensitivity;
                pitch = Mathf.Clamp(pitch - delta.y * dragSensitivity, 8f, 75f);
            }

            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.001f)
                {
                    distance = Mathf.Clamp(distance * Mathf.Exp(-scroll * scrollSensitivity),
                        minimumDistance, maximumDistance);
                }
            }

            float deltaTime = Time.unscaledDeltaTime;
            if (autoOrbit && !dragging)
                yaw += autoOrbitSpeed * deltaTime;

            float blend = 1f - Mathf.Exp(-smoothing * deltaTime);
            currentYaw = Mathf.LerpAngle(currentYaw, yaw, blend);
            currentPitch = Mathf.Lerp(currentPitch, pitch, blend);
            currentDistance = Mathf.Lerp(currentDistance, distance, blend);
            ApplyPose();
        }

        private void ApplyPose()
        {
            float yawRadians = currentYaw * Mathf.Deg2Rad;
            float pitchRadians = currentPitch * Mathf.Deg2Rad;
            float horizontalRadius = currentDistance * Mathf.Cos(pitchRadians);
            Vector3 offset = new Vector3(
                Mathf.Sin(yawRadians) * horizontalRadius,
                Mathf.Sin(pitchRadians) * currentDistance,
                Mathf.Cos(yawRadians) * horizontalRadius);

            transform.SetPositionAndRotation(focus + offset,
                Quaternion.LookRotation(-offset, Vector3.up));
        }
    }
}
