using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Edge.EditorTools
{
    // Toggles the open EDGE scene between a bright daylight preview (to inspect
    // materials) and the real moonlit night. Edits the moon light and the
    // exposure in the scene's global Volume profile.
    public static class EdgeLightPreview
    {
        [MenuItem("EDGE/Lighting/Daylight Preview")]
        public static void Day() => Apply(day: true);

        [MenuItem("EDGE/Lighting/Night (Real)")]
        public static void Night() => Apply(day: false);

        static void Apply(bool day)
        {
            var moonGo = GameObject.Find("Moonlight");
            if (moonGo && moonGo.TryGetComponent(out Light moon))
            {
                Undo.RecordObject(moon, "EDGE lighting");
                moon.lightUnit = LightUnit.Lux;
                moon.intensity = day ? 100000f : 0.4f;
                moon.color = day ? new Color(1f, 0.96f, 0.9f) : new Color(0.66f, 0.74f, 0.88f);
                moonGo.transform.rotation = day ? Quaternion.Euler(55f, -30f, 0f) : Quaternion.Euler(48f, -142f, 0f);
                EditorUtility.SetDirty(moon);
            }
            else Debug.LogWarning("[EDGE] No 'Moonlight' light in the open scene. Build the scene first.");

            foreach (var vol in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                var p = vol.sharedProfile;
                if (!p || !p.TryGet(out Exposure exp)) continue;
                Undo.RecordObject(p, "EDGE lighting");
                exp.mode.Override(ExposureMode.Fixed);
                exp.fixedExposure.Override(day ? 14f : -0.5f);
                if (p.TryGet(out Fog fog)) fog.meanFreePath.Override(day ? 400f : 70f);
                EditorUtility.SetDirty(p);
            }
            Debug.Log(day ? "[EDGE] Daylight preview on" : "[EDGE] Night lighting restored");
        }
    }
}
