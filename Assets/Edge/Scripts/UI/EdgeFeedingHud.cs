using UnityEngine;

namespace Edge.UI
{
    public enum HudScreen : byte { Start, Playing, Results }

    public struct RoundStats
    {
        public bool played;
        public int moths, best, darkStart, darkEnd, rivals;
        public float lightSeconds;
    }

    public struct BatCard
    {
        public string name, species, desc;
        public RoundStats stats;
    }

    // Everything the feeding-game screens show, as plain data (EdgeFeedingGame fills it each frame).
    public struct HudModel
    {
        public HudScreen screen;
        public float time;                        // unscaled seconds, for gentle pulses
        public BatCard tolerant, averse;
        public bool averseCurrent;                // which bat this round / selection is
        public int score;
        public float timeLeft, roundSeconds, hunger01, budget01, scorePulse01;
        public bool lightHurts, inLight, echo;
        public string hint;
        public float hintAlpha;
        public string endReason, verdict;
        public Vector3[] floaters;                // x, y (canvas units), age 0..1
        public int floaterCount;
        public string debug;
        public string subtitle;
        public float subtitleAlpha;
    }

    // The feeding game's screens as a pure layout: HudModel + canvas size -> UiNodes. No engine calls,
    // so the offline preview renders exactly what the game shows. Sizes are canvas units on a 1080-tall
    // reference (CanvasScaler 1920x1080), large enough to read on a projector across a dark room.
    public static class EdgeFeedingHud
    {
        public static readonly Color Ink = new Color(0.92f, 0.93f, 0.97f);
        public static readonly Color Muted = new Color(0.66f, 0.7f, 0.82f);
        public static readonly Color MoonBlue = new Color(0.62f, 0.83f, 1f);
        public static readonly Color Amber = new Color(1f, 0.75f, 0.4f);
        public static readonly Color Danger = new Color(1f, 0.5f, 0.36f);
        public static readonly Color Energy = new Color(0.5f, 0.88f, 0.64f);
        public static readonly Color Warm = new Color(1f, 0.9f, 0.7f);
        public const string Guideline = "Keep corridors dark. Where light is needed, use less of it: warmer, pointed down, shielded, and on for less time.";
        public const string GuidelineSource = "National Light Pollution Guidelines for Wildlife";

        static Color A(Color c, float a) { c.a *= a; return c; }
        static Rect R(float x, float y, float w, float h) => new Rect(x, y, w, h);

        public static void Build(UiList o, in HudModel m, float W, float H)
        {
            switch (m.screen)
            {
                case HudScreen.Playing: Playing(o, m, W, H); break;
                case HudScreen.Start: Start(o, m, W, H); break;
                default: Results(o, m, W, H); break;
            }
            Subtitle(o, m, W, H);
        }

        // ---- shared pieces ------------------------------------------------------------------
        static void Panel(UiList o, string id, Rect r, float alpha = 1f) => o.Image(id, r, "panel", A(Color.white, alpha), 1.4f);

        static void Key(UiList o, string id, float x, float y, string label, float h = 40f)
        {
            float w = Mathf.Max(h, 18f + label.Length * h * 0.32f);
            o.Image(id + ".cap", R(x, y, w, h), "key", A(Ink, 0.85f), 1.4f);
            o.Text(id + ".t", R(x, y, w, h), label, UiFont.BodyBold, (int)(h * 0.45f), Ink, TextAnchor.MiddleCenter, false);
        }

        static float KeyWidth(string label, float h = 40f) => Mathf.Max(h, 18f + label.Length * h * 0.32f);

        static void Bar(UiList o, string id, Rect r, float v, Color c)
        {
            o.Image(id + ".track", r, "pill", A(Color.white, 0.12f), 1f);
            float fw = r.width * Mathf.Clamp01(v);
            if (fw > 1f) o.Image(id + ".fill", R(r.x, r.y, Mathf.Max(fw, r.height), r.height), "pill", c, 1f);
        }

        static string Plural(int n, string one, string many) => n == 1 ? one : many;

