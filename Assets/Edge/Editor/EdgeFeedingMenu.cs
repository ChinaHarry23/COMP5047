using Edge.Feeding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Edge.EditorTools
{
    public static class EdgeFeedingMenu
    {
        const string PresetDir = "Assets/Edge/Feeding";

        [MenuItem("EDGE/Feeding Game/Add Feeding Game To Open Scene")]
        static void Add()
        {
            var game = Object.FindAnyObjectByType<EdgeFeedingGame>();
            if (!game)
            {
                var go = new GameObject("EdgeFeedingGame");
                Undo.RegisterCreatedObjectUndo(go, "Add Feeding Game");
                game = go.AddComponent<EdgeFeedingGame>();
            }
            Undo.RecordObject(game, "Wire Feeding Game");
            game.flight = Object.FindAnyObjectByType<BatFlightController>();
            game.link = game.flight ? game.flight.link : Object.FindAnyObjectByType<UdpLink>();
            game.lamps = Object.FindObjectsByType<LampZone>();
            EnsurePresets(out game.tolerantPreset, out game.aversePreset);
            EditorUtility.SetDirty(game);
            EditorSceneManager.MarkSceneDirty(game.gameObject.scene);
            Selection.activeObject = game;
            Debug.Log($"[EDGE] Feeding game added: bat {(game.flight ? game.flight.name : "NOT FOUND")}, {game.lamps.Length} lamps. Save the scene to keep it.");
        }

        [MenuItem("EDGE/Feeding Game/Set Variant: Light-Tolerant")]
        static void SetTolerant() => Set(EdgeFeedingGame.Variant.LightTolerant);

        [MenuItem("EDGE/Feeding Game/Set Variant: Light-Averse")]
        static void SetAverse() => Set(EdgeFeedingGame.Variant.LightAverse);

        [MenuItem("EDGE/Feeding Game/Create Default Presets")]
        static void Presets() { EnsurePresets(out _, out _); }

        static void Set(EdgeFeedingGame.Variant v)
        {
            var game = Object.FindAnyObjectByType<EdgeFeedingGame>();
            if (!game) { Debug.LogWarning("[EDGE] No EdgeFeedingGame in scene - use Add Feeding Game To Open Scene first."); return; }
            Undo.RecordObject(game, "Set Feeding Variant");
            if (Application.isPlaying) game.SetVariant(v); else game.variant = v;
            EditorUtility.SetDirty(game);
            if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(game.gameObject.scene);
        }

        static void EnsurePresets(out EdgeFeedingPreset tol, out EdgeFeedingPreset av)
        {
            if (!AssetDatabase.IsValidFolder(PresetDir)) AssetDatabase.CreateFolder("Assets/Edge", "Feeding");
            tol = Load(PresetDir + "/Feeding_LightTolerant.asset", EdgeFeedingPreset.CreateTolerant);
            av = Load(PresetDir + "/Feeding_LightAverse.asset", EdgeFeedingPreset.CreateAverse);
            AssetDatabase.SaveAssets();
        }

        static EdgeFeedingPreset Load(string path, System.Func<EdgeFeedingPreset> make)
        {
            var p = AssetDatabase.LoadAssetAtPath<EdgeFeedingPreset>(path);
            if (p) return p;
            p = make();
            AssetDatabase.CreateAsset(p, path);
            return p;
        }
    }
}
