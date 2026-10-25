using UnityEngine;

namespace Edge.Feeding
{
    // The live "feeding buzz" voice of EdgeFeedingAudio: a click train generated in OnAudioFilterRead
    // on its own GameObject (the filter would otherwise also process the one-shot sources). The
    // owner writes rateHz / level / pan from the main thread; they are smoothed per sample here so
    // rate changes never click.
    [RequireComponent(typeof(AudioSource))]
    public class EdgeFeedingBuzz : MonoBehaviour
    {
        [System.NonSerialized] public volatile float rateHz = 12f;
        [System.NonSerialized] public volatile float level;
        [System.NonSerialized] public volatile float pan;

        float _sr = 48000f, _rate, _level, _pan, _env, _ph, _hz, _decay;
        double _phase;
        readonly System.Random _rng = new System.Random(91);

        void Awake()
        {
            _sr = AudioSettings.outputSampleRate;
            _decay = Mathf.Exp(-1f / (0.0011f * _sr));   // ~1.1 ms click ring
            var src = GetComponent<AudioSource>();
            src.playOnAwake = false; src.spatialBlend = 0f; src.volume = 1f; src.loop = true;
            // a silent carrier keeps the source playing so the filter keeps being called
            src.clip = AudioClip.Create("EdgeBuzzCarrier", 44100, 1, 44100, false);
            src.Play();
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            float tRate = rateHz, tLevel = level, tPan = pan;
            for (int i = 0; i < data.Length; i += channels)
            {
                _rate += (tRate - _rate) * 0.002f;
                _level += (tLevel - _level) * 0.001f;
                _pan += (tPan - _pan) * 0.001f;
                _phase += _rate / _sr;
                if (_phase >= 1.0)
                {
                    _phase -= 1.0;
                    if (_level > 1e-4f)
                    {
                        _env = 0.75f + 0.25f * (float)_rng.NextDouble();
                        _ph = 0f;
                        // clicks rise a little in pitch as the buzz tightens, like a heterodyne detector
                        _hz = 950f + _rate * 2.2f + 180f * (float)_rng.NextDouble();
                    }
                }

                float s = 0f;
                if (_env > 1e-4f)
                {
                    _ph += 2f * Mathf.PI * _hz / _sr;
                    s = _env * (0.85f * Mathf.Sin(_ph) + 0.15f * ((float)_rng.NextDouble() * 2f - 1f));
                    _env *= _decay;
                }
                s *= _level;
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