        // ---- in flight ----------------------------------------------------------------------
        static void Playing(UiList o, in HudModel m, float W, float H)
        {
            // top-right: night ring (time left), moths caught, energy and (light-averse) light budget
            float pw = 470f, ph = m.lightHurts ? 178f : 142f, px = W - pw - 32f, py = 28f;
            Panel(o, "hud.panel", R(px, py, pw, ph), 0.92f);
            float rx = px + 22f, ry = py + 22f, rs = 98f;
            float night = m.roundSeconds > 0f ? Mathf.Clamp01(m.timeLeft / m.roundSeconds) : 0f;
            o.Image("hud.ring.track", R(rx, ry, rs, rs), "ring", A(Color.white, 0.12f));
            o.Image("hud.ring", R(rx, ry, rs, rs), "ring", night < 0.2f ? Amber : MoonBlue, 0f, night);
            o.Text("hud.time", R(rx, ry + 16f, rs, 46f), Mathf.CeilToInt(m.timeLeft).ToString(), UiFont.Title, 34, Ink, TextAnchor.MiddleCenter);
            o.Text("hud.time.l", R(rx, ry + 54f, rs, 22f), "sec", UiFont.Body, 16, Muted, TextAnchor.MiddleCenter, false);

            float sx = rx + rs + 22f, pulse = Mathf.Clamp01(m.scorePulse01);
            float icon = 46f + 10f * pulse;
            o.Image("hud.moth", R(sx - 5f * pulse, py + 26f - 5f * pulse, icon, icon), "moth", Color.Lerp(Warm, Color.white, pulse));
            o.Text("hud.score", R(sx + 56f, py + 14f, 120f, 66f), m.score.ToString(), UiFont.Title, (int)(48f + 12f * pulse), Color.Lerp(Ink, Warm, pulse), TextAnchor.MiddleLeft);
            o.Text("hud.score.l", R(sx + 56f + 22f * Mathf.Max(1, m.score.ToString().Length) + 12f, py + 36f, 140f, 34f),
                   Plural(m.score, "moth", "moths"), UiFont.Body, 22, Muted, TextAnchor.MiddleLeft, false);

            float bx = sx, bw = px + pw - 26f - sx, by = py + 92f;
            Color energy = m.hunger01 < 0.25f ? Color.Lerp(Danger, Energy, m.hunger01 / 0.25f) : Energy;
            o.Text("hud.energy.l", R(bx, by - 4f, bw, 22f), "Energy", UiFont.Body, 17, Muted, TextAnchor.UpperLeft, false);
            Bar(o, "hud.energy", R(bx, by + 20f, bw, 14f), m.hunger01, energy);
            if (m.lightHurts)
            {
                by += 40f;
                float glow = m.inLight ? 0.75f + 0.25f * Mathf.Sin(m.time * 2f * Mathf.PI * 0.8f) : 0f;   // slow, never a strobe
                Color budget = m.inLight ? Color.Lerp(Amber, Danger, 0.5f + 0.5f * glow) : Amber;
                o.Text("hud.budget.l", R(bx, by - 4f, bw, 22f), m.inLight ? "Night vision - in the light!" : "Night vision", UiFont.Body, 17,
                       m.inLight ? Color.Lerp(Muted, Danger, glow) : Muted, TextAnchor.UpperLeft, false);
                Bar(o, "hud.budget", R(bx, by + 20f, bw, 14f), m.budget01, budget);
            }

            if (!string.IsNullOrEmpty(m.debug))
            {
                Panel(o, "hud.debug.panel", R(W - 760f, py + ph + 12f, 728f, 76f), 0.8f);
                o.Text("hud.debug", R(W - 744f, py + ph + 20f, 700f, 60f), m.debug, UiFont.Body, 15, Muted, TextAnchor.UpperLeft, false);
            }

            // catches: a moth and +1 rising from where it was eaten
            for (int i = 0; i < m.floaterCount && m.floaters != null && i < m.floaters.Length; i++)
            {
                Vector3 f = m.floaters[i];
                float a = 1f - f.z * f.z, rise = 56f * f.z;
                o.Image("fl.m" + i, R(f.x - 46f, f.y - 30f - rise, 30f, 30f), "moth", A(Warm, a));
                o.Text("fl.t" + i, R(f.x - 14f, f.y - 38f - rise, 80f, 44f), "+1", UiFont.Title, 32, A(Warm, a), TextAnchor.MiddleLeft);
            }

            // round hint, bottom centre, fading out
            if (m.hintAlpha > 0.01f && !string.IsNullOrEmpty(m.hint))
            {
                float hw = Mathf.Min(W - 120f, 1140f), hh = 74f, hx = (W - hw) * 0.5f, hy = H - 236f;
                Panel(o, "hint.panel", R(hx, hy, hw, hh), 0.9f * m.hintAlpha);
                o.Image("hint.bat", R(hx + 22f, hy + 17f, 80f, 40f), "bat", A(m.averseCurrent ? MoonBlue : Amber, m.hintAlpha));
                float textW = hw - 130f - (m.echo ? 220f : 0f);
                o.Text("hint.t", R(hx + 120f, hy, textW, hh), m.hint, UiFont.Body, 25, A(Ink, m.hintAlpha), TextAnchor.MiddleLeft);
                if (m.echo)
                {
                    float kx = hx + hw - 214f;
                    Key(o, "hint.key", kx, hy + 17f, "E");
                    o.Text("hint.echo", R(kx + 50f, hy, 160f, hh), "echolocate", UiFont.Body, 22, A(Muted, m.hintAlpha), TextAnchor.MiddleLeft, false);
                }
            }
        }

