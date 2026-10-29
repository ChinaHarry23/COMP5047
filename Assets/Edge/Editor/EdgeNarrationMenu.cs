using Edge.Narration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Edge.EditorTools
{
    // EDGE > Narration. "Add" creates exactly one GameObject ("Narrator") and wires
    // existing refs; nothing else in the scene is touched (builders unchanged).
    public static class EdgeNarrationMenu
    {
        const string Sample = "Night at the bushland edge. Every lamp I pass spends a little more of my darkness.";

        [MenuItem("EDGE/Narration/Add Narrator To Open Scene")]
        public static void Add()
        {
            var n = Object.FindAnyObjectByType<EdgeNarrator>();
            if (n)
            {
                Selection.activeObject = n.gameObject;
                Debug.Log("[EDGE Narration] Narrator already in scene; selected it");
                return;
            }
            var go = new GameObject("Narrator");
            Undo.RegisterCreatedObjectUndo(go, "Add EDGE narrator");
            n = go.AddComponent<EdgeNarrator>();
            n.dose = Object.FindAnyObjectByType<DoseModel>();
            n.flight = Object.FindAnyObjectByType<BatFlightController>();
            n.bat = n.flight ? n.flight.transform : null;
            n.weather = Object.FindAnyObjectByType<EdgeWeatherSky>();
            n.lamps = Object.FindObjectsByType<LampZone>();
            EditorUtility.SetDirty(n);
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeObject = go;
            Debug.Log($"[EDGE Narration] Narrator added (dose={(n.dose ? "ok" : "MISSING")}, bat={(n.bat ? "ok" : "MISSING")}, " +
                      $"weather={(n.weather ? "ok" : "none")}, lamps={n.lamps.Length}, key={GeminiClient.KeySource()}). Save the scene to keep it.");
        }

        static EdgeNarrator Current() => Object.FindAnyObjectByType<EdgeNarrator>();

        [MenuItem("EDGE/Narration/Test Line")]
        public static void TestLine()
        {
            var n = Current();
            if (Application.isPlaying && n) { n.SpeakManual(Sample); return; }
            var v = new MacSayVoice(n ? n.macVoice : "Karen", n ? n.macRate : 170);
            v.Speak(Sample);
            Debug.Log($"[EDGE Narration] Test line (mac say, voice {v.voice}): {Sample}");
        }

        [MenuItem("EDGE/Narration/Test Gemini Line (uses 1 API call)")]
        public static void TestGemini()
        {
            var n = Current();
            var client = n ? n.gemini : new GeminiClient();
            if (!client.HasKey)
            {
                Debug.LogWarning($"[EDGE Narration] No Gemini key. Set GEMINI_API_KEY (launchctl setenv) or create {GeminiClient.KeyFilePath}");
                return;
            }
            string sys = "You narrate a nature documentary about a Gould's long-eared bat at a Sydney bushland edge. One sentence, at most 20 words.";
            string ctx = "Context: {\"event\":\"entered_lamp_light\",\"budget_left_pct\":62,\"weather\":\"Overcast\",\"nearest_lamp\":{\"id\":3,\"dist_m\":4}}\nWrite the next narration line.";
            var voice = new MacSayVoice(n ? n.macVoice : "Karen", n ? n.macRate : 170);
            bool sent = client.GenerateLine(sys, ctx,
                line => { Debug.Log($"[EDGE Narration] Gemini ({client.model}) OK: {line}"); voice.Speak(line); },
                err => Debug.LogWarning($"[EDGE Narration] Gemini test failed: {err}"));
            if (sent) Debug.Log($"[EDGE Narration] Gemini request sent (key from {GeminiClient.KeySource()})...");
        }
    }
}
