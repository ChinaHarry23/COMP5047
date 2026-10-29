using UnityEngine;
using UnityEngine.InputSystem;

namespace MoonlitIsland
{
    /// <summary>
    /// Rides one bat from the flock. F mounts and dismounts, the mouse aims,
    /// WASD flies, Space/C (or Left Ctrl) climbs and dives, Shift boosts, and
    /// V toggles between first person (from the bat's head) and a third person
    /// chase view. While mounted the orbit camera rig yields, and the flock
    /// keeps flapping the ridden bat's wings.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(IslandCameraRig))]
    public sealed class IslandBatPilot : MonoBehaviour
    {
        [Header("Mounting")]
        [Tooltip("Index of the flock bat to ride; 0 is Bat 01.")]
        [Min(0)] public int batIndex;

        [Header("Flight")]
        public float cruiseSpeed = 26f;
        public float strafeSpeed = 11f;
        public float verticalSpeed = 13f;
        [Min(1f)] public float boostMultiplier = 2.4f;
        [Min(0.1f)] public float velocitySmoothing = 5f;
        [Range(0.005f, 0.5f)] public float mouseSensitivity = 0.13f;
        [Range(10f, 89f)] public float pitchLimit = 78f;
        [Range(0f, 60f)] public float bankAngle = 30f;
        [Tooltip("Lowest altitude above the ocean the bat can fly.")]
        public float minimumAltitude = 1.4f;

        [Header("Third person chase")]
        public Vector3 chaseOffset = new Vector3(0f, 1.5f, -4.4f);
        [Range(0.5f, 20f)] public float chaseSmoothing = 7f;

        [Header("First person")]
        public Vector3 eyeOffset = new Vector3(0f, 0.10f, 0.34f);

        public bool Piloting { get; private set; }
        public bool FirstPersonView { get; private set; }

        private IslandBatFlock flock;
        private IslandCameraRig rig;
        private Camera camera;
        private Transform bat;
        private float batScale = 1f;
        private Renderer[] batRenderers = System.Array.Empty<Renderer>();
        private Vector3 velocity;
        private float aimYaw;
        private float aimPitch;
        private float lastAimYaw;
        private float bank;
        private Vector3 chasePosition;

        private void Awake()
        {
            rig = GetComponent<IslandCameraRig>();
            camera = GetComponent<Camera>();
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.fKey.wasPressedThisFrame)
            {
                if (Piloting) Dismount();
                else Mount();
            }

            if (!Piloting)
                return;

            if (keyboard.vKey.wasPressedThisFrame)
                SetFirstPerson(!FirstPersonView);

            // Give the cursor back when the user needs it; clicking the Game view re-locks it.
            if (keyboard.escapeKey.wasPressedThisFrame)
                UnlockCursor();

            AimAndSteer(keyboard, Time.deltaTime);
        }

        private void LateUpdate()
        {
            if (!Piloting || bat == null)
                return;
            PlaceCamera(Time.deltaTime);
        }

        private void Mount()
        {
            if (flock == null) flock = FindAnyObjectByType<IslandBatFlock>();
            if (flock == null)
            {
                Debug.LogWarning("IslandBatPilot: no IslandBatFlock in the scene.");
                return;
            }
            bat = flock.TakeBat(batIndex);
            if (bat == null)
            {
                Debug.LogWarning("IslandBatPilot: the flock has no bat with index " + batIndex + ".");
                return;
            }

            Piloting = true;
            batRenderers = bat.GetComponentsInChildren<Renderer>(true);
            batScale = Mathf.Max(0.01f, bat.localScale.x);
            Vector3 euler = bat.eulerAngles;
            aimYaw = lastAimYaw = euler.y;
            aimPitch = bank = 0f;
            velocity = Vector3.zero;
            chasePosition = camera.transform.position;
            SetFirstPerson(false);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Dismount()
        {
            Piloting = false;
            SetFirstPerson(false);
            if (bat != null)
            {
                SetBatRenderers(true);
                flock.ReleaseBat(bat);
                bat = null;
            }
            batRenderers = System.Array.Empty<Renderer>();
            UnlockCursor();
            if (rig != null)
                rig.ResyncFromTransform();
        }

        private void AimAndSteer(Keyboard keyboard, float deltaTime)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            {
                Vector2 delta = mouse.delta.ReadValue() * mouseSensitivity;
                aimYaw += delta.x;
                aimPitch = Mathf.Clamp(aimPitch - delta.y, -pitchLimit, pitchLimit);
            }
            else if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            Vector2 move = Vector2.zero;
            if (keyboard.wKey.isPressed) move.y += 1f;
            if (keyboard.sKey.isPressed) move.y -= 1f;
            if (keyboard.dKey.isPressed) move.x += 1f;
            if (keyboard.aKey.isPressed) move.x -= 1f;
            float climb = 0f;
            if (keyboard.spaceKey.isPressed) climb += 1f;
            if (keyboard.leftCtrlKey.isPressed || keyboard.cKey.isPressed) climb -= 1f;
            float boost = keyboard.leftShiftKey.isPressed ? boostMultiplier : 1f;

            Quaternion aim = Quaternion.Euler(aimPitch, aimYaw, 0f);
            Vector3 target = aim * new Vector3(move.x * strafeSpeed, 0f, move.y * cruiseSpeed * boost);
            target.y += climb * verticalSpeed;
            velocity = Vector3.Lerp(velocity, target, 1f - Mathf.Exp(-velocitySmoothing * deltaTime));
            bat.position += velocity * deltaTime;

            Vector3 position = bat.position;
            position.y = Mathf.Max(position.y, minimumAltitude);
            bat.position = position;

            // Bank into mouse turns and A/D strafes so the flight reads as agile rather than sliding.
            float yawRate = Mathf.DeltaAngle(lastAimYaw, aimYaw) / Mathf.Max(deltaTime, 0.0001f);
            lastAimYaw = aimYaw;
            float targetBank = Mathf.Clamp(-move.x * bankAngle - yawRate * 0.3f, -65f, 65f);
            bank = Mathf.Lerp(bank, targetBank, 1f - Mathf.Exp(-4f * deltaTime));
            bat.rotation = aim * Quaternion.Euler(0f, 0f, bank);
        }

        private void PlaceCamera(float deltaTime)
        {
            Transform view = camera.transform;
            if (FirstPersonView)
            {
                view.SetPositionAndRotation(bat.TransformPoint(eyeOffset * batScale), bat.rotation);
                chasePosition = view.position;
            }
            else
            {
                Vector3 anchor = bat.TransformPoint(chaseOffset * batScale);
                float blend = 1f - Mathf.Exp(-chaseSmoothing * deltaTime);
                chasePosition = Vector3.Lerp(chasePosition, anchor, blend);
                view.SetPositionAndRotation(chasePosition, bat.rotation);
            }
        }

        private void SetFirstPerson(bool firstPerson)
        {
            FirstPersonView = firstPerson;
            // In first person the bat's own geometry is hidden so the near plane never clips through it.
            SetBatRenderers(!firstPerson);
        }

        private void SetBatRenderers(bool visible)
        {
            foreach (Renderer renderer in batRenderers)
                if (renderer != null)
                    renderer.enabled = visible;
        }

        private static void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