        // ---- start screen -------------------------------------------------------------------
        static void Start(UiList o, in HudModel m, float W, float H)
        {
            float pw = Mathf.Min(1080f, W - 80f), ph = 660f, px = (W - pw) * 0.5f, py = (H - ph) * 0.5f;
            Panel(o, "start.panel", R(px, py, pw, ph), 0.95f);
            float cx = W * 0.5f;
            o.Image("start.glow", R(cx - 230f, py - 40f, 460f, 280f), "glow", A(MoonBlue, 0.22f));
            o.Image("start.moon", R(cx + 70f, py + 26f, 86f, 86f), "moon", A(Warm, 0.9f));
            o.Image("start.bat", R(cx - 120f, py + 56f, 240f, 120f), "bat", Ink);
            o.Text("start.title", R(px, py + 178f, pw, 80f), "NIGHT FEEDING", UiFont.Title, 64, Ink, TextAnchor.MiddleCenter);
            o.Text("start.tag", R(px, py + 252f, pw, 40f), "Two bats, one lit path. Who gets to eat?", UiFont.Body, 26, Muted, TextAnchor.MiddleCenter);
            o.Image("start.rule", R(cx - 320f, py + 306f, 640f, 6f), "line", A(MoonBlue, 0.55f));

            float cw = (pw - 108f) * 0.5f, ch = 196f, cy = py + 334f;
            ChoiceCard(o, "start.tol", R(px + 44f, cy, cw, ch), "1", m.tolerant, Amber, !m.averseCurrent);
            ChoiceCard(o, "start.av", R(px + 64f + cw, cy, cw, ch), "2", m.averse, MoonBlue, m.averseCurrent);

            // how to play, then the call to start (a slow breathing pulse)
            o.Text("start.how", R(px, py + 548f, pw, 34f), "Tilt the bat to fly. Catch moths before your energy runs out.", UiFont.Body, 23, Muted, TextAnchor.MiddleCenter);
            float breathe = 0.7f + 0.3f * Mathf.Sin(m.time * 2f * Mathf.PI * 0.5f);
            string go = "to start";
            float kw = KeyWidth("SPACE", 44f), total = kw + 14f + 120f;
            Key(o, "start.key", cx - total * 0.5f, py + 592f, "SPACE", 44f);
            o.Text("start.go", R(cx - total * 0.5f + kw + 14f, py + 592f, 200f, 44f), go, UiFont.BodyBold, 24, A(Ink, breathe), TextAnchor.MiddleLeft);
        }

        static void ChoiceCard(UiList o, string id, Rect r, string key, in BatCard b, Color tint, bool selected)
        {
            o.Image(id + ".card", r, "card", A(tint, selected ? 1f : 0.45f), 1.3f);
            if (selected) o.Image(id + ".glow", R(r.x + 10f, r.y - 30f, 150f, 120f), "glow", A(tint, 0.25f));
            Key(o, id + ".key", r.x + 18f, r.y + 18f, key);
            o.Image(id + ".bat", R(r.x + 70f, r.y + 22f, 88f, 44f), "bat", A(tint, selected ? 1f : 0.6f));
            if (selected) o.Text(id + ".sel", R(r.xMax - 150f, r.y + 18f, 132f, 30f), "selected", UiFont.Body, 17, A(tint, 0.9f), TextAnchor.MiddleRight, false);
            o.Text(id + ".name", R(r.x + 22f, r.y + 76f, r.width - 44f, 40f), b.name, UiFont.Title, 27, selected ? Ink : A(Ink, 0.75f), TextAnchor.MiddleLeft);
            o.Text(id + ".sp", R(r.x + 22f, r.y + 114f, r.width - 44f, 26f), b.species, UiFont.BodyBold, 19, A(tint, selected ? 1f : 0.7f), TextAnchor.MiddleLeft, false);
            o.Text(id + ".desc", R(r.x + 22f, r.y + 142f, r.width - 44f, 48f), b.desc, UiFont.Body, 18, A(Muted, selected ? 1f : 0.75f), TextAnchor.UpperLeft, false);
        }

