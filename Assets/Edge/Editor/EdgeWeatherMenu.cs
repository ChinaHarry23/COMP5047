using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Edge.EditorTools
{
    // EDGE > Weather: adds the weather sky to the open scene without a rebuild.
    // Touches nothing else in the scene (the builders are unchanged).
    public static class EdgeWeatherMenu
    {
        const string Dir = "Assets/Edge/Settings/Weather";

        [MenuItem("EDGE/Weather/Add Weather Sky To Open Scene")]
        public static void Add()
        {
            var presets = CreatePresets();
            var sky = Object.FindFirstObjectByType<EdgeWeatherSky>();
            if (!sky)
            {
                var go = new GameObject("Weather Sky");
                Undo.RegisterCreatedObjectUndo(go, "Add EDGE weather sky");
                sky = go.AddComponent<EdgeWeatherSky>();
            }
            Undo.RecordObject(sky, "EDGE weather presets");
            sky.presets = presets;
            EditorUtility.SetDirty(sky);
            EditorSceneManager.MarkSceneDirty(sky.gameObject.scene);
            Selection.activeObject = sky.gameObject;
            Debug.Log("[EDGE Weather] Weather Sky added/updated in open scene; save the scene to keep it");
        }

        [MenuItem("EDGE/Weather/Create Or Refresh Presets")]
        public static void Refresh() => CreatePresets();

        static EdgeWeatherPreset[] CreatePresets()
        {
            Directory.CreateDirectory(Dir);
            var arr = new EdgeWeatherPreset[6];
            for (int i = 0; i < 6; i++)
            {
                var t = (WeatherType)i;
                string path = $"{Dir}/EdgeWeather_{t}.asset";
                var p = AssetDatabase.LoadAssetAtPath<EdgeWeatherPreset>(path);
                if (!p) { p = EdgeWeatherPreset.CreateDefault(t); AssetDatabase.CreateAsset(p, path); }
                arr[i] = p;
            }
            AssetDatabase.SaveAssets();
            return arr;
        }

        static void ForceSel(WeatherType t)
        {
            var s = Object.FindFirstObjectByType<EdgeWeatherSky>();
            if (!s) { Debug.LogWarning("[EDGE Weather] run EDGE/Weather/Add Weather Sky To Open Scene first"); return; }
            s.forceWeather = true; s.forcedType = t; s.Apply(t, "forced (menu)");
        }
        [MenuItem("EDGE/Weather/Force/Clear Night")] static void A() => ForceSel(WeatherType.ClearNight);
        [MenuItem("EDGE/Weather/Force/Partly Cloudy")] static void B() => ForceSel(WeatherType.PartlyCloudy);
        [MenuItem("EDGE/Weather/Force/Overcast")] static void C() => ForceSel(WeatherType.Overcast);
        [MenuItem("EDGE/Weather/Force/Light Rain")] static void D() => ForceSel(WeatherType.LightRain);
        [MenuItem("EDGE/Weather/Force/Rain")] static void E() => ForceSel(WeatherType.Rain);
        [MenuItem("EDGE/Weather/Force/Thunderstorm")] static void F() => ForceSel(WeatherType.Thunderstorm);
    }
}
