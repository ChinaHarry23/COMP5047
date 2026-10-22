using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace Edge
{
    // UDP bridge to the tag on port 1234 (BUILD_SPEC section 8.1).
    // Receives on a background thread, dispatches on the main thread.
    // Replies go to the last endpoint heard from, else the default loopback.
    public class UdpLink : MonoBehaviour
    {
        public string defaultRemoteHost = "127.0.0.1";
        public int listenPort = EdgeConfig.UdpPort;
        public int remotePort = EdgeConfig.UdpPort;

        public event Action<string> OnMessage;

        UdpClient _rx;
        UdpClient _tx;
        System.Threading.Thread _thread;
        volatile bool _running;
        readonly ConcurrentQueue<string> _inbox = new ConcurrentQueue<string>();
        IPEndPoint _learned;
        readonly object _endpointLock = new object();

        void Start()
        {
            try
            {
                _rx = new UdpClient(listenPort);
                _running = true;
                _thread = new System.Threading.Thread(ReceiveLoop) { IsBackground = true };
                _thread.Start();
                Debug.Log($"[EDGE] UDP listening on port {listenPort}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EDGE] UDP listen failed on {listenPort}: {e.Message}");
            }
        }

        void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _rx.Receive(ref from);
                    lock (_endpointLock) _learned = from;
                    string msg = Encoding.UTF8.GetString(data).Trim();
                    if (msg.Length > 0) _inbox.Enqueue(msg);
                }
                catch
                {
                    // socket closed on shutdown
                }
            }
        }

        void Update()
        {
            while (_inbox.TryDequeue(out string msg))
                OnMessage?.Invoke(msg);
        }

        public void Send(string msg)
        {
            try
            {
                IPEndPoint to;
                lock (_endpointLock)
                    to = _learned ?? new IPEndPoint(IPAddress.Parse(defaultRemoteHost), remotePort);
                if (_tx == null) _tx = new UdpClient();
                byte[] data = Encoding.UTF8.GetBytes(msg);
                _tx.Send(data, data.Length, to);
            }
            catch
            {
                // best-effort telemetry; a dropped packet must never stop play
            }
        }

        void OnDestroy()
        {
            _running = false;
            _rx?.Close();
            _tx?.Close();
        }
    }
}