        // ---- results ------------------------------------------------------------------------
        static void Results(UiList o, in HudModel m, float W, float H)
        {
            float pw = Mathf.Min(1280f, W - 60f), ph = 820f, px = (W - pw) * 0.5f, py = (H - ph) * 0.5f;
            Panel(o, "res.panel", R(px, py, pw, ph), 0.96f);
            float cx = W * 0.5f;
            BatCard cur = m.averseCurrent ? m.averse : m.tolerant;
            Color curTint = m.averseCurrent ? MoonBlue : Amber;
            o.Image("res.glow", R(cx - 260f, py - 30f, 520f, 200f), "glow", A(curTint, 0.18f));
            o.Text("res.who", R(px, py + 26f, pw, 32f), cur.name + "  ·  " + m.endReason, UiFont.Body, 23, Muted, TextAnchor.MiddleCenter);
            o.Image("res.moth", R(cx - 150f, py + 70f, 64f, 64f), "moth", Warm);
            o.Text("res.score", R(cx - 76f, py + 58f, 400f, 86f), m.score + " " + Plural(m.score, "moth", "moths"), UiFont.Title, 60, Ink, TextAnchor.MiddleLeft);
            o.Image("res.rule", R(cx - 360f, py + 152f, 720f, 6f), "line", A(MoonBlue, 0.5f));

            int maxMoths = Mathf.Max(1, Mathf.Max(m.tolerant.stats.moths, m.averse.stats.moths));
            float cw = (pw - 108f) * 0.5f, ch = 360f, cy = py + 178f;
            ResultCard(o, "res.tol", R(px + 44f, cy, cw, ch), "1", m.tolerant, Amber, !m.averseCurrent, maxMoths);
            ResultCard(o, "res.av", R(px + 64f + cw, cy, cw, ch), "2", m.averse, MoonBlue, m.averseCurrent, maxMoths);

            float vy = cy + ch + 20f;
            o.Image("res.verdict.bg", R(px + 44f, vy, pw - 88f, 84f), "card", A(Warm, 0.55f), 1.3f);
            o.Text("res.verdict", R(px + 70f, vy, pw - 140f, 84f), m.verdict, UiFont.BodyBold, 23, Warm, TextAnchor.MiddleCenter);

            float gy = vy + 102f;
            o.Image("res.lamp", R(px + 64f, gy + 2f, 48f, 48f), "lamp", A(Amber, 0.9f));
            o.Text("res.guide", R(px + 124f, gy, pw - 190f, 34f), Guideline, UiFont.Body, 21, Amber, TextAnchor.UpperLeft, false);
            o.Text("res.src", R(px + 124f, gy + 30f, pw - 190f, 26f), GuidelineSource, UiFont.Body, 17, A(Amber, 0.7f), TextAnchor.UpperLeft, false);

            // footer: what to press next
            float fy = py + ph - 62f, k1 = KeyWidth("SPACE");
            float total = k1 + 10f + 110f + 40f + 40f + 8f + 40f + 10f + 130f;
            float fx = cx - total * 0.5f;
            Key(o, "res.k.space", fx, fy, "SPACE"); fx += k1 + 10f;
            o.Text("res.k.replay", R(fx, fy, 110f, 40f), "play again", UiFont.Body, 20, Muted, TextAnchor.MiddleLeft, false); fx += 150f;
            Key(o, "res.k.1", fx, fy, "1"); fx += 48f;
            Key(o, "res.k.2", fx, fy, "2"); fx += 50f;
            o.Text("res.k.switch", R(fx, fy, 160f, 40f), "choose a bat", UiFont.Body, 20, Muted, TextAnchor.MiddleLeft, false);
        }

