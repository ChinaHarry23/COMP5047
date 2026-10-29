using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Edge.EditorTools
{
    // Adds cheap primitive colliders to an EDGE scene so the bat cannot fly through things.
    // Idempotent: every collider lives on a child named "EdgeColliders" (layer EdgeObstacle) that is
    // rebuilt on each run; nothing existing is moved, renamed or deleted (only our own children).
    //   Trees     capsule on the trunk (fitted to the lowest LOD's vertices) + trigger crown sphere (EdgeFoliage)
    //   Lamps     capsule on the post, boxes on arm and head
    //   Benches, signposts, fountain, rocks, sandstone ledge: fitted box
    //   Tall bushes (>1.5 m): trigger foliage box; low bushes, ferns, grass, pods: none
    //   Pedestrians (walkers/runners/cyclists): capsule + kinematic Rigidbody. Sitters: none (bench box covers them)
    //   Ground / footpath: existing MeshColliders are kept (checked and reported)
    public static class EdgeColliderPass
    {
        public const string LayerName = "EdgeObstacle";
        const string ChildName = "EdgeColliders";

        [MenuItem("EDGE/Physics/Add Colliders To Open Scene")]
        static void Menu()
        {
            var counts = Apply();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[EDGE] Colliders: " + counts + "  — save the scene to keep them.");
        }

        [MenuItem("EDGE/Physics/Remove EDGE Colliders From Open Scene")]
        static void RemoveMenu()
        {
            int n = 0;
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
                if (t && t.name == ChildName) { Undo.DestroyObjectImmediate(t.gameObject); n++; }
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[EDGE] Removed {n} EdgeColliders children (nothing else touched).");
        }

        public class Counts
        {
            public int trees, crowns, lamps, boxes, foliage, peds, groundOk, skipped;
            public override string ToString() =>
                $"{trees} tree trunks, {crowns} crown triggers, {lamps} lamps, {boxes} boxes (benches/rocks/props), " +
                $"{foliage} shrub triggers, {peds} pedestrians, ground/path mesh colliders present: {groundOk}, skipped {skipped}. " +
                $"Total ≈ {trees + crowns + lamps * 3 + boxes + foliage + peds} primitive colliders.";
        }

        public static int EnsureLayer()
        {
            int l = LayerMask.NameToLayer(LayerName);
            if (l >= 0) return l;
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0) return 0;
            var so = new SerializedObject(assets[0]);
            var layers = so.FindProperty("layers");
            for (int i = 8; i < 32 && layers != null && i < layers.arraySize; i++)
            {
                var p = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(p.stringValue))
                {
                    p.stringValue = LayerName;
                    so.ApplyModifiedProperties();
                    Debug.Log($"[EDGE] Added layer '{LayerName}' at index {i}.");
                    return i;
                }
            }
            Debug.LogWarning($"[EDGE] No free user layer for '{LayerName}'; colliders stay on Default.");
            return 0;
        }

        public static Counts Apply()
        {
            int layer = EnsureLayer();
            var c = new Counts();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
            {
                if (!t || t.name == ChildName) continue;
                string n = t.name;
                string parent = t.parent ? t.parent.name : "";
                if (n.StartsWith("Lamp_") && t.GetComponent<LampZone>()) { Lamp(t, layer); c.lamps++; }
                else if (n == "Tree" && (parent == "Trees")) { if (Tree(t, layer, out bool crown)) { c.trees++; if (crown) c.crowns++; } else c.skipped++; }
                else if ((n == "Bench" || n == "Signpost" || n == "DrinkingFountain") && parent == "Furniture") { Box(t, layer, 1f, false); c.boxes++; }
                else if ((n == "Rock" || n == "SandstoneLedge") && (parent == "Rocks" || parent == "SandstoneOutcrops")) { Box(t, layer, 0.85f, false); c.boxes++; }
                else if (n == "Bush" && parent == "Bushes")
                {
                    var b = LocalBounds(t, out _);
                    if (b.size.y * t.lossyScale.y > 1.5f) { Box(t, layer, 0.8f, true); c.foliage++; }
                    else Clear(t);
                }
                else if (parent == "Pedestrians" && (n.StartsWith("Walker_") || n.StartsWith("Runner_") || n.StartsWith("Cyclist_"))) { Ped(t, layer); c.peds++; }
                else if ((n == "Ground" || n == "Footpath") && t.GetComponent<MeshCollider>()) c.groundOk++;
            }
            return c;
        }

        // ---- helpers -----------------------------------------------------------
        static void Clear(Transform t)
        {
            var old = t.Find(ChildName);
            if (old) Object.DestroyImmediate(old.gameObject);
        }

        static Transform NewChild(Transform t, int layer)
        {
            Clear(t);
            var go = new GameObject(ChildName) { layer = layer, isStatic = t.gameObject.isStatic };
            go.transform.SetParent(t, false);
            return go.transform;
        }

        // bounds of all meshes under root, in root-local space
        static Bounds LocalBounds(Transform root, out bool any)
        {
            bool found = false;
            var b = new Bounds();
            void Add(Mesh m, Transform mt)
            {
                if (!m) return;
                var mb = m.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = root.InverseTransformPoint(mt.TransformPoint(corner));
                    if (!found) { b = new Bounds(p, Vector3.zero); found = true; } else b.Encapsulate(p);
                }
            }
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                if (mf.transform.name != ChildName) Add(mf.sharedMesh, mf.transform);
            foreach (var sm in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Add(sm.sharedMesh, sm.transform);
            any = found;
            return b;
        }

        static void Box(Transform t, int layer, float shrinkXZ, bool foliage)
        {
            var b = LocalBounds(t, out bool any);
            if (!any) return;
            var ch = NewChild(t, layer);
            var box = ch.gameObject.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = new Vector3(b.size.x * shrinkXZ, b.size.y, b.size.z * shrinkXZ);
            if (foliage) { box.isTrigger = true; ch.gameObject.AddComponent<EdgeFoliage>(); }
        }

        static void Lamp(Transform t, int layer)
        {
            var ch = NewChild(t, layer);
            var post = t.Find("Post"); var arm = t.Find("Arm"); var head = t.Find("Head");
            if (post)
            {
                var cap = ch.gameObject.AddComponent<CapsuleCollider>();
                cap.direction = 1;
                cap.center = t.InverseTransformPoint(post.position);
                cap.height = post.lossyScale.y * 2f / Mathf.Max(1e-4f, t.lossyScale.y);
                cap.radius = Mathf.Max(0.06f, post.lossyScale.x * 0.5f) / Mathf.Max(1e-4f, t.lossyScale.x);
            }
            foreach (var part in new[] { arm, head })
            {
                if (!part) continue;
                var box = ch.gameObject.AddComponent<BoxCollider>();
                box.center = t.InverseTransformPoint(part.position);
                var ls = part.lossyScale; var ps = t.lossyScale;
                // arm/head are not rotated relative to the lamp root in the builder
                box.size = new Vector3(ls.x / ps.x, Mathf.Max(ls.y, 0.08f) / ps.y, ls.z / ps.z);
            }
        }

        static bool Tree(Transform t, int layer, out bool crown)
        {
            crown = false;
            // vertices of the lowest-detail LOD (fallback: all meshes), in tree-local space
            var filters = new List<MeshFilter>();
            var lod = t.GetComponentInChildren<LODGroup>();
            if (lod && lod.lodCount > 0)
            {
                var lods = lod.GetLODs();
                foreach (var r in lods[lods.Length - 1].renderers)
                    if (r && r.TryGetComponent(out MeshFilter mf)) filters.Add(mf);
            }
            if (filters.Count == 0) filters.AddRange(t.GetComponentsInChildren<MeshFilter>(true));
            var pts = new List<Vector3>();
            foreach (var mf in filters)
            {
                var m = mf.sharedMesh;
                if (!m || mf.transform.name == ChildName) continue;
                if (!m.isReadable) { var b0 = m.bounds; pts.Add(t.InverseTransformPoint(mf.transform.TransformPoint(b0.min))); pts.Add(t.InverseTransformPoint(mf.transform.TransformPoint(b0.max))); continue; }
                var v = m.vertices;
                int step = Mathf.Max(1, v.Length / 15000);
                for (int i = 0; i < v.Length; i += step) pts.Add(t.InverseTransformPoint(mf.transform.TransformPoint(v[i])));
            }
            if (pts.Count < 8) return false;

            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in pts) { minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y); }
            float H = maxY - minY;
            if (H < 1e-3f) return false;
            float sXZ = Mathf.Max(1e-4f, (Mathf.Abs(t.lossyScale.x) + Mathf.Abs(t.lossyScale.z)) * 0.5f);
            float sY = Mathf.Max(1e-4f, Mathf.Abs(t.lossyScale.y));

            // trunk: slice 5-15% height; centroid + 60th percentile radius
            Vector2 centre = Vector2.zero; int nb = 0;
            var band = new List<Vector2>();
            foreach (var p in pts) if (p.y > minY + 0.05f * H && p.y < minY + 0.15f * H) { band.Add(new Vector2(p.x, p.z)); centre += new Vector2(p.x, p.z); nb++; }
            if (nb < 3) { centre = Vector2.zero; foreach (var p in pts) centre += new Vector2(p.x, p.z); centre /= pts.Count; }
            else centre /= nb;
            float trunkR = Percentile(band, centre, 0.6f);
            // world clamp 0.08-0.6 m
            trunkR = Mathf.Clamp(trunkR * sXZ, 0.08f, 0.6f) / sXZ;
            if (trunkR <= 0f) trunkR = 0.15f / sXZ;

            // crown base: first 5% slice whose 90th percentile spread > 4x trunk radius
            float crownBase = minY + 0.45f * H;
            for (float f = 0.15f; f < 0.8f; f += 0.05f)
            {
                var sl = new List<Vector2>();
                foreach (var p in pts) if (p.y > minY + f * H && p.y < minY + (f + 0.05f) * H) sl.Add(new Vector2(p.x, p.z));
                if (sl.Count >= 3 && Percentile(sl, centre, 0.9f) > 4f * trunkR) { crownBase = minY + f * H; break; }
            }
            crownBase = Mathf.Clamp(crownBase, minY + 0.25f * H, minY + 0.8f * H);

            var ch = NewChild(t, layer);
            var cap = ch.gameObject.AddComponent<CapsuleCollider>();
            cap.direction = 1;
            float top = Mathf.Min(maxY, crownBase + 0.1f * H);
            cap.radius = trunkR;
            cap.height = Mathf.Max(top - minY, 2f * trunkR * sXZ / sY);
            cap.center = new Vector3(centre.x, minY + cap.height * 0.5f, centre.y);

            // crown: trigger sphere (soft foliage), on its own child so the trunk stays solid
            float crownH = maxY - crownBase;
            var crownPts = new List<Vector2>();
            foreach (var p in pts) if (p.y > crownBase) crownPts.Add(new Vector2(p.x, p.z));
            if (crownH > 0.5f / sY && crownPts.Count > 8)
            {
                float spread = Percentile(crownPts, centre, 0.8f) * sXZ;
                float r = Mathf.Max(0.5f, 0.8f * Mathf.Min(spread, crownH * sY * 0.5f));
                var cg = new GameObject("Crown") { layer = layer };
                cg.transform.SetParent(ch, false);
                var sph = cg.AddComponent<SphereCollider>();
                sph.isTrigger = true;
                sph.radius = r / Mathf.Max(sXZ, sY);
                sph.center = new Vector3(centre.x, crownBase + crownH * 0.5f, centre.y);
                cg.AddComponent<EdgeFoliage>();
                crown = true;
            }
            return true;
        }

        static float Percentile(List<Vector2> pts, Vector2 c, float q)
        {
            if (pts.Count == 0) return 0f;
            var d = new float[pts.Count];
            for (int i = 0; i < pts.Count; i++) d[i] = (pts[i] - c).magnitude;
            System.Array.Sort(d);
            return d[Mathf.Clamp((int)(q * (d.Length - 1)), 0, d.Length - 1)];
        }

        static void Ped(Transform t, int layer)
        {
            var b = LocalBounds(t, out bool any);
            var ch = NewChild(t, layer);
            ch.gameObject.isStatic = false;
            var cap = ch.gameObject.AddComponent<CapsuleCollider>();
            cap.direction = 1;
            bool cyclist = t.name.StartsWith("Cyclist_");
            float s = Mathf.Max(1e-4f, t.lossyScale.y);
            float h = any ? Mathf.Clamp(b.size.y, 1.2f / s, 2.2f / s) : 1.75f / s;
            cap.height = h;
            cap.radius = (cyclist ? 0.45f : 0.28f) / s;
            cap.center = new Vector3(0f, (any ? b.min.y : 0f) + h * 0.5f, 0f);
            var rb = ch.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
        }
    }
}
