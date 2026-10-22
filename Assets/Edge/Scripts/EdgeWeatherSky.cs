// Future improvement (not implemented): photoreal lightning. Current storm
// is a scripted double-flicker on a dedicated directional light. Upgrade
// path: procedural branching bolt mesh or a VFX Graph strike paired with an
// HDRP volumetric point/area light flash at the strike, and lightning-lit
// HDRP Volumetric Clouds (pulse cloud emission/ambient). Asset Store
// lightning packs are another route. Note: Crest is an ocean/water renderer,
// only useful for wet surfaces/puddles, not lightning. See Docs/WEATHER_NOTES.md.
using System;
using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Edge
{
    // Weather-driven night sky for the EDGE scenes.
    // In Play mode it fetches the same Open-Meteo AIFS forecast the rev3
    // firmware uses (net.cpp: models=ecmwf_aifs025_single, site from
    // edge_config.h) and picks a WeatherType for the current hour. The look is
    // applied through a separate, higher-priority global Volume that only
    // carries weather overrides, so EdgeNightProfile and the EDGE > Lighting
    // toggles are left alone. When the Moonlight is at daylight-preview levels
    // the weather layer switches itself off. Offline/failed fetch: weather
    // layer stays off (current sky kept).
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class EdgeWeatherSky : MonoBehaviour
    {
        [Header("Forecast (same as firmware net.cpp)")]
        public string latitude = "-33.745";
        public string longitude = "151.04";
        public bool fetchOnPlay = true;
        public float timeoutSeconds = 8f;
        [Tooltip("precipitation mm/h at or above this is Rain (below, >0.1 is LightRain)")]
        public float rainMmPerHour = 1.0f;
        [Tooltip("precipitation mm/h at or above this is Thunderstorm even without a weather_code")]
        public float stormMmPerHour = 6.0f;

        [Header("Testing override")]
        public bool forceWeather = false;
        public WeatherType forcedType = WeatherType.Overcast;

        [Header("Presets (null slot = built-in default)")]
        public EdgeWeatherPreset[] presets = new EdgeWeatherPreset[6];
        public int volumePriority = 10;

        [Header("City light-pollution horizon")]
        public bool cityGlow = true;
        [ColorUsage(false, true)] public Color sodiumColor = new Color(1f, 0.55f, 0.18f);
        [Min(0)] public float glowIntensity = 1.5f;
        [Tooltip("Compass bearing of the city (Sydney CBD is SE of the firmware site)")]
        public float cityAzimuthDeg = 135f;
        public float glowRadius = 190f;
        public float glowHeight = 55f;
        public bool hideFarTreeline = false;

        [Header("Rain / storm")]
        public AudioClip thunderClip; // optional; a procedural rumble is synthesized if empty
        [Range(0, 1)] public float thunderVolume = 0.6f;
        public Vector2 lightningInterval = new Vector2(6f, 20f);
        public float lightningLux = 25f;

        [Header("State (read-only)")]
        public bool hasWeather;
        public WeatherType current;
        public float lastCloudPct = float.NaN, lastPrecipMm = float.NaN;
        public int lastWeatherCode = -1;

        public event Action<WeatherType> WeatherApplied;

        const float DaylightLux = 1000f;
        GameObject _volGo, _glowGo, _rainGo, _flashGo;
        Volume _vol; VolumeProfile _profile; Material _glowMat, _rainMat;
        ParticleSystem _rain; Light _flash; AudioSource _audio;
        Coroutine _storm;

        void OnEnable()
        {
            if (forceWeather) Apply(forcedType, "forced");
        }

        void Start()
        {
            if (!Application.isPlaying) return;
            if (forceWeather) Apply(forcedType, "forced");
            else if (fetchOnPlay) StartCoroutine(Fetch());
        }

        void OnDisable() { Teardown(); }

        void Update()
        {
            if (!hasWeather || !_vol) return;
            bool day = IsDaylightPreview();
            _vol.weight = day ? 0f : 1f;
            if (_glowGo) _glowGo.SetActive(!day && cityGlow);
            if (_rainGo && _rainGo.activeSelf == day) _rainGo.SetActive(!day);
            var cam = Camera.main ? Camera.main : (Camera.current ? Camera.current : null);
            if (_rainGo && cam) _rainGo.transform.position = cam.transform.position + Vector3.up * 12f;
            if (_glowGo && cam) { var p = cam.transform.position; _glowGo.transform.position = new Vector3(p.x, 0f, p.z); }
        }

        static bool IsDaylightPreview()
        {
            var m = GameObject.Find("Moonlight");
            return m && m.TryGetComponent(out Light l) && l.intensity > DaylightLux;
        }

        // ---------------- forecast ----------------
        string Url => "https://api.open-meteo.com/v1/forecast?latitude=" + latitude + "&longitude=" + longitude +
                      "&hourly=cloud_cover,precipitation,weather_code,temperature_2m&daily=sunset" +
                      "&models=ecmwf_aifs025_single&timezone=Australia%2FSydney&forecast_days=2&timeformat=unixtime";

        public IEnumerator Fetch()
        {
            using var req = UnityWebRequest.Get(Url);
            req.timeout = Mathf.CeilToInt(timeoutSeconds);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[EDGE Weather] fetch failed ({req.error}); keeping current sky");
                yield break;
            }
            if (!TryParse(req.downloadHandler.text, out var cloud, out var precip, out var code))
            {
                Debug.LogWarning("[EDGE Weather] could not parse forecast; keeping current sky");
                yield break;
            }
            lastCloudPct = cloud; lastPrecipMm = precip; lastWeatherCode = code;
            Apply(Classify(cloud, precip, code), $"AIFS cloud={cloud:0}% precip={precip:0.0}mm code={code}");
        }

        public WeatherType Classify(float cloudPct, float precipMm, int code)
        {
            if ((code >= 95 && code <= 99) || (!float.IsNaN(precipMm) && precipMm >= stormMmPerHour)) return WeatherType.Thunderstorm;
            if (!float.IsNaN(precipMm) && precipMm >= rainMmPerHour) return WeatherType.Rain;
            if ((!float.IsNaN(precipMm) && precipMm >= 0.1f) || (code >= 51 && code <= 67) || (code >= 80 && code <= 82))
                return code >= 63 && code != 80 && code != 66 ? WeatherType.Rain : WeatherType.LightRain;
            if (float.IsNaN(cloudPct) || cloudPct >= 75f) return WeatherType.Overcast; // firmware: no evidence = cloudy
            return cloudPct >= 25f ? WeatherType.PartlyCloudy : WeatherType.ClearNight;
        }

        static float[] ReadArray(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (!m.Success) return null;
            var parts = m.Groups[1].Value.Split(',');
            var r = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                r[i] = float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN;
            return r;
        }

        static bool TryParse(string json, out float cloud, out float precip, out int code)
        {
            cloud = precip = float.NaN; code = -1;
            int h = json.IndexOf("\"hourly\":", StringComparison.Ordinal);
            if (h < 0) return false;
            json = json.Substring(h);
            var time = ReadArray(json, "time");
            var cc = ReadArray(json, "cloud_cover");
            if (time == null || cc == null || time.Length == 0) return false;
            var pr = ReadArray(json, "precipitation");
            var wc = ReadArray(json, "weather_code");
            double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < time.Length; i++) { double d = Math.Abs(time[i] - now); if (d < bd) { bd = d; best = i; } }
            // float can't hold unix seconds exactly; ±128 s is fine at hourly resolution
            cloud = best < cc.Length ? cc[best] : float.NaN;
            if (pr != null && best < pr.Length) precip = pr[best];
            if (wc != null && best < wc.Length && !float.IsNaN(wc[best])) code = (int)wc[best];
            return true;
        }

        // ---------------- apply ----------------
        EdgeWeatherPreset PresetFor(WeatherType t)
        {
            int i = (int)t;
            return presets != null && i < presets.Length && presets[i] ? presets[i] : EdgeWeatherPreset.CreateDefault(t);
        }

        public void Apply(WeatherType t, string why = "")
        {
            var p = PresetFor(t);
            EnsureVolume();
            _profile.TryGet(out VisualEnvironment ve);
            ve.cloudType.Override(p.cloudOpacity > 0.001f ? (int)CloudType.CloudLayer : 0);
            _profile.TryGet(out CloudLayer cl);
            cl.opacity.Override(p.cloudOpacity);
            cl.layerA.tint.Override(Color.Lerp(p.cloudTint, sodiumColor * 0.8f, cityGlow ? p.cityGlow * 0.35f : 0f));
            cl.layerA.exposure.Override(p.cloudExposure);
            cl.layerA.opacityR.Override(1f);
            cl.layerA.opacityG.Override(p.cloudOpacity > 0.9f ? 1f : 0.3f);
            _profile.TryGet(out PhysicallyBasedSky pbs);
            pbs.spaceEmissionMultiplier.Override(p.starMultiplier);
            _profile.TryGet(out Fog fog);
            fog.meanFreePath.Override(p.fogMeanFreePath);
            fog.albedo.Override(p.fogAlbedo);

            var tree = FindInactive("Far Treeline");
            if (tree) tree.SetActive(!hideFarTreeline);

            EnsureGlow();
            if (_glowMat) _glowMat.SetColor("_UnlitColor", sodiumColor * glowIntensity * p.cityGlow);
            if (_glowGo) _glowGo.SetActive(cityGlow);

            SetupRain(p.rainParticlesPerSecond);
            if (_storm != null) { StopCoroutine(_storm); _storm = null; }
            if (_flash) _flash.intensity = 0f;
            if (p.lightning && Application.isPlaying) _storm = StartCoroutine(Storm());

            current = t; hasWeather = true;
            if (_vol) _vol.weight = IsDaylightPreview() ? 0f : 1f;
            Debug.Log($"[EDGE Weather] {t} ({why}) clouds={p.cloudOpacity:0.##} fog={p.fogMeanFreePath}m glow={p.cityGlow:0.##} rain={p.rainParticlesPerSecond}/s lightning={p.lightning}");
            WeatherApplied?.Invoke(t);
        }

        static GameObject FindInactive(string n)
        {
            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t.name == n && t.gameObject.scene.IsValid()) return t.gameObject;
            return null;
        }

        GameObject Child(string n)
        {
            var go = new GameObject(n) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            return go;
        }

        void EnsureVolume()
        {
            if (_vol) return;
            _volGo = Child("Weather Volume (runtime)");
            _vol = _volGo.AddComponent<Volume>();
            _vol.isGlobal = true; _vol.priority = volumePriority;
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.hideFlags = HideFlags.DontSave;
            _profile.Add<VisualEnvironment>();
            _profile.Add<CloudLayer>();
            _profile.Add<PhysicallyBasedSky>();
            _profile.Add<Fog>();
            _vol.sharedProfile = _profile;
        }

        static Material MakeUnlitAdditive(Texture2D tex, bool fog)
        {
            var sh = Shader.Find("HDRP/Unlit");
            if (!sh) return null;
            var m = new Material(sh) { hideFlags = HideFlags.DontSave };
            m.SetTexture("_UnlitColorMap", tex);
            HDMaterial.SetSurfaceType(m, true);
            m.SetFloat("_BlendMode", 1f); // BlendingMode.Additive
            m.SetFloat("_EnableFogOnTransparent", fog ? 1f : 0f);
            m.SetFloat("_CullMode", 0f);
            m.SetFloat("_CullModeForward", 0f);
            m.SetFloat("_DoubleSidedEnable", 1f);
            HDMaterial.ValidateMaterial(m);
            return m;
        }

        void EnsureGlow()
        {
            if (_glowGo) return;
            const int W = 256, H = 64;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            for (int x = 0; x < W; x++)
            {
                float az = x / (float)W * 360f;
                float d = Mathf.Abs(Mathf.DeltaAngle(az, cityAzimuthDeg)) / 180f;
                float azW = Mathf.Lerp(1f, 0.25f, Mathf.SmoothStep(0f, 1f, d)); // brightest toward the city, faint all round
                for (int y = 0; y < H; y++)
                {
                    float v = y / (float)(H - 1);
                    float a = azW * Mathf.Exp(-v * 4.5f) * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - v) * 3f);
                    tex.SetPixel(x, y, new Color(a, a, a, a));
                }
            }
            tex.Apply();
            _glowMat = MakeUnlitAdditive(tex, false);
            if (!_glowMat) return;
            _glowGo = Child("City Glow Horizon (runtime)");
            var mf = _glowGo.AddComponent<MeshFilter>();
            var mr = _glowGo.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
            mr.sharedMaterial = _glowMat;
            const int seg = 96;
            var v3 = new Vector3[(seg + 1) * 2]; var uv = new Vector2[v3.Length]; var tri = new int[seg * 6];
            for (int i = 0; i <= seg; i++)
            {
                float u = i / (float)seg, ang = u * Mathf.PI * 2f; // u=0 north, clockwise (compass)
                var dir = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * glowRadius;
                v3[i * 2] = dir + Vector3.down * 5f; v3[i * 2 + 1] = dir + Vector3.up * glowHeight;
                uv[i * 2] = new Vector2(u, 0f); uv[i * 2 + 1] = new Vector2(u, 1f);
                if (i < seg) { int a = i * 2, k = i * 6; tri[k] = a; tri[k + 1] = a + 1; tri[k + 2] = a + 2; tri[k + 3] = a + 2; tri[k + 4] = a + 1; tri[k + 5] = a + 3; }
            }
            var mesh = new Mesh { name = "Edge city glow ring", hideFlags = HideFlags.DontSave, vertices = v3, uv = uv, triangles = tri };
            mesh.bounds = new Bounds(Vector3.up * glowHeight * 0.5f, Vector3.one * glowRadius * 2.2f);
            mf.sharedMesh = mesh;
        }

        void SetupRain(int rate)
        {
            if (rate <= 0) { if (_rainGo) _rainGo.SetActive(false); return; }
            if (!_rainGo)
            {
                // procedural streak: thin vertical line, soft ends
                var tex = new Texture2D(8, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                for (int y = 0; y < 64; y++) for (int x = 0; x < 8; x++)
                {
                    float a = Mathf.Sin(y / 63f * Mathf.PI) * Mathf.Clamp01(1f - Mathf.Abs(x - 3.5f) / 3.5f);
                    tex.SetPixel(x, y, new Color(a, a, a, a));
                }
                tex.Apply();
                _rainMat = MakeUnlitAdditive(tex, true);
                _rainGo = Child("Rain (runtime)");
                _rain = _rainGo.AddComponent<ParticleSystem>();
                _rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = _rain.main;
                main.loop = true; main.playOnAwake = true;
                main.startLifetime = 1.6f; main.startSpeed = 0f;
                main.startSize = 0.025f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 4000;
                main.startColor = new Color(0.6f, 0.65f, 0.75f, 0.35f);
                var vel = _rain.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
                vel.x = new ParticleSystem.MinMaxCurve(-0.6f, -0.3f); vel.y = new ParticleSystem.MinMaxCurve(-11f, -9f); vel.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);
                var sh = _rain.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(30f, 1f, 30f);
                var r = _rainGo.GetComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.05f; r.lengthScale = 1f;
                r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                r.sharedMaterial = _rainMat;
                _rainMat?.SetColor("_UnlitColor", new Color(0.35f, 0.38f, 0.45f, 1f));
            }
            _rainGo.SetActive(true);
            var em = _rain.emission; em.rateOverTime = rate;
            if (!_rain.isPlaying) _rain.Play();
        }

        IEnumerator Storm()
        {
            if (!_flashGo)
            {
                _flashGo = Child("Lightning Flash (runtime)");
                _flashGo.transform.rotation = Quaternion.Euler(60f, 20f, 0f);
                _flash = _flashGo.AddComponent<Light>();
                _flash.type = LightType.Directional; _flash.shadows = LightShadows.None;
                _flash.color = new Color(0.8f, 0.85f, 1f);
                _flashGo.AddComponent<HDAdditionalLightData>();
                _flash.lightUnit = LightUnit.Lux; _flash.intensity = 0f;
                _audio = _flashGo.AddComponent<AudioSource>();
                _audio.playOnAwake = false; _audio.spatialBlend = 0f;
                if (!thunderClip) thunderClip = SynthThunder();
            }
            while (true)
            {
                yield return new WaitForSeconds(UnityEngine.Random.Range(lightningInterval.x, lightningInterval.y));
                if (IsDaylightPreview()) continue;
                _flashGo.transform.rotation = Quaternion.Euler(UnityEngine.Random.Range(35f, 75f), UnityEngine.Random.Range(0f, 360f), 0f);
                // double flicker; own light only, Moonlight/exposure never touched
                _flash.intensity = lightningLux; yield return new WaitForSeconds(0.06f);
                _flash.intensity = lightningLux * 0.1f; yield return new WaitForSeconds(0.07f);
                _flash.intensity = lightningLux * 0.8f; yield return new WaitForSeconds(0.08f);
                _flash.intensity = 0f;
                if (thunderClip && _audio) StartCoroutine(Rumble(UnityEngine.Random.Range(1f, 3f)));
            }
        }

        IEnumerator Rumble(float delay)
        {
            yield return new WaitForSeconds(delay);
            _audio.pitch = UnityEngine.Random.Range(0.85f, 1.1f);
            _audio.PlayOneShot(thunderClip, thunderVolume);
        }

        // Procedural brown-noise rumble (no external asset, no licence needed).
        static AudioClip SynthThunder()
        {
            const int sr = 22050; const float len = 4f;
            int n = (int)(sr * len);
            var d = new float[n]; float b = 0f; var rng = new System.Random(5047);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                b = Mathf.Clamp(b + ((float)rng.NextDouble() * 2f - 1f) * 0.02f, -1f, 1f) * 0.998f;
                float env = Mathf.Clamp01(t / 0.15f) * Mathf.Exp(-t * 0.9f) * (0.7f + 0.3f * Mathf.Sin(t * 5.3f) * Mathf.Sin(t * 1.7f));
                d[i] = b * env * 3f;
            }
            var c = AudioClip.Create("EdgeThunderRumble", n, 1, sr, false);
            c.SetData(d, 0);
            return c;
        }

        void Teardown()
        {
            if (_storm != null) { StopCoroutine(_storm); _storm = null; }
            foreach (var g in new[] { _volGo, _glowGo, _rainGo, _flashGo })
                if (g) { if (Application.isPlaying) Destroy(g); else DestroyImmediate(g); }
            if (_profile) { if (Application.isPlaying) Destroy(_profile); else DestroyImmediate(_profile); }
            _vol = null; hasWeather = false;
            var tree = FindInactive("Far Treeline");
            if (tree) tree.SetActive(true);
        }

        // ---------------- testing ----------------
        void Force(WeatherType t) { forceWeather = true; forcedType = t; Apply(t, "forced"); }
        [ContextMenu("Weather/Force Clear Night")] void FClear() => Force(WeatherType.ClearNight);
        [ContextMenu("Weather/Force Partly Cloudy")] void FPartly() => Force(WeatherType.PartlyCloudy);
        [ContextMenu("Weather/Force Overcast")] void FOver() => Force(WeatherType.Overcast);
        [ContextMenu("Weather/Force Light Rain")] void FLight() => Force(WeatherType.LightRain);
        [ContextMenu("Weather/Force Rain")] void FRain() => Force(WeatherType.Rain);
        [ContextMenu("Weather/Force Thunderstorm")] void FStorm() => Force(WeatherType.Thunderstorm);
        [ContextMenu("Weather/Clear Override (keep current sky)")]
        void ClearForce() { forceWeather = false; Teardown(); Debug.Log("[EDGE Weather] override cleared, base sky restored"); }
        [ContextMenu("Weather/Fetch Now (Play mode)")]
        void FetchNow() { if (Application.isPlaying) { forceWeather = false; StartCoroutine(Fetch()); } else Debug.Log("[EDGE Weather] enter Play mode to fetch"); }

        void OnValidate()
        {
            if (presets == null || presets.Length != 6) Array.Resize(ref presets, 6);
        }
    }
}
