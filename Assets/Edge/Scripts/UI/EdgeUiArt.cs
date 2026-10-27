using System.Collections.Generic;
using UnityEngine;

namespace Edge.UI
{
    // One procedurally drawn sprite: RGBA pixels (row 0 = bottom, as Texture2D expects) and its
    // 9-slice border (x = left, y = bottom, z = right, w = top) in pixels.
    public struct UiSpriteData
    {
        public string id;
        public int w, h;
        public Color32[] px;
        public Vector4 border;
    }

    // The bat-themed UI art, drawn in code (no image assets to import): anti-aliased shapes from
    // signed distances, plus a scanline polygon filler for the bat. Icons are white and tinted by
    // the Image colour; the panel bakes its night-sky gradient and moonlit rim. Pure C#, so the
    // same pixels can be rendered offline for previews.
    public static class EdgeUiArt
    {
        public static readonly string[] All = { "panel", "card", "pill", "key", "glow", "ring", "line", "bat", "moth", "moon", "lamp", "echo" };

        public static UiSpriteData Make(string id)
        {
            switch (id)
            {
                case "panel": return Panel();
                case "card": return Card();
                case "pill": return Pill();
                case "key": return Key();
                case "glow": return Glow();
                case "ring": return Ring();
                case "line": return Line();
                case "bat": return Bat();
                case "moth": return Moth();
                case "moon": return Moon();
                case "lamp": return Lamp();
                case "echo": return Echo();
                default: return Pill();
            }
        }

        // ---- shapes -------------------------------------------------------------------------
        static float Cov(float d) => Mathf.Clamp01(0.5f - d);   // signed distance (px) -> pixel coverage

