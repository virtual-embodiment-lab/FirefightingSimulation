# Stickies PC bridge

A small Windows console app that connects to feedback Sensorimotor Stickies with the PC's
Bluetooth and takes motor commands from Unity over UDP. Use it to test wall-proximity haptics in
the Editor (including Quest Link), or to drive the Stickies from a PC while a standalone Quest
runs the simulation.

Requires Windows 10 (2004) or later with Bluetooth LE, and the .NET 10 SDK.

```bash
# Editor on this PC (listens on 127.0.0.1:47800)
dotnet run --project Tools/StickiesBridge

# A standalone Quest on the same network: listen on every interface,
# then set Bridge Host on the Unity component to this PC's IP address
dotnet run --project Tools/StickiesBridge -- --bind 0.0.0.0

# Just list nearby Stickies for 15 s (to find names for Device Filter)
dotnet run --project Tools/StickiesBridge -- --list
```

Set the component's **Transport** to `PcBridge` (or `Auto` in the Editor) and press Play. The
bridge only connects to Stickies Unity asks for, writes motor commands with response, and keeps
at most one pending command per motor (newer ones replace older ones). Ctrl+C stops every motor
before disconnecting.

With `--bind 0.0.0.0`, Windows Firewall may ask to allow the app; allow it on private networks.

Protocol (one UTF-8 datagram per message):

| Direction       | Message                                      |
|-----------------|----------------------------------------------|
| Unity to bridge | `HELLO`, `SCAN 0/1`, `CONNECT addr`, `DISCONNECT addr`, `WRITE addr hex` |
| bridge to Unity | `DEVICES addr\|name\|rssi\|state;...`, `LOG text` (state: 0 found, 1 connecting, 2 ready, 3 disconnected) |
