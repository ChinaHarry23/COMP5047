using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Edge.Narration
{
    // Minimal Gemini REST client (no SDK, no coroutines: UnityWebRequest + completed
    // callback, so it also works from editor menus in edit mode).
    //
    // Text: POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent
    // TTS:  POST https://generativelanguage.googleapis.com/v1beta/interactions  (Gemini 3.8 TTS)
    // Auth: header x-goog-api-key. The key is NEVER stored in the project/scene:
    //   1) env var GEMINI_API_KEY (launchctl setenv GEMINI_API_KEY ... then restart Unity Hub+Unity), or
    //   2) <project>/UserSettings/gemini_key.txt (UserSettings/ is gitignored).
    // Rate guard: min interval between calls, per-minute cap, 60 s back-off after HTTP 429.
    [Serializable]
    public class GeminiClient
    {
        public const string Base = "https://generativelanguage.googleapis.com/v1beta";

        [Tooltip("Text model. gemini-3.5-flash-lite is the cheap/fast current model (Oct 2026); 2.5 models are restricted to legacy users.")]
        public string model = "gemini-3.5-flash-lite";
        [Tooltip("Tried once if the main model returns 404 (alias always points at the latest Flash).")]
        public string fallbackModel = "gemini-flash-latest";
        public string ttsModel = "gemini-3.8-flash-lite-tts";
        [Tooltip("Fixed prebuilt TTS voice used for the WHOLE session (e.g. Charon=Informative, Gacrux=Mature, Sulafat=Warm, Algieba=Smooth).")]
        public string ttsVoice = "Charon";   // prebuilt: "Informative"
        [Tooltip("Style instruction sent with every TTS line so delivery stays consistent.")]
        public string ttsStyle = "warm, calm, hushed nature documentary narrator; steady pace; same voice throughout";
        public int timeoutSeconds = 6;
        [Tooltip("TTS requests take much longer than text; 6 s caused 'Request timeout' failures.")]
        public int ttsTimeoutSeconds = 30;
        public float minSecondsBetweenCalls = 6f;   // <= 10 requests/min
        public int maxCallsPerMinute = 8;          // text + TTS calls combined
        public int maxCallsPerSession = 250;
        public float temperature = 1.0f;
        public int maxOutputTokens = 200;          // headroom for thinking tokens; reply itself is <= 20 words

        [NonSerialized] public string lastError = "";
        [NonSerialized] public int callsThisSession;
        [NonSerialized] public bool busy;
        [NonSerialized] float _lastCall = -999f;
        [NonSerialized] float _backoffUntil;
        [NonSerialized] float[] _minuteWindow = new float[32];
        [NonSerialized] int _mwHead;
        [NonSerialized] string _sessionModel;      // text model resolved once per session
        [NonSerialized] string _sessionTtsModel;   // TTS model locked once per session

        public string ActiveModel => string.IsNullOrEmpty(_sessionModel) ? model : _sessionModel;
        public string ActiveTtsModel => string.IsNullOrEmpty(_sessionTtsModel) ? ttsModel : _sessionTtsModel;

        // Call at session start: locks model/voice so nothing switches mid-session.
        public void BeginSession()
        {
            _sessionModel = model;
            _sessionTtsModel = ttsModel;
            Debug.Log($"[EDGE Narration] session locked: text={_sessionModel} tts={_sessionTtsModel} voice={ttsVoice}");
        }

        public static string KeyFilePath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "UserSettings", "gemini_key.txt"));

        public static string ResolveKey()
        {
            string k = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrWhiteSpace(k)) return k.Trim();
            try
            {
                string p = KeyFilePath;
                if (File.Exists(p)) { k = File.ReadAllText(p).Trim(); if (k.Length > 0) return k; }
            }
            catch { }
            return null;
        }

        public static string KeySource()
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))) return "env GEMINI_API_KEY";
            return File.Exists(KeyFilePath) ? "UserSettings/gemini_key.txt" : "none";
        }

        public bool HasKey => ResolveKey() != null;

        static float Now => Time.realtimeSinceStartup;

        // True if a request may go out now (key present, not busy, within rate limits).
        public bool CanCall(out string why, bool ignoreMinInterval = false)
        {
            if (_minuteWindow == null) _minuteWindow = new float[32];
            why = null;
            if (!HasKey) { why = "no API key"; return false; }
            if (busy) { why = "request in flight"; return false; }
            if (Now < _backoffUntil) { why = "rate-limit back-off"; return false; }
            if (!ignoreMinInterval && Now - _lastCall < minSecondsBetweenCalls) { why = "min interval"; return false; }
            if (callsThisSession >= maxCallsPerSession) { why = "session cap"; return false; }
            int n = 0;
            foreach (float t in _minuteWindow) if (t > 0f && Now - t < 60f) n++;
            if (n >= maxCallsPerMinute) { why = "per-minute cap"; return false; }
            return true;
        }

        void MarkCall()
        {
            if (_minuteWindow == null) _minuteWindow = new float[32];
            _lastCall = Now;
            _minuteWindow[_mwHead] = Now;
            _mwHead = (_mwHead + 1) % _minuteWindow.Length;
            callsThisSession++;
        }

        // ---------------- text ----------------
        public bool GenerateLine(string systemPrompt, string userText, Action<string> onOk, Action<string> onError)
        {
            if (!CanCall(out string why)) { onError?.Invoke(why); return false; }
            string body =
                "{\"systemInstruction\":{\"parts\":[{\"text\":" + Q(systemPrompt) + "}]}," +
                "\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":" + Q(userText) + "}]}]," +
                "\"generationConfig\":{\"temperature\":" + temperature.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"maxOutputTokens\":" + maxOutputTokens + ",\"candidateCount\":1}}";
            SendText(ActiveModel, body, onOk, onError, string.IsNullOrEmpty(_sessionModel) || _sessionModel == model);
            return true;
        }

        void SendText(string m, string body, Action<string> onOk, Action<string> onError, bool allowFallback)
        {
            string url = $"{Base}/models/{m}:generateContent";
            Post(url, body, (code, text) =>
            {
                if (code == 404 && allowFallback && !string.IsNullOrEmpty(fallbackModel) && fallbackModel != m)
                {
                    Debug.LogWarning($"[EDGE Narration] model '{m}' not found; using '{fallbackModel}' for the rest of the session");
                    _sessionModel = fallbackModel;   // switch once, never flip back and forth
                    SendText(fallbackModel, body, onOk, onError, false);
                    return;
                }
                if (code != 200) { Fail(code, text, onError); return; }
                var sb = new StringBuilder();
                foreach (string s in MiniJson.AllStrings(text, "text")) sb.Append(s);
                string line = sb.ToString().Trim();
                if (line.Length == 0) { Fail(code, "empty candidate (finishReason " + MiniJson.FirstString(text, "finishReason") + ")", onError); return; }
                lastError = "";
                onOk?.Invoke(line);
            });
        }

        // ---------------- TTS ----------------
        // Returns 24 kHz mono float samples (Gemini 3.8 TTS, response_format audio/l16).
        public bool Synthesize(string text, Action<float[], int> onOk, Action<string> onError)
        {
            if (!CanCall(out string why, true)) { onError?.Invoke(why); return false; }
            if (string.IsNullOrEmpty(_sessionTtsModel)) _sessionTtsModel = ttsModel;
            string body =
                "{\"model\":" + Q(_sessionTtsModel) + "," +
                "\"input\":[{\"type\":\"user_input\",\"content\":[{\"type\":\"text\",\"text\":" + Q(text) + "," +
                "\"annotations\":[{\"type\":\"speech_metadata\",\"style\":" + Q(ttsStyle) + "}]}]}]," +
                "\"response_format\":{\"type\":\"audio\",\"mime_type\":\"audio/l16\",\"sample_rate\":24000}," +
                "\"generation_config\":{\"speech_config\":[{\"voice\":" + Q(ttsVoice) + "}]}}";
            Post($"{Base}/interactions", body, ttsTimeoutSeconds, (code, resp) =>
            {
                if (code != 200) { Fail(code, resp, onError); return; }
                string b64 = null;
                foreach (string s in MiniJson.AllStrings(resp, "data")) b64 = s; // last audio block
                if (string.IsNullOrEmpty(b64)) { Fail(code, "no audio in response", onError); return; }
                try
                {
                    byte[] pcm = Convert.FromBase64String(b64);
                    int off = (pcm.Length > 44 && pcm[0] == 'R' && pcm[1] == 'I' && pcm[2] == 'F' && pcm[3] == 'F') ? 44 : 0;
                    int n = (pcm.Length - off) / 2;
                    var f = new float[n];
                    for (int i = 0; i < n; i++) f[i] = (short)(pcm[off + 2 * i] | (pcm[off + 2 * i + 1] << 8)) / 32768f;
                    lastError = "";
                    onOk?.Invoke(f, 24000);
                }
                catch (Exception e) { Fail(code, "decode: " + e.Message, onError); }
            });
            return true;
        }

        // ---------------- plumbing ----------------
        void Post(string url, string json, Action<long, string> done) => Post(url, json, timeoutSeconds, done);

        void Post(string url, string json, int timeout, Action<long, string> done)
        {
            string key = ResolveKey();
            MarkCall();
            busy = true;
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("x-goog-api-key", key);
            req.timeout = Mathf.Max(1, timeout);
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                busy = false;
                long code = req.result == UnityWebRequest.Result.ConnectionError ? 0 : req.responseCode;
                string text = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (code == 0) text = req.error;
                req.Dispose();
                try { done(code, text); }
                catch (Exception e) { Debug.LogException(e); }
            };
        }

        void Fail(long code, string text, Action<string> onError)
        {
            if (code == 429) _backoffUntil = Now + 60f;
            string msg = MiniJson.FirstString(text ?? "", "message");
            lastError = $"HTTP {code}: {(string.IsNullOrEmpty(msg) ? Trunc(text, 160) : msg)}";
            onError?.Invoke(lastError);
        }

        static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));

        public static string Q(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    // Tiny JSON helper: pulls string values for a given key anywhere in a document.
    // Enough for Gemini responses without pulling in a JSON package.
    public static class MiniJson
    {
        public static System.Collections.Generic.IEnumerable<string> AllStrings(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) yield break;
            string pat = "\"" + key + "\"";
            int i = 0;
            while ((i = json.IndexOf(pat, i, StringComparison.Ordinal)) >= 0)
            {
                int j = i + pat.Length;
                while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                if (j < json.Length && json[j] == ':')
                {
                    j++;
                    while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                    if (j < json.Length && json[j] == '"')
                    {
                        string v = ReadString(json, j, out int end);
                        if (v != null) { yield return v; i = end; continue; }
                    }
                }
                i = j;
            }
        }

        public static string FirstString(string json, string key)
        {
            foreach (string s in AllStrings(json, key)) return s;
            return null;
        }

        static string ReadString(string s, int q, out int end)
        {
            var sb = new StringBuilder();
            int i = q + 1;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { end = i + 1; return sb.ToString(); }
                if (c == '\\' && i + 1 < s.Length)
                {
                    char e = s[i + 1];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': break;
                        case 'b': case 'f': break;
                        case 'u':
                            if (i + 5 < s.Length && int.TryParse(s.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out int cp))
                            { sb.Append((char)cp); i += 4; }
                            break;
                        default: sb.Append(e); break;
                    }
                    i += 2;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            end = s.Length;
            return null;
        }
    }
}
