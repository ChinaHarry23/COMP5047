using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace Edge.EditorTools
{
    // Bench sitters: a seated "watching a video on the phone" pose authored in code as Humanoid muscle
    // values (no external animation asset), solved per avatar against the procedural bench (BuildBench is
    // only read): thighs ~horizontal, feet on the pad, hips/buttocks on the seat slats, back against the
    // backrest, forearms raised holding a phone in both hands, head tilted down. Baked into the bone
    // transforms (so it shows in edit mode) and stored on an EdgeBenchSitter, which re-applies it in Play
    // with tiny breathing / head motion. The sitter's Animator is disabled (it used to T-pose in edit mode).
    public static partial class EdgeSceneBuilder
    {
        // BuildBench (holder/bench local space, +Z front): seat top 0.45 m, slats z -0.165..0.255,
        // backrest slats reclined 15 deg (centre z = -0.2 - (h-0.5)*tan15, 0.03 thick), pad top 0.06 m (z < 0.455).
        const float SeatTop = 0.45f, SeatZMin = -0.165f, SeatZMax = 0.255f, PadTop = 0.06f, PadZMax = 0.455f;
        static float BackFrontZ(float y) => -0.2f - (y - 0.5f) * Mathf.Tan(15f * Mathf.Deg2Rad) + 0.015f;

        [MenuItem("EDGE/People/Fix Bench Sitters In Open Scene")]
        public static void FixBenchSittersInOpenScene()
        {
            var ped = Object.FindFirstObjectByType<EdgePedestrians>();
            if (!ped || ped.seated == null) { Debug.LogWarning("[EDGE] Fix Bench Sitters: no EdgePedestrians / seated people in the open scene"); return; }
            int n = 0;
            foreach (var holder in ped.seated)
            {
                var an = holder ? holder.GetComponentInChildren<Animator>(true) : null;
                if (!an) continue;
                Undo.RegisterFullObjectHierarchyUndo(holder.gameObject, "Fix Bench Sitters");
                if (FixBenchSitter(holder, an.gameObject)) n++;
            }
            EditorSceneManager.MarkSceneDirty(ped.gameObject.scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[EDGE] Fixed {n} bench sitter(s) (seated phone pose + phone light). Scene not saved.");
        }

        static int Muscle(string name)
        {
            int i = Array.IndexOf(HumanTrait.MuscleName, name);
            if (i < 0) Debug.LogWarning("[EDGE] muscle not found: " + name);
            return i;
        }

        // Pose and place one sitter. 'holder' sits on the bench with the bench's rotation (local +Z = front, y 0 = bench ground).
        static bool FixBenchSitter(Transform holder, GameObject body)
        {
            var an = body.GetComponent<Animator>();
            if (!an || !an.avatar || !an.avatar.isHuman) { Debug.LogWarning("[EDGE] sitter has no Humanoid avatar: " + body.name); return false; }
            an.enabled = true;
            foreach (var t in body.GetComponentsInChildren<Transform>(true)) if (t && t.name == "EdgePhone") Object.DestroyImmediate(t.gameObject);
            Transform B(HumanBodyBones b) => an.GetBoneTransform(b);
            Transform hips = B(HumanBodyBones.Hips), head = B(HumanBodyBones.Head);
            Transform[] up = { B(HumanBodyBones.LeftUpperLeg), B(HumanBodyBones.RightUpperLeg) };
            Transform[] lo = { B(HumanBodyBones.LeftLowerLeg), B(HumanBodyBones.RightLowerLeg) };
            Transform[] ft = { B(HumanBodyBones.LeftFoot), B(HumanBodyBones.RightFoot) };
            Transform[] hd = { B(HumanBodyBones.LeftHand), B(HumanBodyBones.RightHand) };
            Transform[] mid = { B(HumanBodyBones.LeftMiddleProximal), B(HumanBodyBones.RightMiddleProximal) };
            if (!hips || !head || up[0] == null || lo[0] == null || ft[0] == null || hd[0] == null) return false;

            var hph = new HumanPoseHandler(an.avatar, body.transform);
            var hp = new HumanPose();
            hph.GetHumanPose(ref hp);
            hp.bodyRotation = Quaternion.identity;
            for (int i = 0; i < hp.muscles.Length; i++) hp.muscles[i] = 0f;
            void Apply() => hph.SetHumanPose(ref hp);
            Apply();
            Vector3 L(Vector3 w) => holder.InverseTransformPoint(w);
            // Sole offset: foot joint height above the lowest foot vertex in the neutral standing pose.
            float sole = 0.08f;
            { var v = BakedVerts(body, holder); float minY = float.MaxValue; var f = L(ft[0].position);
              foreach (var p in v) if ((new Vector2(p.x - f.x, p.z - f.z)).sqrMagnitude < 0.04f) minY = Mathf.Min(minY, p.y);
              if (minY < float.MaxValue) sole = Mathf.Clamp(f.y - minY, 0.04f, 0.14f); }

            // Fixed parts of the pose.
            void Set(string m, float v) { int i = Muscle(m); if (i >= 0) hp.muscles[i] = v; }
            Set("Head Nod Down-Up", -0.55f); Set("Neck Nod Down-Up", -0.45f);
            Set("Left Upper Leg In-Out", 0.12f); Set("Right Upper Leg In-Out", 0.12f);
            Set("Left Shoulder Down-Up", -0.1f); Set("Right Shoulder Down-Up", -0.1f);
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                string n = HumanTrait.MuscleName[i];
                if (n.Contains("Stretched") && !n.Contains("Thumb")) hp.muscles[i] = n.Contains(" 1 ") ? 0.15f : -0.35f;
                if (n.Contains("Thumb") && n.Contains("Stretched")) hp.muscles[i] = 0.1f;
            }
            // Slight forward lean: pick the spine sign that moves the head forward.
            int spi = Muscle("Spine Front-Back"), chi = Muscle("Chest Front-Back");
            if (spi >= 0)
            {
                hp.muscles[spi] = 0.2f; Apply(); float a = L(head.position).z - L(hips.position).z;
                hp.muscles[spi] = -0.2f; Apply(); float b = L(head.position).z - L(hips.position).z;
                float s = a > b ? 1f : -1f; hp.muscles[spi] = 0.12f * s; if (chi >= 0) hp.muscles[chi] = 0.08f * s;
            }

            float thighTarget = -4f; // deg, knee slightly below the hip joint
            for (int iter = 0; iter < 5; iter++)
            {
                // Legs (solved on the left, mirrored to the right).
                int uL = Muscle("Left Upper Leg Front-Back"), uR = Muscle("Right Upper Leg Front-Back");
                int lL = Muscle("Left Lower Leg Stretch"), lR = Muscle("Right Lower Leg Stretch");
                Solve1D(hp, new[] { uL, uR }, Apply, () =>
                {
                    Vector3 d = L(lo[0].position) - L(up[0].position);
                    if (d.z < 0.05f) return 1000f - d.z;
                    float ang = Mathf.Atan2(d.y, d.z) * Mathf.Rad2Deg;
                    return Mathf.Abs(ang - thighTarget);
                });
                Solve1D(hp, new[] { lL, lR }, Apply, () =>
                {
                    Vector3 d = L(ft[0].position) - L(lo[0].position);
                    if (d.y > -0.1f) return 1000f + d.y;
                    float ang = Mathf.Atan2(d.z, -d.y) * Mathf.Rad2Deg; // + = foot forward of the knee
                    return Mathf.Abs(ang - 8f);
                });
                // Arms: wrists in front of the chest, ~0.18 m apart, phone held below the face.
                Vector3 hipsL = L(hips.position);
                for (int s = 0; s < 2; s++)
                {
                    string side = s == 0 ? "Left" : "Right";
                    // Character faces holder +Z, so its left is -X.
                    Vector3 target = hipsL + new Vector3(s == 0 ? -0.10f : 0.10f, 0.26f, 0.30f) /* palms at the phone short ends */;
                    var ms = new[] { Muscle(side + " Arm Down-Up"), Muscle(side + " Arm Front-Back"), Muscle(side + " Forearm Stretch"), Muscle(side + " Arm Twist In-Out") };
                    var hand = hd[s];
                    Descend(hp, ms, Apply, () => (L(hand.position) - target).sqrMagnitude);
                }
                Set("Left Hand Down-Up", 0.25f); Set("Right Hand Down-Up", 0.25f);
                // Supinate the forearms so the palms face up and slightly inward (towards each other), cradling the phone.
                for (int s = 0; s < 2; s++)
                {
                    int side = s;
                    string sn = s == 0 ? "Left" : "Right";
                    Solve1D(hp, new[] { Muscle(sn + " Forearm Twist In-Out") }, Apply, () =>
                    {
                        Vector3 n = PalmNormal(an, side);
                        Vector3 inward = (hd[1 - side].position - hd[side].position).normalized;
                        Vector3 want = (inward * 0.9f + holder.up * 0.35f).normalized; // palms face each other, gripping the short ends
                        return 1f - Vector3.Dot(n, want);
                    });
                }
                Apply();
                PlaceOnSeat(holder, body, hips);
                // Feet: sole on the pad (or ground in front of it); raise / lower the knees to fit.
                float footZ = L(ft[0].position).z;
                float ground = footZ < PadZMax ? PadTop : 0f;
                float soleY = Mathf.Min(L(ft[0].position).y, L(ft[1].position).y) - sole;
                float err = soleY - ground;
                if (Mathf.Abs(err) < 0.01f) break;
                float thighLen = Vector3.Distance(up[0].position, lo[0].position);
                thighTarget = Mathf.Clamp(thighTarget - Mathf.Rad2Deg * Mathf.Asin(Mathf.Clamp(err / thighLen, -0.5f, 0.5f)), -22f, 14f); // feet too high -> knees down
            }

            // Two-handed landscape grip: place the phone between the palms, then curl each finger round the back
            // and lay each thumb on the front edge (solved in Humanoid muscle space, so the runtime pose keeps it).
            PhonePose(an, head, holder, out var phPos, out var phRot);
            for (int g = 0; g < 2; g++)
            {
                SolveGrip(hp, Apply, an, phPos, phRot);
                if (!Clearance(an, ref phPos, phRot)) break; // re-solve once if the phone had to move
            }
            hph.GetHumanPose(ref hp);
            hph.Dispose();
            an.enabled = false; // pose is driven by EdgeBenchSitter, not by an AnimatorController (no T-pose in edit mode)

            var bs = holder.GetComponent<EdgeBenchSitter>();
            if (!bs) bs = holder.gameObject.AddComponent<EdgeBenchSitter>();
            bs.avatar = an.avatar; bs.body = body.transform; bs.muscles = (float[])hp.muscles.Clone();
            bs.bodyPosition = hp.bodyPosition; bs.bodyRotation = hp.bodyRotation;
            BuildPhone(bs, an, phPos, phRot);
            foreach (var t in holder.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = false;
            EditorUtility.SetDirty(bs);
            var h = L(hips.position);
            Debug.Log($"[EDGE] Sitter {body.name}: hips at y {h.y:F2} z {h.z:F2} (seat {SeatTop}), thigh {thighTarget:F0} deg, sole offset {sole:F2} m, feet y {Mathf.Min(L(ft[0].position).y, L(ft[1].position).y) - sole:F2}");
            return true;
        }

        // Grid search then refine one muscle value (applied to all 'idx', mirrored) minimising err().
        static void Solve1D(HumanPose hp, int[] idx, Action apply, Func<float> err)
        {
            float best = float.MaxValue, bv = 0f;
            void Try(float v) { foreach (var i in idx) if (i >= 0) hp.muscles[i] = v; apply(); float e = err(); if (e < best) { best = e; bv = v; } }
            for (int k = 0; k <= 40; k++) Try(-1f + k * 0.05f);
            for (float step = 0.025f; step > 0.002f; step *= 0.5f) { float c = bv; Try(Mathf.Clamp(c - step, -1f, 1f)); Try(Mathf.Clamp(c + step, -1f, 1f)); }
            foreach (var i in idx) if (i >= 0) hp.muscles[i] = bv;
            apply();
        }

        // Coordinate descent over several muscles minimising err().
        static void Descend(HumanPose hp, int[] ms, Action apply, Func<float> err)
        {
            apply(); float best = err();
            for (float step = 0.3f; step > 0.004f; step *= 0.5f)
                for (int rep = 0; rep < 6; rep++)
                {
                    bool imp = false;
                    foreach (var i in ms)
                    {
                        if (i < 0) continue;
                        float c = hp.muscles[i];
                        foreach (float d in new[] { step, -step })
                        {
                            hp.muscles[i] = Mathf.Clamp(c + d, -1f, 1f); apply(); float e = err();
                            if (e < best - 1e-7f) { best = e; c = hp.muscles[i]; imp = true; break; }
                            hp.muscles[i] = c;
                        }
                    }
                    apply();
                    if (!imp) break;
                }
        }

        static List<Vector3> BakedVerts(GameObject body, Transform space)
        {
            var res = new List<Vector3>();
            var m = new Mesh();
            foreach (var r in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!r.sharedMesh) continue;
                r.BakeMesh(m, true);
                var mtx = space.worldToLocalMatrix * Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
                foreach (var v in m.vertices) res.Add(mtx.MultiplyPoint3x4(v));
            }
            Object.DestroyImmediate(m);
            return res;
        }

        // Hips over the seat, then lift/lower so the buttocks rest on the slats and push back against the backrest.
        static void PlaceOnSeat(Transform holder, GameObject body, Transform hips)
        {
            Vector3 target = new Vector3(0f, SeatTop + 0.09f, -0.04f);
            var hl = holder.InverseTransformPoint(hips.position);
            body.transform.position += holder.TransformVector(target - hl);
            for (int it = 0; it < 3; it++)
            {
                var verts = BakedVerts(body, holder);
                hl = holder.InverseTransformPoint(hips.position);
                float minY = float.MaxValue, pen = float.MinValue;
                foreach (var p in verts)
                {
                    if (Mathf.Abs(p.x - hl.x) > 0.22f) continue;
                    if (p.z > SeatZMin && p.z < SeatZMax && p.y < hl.y + 0.05f) minY = Mathf.Min(minY, p.y);
                    if (p.y > SeatTop + 0.03f && p.y < 0.88f && Mathf.Abs(p.x - hl.x) < 0.25f) pen = Mathf.Max(pen, BackFrontZ(p.y) - p.z);
                }
                Vector3 d = Vector3.zero;
                if (minY < float.MaxValue) d.y = SeatTop + 0.003f - minY;
                if (pen > float.MinValue) d.z = pen + 0.008f; // >0: inside the backrest -> forward; <0: gap -> back
                if (d.z < 0f) d.z = Mathf.Max(d.z, -0.08f);
                body.transform.position += holder.TransformVector(d);
                if (d.sqrMagnitude < 1e-6f) break;
            }
        }
        static Vector3 Inv(Vector3 s) => new Vector3(1f / s.x, 1f / s.y, 1f / s.z);

        // Palm normal (out of the palm) of one hand: the curled fingers bend towards the palm side, so the
        // direction from the middle proximal to the middle distal joint, minus its along-the-hand part, points
        // out of the palm. Falls back to the cross product of the knuckle line and the hand axis.
        static Vector3 PalmNormal(Animator an, int side)
        {
            bool l = side == 0;
            Transform h = an.GetBoneTransform(l ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            Transform mp = an.GetBoneTransform(l ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            Transform md = an.GetBoneTransform(l ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal);
            Transform ip = an.GetBoneTransform(l ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            Transform lp = an.GetBoneTransform(l ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            if (!h || !mp) return Vector3.up;
            Vector3 axis = (mp.position - h.position).normalized;
            Vector3 n = Vector3.zero;
            if (md) n = Vector3.ProjectOnPlane(md.position - mp.position, axis);
            if (n.sqrMagnitude < 1e-6f && ip && lp)
            {
                n = Vector3.Cross(axis, ip.position - lp.position);
                if (!l) n = -n;
            }
            return n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.up;
        }

        static readonly HumanBodyBones[] FingerBones = {
            HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
            HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
            HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
            HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
            HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
            HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
            HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
        };

        // Phone rests ON the palms: centred between both palm centres, lifted along the mean palm normal by half a
        // hand thickness + 1 cm, screen tilted ~35 deg from horizontal towards the face, then pushed further along
        // its normal until no finger joint (+ finger radius + 3 mm) is inside the slab. Thumbs lying on the screen
        // side are pushed the other way only if that does not break the finger clearance.
        const float PL = 0.15f, PT = 0.008f, PW = 0.075f, FingerR = 0.007f, Clear = 0.003f;

        // Phone pose: centred between the two palm centres (palms face each other at its short ends), held
        // landscape, screen tilted ~35 deg from horizontal towards the face, mid-plane ~1.2 cm in front of the
        // palm centres so the curled fingers end up behind it and the thumbs on its front.
        static void PhonePose(Animator an, Transform head, Transform holder, out Vector3 pos, out Quaternion rot)
        {
            Transform hl = an.GetBoneTransform(HumanBodyBones.LeftHand), hr = an.GetBoneTransform(HumanBodyBones.RightHand);
            Transform ml = an.GetBoneTransform(HumanBodyBones.LeftMiddleProximal), mr = an.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            Vector3 pl = ml ? (hl.position + ml.position) * 0.5f : hl.position;
            Vector3 pr = mr ? (hr.position + mr.position) * 0.5f : hr.position;
            Vector3 c = (pl + pr) * 0.5f;
            Vector3 x = Vector3.ProjectOnPlane(pr - pl, holder.up).normalized;      // left -> right
            if (x.sqrMagnitude < 1e-6f) x = holder.right;
            Vector3 toFace = Vector3.ProjectOnPlane(Vector3.ProjectOnPlane(head.position - c, x), holder.up).normalized;
            Vector3 y = Vector3.ProjectOnPlane(holder.up * Mathf.Cos(35f * Mathf.Deg2Rad) + toFace * Mathf.Sin(35f * Mathf.Deg2Rad), x).normalized;
            rot = Quaternion.LookRotation(Vector3.Cross(x, y), y);
            pos = c + y * 0.012f;
            float sep = Vector3.Distance(pl, pr);
            if (sep < PL + 0.02f) Debug.Log($"[EDGE] Phone grip: palms only {sep:F3} m apart (phone {PL} m); clearance will lift it");
            Clearance(an, ref pos, rot);
        }

        // Penetration depth of a sphere (radius r) into the phone slab; <= 0 = outside.
        static float Depth(Vector3 w, Vector3 pos, Quaternion rot, float r)
        {
            Vector3 q = Quaternion.Inverse(rot) * (w - pos);
            float dx = PL * 0.5f + r - Mathf.Abs(q.x), dy = PT * 0.5f + r - Mathf.Abs(q.y), dz = PW * 0.5f + r - Mathf.Abs(q.z);
            return Mathf.Min(dx, Mathf.Min(dy, dz));
        }

        // Push the phone along its screen normal until no hand / finger joint (+ radius + 3 mm) is inside it.
        // Returns true if it moved.
        static bool Clearance(Animator an, ref Vector3 pos, Quaternion rot)
        {
            Vector3 y = rot * Vector3.up;
            float half = PT * 0.5f + FingerR + Clear;
            bool moved = false;
            for (int it = 0; it < 6; it++)
            {
                float up = 0f, down = 0f;
                foreach (var b in FingerBones)
                {
                    var t = an.GetBoneTransform(b);
                    if (!t) continue;
                    Vector3 q = Quaternion.Inverse(rot) * (t.position - pos);
                    if (Mathf.Abs(q.x) > PL * 0.5f + FingerR + Clear || Mathf.Abs(q.z) > PW * 0.5f + FingerR + Clear || Mathf.Abs(q.y) >= half) continue;
                    bool thumb = b.ToString().Contains("Thumb");
                    if (thumb && q.y > 0f) down = Mathf.Max(down, half - q.y);
                    else up = Mathf.Max(up, q.y + half);
                }
                if (up <= 0f && down <= 0f) break;
                pos += y * (up > 0f ? up : -down); moved = true;
                if (up > 0f && down > 0f) { Debug.Log("[EDGE] Phone: thumb rests on the screen face (fingers take priority)"); break; }
            }
            return moved;
        }

        static Vector3 Tip(Transform inter, Transform dist) =>
            dist.position + (dist.position - inter.position) * 0.8f; // fingertip ~ 0.8 distal-segment lengths past the distal joint

        // Per-finger grip "IK" in muscle space: one curl parameter per finger drives 1/2/3 Stretched with fixed
        // ratios (little finger curls a bit more), searched so the fingertip lies 3-5 mm behind the phone back
        // with no joint inside the phone. Thumbs: coordinate descent on the 4 thumb muscles to put the tip on the
        // front bezel near the short edge without penetrating. Slight fanning via Spread.
        static void SolveGrip(HumanPose hp, Action apply, Animator an, Vector3 pos, Quaternion rot)
        {
            string[] fingers = { "Index", "Middle", "Ring", "Little" };
            float[] spread = { 0.12f, 0.0f, -0.08f, -0.2f };
            float[] extra = { 1f, 1f, 1.05f, 1.15f };
            for (int s = 0; s < 2; s++)
            {
                string side = s == 0 ? "Left" : "Right";
                float sx = s == 0 ? -1f : 1f; // left hand holds the -X end
                for (int f = 0; f < 4; f++)
                {
                    int m1 = Muscle($"{side} {fingers[f]} 1 Stretched"), m2 = Muscle($"{side} {fingers[f]} 2 Stretched"), m3 = Muscle($"{side} {fingers[f]} 3 Stretched");
                    int sp = Muscle($"{side} {fingers[f]} Spread");
                    if (sp >= 0) hp.muscles[sp] = spread[f];
                    var bp = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), $"{side}{fingers[f]}Proximal");
                    var bi = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), $"{side}{fingers[f]}Intermediate");
                    var bd = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), $"{side}{fingers[f]}Distal");
                    Transform tp = an.GetBoneTransform(bp), ti = an.GetBoneTransform(bi), td = an.GetBoneTransform(bd);
                    if (!tp || !ti || !td || m1 < 0) continue;
                    float ex = extra[f];
                    float best = float.MaxValue, bc = -0.4f;
                    void TryC(float c)
                    {
                        hp.muscles[m1] = Mathf.Clamp(c * 0.85f * ex, -1f, 1f);
                        if (m2 >= 0) hp.muscles[m2] = Mathf.Clamp(c * 1.0f * ex, -1f, 1f);
                        if (m3 >= 0) hp.muscles[m3] = Mathf.Clamp(c * 0.55f * ex, -1f, 1f);
                        apply();
                        Vector3 tip = Tip(ti, td);
                        float pen = Mathf.Max(Mathf.Max(Depth(tp.position, pos, rot, 0.008f), Depth(ti.position, pos, rot, 0.007f)),
                                              Mathf.Max(Depth(td.position, pos, rot, 0.006f), Depth(tip, pos, rot, 0.005f)));
                        float e;
                        if (pen > 0f) e = 10f + pen * 100f;
                        else
                        {
                            Vector3 q = Quaternion.Inverse(rot) * (tip - pos);
                            float back = -(q.y + PT * 0.5f) - 0.005f;       // gap from fingertip skin to the phone back
                            e = Mathf.Abs(back - 0.004f) + 2f * Mathf.Max(0f, Mathf.Abs(q.z) - PW * 0.5f)
                              + 2f * Mathf.Max(0f, sx * q.x - (PL * 0.5f - 0.005f)) // tip must be behind the phone, not past its end
                              + 2f * Mathf.Max(0f, -sx * q.x);                     // ...and not past the middle
                        }
                        if (e < best) { best = e; bc = c; }
                    }
                    for (int k = 0; k <= 50; k++) TryC(-1f + k * 0.026f); // -1 (curled) .. +0.3
                    for (float st = 0.013f; st > 0.001f; st *= 0.5f) { float c0 = bc; TryC(c0 - st); TryC(c0 + st); }
                    TryC(bc);
                    Debug.Log($"[EDGE] Grip {side} {fingers[f]}: curl {bc:F2}, prox {Vector3.Angle(tp.position - an.GetBoneTransform(s == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand).position, ti.position - tp.position):F0} / inter {Vector3.Angle(ti.position - tp.position, td.position - ti.position):F0} / dist {Vector3.Angle(td.position - ti.position, Tip(ti, td) - td.position):F0} deg (approx), err {best * 1000f:F1} mm");
                }
                // Thumb on the front, tip on the bezel near the short edge.
                var tm = new[] { Muscle($"{side} Thumb 1 Stretched"), Muscle($"{side} Thumb 2 Stretched"), Muscle($"{side} Thumb 3 Stretched"), Muscle($"{side} Thumb Spread") };
                Transform t1 = an.GetBoneTransform(s == 0 ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal);
                Transform t2 = an.GetBoneTransform(s == 0 ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate);
                Transform t3 = an.GetBoneTransform(s == 0 ? HumanBodyBones.LeftThumbDistal : HumanBodyBones.RightThumbDistal);
                if (!t1 || !t2 || !t3) continue;
                Vector3 target = pos + rot * new Vector3(sx * (PL * 0.5f - 0.012f), PT * 0.5f + 0.007f, 0f);
                Descend(hp, tm, apply, () =>
                {
                    Vector3 tip = Tip(t2, t3);
                    float pen = Mathf.Max(Mathf.Max(Depth(t1.position, pos, rot, 0.009f), Depth(t2.position, pos, rot, 0.008f)),
                                          Mathf.Max(Depth(t3.position, pos, rot, 0.007f), Depth(tip, pos, rot, 0.006f)));
                    return (tip - target).sqrMagnitude + (pen > 0f ? 1f + pen * 10f : 0f);
                });
                Debug.Log($"[EDGE] Grip {side} Thumb: tip {Vector3.Distance(Tip(t2, t3), target) * 1000f:F1} mm from the bezel target");
            }
            apply();
        }

        static void BuildPhone(EdgeBenchSitter bs, Animator an, Vector3 pos, Quaternion rot)
        {
            Transform hr = an.GetBoneTransform(HumanBodyBones.RightHand);
            var phone = new GameObject("EdgePhone").transform;
            phone.SetPositionAndRotation(pos, rot);
            phone.SetParent(hr, true); // anchor only; position comes from both palms
            var bodyMat = LitMat("Edge_Phone_Body", new Color(0.02f, 0.02f, 0.025f), 0.75f, 0.3f);
            var scrMat = UnlitScreenMat();
            Transform Part(string n, Vector3 pos, Vector3 size, Material m)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(g.GetComponent<Collider>());
                g.name = n; g.transform.SetParent(phone, false);
                g.transform.localPosition = pos; g.transform.localRotation = Quaternion.identity;
                g.transform.localScale = Vector3.Scale(size, Inv(phone.lossyScale));
                var r = g.GetComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = ShadowCastingMode.Off;
                return g.transform;
            }
            Vector3 k = Inv(phone.lossyScale); // keep metric sizes / offsets whatever the avatar scale
            Part("Body", Vector3.zero, new Vector3(0.15f, 0.008f, 0.075f), bodyMat);
            var scr = Part("Screen", Vector3.Scale(new Vector3(0f, 0.0042f, 0f), k), new Vector3(0.142f, 0.0006f, 0.067f), scrMat);
            var lg = new GameObject("PhoneLight");
            lg.transform.SetParent(phone, false);
            lg.transform.localPosition = Vector3.Scale(new Vector3(0f, 0.04f, 0f), k);
            var l = lg.AddComponent<Light>();
            l.type = LightType.Point; l.shadows = LightShadows.None;
            l.color = new Color(0.78f, 0.87f, 1f); l.range = 0.8f;
            if (!lg.GetComponent<HDAdditionalLightData>()) lg.AddComponent<HDAdditionalLightData>();
            l.lightUnit = LightUnit.Lumen; l.intensity = 0.6f;
            bs.screen = scr.GetComponent<Renderer>(); bs.phoneLight = l;
        }

        static Material UnlitScreenMat()
        {
            string path = $"{Root}/Materials/Edge_Phone_Screen.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            var sh = Shader.Find("HDRP/Unlit");
            if (!m) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); } else m.shader = sh;
            m.SetColor("_UnlitColor", new Color(0.30f, 0.42f, 0.62f) * 0.35f);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
