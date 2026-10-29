using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace MoonlitIsland.Editor
{
    /// <summary>Creates the scene's native HDRP ocean without changing a shared pipeline asset.</summary>
    public static class IslandOceanBuilder
    {
        private const string ProfilePath = "Assets/MoonlitIsland/Settings/OceanRendering.asset";

        public static GameObject Create(Transform parent)
        {
            var ocean = new GameObject("Ocean • moonlit open water");
            ocean.transform.SetParent(parent, false);
            ocean.transform.position = Vector3.zero;

            var water = ocean.AddComponent<WaterSurface>();
            water.surfaceType = WaterSurfaceType.OceanSeaLake;
            water.geometryType = WaterGeometryType.Infinite;
            water.timeMultiplier = 0.7f;

            // Two gentle swell bands keep a visible silhouette at the shoreline.
            // These are GPU waves; no CPU height queries are needed by this scene.
            water.scriptInteractions = false;
            water.cpuEvaluateRipples = false;
            water.repetitionSize = 350f;
            water.largeWindSpeed = 19f;
            water.largeOrientationValue = 28f;
            water.largeChaos = 0.55f;
            water.largeBand0Multiplier = 0.35f;
            water.largeBand1Multiplier = 0.55f;
            water.ripples = true;
            water.ripplesMotionMode = WaterPropertyOverrideMode.Custom;
            water.ripplesWindSpeed = 6f;
            water.ripplesOrientationValue = 40f;
            water.ripplesChaos = 0.4f;

            water.startSmoothness = 0.96f;
            water.endSmoothness = 0.87f;
            water.smoothnessFadeStart = 100f;
            water.smoothnessFadeDistance = 900f;
            water.refractionColor = new Color(0.09f, 0.34f, 0.39f).linear;
            water.scatteringColor = new Color(0.05f, 0.23f, 0.28f).linear;
            water.absorptionDistance = 4.5f;
            water.maxRefractionDistance = 0.35f;
            water.ambientScattering = 0.3f;
            water.heightScattering = 0.15f;
            water.displacementScattering = 0.12f;
            water.directLightTipScattering = 0.5f;
            water.directLightBodyScattering = 0.35f;

            water.foam = true;
            water.foamResolution = WaterSurface.WaterDecalRegionResolution.Resolution512;
            water.foamColor = new Color(0.77f, 0.87f, 0.92f);
            water.foamTextureTiling = 0.16f;
            water.foamSmoothness = 0.5f;
            water.foamPersistenceMultiplier = 0.25f;
            water.simulationFoamAmount = 0.13f;
            water.decalRegionSize = new Vector2(300f, 300f);

            // The camera remains above water; omit the unused underwater and caustic passes.
            water.caustics = false;
            water.underWater = false;
            water.tessellation = true;
            water.maxTessellationFactor = 2f;
            water.tessellationFactorFadeStart = 70f;
            water.tessellationFactorFadeRange = 450f;

            var volumeObject = new GameObject("Ocean rendering settings");
            volumeObject.transform.SetParent(ocean.transform, false);
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 20f;
            volume.sharedProfile = CreateRenderingProfile();
            return ocean;
        }

        /// <summary>Useful when a camera uses custom frame settings instead of HDRP defaults.</summary>
        public static void EnableCameraWater(HDAdditionalCameraData cameraData)
        {
            cameraData.customRenderingSettings = true;
            var fields = new[]
            {
                FrameSettingsField.TransparentObjects,
                FrameSettingsField.Refraction,
                FrameSettingsField.Water
            };
            foreach (var field in fields)
            {
                cameraData.renderingPathCustomFrameSettings.SetEnabled(field, true);
                cameraData.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field] = true;
            }
        }

        private static VolumeProfile CreateRenderingProfile()
        {
            EnsureFolder("Assets", "MoonlitIsland");
            EnsureFolder("Assets/MoonlitIsland", "Settings");
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.name = "Moonlit Ocean Rendering";
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }

            if (!profile.TryGet<WaterRendering>(out var rendering))
            {
                rendering = profile.Add<WaterRendering>(true);
                AssetDatabase.AddObjectToAsset(rendering, profile);
            }
            rendering.enable.Override(true);
            rendering.triangleSize.Override(30f);
            rendering.ambientProbeDimmer.Override(0.7f);
            EditorUtility.SetDirty(rendering);
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + name))
                AssetDatabase.CreateFolder(parent, name);
        }
    }
}
