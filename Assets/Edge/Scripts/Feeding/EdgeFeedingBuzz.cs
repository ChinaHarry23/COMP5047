using UnityEngine;

namespace Edge.Feeding
{
    // The live approach-call voice of EdgeFeedingAudio, generated in OnAudioFilterRead on its own
    // GameObject (the filter would otherwise also process the one-shot sources). The owner writes
    // closeness / level / pan from the main thread; EdgeBatCallSynth smooths them per sample.
    [RequireComponent(typeof(AudioSource))]
    public class EdgeFeedingBuzz : MonoBehaviour
    {
        [System.NonSerialized] public volatile float closeness;
        [System.NonSerialized] public volatile float level;
        [System.NonSerialized] public volatile float pan;

        EdgeBatCallSynth _synth;

        void Awake()
        {
            _synth = new EdgeBatCallSynth(AudioSettings.outputSampleRate);
            var src = GetComponent<AudioSource>();
            src.playOnAwake = false; src.spatialBlend = 0f; src.volume = 1f; src.loop = true;
            // a silent carrier keeps the source playing so the filter keeps being called
            src.clip = AudioClip.Create("EdgeBuzzCarrier", 44100, 1, 44100, false);
        }

        // (Re)start on every enable: a source stopped by deactivation would never call the filter again.
        void OnEnable()
        {
            var src = GetComponent<AudioSource>();
            if (src && src.clip && !src.isPlaying) src.Play();
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            _synth?.Render(data, channels, closeness, level, pan);
        }
    }

    // Approach calls as a time-expanded bat recording sounds to people: short downward FM chirps
    // ("tsip") instead of detector clicks. As the moth ahead closes in, the calls come faster, shorter
    // and shallower, ending in a soft terminal trill, and each call's echo (delayed, darker) slides in
    // towards the call: you can hear the distance close. Silent when nothing is being approached.
    // Plain C# (no engine calls), so it runs on the audio thread and can be rendered offline.
    public class EdgeBatCallSynth
    {
        readonly float _sr;
        readonly float[] _delay;          // dry call history for the echo
        int _w;
        float _c, _lvl, _pan, _echoD;     // smoothed closeness, level, pan, echo delay (samples)
        float _untilNext;                 // samples until the next call
        float _t, _dur, _f, _fMul, _ph;   // current call: elapsed / length (samples), frequency, sweep factor, phase
        float _echoLp, _outLp;
        readonly float _aEcho, _aOut;

        public EdgeBatCallSynth(float sampleRate)
        {
            _sr = Mathf.Max(8000f, sampleRate);
            _delay = new float[(int)(_sr * 0.25f)];
            _aEcho = 1f - Mathf.Exp(-2f * Mathf.PI * 2400f / _sr);
            _aOut = 1f - Mathf.Exp(-2f * Mathf.PI * 7500f / _sr);
            _echoD = 0.085f * _sr;
            _dur = 0f;
        }

        // Call rate (Hz) by closeness: search-to-approach 5 -> 18 Hz, then a terminal trill up to ~55 Hz.
        public static float RateHz(float c) => c < 0.7f ? 5f + 13f * c / 0.7f : 18f + 37f * Mathf.Pow((c - 0.7f) / 0.3f, 1.5f);

        public void Render(float[] data, int channels, float closeness, float level, float pan)
        {
            float tc = Mathf.Clamp01(closeness), tl = Mathf.Max(0f, level), tp = Mathf.Clamp(pan, -1f, 1f);
            float kSlow = 1f - Mathf.Exp(-1f / (0.06f * _sr)), kLvl = 1f - Mathf.Exp(-1f / (0.04f * _sr));
            for (int i = 0; i < data.Length; i += channels)
            {
                _c += (tc - _c) * kSlow;
                _lvl += (tl - _lvl) * kLvl;
                _pan += (tp - _pan) * kSlow;

                // schedule calls only while something is being approached and audible
                if (--_untilNext <= 0f)
                {
                    _untilNext = _sr / RateHz(_c);
                    if (_lvl > 1e-4f && _c > 0.02f)
                    {
                        float k = Mathf.Pow(_c, 0.8f);
                        _dur = Mathf.Lerp(0.024f, 0.006f, k) * _sr;
                        float f0 = Mathf.Lerp(5600f, 4200f, _c), f1 = Mathf.Lerp(2600f, 2900f, _c);
                        _f = f0;
                        _fMul = Mathf.Pow(f1 / f0, 1f / _dur);   // exponential sweep, one multiply per sample
                        _t = 0f;
                        _ph = 0f;
                    }
                }

                float dry = 0f;
                if (_t < _dur)
                {
                    float u = _t / _dur, w = Mathf.Sin(Mathf.PI * u);
                    _ph += 2f * Mathf.PI * _f / _sr;
                    if (_ph > 2f * Mathf.PI) _ph -= 2f * Mathf.PI;
                    dry = w * w * (Mathf.Sin(_ph) + 0.18f * Mathf.Sin(2f * _ph));
                    _f *= _fMul;
                    _t += 1f;
                }
                // terminal calls are tighter and a touch quieter, so the trill purrs rather than buzzes
                dry *= 1f - 0.25f * Mathf.Clamp01((_c - 0.7f) / 0.3f);

                // echo: the same call returning from the moth; the delay shrinks as it closes in
                _delay[_w] = dry;
                _echoD += (Mathf.Lerp(0.085f, 0.012f, _c) * _sr - _echoD) * kSlow;
                float rp = _w - _echoD;
                if (rp < 0f) rp += _delay.Length;
                int r0 = (int)rp, r1 = (r0 + 1) % _delay.Length;
                float echo = Mathf.Lerp(_delay[r0], _delay[r1], rp - r0);
                _echoLp += (echo - _echoLp) * _aEcho;
                _w = (_w + 1) % _delay.Length;

                float s = dry + 0.32f * _echoLp;
                _outLp += (s - _outLp) * _aOut;
                s = _outLp * _lvl;

                if (channels >= 2)
                {
                    float a = (_pan + 1f) * 0.25f * Mathf.PI;   // equal-power pan, centre = unity
                    data[i] = s * Mathf.Cos(a) * 1.4142f;
                    data[i + 1] = s * Mathf.Sin(a) * 1.4142f;
                    for (int c = 2; c < channels; c++) data[i + c] = 0f;
                }
                else data[i] = s;
            }
        }
    }
}
