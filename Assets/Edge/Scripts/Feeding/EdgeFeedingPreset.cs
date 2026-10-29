using UnityEngine;

namespace Edge.Feeding
{
    // Tunables for one feeding-game variant. Create via Assets > Create > EDGE > Feeding Preset,
    // or EDGE > Feeding Game > Create Default Presets.
    [CreateAssetMenu(menuName = "EDGE/Feeding Preset", fileName = "EdgeFeedingPreset")]
    public class EdgeFeedingPreset : ScriptableObject
    {
        [Header("Identity")]
        public string displayName = "Light-tolerant bat";
        [TextArea] public string speciesBlurb = "";
        [TextArea] public string endMessage = "";

        [Header("Round")]
        public float roundSeconds = 90f;
        [Tooltip("Hunger 0..1 lost per second")] public float hungerDrainPerSec = 0.022f;
        [Tooltip("Hunger regained per insect")] public float hungerPerInsect = 0.06f;
        public float startHunger = 0.7f;

        [Header("Insects")]
        [Range(10, 300)] public int insectCount = 220;
        [Tooltip("Share of insects that live around lamps (rest in dark vegetation)")]
        [Range(0f, 1f)] public float lampShare = 0.8f;
        [Tooltip("Swarm radius around a lamp head (m)")] public float lampSwarmRadius = 3.5f;
        [Tooltip("Dark insects: area around the path (m) and height band")]
        public float darkSpread = 20f;
        public Vector2 darkHeight = new Vector2(1f, 6f);
        [Tooltip("Eat radius around the bat (m)")] public float eatRadius = 0.9f;
        [Tooltip("Seconds before an eaten insect respawns")] public float respawnSeconds = 4f;
        public float insectSpeed = 1.2f;
        [Tooltip("Lamp moths orbit the lamp head at this distance range (m)")]
        public Vector2 lampOrbitRadius = new Vector2(0.5f, 2f);
        [Tooltip("Random darts per second per lamp moth")] public float dartRate = 0.6f;

        public enum EchoMode { Auto, On, Off }
        [Header("Echolocation cue (briefly outlines nearby moths)")]
        [Tooltip("Auto = on when light hurts (light-averse variant)")] public EchoMode echoMode = EchoMode.On;
        public bool EchoEnabled => echoMode == EchoMode.On || (echoMode == EchoMode.Auto && lightHurts);
        [Tooltip("Seconds between automatic pings (0 = key/handset button only)")] public float echoInterval = 0f;
        public float echoRadius = 20f;
        public float echoDuration = 1.5f;

        [Header("Light")]
        [Tooltip("Light hurts? false = light-tolerant (no budget)")] public bool lightHurts = false;
        [Tooltip("Variant light budget (FMM). The default exhibit budget is EdgeConfig.BMaxFmm = 60")]
        public float budgetFmm = 12f;
        [Tooltip("Multiplies the LampZone dose rate for this variant")] public float doseMultiplier = 2.5f;
        [Tooltip("Lux at the bat above which you count as 'in light'")] public float litLuxThreshold = 0.3f;
        [Tooltip("Speed multiplier while in light")] public float litSpeedMultiplier = 0.7f;
        [Tooltip("Extra steering instability while in light, 0..1")] [Range(0f, 1f)] public float litInstability = 0.5f;
        [Tooltip("Hunger 0..1 lost per second while in light (stress)")] public float litHungerDrainPerSec = 0.02f;
        [Tooltip("Seconds after the round starts before light hurts: the bat starts on the lit path and needs a moment to leave it")]
        public float lightGraceSeconds = 4f;
        [Tooltip("Ignore DoseModel instability for this variant")] public bool ignoreDoseInstability = true;

        [Header("Collisions")]
        [Tooltip("Hunger 0..1 lost per hard bump (trunk, post, rock...)")] public float bumpHungerCost = 0.03f;

        [Header("Flight")]
        public float speedMultiplier = 1f;

        [Header("Ecology: lamps take the food")]
        [Tooltip("Vacuum-cleaner effect: how strongly lamps pull dark insects into their swarm during the round (0 = off)")]
        [Range(0f, 1f)] public float lampPull = 0.5f;
        [Tooltip("Dark insects closer than this to a lamp (m, horizontal) feel its pull")]
        public float lampPullRange = 22f;
        [Tooltip("Light-tolerant rival bats feeding at the lamps. -1 = auto: 3 when light hurts you, else 1")]
        public int rivalBats = -1;
        public int RivalCount => rivalBats >= 0 ? rivalBats : (lightHurts ? 3 : 1);

        [Header("Light effects (EdgeLightEffects)")]
        [Tooltip("How much light disturbs this bat: screen, hand and steering. -1 = auto: 1 when light hurts, else 0 (the overlays blend in linear space, so even a few percent greys out the night)")]
        public float lightEffectScale = -1f;
        public float LightEffectScale => lightEffectScale >= 0f ? Mathf.Clamp01(lightEffectScale) : (lightHurts ? 1f : 0f);
        [Tooltip("Street lamp glow and light as this bat sees it (visual only; the lux and dose maths are unchanged). -1 = auto: 1 when light hurts, else 0.35")]
        public float lampBrightness = -1f;
        public float LampBrightness => lampBrightness >= 0f ? Mathf.Clamp01(lampBrightness) : (lightHurts ? 1f : 0.35f);
        [Tooltip("Lamp glow as this bat sees it: the lit fog and the bloom round the lamp head, the halo that blinds (visual only). -1 = auto: 1 when light hurts, else 0.01")]
        public float lampGlow = -1f;
        public float LampGlow => lampGlow >= 0f ? Mathf.Clamp01(lampGlow) : (lightHurts ? 1f : 0.01f);
        [TextArea] public string playHint = "";

        public static EdgeFeedingPreset CreateTolerant()
        {
            var p = CreateInstance<EdgeFeedingPreset>();
            p.name = "Feeding_LightTolerant";
            p.displayName = "Light-tolerant bat";
            p.speciesBlurb = "Gould's wattled bat (Chalinolobus gouldii): fast open-air forager that hunts moths at streetlights.";
            p.endMessage = "Light-tolerant species can gain from streetlights: moths gather at lamps, and these fast open-air bats exploit them.";
            p.lightHurts = false; p.ignoreDoseInstability = true; p.speedMultiplier = 1.15f;
            p.lampShare = 0.75f; p.hungerDrainPerSec = 0.02f;   // same insect field as the averse bat: only the bat differs
            p.playHint = "Moths crowd the lamps. Fly straight in.";
            return p;
        }

        public static EdgeFeedingPreset CreateAverse()
        {
            var p = CreateInstance<EdgeFeedingPreset>();
            p.name = "Feeding_LightAverse";
            p.displayName = "Light-averse bat";
            p.speciesBlurb = "Gould's long-eared bat (Nyctophilus gouldi): slow clutter forager that avoids light - EDGE's subject.";
            p.endMessage = "Light-averse species like Gould's long-eared bat lose foraging habitat: the insects gather at lamps they cannot use, and the dark is left poorer.";
            p.lightHurts = true; p.ignoreDoseInstability = false; p.speedMultiplier = 0.85f;
            p.lampShare = 0.75f; p.hungerDrainPerSec = 0.012f; p.bumpHungerCost = 0.015f; // clutter specialist: agile, small penalty
            p.budgetFmm = 30f; p.doseMultiplier = 2.5f;   // about 14 s under a lamp in total, so a dash for a moth is possible
            p.playHint = "Light hurts you. Hunt the scattered moths in the dark.";
            return p;
        }
    }
}
