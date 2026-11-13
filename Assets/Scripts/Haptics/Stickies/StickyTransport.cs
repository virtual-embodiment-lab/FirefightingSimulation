using System;
using System.Collections.Generic;
using UnityEngine;

namespace Firefighting.Haptics.Stickies
{
    /// <summary>Matches the integer codes sent by the Android plugin and the PC bridge.</summary>
    public enum StickyConnectionState
    {
        Discovered = 0,
        Connecting = 1,
        Ready = 2,
        Disconnected = 3,
    }

    [Serializable]
    public struct StickyDeviceInfo
    {
        public string Address;
        public string Name;
        public int Rssi;
        public StickyConnectionState State;

        public override string ToString() => $"{Name} ({Address}) {State}";
    }

    /// <summary>
    /// A way of reaching feedback Stickies over BLE. Implementations only list feedback
    /// Stickies (sensing Stickies are filtered out) and keep at most one pending command per
    /// motor, so a newer command replaces one that has not gone out yet.
    /// All members are called from the Unity main thread.
    /// </summary>
    public interface IStickyTransport : IDisposable
    {
        string Name { get; }

        /// <summary>One-line human readable status for the Inspector.</summary>
        string Status { get; }

        IReadOnlyList<StickyDeviceInfo> Devices { get; }

        void Start();

        /// <summary>Polls for discovered devices and connection changes.</summary>
        void Tick();

        void SetScanning(bool scanning);
        void Connect(string address);
        void Disconnect(string address);

        /// <summary>Queues a Motor Command write. Returns false when the device is not Ready.</summary>
        bool Write(string address, byte[] payload);
    }

    /// <summary>
    /// No hardware: pretends one feedback Sticky exists per slot so the whole pipeline
    /// (assignment, zones, pattern scheduling) can be tested in the Editor.
    /// </summary>
    public sealed class SimulatedStickyTransport : IStickyTransport
    {
        private readonly List<StickyDeviceInfo> devices = new();
        private readonly int deviceCount;
        private readonly bool logWrites;

        public SimulatedStickyTransport(int deviceCount, bool logWrites)
        {
            this.deviceCount = Mathf.Max(1, deviceCount);
            this.logWrites = logWrites;
        }

        public string Name => "Simulated";
        public string Status => $"Simulating {devices.Count} feedback Stickies (no Bluetooth)";
        public IReadOnlyList<StickyDeviceInfo> Devices => devices;

        public void Start()
        {
            devices.Clear();
            for (int i = 0; i < deviceCount; i++)
            {
                devices.Add(new StickyDeviceInfo
                {
                    Address = $"SIM:{i + 1:00}",
                    Name = $"STICKY_FB_SIM{i + 1}",
                    Rssi = -50,
                    State = StickyConnectionState.Discovered,
                });
            }
        }

        public void Tick() { }
        public void SetScanning(bool scanning) { }

        public void Connect(string address) => SetState(address, StickyConnectionState.Ready);
        public void Disconnect(string address) => SetState(address, StickyConnectionState.Disconnected);

        public bool Write(string address, byte[] payload)
        {
            int index = devices.FindIndex(d => d.Address == address);
            if (index < 0 || devices[index].State != StickyConnectionState.Ready) return false;
            if (logWrites) Debug.Log($"[Stickies/Sim] {devices[index].Name} <- {StickyProtocol.ToHex(payload)}");
            return true;
        }

        public void Dispose() => devices.Clear();

        private void SetState(string address, StickyConnectionState state)
        {
            int index = devices.FindIndex(d => d.Address == address);
            if (index < 0) return;
            StickyDeviceInfo device = devices[index];
            device.State = state;
            devices[index] = device;
        }
    }

    internal static class StickyDeviceListParser
    {
        /// <summary>
        /// Parses "address|name|rssi|state" records separated by <paramref name="recordSeparator"/>.
        /// </summary>
        public static void Parse(string text, char recordSeparator, List<StickyDeviceInfo> into)
        {
            into.Clear();
            if (string.IsNullOrEmpty(text)) return;
            foreach (string record in text.Split(recordSeparator))
            {
                if (string.IsNullOrWhiteSpace(record)) continue;
                string[] fields = record.Split('|');
                if (fields.Length < 4) continue;
                int.TryParse(fields[2], out int rssi);
                int.TryParse(fields[3], out int state);
                into.Add(new StickyDeviceInfo
                {
                    Address = fields[0].Trim(),
                    Name = fields[1].Trim(),
                    Rssi = rssi,
                    State = (StickyConnectionState)Mathf.Clamp(state, 0, 3),
                });
            }
        }
    }
}
