using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Edge.EditorTools
{
    // Builds Assets/Edge/Scenes/EdgeBushland.unity from nothing.
    // CLI: Unity -batchmode -projectPath <proj>
    //   -executeMethod Edge.EditorTools.EdgeSceneBuilder.Build -quit
    //
    // Photoreal pass: reuses the scanned PBR textures, materials and sculpted
    // meshes from Assets/MoonlitIsland (read only; nothing there is modified).
    // partial: EdgeSydneySceneBuilder.cs adds the Sydney starter-pack variant using these helpers.
    public static partial class EdgeSceneBuilder
    {
        const string Root = "Assets/Edge";
        const string ScenePath = Root + "/Scenes/EdgeBushland.unity";
        const string Moon = "Assets/MoonlitIsland";
        const float HalfSize = 110f;      // ground is 220 m across
        const int GroundRes = 220;        // ~1 m vertex spacing
        // Real-world layout, 1 unit = 1 m. Flight volume (BatFlightController) is x,z in [-100,100].
        const float PathHalfWidth = 0.9f;     // 1.8 m footpath
        const float PathHalfLength = 105f;    // runs the full length of the flight area
        const float VergeWidth = 4f;          // cleared, low ground cover each side of the path
        const float BushStart = PathHalfWidth + VergeWidth; // 4.9 m from path centre
        const float LampSpacing = 18f;        // m between path lamps (one side only)
        const float LampHeight = 4.5f;        // m, luminaire height on the post
        const float BatWingspan = 0.30f;      // m, Nyctophilus gouldi
        const float BatBodyLength = 0.06f;    // m

        // Gently curving path centreline (x as a function of z).
        static float PathX(float z) => 4.5f * Mathf.Sin(z * 0.021f + 0.6f) + 1.8f * Mathf.Sin(z * 0.057f + 2.1f);
        static float PathDist(float x, float z) => Mathf.Abs(x - PathX(z));

        [MenuItem("EDGE/Build Bushland Scene")]
        public static void Build()
        {
            Directory.CreateDirectory(Root + "/Scenes");
            Directory.CreateDirectory(Root + "/Settings");
            Directory.CreateDirectory(Root + "/Materials");
            Directory.CreateDirectory(Root + "/Meshes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Scanned ground surfaces (Color + Normal + AO), tiling in world metres.
            // Poly Haven CC0 ground scans (4K floor/path, 2K patches), falling back to the MoonlitIsland scans.
            Material floorMat = PHGroundMat("Edge_PH_ForestFloor", "forest_floor", "4k", 0.25f)
                                ?? ScanMat("Edge_ForestFloor", "ForestFloor", new Color(0.78f, 0.74f, 0.68f), 0.30f, 0.35f);
            Material mossMat = PHGroundMat("Edge_PH_LeavesForestGround", "leaves_forest_ground", "2k", 0.3f)
                                ?? ScanMat("Edge_MossGround", "MossGround", new Color(0.80f, 0.85f, 0.78f), 0.25f, 0.30f);
            Material pathMat = PHGroundMat("Edge_PH_ForestLeavesPath", "forest_leaves_02", "4k", 0.4f)
                                ?? ScanMat("Edge_LeafLitterPath", "LeafLitter", new Color(0.85f, 0.80f, 0.72f), 0.35f, 0.55f);
            Material postMat = LitMat("Edge_Post", new Color(0.18f, 0.18f, 0.19f), 0.55f, 0.6f);
            Material headMat = EmissiveMat("Edge_LampHead", AmberColor(), 6000f);

            // Existing MoonlitIsland PBR materials.
            Material bark = LoadMat("Cedar bark scanned");
            Material needleA = LoadMat("Pine deep green");
            Material needleB = LoadMat("Pine blue green");
            Material batFur = LoadMat("Bat fur charcoal");
            Material batWing = LoadMat("Bat wing membrane");

            BuildSky();

            var ground = BuildGround(floorMat, mossMat);
            var path = BuildPath(pathMat);

            var bat = BuildBat(batFur, batWing);
            float bz = -45f, bx = PathX(bz);
            bat.transform.position = new Vector3(bx, Height(bx, bz) + 3f, bz); // 3 m over the path

            var sys = new GameObject("EdgeSystem");
            var link = sys.AddComponent<UdpLink>();
            var dose = sys.AddComponent<DoseModel>();
            dose.link = link;
            var hud = sys.AddComponent<EdgeHud>();
            var flight = bat.AddComponent<BatFlightController>();
            flight.link = link;
            flight.dose = dose;
            flight.minAltitude = 1f;   // N. gouldi forages ~1-10 m above ground
            flight.maxAltitude = 10f;

            // Cameras sized for a 0.30 m bat (mesh measured and normalised in BuildBat).
            var fpvGo = new GameObject("FPV Camera");
            fpvGo.transform.SetParent(bat.transform, false);
            fpvGo.transform.localPosition = new Vector3(0f, 0.012f, BatBodyLength * 0.55f); // at the head
            var fpv = fpvGo.AddComponent<Camera>();
            fpv.fieldOfView = 95f;
            fpv.nearClipPlane = 0.01f;
            fpv.farClipPlane = 400f;
            fpvGo.AddComponent<HDAdditionalCameraData>().allowDynamicResolution = true; // PERF: STP 75%
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
            cams.followDistance = 1.6f;  // ~5 wingspans behind
            cams.followHeight = 0.45f;
            cams.lookHeight = 0.02f;
            hud.dose = dose;
            hud.flight = flight;
            hud.cams = cams;

            // Lamps on posts along the east side of the path, LampSpacing apart.
            const int lampCount = 8; // 2026-10-22: 18 m spacing covers the same ~125 m path (was 6 x 25 m)
            for (int i = 0; i < lampCount; i++)
            {
                float z = -62.5f + i * LampSpacing;
                float x = PathX(z) + PathHalfWidth + 0.6f;
                BuildLamp(i, new Vector3(x, Height(x, z), z), postMat, headMat, bat.transform, dose, link);
            }

            var trees = new GameObject("Trees").transform;
            var bushes = new GameObject("Bushes").transform;
            var rocks = new GameObject("Rocks").transform;
            Random.InitState(5047); // deterministic layout; seed is a declared number

            var canopy = LoadPH(new[] { "island_tree_02" });
            var small = LoadPH(new[] { "tree_small_02" });
            var logs = LoadPH(new[] { "dead_tree_trunk_02" });
            var shrubs = LoadPH(new[] { "shrub_02", "shrub_04" });
            var ferns = LoadPH(new[] { "fern_02" });
            var rockModels = LoadPH(RockIds);

            // ROW LAYOUT: exactly 2 staggered rows of trees per side of the path, following the
            // curved centreline (tangent/normal). Row 1 ~6.5 m from the centre (just past the 4.9 m
            // verge), row 2 ~11.5 m. Trees 8-10 m apart along each row (verge planting distance
            // for medium-large trees); row 2 shifted half a spacing so the rows zig-zag.
            var occupied = new List<Vector3>(); // x, z, keep-out radius
            for (int i = 0; i < lampCount; i++)
            {
                float lz = -62.5f + i * LampSpacing;
                occupied.Add(new Vector3(PathX(lz) + PathHalfWidth + 0.6f, lz, 2.5f)); // lamp posts
            }
            int nCanopy = 0, nSmall = 0, nLog = 0, nShrub = 0, nFern = 0, nRock = 0;
            var treeSpots = new List<Vector3>(); // x, z, row offset
            float[] rowOffset = { 6.5f, 11.5f };
            for (int side = -1; side <= 1; side += 2)
                for (int row = 0; row < 2; row++)
                {
                    float spacing0 = 9f;
                    float arc = row == 1 ? spacing0 * 0.5f : 0f; // zig-zag phase
                    float nextAt = arc;
                    // March along the centreline by arc length.
                    float zPrev = -PathHalfLength, travelled = 0f;
                    for (float z = -PathHalfLength; z <= PathHalfLength; z += 0.25f)
                    {
                        travelled += Vector2.Distance(new Vector2(PathX(zPrev), zPrev), new Vector2(PathX(z), z));
                        zPrev = z;
                        if (travelled < nextAt) continue;
                        nextAt = travelled + Random.Range(8f, 10f);
                        Vector2 c = new Vector2(PathX(z), z);
                        Vector2 t = new Vector2(PathX(z + 0.5f) - PathX(z - 0.5f), 1f).normalized; // tangent
                        Vector2 n = new Vector2(t.y, -t.x);                                         // normal (+x side)
                        Vector2 p = c + n * side * (rowOffset[row] + Random.Range(-0.8f, 0.8f))
                                      + t * Random.Range(-0.8f, 0.8f);
                        if (Mathf.Abs(p.x) > HalfSize - 3f || Mathf.Abs(p.y) > HalfSize - 3f) continue;
                        if (PathDist(p.x, p.y) < BushStart + 0.8f || !Free(occupied, p.x, p.y, 2f, true)) continue;
                        var pos = new Vector3(p.x, Height(p.x, p.y), p.y);
                        // island_tree_02 (~1.07M tris) is used more than tree_small_02 (~2.06M tris).
                        if (Random.value < 0.75f && canopy.Count > 0)
                        {
                            float h = row == 0 ? Random.Range(10f, 16f) : Random.Range(13f, 22f);
                            PlaceSized(trees, "Tree", canopy[0], pos, h, SizeAxis.Height, 2f, 0.25f); nCanopy++;
                        }
                        else if (small.Count > 0)
                        { PlaceSized(trees, "Tree", small[0], pos, Random.Range(6f, 10f), SizeAxis.Height, 3f, 0.2f); nSmall++; }
                        occupied.Add(new Vector3(p.x, p.y, 1.2f));
                        treeSpots.Add(new Vector3(p.x, p.y, side * (row + 1)));
                    }
                }
            BuildFurniture(occupied); // before plants so clumps keep clear of benches/signs

            // PLANTS in natural clumps of 3-8 (mixed variants/sizes, slight overlap, random yaw, tint).
            // Real sizes: bracken-like ferns 0.5-1.0 m; shrubs 0.6-2 m. shrub_04 is authored tiny
            // (0.19-0.27 m) so it is capped at 3x scale (<= ~0.8 m) and only used as a small shrub.
            PHModel shrub02 = shrubs.Find(x => x.id == "shrub_02"), shrub04 = shrubs.Find(x => x.id == "shrub_04");
            PHModel fernM = ferns.Count > 0 ? ferns[0] : null;
            int nClumps = 0;
            void PlantClump(Vector2 c, float radius, int count, float fernBias)
            {
                nClumps++;
                for (int k = 0; k < count; k++)
                {
                    Vector2 q = c + Random.insideUnitCircle * radius;
                    if (PathDist(q.x, q.y) < PathHalfWidth + 0.6f || Mathf.Abs(q.x) > HalfSize - 2f || Mathf.Abs(q.y) > HalfSize - 2f) continue;
                    if (!Free(occupied, q.x, q.y, 0.25f, true)) continue; // small keep-out: plants may touch
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
            foreach (var t in treeSpots) // under the trees (dense)
            {
                if (Random.value > 0.6f) continue;
                float side = Mathf.Sign(t.z);
                PlantClump(new Vector2(t.x + side * Random.Range(1.5f, 3f), t.y + Random.Range(-2.5f, 2.5f)), 1.5f, Random.Range(3, 9), 0.6f);
            }
            for (float z = -HalfSize + 3f; z < HalfSize - 3f; z += Random.Range(7f, 11f)) // verge-bush edge (dense)
                for (int side = -1; side <= 1; side += 2)
                    PlantClump(new Vector2(PathX(z) + side * (BushStart + Random.Range(-0.4f, 0.8f)), z), 1.6f, Random.Range(4, 9), 0.55f);
            for (float z = -HalfSize + 5f; z < HalfSize - 5f; z += Random.Range(25f, 40f)) // open verge (sparse ferns)
                for (int side = -1; side <= 1; side += 2)
                    PlantClump(new Vector2(PathX(z) + side * Random.Range(2.2f, 3.8f), z), 0.7f, Random.Range(1, 4), 1f);
            Debug.Log($"[EDGE] Plants: {nClumps} clumps, {nFern} ferns, {nShrub} shrubs (shrub_04 capped at 3x).");

            // A few fallen logs between the rows.
            for (int attempt = 0; attempt < 400 && nLog < 4 && logs.Count > 0; attempt++)
            {
                float z = Random.Range(-90f, 90f), side = Random.value < 0.5f ? -1f : 1f;
                float x = PathX(z) + side * Random.Range(8.5f, 9.5f);
                if (!Free(occupied, x, z, 3f, true)) continue;
                PlaceSized(trees, "Tree", logs[0], new Vector3(x, Height(x, z), z), Random.Range(4f, 6f), SizeAxis.Length, 2f, 0.12f);
                occupied.Add(new Vector3(x, z, 3f)); nLog++;
            }
            // Rocks: a few small clusters near the path (just past the verge).
            for (int c = 0, tries = 0; c < 5 && tries < 500 && rockModels.Count > 0; tries++)
            {
                float cz = Random.Range(-95f, 95f), cside = Random.value < 0.5f ? -1f : 1f;
                float cx = PathX(cz) + cside * Random.Range(BushStart + 0.5f, BushStart + 3f);
                c++;
                int n = Random.Range(2, 5);
                for (int k = 0; k < n; k++)
                {
                    float x = cx + Random.Range(-1.5f, 1.5f), z = cz + Random.Range(-1.5f, 1.5f);
                    float size = k == 0 ? Random.Range(0.6f, 1.2f) : Random.Range(0.3f, 0.6f);
                    if (PathDist(x, z) < BushStart || !Free(occupied, x, z, size * 0.6f, true)) continue;
                    PlaceSized(rocks, "Rock", rockModels[Random.Range(0, rockModels.Count)],
                               new Vector3(x, Height(x, z), z), size, SizeAxis.Height, 8f, size * 0.2f);
                    occupied.Add(new Vector3(x, z, size * 0.6f)); nRock++;
                }
            }

            BuildFarTreeline();
            BuildGrass(occupied, fpv, tpv);
            BuildPedestrians(fpv, tpv);
            LogSceneCost(trees, bushes, rocks);

            Debug.Log("[EDGE] Colliders: " + EdgeColliderPass.Apply());
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[EDGE] Bushland scene built at {ScenePath}: {lampCount} lamps ({LampSpacing} m apart, {LampHeight} m high), " +
                      $"{nCanopy} canopy + {nSmall} small trees, {nLog} logs, {nShrub} shrubs, {nFern} ferns, {nRock} rocks, UDP port {EdgeConfig.UdpPort}");
        }

        // ---------- park furniture ----------
        // Benches: procedural modern council bench (BuildBench: 1.8 m long, seat 0.45 m, ~0.6 m deep, back 0.85 m), one every
        // 40-60 m of path, alternating sides, set back 0.5-1.0 m from the path edge, facing the path.
        // Signposts (~2.2 m) at both path ends and the two sharpest bends; one drinking fountain (~1 m).
        // Signs/fountain are simple shapes textured with Poly Haven CC0 PBR scans (brown_planks_05,
        // blue_metal_plate, brushed_concrete). All: no colliders, static, culled at 2% screen height.
        static Vector2 PathTangent(float z) => new Vector2(PathX(z + 0.5f) - PathX(z - 0.5f), 1f).normalized;

        static void BuildFurniture(List<Vector3> occupied)
        {
            var root = new GameObject("Furniture").transform;
            int nBench = 0, nSign = 0, nFountain = 0;
            // Modern council-style bench, built procedurally (see BuildBench); the old Poly Haven pew is no longer used.

            // Benches by arc length.
            {
                float zPrev = -PathHalfLength + 5f, travelled = 0f, nextAt = Random.Range(15f, 30f);
                int side = -1;
                for (float z = zPrev; z <= PathHalfLength - 5f; z += 0.25f)
                {
                    travelled += Vector2.Distance(new Vector2(PathX(zPrev), zPrev), new Vector2(PathX(z), z));
                    zPrev = z;
                    if (travelled < nextAt) continue;
                    Vector2 t = PathTangent(z), n = new Vector2(t.y, -t.x);
                    float setback = PathHalfWidth + Random.Range(0.5f, 1.0f) + 0.25f; // edge + gap + half bench depth
                    Vector2 c = new Vector2(PathX(z), z) + n * side * setback;
                    if (!Free(occupied, c.x, c.y, 1.2f, true)) { side = -side; c = new Vector2(PathX(z), z) + n * side * setback; }
                    if (!Free(occupied, c.x, c.y, 1.2f, true)) continue; // e.g. a lamp post here; try further on
                    nextAt = travelled + Random.Range(40f, 60f);
                    var go = BuildBench(root, new Vector3(c.x, Height(c.x, c.y), c.y), new Vector3(-n.x * side, 0f, -n.y * side));
                    if (nBench == 0) { var b = WorldBounds(go); Debug.Log($"[EDGE] Bench: {b.size.x:F2} x {b.size.z:F2} m footprint incl. pad, {b.size.y:F2} m tall; seat 0.45 m, 1.8 m long, ~0.6 m deep, back 0.85 m"); }
                    occupied.Add(new Vector3(c.x, c.y, 1.1f)); nBench++;
                    side = -side;
                }
            }

            var wood = PHGroundMat("Edge_Sign_Wood", "brown_planks_05", "2k", 1f);
            var plate = PHGroundMat("Edge_Sign_Plate", "blue_metal_plate", "2k", 1.5f);
            var conc = PHGroundMat("Edge_Fountain_Concrete", "brushed_concrete", "2k", 1.5f);
            var steel = LitMat("Edge_Fountain_Steel", new Color(0.62f, 0.63f, 0.64f), 0.75f, 1f);

            // Signposts: the two path ends + the two sharpest bends (largest |PathX''|).
            var signZ = new List<float> { -PathHalfLength + 3f, PathHalfLength - 3f };
            var bends = new List<KeyValuePair<float, float>>();
            for (float z = -90f; z <= 90f; z += 1f)
            {
                float k = Mathf.Abs(PathX(z + 1f) - 2f * PathX(z) + PathX(z - 1f));
                float kl = Mathf.Abs(PathX(z) - 2f * PathX(z - 1f) + PathX(z - 2f)), kr = Mathf.Abs(PathX(z + 2f) - 2f * PathX(z + 1f) + PathX(z));
                if (k >= kl && k >= kr) bends.Add(new KeyValuePair<float, float>(z, k));
            }
            bends.Sort((x, y) => y.Value.CompareTo(x.Value));
            foreach (var kv in bends) { if (signZ.Count >= 4) break; if (signZ.TrueForAll(q => Mathf.Abs(q - kv.Key) > 30f)) signZ.Add(kv.Key); }
            foreach (float z in signZ)
            {
                Vector2 t = PathTangent(z), n = new Vector2(t.y, -t.x);
                // Outside of the bend (or west side at the ends), 0.75 m off the edge.
                float side = Mathf.Sign(PathX(z + 1f) - 2f * PathX(z) + PathX(z - 1f));
                if (Mathf.Abs(z) > 90f || side == 0f) side = -1f;
                Vector2 c = new Vector2(PathX(z), z) + n * side * (PathHalfWidth + 0.75f);
                if (!Free(occupied, c.x, c.y, 0.5f, true)) c += n * side * 1.0f;
                var sign = new GameObject("Signpost").transform;
                sign.SetParent(root, false);
                sign.position = new Vector3(c.x, Height(c.x, c.y) - 0.3f, c.y); // 0.3 m footing in the soil
                sign.rotation = Quaternion.LookRotation(new Vector3(t.x, 0f, t.y), Vector3.up);
                Prim(sign, "Post", PrimitiveType.Cube, wood, new Vector3(0f, 1.25f, 0f), new Vector3(0.1f, 2.5f, 0.1f), Quaternion.identity);
                Prim(sign, "Board_A", PrimitiveType.Cube, plate, new Vector3(0.3f, 2.25f, 0f), new Vector3(0.55f, 0.16f, 0.025f), Quaternion.Euler(0f, 0f, 0f));
                Prim(sign, "Board_B", PrimitiveType.Cube, plate, new Vector3(-0.28f, 1.98f, 0f), new Vector3(0.5f, 0.16f, 0.025f), Quaternion.Euler(0f, 25f, 0f));
                Prim(sign, "Cap", PrimitiveType.Cube, wood, new Vector3(0f, 2.52f, 0f), new Vector3(0.14f, 0.04f, 0.14f), Quaternion.identity);
                FinishProp(sign.gameObject);
                occupied.Add(new Vector3(c.x, c.y, 0.6f)); nSign++;
            }

            // Drinking fountain near the middle of the path, west side (lamps are on the east side).
            for (float z = 5f; z < 40f && nFountain == 0; z += 2f)
            {
                Vector2 t = PathTangent(z), n = new Vector2(t.y, -t.x);
                Vector2 c = new Vector2(PathX(z), z) - n * (PathHalfWidth + 0.8f);
                if (!Free(occupied, c.x, c.y, 0.6f, true)) continue;
                var f = new GameObject("DrinkingFountain").transform;
                f.SetParent(root, false);
                f.position = new Vector3(c.x, Height(c.x, c.y), c.y);
                f.rotation = Quaternion.LookRotation(new Vector3(n.x, 0f, n.y), Vector3.up); // spout faces the path
                Prim(f, "Pad", PrimitiveType.Cylinder, conc, new Vector3(0f, 0.03f, 0f), new Vector3(0.9f, 0.04f, 0.9f), Quaternion.identity);
                Prim(f, "Column", PrimitiveType.Cylinder, conc, new Vector3(0f, 0.46f, 0f), new Vector3(0.28f, 0.42f, 0.28f), Quaternion.identity);
                Prim(f, "Bowl", PrimitiveType.Cylinder, steel, new Vector3(0f, 0.93f, 0.05f), new Vector3(0.38f, 0.05f, 0.38f), Quaternion.identity);
                Prim(f, "Spout", PrimitiveType.Cylinder, steel, new Vector3(0f, 1.0f, 0.12f), new Vector3(0.03f, 0.04f, 0.03f), Quaternion.identity);
                FinishProp(f.gameObject);
                occupied.Add(new Vector3(c.x, c.y, 0.8f)); nFountain++;
            }
            Debug.Log($"[EDGE] Furniture: {nBench} benches (1.8 m, every 40-60 m), {nSign} signposts (2.2 m), {nFountain} drinking fountain (1.0 m)");
        }

        // Modern Australian council-style park bench, real dimensions (1 unit = 1 m):
        // 1.8 m long, seat top 0.45 m, ~0.6 m deep, backrest top 0.85 m, armrests at both ends,
        // 4 seat + 3 back timber slats running along the length (Poly Haven brown_planks_05, CC0),
        // dark grey powder-coated steel frames (3 supports) and a small concrete pad it is bolted to.
        // Local frame: +X along the bench, +Z = front (faces 'facing'). ~40 cube parts, ~500 tris.
        static GameObject BuildBench(Transform parent, Vector3 groundPos, Vector3 facing)
        {
            var wood = PHGroundMat("Edge_Bench_Timber", "brown_planks_05", "2k", 1f);
            if (wood) { wood.SetTextureScale("_BaseColorMap", new Vector2(0.6f, 0.15f)); EditorUtility.SetDirty(wood); } // grain runs along each slat
            var steel = LitMat("Edge_Bench_PowderCoat", new Color(0.075f, 0.08f, 0.085f), 0.42f, 0f);
            var conc = PHGroundMat("Edge_Fountain_Concrete", "brushed_concrete", "2k", 1.5f);
            var bench = new GameObject("Bench").transform;
            bench.SetParent(parent, false);
            bench.position = groundPos;
            bench.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up) * Quaternion.Euler(0f, 0f, 0f);
            // LookRotation maps local +Z to 'facing'; so the bench front is +Z.

            const float L = 1.8f, padH = 0.06f;
            float y0 = padH; // frame stands on the pad
            Prim(bench, "Pad", PrimitiveType.Cube, conc, new Vector3(0f, padH * 0.5f - 0.03f, -0.02f), new Vector3(L + 0.3f, padH + 0.06f, 0.95f), Quaternion.identity);

            // Seat slats: 4 x (1.8 x 0.035 x 0.09) with 0.02 m gaps; top surface at 0.45 m; seat depth 0.45 m.
            for (int i = 0; i < 4; i++)
            {
                float z = 0.21f - i * 0.11f;
                Prim(bench, "SeatSlat" + i, PrimitiveType.Cube, wood, new Vector3(0f, 0.45f - 0.0175f, z), new Vector3(L, 0.035f, 0.09f), Quaternion.identity);
                // cheap bevel: slightly narrower, darker edge strip under each slat reads as a rounded arris
                Prim(bench, "SeatSlatEdge" + i, PrimitiveType.Cube, wood, new Vector3(0f, 0.45f - 0.038f, z), new Vector3(L - 0.01f, 0.008f, 0.08f), Quaternion.identity);
            }
            // Backrest: 3 slats on a 15 deg recline, from ~0.55 to 0.85 m high.
            var recline = Quaternion.Euler(-15f, 0f, 0f);
            for (int i = 0; i < 3; i++)
            {
                float h = 0.58f + i * 0.11f;
                float z = -0.2f - (h - 0.5f) * Mathf.Tan(15f * Mathf.Deg2Rad);
                Prim(bench, "BackSlat" + i, PrimitiveType.Cube, wood, new Vector3(0f, h, z), new Vector3(L, 0.09f, 0.03f), recline);
            }

            // Frames at both ends (with armrests) and one centre support.
            foreach (float x in new[] { -0.86f, 0f, 0.86f })
            {
                bool end = x != 0f;
                string n = end ? (x < 0 ? "FrameL" : "FrameR") : "FrameC";
                float legH = 0.45f - 0.035f - y0;
                Prim(bench, n + "_FrontLeg", PrimitiveType.Cube, steel, new Vector3(x, y0 + legH * 0.5f, 0.22f), new Vector3(0.05f, legH, 0.05f), Quaternion.identity);
                Prim(bench, n + "_RearLeg", PrimitiveType.Cube, steel, new Vector3(x, y0 + legH * 0.5f, -0.2f), new Vector3(0.05f, legH, 0.05f), Quaternion.identity);
                Prim(bench, n + "_SeatRail", PrimitiveType.Cube, steel, new Vector3(x, 0.45f - 0.035f - 0.02f, 0.01f), new Vector3(0.05f, 0.04f, 0.5f), Quaternion.identity);
                Prim(bench, n + "_FootRail", PrimitiveType.Cube, steel, new Vector3(x, y0 + 0.08f, 0.01f), new Vector3(0.04f, 0.03f, 0.44f), Quaternion.identity);
                // Back upright, reclined, from seat rail to 0.85 m.
                float bh = 0.85f - 0.40f;
                Prim(bench, n + "_BackUpright", PrimitiveType.Cube, steel,
                     new Vector3(x, 0.40f + bh * 0.5f, -0.23f - bh * 0.5f * Mathf.Tan(15f * Mathf.Deg2Rad)), new Vector3(0.05f, bh, 0.04f), recline);
                // Foot plates bolted to the pad.
                Prim(bench, n + "_FootPlateF", PrimitiveType.Cube, steel, new Vector3(x, y0 + 0.004f, 0.22f), new Vector3(0.12f, 0.008f, 0.12f), Quaternion.identity);
                Prim(bench, n + "_FootPlateR", PrimitiveType.Cube, steel, new Vector3(x, y0 + 0.004f, -0.2f), new Vector3(0.12f, 0.008f, 0.12f), Quaternion.identity);
                if (end)
                {
                    float ax = x + Mathf.Sign(x) * 0.0f;
                    Prim(bench, n + "_ArmPost", PrimitiveType.Cube, steel, new Vector3(ax, 0.45f + 0.1f, 0.2f), new Vector3(0.04f, 0.2f, 0.04f), Quaternion.identity);
                    Prim(bench, n + "_Armrest", PrimitiveType.Cube, steel, new Vector3(ax, 0.665f, 0.0f), new Vector3(0.06f, 0.03f, 0.5f), Quaternion.identity);
                    Prim(bench, n + "_ArmrestCap", PrimitiveType.Cube, wood, new Vector3(ax, 0.685f, 0.0f), new Vector3(0.07f, 0.012f, 0.48f), Quaternion.identity);
                }
            }
            foreach (var r in bench.GetComponentsInChildren<MeshRenderer>(true)) r.shadowCastingMode = ShadowCastingMode.On;
            // Pad and foot plates cast no shadow (flat); saves a little shadow-map work.
            foreach (var r in bench.GetComponentsInChildren<MeshRenderer>(true))
                if (r.name == "Pad" || r.name.Contains("FootPlate") || r.name.Contains("Edge")) r.shadowCastingMode = ShadowCastingMode.Off;
            FinishProp(bench.gameObject); // static + LODGroup cull at 2% screen height
            return bench.gameObject;
        }


        // Turn a bench so its open (front) side faces 'towards'. The backrest is the upper part of the
        // mesh, so the front is opposite the horizontal offset of the upper vertices from the centre.
        static void FacePath(GameObject go, Vector2 at, Vector2 towards)
        {
            var b = WorldBounds(go);
            Vector3 upper = Vector3.zero; int n = 0;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh || System.Text.RegularExpressions.Regex.IsMatch(mf.name, @"_LOD[1-9]$")) continue;
                var v = mf.sharedMesh.vertices;
                for (int i = 0; i < v.Length; i++)
                {
                    var w = mf.transform.TransformPoint(v[i]);
                    if (w.y > b.min.y + b.size.y * 0.7f) { upper += w - b.center; n++; }
                }
            }
            if (n == 0) return;
            Vector3 back = upper / n; back.y = 0f;
            if (back.sqrMagnitude < 1e-6f) return;
            Vector3 front = -back.normalized, want = new Vector3(towards.x, 0f, towards.y).normalized;
            float yaw = Vector3.SignedAngle(front, want, Vector3.up);
            go.transform.RotateAround(new Vector3(at.x, b.min.y, at.y), Vector3.up, yaw);
        }

        static void Prim(Transform parent, string name, PrimitiveType type, Material mat, Vector3 pos, Vector3 scale, Quaternion rot)
        {
            var g = GameObject.CreatePrimitive(type);
            g.name = name;
            Object.DestroyImmediate(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos; g.transform.localRotation = rot; g.transform.localScale = scale;
            if (mat) g.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        static void FinishProp(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;
            AddCull(go, 0.02f);
        }

        // ---------- pedestrians ----------
        // Microsoft Rocketbox Avatar Library (MIT): 8 realistic, textured, Biped-rigged adults in casual /
        // sporty clothes, imported as Humanoid. Animation: Quaternius Universal Animation Library (CC0)
        // clips (Walk_Loop / Jog_Fwd_Loop / Sitting_Idle_Loop / Driving_Loop) imported as Humanoid and
        // retargeted by Mecanim through one AnimatorController. drummyfish low-poly bicycle (CC0).
        // 8 moving agents (4 walkers, 3 runners, 1 cyclist, both directions, driven by EdgePedestrians)
        // + 2 people sitting on existing benches (benches are only read, never moved).
        const string CharFbx = Root + "/ThirdParty/Quaternius/AnimationLibrary_Unity_Standard.fbx";
        const string BikeObj = Root + "/ThirdParty/OpenGameArt/bicycle.obj";
        const string RbxDir = Root + "/ThirdParty/Rocketbox";
        const string PedController = Root + "/Animation/Edge_Pedestrian.controller";
        // Generated-asset name prefix for scene variants. Empty (the default) = the original EdgeBushland
        // asset paths, byte-for-byte as before; the Sydney variant sets it so it never deletes/recreates
        // (and so re-GUIDs) meshes, the sky profile or the controller that EdgeBushland.unity references.
        static string s_variant = "";
        static string PedControllerPath => s_variant.Length == 0 ? PedController : $"{Root}/Animation/{s_variant}Edge_Pedestrian.controller";
        // name, female?  (casual / sporty picks from Assets/Avatars/Adults)
        static readonly (string name, bool female)[] RbxAvatars = {
            ("Female_Adult_01", true),  // pink shirt, jeans
            ("Male_Adult_04", false),   // hoodie, chinos, sneakers
            ("Female_Adult_08", true),  // grey tee, jeans
            ("Male_Adult_01", false),   // striped tee, shorts, sneakers
            ("Female_Adult_12", true),  // hoodie, shorts, sneakers (runner)
            ("Male_Adult_10", false),   // red track jacket (runner)
            ("Male_Adult_17", false),   // blue/white track jacket (runner)
            ("Female_Adult_17", true),  // green tee, jeans (cyclist)
        };

        static readonly HashSet<string> LoggedAvatars = new HashSet<string>();

        static AnimationClip FindClip(string name)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(CharFbx))
                if (o is AnimationClip c && !c.name.StartsWith("__preview__") && (c.name == name || c.name.EndsWith("|" + name))) return c;
            Debug.LogWarning("[EDGE] clip not found in UAL: " + name);
            return null;
        }

        static RuntimeAnimatorController PedestrianController(Dictionary<string, AnimationClip> clips)
        {
            Directory.CreateDirectory(Root + "/Animation");
            AssetDatabase.DeleteAsset(PedControllerPath);
            var ac = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(PedControllerPath);
            var sm = ac.layers[0].stateMachine;
            foreach (var kv in clips)
            {
                var st = sm.AddState(kv.Key);
                st.motion = kv.Value;
                st.writeDefaultValues = true;
                st.iKOnFeet = false;
            }
            EnableIKPass(ac);
            AssetDatabase.SaveAssets();
            return ac;
        }

        // EdgeCyclist drives the rider's feet and hands from OnAnimatorIK, which only fires with IK Pass on.
        static void EnableIKPass(UnityEditor.Animations.AnimatorController ac)
        {
            var layers = ac.layers;
            if (layers.Length == 0 || layers[0].iKPass) return;
            layers[0].iKPass = true;
            ac.layers = layers;
            EditorUtility.SetDirty(ac);
        }

        // HDRP/Lit materials for one Rocketbox avatar: body + head (opaque, smoothness from the
        // specular map packed into the mask alpha) and the opacity sheet (hair, lashes, brows:
        // alpha-clipped, double-sided).
        static Dictionary<string, Material> RbxMats(string avatar)
        {
            string dir = $"{RbxDir}/{avatar}";
            string pre = null;
            foreach (var f in Directory.GetFiles(dir, "*_body_color.jpg")) pre = Path.GetFileName(f).Replace("_body_color.jpg", "");
            var res = new Dictionary<string, Material>();
            if (pre == null) return res;
            foreach (var part in new[] { "body", "head", "opacity" })
            {
                var m = GetOrCreate($"Edge_RBX_{avatar}_{part}");
                bool op = part == "opacity";
                var col = AssetDatabase.LoadAssetAtPath<Texture2D>(op ? $"{dir}/{pre}_opacity_color.png" : $"{dir}/{pre}_{part}_color.jpg");
                if (op && !col) continue;
                m.SetColor("_BaseColor", Color.white);
                m.SetTexture("_BaseColorMap", col);
                m.SetFloat("_Metallic", 0f);
                if (!op)
                {
                    var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/{pre}_{part}_normal.png");
                    var mask = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/{pre}_{part}_mask.png");
                    m.SetTexture("_NormalMap", nrm); m.SetFloat("_NormalScale", 1f);
                    m.SetTexture("_MaskMap", mask);
                    m.SetFloat("_MetallicRemapMin", 0f); m.SetFloat("_MetallicRemapMax", 0f);
                    m.SetFloat("_AORemapMin", 1f); m.SetFloat("_AORemapMax", 1f);
                    m.SetFloat("_SmoothnessRemapMin", 0.05f);
                    m.SetFloat("_SmoothnessRemapMax", part == "head" ? 0.55f : 0.45f);
                    m.SetFloat("_Smoothness", 0.4f);
                    m.SetFloat("_AlphaCutoffEnable", 0f);
                    m.SetFloat("_DoubleSidedEnable", 0f);
                }
                else
                {
                    m.SetTexture("_NormalMap", null); m.SetTexture("_MaskMap", null);
                    m.SetFloat("_Smoothness", 0.3f);
                    m.SetFloat("_AlphaCutoffEnable", 1f);
                    m.SetFloat("_AlphaCutoff", 0.35f);
                    m.SetFloat("_UseShadowThreshold", 1f);
                    m.SetFloat("_AlphaCutoffShadow", 0.5f);
                    m.SetFloat("_DoubleSidedEnable", 1f);
                    m.SetFloat("_DoubleSidedNormalMode", 0f);
                    m.SetFloat("_CullMode", 0f);
                    m.SetFloat("_CullModeForward", 0f);
                }
                HDMaterial.ValidateMaterial(m);
                EditorUtility.SetDirty(m);
                res[part] = m;
            }
            return res;
        }

        // Instantiate one Rocketbox avatar as a Humanoid body under 'parent', scaled to 'height' m,
        // HDRP materials swapped in, no colliders, Animator culled off-screen.
        static GameObject SpawnPerson(Transform parent, string avatar, float height, RuntimeAnimatorController ac,
                                      Dictionary<string, Dictionary<string, Material>> matCache)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{RbxDir}/{avatar}/{avatar}.fbx");
            if (!prefab) { Debug.LogWarning("[EDGE] Rocketbox avatar missing: " + avatar); return null; }
            if (!matCache.TryGetValue(avatar, out var mats)) matCache[avatar] = mats = RbxMats(avatar);
            var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            body.name = "Body_" + avatar;
            body.transform.SetParent(parent, false);
            foreach (var c in body.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            var smrs = body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            float native = 0f;
            var slots = new List<string>();
            foreach (var r in smrs)
            {
                r.updateWhenOffscreen = false;
                r.shadowCastingMode = ShadowCastingMode.On;
                native = Mathf.Max(native, r.bounds.size.y);
                var arr = r.sharedMaterials;
                for (int k = 0; k < arr.Length; k++)
                {
                    string n = arr[k] ? arr[k].name.ToLowerInvariant() : "";
                    string part = n.Contains("opacity") || n.Contains("hair") || n.Contains("lash") ? "opacity" : n.Contains("head") ? "head" : "body";
                    slots.Add((arr[k] ? arr[k].name : "null") + "->" + part);
                    if (mats.TryGetValue(part, out var hm)) arr[k] = hm;
                    else if (mats.TryGetValue("body", out var bm)) arr[k] = bm;
                }
                r.sharedMaterials = arr;
            }
            if (native > 1e-3f) body.transform.localScale *= height / native;
            var an = body.GetComponent<Animator>();
            if (!an) an = body.AddComponent<Animator>();
            an.runtimeAnimatorController = ac;
            an.applyRootMotion = false;
            an.cullingMode = AnimatorCullingMode.CullCompletely; // no animation work when off-screen
            if (!an.avatar || !an.avatar.isHuman) Debug.LogWarning($"[EDGE] {avatar}: Humanoid avatar not valid; retargeting will fail");
            if (LoggedAvatars.Add(avatar))
            { Debug.Log($"[EDGE] {avatar}: {native:F2} native -> {height:F2} m; slots {string.Join(", ", slots)}"); }
            return body;
        }

        // Pose 'body' with 'clip' at t=0 and return the world hips position. Evaluated through the Animator
        // (PlayableGraph), not AnimationMode.SampleAnimationClip: only the Animator applies the clip's
        // root-orientation import settings, so this is the pose Play mode shows. Bind pose restored after.
        static Vector3 SampledHips(GameObject body, AnimationClip clip)
        {
            var an = body.GetComponent<Animator>();
            var hips = an && an.isHuman ? an.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (!hips || !clip) return body.transform.position + Vector3.up * 0.9f;
            var bones = body.GetComponentsInChildren<Transform>(true);
            var rest = new (Vector3 p, Quaternion r, Vector3 s)[bones.Length];
            for (int i = 0; i < bones.Length; i++) rest[i] = (bones[i].localPosition, bones[i].localRotation, bones[i].localScale);
            var root = (bones[0].localPosition, bones[0].localRotation);
            var graph = UnityEngine.Playables.PlayableGraph.Create("EdgeSampledHips");
            var output = UnityEngine.Animations.AnimationPlayableOutput.Create(graph, "pose", an);
            UnityEngine.Playables.PlayableOutputExtensions.SetSourcePlayable(output, UnityEngine.Animations.AnimationClipPlayable.Create(graph, clip));
            graph.Evaluate(0f);
            var p = hips.position;
            graph.Destroy();
            for (int i = 0; i < bones.Length; i++) { bones[i].localPosition = rest[i].p; bones[i].localRotation = rest[i].r; bones[i].localScale = rest[i].s; }
            return p;
        }

        // Measure the body's mean facing (humanoid hips+shoulders "right" axis crossed with up, averaged
        // over one clip cycle through the Animator, i.e. with the clip's import root-orientation settings
        // applied) in agent space, and yaw the body about the agent's up axis so it faces agent +Z, the
        // direction EdgePedestrians moves it. Measured per clip, so it stays right if import settings change.
        // Returns the correction applied (deg). Idempotent: a second call measures ~0.
        static float AlignBodyToTravel(Transform agent, GameObject body, AnimationClip clip)
        {
            var an = body ? body.GetComponent<Animator>() : null;
            if (!an || !an.isHuman || !clip) return 0f;
            var lh = an.GetBoneTransform(HumanBodyBones.LeftUpperLeg); var rh = an.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            var ls = an.GetBoneTransform(HumanBodyBones.LeftUpperArm); var rs = an.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (!lh || !rh || !ls || !rs) return 0f;
            var bones = body.GetComponentsInChildren<Transform>(true);
            var rest = new (Vector3 p, Quaternion r, Vector3 s)[bones.Length];
            for (int i = 0; i < bones.Length; i++) rest[i] = (bones[i].localPosition, bones[i].localRotation, bones[i].localScale);
            var bodyPose = (body.transform.localPosition, body.transform.localRotation);
            var graph = UnityEngine.Playables.PlayableGraph.Create("EdgeAlignBody");
            var output = UnityEngine.Animations.AnimationPlayableOutput.Create(graph, "pose", an);
            var cp = UnityEngine.Animations.AnimationClipPlayable.Create(graph, clip);
            UnityEngine.Playables.PlayableOutputExtensions.SetSourcePlayable(output, cp);
            Vector3 sum = Vector3.zero;
            const int N = 24;
            for (int k = 0; k < N; k++)
            {
                UnityEngine.Playables.PlayableExtensions.SetTime(cp, clip.length * k / N);
                graph.Evaluate(0f);
                Vector3 right = (rh.position - lh.position) + (rs.position - ls.position);
                Vector3 f = agent.InverseTransformDirection(Vector3.Cross(right, agent.up)); f.y = 0f;
                if (f.sqrMagnitude > 1e-8f) sum += f.normalized;
            }
            graph.Destroy();
            for (int i = 0; i < bones.Length; i++) { bones[i].localPosition = rest[i].p; bones[i].localRotation = rest[i].r; bones[i].localScale = rest[i].s; }
            body.transform.localPosition = bodyPose.Item1; body.transform.localRotation = bodyPose.Item2;
            if (sum.sqrMagnitude < 1e-6f) return 0f;
            float yaw = Vector3.SignedAngle(sum, Vector3.forward, Vector3.up);
            if (Mathf.Abs(yaw) > 1f) body.transform.RotateAround(agent.position, agent.up, yaw);
            return yaw;
        }

        // Move 'body' (a child of 'holder') so its runtime hips land on 'hipsTarget' (holder space).
        static void SeatOn(Transform holder, GameObject body, AnimationClip clip, Vector3 hipsTarget)
        {
            var hipsL = holder.InverseTransformPoint(SampledHips(body, clip));
            body.transform.localPosition += hipsTarget - hipsL;
        }

        static void BuildPedestrians(Camera fpv, Camera tpv)
        {
            var clipWalk = FindClip("Walk_Loop"); var clipJog = FindClip("Jog_Fwd_Loop");
            var clipSit = FindClip("Sitting_Idle_Loop"); var clipRide = FindClip("Driving_Loop") ?? clipSit;
            if (!clipWalk || !clipJog) { Debug.LogWarning("[EDGE] pedestrian clips missing: " + CharFbx); return; }
            var clipMap = new Dictionary<string, AnimationClip> { { "Walk", clipWalk }, { "Jog", clipJog } };
            if (clipSit) clipMap["Sit"] = clipSit;
            if (clipRide) clipMap["Ride"] = clipRide;
            var ac = PedestrianController(clipMap);
            LoggedAvatars.Clear();
            var matCache = new Dictionary<string, Dictionary<string, Material>>();
            var bikePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BikeObj);
            var root = new GameObject("Pedestrians");
            var ped = root.AddComponent<EdgePedestrians>();

            var pts = new List<Vector3>();
            for (float z = -PathHalfLength + 1f; z <= PathHalfLength - 1f; z += 1f)
                pts.Add(new Vector3(PathX(z), Height(PathX(z), z) + 0.035f, z));
            var bikeMat = LitMat("Edge_Bike", new Color(0.08f, 0.08f, 0.09f), 0.55f, 0.6f);

            // kind: 0 walk, 1 run, 2 cycle
            int[] kinds = { 0, 0, 0, 0, 1, 1, 1, 2 };
            int[] dirs = { 1, -1, 1, -1, 1, -1, -1, 1 };
            var agents = new List<Transform>(); var speeds = new List<float>(); var clipNames = new List<string>(); var natural = new List<float>();
            bool cyclistOk = false;
            for (int i = 0; i < kinds.Length; i++)
            {
                int kind = kinds[i];
                var (avatar, female) = RbxAvatars[i % RbxAvatars.Length];
                var agent = new GameObject((kind == 0 ? "Walker_" : kind == 1 ? "Runner_" : "Cyclist_") + avatar).transform;
                agent.SetParent(root.transform, false);
                float h = female ? Random.Range(1.58f, 1.72f) : Random.Range(1.70f, 1.88f);
                var body = SpawnPerson(agent, avatar, h, ac, matCache);
                if (!body) { Object.DestroyImmediate(agent.gameObject); continue; }
                float speed; string clip; float nat;
                if (kind == 0) { speed = Random.Range(1.25f, 1.5f); clip = "Walk"; nat = 1.4f; }
                else if (kind == 1 || !bikePrefab || !clipMap.ContainsKey("Ride")) { kind = 1; speed = Random.Range(2.7f, 3.3f); clip = "Jog"; nat = 3.0f; }
                else
                {
                    speed = Random.Range(5f, 6f); clip = "Ride"; nat = 0f;
                    var bike = (GameObject)PrefabUtility.InstantiatePrefab(bikePrefab);
                    PrefabUtility.UnpackPrefabInstance(bike, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    bike.name = "Bicycle";
                    var bw = new GameObject("BikeRoot").transform; bw.SetParent(agent, false);
                    bike.transform.SetParent(bw, false);
                    foreach (var c in bike.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
                    foreach (var r in bike.GetComponentsInChildren<MeshRenderer>(true))
                    { var arr = r.sharedMaterials; for (int k = 0; k < arr.Length; k++) arr[k] = bikeMat; r.sharedMaterials = arr; }
                    var bb = WorldBounds(bike);
                    if (bb.size.x > bb.size.z) bike.transform.localRotation = Quaternion.Euler(0f, 90f, 0f) * bike.transform.localRotation;
                    bb = WorldBounds(bike);
                    float len = Mathf.Max(bb.size.x, bb.size.z);
                    if (len > 1e-3f) bw.localScale = Vector3.one * (1.75f / len); // adult bike ~1.75 m long
                    bb = WorldBounds(bike);
                    bw.localPosition -= new Vector3(bb.center.x, bb.min.y, bb.center.z);
                    // Find handlebars (widest top part) and saddle (top of the other half); make the bars face +Z.
                    Vector3 saddle; bool flip;
                    BikeLandmarks(bike, agent, out saddle, out flip);
                    if (flip)
                    {
                        bw.localRotation = Quaternion.Euler(0f, 180f, 0f) * bw.localRotation;
                        bw.localPosition = Quaternion.Euler(0f, 180f, 0f) * bw.localPosition;
                        BikeLandmarks(bike, agent, out saddle, out flip);
                    }
                    SeatOn(agent, body, clipMap["Ride"], RiderHips(saddle));
                    Debug.Log($"[EDGE] Bicycle: {WorldBounds(bike).size:F2} m, saddle at {saddle.y:F2} m, rider hips at {agent.InverseTransformPoint(SampledHips(body, clipMap["Ride"])).y:F2} m");
                    RigBike(agent, body, bike);
                    cyclistOk = true;
                }
                agent.name = (kind == 0 ? "Walker_" : kind == 1 ? "Runner_" : "Cyclist_") + avatar;
                if (kind != 2)
                {
                    float yfix = AlignBodyToTravel(agent, body, clipMap[clip]);
                    Debug.Log($"[EDGE] facing {agent.name} ({clip}): measured body yaw offset {-yfix:F1} deg, corrected");
                }
                foreach (var t in agent.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = false;
                agents.Add(agent); speeds.Add(speed); clipNames.Add(clip); natural.Add(nat);
            }

            // Two people sitting on existing benches, facing the path (bench +Z is its front).
            var seated = new List<Transform>();
            if (clipMap.ContainsKey("Sit"))
            {
                var benches = new List<Transform>();
                foreach (var g in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    if (g.name == "Furniture") foreach (Transform t in g.transform) if (t.name == "Bench") benches.Add(t);
                benches.Sort((a, b) => Mathf.Abs(a.position.z).CompareTo(Mathf.Abs(b.position.z))); // nearest the middle of the path
                string[] sitAv = { "Male_Adult_01", "Female_Adult_08" };
                for (int k = 0; k < Mathf.Min(2, benches.Count); k++)
                {
                    var bench = benches[k];
                    bool female = sitAv[k].StartsWith("Female");
                    var holder = new GameObject("Sitter_" + sitAv[k]).transform;
                    holder.SetParent(root.transform, false);
                    holder.SetPositionAndRotation(bench.TransformPoint(new Vector3(k == 0 ? -0.45f : 0.4f, 0f, 0f)), bench.rotation);
                    var body = SpawnPerson(holder, sitAv[k], female ? 1.65f : 1.78f, ac, matCache);
                    if (!body) continue;
                    body.GetComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
                    FixBenchSitter(holder, body); // seated phone pose (EdgeBenchSitters.cs); replaces the UAL Sit clip, which T-posed in edit mode
                    foreach (var t in holder.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = false;
                    seated.Add(holder);
                    Debug.Log($"[EDGE] Sitter {sitAv[k]} on bench at ({bench.position.x:F1}, {bench.position.z:F1})");
                }
            }

            ped.path = pts.ToArray();
            ped.agents = agents.ToArray();
            ped.speeds = speeds.ToArray();
            ped.directions = dirs;
            ped.clips = clipNames.ToArray();
            ped.clipNaturalSpeed = natural.ToArray();
            ped.seated = seated.ToArray();
            ped.cameras = new[] { fpv, tpv };
            ped.keepLeft = 0.45f;
            ped.shadowDistance = 20f;
            Debug.Log($"[EDGE] Pedestrians (Rocketbox + UAL Humanoid): {agents.Count} agents, cyclist {(cyclistOk ? "on bike" : "replaced by runner")}, {seated.Count} seated, keep-left 0.45 m, shadows within 20 m");
        }

        // Hip joint ~0.09 m above the 0.45 m bench seat, ~0.1 m in front of the backrest base (holder space).
        static readonly Vector3 SitterHips = new Vector3(0f, 0.45f + 0.09f, -0.06f);
        // Rider hips just above and slightly behind the saddle top (agent space).
        static Vector3 RiderHips(Vector3 saddle) => new Vector3(0f, saddle.y + 0.07f, saddle.z - 0.04f);

        // Re-seat the sitters and the cyclist and (re)rig the bike in the OPEN scene, without a rebuild.
        // Use after changing the pedestrian clips' import settings; leaves the scene dirty, does not save.
        [MenuItem("EDGE/Refresh Pedestrian Rigs")]
        public static void RefreshPedestrianRigs()
        {
            var ped = Object.FindFirstObjectByType<EdgePedestrians>();
            if (!ped) { Debug.LogWarning("[EDGE] Refresh Pedestrian Rigs: no EdgePedestrians in the open scene"); return; }
            s_variant = ped.gameObject.scene.path == SydneyScenePath ? SydneyVariant : ""; // "" for EdgeBushland (unchanged)
            try { RefreshPedestrianRigsInner(ped); } finally { s_variant = ""; }
        }

        static void RefreshPedestrianRigsInner(EdgePedestrians ped)
        {
            var ac = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(PedControllerPath);
            if (ac) EnableIKPass(ac);
            var clipSit = FindClip("Sitting_Idle_Loop"); var clipRide = FindClip("Driving_Loop") ?? clipSit;
            if (ped.seated != null && clipSit)
                foreach (var holder in ped.seated)
                {
                    var an = holder ? holder.GetComponentInChildren<Animator>() : null;
                    if (!an) continue;
                    Undo.RecordObject(an.transform, "Refresh Pedestrian Rigs");
                    FixBenchSitter(holder, an.gameObject);
                }
            foreach (var agent in ped.agents)
            {
                if (!agent || !agent.name.StartsWith("Cyclist_") || !clipRide) continue;
                var an = agent.GetComponentInChildren<Animator>();
                var bike = agent.Find("BikeRoot/Bicycle");
                if (!an || !bike) continue;
                BikeLandmarks(bike.gameObject, agent, out var saddle, out _);
                Undo.RecordObject(an.transform, "Refresh Pedestrian Rigs");
                SeatOn(agent, an.gameObject, clipRide, RiderHips(saddle));
                RigBike(agent, an.gameObject, bike.gameObject);
            }
            // Walkers/runners: re-measure facing from their clip and yaw the body onto the travel direction.
            var clipWalk = FindClip("Walk_Loop"); var clipJog = FindClip("Jog_Fwd_Loop");
            foreach (var agent in ped.agents)
            {
                if (!agent) continue;
                bool walker = agent.name.StartsWith("Walker_"), runner = agent.name.StartsWith("Runner_");
                if (!walker && !runner) continue;
                var an = agent.GetComponentInChildren<Animator>();
                if (!an) continue;
                Undo.RecordObject(an.transform, "Refresh Pedestrian Rigs");
                float yfix = AlignBodyToTravel(agent, an.gameObject, walker ? clipWalk : clipJog);
                Debug.Log($"[EDGE] facing {agent.name}: measured body yaw offset {-yfix:F1} deg, corrected");
            }
            EditorSceneManager.MarkSceneDirty(ped.gameObject.scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[EDGE] Pedestrian rigs refreshed (sitters and cyclist re-seated, bike rigged, walker/runner facing re-measured). Scene not saved.");
        }

        // Split the single-mesh bike into frame + two wheels (they are separate connected parts of the OBJ),
        // pivot each wheel on its hub, and give the rider an EdgeCyclist: rolling wheels, feet on a virtual
        // crank at the bottom bracket, hands on the grips. Safe to run again on an already rigged bike.
        static void RigBike(Transform agent, GameObject body, GameObject bike)
        {
            var mf = bike.GetComponentInChildren<MeshFilter>(true); // the imported OBJ mesh (wheels hang below it)
            if (!mf || !mf.sharedMesh) return;
            var mr = mf.GetComponent<MeshRenderer>();
            Transform front = mf.transform.Find("Wheel_Front"), rear = mf.transform.Find("Wheel_Rear");
            if (!front || !rear)
            {
                var src = mf.sharedMesh;
                var v = src.vertices; var nrm = src.normals; var uv = src.uv; var tri = src.triangles;
                // Connected parts: shared triangle indices + identical positions (the importer splits hard edges).
                var par = new int[v.Length];
                for (int i = 0; i < v.Length; i++) par[i] = i;
                int Find(int a) { while (par[a] != a) a = par[a] = par[par[a]]; return a; }
                void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) par[a] = b; }
                for (int t = 0; t < tri.Length; t += 3) { Union(tri[t], tri[t + 1]); Union(tri[t], tri[t + 2]); }
                var byPos = new Dictionary<Vector3, int>();
                for (int i = 0; i < v.Length; i++) { if (byPos.TryGetValue(v[i], out var j)) Union(i, j); else byPos[v[i]] = i; }
                var parts = new Dictionary<int, List<int>>();
                for (int t = 0; t < tri.Length; t += 3)
                {
                    int r = Find(tri[t]);
                    if (!parts.TryGetValue(r, out var l)) parts[r] = l = new List<int>();
                    l.Add(tri[t]); l.Add(tri[t + 1]); l.Add(tri[t + 2]);
                }
                var sorted = new List<List<int>>(parts.Values);
                sorted.Sort((a, b) => b.Count.CompareTo(a.Count));
                if (sorted.Count != 3) { Debug.LogWarning($"[EDGE] Bicycle has {sorted.Count} parts, expected frame + 2 wheels; wheels not rigged"); }
                else
                {
                    Mesh Part(List<int> tris, Vector3 offset, string file)
                    {
                        var map = new Dictionary<int, int>(); var vs = new List<Vector3>(); var ns = new List<Vector3>(); var us = new List<Vector2>(); var ts = new List<int>();
                        foreach (var i in tris)
                        {
                            if (!map.TryGetValue(i, out var k))
                            {
                                k = vs.Count; map[i] = k; vs.Add(v[i] - offset);
                                if (nrm.Length == v.Length) ns.Add(nrm[i]);
                                if (uv.Length == v.Length) us.Add(uv[i]);
                            }
                            ts.Add(k);
                        }
                        var m = new Mesh { name = file };
                        m.SetVertices(vs); m.SetTriangles(ts, 0);
                        if (ns.Count == vs.Count) m.SetNormals(ns); else m.RecalculateNormals();
                        if (us.Count == vs.Count) m.SetUVs(0, us);
                        m.RecalculateBounds();
                        SaveMesh(m, file);
                        return m;
                    }
                    Vector3 Centre(List<int> tris)
                    {
                        var b = new Bounds(v[tris[0]], Vector3.zero);
                        foreach (var i in tris) b.Encapsulate(v[i]);
                        return b.center;
                    }
                    Vector3 c1 = Centre(sorted[1]), c2 = Centre(sorted[2]);
                    bool oneIsFront = agent.InverseTransformPoint(mf.transform.TransformPoint(c1)).z > agent.InverseTransformPoint(mf.transform.TransformPoint(c2)).z;
                    Transform Wheel(string name, List<int> tris, Vector3 c)
                    {
                        var go = new GameObject(name);
                        Undo.RegisterCreatedObjectUndo(go, "Rig bike");
                        go.transform.SetParent(mf.transform, false);
                        go.transform.localPosition = c;
                        go.AddComponent<MeshFilter>().sharedMesh = Part(tris, c, "Edge_Bike_" + name);
                        var r = go.AddComponent<MeshRenderer>();
                        r.sharedMaterials = mr.sharedMaterials; r.shadowCastingMode = mr.shadowCastingMode;
                        return go.transform;
                    }
                    front = Wheel("Wheel_Front", oneIsFront ? sorted[1] : sorted[2], oneIsFront ? c1 : c2);
                    rear = Wheel("Wheel_Rear", oneIsFront ? sorted[2] : sorted[1], oneIsFront ? c2 : c1);
                    Undo.RecordObject(mf, "Rig bike");
                    mf.sharedMesh = Part(sorted[0], Vector3.zero, "Edge_Bike_Frame");
                }
            }

            // Landmarks in the agent frame, from the frame mesh and the wheel hubs.
            var frame = new List<Vector3>();
            foreach (var p in mf.sharedMesh.vertices) frame.Add(agent.InverseTransformPoint(mf.transform.TransformPoint(p)));
            float wheelR = 0.34f, zRear = -0.5f, zFront = 0.5f;
            if (front && rear)
            {
                zFront = agent.InverseTransformPoint(front.position).z; zRear = agent.InverseTransformPoint(rear.position).z;
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var p in front.GetComponent<MeshFilter>().sharedMesh.vertices) { float y = agent.InverseTransformPoint(front.TransformPoint(p)).y; lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y); }
                wheelR = 0.5f * (hi - lo);
            }
            // Bottom bracket = lowest centre-plane frame point between the hubs, clear of both wheels.
            Vector3 bb = new Vector3(0f, wheelR * 0.8f, Mathf.Lerp(zRear, zFront, 0.4f)); float bbY = float.MaxValue;
            float span = zFront - zRear;
            foreach (var p in frame)
                if (Mathf.Abs(p.x) < 0.1f && p.z > zRear + 0.2f * span && p.z < zFront - 0.3f * span && p.y < bbY) { bbY = p.y; bb = new Vector3(0f, p.y, p.z); }
            float crankR = Mathf.Clamp(bb.y - 0.06f, 0.08f, 0.17f); // pedals must clear the path
            // Grips = outermost points of the top of the front half (the handlebar ends), hands 5 cm inboard.
            float top = float.MinValue; foreach (var p in frame) top = Mathf.Max(top, p.y);
            Vector3 gl = new Vector3(-0.25f, top, zFront - 0.3f), gr = new Vector3(0.25f, top, zFront - 0.3f);
            float zMid = 0.5f * (zRear + zFront);
            foreach (var p in frame)
            {
                if (p.y < top - 0.12f || p.z < zMid) continue;
                if (p.x < gl.x) gl = p;
                if (p.x > gr.x) gr = p;
            }
            gl.x += 0.05f; gr.x -= 0.05f;

            var cy = body.GetComponent<EdgeCyclist>();
            if (!cy) cy = Undo.AddComponent<EdgeCyclist>(body);
            Undo.RecordObject(cy, "Rig bike");
            cy.agent = agent;
            cy.wheels = front && rear ? new[] { front, rear } : new Transform[0];
            cy.wheelRadius = wheelR;
            cy.crankCentre = bb;
            cy.crankRadius = crankR;
            cy.gripLeft = gl; cy.gripRight = gr;
            foreach (var t in bike.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = false;
            Debug.Log($"[EDGE] Bike rigged: wheel r {wheelR:F2} m, hubs z {zRear:F2}/{zFront:F2}, bottom bracket {bb}, crank {crankR:F2} m, grips {gl} / {gr}");
        }

        // Bike landmarks in the agent's local frame: saddle = top point of the half without the wide
        // handlebars; flip = handlebars are at -Z (bike would ride backwards).
        static void BikeLandmarks(GameObject bike, Transform agent, out Vector3 saddle, out bool flip)
        {
            var pts = new List<Vector3>();
            foreach (var mf in bike.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh) continue;
                foreach (var v in mf.sharedMesh.vertices) pts.Add(agent.InverseTransformPoint(mf.transform.TransformPoint(v)));
            }
            saddle = new Vector3(0f, 0.95f, -0.3f); flip = false;
            if (pts.Count == 0) return;
            float top = float.MinValue, zMin = float.MaxValue, zMax = float.MinValue;
            foreach (var p in pts) { top = Mathf.Max(top, p.y); zMin = Mathf.Min(zMin, p.z); zMax = Mathf.Max(zMax, p.z); }
            float zc = 0.5f * (zMin + zMax);
            float wideFront = 0f, wideBack = 0f;
            foreach (var p in pts) if (p.y > top - 0.15f) { if (p.z > zc) wideFront = Mathf.Max(wideFront, Mathf.Abs(p.x)); else wideBack = Mathf.Max(wideBack, Mathf.Abs(p.x)); }
            flip = wideBack > wideFront;
            float best = float.MinValue;
            foreach (var p in pts)
            {
                bool rear = flip ? p.z > zc : p.z < zc;
                if (rear && Mathf.Abs(p.x) < 0.1f && p.y > best) { best = p.y; saddle = p; }
            }
        }


        // ---------- grass (Poly Haven grass_medium_01, CC0) ----------
        // GPU-instanced via EdgeGrass (Graphics.RenderMeshInstanced): no per-clump GameObjects,
        // no shadows, alpha-clipped, 35 m draw distance with density falloff from 12 m.
        static void BuildGrass(List<Vector3> occupied, Camera fpv, Camera tpv)
        {
            var models = LoadPH(new[] { "grass_medium_01" });
            if (models.Count == 0) return;
            var m = models[0];
            var meshes = new List<Mesh>(); var fixes = new List<Matrix4x4>();
            var tmp = (GameObject)PrefabUtility.InstantiatePrefab(m.prefab);
            Material mat = null;
            foreach (var key in new[] { "mid_b", "mid_c", "tall_a", "tall_b", "tall_c" })
            {
                MeshRenderer r = null;
                foreach (var cand in tmp.GetComponentsInChildren<MeshRenderer>(true))
                    if (cand.name.Contains("_" + key + "_LOD0")) { r = cand; break; }
                if (!r) continue;
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                // Same base-origin rule: imported node rotation/scale, re-centred on its base,
                // flipped only if the base-to-centroid direction points down.
                var rot = r.transform.rotation;
                if (Vector3.Dot(rot * Vector3.Scale(mesh.bounds.center, r.transform.lossyScale), Vector3.up) < 0f)
                    rot = Quaternion.AngleAxis(180f, Vector3.right) * rot;
                meshes.Add(mesh); fixes.Add(Matrix4x4.TRS(Vector3.zero, rot, r.transform.lossyScale));
                if (!mat && r.sharedMaterial && m.mats.TryGetValue(r.sharedMaterial.name, out var hd)) mat = hd;
            }
            Object.DestroyImmediate(tmp);
            if (meshes.Count == 0 || !mat) { Debug.LogWarning("[EDGE] grass meshes/material not found"); return; }
            mat.enableInstancing = true;
            int nVerge = Mathf.Min(2, meshes.Count);

            var posYaw = new List<Vector4>(); var meshScale = new List<Vector2>();
            const float cell = 0.45f;
            for (float z = -HalfSize + 1f; z < HalfSize - 1f; z += cell)
            {
                float cx = PathX(z);
                for (float x = cx - 16f; x < cx + 16f; x += cell)
                {
                    float px = x + Random.Range(-0.2f, 0.2f), pz = z + Random.Range(-0.2f, 0.2f);
                    float pd = PathDist(px, pz);
                    if (pd < PathHalfWidth + 0.12f) continue;                        // none on the path
                    float clump = Mathf.PerlinNoise(px * 0.35f + 13f, pz * 0.35f + 71f);
                    float density;
                    if (pd < PathHalfWidth + 0.6f) density = 0.25f;                   // thin at the path edge
                    else if (pd < BushStart - 0.5f) density = 0.35f + 0.65f * clump;  // verge: clumpy
                    else if (pd < BushStart + 2f) density = 0.6f + 0.4f * clump;      // bush edge: dense
                    else density = 0.15f + 0.35f * clump;                             // under trees: sparse
                    if (Random.value > density) continue;
                    if (!Free(occupied, px, pz, 0.1f, true)) continue;
                    bool isVerge = pd < BushStart - 0.5f;
                    int mi = isVerge || meshes.Count <= nVerge ? Random.Range(0, nVerge) : Random.Range(nVerge, meshes.Count);
                    float sc = isVerge ? Random.Range(0.8f, 1.2f) : Random.Range(1.0f, 1.6f);
                    posYaw.Add(new Vector4(px, Height(px, pz) - 0.02f, pz, Random.Range(0f, 360f)));
                    meshScale.Add(new Vector2(mi, sc));
                }
            }
            var go = new GameObject("Grass");
            var g = go.AddComponent<EdgeGrass>();
            g.meshes = meshes.ToArray(); g.meshFix = fixes.ToArray(); g.material = mat;
            g.posYaw = posYaw.ToArray(); g.meshScale = meshScale.ToArray();
            g.cameras = new[] { fpv, tpv };
            long tris = 0; foreach (var mm in meshes) for (int i = 0; i < mm.subMeshCount; i++) tris += mm.GetSubMesh(i).indexCount / 3;
            Debug.Log($"[EDGE] Grass: {posYaw.Count} instanced clumps ({meshes.Count} clump meshes, avg {tris / meshes.Count} tris), " +
                      $"drawn within {g.drawDistance} m (full density to {g.fullDensityDistance} m), no shadows, no per-clump GameObjects.");
        }

        // ---------- terrain ----------

        // Gentle rolling displacement, flattened under the footpath.
        static float Height(float x, float z)
        {
            float h = (Mathf.PerlinNoise(x * 0.018f + 11f, z * 0.018f + 7f) - 0.5f) * 2.4f
                    + (Mathf.PerlinNoise(x * 0.07f + 3f, z * 0.07f + 19f) - 0.5f) * 0.55f
                    + (Mathf.PerlinNoise(x * 0.31f + 5f, z * 0.31f + 2f) - 0.5f) * 0.10f;
            float pathBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(PathHalfWidth + 0.5f, BushStart + 2f, PathDist(x, z)));
            float pathLevel = (Mathf.PerlinNoise(11f, z * 0.018f + 7f) - 0.5f) * 1.2f; // path follows a softer grade
            return Mathf.Lerp(pathLevel, h, pathBlend);
        }

        static float Moss(float x, float z) => Mathf.PerlinNoise(x * 0.045f + 40f, z * 0.045f + 90f);

        static GameObject BuildGround(Material floorMat, Material mossMat)
        {
            int n = GroundRes + 1;
            var verts = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            float step = HalfSize * 2f / GroundRes;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    float x = -HalfSize + i * step, z = -HalfSize + j * step;
                    verts[j * n + i] = new Vector3(x, Height(x, z), z);
                    uvs[j * n + i] = new Vector2(x, z); // metres; material sets tiling
                }
            var floorTris = new List<int>();
            var mossTris = new List<int>();
            for (int j = 0; j < GroundRes; j++)
                for (int i = 0; i < GroundRes; i++)
                {
                    int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                    float cx = -HalfSize + (i + 0.5f) * step, cz = -HalfSize + (j + 0.5f) * step;
                    // Verge (low ground cover) = submesh 0; leaf-littered bush floor beyond, ragged edge = submesh 1.
                    float edgeJitter = (Moss(cx, cz) - 0.5f) * 2.5f;
                    var list = PathDist(cx, cz) > BushStart + edgeJitter ? mossTris : floorTris;
                    list.Add(a); list.Add(c); list.Add(b);
                    list.Add(b); list.Add(c); list.Add(d);
                }
            var mesh = new Mesh { name = "Edge bushland ground", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(floorTris, 0);
            mesh.SetTriangles(mossTris, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            SaveMesh(mesh, "EdgeGround");

            var go = new GameObject("Ground", typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterials = new[] { floorMat, mossMat };
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.isStatic = true;
            return go;
        }

        static GameObject BuildPath(Material pathMat)
        {
            const int segZ = 420, segX = 6;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            for (int j = 0; j <= segZ; j++)
            {
                float z = -PathHalfLength + j * (PathHalfLength * 2f / segZ);
                for (int i = 0; i <= segX; i++)
                {
                    float t = i / (float)segX;
                    float x = PathX(z) + Mathf.Lerp(-PathHalfWidth, PathHalfWidth, t);
                    float edge = Mathf.Abs(t - 0.5f) * 2f; // edges tuck slightly into the soil
                    verts.Add(new Vector3(x, Height(x, z) + 0.035f - edge * edge * 0.03f, z));
                    uvs.Add(new Vector2(x, z));
                }
            }
            int w = segX + 1;
            for (int j = 0; j < segZ; j++)
                for (int i = 0; i < segX; i++)
                {
                    int a = j * w + i, b = a + 1, c = a + w, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            var mesh = new Mesh { name = "Edge footpath", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            SaveMesh(mesh, "EdgeFootpath");

            var go = new GameObject("Footpath", typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = pathMat;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.isStatic = true;
            return go;
        }

        // ---------- sky, light, post ----------

        static void BuildSky()
        {
            string profilePath = $"{Root}/Settings/{s_variant}EdgeNightProfile.asset";
            AssetDatabase.DeleteAsset(profilePath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);

            var ve = profile.Add<VisualEnvironment>(true);
            ve.skyType.Override((int)SkyType.PhysicallyBased);
            ve.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            var pbs = profile.Add<PhysicallyBasedSky>(true);
            pbs.spaceEmissionMultiplier.Override(1f);

            // EV100 for a ~0.4 lux moonlit scene: mid grey sits near EV -2;
            // -1.3 keeps it reading as night while the path stays legible.
            var exp = profile.Add<Exposure>(true);
            exp.mode.Override(ExposureMode.Fixed);
            exp.fixedExposure.Override(-0.5f); // 2026-10-22 was -1.3: gaps between lamps read dim, not black

            var fog = profile.Add<Fog>(true);
            fog.enabled.Override(true);
            fog.meanFreePath.Override(120f); // haze fades the far edge of the 210 m scene; camera far plane 400 m
            fog.baseHeight.Override(0f);
            fog.maximumHeight.Override(25f);
            fog.albedo.Override(new Color(0.55f, 0.60f, 0.70f));
            fog.enableVolumetricFog.Override(true);
            fog.quality.Override((int)ScalableSettingLevelParameter.Level.Low); // PERF: smaller froxel budget
            fog.anisotropy.Override(0.6f); // forward scattering gives lamp halos

            var shadows = profile.Add<HDShadowSettings>(true);
            shadows.maxShadowDistance.Override(40f);   // PERF: trees cast shadows only within 40 m (was 150, then 70)
            shadows.cascadeShadowSplitCount.Override(3);

            var contact = profile.Add<ContactShadows>(true);
            contact.enable.Override(true);
            contact.length.Override(0.25f);
            contact.opacity.Override(1f);

            var ssao = profile.Add<ScreenSpaceAmbientOcclusion>(true);
            ssao.quality.Override((int)ScalableSettingLevelParameter.Level.Low); // PERF
            ssao.intensity.Override(1.2f);
            ssao.radius.Override(1.5f);

            var ssr = profile.Add<ScreenSpaceReflection>(true);
            ssr.enabled.Override(false); // PERF: no water or wet surfaces worth reflecting

            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.25f);
            bloom.scatter.Override(0.7f);

            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);

            var vig = profile.Add<Vignette>(true);
            vig.intensity.Override(0.22f);

            // Volume components are sub-assets: without AddObjectToAsset they are
            // saved as {fileID: 0} and the Volume silently falls back to the HDRP
            // default profile (Automatic exposure -> night brightens near lamps).
            foreach (var c in profile.components)
                if (c && !AssetDatabase.Contains(c)) { c.hideFlags |= HideFlags.HideInInspector | HideFlags.HideInHierarchy; AssetDatabase.AddObjectToAsset(c, profile); }
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var skyGo = new GameObject("Sky and Fog");
            var vol = skyGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = profile;

            // Moonlight: a bright full moon is ~0.1-0.3 lux; 0.4 lux keeps the
            // scene readable. Cool tint approximates the Purkinje shift.
            var moonGo = new GameObject("Moonlight");
            var moon = moonGo.AddComponent<Light>();
            moon.type = LightType.Directional;
            moon.color = new Color(0.66f, 0.74f, 0.88f);
            moon.shadows = LightShadows.Soft;
            moonGo.transform.rotation = Quaternion.Euler(38f, -142f, 0f);
            var moonHd = moonGo.AddComponent<HDAdditionalLightData>();
            moon.lightUnit = LightUnit.Lux;
            moon.intensity = 0.4f;
            moonHd.angularDiameter = 0.52f;
            moonHd.SetShadowResolution(2048); // PERF: was 4096
            moonHd.useContactShadow.useOverride = true;
            moonHd.useContactShadow.@override = true;
        }

        // ---------- bat (MoonlitIsland sculpted meshes + fur/membrane) ----------

        static GameObject BuildBat(Material fur, Material membrane)
        {
            var bat = new GameObject("Bat");
            var model = new GameObject("Model").transform; // scaled mesh rig; the Bat root stays at scale 1
            model.SetParent(bat.transform, false);
            Mesh body = LoadMesh("Bat rounded body");
            Mesh ear = LoadMesh("Bat pointed ear");
            Mesh wing = LoadMesh("Bat curved scalloped wing");
            Mesh fingers = LoadMesh("Bat articulated wing fingers");
            Mesh tail = LoadMesh("Bat tail membrane");

            var b = Part("Body", model, body, fur, new Vector3(0f, 0f, -0.04f), new Vector3(0.18f, 0.16f, 0.43f));
            var head = Part("Head", model, body, fur, new Vector3(0f, 0.025f, 0.30f), new Vector3(0.19f, 0.17f, 0.18f));
            Part("EarL", model, ear, fur, new Vector3(-0.11f, 0.10f, 0.36f), Vector3.one);
            Part("EarR", model, ear, fur, new Vector3(0.11f, 0.10f, 0.36f), Vector3.one);
            Part("Tail", model, tail, membrane, Vector3.zero, Vector3.one);

            for (int side = -1; side <= 1; side += 2)
            {
                var w = new GameObject(side < 0 ? "WingL" : "WingR");
                w.transform.SetParent(model, false);
                w.transform.localPosition = new Vector3(side * 0.12f, 0f, 0.02f);
                var mirror = new Vector3(side, 1f, 1f);
                Part("Membrane", w.transform, wing, membrane, Vector3.zero, mirror);
                Part("Fingers", w.transform, fingers, fur, Vector3.zero, mirror);
            }

            // Measure the rig, then scale to N. gouldi: wingspan 0.30 m (uniform), body 6 cm long.
            Bounds all = WorldBounds(model.gameObject);
            float span = Mathf.Max(all.size.x, 1e-4f);
            model.localScale = Vector3.one * (BatWingspan / span);
            Bounds bodyB = WorldBounds(b);
            float bodyLen = Mathf.Max(bodyB.size.z, 1e-4f);
            var bs = b.transform.localScale;
            b.transform.localScale = new Vector3(bs.x, bs.y, bs.z * BatBodyLength / bodyLen);
            Debug.Log($"[EDGE] Bat rig measured span {span:F3} -> {BatWingspan} m, body {bodyLen:F3} -> {BatBodyLength} m");
            return bat;
        }

        static GameObject Part(string name, Transform parent, Mesh mesh, Material mat, Vector3 pos, Vector3 scale)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.On;
            return go;
        }

        // ---------- lamps ----------

        // Amber / low-pressure-sodium-like look (~590 nm, ~2200 K) for the emissive head.
        static Color AmberColor() => new Color(1.0f, 0.52f, 0.12f);

        static void BuildLamp(int id, Vector3 basePos, Material postMat, Material headMat,
                              Transform bat, DoseModel dose, UdpLink link)
        {
            var root = new GameObject($"Lamp_{id}");
            root.transform.position = basePos;
            float inward = basePos.x > PathX(basePos.z) ? -1f : 1f; // head hangs over the path

            var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            post.name = "Post";
            post.transform.SetParent(root.transform, false);
            post.transform.localScale = new Vector3(0.11f, LampHeight * 0.5f, 0.11f); // cylinder primitive is 2 m tall
            post.transform.localPosition = new Vector3(0f, LampHeight * 0.5f, 0f);
            Finish(post, postMat);

            var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arm.name = "Arm";
            arm.transform.SetParent(root.transform, false);
            arm.transform.localScale = new Vector3(1.0f, 0.06f, 0.06f);
            arm.transform.localPosition = new Vector3(inward * 0.5f, LampHeight, 0f);
            Finish(arm, postMat);

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(root.transform, false);
            head.transform.localScale = new Vector3(0.32f, 0.12f, 0.32f);
            head.transform.localPosition = new Vector3(inward * 1.0f, LampHeight - 0.05f, 0f); // over the path centreline
            Finish(head, headMat);
            head.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

            // Downward spot like a real path luminaire, warm 2200 K.
            var lightGo = new GameObject("LampLight");
            lightGo.transform.SetParent(root.transform, false);
            lightGo.transform.localPosition = head.transform.localPosition + new Vector3(0f, -0.12f, 0f);
            lightGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Spot;
            // Cone sized to light the path plus verge: ~12 m footprint from 4.4 m up (half-angle ~54 deg).
            l.spotAngle = 108f;
            l.innerSpotAngle = 60f;
            l.useColorTemperature = true;
            l.colorTemperature = 2200f;
            l.color = new Color(1.0f, 0.86f, 0.62f); // filter nudges toward the ~590 nm amber look
            l.range = 32f; // wider visual spill onto the path (render only)
            // PERF: only the two lamps nearest the bat's start cast (512) shadows; the rest none.
            l.shadows = id <= 1 ? LightShadows.Soft : LightShadows.None;
            var hd = lightGo.AddComponent<HDAdditionalLightData>();
            // Matched to the gameplay dose model: LampZone uses E = LampPhotLuxAtRef * (d_ref/d)^2,
            // i.e. a point source of I = 4 lux * (2 m)^2 = 16 cd. Same number drives the render.
            l.lightUnit = LightUnit.Candela;
            l.intensity = EdgeConfig.LampPhotLuxAtRef * EdgeConfig.LampRefDistanceM * EdgeConfig.LampRefDistanceM;
            l.intensity *= 2.5f; // 2026-10-22 render-only boost so the path reads safe for people; gameplay lux (EdgeConfig) unchanged
            l.shapeRadius = 0.08f;
            hd.SetShadowResolution(512);
            hd.useContactShadow.useOverride = true;
            hd.useContactShadow.@override = false; // PERF: contact shadows on the moon only
            hd.volumetricDimmer = 1f;

            var zone = root.AddComponent<LampZone>();
            zone.id = id;
            zone.bat = bat;
            zone.measureFrom = head.transform;
            zone.dose = dose;
            zone.link = link;
        }

        // ---------- vegetation ----------

        static void BuildTree(Transform parent, Vector3 pos, Mesh[] meshes, Material bark, Material a, Material b, float s)
        {
            var go = new GameObject("Tree", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.position = pos + Vector3.down * 0.15f;
            go.transform.rotation = Quaternion.Euler(Random.Range(-3f, 3f), Random.Range(0f, 360f), Random.Range(-3f, 3f));
            go.transform.localScale = new Vector3(s, s * Random.Range(0.85f, 1.35f), s);
            AssignVeg(go, meshes, bark, a, b);
            go.isStatic = true;
        }

        static void BuildBush(Transform parent, Vector3 pos, Mesh[] meshes, Material bark, Material a, Material b, float s)
        {
            // Squat, wide cedar crowns read as understorey shrubs.
            var go = new GameObject("Bush", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.position = pos + Vector3.down * 0.4f * s;
            go.transform.rotation = Quaternion.Euler(Random.Range(-6f, 6f), Random.Range(0f, 360f), Random.Range(-6f, 6f));
            go.transform.localScale = new Vector3(0.45f * s, 0.18f * s, 0.45f * s);
            AssignVeg(go, meshes, bark, a, b);
            go.isStatic = true;
        }

        static void AssignVeg(GameObject go, Mesh[] meshes, Material bark, Material a, Material b)
        {
            var mesh = meshes[Random.Range(0, meshes.Length)];
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mats = new Material[Mathf.Max(1, mesh.subMeshCount)];
            for (int i = 0; i < mats.Length; i++) mats[i] = i == 0 ? bark : (i % 2 == 1 ? a : b);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.On;
        }

        static Vector3 Scatter(float pathClearance)
        {
            float x, z;
            do
            {
                x = Random.Range(-95f, 95f);
                z = Random.Range(-95f, 95f);
            } while (Mathf.Abs(x) < pathClearance);
            return new Vector3(x, Height(x, z), z);
        }


        // Cheap dark backdrop: one jagged ribbon mesh per side ~28 m from the path, unlit-looking
        // MoonlitIsland "Far treeline silhouette" material. ~1.7k triangles total, no shadows.
        static void BuildFarTreeline()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{Moon}/Materials/Far treeline silhouette.mat");
            if (!mat) return;
            var verts = new List<Vector3>(); var tris = new List<int>();
            for (int side = -1; side <= 1; side += 2)
            {
                int start = verts.Count, cols = 0;
                for (float z = -HalfSize; z <= HalfSize; z += 2f, cols++)
                {
                    float x = PathX(z) + side * (28f + 4f * Mathf.PerlinNoise(z * 0.05f, side * 3f));
                    float top = 14f + 9f * Mathf.PerlinNoise(z * 0.11f + 7f, side) + 3f * Mathf.PerlinNoise(z * 0.6f, 2f);
                    float y = Height(x, z) - 0.5f;
                    verts.Add(new Vector3(x, y, z)); verts.Add(new Vector3(x, y + top, z));
                }
                for (int i = 0; i < cols - 1; i++)
                {
                    int a = start + i * 2;
                    tris.AddRange(new[] { a, a + 1, a + 2, a + 2, a + 1, a + 3 });
                    tris.AddRange(new[] { a, a + 2, a + 1, a + 2, a + 3, a + 1 }); // both faces
                }
            }
            var mesh = new Mesh { name = "Edge far treeline" };
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            SaveMesh(mesh, "EdgeFarTreeline");
            var go = new GameObject("Far Treeline", typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            go.isStatic = true;
        }

        // Console summary of what the GPU has to draw (full-detail LOD0 / first mesh LOD).
        static void LogSceneCost(params Transform[] groups)
        {
            long totalTris = 0; int objects = 0, renderers = 0, casters = 0;
            var perModel = new Dictionary<string, long>();
            foreach (var g in groups)
                foreach (Transform inst in g)
                {
                    objects++;
                    foreach (var r in inst.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(r.name, @"_LOD[1-9]$")) continue;
                        var mf = r.GetComponent<MeshFilter>();
                        if (!mf || !mf.sharedMesh) continue;
                        var m = mf.sharedMesh;
                        long t = 0;
                        for (int i = 0; i < m.subMeshCount; i++) t += m.GetSubMesh(i).indexCount / 3;
                        totalTris += t; renderers++;
                        if (r.shadowCastingMode != ShadowCastingMode.Off) casters++;
                        string key = inst.childCount > 0 ? inst.GetChild(0).name : inst.name;
                        perModel[key] = t;
                    }
                }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in perModel) sb.Append($"{kv.Key}={kv.Value / 1000}k ");
            Debug.Log($"[EDGE] Scene cost: {objects} placed objects, {renderers} renderers ({casters} shadow casters), " +
                      $"{totalTris / 1e6:F1} M triangles at full detail. Per model (LOD0): {sb}");
        }

        enum SizeAxis { Height, Length }

        static bool Free(List<Vector3> pts, float x, float z, float r, bool sumRadii)
        {
            foreach (var p in pts)
            {
                float min = sumRadii ? r + p.z : r;
                float dx = p.x - x, dz = p.y - z;
                if (dx * dx + dz * dz < min * min) return false;
            }
            return true;
        }

        // Instantiates a model, MEASURES its rendered bounds at unit scale, and scales it
        // uniformly so the chosen axis equals targetMetres. Bottom is then set on the
        // terrain and sunk by sinkMetres.
        static GameObject PlaceSized(Transform parent, string name, PHModel m, Vector3 groundPos,
                                     float targetMetres, SizeAxis axis, float tilt, float sinkMetres,
                                     float maxScale = float.MaxValue)
        {
            var go = PlacePH(parent, name, m, Vector3.zero, 1f, 0f); // wrapper at origin, identity
            Bounds b = WorldBounds(go);                               // measured, upright, unit scale
            float native = axis == SizeAxis.Height ? b.size.y : Mathf.Max(b.size.x, b.size.z);
            if (native < 1e-4f) native = 1f;
            float s = Mathf.Min(targetMetres / native, maxScale);
            float yaw = Random.Range(0f, 360f);
            go.transform.localScale = Vector3.one * s;
            go.transform.rotation = Quaternion.Euler(Random.Range(-tilt, tilt), yaw, Random.Range(-tilt, tilt));
            if (name == "Tree" && axis == SizeAxis.Height) CheckTrunkBase(go, m.id);
            // Anchor on the BASE (mesh origin = ground contact): its x,z goes on groundPos, and the lowest
            // vertex near the base (stem/root, not a drooping leaf) touches the terrain, sunk by sinkMetres.
            var br = BaseRenderer(go);
            Vector3 origin = br ? br.transform.position : WorldBounds(go).center;
            float baseY = BaseContactY(br, origin);
            go.transform.position += new Vector3(groundPos.x - origin.x, groundPos.y - baseY - sinkMetres, groundPos.z - origin.z);

            // PERF: small plants and rocks do not cast shadows; trees and logs do.
            bool big = name == "Tree";
            if (!big)
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true)) r.shadowCastingMode = ShadowCastingMode.Off;
            // PERF: cull by screen height (trees 1.5%, rocks 2%, shrubs/ferns 4.5%).
            float cull = big ? 0.015f : name == "Rock" ? 0.02f : 0.045f;
            AddCull(go, cull);
            return go;
        }

        // Lowest vertex within a small radius of the base origin (stem / root flare).
        static float BaseContactY(MeshRenderer r, Vector3 origin)
        {
            if (!r) return origin.y;
            var v = r.GetComponent<MeshFilter>().sharedMesh.vertices;
            float radius = Mathf.Max(0.05f, 0.12f * WorldBounds(r.gameObject).size.y), min = float.MaxValue;
            int step = Mathf.Max(1, v.Length / 40000);
            for (int i = 0; i < v.Length; i += step)
            {
                Vector3 w = r.transform.TransformPoint(v[i]);
                float dx = w.x - origin.x, dz = w.z - origin.z;
                if (dx * dx + dz * dz <= radius * radius) min = Mathf.Min(min, w.y);
            }
            return min < float.MaxValue ? min : origin.y;
        }

        static void AddCull(GameObject wrapper, float cullScreenHeight)
        {
            var existing = wrapper.GetComponentInChildren<LODGroup>(true);
            if (existing)
            {
                // Imported LOD chain (rocks, log): make sure the last level culls no later than cullScreenHeight.
                var lods = existing.GetLODs();
                if (lods.Length > 0 && lods[lods.Length - 1].screenRelativeTransitionHeight < cullScreenHeight)
                {
                    for (int i = 0; i < lods.Length; i++)
                        lods[i].screenRelativeTransitionHeight = Mathf.Max(lods[i].screenRelativeTransitionHeight,
                            cullScreenHeight * (lods.Length - i));
                    existing.SetLODs(lods);
                }
                return;
            }
            var lg = wrapper.AddComponent<LODGroup>();
            lg.SetLODs(new[] { new LOD(cullScreenHeight, wrapper.GetComponentsInChildren<Renderer>(true)) });
            lg.fadeMode = LODFadeMode.None;
            lg.RecalculateBounds();
        }

        static Bounds WorldBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<MeshRenderer>(true);
            Bounds b = default; bool any = false;
            foreach (var r in rs)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(r.name, @"_LOD[1-9]$")) continue; // measure LOD0 only
                var mb = mf.sharedMesh.bounds;
                var t = r.transform;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var w = t.TransformPoint(c);
                    if (!any) { b = new Bounds(w, Vector3.zero); any = true; } else b.Encapsulate(w);
                }
            }
            return b;
        }

        // ---------- Poly Haven models (CC0, see ThirdParty/PolyHaven/CREDITS.md) ----------

        const string PH = Root + "/ThirdParty/PolyHaven";
        static readonly string[] TreeIds = { "island_tree_02", "tree_small_02", "dead_tree_trunk_02" };
        static readonly string[] BushIds = { "fern_02", "shrub_02", "shrub_04" };
        static readonly string[] RockIds = { "boulder_01", "namaqualand_boulder_02", "rock_09" };

        sealed class PHModel
        {
            public string id;
            public GameObject prefab;
            public Dictionary<string, Material> mats = new Dictionary<string, Material>();
            public List<string> variants = new List<string>();
            public Dictionary<string, Material>[] tints; // plants only: 3 subtle colour variants
        }

        static readonly System.Text.RegularExpressions.Regex LodSuffix =
            new System.Text.RegularExpressions.Regex(@"_LOD\d+$");

        static List<PHModel> LoadPH(string[] ids)
        {
            var list = new List<PHModel>();
            foreach (var id in ids)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PH}/Models/{id}/{id}_2k.fbx");
                if (!prefab) { Debug.LogWarning($"[EDGE] Poly Haven model missing: {id}"); continue; }
                var m = new PHModel { id = id, prefab = prefab };
                foreach (Transform c in prefab.transform)
                {
                    string v = LodSuffix.Replace(c.name, "");
                    if (!m.variants.Contains(v)) m.variants.Add(v);
                }
                // One HDRP/Lit material per FBX material slot, keyed by the slot name.
                foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                    foreach (var src in r.sharedMaterials)
                        if (src && !m.mats.ContainsKey(src.name))
                            m.mats[src.name] = PHModelMat(id, src.name);
                if (id.StartsWith("shrub") || id.StartsWith("fern"))
                {
                    m.tints = new Dictionary<string, Material>[PlantTints.Length];
                    for (int k = 0; k < PlantTints.Length; k++)
                    {
                        m.tints[k] = new Dictionary<string, Material>();
                        foreach (var kv in m.mats) m.tints[k][kv.Key] = PHModelMat(id, kv.Key, k, PlantTints[k]);
                    }
                }
                list.Add(m);
            }
            return list;
        }

        // ROOT CAUSE of the sideways trees: for single-node FBX files (island_tree_02, tree_small_02,
        // dead_tree_trunk_02, namaqualand_boulder_02) Unity makes the mesh node itself the prefab root,
        // and that node carries Blender's -90 deg X (Z-up -> Y-up) rotation. The old code overwrote the
        // root's rotation with a small random tilt, throwing the -90 away. Now every instance is
        // parented under a clean wrapper; placement transforms go on the wrapper only, and the
        // imported node transforms are never touched.
        static GameObject PlacePH(Transform parent, string name, PHModel m, Vector3 pos, float s, float tilt)
        {
            int tint = m.tints != null ? Random.Range(0, m.tints.Length) : -1;
            var mats0 = tint >= 0 ? m.tints[tint] : m.mats;
            var wrapper = new GameObject(name);
            wrapper.transform.SetParent(parent, false);

            var go = (GameObject)PrefabUtility.InstantiatePrefab(m.prefab);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = m.id;
            go.transform.SetParent(wrapper.transform, false); // keeps the imported local rotation/scale

            // Multi-variant files (fern a-d, shrub a-d): keep one variant, centred.
            if (m.variants.Count > 1)
            {
                string keep = m.variants[Random.Range(0, m.variants.Count)];
                for (int i = go.transform.childCount - 1; i >= 0; i--)
                {
                    var c = go.transform.GetChild(i);
                    if (LodSuffix.Replace(c.name, "") != keep) Object.DestroyImmediate(c.gameObject);
                    else c.localPosition = new Vector3(0f, c.localPosition.y, 0f);
                }
                var lod = go.GetComponent<LODGroup>();
                if (lod) lod.RecalculateBounds();
            }

            OrientByBase(go, m.id);

            wrapper.transform.position = pos;
            wrapper.transform.rotation = Quaternion.Euler(Random.Range(-tilt, tilt), Random.Range(0f, 360f), Random.Range(-tilt, tilt));
            wrapper.transform.localScale = Vector3.one * s;

            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] && mats0.TryGetValue(mats[i].name, out var hd)) mats[i] = hd;
                r.sharedMaterials = mats;
                r.shadowCastingMode = ShadowCastingMode.On;
            }
            foreach (var c in wrapper.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (Transform t in wrapper.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;
            return wrapper;
        }

        // ORIENTATION RULE (verified from the raw FBX vertex data of all 9 Poly Haven files, every
        // sub-mesh / variant): each mesh's origin is its ground contact (Blender Z=0) and the geometry
        // grows AWAY from it - e.g. shrub_02_c along up -0.014 .. 1.055 m, island_tree_02 0.007 .. 3.355 m.
        // So "up" is the direction from the mesh origin to its vertex centroid. If that points below the
        // horizon after import, flip 180 deg about X around the base. No shape heuristics, no stacked fixes.
        static void OrientByBase(GameObject inst, string id)
        {
            var r = BaseRenderer(inst);
            if (!r) return;
            Vector3 origin = r.transform.position;
            Vector3 up = (Centroid(r) - origin).normalized;
            bool flipped = false;
            if (Vector3.Dot(up, Vector3.up) < 0f)
            {
                inst.transform.RotateAround(origin, Vector3.right, 180f);
                flipped = true;
            }
            string variant = LodSuffix.Replace(r.name, "");
            if (s_warned.Add(id + "/" + variant + "_orient"))
                Debug.Log($"[EDGE] {id}/{variant}: base-to-centroid up {up:F2}" + (flipped ? " -> FLIPPED 180 about X" : " (upright as imported)") +
                          $", base offset in file {(origin - inst.transform.parent.position):F2} m (re-centred on its base)");
        }

        static MeshRenderer BaseRenderer(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf && mf.sharedMesh && !System.Text.RegularExpressions.Regex.IsMatch(r.name, @"_LOD[1-9]$")) return r;
            }
            return null;
        }

        static Vector3 Centroid(MeshRenderer r)
        {
            var v = r.GetComponent<MeshFilter>().sharedMesh.vertices;
            Vector3 sum = Vector3.zero; int n = 0, step = Mathf.Max(1, v.Length / 20000);
            for (int i = 0; i < v.Length; i += step) { sum += v[i]; n++; }
            return r.transform.TransformPoint(n > 0 ? sum / n : Vector3.zero);
        }

        static void LogOnce(string id, string msg)
        {
            if (s_warned.Add(id + "_orient")) Debug.Log(msg);
        }

        static List<Vector3> SamplePoints(GameObject go)
        {
            var pts = new List<Vector3>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh || System.Text.RegularExpressions.Regex.IsMatch(mf.name, @"_LOD[1-9]$")) continue;
                var v = mf.sharedMesh.vertices;
                int step = Mathf.Max(1, v.Length / 20000);
                for (int i = 0; i < v.Length; i += step) pts.Add(mf.transform.TransformPoint(v[i]));
            }
            return pts;
        }

        static Bounds BoundsOf(List<Vector3> pts)
        {
            var b = new Bounds(pts[0], Vector3.zero);
            foreach (var p in pts) b.Encapsulate(p);
            return b;
        }

        // Spread (largest extent perpendicular to 'up') of the top 10% minus the bottom 10% along 'up'.
        static float TaperScore(List<Vector3> pts, Vector3 up)
        {
            Vector3 u = up.normalized;
            Vector3 a = Vector3.Cross(u, Mathf.Abs(u.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 c = Vector3.Cross(u, a);
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in pts) { float h = Vector3.Dot(p, u); min = Mathf.Min(min, h); max = Mathf.Max(max, h); }
            float lo = min + (max - min) * 0.1f, hi = max - (max - min) * 0.1f;
            return Spread(pts, u, a, c, hi, float.MaxValue) - Spread(pts, u, a, c, float.MinValue, lo);
        }

        static float Spread(List<Vector3> pts, Vector3 u, Vector3 a, Vector3 c, float hMin, float hMax)
        {
            float a0 = float.MaxValue, a1 = float.MinValue, c0 = float.MaxValue, c1 = float.MinValue; bool any = false;
            foreach (var p in pts)
            {
                float h = Vector3.Dot(p, u);
                if (h < hMin || h > hMax) continue;
                any = true;
                float pa = Vector3.Dot(p, a), pc = Vector3.Dot(p, c);
                a0 = Mathf.Min(a0, pa); a1 = Mathf.Max(a1, pa); c0 = Mathf.Min(c0, pc); c1 = Mathf.Max(c1, pc);
            }
            return any ? Mathf.Max(a1 - a0, c1 - c0) : 0f;
        }
        static readonly HashSet<string> s_warned = new HashSet<string>();

        // For trees: the trunk base must be the lowest part. The vertices in the bottom 5% of the
        // height should form a narrow cluster (the trunk), not span the crown. Logs a warning otherwise.
        static void CheckTrunkBase(GameObject go, string id)
        {
            var pts = new List<Vector3>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh || System.Text.RegularExpressions.Regex.IsMatch(mf.name, @"_LOD[1-9]$")) continue;
                var v = mf.sharedMesh.vertices;
                for (int i = 0; i < v.Length; i += 7) pts.Add(mf.transform.TransformPoint(v[i]));
            }
            if (pts.Count == 0) return;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in pts) { minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y); }
            float cut = minY + (maxY - minY) * 0.05f;
            Bounds all = new Bounds(pts[0], Vector3.zero), low = default; bool any = false;
            foreach (var p in pts)
            {
                all.Encapsulate(p);
                if (p.y <= cut) { if (!any) { low = new Bounds(p, Vector3.zero); any = true; } else low.Encapsulate(p); }
            }
            float crown = Mathf.Max(all.size.x, all.size.z), baseW = Mathf.Max(low.size.x, low.size.z);
            if (!s_warned.Contains(id + "_base"))
            {
                s_warned.Add(id + "_base");
                if (baseW > 0.6f * crown)
                    Debug.LogWarning($"[EDGE] {id}: bottom 5% spans {baseW:F2} m of {crown:F2} m crown - trunk base is NOT at the bottom (model may be on its side).");
                else
                    Debug.Log($"[EDGE] {id}: upright OK - height {all.size.y:F2} m, base cluster {baseW:F2} m vs crown {crown:F2} m.");
            }
        }

        // Texture prefix for an FBX material slot, e.g. "island_tree_02_leaves" or "tree_small_02_branch".
        static string PHTexPrefix(string id, string slot)
        {
            string dir = $"{PH}/Models/{id}/textures/";
            foreach (var cand in new[] { slot, slot.Replace("_branches", "_branch"), slot.Replace("_trunk", ""), id })
                if (File.Exists($"{dir}{cand}_diff_2k.jpg")) return dir + cand;
            return dir + id;
        }

        // Cheap per-instance colour variation: 3 tinted copies of each plant material (keeps instancing).
        static readonly Color[] PlantTints = { new Color(1f, 1f, 1f), new Color(0.88f, 0.98f, 0.82f), new Color(1.04f, 0.94f, 0.78f) };

        static Material PHModelMat(string id, string slot) => PHModelMat(id, slot, -1, Color.white);

        static Material PHModelMat(string id, string slot, int tintIndex, Color tint)
        {
            var m = GetOrCreate("Edge_PH_" + slot + (tintIndex >= 0 ? "_t" + tintIndex : ""));
            string pre = PHTexPrefix(id, slot);
            bool foliage = File.Exists(pre + "_diffalpha_2k.png");
            var baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>(foliage ? pre + "_diffalpha_2k.png" : pre + "_diff_2k.jpg");
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(pre + "_nor_gl_2k.jpg");
            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(pre + "_hdrpmask_2k.png");
            m.SetColor("_BaseColor", tint);
            m.SetTexture("_BaseColorMap", baseTex);
            m.SetTextureScale("_BaseColorMap", Vector2.one);
            m.SetTexture("_NormalMap", nrm);
            m.SetFloat("_NormalScale", 1f);
            SetMask(m, mask, 0f, 1f);
            if (foliage)
            {
                m.SetFloat("_AlphaCutoffEnable", 1f);
                m.SetFloat("_AlphaCutoff", 0.45f);
                m.SetFloat("_AlphaCutoffShadow", 0.45f);
                m.SetFloat("_DoubleSidedEnable", 1f);
                m.SetFloat("_DoubleSidedNormalMode", 0f); // flip normals on back faces
                m.SetFloat("_CullMode", 0f);
                m.SetFloat("_CullModeForward", 0f);
            }
            m.enableInstancing = true;
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material PHGroundMat(string name, string id, string res, float tilesPerMetre)
        {
            string dir = $"{PH}/Textures/{id}/";
            string diffPath = File.Exists($"{dir}{id}_diff_{res}.jpg") ? $"{dir}{id}_diff_{res}.jpg" : $"{dir}{id}_diffuse_{res}.jpg";
            var diff = AssetDatabase.LoadAssetAtPath<Texture2D>(diffPath);
            if (!diff) return null;
            var m = GetOrCreate(name);
            m.SetColor("_BaseColor", Color.white);
            m.SetTexture("_BaseColorMap", diff);
            m.SetTextureScale("_BaseColorMap", Vector2.one * tilesPerMetre);
            m.SetTexture("_NormalMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}{id}_nor_gl_{res}.jpg"));
            m.SetFloat("_NormalScale", 1.3f);
            SetMask(m, AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}{id}_hdrpmask_{res}.png"), 0f, 1f);
            m.enableInstancing = true;
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // HDRP mask map generated from Poly Haven ARM: R metal, G AO, B 0, A smoothness (1 - rough).
        static void SetMask(Material m, Texture2D mask, float smMin, float smMax)
        {
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", 0.3f);
            if (!mask) return;
            m.SetTexture("_MaskMap", mask);
            m.SetFloat("_MetallicRemapMin", 0f);
            m.SetFloat("_MetallicRemapMax", 1f);
            m.SetFloat("_AORemapMin", 0f);
            m.SetFloat("_AORemapMax", 1f);
            m.SetFloat("_SmoothnessRemapMin", smMin);
            m.SetFloat("_SmoothnessRemapMax", smMax);
        }

        // ---------- assets ----------

        static Material LoadMat(string name)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>($"{Moon}/Materials/{name}.mat");
            if (!m) throw new System.InvalidOperationException("Missing MoonlitIsland material: " + name);
            return m;
        }

        static Mesh[] LoadMeshes(string namePrefix)
        {
            var byName = new SortedDictionary<string, Mesh>();
            foreach (var guid in AssetDatabase.FindAssets("t:Mesh", new[] { Moon + "/Meshes" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string file = System.IO.Path.GetFileNameWithoutExtension(path);
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                bool match = mesh && (file.Contains(namePrefix) || mesh.name.Contains(namePrefix));
                if (match && !byName.ContainsKey(file))
                    byName[file] = mesh;
            }
            if (byName.Count == 0) throw new System.InvalidOperationException("No meshes named " + namePrefix);
            return new List<Mesh>(byName.Values).ToArray();
        }

        static Mesh LoadMesh(string name) => LoadMeshes(name)[0];

        static void SaveMesh(Mesh mesh, string file)
        {
            string path = $"{Root}/Meshes/{s_variant}{file}.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
        }

        static void Finish(GameObject go, Material mat)
        {
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            var col = go.GetComponent<Collider>();
            if (col) Object.DestroyImmediate(col);
        }

        static Material GetOrCreate(string name)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m) { m = new Material(Shader.Find("HDRP/Lit")); AssetDatabase.CreateAsset(m, path); }
            else m.shader = Shader.Find("HDRP/Lit");
            return m;
        }

        // HDRP/Lit with scanned Color + Normal, AO packed via the mask map:
        // the AO jpg goes in _MaskMap (G = AO); metallic is remapped to 0 and
        // smoothness to a fixed low range so the greyscale R/A channels are ignored.
        static Material ScanMat(string name, string folder, Color tint, float smoothness, float tilesPerMetre)
        {
            var m = GetOrCreate(name);
            string prefix = $"{Moon}/ScanTextures/{folder}/{folder}";
            var color = AssetDatabase.LoadAssetAtPath<Texture2D>(prefix + "_Color.jpg");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(prefix + "_Normal.jpg");
            var ao = AssetDatabase.LoadAssetAtPath<Texture2D>(prefix + "_AO.jpg");
            if (!color || !normal) throw new System.InvalidOperationException("Missing scan textures: " + folder);
            var tiling = Vector2.one * tilesPerMetre;
            m.SetColor("_BaseColor", tint);
            m.SetTexture("_BaseColorMap", color);
            m.SetTextureScale("_BaseColorMap", tiling);
            m.SetTexture("_NormalMap", normal);
            m.SetFloat("_NormalScale", 1.2f);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", smoothness);
            if (ao)
            {
                m.SetTexture("_MaskMap", ao);
                m.SetFloat("_MetallicRemapMin", 0f);
                m.SetFloat("_MetallicRemapMax", 0f);
                m.SetFloat("_AORemapMin", 0f);
                m.SetFloat("_AORemapMax", 1f);
                m.SetFloat("_SmoothnessRemapMin", smoothness * 0.6f);
                m.SetFloat("_SmoothnessRemapMax", smoothness);
            }
            m.enableInstancing = true;
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material LitMat(string name, Color baseColor, float smoothness, float metallic)
        {
            var m = GetOrCreate(name);
            m.SetColor("_BaseColor", baseColor);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material EmissiveMat(string name, Color color, float nits)
        {
            var m = GetOrCreate(name);
            m.SetColor("_BaseColor", color * 0.2f);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissiveColor", color * nits); // HDR, in nits at EV0
            m.SetColor("_EmissiveColorLDR", color);
            m.SetFloat("_EmissiveIntensity", nits);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
