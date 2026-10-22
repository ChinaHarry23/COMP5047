using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace Edge.Narration
{
    // STUB for the hardware path: narration out of the bat tag's own speaker.
    // Today it sends "SAY <text>" as one UDP datagram to the tag on its own port
    // (default 1235, deliberately NOT the IMU/dose link on 1234, so the input path
    // in UdpLink/BatFlightController is untouched).
    //
    // TODO(ESP32): pick one of
    //   a) on-device TTS is not realistic on an ESP32 -> send pre-rendered audio:
    //      request Gemini TTS as audio/l16 at sample_rate 16000 (or audio/mulaw 8 kHz),
    //      chunk into <=1400-byte datagrams "PCM <seq> <bytes>", play via I2S DAC (MAX98357A).
    //   b) a small fixed set of offline clips flashed to SPIFFS, triggered by "CLIP <id>".
    //   c) serial (USB) instead of UDP when tethered.
    // IsSpeaking is an estimate (~2.6 words/s) until the tag acks "SAYDONE".
    public class Esp32SpeakerVoice : INarrationVoice
    {
        public string host;
        public int port;
        float _busyUntil;
        UdpClient _udp;

        public Esp32SpeakerVoice(string host = "192.168.4.1", int port = 1235)
        {
            this.host = host;
            this.port = port;
        }

        public bool IsSpeaking => Time.realtimeSinceStartup < _busyUntil;

        public void Speak(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                if (_udp == null) _udp = new UdpClient();
                byte[] b = Encoding.UTF8.GetBytes("SAY " + text.Replace('\n', ' '));
                _udp.Send(b, b.Length, host, port);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[EDGE Narration] ESP32 send failed ({host}:{port}): {e.Message}");
            }
            int words = text.Split(' ').Length;
            _busyUntil = Time.realtimeSinceStartup + words / 2.6f + 0.5f;
        }

        public void Stop()
        {
            _busyUntil = 0f;
            // TODO(ESP32): send "SAYSTOP" once firmware supports it.
        }
    }
}