        static void ResultCard(UiList o, string id, Rect r, string key, in BatCard b, Color tint, bool current, int maxMoths)
        {
            o.Image(id + ".card", r, "card", A(tint, current ? 1f : 0.5f), 1.3f);
            o.Image(id + ".bat", R(r.x + 22f, r.y + 20f, 84f, 42f), "bat", tint);
            o.Text(id + ".name", R(r.x + 120f, r.y + 14f, r.width - 140f, 36f), b.name, UiFont.Title, 26, Ink, TextAnchor.MiddleLeft);
            o.Text(id + ".sp", R(r.x + 120f, r.y + 48f, r.width - 140f, 24f), b.species, UiFont.Body, 18, A(tint, 0.9f), TextAnchor.MiddleLeft, false);
            if (current) o.Text(id + ".cur", R(r.xMax - 170f, r.y + 14f, 150f, 26f), "this round", UiFont.BodyBold, 16, tint, TextAnchor.MiddleRight, false);

            if (!b.stats.played)
            {
                o.Text(id + ".none", R(r.x, r.y + 140f, r.width, 40f), "Not played yet", UiFont.Body, 24, Muted, TextAnchor.MiddleCenter);
                float kw = KeyWidth(key), total = 64f + kw + 10f + 120f, kx = r.center.x - total * 0.5f;
                o.Text(id + ".press", R(kx, r.y + 190f, 64f, 40f), "press", UiFont.Body, 20, Muted, TextAnchor.MiddleLeft, false);
                Key(o, id + ".k", kx + 64f, r.y + 190f, key);
                o.Text(id + ".try", R(kx + 64f + kw + 10f, r.y + 190f, 140f, 40f), "to try it", UiFont.Body, 20, Muted, TextAnchor.MiddleLeft, false);
                return;
            }
            var s = b.stats;
            float y = r.y + 92f, lx = r.x + 74f, vw = 150f, vx = r.xMax - 22f - vw;
            // moths, with a bar against the better of the two bats
            o.Image(id + ".i0", R(r.x + 24f, y + 4f, 36f, 36f), "moth", Warm);
            o.Text(id + ".l0", R(lx, y, vx - lx, 44f), "Moths eaten", UiFont.Body, 21, Muted, TextAnchor.MiddleLeft, false);
            o.Text(id + ".v0", R(vx, y - 4f, vw, 50f), s.moths.ToString(), UiFont.Title, 36, Ink, TextAnchor.MiddleRight);
            Bar(o, id + ".bar", R(lx, y + 46f, r.xMax - 22f - lx, 10f), s.moths / (float)maxMoths, A(tint, 0.95f));
            o.Text(id + ".best", R(lx, y + 58f, r.xMax - 22f - lx, 24f), "today's best " + s.best, UiFont.Body, 16, A(Muted, 0.85f), TextAnchor.MiddleRight, false);
            y += 92f;
            Stat(o, id + ".s1", r, y, "lamp", Amber, "Time in the light", Mathf.RoundToInt(s.lightSeconds) + " s");
            y += 56f;
            Stat(o, id + ".s2", r, y, "moon", MoonBlue, "Moths left in the dark", s.darkEnd + " of " + s.darkStart);
            y += 56f;
            Stat(o, id + ".s3", r, y, "bat", Amber, "Taken at the lamps by other bats", s.rivals.ToString());
        }

        static void Stat(UiList o, string id, Rect r, float y, string icon, Color iconTint, string label, string value)
        {
            float lx = r.x + 74f, vw = 140f, vx = r.xMax - 22f - vw;
            o.Image(id + ".i", R(r.x + 24f, y + 6f, icon == "bat" ? 40f : 34f, icon == "bat" ? 20f : 34f), icon, iconTint);
            o.Text(id + ".l", R(lx, y, vx - lx, 44f), label, UiFont.Body, 20, Muted, TextAnchor.MiddleLeft, false);
            o.Text(id + ".v", R(vx, y, vw, 44f), value, UiFont.BodyBold, 24, Ink, TextAnchor.MiddleRight);
        }

        // ---- narrator subtitle (bottom centre) -------------------------------------------------
        static void Subtitle(UiList o, in HudModel m, float W, float H)
        {
            if (m.subtitleAlpha <= 0.01f || string.IsNullOrEmpty(m.subtitle)) return;
            float sw = Mathf.Min(W - 120f, 1180f), sh = 92f, sx = (W - sw) * 0.5f, sy = H - sh - 40f;
            Panel(o, "sub.panel", R(sx, sy, sw, sh), 0.9f * m.subtitleAlpha);
            o.Image("sub.echo", R(sx + 22f, sy + 22f, 48f, 48f), "echo", A(MoonBlue, 0.85f * m.subtitleAlpha));
            o.Text("sub.t", R(sx + 86f, sy, sw - 116f, sh), m.subtitle, UiFont.Body, 26, A(Ink, m.subtitleAlpha), TextAnchor.MiddleLeft);
        }
    }
}
