using UnityEngine;

namespace Edge
{
    public enum WeatherType { ClearNight, PartlyCloudy, Overcast, LightRain, Rain, Thunderstorm }

    // Per-weather sky look. Only these values go into the weather Volume, which
    // sits above the scene's base night profile, so the base profile (and the
    // EDGE > Lighting toggles that edit it) is never touched.
    [CreateAssetMenu(menuName = "EDGE/Weather Preset", fileName = "EdgeWeather_")]
    public class EdgeWeatherPreset : ScriptableObject
    {
        public WeatherType type;
        [Range(0, 1)] public float cloudOpacity = 0f;
        [ColorUsage(false, true)] public Color cloudTint = new Color(0.45f, 0.45f, 0.5f);
        public float cloudExposure = 0f;
        [Min(0)] public float starMultiplier = 1f;      // PhysicallyBasedSky space emission (stars hidden by cloud)
        [Min(1)] public float fogMeanFreePath = 120f;
        public Color fogAlbedo = new Color(0.55f, 0.60f, 0.70f);
        [Range(0, 1)] public float cityGlow = 0.3f;     // clouds reflect city light back down
        [Min(0)] public int rainParticlesPerSecond = 0;
        public bool lightning = false;

        public static EdgeWeatherPreset CreateDefault(WeatherType t)
        {
            var p = CreateInstance<EdgeWeatherPreset>();
            p.type = t; p.name = "EdgeWeather_" + t;
            switch (t)
            {
                case WeatherType.ClearNight:
                    p.cloudOpacity = 0f; p.starMultiplier = 1f; p.fogMeanFreePath = 140f; p.cityGlow = 0.25f; break;
                case WeatherType.PartlyCloudy:
                    p.cloudOpacity = 0.5f; p.starMultiplier = 0.6f; p.fogMeanFreePath = 120f; p.cityGlow = 0.55f; break;
                case WeatherType.Overcast:
                    p.cloudOpacity = 1f; p.starMultiplier = 0f; p.fogMeanFreePath = 90f; p.cityGlow = 1f;
                    p.cloudTint = new Color(0.55f, 0.45f, 0.38f); break;
                case WeatherType.LightRain:
                    p.cloudOpacity = 1f; p.starMultiplier = 0f; p.fogMeanFreePath = 70f; p.cityGlow = 0.85f;
                    p.cloudTint = new Color(0.45f, 0.40f, 0.36f); p.fogAlbedo = new Color(0.5f, 0.52f, 0.56f);
                    p.rainParticlesPerSecond = 600; break;
                case WeatherType.Rain:
                    p.cloudOpacity = 1f; p.starMultiplier = 0f; p.fogMeanFreePath = 55f; p.cityGlow = 0.8f;
                    p.cloudTint = new Color(0.40f, 0.36f, 0.33f); p.fogAlbedo = new Color(0.5f, 0.52f, 0.56f);
                    p.rainParticlesPerSecond = 1500; break;
                case WeatherType.Thunderstorm:
                    p.cloudOpacity = 1f; p.starMultiplier = 0f; p.fogMeanFreePath = 45f; p.cityGlow = 0.75f;
                    p.cloudTint = new Color(0.33f, 0.30f, 0.30f); p.fogAlbedo = new Color(0.45f, 0.47f, 0.52f);
                    p.rainParticlesPerSecond = 2500; p.lightning = true; break;
            }
            return p;
        }
    }
}
