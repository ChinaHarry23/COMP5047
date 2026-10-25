using UnityEngine;

namespace Edge.Feeding
{
    // Feeding sounds for EdgeFeedingGame, synthesised once at start (no audio assets needed).
    //  - Crunch: a chitin snap, a run of band-passed crackle grains and a soft pitched-down chew,
    //    rendered as several variants that are never repeated back to back, panned towards the side
    //    the moth was on. Drop recorded clips into eatClips to use those instead.
    //  - Streak: a soft pentatonic blip that climbs while moths are eaten in quick succession.
    //  - Feeding buzz: what a bat detector plays as a bat closes on prey. A click train whose rate
    //    climbs from ~12 Hz (approach) to ~170 Hz (terminal buzz) as the nearest moth ahead closes in.
    //    Generated live in OnAudioFilterRead so the rate follows the distance smoothly.
    //  - Ping: a quiet three-click chirp for the echolocation cue.
    // All one-shots are 2D (the listener is the bat); level and pan are set per play.
    public class EdgeFeedingAudio : MonoBehaviour
    {
        [Tooltip("Optional recorded eat sounds; empty = synthesised crunch variants")]
        public AudioClip[] eatClips;
        [Range(1, 12)] public int crunchVariants = 6;
        [Range(0f, 1f)] public float eatVolume = 0.55f;
        [Tooltip("Random pitch spread per crunch (+/- fraction). Small: big shifts sound like a different object")]
        [Range(0f, 0.2f)] public float pitchJitter = 0.06f;
        [Range(0f, 1f)] public float stereoWidth = 0.55f;

        [Header("Streak blip")]
        [Range(0f, 1f)] public float streakVolume = 0.2f;
        [Tooltip("Eats closer together than this (s) climb the pentatonic scale")]
        public float streakWindow = 1.8f;
        public float streakBaseHz = 660f;

        [Header("Feeding buzz (approach)")]
        [Range(0f, 1f)] public float buzzVolume = 0.14f;
        public Vector2 buzzRateHz = new Vector2(12f, 170f);

        [Header("Echolocation ping")]
        [Range(0f, 1f)] public float pingVolume = 0.1f;

        public int Streak { get; private set; }

        const int Sr = 44100;
        static readonly float[] Pentatonic = { 1f, 9f / 8f, 5f / 4f, 3f / 2f, 5f / 3f, 2f, 9f / 4f };

        AudioClip[] _crunch;
        AudioClip[] _blips;
        AudioClip _ping;
        AudioSource[] _pool;
        int _next, _lastCrunch = -1;
        float _lastEat = -100f;

        EdgeFeedingBuzz _buzz;

        // Built on first use (or Start), not Awake: the owner sets eatClips right after AddComponent.
        void Start() => Build();

        void Build()
        {
            if (_pool != null) return;
            BuildCrunches();
            _blips = new AudioClip[Pentatonic.Length];
            for (int i = 0; i < _blips.Length; i++)
                _blips[i] = MakeClip("EdgeStreak" + i, SynthBlip(streakBaseHz * Pentatonic[i]));
            _ping = MakeClip("EdgePing", SynthPing());

            // one voice per sound: a still-playing crunch must not pick up the next one's pitch or pan
            _pool = new AudioSource[8];
            for (int i = 0; i < _pool.Length; i++) _pool[i] = NewSource();

            // The buzz runs in OnAudioFilterRead, which taps every source on its GameObject, so it
            // gets a child object of its own instead of sharing this one with the one-shot pool.
            var go = new GameObject("EdgeFeedingBuzz");
            go.transform.SetParent(transform, false);
            _buzz = go.AddComponent<EdgeFeedingBuzz>();
        }

        void BuildCrunches()
        {
            var given = new System.Collections.Generic.List<AudioClip>();
            if (eatClips != null) foreach (var c in eatClips) if (c) given.Add(c);
            if (given.Count > 0) { _crunch = given.ToArray(); return; }
            _crunch = new AudioClip[Mathf.Max(1, crunchVariants)];
            for (int i = 0; i < _crunch.Length; i++)
                _crunch[i] = MakeClip("EdgeCrunch" + i, SynthCrunch(1000 + 37 * i));
        }

        /// Use recorded eat sounds (null entries ignored); empty = the synthesised crunches.
        public void SetEatClips(AudioClip[] clips)
        {
            eatClips = clips;
            if (_pool != null) BuildCrunches();
        }

        AudioSource NewSource()
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false; s.spatialBlend = 0f; s.volume = 1f;
            return s;
        }

