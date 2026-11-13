// UDP <-> Bluetooth LE bridge for Sensorimotor Stickies feedback devices
// (https://stickies.catherineyu.com/docs/develop/ble-api).
//
// Unity's UdpBridgeStickyTransport talks to this over plain-text datagrams:
//   Unity -> bridge: HELLO | SCAN 0/1 | CONNECT addr | DISCONNECT addr | WRITE addr hex
//   bridge -> Unity: DEVICES addr|name|rssi|state;... | LOG text
//
// Usage:
//   dotnet run --project Tools/StickiesBridge                 (Editor on this PC)
//   dotnet run --project Tools/StickiesBridge -- --bind 0.0.0.0   (a standalone Quest on the LAN)
//   dotnet run --project Tools/StickiesBridge -- --list       (just print nearby Stickies)

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace StickiesBridge;

internal enum StickyState { Discovered = 0, Connecting = 1, Ready = 2, Disconnected = 3 }

internal sealed class Sticky
{
    public required ulong Address { get; init; }
    public string Text => FormatAddress(Address);
    public string Name = "";
    public int Rssi;
    public bool SawFeedbackService;
    public bool SawSensingService;
    public volatile StickyState State = StickyState.Discovered;
    public bool Listed;

    public BluetoothLEDevice? Device;
    public GattSession? Session;
    public GattDeviceService? Service;
    public GattCharacteristic? Motor;
    public CancellationTokenSource? WriterStop;

    // One pending command per motor; a newer command replaces one not yet written.
    private readonly object gate = new();
    private readonly Dictionary<int, byte[]> pending = new();
    private readonly Queue<int> order = new();
    public readonly SemaphoreSlim Signal = new(0);

    public void Enqueue(byte[] payload)
    {
        int motor = payload.Length > 1 ? payload[1] : 0;
        lock (gate)
        {
            if (motor == 0xFF)
            {
                pending.Clear();
                order.Clear();
            }
            if (!pending.ContainsKey(motor)) order.Enqueue(motor);
            pending[motor] = payload;
        }
        Signal.Release();
    }

    public bool TryTake(out byte[] payload)
    {
        lock (gate)
        {
            while (order.Count > 0)
                if (pending.Remove(order.Dequeue(), out payload!)) return true;
        }
        payload = Array.Empty<byte>();
        return false;
    }

    public void ClearPending()
    {
        lock (gate)
        {
            pending.Clear();
            order.Clear();
        }
    }

    public static string FormatAddress(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    public static bool TryParseAddress(string text, out ulong address)
    {
        address = 0;
        string hex = text.Replace(":", "").Replace("-", "");
        return hex.Length == 12 && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out address);
    }
}

internal static class Program
{
    private static readonly Guid FeedbackService = Guid.Parse("00020000-7374-6963-6b79-535449434b59");
    private static readonly Guid MotorCommand = Guid.Parse("00020001-7374-6963-6b79-535449434b59");
    private static readonly Guid SensingService = Guid.Parse("00010000-7374-6963-6b79-535449434b59");
    private static readonly TimeSpan GattTimeout = TimeSpan.FromSeconds(20);

    private static readonly ConcurrentDictionary<ulong, Sticky> Stickies = new();
    private static readonly object ConnectGate = new();
    private static BluetoothLEAdvertisementWatcher? watcher;
    private static UdpClient? udp;
    private static IPEndPoint? peer;
    private static bool listOnly;

