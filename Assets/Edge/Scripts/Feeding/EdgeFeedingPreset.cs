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
        [Tooltip("Auto = on when light hurts (light-averse variant)")] public EchoMode echoMode = EchoMode.Auto;
        public bool EchoEnabled => echoMode == EchoMode.On || (echoMode == EchoMode.Auto && lightHurts);
        [Tooltip("Seconds between automatic pings (0 = key only)")] public float echoInterval = 2.5f;
        public float echoRadius = 14f;
        public float echoDuration = 0.6f;

        [Header("Light")]
        [Tooltip("Light hurts? false = light-tolerant (no budget)")] public bool lightHurts = false;
        [Tooltip("Variant light budget (FMM). The default exhibit budget is EdgeConfig.BMaxFmm = 60")]
        public float budgetFmm = 12f;
        [Tooltip("Multiplies the LampZone dose rate for this variant")] public float doseMultiplier = 2.5f;
        [Tooltip("Lux at the bat above which you count as 'in light'")] public float litLuxThreshold = 0.3f;
        [Tooltip("Speed multiplier while in light")] public float litSpeedMultiplier = 0.7f;
        [Tooltip("Extra steering instability while in light, 0..1")] [Range(0f, 1f)] public float litInstability = 0.5f;
        [Tooltip("Hunger 0..1 lost per second while in light (stress)")] public float litHungerDrainPerSec = 0.02f;
        [Tooltip("Ignore DoseModel instability for this variant")] public bool ignoreDoseInstability = true;

        [Header("Collisions")]
        [Tooltip("Hunger 0..1 lost per hard bump (trunk, post, rock...)")] public float bumpHungerCost = 0.03f;

        [Header("Flight")]
        public float speedMultiplier = 1f;

        public static EdgeFeedingPreset CreateTolerant()
        {
            var p = CreateInstance<EdgeFeedingPreset>();
            p.name = "Feeding_LightTolerant";
            p.displayName = "Light-tolerant bat";
            p.speciesBlurb = "Gould's wattled bat (Chalinolobus gouldii): fast open-air forager that hunts moths at streetlights.";
            p.endMessage = "Light-tolerant species can gain from streetlights: moths gather at lamps, and these fast open-air bats exploit them.";
            p.lightHurts = false; p.ignoreDoseInstability = true; p.speedMultiplier = 1.15f;
            p.lampShare = 0.8f; p.hungerDrainPerSec = 0.02f;
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
            p.lampShare = 0.75f; p.hungerDrainPerSec = 0.024f; p.bumpHungerCost = 0.015f; // clutter specialist: agile, small penalty
            p.budgetFmm = 12f; p.doseMultiplier = 2.5f;
            return p;
        }
    }
}
