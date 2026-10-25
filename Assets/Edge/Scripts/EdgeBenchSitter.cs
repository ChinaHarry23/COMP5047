using UnityEngine;

namespace Edge
{
    // A person seated on a bench watching a video on a phone. The seated pose is baked by the editor
    // (EdgeSceneBuilder.FixBenchSitters) as Humanoid muscle values and applied with HumanPoseHandler,
    // so it shows in edit mode and in Play without an AnimatorController (the Animator is disabled).
    // Play mode adds a tiny breathing / head motion and a video-like flicker on the phone screen.
    // The phone light (no shadows) is only on at night: off while 'Moonlight' is brighter than nightLuxMax
    // (EDGE > Lighting > Daylight Preview sets it to 100000 lux, Night (Real) to 0.4 lux).
    // At night the sitter also takes flash photos of a bat that flies close in front of them: a short
    // aim delay, a phone-LED burst on the phone light, and EdgeLightEffects.Flash (white-out, buzz).
    [ExecuteAlways]
    public class EdgeBenchSitter : MonoBehaviour
    {
        [Header("Flash photos")]
        public bool takesFlashPhotos = true;
        [Tooltip("Bat closer than this (m) and in front of the sitter gets photographed")]
        public float flashRange = 12f;
        [Tooltip("Half-angle (deg) of the sitter's view, measured from the body's facing")]
        public float flashViewAngle = 70f;
        [Tooltip("Seconds between photos (random in range)")]
        public Vector2 flashCooldown = new Vector2(7f, 16f);
        [Tooltip("Seconds between noticing the bat and the flash")]
        public float flashAimDelay = 0.6f;
        public float flashLumen = 350f;

        public Avatar avatar;
        public Transform body;            // the Humanoid root (Animator object)
        public float[] muscles;           // baked seated pose
        public Vector3 bodyPosition;
        public Quaternion bodyRotation = Quaternion.identity;
        public Renderer screen;
        public Light phoneLight;
        public Color screenColor = new Color(0.30f, 0.42f, 0.62f);
        public float screenLevel = 0.35f; // HDRP Unlit, exposure-relative (night EV -1.3)
        public float lightLumen = 0.6f;
        public float nightLuxMax = 1000f;
        public float animDistance = 40f;

        HumanPoseHandler _hph;
        HumanPose _pose;
        int _chest = -1, _spine = -1, _head = -1, _neck = -1;
        float _seed;
        Light _moon;
        float _nextMoonCheck;
        MaterialPropertyBlock _mpb;
        float _nextPhoto, _photoAt = -1f, _burstUntil = -1f;

        void OnEnable()
        {
            _seed = Random.value * 100f;
            _mpb = new MaterialPropertyBlock();
            UpdateNight(true);
            ApplyScreen(1f);
        }

        void OnDisable() { _hph?.Dispose(); _hph = null; }

        bool InitPose()
        {
            if (_hph != null) return true;
            if (!avatar || !avatar.isHuman || !body || muscles == null || muscles.Length != HumanTrait.MuscleCount) return false;
            _hph = new HumanPoseHandler(avatar, body);
            _pose = new HumanPose { muscles = (float[])muscles.Clone(), bodyPosition = bodyPosition, bodyRotation = bodyRotation };
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                string n = HumanTrait.MuscleName[i];
                if (n == "Chest Front-Back") _chest = i;
                else if (n == "Spine Front-Back") _spine = i;
                else if (n == "Head Nod Down-Up") _head = i;
                else if (n == "Neck Turn Left-Right") _neck = i;
            }
            return true;
        }

        void UpdateNight(bool force)
        {
            if (!phoneLight) return;
            if (force || Time.realtimeSinceStartup >= _nextMoonCheck || !Application.isPlaying)
            {
                _nextMoonCheck = Time.realtimeSinceStartup + 1f;
                if (!_moon) { var g = GameObject.Find("Moonlight"); _moon = g ? g.GetComponent<Light>() : null; }
            }
            bool night = !_moon || _moon.intensity <= nightLuxMax;
            if (phoneLight.enabled != night) phoneLight.enabled = night;
        }