        // A free voice if there is one, else the oldest is stolen.
        void Play(AudioClip clip, float volume, float pitch, float pan)
        {
            AudioSource s = null;
            for (int k = 0; k < _pool.Length && !s; k++)
            {
                var c = _pool[(_next + k) % _pool.Length];
                if (!c.isPlaying) s = c;
            }
            if (!s) s = _pool[_next];
            _next = (System.Array.IndexOf(_pool, s) + 1) % _pool.Length;
            s.clip = clip; s.volume = volume; s.pitch = pitch; s.panStereo = pan;
            s.Play();
        }

        // ---- API used by EdgeFeedingGame -------------------------------------------------------
        /// pan: -1 = moth was on the bat's left, +1 = right.
        public void PlayEat(float pan)
        {
            Build();
            if (_crunch == null || _crunch.Length == 0) return;
            int k = Random.Range(0, _crunch.Length);
            if (_crunch.Length > 1 && k == _lastCrunch) k = (k + 1 + Random.Range(0, _crunch.Length - 1)) % _crunch.Length;
            _lastCrunch = k;
            float p = Mathf.Clamp(pan, -1f, 1f) * stereoWidth;
            Play(_crunch[k], eatVolume, 1f + Random.Range(-pitchJitter, pitchJitter), p);

            float now = Time.time;
            Streak = now - _lastEat <= streakWindow ? Streak + 1 : 1;
            _lastEat = now;
            if (streakVolume > 0f)
            {
                int note = Mathf.Min(Streak - 1, _blips.Length - 1);
                Play(_blips[note], streakVolume * (Streak == 1 ? 0.6f : 1f), 1f, p * 0.5f);
            }
        }

        /// closeness: 0 = nothing near, 1 = moth at the mouth. pan as for PlayEat.
        public void SetApproach(float closeness, float pan)
        {
            Build();
            if (!_buzz) return;
            closeness = Mathf.Clamp01(closeness);
            _buzz.rateHz = Mathf.Lerp(buzzRateHz.x, buzzRateHz.y, closeness * closeness);
            _buzz.level = closeness <= 0f ? 0f : buzzVolume * Mathf.SmoothStep(0f, 1f, closeness / 0.2f);
            _buzz.pan = Mathf.Clamp(pan, -1f, 1f) * stereoWidth;
        }

        public void PlayPing()
        {
            Build();
            if (pingVolume <= 0f || !_ping) return;
            Play(_ping, pingVolume, 1f, 0f);
        }

        public void ResetStreak() { Streak = 0; _lastEat = -100f; }

        // ---- synthesis -------------------------------------------------------------------------
        static AudioClip MakeClip(string name, float[] d)
        {
            var c = AudioClip.Create(name, d.Length, 1, Sr, false);
            c.SetData(d, 0);
            return c;
        }

        struct Biquad
        {
            float b0, b1, b2, a1, a2, z1, z2;

            static Biquad Make(float b0, float b1, float b2, float a0, float a1, float a2) =>
                new Biquad { b0 = b0 / a0, b1 = b1 / a0, b2 = b2 / a0, a1 = a1 / a0, a2 = a2 / a0 };

            // RBJ cookbook filters
            public static Biquad BandPass(float hz, float q)
            {
                float w = 2f * Mathf.PI * hz / Sr, al = Mathf.Sin(w) / (2f * q), c = Mathf.Cos(w);
                return Make(al, 0f, -al, 1f + al, -2f * c, 1f - al);
            }

            public static Biquad LowPass(float hz, float q)
            {
                float w = 2f * Mathf.PI * hz / Sr, al = Mathf.Sin(w) / (2f * q), c = Mathf.Cos(w);
                return Make((1f - c) * 0.5f, 1f - c, (1f - c) * 0.5f, 1f + al, -2f * c, 1f - al);
            }

            public static Biquad HighPass(float hz, float q)
            {
                float w = 2f * Mathf.PI * hz / Sr, al = Mathf.Sin(w) / (2f * q), c = Mathf.Cos(w);
                return Make((1f + c) * 0.5f, -(1f + c), (1f + c) * 0.5f, 1f + al, -2f * c, 1f - al);
            }

            public float Run(float x)
            {
                float y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
        }

        static float Env(float t, float attack, float tau) => (1f - Mathf.Exp(-t / attack)) * Mathf.Exp(-t / tau);