        static float RoundRect(float x, float y, float cx, float cy, float hx, float hy, float r)
        {
            float qx = Mathf.Abs(x - cx) - (hx - r), qy = Mathf.Abs(y - cy) - (hy - r);
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        static float Circle(float x, float y, float cx, float cy, float r)
        {
            float dx = x - cx, dy = y - cy;
            return Mathf.Sqrt(dx * dx + dy * dy) - r;
        }

        // Rotated ellipse (approximate distance, fine for anti-aliasing)
        static float Ellipse(float x, float y, float cx, float cy, float rx, float ry, float deg)
        {
            float a = deg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            float dx = x - cx, dy = y - cy;
            float u = (dx * c + dy * s) / rx, v = (-dx * s + dy * c) / ry;
            return (Mathf.Sqrt(u * u + v * v) - 1f) * Mathf.Min(rx, ry);
        }

        static float Segment(float x, float y, Vector2 a, Vector2 b, float halfWidth)
        {
            Vector2 p = new Vector2(x, y), ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return (p - (a + ab * t)).magnitude - halfWidth;
        }

        static UiSpriteData White(string id, int w, int h, float[] a, Vector4 border)
        {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a[i]) * 255f + 0.5f));
            return new UiSpriteData { id = id, w = w, h = h, px = px, border = border };
        }

        delegate float Shape(float x, float y);

        static float[] Raster(int w, int h, Shape alpha)
        {
            var a = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    a[y * w + x] = alpha(x + 0.5f, y + 0.5f);
            return a;
        }

        // Scanline fill with 5 sub-rows per pixel and exact horizontal span coverage.
        static void FillPolygon(float[] a, int w, int h, List<Vector2> poly, float weight = 1f)
        {
            const int SS = 5;
            var xs = new List<float>(16);
            for (int y = 0; y < h; y++)
                for (int s = 0; s < SS; s++)
                {
                    float sy = y + (s + 0.5f) / SS;
                    xs.Clear();
                    for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                    {
                        Vector2 p1 = poly[j], p2 = poly[i];
                        if ((p1.y <= sy && p2.y > sy) || (p2.y <= sy && p1.y > sy))
                            xs.Add(p1.x + (sy - p1.y) / (p2.y - p1.y) * (p2.x - p1.x));
                    }
                    xs.Sort();
                    for (int k = 0; k + 1 < xs.Count; k += 2) AddSpan(a, w, y, xs[k], xs[k + 1], weight / SS);
                }
        }

        static void AddSpan(float[] a, int w, int y, float x0, float x1, float wt)
        {
            x0 = Mathf.Clamp(x0, 0f, w); x1 = Mathf.Clamp(x1, 0f, w);
            if (x1 <= x0) return;
            int i0 = (int)x0, i1 = (int)x1;
            if (i0 == i1) { a[y * w + i0] += (x1 - x0) * wt; return; }
            a[y * w + i0] += (i0 + 1 - x0) * wt;
            for (int x = i0 + 1; x < i1; x++) a[y * w + x] += wt;
            if (i1 < w) a[y * w + i1] += (x1 - i1) * wt;
        }

        static void Bezier(List<Vector2> into, Vector2 a, Vector2 c, Vector2 b, int steps)
        {
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps, u = 1f - t;
                into.Add(u * u * a + 2f * u * t * c + t * t * b);
            }
        }

        // ---- sprites ------------------------------------------------------------------------
        // Night-sky panel: deep indigo gradient, moonlit rim brighter at the top.
        static UiSpriteData Panel()
        {
            const int S = 96; const float R = 28f;
            var px = new Color32[S * S];
            Color top = new Color(0.105f, 0.11f, 0.205f), bottom = new Color(0.043f, 0.043f, 0.095f);
            Color rimTop = new Color(0.69f, 0.81f, 1f), rimBottom = new Color(0.36f, 0.4f, 0.64f);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f, t = fy / S;
                    float d = RoundRect(fx, fy, S * 0.5f, S * 0.5f, S * 0.5f - 0.5f, S * 0.5f - 0.5f, R);
                    float cov = Cov(d);
                    float rim = Cov(Mathf.Abs(d + 1.3f) - 0.8f) * Mathf.Lerp(0.28f, 0.6f, t);
                    Color c = Color.Lerp(Color.Lerp(bottom, top, t), Color.Lerp(rimBottom, rimTop, t), rim);
                    float alpha = cov * Mathf.Max(0.9f, rim);
                    px[y * S + x] = new Color(c.r, c.g, c.b, alpha);
                }
            return new UiSpriteData { id = "panel", w = S, h = S, px = px, border = new Vector4(32, 32, 32, 32) };
        }

        // Inner card: faint fill with a brighter rim; tinted per bat.
        static UiSpriteData Card()
        {
            const int S = 64;
            var a = Raster(S, S, (x, y) =>
            {
                float d = RoundRect(x, y, 32f, 32f, 31.5f, 31.5f, 16f);
                return Cov(d) * 0.1f + Cov(Mathf.Abs(d + 1f) - 0.75f) * 0.42f;
            });
            return White("card", S, S, a, new Vector4(20, 20, 20, 20));
        }

        static UiSpriteData Pill()
        {
            var a = Raster(64, 32, (x, y) => Cov(RoundRect(x, y, 32f, 16f, 31.5f, 15.5f, 15.5f)));
            return White("pill", 64, 32, a, new Vector4(16, 16, 16, 16));
        }

        // Keycap: rounded square outline with a faint face.
        static UiSpriteData Key()
        {
            var a = Raster(64, 64, (x, y) =>
            {
                float d = RoundRect(x, y, 32f, 32f, 31f, 31f, 13f);
                return Cov(d) * 0.16f + Cov(Mathf.Abs(d + 1.2f) - 1f) * 0.85f;
            });
            return White("key", 64, 64, a, new Vector4(18, 18, 18, 18));
        }

        static UiSpriteData Glow()
        {
            var a = Raster(128, 128, (x, y) =>
            {
                float dx = (x - 64f) / 64f, dy = (y - 64f) / 64f;
                return Mathf.Pow(Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)), 2.2f);
            });
            return White("glow", 128, 128, a, Vector4.zero);
        }

        static UiSpriteData Ring()
        {
            var a = Raster(128, 128, (x, y) => Cov(Mathf.Abs(Circle(x, y, 64f, 64f, 56f)) - 5f));
            return White("ring", 128, 128, a, Vector4.zero);
        }

        // Divider: a hairline that fades out at both ends.
        static UiSpriteData Line()
        {
            var a = Raster(256, 8, (x, y) => Mathf.Pow(Mathf.Sin(Mathf.PI * x / 256f), 1.6f) * Cov(Mathf.Abs(y - 4f) - 1f));
            return White("line", 256, 8, a, Vector4.zero);
        }

        // Bat in flight, wings spread: pointed ears, thumb claws, scalloped trailing edges.
        static UiSpriteData Bat()
        {
            const int W = 256, H = 128; const float pad = 3f;
            var half = new List<Vector2>
            {
                new Vector2(0f, 0.06f), new Vector2(0.028f, 0.1f), new Vector2(0.052f, 0.25f), new Vector2(0.078f, 0.09f),
                new Vector2(0.11f, 0.08f), new Vector2(0.2f, 0.12f), new Vector2(0.32f, 0.2f), new Vector2(0.46f, 0.27f),
                new Vector2(0.575f, 0.3f), new Vector2(0.6f, 0.345f), new Vector2(0.63f, 0.295f), new Vector2(0.78f, 0.3f),
                new Vector2(0.92f, 0.26f), new Vector2(1f, 0.2f),
            };
            Vector2 A = new Vector2(1f, 0.2f), B = new Vector2(0.82f, -0.12f), C = new Vector2(0.6f, -0.22f),
                    D = new Vector2(0.38f, -0.2f), E = new Vector2(0.17f, -0.25f);
            Bezier(half, A, new Vector2(0.84f, 0.1f), B, 8);
            Bezier(half, B, new Vector2(0.7f, -0.06f), C, 8);
            Bezier(half, C, new Vector2(0.49f, -0.1f), D, 8);
            Bezier(half, D, new Vector2(0.28f, -0.12f), E, 8);
            half.Add(new Vector2(0.11f, -0.18f)); half.Add(new Vector2(0.07f, -0.27f)); half.Add(new Vector2(0f, -0.22f));
            var poly = new List<Vector2>(half.Count * 2);
            foreach (var p in half) poly.Add(p);
            for (int i = half.Count - 2; i >= 1; i--) poly.Add(new Vector2(-half[i].x, half[i].y));   // mirrored left half
            for (int i = 0; i < poly.Count; i++)
                poly[i] = new Vector2((poly[i].x + 1f) * 0.5f * (W - 2f * pad) + pad, (poly[i].y + 0.5f) * (H - 2f * pad) + pad);
            var a = new float[W * H];
            FillPolygon(a, W, H, poly);
            return White("bat", W, H, a, Vector4.zero);
        }

        static UiSpriteData Moth()
        {
            var a = Raster(128, 128, (x, y) =>
            {
                float d = Ellipse(x, y, 64f, 62f, 6f, 27f, 90f);                       // body
                d = Mathf.Min(d, Ellipse(x, y, 37f, 72f, 30f, 15f, -25f));            // forewings
                d = Mathf.Min(d, Ellipse(x, y, 91f, 72f, 30f, 15f, 25f));
                d = Mathf.Min(d, Ellipse(x, y, 45f, 48f, 20f, 13f, 25f));             // hindwings
                d = Mathf.Min(d, Ellipse(x, y, 83f, 48f, 20f, 13f, -25f));
                d = Mathf.Min(d, Segment(x, y, new Vector2(61f, 88f), new Vector2(48f, 112f), 1.4f));   // antennae
                d = Mathf.Min(d, Segment(x, y, new Vector2(67f, 88f), new Vector2(80f, 112f), 1.4f));
                d = Mathf.Min(d, Circle(x, y, 48f, 112f, 3f));
                d = Mathf.Min(d, Circle(x, y, 80f, 112f, 3f));
                return Cov(d);
            });
            return White("moth", 128, 128, a, Vector4.zero);
        }

        static UiSpriteData Moon()
        {
            var a = Raster(128, 128, (x, y) => Cov(Circle(x, y, 60f, 62f, 46f)) * (1f - Cov(Circle(x, y, 80f, 76f, 40f))));
            return White("moon", 128, 128, a, Vector4.zero);
        }

        // Street lamp with its light cone (the cone is translucent).
        static UiSpriteData Lamp()
        {
            const int S = 128;
            var a = Raster(S, S, (x, y) =>
            {
                float d = RoundRect(x, y, 40f, 50f, 4f, 44f, 3f);                                  // pole
                d = Mathf.Min(d, RoundRect(x, y, 40f, 8f, 13f, 4f, 2f));                          // base
                d = Mathf.Min(d, Segment(x, y, new Vector2(40f, 90f), new Vector2(54f, 102f), 3f)); // arm
                d = Mathf.Min(d, Segment(x, y, new Vector2(54f, 102f), new Vector2(76f, 104f), 3f));
                d = Mathf.Min(d, RoundRect(x, y, 86f, 99f, 15f, 6f, 4f));                         // head
                return Mathf.Max(Cov(d), Cov(Circle(x, y, 86f, 90f, 5f)));
            });
            var cone = new float[S * S];
            FillPolygon(cone, S, S, new List<Vector2> { new Vector2(74f, 90f), new Vector2(98f, 90f), new Vector2(122f, 14f), new Vector2(50f, 14f) });
            for (int i = 0; i < a.Length; i++) a[i] = Mathf.Max(a[i], cone[i] * 0.3f * Mathf.Clamp01((i / S - 14f) / 76f + 0.2f));
            return White("lamp", S, S, a, Vector4.zero);
        }

        // Echolocation: three arcs spreading to the right.
        static UiSpriteData Echo()
        {
            var a = Raster(128, 128, (x, y) =>
            {
                float dx = x - 18f, dy = y - 64f, r = Mathf.Sqrt(dx * dx + dy * dy);
                float ang = Mathf.Abs(Mathf.Atan2(dy, dx)) * Mathf.Rad2Deg;
                float edge = Cov((ang - 40f) * Mathf.Deg2Rad * r);   // soft angular edge
                float best = 0f;
                float[] radii = { 34f, 60f, 86f }, alpha = { 1f, 0.8f, 0.6f };
                for (int k = 0; k < 3; k++) best = Mathf.Max(best, Cov(Mathf.Abs(r - radii[k]) - 4f) * alpha[k]);
                return best * edge;
            });
            return White("echo", 128, 128, a, Vector4.zero);
        }
    }
}