        void ApplyScreen(float k)
        {
            if (!screen) return;
            screen.GetPropertyBlock(_mpb);
            _mpb.SetColor("_UnlitColor", screenColor * (screenLevel * k));
            screen.SetPropertyBlock(_mpb);
        }

        // Notice a bat in front, aim for a moment, then flash (only at night, only with a clear line).
        void FlashPhotos()
        {
            var fx = EdgeLightEffects.Instance;
            if (!fx || !fx.flight || !EdgeBeam.IsNight(nightLuxMax)) { _photoAt = -1f; return; }
            float now = Time.time;
            Transform batT = fx.flight.transform;
            Transform me = body ? body : transform;
            Vector3 eye = phoneLight ? phoneLight.transform.position : me.position + Vector3.up * 1.1f;
            Vector3 to = batT.position - eye;
            Vector3 fwd = Vector3.ProjectOnPlane(me.forward, Vector3.up);
            bool sees = to.magnitude < flashRange && Vector3.Angle(fwd, Vector3.ProjectOnPlane(to, Vector3.up)) < flashViewAngle;
            if (sees && Physics.Linecast(eye, batT.position, out var hit, ~(1 << 2), QueryTriggerInteraction.Ignore))
                sees = hit.transform.IsChildOf(batT) || hit.transform.IsChildOf(transform);
            if (_photoAt < 0f)
            {
                if (sees && now >= _nextPhoto) _photoAt = now + flashAimDelay;
                return;
            }
            if (now < _photoAt) return;
            _photoAt = -1f;
            if (!sees) return;
            _nextPhoto = now + Random.Range(flashCooldown.x, flashCooldown.y);
            _burstUntil = now + 0.12f;
            fx.Flash(Mathf.Lerp(1f, 0.45f, to.magnitude / Mathf.Max(0.1f, flashRange)));
        }

        void LateUpdate()
        {
            UpdateNight(false);
            if (!Application.isPlaying) return;
            if (takesFlashPhotos) FlashPhotos();
            var cam = Camera.main;
            if (cam && (transform.position - cam.transform.position).sqrMagnitude > animDistance * animDistance) return;
            float t = Time.time + _seed;
            // Video flicker: slow scene changes + small fast variation.
            float k = 0.75f + 0.18f * Mathf.PerlinNoise(t * 0.7f, _seed) + 0.07f * Mathf.PerlinNoise(t * 6f, 1f + _seed);
            Color hue = Color.Lerp(new Color(0.85f, 0.95f, 1.15f), new Color(1.1f, 0.95f, 0.85f), Mathf.PerlinNoise(t * 0.25f, 3f + _seed));
            if (screen) { screen.GetPropertyBlock(_mpb); _mpb.SetColor("_UnlitColor", Color.Lerp(screenColor, screenColor * hue, 0.6f) * (screenLevel * k)); screen.SetPropertyBlock(_mpb); }
            if (phoneLight && phoneLight.enabled) phoneLight.intensity = Time.time < _burstUntil ? flashLumen : lightLumen * k;
            if (screen && Time.time < _burstUntil) { screen.GetPropertyBlock(_mpb); _mpb.SetColor("_UnlitColor", Color.white * 4f); screen.SetPropertyBlock(_mpb); }
            if (!InitPose()) return;
            float breath = Mathf.Sin(t * 2f * Mathf.PI / 4.2f); // ~14 breaths/min
            if (_chest >= 0) _pose.muscles[_chest] = muscles[_chest] + 0.025f * breath;
            if (_spine >= 0) _pose.muscles[_spine] = muscles[_spine] + 0.01f * breath;
            if (_head >= 0) _pose.muscles[_head] = muscles[_head] + 0.04f * (Mathf.PerlinNoise(t * 0.3f, 5f + _seed) - 0.5f);
            if (_neck >= 0) _pose.muscles[_neck] = muscles[_neck] + 0.06f * (Mathf.PerlinNoise(t * 0.2f, 7f + _seed) - 0.5f);
            _hph.SetHumanPose(ref _pose);
        }
    }
}
