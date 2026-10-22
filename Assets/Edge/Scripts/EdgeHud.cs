using UnityEngine;

namespace Edge
{
    // The one budget, split by source. Real light (cyan) and virtual lamps
    // (amber) are drawn on the same bar in the same units — the scene rule
    // from FINAL_PLAN section 8.
    public class EdgeHud : MonoBehaviour
    {
        public DoseModel dose;
        public BatFlightController flight;
        public CameraToggle cams;
        public bool showFps = true;   // F toggles

        [Header("Compact mode (set by EdgeFeedingGame while a round runs)")]
        [Tooltip("Hide all lines except a tiny tag status dot")] public bool compact = false;
        [Tooltip("In compact mode also hide the exhibit budget-spent overlay")] public bool hideBudget = false;

        float _fpsSmoothed;

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f) _fpsSmoothed = _fpsSmoothed <= 0f ? 1f / dt : Mathf.Lerp(_fpsSmoothed, 1f / dt, 0.05f);
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.fKey.wasPressedThisFrame) showFps = !showFps;
        }

        void OnGUI()
        {
            if (compact)
            {
                // tiny tag status dot: green = tag streaming, grey = keyboard/no tag
                bool tag = flight && flight.TagActive;
                GUI.color = tag ? new Color(0.3f, 1f, 0.4f, 0.9f) : new Color(0.6f, 0.6f, 0.6f, 0.6f);
                GUI.DrawTexture(new Rect(12f, 12f, 8f, 8f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                if (dose && dose.Exhausted && !hideBudget)
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.55f);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
                return;
            }
            if (showFps)
                GUI.Label(new Rect(Screen.width - 150f, 12f, 140f, 22f),
                    $"{_fpsSmoothed:F0} fps  {1000f / Mathf.Max(_fpsSmoothed, 0.01f):F1} ms");
            if (!dose) return;
            Texture2D white = Texture2D.whiteTexture;

            const float w = 440f;
            float x = 24f;
            float y = 24f;

            GUI.Box(new Rect(x - 8f, y - 8f, w + 16f, 142f), GUIContent.none);
            GUI.Label(new Rect(x, y, w, 22f),
                $"EDGE — tonight's budget {dose.BudgetTonight:F0} FMM   (C = {dose.CloudC:F2})");
            y += 24f;

            float frac = Mathf.Clamp01(dose.Fraction);
            float realW = w * Mathf.Clamp01(dose.RealFmm / dose.BudgetTonight);
            float virtW = w * Mathf.Clamp01(dose.VirtualFmm / dose.BudgetTonight);
            GUI.color = new Color(0.15f, 0.15f, 0.15f);
            GUI.DrawTexture(new Rect(x, y, w, 18f), white);
            GUI.color = new Color(0.3f, 0.8f, 1f);
            GUI.DrawTexture(new Rect(x, y, realW, 18f), white);
            GUI.color = new Color(1f, 0.65f, 0.2f);
            GUI.DrawTexture(new Rect(x + realW, y, virtW, 18f), white);
            GUI.color = Color.white;
            y += 22f;

            GUI.Label(new Rect(x, y, w, 22f),
                $"dose {dose.TotalFmm:F1} FMM ({frac * 100f:F0}%)   real {dose.RealFmm:F1} + virtual {dose.VirtualFmm:F1}");
            y += 24f;
            GUI.Label(new Rect(x, y, w, 22f),
                $"instability {dose.Instability01 * 100f:F0}%    " +
                $"{(flight && flight.TagActive ? "TAG" : "KEYBOARD arrows/WASD")}    " +
                $"{(cams && cams.ThirdPerson ? "THIRD-PERSON" : "FPV")}  [C]");

            if (flight && flight.tagInput)
                GUI.Label(new Rect(x, y + 24f, w + 200f, 22f), flight.tagInput.Status + "   [Z] zero" +
                    (flight.tagInput.LuxFresh ? $"   tag light {flight.tagInput.LatestLux:F0} lx" : ""));

            if (dose.Exhausted)
            {
                // Budget spent: still flyable, vision at the floor (FINAL_PLAN 5.6)
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), white);
                GUI.color = Color.white;
                GUI.Label(new Rect(x, Screen.height - 40f, 700f, 24f),
                    "Budget spent. You can still fly — you just cannot see well.");
            }
        }
    }
}
