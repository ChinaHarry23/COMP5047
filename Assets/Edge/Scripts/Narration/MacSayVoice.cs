using System.Text;
using UnityEngine;

namespace Edge.Narration
{
    // macOS speech via /usr/bin/say. Non-blocking: the process runs on its own and
    // is killed if a new line arrives. Editor on macOS / macOS standalone only;
    // elsewhere it logs the line and stays silent.
    public class MacSayVoice : INarrationVoice
    {
        public string voice;   // e.g. "Karen" (en_AU); empty = system default
        public int rate;       // words per minute; 0 = default

        System.Diagnostics.Process _proc;

        public MacSayVoice(string voice = "Karen", int rate = 175)
        {
            this.voice = voice;
            this.rate = rate;
        }

        public bool IsSpeaking
        {
            get
            {
                try { return _proc != null && !_proc.HasExited; }
                catch { return false; }
            }
        }

        public void Speak(string text)
        {
            Stop();
            if (string.IsNullOrWhiteSpace(text)) return;
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            var args = new StringBuilder();
            if (!string.IsNullOrEmpty(voice)) args.Append("-v \"").Append(Clean(voice)).Append("\" ");
            if (rate > 0) args.Append("-r ").Append(rate).Append(' ');
            args.Append("-- \"").Append(Clean(text)).Append('"');
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/say", args.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };
                _proc = System.Diagnostics.Process.Start(psi);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[EDGE Narration] say failed: {e.Message}");
                _proc = null;
            }
#else
            Debug.Log($"[EDGE Narration] (MacSayVoice unavailable on this platform) {text}");
#endif
        }

        public void Stop()
        {
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); }
            catch { /* already gone */ }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
        }

        // Only plain speakable characters reach the command line (no quotes, no shell metachars).
        static string Clean(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == ',' || c == '.' || c == '!' ||
                    c == '?' || c == '-' || c == ':' || c == ';' || c == '\'' || c == '%')
                    sb.Append(c);
                else if (c == '\n' || c == '\t') sb.Append(' ');
            }
            return sb.ToString();
        }
    }
}
