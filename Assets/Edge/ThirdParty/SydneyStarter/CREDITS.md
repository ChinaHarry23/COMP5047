# Sydney Bushland Starter: credits and licences

These assets come from `Final Project/assets/sydney-bushland-starter-2026-10-12/` (downloaded 12 Oct 2026; the originals are unchanged in that folder).
They are used by `Assets/Edge/Editor/EdgeSydneySceneBuilder.cs` (menu **EDGE/Build Sydney Bushland Scene (Starter Pack)**), which builds `Assets/Edge/Scenes/EdgeSydneyBushland.unity`.

## CC BY 4.0 models (attribution REQUIRED; show these credits with the project)

Licence: Creative Commons Attribution 4.0 International, https://creativecommons.org/licenses/by/4.0/

- **"Eucalyptus Tree Scan"** by Michael Neocleous, https://sketchfab.com/3d-models/eucalyptus-tree-scan-f657f078660e4380be3833c076956a4c, licensed under CC BY 4.0.
  Changes: the starter pack rebuilt the material from the supplied base-colour, roughness and normal maps. For this project we baked the transforms, moved the pivot to the base, added Blender-decimated LOD1 (45%) and LOD2 (15%) meshes, and packed AO and (1 - roughness) into an HDRP mask map. Used at its real size (about 1.4 m) as cut eucalypt trunk bases. It was scanned in Leeds, UK, and the species is not specified.
  Files: `Models/eucalyptus_tree_scan/`
- **"Banksia Seed pod"** by CrowsEpicScans, https://sketchfab.com/3d-models/banksia-seed-pod-c0505382849e49a1b750eb4b865556f5, licensed under CC BY 4.0.
  Changes: we decimated it in Blender from 389k to 23k triangles (LOD0), 5.8k (LOD1) and 1.6k (LOD2), baked the transforms and moved the pivot to the base. Used at its real size (about 0.1 m) as ground detail. The species is not specified.
  Files: `Models/banksia_seed_pod/`
- **"Kangaroo"** by Stephengury, https://sketchfab.com/3d-models/kangaroo-cb181170e1134116a3b8773e199a72c4, licensed under CC BY 4.0.
  Changes: the starter pack prepared it from the official Sketchfab glTF conversion. For this project we started from that GLB, baked the node hierarchy (including mirrored nodes), joined the meshes, moved the pivot to the base, added decimated LOD1 (30%) and LOD2 (8%) meshes and rebuilt the HDRP materials from the supplied diffuse maps. It is a static prototype with no rig or animation and textures of 512 px or smaller.
  Files: `Models/kangaroo/`

## CC0 surfaces (Poly Haven; attribution not required, given as a courtesy)

- **Gravel Road**, https://polyhaven.com/a/gravel_road, CC0. Used for the footpath. Files: `Textures/gravel_road/`
- **Sandstone Cracks**, https://polyhaven.com/a/sandstone_cracks, CC0. Used for the outcrops and the path edging (triplanar). Files: `Textures/sandstone_cracks/`
- **Eucalyptus Bark**, https://polyhaven.com/a/eucalyptus_bark, CC0. The project already had the identical set at `Assets/Edge/ThirdParty/PolyHaven/Textures/eucalyptus_bark`, so it was not copied again. Used on the canopy-tree trunks.

For each surface we copied the 2K diffuse JPG and the normal-GL PNG unchanged and generated `_hdrpmask_2k.png` (R 0, G AO, B 0, A 1 - roughness) from the publisher's AO and roughness maps. Licence: https://polyhaven.com/license. Powered by Poly Haven.

## Not used

- **Concrete Pavement 02** (CC0): not copied. A gravel track suits the bush path, and the bench, sign and fountain pads keep their existing materials.
- **Kookaburra** by EddyTaylor (CC BY 4.0): not copied. It has no UVs, textures or materials and has no rig.

Conversion scripts (Blender 5.2 headless + Python/PIL) are kept in `Tools~/`. Unity ignores that folder.