        // Exposed for offline rendering/tests.
        public static float[] SynthCrunch(int seed)
        {
            var r = new System.Random(seed);
            float Rnd() => (float)r.NextDouble();
            float Noise() => Rnd() * 2f - 1f;
            int n = (int)(Sr * 0.17f);
            var d = new float[n];

            // 1) snap: the first bite through the thorax, a few ms of bright noise
            var hp = Biquad.HighPass(2600f, 0.7f);
            float snapAmp = 0.7f + 0.3f * Rnd();
            for (int i = 0; i < (int)(Sr * 0.014f); i++)
            {
                float t = i / (float)Sr;
                d[i] += snapAmp * Env(t, 0.0003f, 0.0024f) * hp.Run(Noise());
            }

            // 2) crackle: 4-7 short resonant grains spread over ~80 ms, each fainter and a little
            //    lower than the last (wings and legs breaking up)
            int grains = 4 + r.Next(4);
            float tg = 0.006f + Rnd() * 0.006f;
            float fTop = Mathf.Lerp(3000f, 4600f, Rnd());
            for (int g = 0; g < grains; g++)
            {
                float k = g / (float)Mathf.Max(1, grains - 1);
                float hz = fTop * Mathf.Lerp(1f, 0.45f, k) * (0.85f + 0.3f * Rnd());
                var bp = Biquad.BandPass(hz, 1.4f + Rnd() * 2.4f);
                float amp = Mathf.Lerp(1f, 0.35f, k) * (0.6f + 0.4f * Rnd()) * 2.4f;
                float tau = 0.002f + Rnd() * 0.005f;
                int i0 = (int)(tg * Sr), len = (int)(tau * 7f * Sr);
                for (int j = 0; j < len && i0 + j < n; j++)
                    d[i0 + j] += amp * Env(j / (float)Sr, 0.0004f, tau) * bp.Run(Noise());
                tg += 0.006f + Rnd() * 0.017f;
            }

            // 3) chew: a soft pitched-down thump plus muffled noise, so it has some body
            var lp = Biquad.LowPass(850f, 0.7f);
            float f0 = 190f + Rnd() * 70f, ph = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Sr;
                float hz = Mathf.Lerp(f0, f0 * 0.45f, Mathf.Clamp01(t / 0.06f));
                ph += 2f * Mathf.PI * hz / Sr;
                float e = Env(t, 0.002f, 0.026f);
                d[i] += e * (0.4f * Mathf.Sin(ph) + 0.28f * lp.Run(Noise()));
            }
            var air = Biquad.LowPass(8500f, 0.7f);   // take the fizz off the top
            for (int i = 0; i < n; i++) d[i] = air.Run(d[i]);
            Finish(d, 0.9f, 0.012f);
            return d;
        }

        public static float[] SynthBlip(float hz)
        {
            int n = (int)(Sr * 0.2f);
            var d = new float[n];
            float ph = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Sr;
                ph += 2f * Mathf.PI * hz * (1f + 0.012f * Mathf.Clamp01(t / 0.05f)) / Sr;   // slight upward lift
                float e = Env(t, 0.004f, 0.045f);
                d[i] = e * (Mathf.Sin(ph) + 0.22f * Mathf.Sin(2f * ph) + 0.06f * Mathf.Sin(3f * ph));
            }
            Finish(d, 0.8f, 0.03f);
            return d;
        }

        public static float[] SynthPing()
        {
            int n = (int)(Sr * 0.12f);
            var d = new float[n];
            for (int c = 0; c < 3; c++)
            {
                int i0 = (int)((0.004f + 0.028f * c) * Sr);
                float hz = 2300f - 250f * c, amp = 1f - 0.25f * c, ph = 0f;
                for (int j = 0; i0 + j < n && j < Sr * 0.012f; j++)
                {
                    ph += 2f * Mathf.PI * hz / Sr;
                    d[i0 + j] += amp * Env(j / (float)Sr, 0.0002f, 0.0016f) * Mathf.Sin(ph);
                }
            }
            Finish(d, 0.7f, 0.01f);
            return d;
        }

        // DC block, gentle saturation, de-click fades, peak-normalise.
        static void Finish(float[] d, float peak, float fadeOut)
        {
            var dc = Biquad.HighPass(35f, 0.7f);
            for (int i = 0; i < d.Length; i++) d[i] = (float)System.Math.Tanh(1.3f * dc.Run(d[i]));
            int fi = (int)(Sr * 0.0008f), fo = (int)(Sr * fadeOut);
            for (int i = 0; i < fi && i < d.Length; i++) d[i] *= i / (float)fi;
            for (int i = 0; i < fo && i < d.Length; i++) d[d.Length - 1 - i] *= i / (float)fo;
            float m = 1e-6f;
            foreach (float v in d) m = Mathf.Max(m, Mathf.Abs(v));
            float g = peak / m;
            for (int i = 0; i < d.Length; i++) d[i] *= g;
        }
    }
}
