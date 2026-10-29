using UnityEditor;
using UnityEngine;

namespace Edge.EditorTools
{
    // Import settings for the pedestrian assets:
    //   Assets/Edge/ThirdParty/Rocketbox/   - Microsoft Rocketbox avatars (MIT): 3ds Max Biped rigs -> Humanoid
    //   Assets/Edge/ThirdParty/Quaternius/  - Universal Animation Library (CC0): clip source only -> Humanoid,
    //                                         retargeted onto the Rocketbox avatars via Mecanim
    //   Assets/Edge/ThirdParty/OpenGameArt/ - low-poly bicycle (OBJ)
    // Every clip is imported looping, in place (root motion baked into the pose, the
    // EdgePedestrians script moves the agents along the path).
    public class EdgeCharacterImportSettings : AssetPostprocessor
    {
        const string Quat = "Assets/Edge/ThirdParty/Quaternius/";
        const string Rbx = "Assets/Edge/ThirdParty/Rocketbox/";
        const string Oga = "Assets/Edge/ThirdParty/OpenGameArt/";
        public override uint GetVersion() => 4;
        const float JogYawFix = -28f, WalkYawFix = -7f;

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Quat) && !assetPath.StartsWith(Oga) && !assetPath.StartsWith(Rbx)) return;
            var mi = (ModelImporter)assetImporter;
            mi.useFileScale = true;
            mi.globalScale = 1f;            // the builder measures and scales to real height/length anyway
            mi.addCollider = false;         // pedestrians must never physically touch the bat
            mi.importCameras = false;
            mi.importLights = false;
            mi.isReadable = false;
            mi.importBlendShapes = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard; // keeps slot names; builder swaps in HDRP/Lit
            if (assetPath.StartsWith(Oga))
            {
                mi.importAnimation = false;
                mi.animationType = ModelImporterAnimationType.None;
                return;
            }
            mi.animationType = ModelImporterAnimationType.Human;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; // Unity auto-maps UE-style (Quaternius) and Biped (Rocketbox) bones
            mi.optimizeGameObjects = false;
            mi.importAnimation = assetPath.StartsWith(Quat);
            mi.animationCompression = ModelImporterAnimationCompression.Optimal;
            if (assetPath.StartsWith(Rbx)) mi.skinWeights = ModelImporterSkinWeights.Standard; // 4 bones/vertex
        }

        void OnPreprocessAnimation()
        {
            if (!assetPath.StartsWith(Quat)) return;
            var mi = (ModelImporter)assetImporter;
            var clips = mi.defaultClipAnimations;
            for (int i = 0; i < clips.Length; i++)
            {
                clips[i].loopTime = true;
                clips[i].loopPose = true;
                clips[i].wrapMode = WrapMode.Loop;
                // In place: bake root rotation / height / XZ into the pose. Rotation is based on Body
                // Orientation, not Original: the UAL rig faces Blender -Y (= Unity -Z), so "Original"
                // turned every retargeted body 180 deg away from its direction of travel.
                clips[i].lockRootRotation = true; clips[i].keepOriginalOrientation = false;
                clips[i].lockRootHeightY = true; clips[i].keepOriginalPositionY = true;
                clips[i].lockRootPositionXZ = true; clips[i].keepOriginalPositionXZ = true;
                // Body Orientation still leaves some locomotion loops heading off +Z. Measured in Play
                // mode (circular-mean hip yaw and planted-foot push direction over one cycle):
                // Jog_Fwd_Loop runs ~28 deg off, Walk_Loop ~7 deg.
                if (clips[i].name.EndsWith("Jog_Fwd_Loop")) clips[i].rotationOffset = JogYawFix;
                else if (clips[i].name.EndsWith("Walk_Loop")) clips[i].rotationOffset = WalkYawFix;
            }
            mi.clipAnimations = clips;
        }

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Rbx)) return;
            var ti = (TextureImporter)assetImporter;
            ti.maxTextureSize = 2048;
            ti.mipmapEnabled = true;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            if (assetPath.Contains("_normal")) { ti.textureType = TextureImporterType.NormalMap; ti.sRGBTexture = false; }
            else if (assetPath.Contains("_mask")) { ti.textureType = TextureImporterType.Default; ti.sRGBTexture = false; }
            else { ti.textureType = TextureImporterType.Default; ti.sRGBTexture = true; ti.alphaIsTransparency = assetPath.Contains("_opacity"); }
        }
    }
}
