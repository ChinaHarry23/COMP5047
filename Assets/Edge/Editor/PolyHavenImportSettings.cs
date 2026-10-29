using UnityEditor;

namespace Edge.EditorTools
{
    // Import settings for the CC0 Poly Haven assets under Assets/Edge/ThirdParty/PolyHaven.
    public class PolyHavenImportSettings : AssetPostprocessor
    {
        const string Root = "Assets/Edge/ThirdParty/PolyHaven/";

        // Bumping the version forces Unity to reimport these assets with the settings below
        // (the earlier generateMeshLods change never reached the .meta files).
        public override uint GetVersion() => 3;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Root)) return;
            var ti = (TextureImporter)assetImporter;
            string p = assetPath.ToLowerInvariant();
            ti.maxTextureSize = p.Contains("_4k") ? 4096 : 2048;
            ti.mipmapEnabled = true;
            ti.anisoLevel = 8;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            if (p.Contains("_nor_gl"))
            {
                ti.textureType = TextureImporterType.NormalMap;
            }
            else if (p.Contains("_hdrpmask") || p.Contains("_arm_") || p.Contains("_rough") ||
                     p.Contains("_ao_") || p.Contains("_alpha_") || p.Contains("_mask_"))
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = false;
            }
            if (p.Contains("_diffalpha"))
            {
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = true;
                ti.mipMapsPreserveCoverage = true;   // keeps leaves from thinning out at distance
                ti.alphaTestReferenceValue = 0.45f;
            }
        }

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Root)) return;
            var mi = (ModelImporter)assetImporter;
            // Poly Haven FBX (Blender export): UnitScaleFactor 1 (cm) with node scale 100 and a -90 deg X
            // rotation for Z-up -> Y-up (geometry is authored Z-up). useFileScale converts cm -> m, so 1 unit = 1 m with globalScale 1.
            mi.useFileScale = true;
            mi.globalScale = 1f;
            // Bake the FBX axis conversion into the meshes so prefab roots import upright with identity
            // rotation. The builder still verifies orientation (EnsureUpright) and never overwrites
            // imported node rotations (it places a wrapper instead).
            mi.bakeAxisConversion = true;
            // PERF: Unity 6 built-in Mesh LOD generation (the 32 MB trees ship with no LODs).
            mi.generateMeshLods = true;
            mi.maximumMeshLod = 6;
            mi.isReadable = false;
            mi.addCollider = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard; // keeps slot names; builder swaps in HDRP/Lit
        }
    }
}
