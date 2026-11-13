using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Firefighting.Haptics.Stickies
{
    /// <summary>
    /// Reaches Stickies through a PC's Bluetooth via the Tools/StickiesBridge console app
    /// (dotnet run --project Tools/StickiesBridge).
    /// Used in the Editor (including Quest Link), or from a standalone Quest over Wi-Fi when the
    /// headset's own Bluetooth is not wanted.
    ///
    /// Plain-text datagrams, one message each:
    ///   Unity to bridge: HELLO | SCAN 0/1 | CONNECT addr | DISCONNECT addr | WRITE addr hex
    ///   bridge to Unity: DEVICES addr|name|rssi|state;... | LOG text
    /// </summary>
    public sealed class UdpBridgeStickyTransport : IStickyTransport
    {
        private const float HelloInterval = 1f;
        private const float BridgeTimeout = 3f;

        private readonly string host;
        private readonly int port;
        private readonly List<StickyDeviceInfo> devices = new();
        private readonly ConcurrentQueue<string> inbox = new();

        private UdpClient client;
        private IPEndPoint bridge;
        private Thread receiver;
        private volatile bool running;
        private float nextHello;
        private float lastHeard = float.NegativeInfinity;
        private bool scanning;
        private string status = "Not started";

        public UdpBridgeStickyTransport(string host, int port)
        {
            this.host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
            this.port = port;
        }

        public string Name => "PC bridge (UDP)";
        public string Status => status;
        public IReadOnlyList<StickyDeviceInfo> Devices => devices;

        public void Start()
        {
            try
            {
                bridge = new IPEndPoint(Resolve(host), port);
                client = new UdpClient(0);
                client.Client.ReceiveTimeout = 500;
                IgnoreWindowsConnectionReset(client);
                running = true;
                receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "Stickies UDP bridge" };
                receiver.Start();
                status = $"Looking for the bridge at {bridge}";
            }
            catch (Exception e)
            {
                status = $"Could not open UDP socket: {e.Message}";
                Debug.LogError("[Stickies/Bridge] " + status);
            }
        }

        public void Tick()
        {
            if (client == null) return;

            while (inbox.TryDequeue(out string message))
            {
                lastHeard = Time.unscaledTime;
                if (message.StartsWith("DEVICES", StringComparison.Ordinal))
                    StickyDeviceListParser.Parse(message.Length > 8 ? message.Substring(8) : string.Empty, ';', devices);
                else if (message.StartsWith("LOG ", StringComparison.Ordinal))
                    Debug.Log("[Stickies/Bridge] " + message.Substring(4));
            }

            if (Time.unscaledTime >= nextHello)
            {
                nextHello = Time.unscaledTime + HelloInterval;
                // Doubles as a keep-alive so the bridge always knows where to answer.
                Send("HELLO");
                Send(scanning ? "SCAN 1" : "SCAN 0");
            }

            bool connected = Time.unscaledTime - lastHeard < BridgeTimeout;
            if (!connected) devices.Clear();
            status = connected
                ? $"Bridge at {bridge}: {devices.Count} feedback Stickies seen"
                : $"No bridge at {bridge}. Run 'dotnet run --project Tools/StickiesBridge', or switch Transport to Simulated.";
        }

        public void SetScanning(bool value)
        {
            scanning = value;
            Send(value ? "SCAN 1" : "SCAN 0");
        }

        public void Connect(string address) => Send("CONNECT " + address);
        public void Disconnect(string address) => Send("DISCONNECT " + address);

        public bool Write(string address, byte[] payload)
        {
            if (!devices.Exists(d => d.Address == address && d.State == StickyConnectionState.Ready)) return false;
            Send($"WRITE {address} {StickyProtocol.ToHex(payload)}");
            return true;
        }

        public void Dispose()
        {
            running = false;
            try { client?.Close(); } catch { /* already closed */ }
            client = null;
            receiver?.Join(1000);
            receiver = null;
            devices.Clear();
        }

        private void Send(string message)
        {
            if (client == null || bridge == null) return;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                client.Send(data, data.Length, bridge);
            }
            catch (Exception)
            {
                // Bridge not up yet; HELLO keeps retrying.
            }
        }

        private void ReceiveLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    byte[] data = client.Receive(ref any);
                    inbox.Enqueue(Encoding.UTF8.GetString(data).Trim());
                }
                catch (SocketException)
                {
                    // Timeout, or Windows reporting that the bridge port is closed; keep listening.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (NullReferenceException)
                {
                    return; // client closed during shutdown
                }
            }
        }

        private static IPAddress Resolve(string hostName)
        {
            if (IPAddress.TryParse(hostName, out IPAddress address)) return address;
            foreach (IPAddress candidate in Dns.GetHostAddresses(hostName))
                if (candidate.AddressFamily == AddressFamily.InterNetwork) return candidate;
            throw new ArgumentException($"Cannot resolve '{hostName}' to an IPv4 address");
        }

        /// <summary>
        /// On Windows an ICMP "port unreachable" (bridge not running yet) would otherwise make
        /// every later Receive on this socket throw.
        /// </summary>
        private static void IgnoreWindowsConnectionReset(UdpClient udp)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor &&
                Application.platform != RuntimePlatform.WindowsPlayer) return;
            const int SioUdpConnReset = -1744830452;
            try { udp.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null); }
            catch { /* not supported on this runtime */ }
        }
    }
}
