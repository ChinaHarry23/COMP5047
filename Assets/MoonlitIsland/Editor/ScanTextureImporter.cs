using UnityEditor;
using UnityEngine;

namespace MoonlitIsland.Editor
{
    /// <summary>Configures import settings for the ambientCG CC0 scan textures used by the island.</summary>
    public static class ScanTextureImporter
    {
        private const string Root = "Assets/MoonlitIsland/ScanTextures";

        [MenuItem("Moonlit Island/Configure scan texture importers")]
        public static void ConfigureAll()
        {
            int configured = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                if (importer == null)
                    continue;

                bool normal = path.EndsWith("_Normal.jpg", System.StringComparison.OrdinalIgnoreCase);
                importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = !normal;
                importer.maxTextureSize = 2048;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 8;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
                configured++;
            }
            Debug.Log("MOONLIT_SCANS_CONFIGURED " + configured + " textures under " + Root);
        }
    }
}
