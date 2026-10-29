using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace MoonlitIsland.Editor
{
    /// <summary>
    /// Builds the island ground as a stock HDRP LayeredLit material: four CC0 scan
    /// layers (forest floor, pine needles, rock, snow) blended by a painted mask
    /// that follows height, slope, snowline and the forest-density field, so the
    /// ground reads as one continuous scanned surface.
    /// </summary>
    public static class IslandGroundSurface
    {
        private const int MaskSize = 1024;
        private const string MaskPath = "Assets/MoonlitIsland/Materials/IslandLayerMask.asset";
        private const string MaterialPath = "Assets/MoonlitIsland/Materials/IslandGroundLayered.mat";
        private const float TileWorldSize = 7f; // world units per scan-texture tile

        public static Material Apply(GameObject land, float extent)
        {
            var filter = land.GetComponent<MeshFilter>();
            var renderer = land.GetComponent<MeshRenderer>();
            if (filter == null || filter.sharedMesh == null || renderer == null)
                throw new System.ArgumentException("IslandGroundSurface.Apply requires the island mesh object.");

            var mask = BuildMask(extent);
            var material = BuildMaterial(extent, mask);

            // UV0 spans the island 0-1 so the layer mask maps cleanly; scan tiling
            // comes from each layer's texture scale, not from the mesh.
            var mesh = filter.sharedMesh;
            var vertices = mesh.vertices;
            var uv = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                uv[i] = new Vector2(
                    (vertices[i].x + extent * 0.5f) / extent,
                    (vertices[i].z + extent * 0.5f) / extent);
            mesh.uv = uv;
            mesh.RecalculateTangents();
            renderer.sharedMaterials = new[] { material };
            EditorUtility.SetDirty(mesh);
            return material;
        }

        private static Texture2D BuildMask(float extent)
        {
            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(MaskPath);
            if (mask == null)
            {
                mask = new Texture2D(MaskSize, MaskSize, TextureFormat.RGBA32, true, false);
                AssetDatabase.CreateAsset(mask, MaskPath);
            }
            else
            {
                mask.Reinitialize(MaskSize, MaskSize, TextureFormat.RGBA32, true);
            }
            mask.name = "IslandLayerMask";
            mask.wrapMode = TextureWrapMode.Clamp;
            mask.filterMode = FilterMode.Bilinear;
            mask.anisoLevel = 4;

            float step = extent / (MaskSize - 1);
            var pixels = new Color32[MaskSize * MaskSize];
            for (int z = 0; z < MaskSize; z++)
            {
                for (int x = 0; x < MaskSize; x++)
                {
                    float px = x * step - extent * 0.5f;
                    float pz = z * step - extent * 0.5f;
                    pixels[z * MaskSize + x] = WeightPixel(px, pz, step);
                }
            }
            mask.SetPixels32(pixels);
            mask.Apply(true, false);
            EditorUtility.SetDirty(mask);
            return mask;
        }

        private static Color32 WeightPixel(float x, float z, float step)
        {
            float h = MoonlitIslandBuilder.Height(x, z);
            float hx = MoonlitIslandBuilder.Height(x + step, z) - MoonlitIslandBuilder.Height(x - step, z);
            float hz = MoonlitIslandBuilder.Height(x, z + step) - MoonlitIslandBuilder.Height(x, z - step);
            float slope = Mathf.Atan2(Mathf.Sqrt(hx * hx + hz * hz), 2f * step);

            float density = MoonlitIslandBuilder.ForestDensity(x, z);
            float snowJitter = Mathf.PerlinNoise(x * 0.03f + 11.4f, z * 0.03f + 4.7f);
            float rockJitter = Mathf.PerlinNoise(x * 0.09f + 21.3f, z * 0.09f + 8.9f);

            // Layer weights before normalisation.
            float floor = Smooth(1.6f, 3.4f, h) * Smooth(0.42f, 0.18f, slope) * (1f - Smooth(0.52f, 0.72f, density));
            float needles = Smooth(0.38f, 0.58f, density) * Smooth(52f, 40f, h) * Smooth(0.55f, 0.30f, slope);
            float rock = Smooth(0.32f, 0.62f, slope + rockJitter * 0.14f)
                + Smooth(40f, 55f, h)
                + Smooth(2.2f, 0.6f, h); // wet shoreline bedrock
            float snow = Smooth(58f + snowJitter * 9f, 72f + snowJitter * 9f, h)
                + Smooth(0.62f, 0.85f, slope) * Smooth(44f, 58f, h) * 0.35f; // ridge crest dusting

            float total = floor + needles + rock + snow;
            if (total < 0.0001f)
            {
                floor = 1f;
                total = 1f;
            }
            return new Color32(
                (byte)(Mathf.Clamp01(floor / total) * 255f),
                (byte)(Mathf.Clamp01(needles / total) * 255f),
                (byte)(Mathf.Clamp01(rock / total) * 255f),
                (byte)(Mathf.Clamp01(snow / total) * 255f));
        }

        private static Material BuildMaterial(float extent, Texture2D mask)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            Shader shader = Shader.Find("HDRP/LayeredLit");
            if (shader == null)
                throw new System.InvalidOperationException("HDRP/LayeredLit shader not found.");
            if (material == null || material.shader != shader)
            {
                if (material != null) AssetDatabase.DeleteAsset(MaterialPath);
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, MaterialPath);
            }

            float tiling = extent / TileWorldSize;
            SetLayer(material, 0, "ForestFloor", new Color(1f, 1f, 1f), 0.14f, tiling);
            SetLayer(material, 1, "PineNeedles", new Color(0.92f, 0.95f, 0.92f), 0.16f, tiling);
            SetLayer(material, 2, "MossyRock", new Color(1f, 1f, 1f), 0.30f, tiling * 0.8f);
            SetLayer(material, 3, "Snow", new Color(0.97f, 0.98f, 1f), 0.06f, tiling * 0.6f);

            material.SetFloat("_LayerCount", 4f);
            if (material.HasProperty("_LayerMaskMap"))
            {
                material.SetTexture("_LayerMaskMap", mask);
                material.SetTextureScale("_LayerMaskMap", Vector2.one);
                material.SetTextureOffset("_LayerMaskMap", Vector2.zero);
            }
            else
            {
                Debug.LogWarning("IslandGroundSurface: LayeredLit lacks _LayerMaskMap; layers will not blend.");
            }
            material.enableInstancing = true;
            HDMaterial.ValidateMaterial(material);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void SetLayer(Material material, int layer, string folder, Color tint, float smoothness, float tiling)
        {
            string prefix = "Assets/MoonlitIsland/ScanTextures/" + folder + "/" + folder;
            var color = AssetDatabase.LoadAssetAtPath<Texture2D>(prefix + "_Color.jpg");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(prefix + "_Normal.jpg");
            if (color == null || normal == null)
                throw new System.InvalidOperationException("Missing scan textures for layer " + folder + ".");

            material.SetColor("_BaseColor" + layer, tint);
            material.SetTexture("_BaseColorMap" + layer, color);
            material.SetTextureScale("_BaseColorMap" + layer, new Vector2(tiling, tiling));
            material.SetTextureOffset("_BaseColorMap" + layer, Vector2.zero);
            material.SetTexture("_NormalMap" + layer, normal);
            material.SetFloat("_NormalScale" + layer, 1f);
            material.SetFloat("_Smoothness" + layer, smoothness);
            material.SetFloat("_Metallic" + layer, 0f);
        }

        private static float Smooth(float min, float max, float value)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(min, max, value));
        }
    }
}
