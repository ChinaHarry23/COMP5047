using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoonlitIsland
{
    /// <summary>Scene-persistent bat geometry with independent flight and articulated wing beats.</summary>
    public sealed class IslandBatFlock : MonoBehaviour
    {
        [Serializable]
        private sealed class Flight
        {
            public Transform bat;
            public Transform leftWing;
            public Transform rightWing;
            public float radius;
            public float ellipse;
            public float height;
            public float phase;
            public float angularSpeed;
            public float wingRate;
            public float wingPhase;
            public bool piloted;
        }

        [SerializeField] private List<Flight> flights = new List<Flight>();
        public int BatCount { get { return flights.Count; } }

        /// <summary>Takes a bat out of autonomous flight so IslandBatPilot can drive it directly.</summary>
        public Transform TakeBat(int index)
        {
            if (index < 0 || index >= flights.Count)
                return null;
            flights[index].piloted = true;
            return flights[index].bat;
        }

        /// <summary>Returns a bat to its autonomous orbit, resuming from wherever it was released.</summary>
        public void ReleaseBat(Transform bat)
        {
            foreach (Flight flight in flights)
            {
                if (flight.bat != bat)
                    continue;
                // Re-derive the orbit angle from the release position so the bat glides on
                // from where the pilot left it instead of teleporting back to its path.
                Vector3 local = bat.localPosition;
                if (local.sqrMagnitude > 0.01f)
                {
                    float resume = Mathf.Atan2(local.z / Mathf.Max(0.01f, flight.ellipse), local.x);
                    flight.phase = resume - Time.time * flight.angularSpeed;
                }
                flight.piloted = false;
                return;
            }
        }

        /// <summary>
        /// Builds an immediately visible flock. Persist its MeshFilter.sharedMesh objects
        /// as assets when saving a generated scene from an editor builder.
        /// </summary>
        public static GameObject CreateFlock(Transform parent, Material bodyMaterial, Material wingMaterial)
        {
            GameObject root = new GameObject("Bats - 20 animated forest residents");
            root.transform.SetParent(parent, false);
            IslandBatFlock flock = root.AddComponent<IslandBatFlock>();
            System.Random random = new System.Random(5047);
            Mesh sphere = MakeSphere();
            Mesh membrane = MakeWing();
            Mesh ear = MakeEar();
            Mesh spars = MakeSpars();
            Mesh tail = MakeTail();

            const int count = 20;
            for (int i = 0; i < count; i++)
            {
                GameObject bat = new GameObject("Bat " + (i + 1).ToString("00"));
                bat.transform.SetParent(root.transform, false);
                float scale = Range(random, 1.0f, 1.55f);
                bat.transform.localScale = Vector3.one * scale;
                Part("Body", bat.transform, sphere, bodyMaterial,
                    new Vector3(0, 0, -0.04f), new Vector3(0.18f, 0.16f, 0.43f));
                Part("Head", bat.transform, sphere, bodyMaterial,
                    new Vector3(0, 0.025f, 0.39f), new Vector3(0.19f, 0.17f, 0.18f));
                Part("Left pointed ear", bat.transform, ear, bodyMaterial,
                    new Vector3(-0.11f, 0.10f, 0.45f), Vector3.one);
                Part("Right pointed ear", bat.transform, ear, bodyMaterial,
                    new Vector3(0.11f, 0.10f, 0.45f), Vector3.one);
                Part("Tail membrane", bat.transform, tail, wingMaterial,
                    Vector3.zero, Vector3.one);

                GameObject right = new GameObject("Right wing - articulated shoulder");
                right.transform.SetParent(bat.transform, false);
                right.transform.localPosition = new Vector3(0.12f, 0, 0.02f);
                Part("Scalloped membrane", right.transform, membrane, wingMaterial, Vector3.zero, Vector3.one);
                Part("Wing fingers", right.transform, spars, bodyMaterial, Vector3.zero, Vector3.one);

                GameObject left = new GameObject("Left wing - articulated shoulder");
                left.transform.SetParent(bat.transform, false);
                left.transform.localPosition = new Vector3(-0.12f, 0, 0.02f);
                // Double-sided geometry keeps both mirrored and upward/downward surfaces visible.
                Part("Scalloped membrane", left.transform, membrane, wingMaterial, Vector3.zero,
                    new Vector3(-1, 1, 1));
                Part("Wing fingers", left.transform, spars, bodyMaterial, Vector3.zero,
                    new Vector3(-1, 1, 1));

                Flight flight = new Flight
                {
                    bat = bat.transform,
                    leftWing = left.transform,
                    rightWing = right.transform,
                    radius = Range(random, 22.0f, 62.0f),
                    ellipse = Range(random, 0.79f, 1.0f),
                    height = Range(random, 32.0f, 44.0f),
                    phase = (i + 0.18f) / count * Mathf.PI * 2,
                    angularSpeed = Range(random, 0.10f, 0.18f) * (i % 4 == 0 ? -1 : 1),
                    wingRate = Range(random, 3.1f, 4.7f),
                    wingPhase = Range(random, 0, Mathf.PI * 2)
                };
                flock.flights.Add(flight);
            }
            flock.SetPose(0);
            return root;
        }

        private void Update()
        {
            SetPose(Time.time);
        }

        private void SetPose(float time)
        {
            foreach (Flight flight in flights)
            {
                if (flight.bat == null || flight.leftWing == null || flight.rightWing == null)
                    continue;
                if (flight.piloted)
                {
                    // The pilot owns the body pose; only keep the wings beating.
                    FlapWings(flight, time);
                    continue;
                }
                float angle = flight.phase + time * flight.angularSpeed;
                float bob = Mathf.Sin(angle * 2.1f + flight.wingPhase) * 1.3f;
                flight.bat.localPosition = new Vector3(
                    Mathf.Cos(angle) * flight.radius,
                    flight.height + bob,
                    Mathf.Sin(angle) * flight.radius * flight.ellipse);
                Vector3 tangent = new Vector3(
                    -Mathf.Sin(angle) * flight.radius * flight.angularSpeed,
                    Mathf.Cos(angle * 2.1f + flight.wingPhase) * 1.3f * 2.1f * flight.angularSpeed,
                    Mathf.Cos(angle) * flight.radius * flight.ellipse * flight.angularSpeed);
                float bank = -Mathf.Sign(flight.angularSpeed) * 9.0f + Mathf.Sin(angle * 1.6f) * 6;
                flight.bat.localRotation = Quaternion.LookRotation(tangent, Vector3.up)
                    * Quaternion.Euler(0, 0, bank);
                FlapWings(flight, time);
            }
        }

        private static void FlapWings(Flight flight, float time)
        {
            float flap = Mathf.Sin(time * flight.wingRate * Mathf.PI * 2 + flight.wingPhase);
            float wingAngle = flap * 38 + 5;
            flight.rightWing.localRotation = Quaternion.Euler(0, 0, wingAngle);
            flight.leftWing.localRotation = Quaternion.Euler(0, 0, -wingAngle);
        }

        private static float Range(System.Random random, float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
        }

        private static GameObject Part(string name, Transform parent, Mesh mesh, Material material,
            Vector3 position, Vector3 scale)
        {
            GameObject part = new GameObject(name);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            return part;
        }

        private static Mesh MakeSphere()
        {
            const int rings = 10;
            const int sides = 14;
            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();
            for (int y = 0; y <= rings; y++)
            {
                float latitude = y * Mathf.PI / rings;
                for (int x = 0; x <= sides; x++)
                {
                    float longitude = x * Mathf.PI * 2 / sides;
                    vertices.Add(new Vector3(Mathf.Sin(latitude) * Mathf.Cos(longitude),
                        Mathf.Cos(latitude), Mathf.Sin(latitude) * Mathf.Sin(longitude)));
                }
            }
            for (int y = 0; y < rings; y++)
            {
                for (int x = 0; x < sides; x++)
                {
                    int a = y * (sides + 1) + x;
                    int b = a + sides + 1;
                    triangles.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
                }
            }
            return Finish("Bat rounded body", vertices, triangles);
        }

        private static Mesh MakeWing()
        {
            List<Vector2> outline = new List<Vector2> { new Vector2(0, 0.22f) };
            Curve(outline, new Vector2(0.18f, 0.54f), new Vector2(0.56f, 0.48f), 7);
            Curve(outline, new Vector2(1.10f, 0.46f), new Vector2(1.46f, 0.22f), 8);
            // Concave curves between finger tips form the bat's distinctive scalloped edge.
            Curve(outline, new Vector2(1.01f, 0.21f), new Vector2(0.99f, -0.22f), 7);
            Curve(outline, new Vector2(0.69f, 0.03f), new Vector2(0.60f, -0.44f), 7);
            Curve(outline, new Vector2(0.36f, -0.09f), new Vector2(0.27f, -0.45f), 7);
            Curve(outline, new Vector2(0.12f, -0.14f), new Vector2(0, -0.22f), 7);
            return DoubleSidedPolygon("Bat curved scalloped wing", outline);
        }

        private static void Curve(List<Vector2> points, Vector2 control, Vector2 end, int steps)
        {
            Vector2 start = points[points.Count - 1];
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                points.Add((1 - t) * (1 - t) * start + 2 * (1 - t) * t * control + t * t * end);
            }
        }

        private static Mesh MakeTail()
        {
            return DoubleSidedPolygon("Bat tail membrane", new List<Vector2>
            {
                new Vector2(-0.18f, -0.25f), new Vector2(0, -0.64f), new Vector2(0.18f, -0.25f)
            });
        }

        private static Mesh MakeEar()
        {
            List<Vector3> vertices = new List<Vector3>
            {
                new Vector3(-0.085f, 0, -0.055f), new Vector3(0.085f, 0, -0.055f),
                new Vector3(0.055f, 0, 0.075f), new Vector3(-0.055f, 0, 0.075f),
                new Vector3(0, 0.36f, 0.17f)
            };
            List<int> triangles = new List<int> { 0, 4, 1, 1, 4, 2, 2, 4, 3, 3, 4, 0, 0, 2, 3, 0, 1, 2 };
            return Finish("Bat pointed ear", vertices, triangles);
        }

        private static Mesh MakeSpars()
        {
            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();
            Vector3 root = new Vector3(0, 0, 0.22f);
            Vector3 wrist = new Vector3(0.56f, 0, 0.48f);
            Tube(vertices, triangles, root, wrist, 0.022f);
            Tube(vertices, triangles, wrist, new Vector3(1.46f, 0, 0.22f), 0.015f);
            Tube(vertices, triangles, wrist, new Vector3(0.99f, 0, -0.22f), 0.012f);
            Tube(vertices, triangles, wrist, new Vector3(0.60f, 0, -0.44f), 0.011f);
            Tube(vertices, triangles, wrist, new Vector3(0.27f, 0, -0.45f), 0.010f);
            return Finish("Bat articulated wing fingers", vertices, triangles);
        }

        private static void Tube(List<Vector3> vertices, List<int> triangles, Vector3 a, Vector3 b, float radius)
        {
            const int sides = 6;
            int start = vertices.Count;
            Vector3 axis = (b - a).normalized;
            Vector3 across = Vector3.Cross(axis, Vector3.up).normalized;
            for (int end = 0; end < 2; end++)
            {
                for (int i = 0; i < sides; i++)
                {
                    float angle = i * Mathf.PI * 2 / sides;
                    vertices.Add((end == 0 ? a : b) + radius *
                        (Mathf.Cos(angle) * across + Mathf.Sin(angle) * Vector3.up));
                }
            }
            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                triangles.AddRange(new[] { start + i, start + sides + i, start + next,
                    start + next, start + sides + i, start + sides + next });
            }
        }

        private static Mesh DoubleSidedPolygon(string name, List<Vector2> outline)
        {
            // Ear clipping respects concave scallops, unlike a centre triangle fan.
            List<int> remaining = new List<int>();
            float area = 0;
            for (int i = 0; i < outline.Count; i++)
            {
                Vector2 a = outline[i];
                Vector2 b = outline[(i + 1) % outline.Count];
                area += a.x * b.y - b.x * a.y;
            }
            for (int i = 0; i < outline.Count; i++)
                remaining.Add(area > 0 ? i : outline.Count - 1 - i);

            List<int> triangles = new List<int>();
            int safety = outline.Count * outline.Count;
            while (remaining.Count > 2 && safety-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int ia = remaining[(i + remaining.Count - 1) % remaining.Count];
                    int ib = remaining[i];
                    int ic = remaining[(i + 1) % remaining.Count];
                    Vector2 a = outline[ia];
                    Vector2 b = outline[ib];
                    Vector2 c = outline[ic];
                    if (Cross(b - a, c - b) <= 0.0000001f)
                        continue;
                    bool contains = false;
                    foreach (int other in remaining)
                    {
                        if (other == ia || other == ib || other == ic)
                            continue;
                        Vector2 p = outline[other];
                        if (Cross(b - a, p - a) >= -0.0000001f &&
                            Cross(c - b, p - b) >= -0.0000001f &&
                            Cross(a - c, p - c) >= -0.0000001f)
                        {
                            contains = true;
                            break;
                        }
                    }
                    if (contains)
                        continue;
                    triangles.AddRange(new[] { ia, ic, ib });
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped)
                    throw new InvalidOperationException("Could not triangulate bat membrane.");
            }

            List<Vector3> vertices = new List<Vector3>();
            foreach (Vector2 point in outline)
                vertices.Add(new Vector3(point.x, 0.005f, point.y));
            foreach (Vector2 point in outline)
                vertices.Add(new Vector3(point.x, -0.005f, point.y));
            int frontCount = triangles.Count;
            for (int i = 0; i < frontCount; i += 3)
                triangles.AddRange(new[] { triangles[i + 2] + outline.Count,
                    triangles[i + 1] + outline.Count, triangles[i] + outline.Count });
            return Finish(name, vertices, triangles);
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static Mesh Finish(string name, List<Vector3> vertices, List<int> triangles)
        {
            Mesh mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
