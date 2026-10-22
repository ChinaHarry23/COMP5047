namespace Edge.Narration
{
    // Which output speaks the narration. The narrator only talks to INarrationVoice,
    // so swapping Mac speakers -> Gemini TTS -> the ESP32 tag needs no logic change.
    public enum NarrationVoiceKind { MacSay, GeminiTts, Esp32Speaker, SubtitleOnly }

    public interface INarrationVoice
    {
        void Speak(string text);
        bool IsSpeaking { get; }
        void Stop();
    }

    // Subtitles only (silent). Also the safe fallback on non-mac platforms.
    public class SilentVoice : INarrationVoice
    {
        public void Speak(string text) { }
        public bool IsSpeaking => false;
        public void Stop() { }
    }
}
