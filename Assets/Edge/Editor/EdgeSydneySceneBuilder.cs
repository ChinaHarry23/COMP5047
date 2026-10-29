using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Edge.EditorTools
{
    // CLI entry point for the Sydney variant:
    //   Unity -batchmode -projectPath <proj> -executeMethod Edge.EditorTools.EdgeSydneySceneBuilder.Build -quit
    public static class EdgeSydneySceneBuilder
    {
        public static void Build() => EdgeSceneBuilder.BuildSydney();
    }

    // Sydney bushland variant of EdgeBushland, built from the 2026-10-12 starter pack
    // (Assets/Edge/ThirdParty/SydneyStarter, see CREDITS.md there). Same footpath curve, terrain,
    // lamps + LampZone, bat + BatFlightController, FPV/TPV CameraToggle, EdgeHud, council bench,
    // signs, fountain, EdgeGrass and Rocketbox pedestrians as the original builder (shared helpers),
    // dressed differently:
    //   - path: Poly Haven gravel_road (CC0) with arc-length UVs, edged with sandstone_cracks kerbs;
    //   - sandstone_cracks (triplanar) outcrops: procedural low ledges + re-skinned boulders;
    //   - canopy: Poly Haven island_tree_02 / tree_small_02 at mature-eucalypt heights (15-25 m)
    //     with the trunk slot re-skinned in Poly Haven eucalyptus_bark (CC0);
    //   - "Eucalyptus Tree Scan" (CC BY 4.0) as real-size (~1.4 m) cut trunk bases between the trees.
    //     The scan is only a 1.73 x 1.46 x 1.38 m two-stem trunk base with no canopy, so it is NOT
    //     scaled up into a 20 m tree;
    //   - "Banksia Seed pod" (CC BY 4.0, decimated 389k -> 23k/5.8k/1.6k tris LODs) at its real
    //     ~0.1 m size as close-up ground detail near benches and trunk bases;
    //   - "Kangaroo" (CC BY 4.0, static, no rig) scaled to ~1.4 m, in two small mobs on the far bush
    //     edge 15-22 m from the path.
    // Every generated mesh/profile/controller asset is prefixed "Syd_" (s_variant) so this never
    // touches the assets EdgeBushland.unity references.
    public static partial class EdgeSceneBuilder
    {
        const string SydneyScenePath = Root + "/Scenes/EdgeSydneyBushland.unity";
        const string SydneyVariant = "Syd_";
        const string Syd = Root + "/ThirdParty/SydneyStarter";

        [MenuItem("EDGE/Build Sydney Bushland Scene (Starter Pack)")]
        public static void BuildSydney()
        {
            s_variant = SydneyVariant;
            try { BuildSydneyInner(); }
            finally { s_variant = ""; }
        }

        const bool IncludeKangaroos = false;
        const bool IncludeTrunkScans = false;

        static void BuildSydneyInner()
        {
            Directory.CreateDirectory(Root + "/Scenes");
            Directory.CreateDirectory(Root + "/Settings");
            Directory.CreateDirectory(Root + "/Materials");
            Directory.CreateDirectory(Root + "/Meshes");
            if (!AssetDatabase.IsValidFolder(Syd))
            {
                Debug.LogError($"[EDGE] {Syd} is missing; copy the Sydney starter assets first.");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- materials ----
            Material verge = PHGroundMat("Edge_PH_ForestFloor", "forest_floor", "4k", 0.25f)
                             ?? ScanMat("Edge_ForestFloor", "ForestFloor", new Color(0.78f, 0.74f, 0.68f), 0.30f, 0.35f);
            Material litter = PHGroundMat("Edge_PH_LeavesForestGround", "leaves_forest_ground", "2k", 0.3f)
                              ?? ScanMat("Edge_MossGround", "MossGround", new Color(0.80f, 0.85f, 0.78f), 0.25f, 0.30f);
            // gravel_road is a 2 m tile; path UVs are metres (u across, v along the arc).
            Material gravel = SydTexMat("Edge_Syd_GravelRoad", "gravel_road", 0.5f, false, 1.2f);
            // sandstone_cracks is a 2 m tile; triplanar so the procedural ledges/kerbs/boulders need no UVs.
            Material sandstone = SydTexMat("Edge_Syd_SandstoneCracks", "sandstone_cracks", 0.5f, true, 1.3f);
            Material eucBark = PHGroundMat("Edge_Syd_EucalyptusBark", "eucalyptus_bark", "2k", 1f);
            Material postMat = LitMat("Edge_Post", new Color(0.18f, 0.18f, 0.19f), 0.55f, 0.6f);
            Material headMat = EmissiveMat("Edge_LampHead", AmberColor(), 6000f);
            Material batFur = LoadMat("Bat fur charcoal");
            Material batWing = LoadMat("Bat wing membrane");
            if (!gravel || !sandstone) { Debug.LogError("[EDGE] Sydney starter textures missing under " + Syd + "/Textures"); return; }

            BuildSky(); // -> Settings/Syd_EdgeNightProfile.asset, "Moonlight" + global Volume (EdgeLightPreview works)

            BuildGround(verge, litter); // -> Meshes/Syd_EdgeGround.asset
            BuildSydPath(gravel);
            BuildSandstoneEdging(sandstone);

            // ---- bat, systems, cameras, HUD (same hookups as EdgeBushland) ----
            var bat = BuildBat(batFur, batWing);
            float bz = -45f, bx = PathX(bz);
            bat.transform.position = new Vector3(bx, Height(bx, bz) + 3f, bz);

            var sys = new GameObject("EdgeSystem");
            var link = sys.AddComponent<UdpLink>();
            var dose = sys.AddComponent<DoseModel>();
            dose.link = link;
            var hud = sys.AddComponent<EdgeHud>();
            var flight = bat.AddComponent<BatFlightController>();
            flight.link = link;
            flight.dose = dose;
            flight.minAltitude = 1f;
            flight.maxAltitude = 10f;

            var fpvGo = new GameObject("FPV Camera");
            fpvGo.transform.SetParent(bat.transform, false);
            fpvGo.transform.localPosition = new Vector3(0f, 0.012f, BatBodyLength * 0.55f);
            var fpv = fpvGo.AddComponent<Camera>();
            fpv.fieldOfView = 95f;
            fpv.nearClipPlane = 0.01f;
            fpv.farClipPlane = 400f;
            fpvGo.AddComponent<HDAdditionalCameraData>().allowDynamicResolution = true;
            fpvGo.AddComponent<AudioListener>();

            var tpvGo = new GameObject("TPV Camera");
            tpvGo.transform.position = bat.transform.position + new Vector3(0f, 0.45f, -1.6f);
            var tpv = tpvGo.AddComponent<Camera>();
            tpv.fieldOfView = 55f;
            tpv.nearClipPlane = 0.02f;
            tpv.farClipPlane = 400f;
            tpvGo.AddComponent<HDAdditionalCameraData>().allowDynamicResolution = true;
            tpvGo.AddComponent<AudioListener>();

            var cams = sys.AddComponent<CameraToggle>();
            cams.fpv = fpv;
            cams.tpv = tpv;
            cams.bat = bat.transform;
            cams.followDistance = 1.6f;
            cams.followHeight = 0.45f;
            cams.lookHeight = 0.02f;
            hud.dose = dose;
            hud.flight = flight;
            hud.cams = cams;

            const int lampCount = 8; // 2026-10-22: 18 m spacing covers the same ~125 m path (was 6 x 25 m)
            for (int i = 0; i < lampCount; i++)
            {
                float z = -62.5f + i * LampSpacing;
                float x = PathX(z) + PathHalfWidth + 0.6f;
                BuildLamp(i, new Vector3(x, Height(x, z), z), postMat, headMat, bat.transform, dose, link);
            }

            var trees = new GameObject("Trees").transform;
            var stumps = new GameObject("EucalyptTrunkScans").transform;
            var bushes = new GameObject("Bushes").transform;
            var rocks = new GameObject("SandstoneOutcrops").transform;
            var wildlife = new GameObject("Wildlife").transform;
            var pods = new GameObject("BanksiaPods").transform;
            Random.InitState(5047 + 2); // deterministic, distinct from EdgeBushland

            // ---- models ----
            var canopy = WithBark(LoadPH(new[] { "island_tree_02" }), eucBark);
            var small = WithBark(LoadPH(new[] { "tree_small_02" }), eucBark);
            var logs = LoadPH(new[] { "dead_tree_trunk_02" });
            var shrubs = LoadPH(new[] { "shrub_02", "shrub_04" });
            var ferns = LoadPH(new[] { "fern_02" });
            var boulders = new List<PHModel>();
            foreach (var r in LoadPH(RockIds)) boulders.Add(Reskin(r, sandstone));

            PHModel eucScan = LoadStarter("EucScan", $"{Syd}/Models/eucalyptus_tree_scan/EucScan.fbx", slot =>
                SydModelMat("Edge_Syd_EucScan", $"{Syd}/Models/eucalyptus_tree_scan/textures/euctree_bc.png",
                            $"{Syd}/Models/eucalyptus_tree_scan/textures/EucTree_N.png",
                            $"{Syd}/Models/eucalyptus_tree_scan/textures/EucTree_hdrpmask.png", Color.white, 0.3f));
            PHModel banksia = LoadStarter("BanksiaPod", $"{Syd}/Models/banksia_seed_pod/BanksiaPod.fbx", slot =>
                SydModelMat("Edge_Syd_BanksiaPod", $"{Syd}/Models/banksia_seed_pod/textures/tex_u1_v1_diffuse.jpg",
                            $"{Syd}/Models/banksia_seed_pod/textures/tex_u1_v1_normal.jpg", null, Color.white, 0.25f));
            PHModel roo = LoadStarter("Kangaroo", $"{Syd}/Models/kangaroo/Kangaroo.fbx", KangarooMat);

            // ---- canopy: 2 staggered rows per side (same row rule as EdgeBushland), wider spacing for mature eucalypts ----
            var occupied = new List<Vector3>();
            for (int i = 0; i < lampCount; i++)
            {
                float lz = -62.5f + i * LampSpacing;
                occupied.Add(new Vector3(PathX(lz) + PathHalfWidth + 0.6f, lz, 2.5f));
            }
            int nCanopy = 0, nSmall = 0, nScan = 0, nLog = 0, nShrub = 0, nFern = 0, nRock = 0, nLedge = 0, nRoo = 0, nPod = 0;
            var treeSpots = new List<Vector3>();
            float[] rowOffset = { 6.8f, 12.5f };
            for (int side = -1; side <= 1; side += 2)
                for (int row = 0; row < 2; row++)
                {
                    float spacing0 = 11f;
                    float nextAt = row == 1 ? spacing0 * 0.5f : 0f;
                    float zPrev = -PathHalfLength, travelled = 0f;
                    for (float z = -PathHalfLength; z <= PathHalfLength; z += 0.25f)
                    {
                        travelled += Vector2.Distance(new Vector2(PathX(zPrev), zPrev), new Vector2(PathX(z), z));
                        zPrev = z;
                        if (travelled < nextAt) continue;
                        nextAt = travelled + Random.Range(10f, 13f);
                        Vector2 c = new Vector2(PathX(z), z);
                        Vector2 t = PathTangent(z), n = new Vector2(t.y, -t.x);
                        Vector2 p = c + n * side * (rowOffset[row] + Random.Range(-0.8f, 0.8f)) + t * Random.Range(-0.8f, 0.8f);
                        if (Mathf.Abs(p.x) > HalfSize - 3f || Mathf.Abs(p.y) > HalfSize - 3f) continue;
                        if (PathDist(p.x, p.y) < BushStart + 0.8f || !Free(occupied, p.x, p.y, 2f, true)) continue;
                        var pos = new Vector3(p.x, Height(p.x, p.y), p.y);
                        // Mature Sydney eucalypts (e.g. Angophora / Eucalyptus spp.) ~15-25 m.
                        if (Random.value < 0.8f && canopy.Count > 0)
                        {
                            float h = row == 0 ? Random.Range(15f, 20f) : Random.Range(18f, 25f);
                            PlaceSized(trees, "Tree", canopy[0], pos, h, SizeAxis.Height, 2f, 0.25f); nCanopy++;
                        }
                        else if (small.Count > 0)
                        { PlaceSized(trees, "Tree", small[0], pos, Random.Range(7f, 11f), SizeAxis.Height, 3f, 0.2f); nSmall++; }
                        occupied.Add(new Vector3(p.x, p.y, 1.4f));
                        treeSpots.Add(new Vector3(p.x, p.y, side * (row + 1)));
                    }
                }

            BuildFurniture(occupied); // same council bench / signs / fountain as EdgeBushland

            // ---- eucalyptus trunk scans: real size (measured ~1.38 m tall), between the rows ----
            var scanSpots = new List<Vector2>();
            if (eucScan != null)
                for (int attempt = 0; attempt < 600 && nScan < 10; attempt++)
                {
                    float z = Random.Range(-95f, 95f), side = Random.value < 0.5f ? -1f : 1f;
                    float x = PathX(z) + side * Random.Range(BushStart + 1.5f, 15f);
                    if (!Free(occupied, x, z, 1.1f, true)) continue;
                    var go = PlaceSized(stumps, "TrunkScan", eucScan, new Vector3(x, Height(x, z), z),
                                        Random.Range(1.25f, 1.6f), SizeAxis.Height, 2f, 0.06f);
                    SetLod0Shadows(go, true); // ~1.5 m solid trunk: LOD0 casts, LOD1/2 do not
                    occupied.Add(new Vector3(x, z, 1.1f)); scanSpots.Add(new Vector2(x, z)); nScan++;
                    if (nScan == 1) { var b = WorldBounds(go); Debug.Log($"[EDGE] Eucalyptus trunk scan placed at {b.size.x:F2} x {b.size.z:F2} m, {b.size.y:F2} m tall (real-size cut base; no canopy in the scan)"); }
                    if (!IncludeTrunkScans) Object.DestroyImmediate(go); // removed at Bao's request 2026-10-13 (looked fake); placement still runs so the seeded layout is unchanged
                }

            // ---- sandstone outcrops: a procedural ledge + 2-4 re-skinned boulders, just past the verge ----
            var ledgeMeshes = new Mesh[5];
            for (int i = 0; i < ledgeMeshes.Length; i++) ledgeMeshes[i] = LedgeMesh(i);
            for (int c = 0, tries = 0; c < 7 && tries < 600; tries++)
            {
                float cz = Random.Range(-95f, 95f), cside = Random.value < 0.5f ? -1f : 1f;
                float cx = PathX(cz) + cside * Random.Range(BushStart + 1.5f, BushStart + 7f);
                if (!Free(occupied, cx, cz, 2.2f, true)) continue;
                c++;
                var ledge = new GameObject("SandstoneLedge", typeof(MeshFilter), typeof(MeshRenderer));
                ledge.transform.SetParent(rocks, false);
                float ls = Random.Range(0.8f, 1.4f);
                ledge.transform.position = new Vector3(cx, Height(cx, cz) - 0.22f * ls, cz); // sides bedded into the soil
                ledge.transform.rotation = Quaternion.Euler(Random.Range(-3f, 3f), Random.Range(0f, 360f), Random.Range(-3f, 3f));
                ledge.transform.localScale = new Vector3(ls, ls * Random.Range(0.8f, 1.2f), ls);
                ledge.GetComponent<MeshFilter>().sharedMesh = ledgeMeshes[Random.Range(0, ledgeMeshes.Length)];
                var lr = ledge.GetComponent<MeshRenderer>();
                lr.sharedMaterial = sandstone;
                lr.shadowCastingMode = ShadowCastingMode.Off; // PERF: low slabs
                ledge.isStatic = true;
                AddCull(ledge, 0.02f);
                occupied.Add(new Vector3(cx, cz, 2.2f * ls)); nLedge++;

                int n = Random.Range(2, 5);
                for (int k = 0; k < n && boulders.Count > 0; k++)
                {
                    float a = Random.Range(0f, Mathf.PI * 2f), d = Random.Range(1.6f, 3.2f) * ls;
                    float x = cx + Mathf.Cos(a) * d, z = cz + Mathf.Sin(a) * d;
                    float size = k == 0 ? Random.Range(1.0f, 1.8f) : Random.Range(0.4f, 0.9f);
                    if (PathDist(x, z) < BushStart || !Free(occupied, x, z, size * 0.6f, true)) continue;
                    PlaceSized(rocks, "Rock", boulders[Random.Range(0, boulders.Count)], new Vector3(x, Height(x, z), z),
                               size, SizeAxis.Height, 8f, size * 0.25f);
                    occupied.Add(new Vector3(x, z, size * 0.6f)); nRock++;
                }
            }

            // ---- understorey clumps (same plant sizes/caps as EdgeBushland; a little sparser) ----
            PHModel shrub02 = shrubs.Find(x => x.id == "shrub_02"), shrub04 = shrubs.Find(x => x.id == "shrub_04");
            PHModel fernM = ferns.Count > 0 ? ferns[0] : null;
            void PlantClump(Vector2 c, float radius, int count, float fernBias)
            {
                for (int k = 0; k < count; k++)
                {
                    Vector2 q = c + Random.insideUnitCircle * radius;
                    if (PathDist(q.x, q.y) < PathHalfWidth + 0.6f || Mathf.Abs(q.x) > HalfSize - 2f || Mathf.Abs(q.y) > HalfSize - 2f) continue;
                    if (!Free(occupied, q.x, q.y, 0.25f, true)) continue;
                    var pos = new Vector3(q.x, Height(q.x, q.y), q.y);
                    float pick = Random.value;
                    if (pick < fernBias && fernM != null)
                    { PlaceSized(bushes, "Bush", fernM, pos, Random.Range(0.5f, 1.0f), SizeAxis.Height, 6f, 0.03f); nFern++; }
                    else if (pick < fernBias + (1f - fernBias) * 0.65f && shrub02 != null)
                    { PlaceSized(bushes, "Bush", shrub02, pos, Random.Range(0.6f, 2.0f), SizeAxis.Height, 5f, 0.04f); nShrub++; }
                    else if (shrub04 != null)
                    { PlaceSized(bushes, "Bush", shrub04, pos, Random.Range(0.6f, 0.8f), SizeAxis.Height, 5f, 0.03f, 3f); nShrub++; }
                    occupied.Add(new Vector3(q.x, q.y, 0.25f));
                }
            }
            foreach (var t in treeSpots)
            {
                if (Random.value > 0.5f) continue;
                float side = Mathf.Sign(t.z);
                PlantClump(new Vector2(t.x + side * Random.Range(1.5f, 3f), t.y + Random.Range(-2.5f, 2.5f)), 1.5f, Random.Range(3, 8), 0.5f);
            }
            for (float z = -HalfSize + 3f; z < HalfSize - 3f; z += Random.Range(8f, 13f))
                for (int side = -1; side <= 1; side += 2)
                    PlantClump(new Vector2(PathX(z) + side * (BushStart + Random.Range(-0.4f, 0.8f)), z), 1.6f, Random.Range(3, 8), 0.5f);

            for (int attempt = 0; attempt < 400 && nLog < 3 && logs.Count > 0; attempt++)
            {
                float z = Random.Range(-90f, 90f), side = Random.value < 0.5f ? -1f : 1f;
                float x = PathX(z) + side * Random.Range(9f, 10f);
                if (!Free(occupied, x, z, 3f, true)) continue;
                PlaceSized(trees, "Tree", logs[0], new Vector3(x, Height(x, z), z), Random.Range(4f, 6f), SizeAxis.Length, 2f, 0.12f);
                occupied.Add(new Vector3(x, z, 3f)); nLog++;
            }

            // ---- kangaroos: two small mobs on the far bush edge, well away from the path ----
            if (roo != null)
            {
                foreach (float mobZ in new[] { Random.Range(-70f, -30f), Random.Range(20f, 70f) })
                {
                    float side = Random.value < 0.5f ? -1f : 1f;
                    Vector2 t = PathTangent(mobZ), n = new Vector2(t.y, -t.x);
                    Vector2 centre = new Vector2(PathX(mobZ), mobZ) + n * side * Random.Range(17f, 20f);
                    int want = nRoo == 0 ? 3 : 2;
                    for (int k = 0, tries = 0; k < want && tries < 60; tries++)
                    {
                        Vector2 q = centre + Random.insideUnitCircle * 3f;
                        float pd = PathDist(q.x, q.y);
                        if (pd < 15f || pd > 23f || !Free(occupied, q.x, q.y, 0.9f, true)) continue;
                        var go = PlaceSized(wildlife, "Kangaroo", roo, new Vector3(q.x, Height(q.x, q.y), q.y),
                                            Random.Range(1.3f, 1.5f), SizeAxis.Height, 1f, 0.02f);
                        SetLod0Shadows(go, true);
                        SetLodHeights(go, new[] { 0.10f, 0.03f, 0.008f }); // ~1.4 m animal: culled beyond ~80 m
                        occupied.Add(new Vector3(q.x, q.y, 0.9f)); nRoo++; k++;
                        if (nRoo == 1) { var b = WorldBounds(go); Debug.Log($"[EDGE] Kangaroo placed at {b.size.y:F2} m tall, {Mathf.Max(b.size.x, b.size.z):F2} m long (static model, no rig/animation)"); }
                        if (!IncludeKangaroos) Object.DestroyImmediate(go); // removed at Bao's request 2026-10-13; placement still runs so the seeded layout of everything else is unchanged
                    }
                }
            }

            // ---- banksia pods: real ~0.1 m size, close-up detail behind benches and at trunk-scan bases ----
            if (banksia != null)
            {
                var spots = new List<Vector2>(scanSpots);
                var furn = GameObject.Find("Furniture");
                if (furn)
                    foreach (Transform b in furn.transform)
                        if (b.name == "Bench") spots.Add(new Vector2(b.position.x - b.forward.x * 1.1f, b.position.z - b.forward.z * 1.1f));
                foreach (var s in spots)
                {
                    int count = Random.Range(1, 4);
                    for (int k = 0; k < count; k++)
                    {
                        Vector2 q = s + Random.insideUnitCircle * 0.9f;
                        if (PathDist(q.x, q.y) < PathHalfWidth + 0.4f) continue;
                        var go = PlaceSized(pods, "Pod", banksia, new Vector3(q.x, Height(q.x, q.y), q.y),
                                            Random.Range(0.09f, 0.13f), SizeAxis.Height, 70f, 0.01f, 1.4f);
                        SetLodHeights(go, new[] { 0.02f, 0.008f, 0.003f }); // visible to ~15 m, LOD0 only up close
                        nPod++;
                    }
                }
            }

            BuildFarTreeline();
            BuildGrass(occupied, fpv, tpv);
            BuildPedestrians(fpv, tpv);
            SydneyCost(lampCount, (trees, "trees+logs"), (stumps, "trunk scans"), (rocks, "sandstone"),
                       (bushes, "shrubs/ferns"), (wildlife, "kangaroos"), (pods, "banksia pods"));

            Debug.Log("[EDGE] Colliders: " + EdgeColliderPass.Apply());
            EditorSceneManager.SaveScene(scene, SydneyScenePath);
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!list.Exists(s => s.path == SydneyScenePath)) list.Add(new EditorBuildSettingsScene(SydneyScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log($"[EDGE] Sydney bushland scene built at {SydneyScenePath}: {lampCount} lamps, {nCanopy} canopy + {nSmall} small trees (eucalyptus_bark trunks), " +
                      $"{nScan} eucalyptus trunk scans, {nLedge} sandstone ledges + {nRock} boulders, {nShrub} shrubs, {nFern} ferns, {nLog} logs, " +
                      $"{nRoo} kangaroos, {nPod} banksia pods, UDP port {EdgeConfig.UdpPort}");
        }

        // ---------- Sydney helpers ----------

        static void SydneyCost(int lamps, params (Transform g, string label)[] groups)
        {
            long total = 0; int objects = 0, renderers = 0, casters = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var (g, label) in groups)
            {
                long tris = 0; int objs = 0;
                foreach (Transform inst in g)
                {
                    objs++;
                    foreach (var r in inst.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(r.name, @"_LOD[1-9]$")) continue;
                        var mf = r.GetComponent<MeshFilter>();
                        if (!mf || !mf.sharedMesh) continue;
                        for (int i = 0; i < mf.sharedMesh.subMeshCount; i++) tris += mf.sharedMesh.GetSubMesh(i).indexCount / 3;
                        renderers++;
                        if (r.shadowCastingMode != ShadowCastingMode.Off) casters++;
                    }
                }
                objects += objs; total += tris;
                sb.Append($"{label}={objs} ({tris / 1000}k tris) ");
            }
            Debug.Log($"[EDGE] Sydney scene cost: {objects} placed objects, {renderers} LOD0 renderers ({casters} shadow casters), " +
                      $"{total / 1e6:F2} M triangles at full detail, {lamps} lamps (2 shadowed). {sb}");
        }

        static void SetLod0Shadows(GameObject go, bool on)
        {
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                r.shadowCastingMode = on && !System.Text.RegularExpressions.Regex.IsMatch(r.name, @"_LOD[1-9]$")
                    ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        // Explicit LOD switch heights for the starter models (they ship _LOD0/1/2 meshes from Blender).
        static void SetLodHeights(GameObject go, float[] heights)
        {
            var lg = go.GetComponentInChildren<LODGroup>(true);
            if (!lg) return;
            var lods = lg.GetLODs();
            float h = 1f;
            for (int i = 0; i < lods.Length; i++)
            {
                h = i < heights.Length ? Mathf.Min(heights[i], h * 0.99f) : h * 0.5f; // strictly decreasing
                lods[i].screenRelativeTransitionHeight = h;
            }
            lg.SetLODs(lods);
            lg.fadeMode = LODFadeMode.None;
        }

        // A model from SydneyStarter/Models as a PHModel, so PlacePH/PlaceSized (wrapper, OrientByBase,
        // measured bounds, base contact, LOD cull) handle it exactly like the Poly Haven ones.
        static PHModel LoadStarter(string id, string fbxPath, System.Func<string, Material> matForSlot)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (!prefab) { Debug.LogWarning($"[EDGE] Sydney starter model missing: {fbxPath} (skipped)"); return null; }
            var m = new PHModel { id = id, prefab = prefab };
            foreach (Transform c in prefab.transform)
            {
                string v = LodSuffix.Replace(c.name, "");
                if (!m.variants.Contains(v)) m.variants.Add(v);
            }
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                foreach (var src in r.sharedMaterials)
                    if (src && !m.mats.ContainsKey(src.name)) m.mats[src.name] = matForSlot(src.name);
            if (!prefab.GetComponentInChildren<LODGroup>(true))
                Debug.LogWarning($"[EDGE] {id}: no LODGroup on import (expected _LOD0/1/2 children); a single cull level is used.");
            return m;
        }

        // Same Poly Haven tree, trunk slot (the one textured "<id>_diff") swapped for eucalyptus_bark.
        static List<PHModel> WithBark(List<PHModel> src, Material bark)
        {
            if (!bark) return src;
            var outList = new List<PHModel>();
            foreach (var m in src)
            {
                var c = new PHModel { id = m.id, prefab = m.prefab, variants = m.variants, tints = m.tints, mats = new Dictionary<string, Material>(m.mats) };
                foreach (var slot in new List<string>(m.mats.Keys))
                    if (PHTexPrefix(m.id, slot) == $"{PH}/Models/{m.id}/textures/{m.id}") c.mats[slot] = bark;
                outList.Add(c);
            }
            return outList;
        }

        static PHModel Reskin(PHModel m, Material mat)
        {
            var c = new PHModel { id = m.id, prefab = m.prefab, variants = m.variants };
            foreach (var k in m.mats.Keys) c.mats[k] = mat;
            return c;
        }

        // HDRP/Lit for a Poly Haven surface copied into SydneyStarter/Textures (diff jpg, nor_gl png, packed hdrpmask png).
        static Material SydTexMat(string name, string id, float tilesPerMetre, bool triplanar, float normalScale)
        {
            string dir = $"{Syd}/Textures/{id}/";
            var diff = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}{id}_diff_2k.jpg");
            if (!diff) return null;
            var m = GetOrCreate(name);
            m.SetColor("_BaseColor", Color.white);
            m.SetTexture("_BaseColorMap", diff);
            m.SetTexture("_NormalMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}{id}_nor_gl_2k.png"));
            m.SetFloat("_NormalScale", normalScale);
            SetMask(m, AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}{id}_hdrpmask_2k.png"), 0f, 1f);
            if (triplanar)
            {
                m.SetFloat("_UVBase", 5f);              // UVBaseMapping.Triplanar
                m.SetFloat("_TexWorldScale", tilesPerMetre);
                m.SetTextureScale("_BaseColorMap", Vector2.one);
            }
            else
            {
                m.SetFloat("_UVBase", 0f);
                m.SetTextureScale("_BaseColorMap", Vector2.one * tilesPerMetre);
            }
            m.enableInstancing = true;
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material SydModelMat(string name, string baseTex, string normalTex, string maskTex, Color tint, float smoothness)
        {
            var m = GetOrCreate(name);
            m.SetColor("_BaseColor", tint);
            m.SetTexture("_BaseColorMap", baseTex != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(baseTex) : null);
            m.SetTextureScale("_BaseColorMap", Vector2.one);
            m.SetTexture("_NormalMap", normalTex != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(normalTex) : null);
            m.SetFloat("_NormalScale", 1f);
            var mask = maskTex != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(maskTex) : null;
            SetMask(m, mask, 0f, 1f);
            if (!mask) m.SetFloat("_Smoothness", smoothness);
            m.enableInstancing = true;
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // Kangaroo slots KGROmain/back/bttm/frt/sup/intr/ear/hr01 -> "<slot>_diffuse.jpg" (KGROnail is .png);
        // KGROeyes and KGROhr02 have no texture in the source and get a flat colour.
        static Material KangarooMat(string slot)
        {
            string key = slot;
            int dot = key.IndexOf('.'); if (dot > 0) key = key.Substring(0, dot);
            string dir = $"{Syd}/Models/kangaroo/textures/";
            string tex = File.Exists($"{dir}{key}_diffuse.jpg") ? $"{dir}{key}_diffuse.jpg"
                       : File.Exists($"{dir}{key}_diffuse.png") ? $"{dir}{key}_diffuse.png" : null;
            Color tint = tex != null ? Color.white : key.Contains("eyes") ? new Color(0.03f, 0.025f, 0.02f) : new Color(0.55f, 0.38f, 0.25f);
            return SydModelMat("Edge_Syd_Kangaroo_" + key, tex, null, null, tint, key.Contains("eyes") ? 0.8f : 0.2f);
        }

        // Footpath along the same curve as EdgeBushland, but UVs in metres across (u) and along the arc (v)
        // so the gravel tiles follow the curve.
        static void BuildSydPath(Material pathMat)
        {
            const int segZ = 420, segX = 6;
            var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
            float arc = 0f, zPrev = -PathHalfLength;
            for (int j = 0; j <= segZ; j++)
            {
                float z = -PathHalfLength + j * (PathHalfLength * 2f / segZ);
                arc += Vector2.Distance(new Vector2(PathX(zPrev), zPrev), new Vector2(PathX(z), z)); zPrev = z;
                Vector2 t = PathTangent(z), n = new Vector2(t.y, -t.x);
                for (int i = 0; i <= segX; i++)
                {
                    float s = i / (float)segX, off = Mathf.Lerp(-PathHalfWidth, PathHalfWidth, s);
                    float x = PathX(z) + n.x * off, zz = z + n.y * off;
                    float edge = Mathf.Abs(s - 0.5f) * 2f;
                    verts.Add(new Vector3(x, Height(x, zz) + 0.035f - edge * edge * 0.03f, zz));
                    uvs.Add(new Vector2(off, arc));
                }
            }
            int w = segX + 1;
            for (int j = 0; j < segZ; j++)
                for (int i = 0; i < segX; i++)
                {
                    int a = j * w + i, b = a + 1, c = a + w, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b); tris.Add(b); tris.Add(c); tris.Add(d);
                }
            var mesh = new Mesh { name = "Edge Sydney gravel footpath", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            SaveMesh(mesh, "SydFootpath");
            var go = new GameObject("Footpath", typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = pathMat;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.isStatic = true;
        }

        // Sandstone kerb/edge strip both sides of the path: 0.28 m wide blocks, top 6 cm proud of the
        // path, bedded 8 cm into the soil on the outer side, with a 3 cm joint every 0.9 m.
        static void BuildSandstoneEdging(Material sandstone)
        {
            var verts = new List<Vector3>(); var tris = new List<int>();
            const float width = 0.28f, proud = 0.06f, step = 0.9f, joint = 0.03f;
            for (int side = -1; side <= 1; side += 2)
            {
                float arc = 0f, zPrev = -PathHalfLength, blockStart = -PathHalfLength;
                for (float z = -PathHalfLength; z <= PathHalfLength; z += 0.05f)
                {
                    arc += Vector2.Distance(new Vector2(PathX(zPrev), zPrev), new Vector2(PathX(z), z)); zPrev = z;
                    if (arc < step) continue;
                    arc = 0f;
                    float z0 = blockStart, z1 = z - joint;
                    blockStart = z;
                    // one block: 4 cross-section points at z0 and z1 (inner low, inner top, outer top, outer low)
                    int b0 = verts.Count;
                    foreach (float zz in new[] { z0, z1 })
                    {
                        Vector2 t = PathTangent(zz), n = new Vector2(t.y, -t.x) * side;
                        Vector2 c = new Vector2(PathX(zz), zz);
                        Vector2 pi = c + n * (PathHalfWidth - 0.02f), po = c + n * (PathHalfWidth + width);
                        float hi = Height(pi.x, pi.y), ho = Height(po.x, po.y);
                        verts.Add(new Vector3(pi.x, hi - 0.02f, pi.y));
                        verts.Add(new Vector3(pi.x, hi + proud, pi.y));
                        verts.Add(new Vector3(po.x, ho + proud, po.y));
                        verts.Add(new Vector3(po.x, ho - 0.08f, po.y));
                    }
                    void Quad(int a, int b, int c2, int d) { tris.AddRange(new[] { a, b, c2, a, c2, d }); }
                    // faces: inner, top, outer, end caps (winding chosen per side)
                    int[,] faces = { { 0, 1, 5, 4 }, { 1, 2, 6, 5 }, { 2, 3, 7, 6 }, { 0, 3, 2, 1 }, { 4, 5, 6, 7 } };
                    for (int f = 0; f < faces.GetLength(0); f++)
                    {
                        if (side > 0) Quad(b0 + faces[f, 3], b0 + faces[f, 2], b0 + faces[f, 1], b0 + faces[f, 0]);
                        else Quad(b0 + faces[f, 0], b0 + faces[f, 1], b0 + faces[f, 2], b0 + faces[f, 3]);
                    }
                }
            }
            var mesh = new Mesh { name = "Edge Sydney sandstone edging", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            SaveMesh(mesh, "SydSandstoneEdging");
            var go = new GameObject("SandstoneEdging", typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = sandstone;
            r.shadowCastingMode = ShadowCastingMode.Off; // PERF: 6 cm kerb
            go.isStatic = true;
        }

        // Low weathered sandstone shelf (Hawkesbury-sandstone-like outcrop): irregular outline ~2-3 m across,
        // 0.35-0.6 m thick, slightly domed, flat underside below ground. Origin at the ground plane centre.
        static Mesh LedgeMesh(int seed)
        {
            var rs = Random.state; Random.InitState(9100 + seed);
            const int ring = 18;
            float rx = Random.Range(1.4f, 2.2f), rz = Random.Range(0.9f, 1.5f), h = Random.Range(0.35f, 0.6f);
            var verts = new List<Vector3> { new Vector3(0f, h + 0.05f, 0f) };
            var outer = new List<Vector2>();
            for (int i = 0; i < ring; i++)
            {
                float a = i * Mathf.PI * 2f / ring;
                float k = 1f + 0.22f * (Mathf.PerlinNoise(seed * 3.1f + Mathf.Cos(a) * 1.5f, Mathf.Sin(a) * 1.5f) - 0.5f) * 2f;
                outer.Add(new Vector2(Mathf.Cos(a) * rx * k, Mathf.Sin(a) * rz * k));
            }
            for (int i = 0; i < ring; i++) verts.Add(new Vector3(outer[i].x * 0.6f, h + Random.Range(-0.04f, 0.04f), outer[i].y * 0.6f)); // top inner
            for (int i = 0; i < ring; i++) verts.Add(new Vector3(outer[i].x * 0.92f, h * Random.Range(0.75f, 0.9f), outer[i].y * 0.92f)); // shoulder
            for (int i = 0; i < ring; i++) verts.Add(new Vector3(outer[i].x, -0.1f, outer[i].y)); // foot (below ground)
            Random.state = rs;
            var tris = new List<int>();
            for (int i = 0; i < ring; i++)
            {
                int j = (i + 1) % ring;
                tris.AddRange(new[] { 0, 1 + j, 1 + i });
                int a0 = 1 + i, a1 = 1 + j, b0 = 1 + ring + i, b1 = 1 + ring + j, c0 = 1 + 2 * ring + i, c1 = 1 + 2 * ring + j;
                tris.AddRange(new[] { a0, a1, b1, a0, b1, b0 });
                tris.AddRange(new[] { b0, b1, c1, b0, c1, c0 });
            }
            var mesh = new Mesh { name = "Edge Sydney sandstone ledge " + seed };
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            SaveMesh(mesh, "SydSandstoneLedge" + seed);
            return mesh;
        }
    }
}
