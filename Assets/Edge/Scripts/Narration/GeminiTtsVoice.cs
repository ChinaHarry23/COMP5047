using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Edge.Narration
{
    // Gemini 3.8 TTS -> raw 24 kHz PCM -> AudioClip on an AudioSource.
    // One fixed voice for the whole session: on failure the line is retried once after a
    // back-off, then shown subtitle-only (logged). It never switches to a different voice
    // unless fallbackToOtherVoice is explicitly enabled.
    public class GeminiTtsVoice : INarrationVoice
    {
        readonly GeminiClient _client;
        readonly AudioSource _src;
        readonly INarrationVoice _fallback;
        readonly Dictionary<string, AudioClip> _cache = new Dictionary<string, AudioClip>();
        bool _pending;
        int _token;

        public bool fallbackToOtherVoice;   // false = subtitle-only on failure (keeps one voice)
        public float retryDelaySeconds = 3f;
        public bool cacheAudio = true;

        public GeminiTtsVoice(GeminiClient client, AudioSource src, INarrationVoice fallback, bool fallbackToOtherVoice = false)
        {
            _client = client;
            _src = src;
            _fallback = fallback ?? new SilentVoice();
            this.fallbackToOtherVoice = fallbackToOtherVoice;
        }

        string CacheKey(string text) => _client.ttsVoice + "|" + text;

        public bool IsSpeaking => _pending || (_src && _src.isPlaying) || _fallback.IsSpeaking;

        public void Speak(string text)
        {
            Stop();
            if (string.IsNullOrWhiteSpace(text)) return;
            // keyed by voice too: each bat has its own voice, and the same words must not replay in the other one
            if (cacheAudio && _cache.TryGetValue(CacheKey(text), out var cached) && cached) { Play(cached, false); return; }
            int tok = ++_token;
            _pending = true;
            Attempt(text, tok, 0);
        }

        void Attempt(string text, int tok, int attempt)
        {
            _client.Synthesize(text, (samples, rate) =>
            {
                if (tok != _token) return;   // superseded
                _pending = false;
                if (!_src) return;
                var clip = AudioClip.Create("EDGE narration", samples.Length, 1, rate, false);
                clip.SetData(samples, 0);
                if (cacheAudio) _cache[CacheKey(text)] = clip;
                Play(clip, !cacheAudio);
            }, err =>
            {
                if (tok != _token) return;
                if (attempt == 0)
                {
                    Debug.LogWarning($"[EDGE Narration] Gemini TTS failed ({err}); retrying once in {retryDelaySeconds:0.#} s (voice {_client.ttsVoice})");
                    RetryLater(text, tok);
                    return;
                }
                _pending = false;
                if (fallbackToOtherVoice)
                {
                    Debug.LogWarning($"[EDGE Narration] Gemini TTS failed again ({err}); using fallback voice");
                    _fallback.Speak(text);
                }
                else Debug.LogWarning($"[EDGE Narration] Gemini TTS failed again ({err}); line shown subtitle-only (voice kept consistent)");
            });
        }

        async void RetryLater(string text, int tok)
        {
            float until = Time.realtimeSinceStartup + retryDelaySeconds;
            // wait the back-off, plus any in-flight request (max ~15 s extra)
            while (Time.realtimeSinceStartup < until || (_client.busy && Time.realtimeSinceStartup < until + 15f))
            {
                await Task.Delay(250);
                if (tok != _token) return;
            }
            Attempt(text, tok, 1);
        }

        void Play(AudioClip clip, bool destroyOld)
        {
            if (!_src) return;
            if (destroyOld && _src.clip && !_cache.ContainsValue(_src.clip)) Object.Destroy(_src.clip);
            _src.clip = clip;
            _src.Play();
        }

        public void Stop()
        {
            _token++;
            _pending = false;
            if (_src && _src.isPlaying) _src.Stop();
            _fallback.Stop();
        }
    }
}
