using UnityEditor;

namespace Edge.EditorTools
{
    // Import settings for Assets/Edge/ThirdParty/SydneyStarter (Poly Haven CC0 surfaces + CC BY 4.0
    // Sketchfab models re-exported from Blender with _LOD0/1/2 meshes, origin at the base).
    public class EdgeSydneyImportSettings : AssetPostprocessor
    {
        const string Root = "Assets/Edge/ThirdParty/SydneyStarter/";

        public override uint GetVersion() => 1;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Root)) return;
            var ti = (TextureImporter)assetImporter;
            string p = assetPath.ToLowerInvariant();
            string file = System.IO.Path.GetFileNameWithoutExtension(p);
            ti.maxTextureSize = 2048; // 4K scan maps capped (PERF / memory)
            ti.mipmapEnabled = true;
            ti.anisoLevel = 8;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            if (p.Contains("_nor_gl") || file.EndsWith("_normal") || file.EndsWith("_n"))
                ti.textureType = TextureImporterType.NormalMap;
            else if (p.Contains("_hdrpmask") || p.Contains("_rough") || p.Contains("_ao"))
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = false;
            }
        }

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Root)) return;
            var mi = (ModelImporter)assetImporter;
            mi.useFileScale = true;
            mi.globalScale = 1f;
            mi.bakeAxisConversion = true;   // upright, identity root; builder still verifies (OrientByBase)
            mi.isReadable = false;
            mi.addCollider = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importAnimation = false;     // none of these models has a rig or animation
            mi.animationType = ModelImporterAnimationType.None;
            mi.importBlendShapes = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard; // keeps slot names; builder swaps in HDRP/Lit
        }
    }
}
