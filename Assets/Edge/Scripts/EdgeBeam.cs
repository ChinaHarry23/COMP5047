using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Edge
{
    // A moving light that can shine on the bat: a walker's torch or a cyclist's headlight. A real HDRP
    // spot light (no shadows) so it lights the path; EdgeLightEffects reads every enabled beam each frame
    // and turns exposure into glare + pulsing vibration. Beams are not LampZones: they never feed the
    // DoseModel, so the exhibit's dose account is unchanged.
    [RequireComponent(typeof(Light))]
    public class EdgeBeam : MonoBehaviour
    {
        public enum Kind { Torch, Bike }

        public static readonly List<EdgeBeam> All = new List<EdgeBeam>();

        public Kind kind = Kind.Torch;

        /// Eased glare on the bat (written by EdgeLightEffects), so occlusion edges fade instead of popping.
        [System.NonSerialized] public float glare;

        Light _light;
        public Light Light => _light ? _light : (_light = GetComponent<Light>());

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        static Light s_moon;
        static float s_nextMoonCheck = float.MinValue;

        /// Night = the 'Moonlight' light is no brighter than nightLuxMax (EDGE > Lighting > Daylight
        /// Preview sets it to 100000 lux, Night (Real) to 0.4 lux). Same rule as EdgeBenchSitter's phones.
        public static bool IsNight(float nightLuxMax = 1000f)
        {
            if (!s_moon && Time.unscaledTime >= s_nextMoonCheck)
            {
                s_nextMoonCheck = Time.unscaledTime + 1f;
                var g = GameObject.Find("Moonlight");
                s_moon = g ? g.GetComponent<Light>() : null;
            }
            return !s_moon || s_moon.intensity <= nightLuxMax;
        }

        /// 0..1: how hard this beam shines on point p (cone and distance only; occlusion is the caller's).
        public float Exposure(Vector3 p)
        {
            var l = Light;
            if (!l || !l.isActiveAndEnabled || l.intensity <= 0f) return 0f;
            Vector3 d = p - transform.position;
            float dist = d.magnitude, range = Mathf.Max(0.1f, l.range);
            if (dist < 0.05f || dist > range) return 0f;
            float cos = Vector3.Dot(transform.forward, d / dist);
            float outer = Mathf.Cos(0.5f * l.spotAngle * Mathf.Deg2Rad);
            float inner = Mathf.Cos(0.5f * Mathf.Min(l.innerSpotAngle, l.spotAngle * 0.95f) * Mathf.Deg2Rad);
            float cone = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, inner, cos));
            float fall = 1f - dist / range;
            return cone * fall * fall;
        }

        public static EdgeBeam Create(Transform parent, string name, Kind kind, Vector3 localPos, Quaternion localRot,
                                      float candela, float spotAngle, float range, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(localPos, localRot);
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot;
            l.shadows = LightShadows.None;
            l.spotAngle = spotAngle;
            l.innerSpotAngle = spotAngle * 0.55f;
            l.range = range;
            l.color = color;
            go.AddComponent<HDAdditionalLightData>();
            l.lightUnit = LightUnit.Candela;
            l.intensity = candela;
            var b = go.AddComponent<EdgeBeam>();
            b.kind = kind;
            return b;
        }
    }
}