    private static async Task<int> Main(string[] args)
    {
        int port = 47800;
        IPAddress bind = IPAddress.Loopback;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--bind" when i + 1 < args.Length: bind = IPAddress.Parse(args[++i]); break;
                case "--list": listOnly = true; break;
                case "-h" or "--help":
                    Console.WriteLine("StickiesBridge [--port 47800] [--bind 127.0.0.1|0.0.0.0] [--list]");
                    return 0;
            }
        }

        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null || !adapter.IsLowEnergySupported)
        {
            Console.Error.WriteLine("No Bluetooth LE adapter found. Turn Bluetooth on in Windows settings.");
            return 1;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += OnAdvertisement;

        if (listOnly)
        {
            Console.WriteLine("Listing Sensorimotor Stickies for 15 s (Ctrl+C to stop)...");
            watcher.Start();
            try { await Task.Delay(TimeSpan.FromSeconds(15), stop.Token); } catch (OperationCanceledException) { }
            watcher.Stop();
            return 0;
        }

        udp = new UdpClient(new IPEndPoint(bind, port));
        try { udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
        Log($"Stickies bridge listening on udp://{bind}:{port}. Waiting for Unity... (Ctrl+C to quit)");

        Task receive = ReceiveLoop(stop.Token);
        Task publish = PublishLoop(stop.Token);
        try { await Task.WhenAll(receive, publish); } catch (OperationCanceledException) { }

        Log("Stopping every motor and disconnecting...");
        watcher.Stop();
        foreach (Sticky sticky in Stickies.Values.Where(s => s.State == StickyState.Ready && s.Motor != null))
        {
            try { await Write(sticky, new byte[] { 0x00, 0xFF }).WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        foreach (Sticky sticky in Stickies.Values) Cleanup(sticky);
        udp.Dispose();
        return 0;
    }

    // ------------------------------------------------------------------ UDP

    private static async Task ReceiveLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp!.ReceiveAsync(token);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; } // Unity went away; keep listening

            bool first = peer == null || !peer.Equals(result.RemoteEndPoint);
            peer = result.RemoteEndPoint;
            if (first) Log($"Unity connected from {peer}");

            string line = Encoding.UTF8.GetString(result.Buffer).Trim();
            try { Handle(line); }
            catch (Exception e) { Log($"Bad message '{line}': {e.Message}"); }
        }
    }

    private static void Handle(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        switch (parts[0].ToUpperInvariant())
        {
            case "HELLO":
                SendDevices();
                break;
            case "SCAN" when parts.Length > 1:
                SetScanning(parts[1] == "1");
                break;
            case "CONNECT" when parts.Length > 1 && Sticky.TryParseAddress(parts[1], out ulong connectAddress):
                Sticky target = Stickies.GetOrAdd(connectAddress, a => new Sticky { Address = a });
                lock (ConnectGate)
                {
                    if (target.State is StickyState.Connecting or StickyState.Ready) break;
                    target.State = StickyState.Connecting;
                }
                _ = ConnectAsync(target);
                break;
            case "DISCONNECT" when parts.Length > 1 && Sticky.TryParseAddress(parts[1], out ulong dropAddress):
                if (Stickies.TryGetValue(dropAddress, out Sticky? dropped))
                {
                    Cleanup(dropped);
                    dropped.State = StickyState.Disconnected;
                }
                break;
            case "WRITE" when parts.Length > 2 && Sticky.TryParseAddress(parts[1], out ulong writeAddress):
                if (Stickies.TryGetValue(writeAddress, out Sticky? sticky) && sticky.State == StickyState.Ready)
                    sticky.Enqueue(Convert.FromHexString(parts[2]));
                break;
        }
    }

    private static async Task PublishLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            SendDevices();
            try { await Task.Delay(500, token); } catch (OperationCanceledException) { return; }
        }
    }

    private static void SendDevices()
    {
        IEnumerable<string> records = Stickies.Values
            .Where(s => s.Listed || s.State != StickyState.Discovered)
            .Select(s => $"{s.Text}|{Clean(s.Name)}|{s.Rssi}|{(int)s.State}");
        Send("DEVICES " + string.Join(";", records));
    }

    private static void Send(string message)
    {
        IPEndPoint? target = peer;
        if (udp == null || target == null) return;
        byte[] data = Encoding.UTF8.GetBytes(message);
        try { udp.Send(data, data.Length, target); } catch { }
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        Send("LOG " + message);
    }

    private static string Clean(string text) => text.Replace('|', '/').Replace(';', ',');

    // ------------------------------------------------------------------ Bluetooth

    private static void SetScanning(bool on)
    {
        if (watcher == null) return;
        bool running = watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;
        if (on && !running)
        {
            watcher.Start();
            Log("Scanning for feedback Stickies");
        }
        else if (!on && running)
        {
            watcher.Stop();
            Log("Scan stopped");
        }
    }

    private static void OnAdvertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        // Only track devices that look like Stickies, so a busy lab does not fill the table.
        string localName = e.Advertisement.LocalName ?? "";
        bool looksLikeSticky = localName.StartsWith("STICKY", StringComparison.OrdinalIgnoreCase) ||
                               e.Advertisement.ServiceUuids.Contains(FeedbackService) ||
                               e.Advertisement.ServiceUuids.Contains(SensingService);
        if (!looksLikeSticky && !Stickies.ContainsKey(e.BluetoothAddress)) return;

        Sticky sticky = Stickies.GetOrAdd(e.BluetoothAddress, a => new Sticky { Address = a });
        // Name and service list can arrive in separate packets (advertisement and scan response).
        if (!string.IsNullOrEmpty(e.Advertisement.LocalName)) sticky.Name = e.Advertisement.LocalName;
        sticky.SawFeedbackService |= e.Advertisement.ServiceUuids.Contains(FeedbackService);
        sticky.SawSensingService |= e.Advertisement.ServiceUuids.Contains(SensingService);
        sticky.Rssi = e.RawSignalStrengthInDBm;

        if (sticky.Listed || !IsFeedbackSticky(sticky)) return;
        sticky.Listed = true;
        if (listOnly) Console.WriteLine($"  {sticky.Name,-24} {sticky.Text}  RSSI {sticky.Rssi} dBm");
        else Log($"Found {sticky.Name} ({sticky.Text})");
    }

    private static bool IsFeedbackSticky(Sticky s)
    {
        if (!s.Name.StartsWith("STICKY", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.SawFeedbackService) return true;
        if (s.SawSensingService) return false;
        string upper = s.Name.ToUpperInvariant();
        if (upper.Contains("SENSE")) return false;
        return upper.Contains("FB") || upper.Contains("FEEDBACK");
    }

    private static async Task ConnectAsync(Sticky sticky)
    {
        Log($"Connecting to {Label(sticky)}");
        try
        {
            BluetoothLEDevice? device = await BluetoothLEDevice.FromBluetoothAddressAsync(sticky.Address).AsTask().WaitAsync(GattTimeout);
            if (device == null) throw new InvalidOperationException("device not reachable");
            sticky.Device = device;
            if (string.IsNullOrEmpty(sticky.Name)) sticky.Name = device.Name;

            sticky.Session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask().WaitAsync(GattTimeout);
            sticky.Session.MaintainConnection = true;

            GattDeviceServicesResult services = await device
                .GetGattServicesForUuidAsync(FeedbackService, BluetoothCacheMode.Uncached).AsTask().WaitAsync(GattTimeout);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new InvalidOperationException($"no feedback service ({services.Status}); is it a sensing Sticky?");
            sticky.Service = services.Services[0];

            GattCharacteristicsResult characteristics = await sticky.Service
                .GetCharacteristicsForUuidAsync(MotorCommand, BluetoothCacheMode.Uncached).AsTask().WaitAsync(GattTimeout);
            if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
                throw new InvalidOperationException($"no Motor Command characteristic ({characteristics.Status})");
            sticky.Motor = characteristics.Characteristics[0];

            device.ConnectionStatusChanged += (d, _) =>
            {
                if (d.ConnectionStatus != BluetoothConnectionStatus.Disconnected || sticky.State != StickyState.Ready) return;
                Log($"{Label(sticky)} disconnected");
                Cleanup(sticky);
                sticky.State = StickyState.Disconnected;
            };

            sticky.ClearPending();
            sticky.WriterStop = new CancellationTokenSource();
            _ = WriterLoop(sticky, sticky.WriterStop.Token);
            sticky.State = StickyState.Ready;
            Log($"{Label(sticky)} ready");
        }
        catch (Exception e)
        {
            Log($"Could not connect to {Label(sticky)}: {e.Message}");
            Cleanup(sticky);
            sticky.State = StickyState.Disconnected;
        }
        SendDevices();
    }

    private static async Task WriterLoop(Sticky sticky, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await sticky.Signal.WaitAsync(token);
                while (sticky.TryTake(out byte[] payload))
                {
                    GattCommunicationStatus status = await Write(sticky, payload);
                    if (status != GattCommunicationStatus.Success)
                        Log($"Write to {Label(sticky)} failed: {status}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log($"Writer for {Label(sticky)} stopped: {e.Message}");
        }
    }

    private static async Task<GattCommunicationStatus> Write(Sticky sticky, byte[] payload)
    {
        GattCharacteristic? motor = sticky.Motor;
        if (motor == null) return GattCommunicationStatus.Unreachable;
        using var writer = new DataWriter();
        writer.WriteBytes(payload);
        GattWriteResult result = await motor.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
        return result.Status;
    }

    private static void Cleanup(Sticky sticky)
    {
        sticky.WriterStop?.Cancel();
        sticky.WriterStop = null;
        sticky.ClearPending();
        sticky.Motor = null;
        try { sticky.Service?.Dispose(); } catch { }
        try { sticky.Session?.Dispose(); } catch { }
        try { sticky.Device?.Dispose(); } catch { }
        sticky.Service = null;
        sticky.Session = null;
        sticky.Device = null;
    }

    private static string Label(Sticky sticky) =>
        $"{(string.IsNullOrEmpty(sticky.Name) ? "Sticky" : sticky.Name)} ({sticky.Text})";
}
