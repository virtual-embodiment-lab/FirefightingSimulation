# Wall-proximity haptics with Sensorimotor Stickies

Participants wear feedback **Sensorimotor Stickies** (https://stickies.catherineyu.com) taped to
the body. Each Sticky stands for one side of the body. When a wall in the virtual house comes
within reach on that side, that Sticky buzzes. This lets a participant feel walls in
zero-visibility smoke, much like a firefighter keeping contact with a wall during a search.

## How it works

```
Avatar bone (VRIK)  ->  fan of rays toward that side  ->  nearest "Walls"/door hit
      -> proximity zone  ->  Motor Command (Play) over BLE  ->  Sticky renders the pulses
```

* **Body sites.** Each slot is tied to a bone of the local Normcore/VRIK avatar (chest, upper
  back, upper arms, forearms, hands, legs...). Directions are relative to where the **body**
  faces (the VRIK root), not the head, so looking around does not move the cues. With no avatar
  yet, positions are estimated from the headset.
* **What counts as a wall.** By default, any collider under an object named `Walls` (the house
  generator merges each room's walls into one), plus closed doors (`Door`, `Door_Interior`...).
  Rays pass through furniture, so a wardrobe in front of a wall does not hide the wall, and the
  wardrobe itself does not buzz. You can add layers, or switch on *Any Vertical Static Surface*
  for hand-built scenes.

## Feedback: buzz only near a wall (default)

**Feedback Style = Alert On Approach.** A Sticky buzzes **once** when a wall comes within reach
on its side, and once more (stronger) on contact. It then stays silent while the participant
stands there, and re-arms only after they move away and come back. Nothing beyond 0.6 m buzzes,
so walking down a corridor stays quiet.

| Zone     | Wall closer than | Cue (played once on entry)       | Intensity |
|----------|------------------|----------------------------------|-----------|
| Near     | 0.6 m            | 1 gentle 150 ms buzz             | 170       |
| Touching | 0.3 m            | 3 quick pulses (70 ms on/70 off) | 255       |

* Distances are from the bone, a few cm inside the body. A 5 cm hysteresis means swaying at a
  zone edge does not re-trigger.
* **Remind Every (s)**: 0 by default (once per approach). Set it to e.g. 3 to repeat the cue
  every 3 s while the wall stays close.
* **Feedback Style = Continuous Proximity** brings back parking-sensor behaviour: pulses keep
  coming while a wall is in a zone, and stop when it leaves.
* Zones are editable in the Inspector (distance, pulse length, gap, number of pulses, intensity),
  and *Master Intensity* / each slot's *Intensity Scale* adjust strength for comfort.

The firmware plays a whole pulse train from one 8-byte `Motor Command (Play)`, so a cue is a
single BLE write. Motors are stopped on app pause (headset off) and on quit.

## Setup in Unity

1. **Tools > Stickies > Add Wall Proximity Haptics to Scene.** This creates a
   `Stickies Wall Haptics` object. It stays alive across scene loads and finds the local XR
   Origin and avatar by itself. Monitor and commander clients ignore it.
2. In the Inspector, check the four default slots (chest = ahead, upper back = behind,
   left/right upper arm = left/right). Add, remove or retarget slots to match where you tape the
   Stickies.
3. Choose the **Transport**:
   * `Auto`: Android BLE on the Quest, PC bridge in the Editor.
   * `AndroidBle`: the Quest talks to the Stickies directly.
   * `PcBridge`: a Windows PC talks to them (see `Tools/StickiesBridge/README.md`). Use this in
     the Editor / Quest Link, or from a standalone Quest by setting *Bridge Host* to the PC's IP.
   * `Simulated`: no hardware; one fake Sticky per slot. Turn on *Log Simulated Writes* to see
     the bytes that would be sent.
4. Press Play. In the **Scene view** each slot draws its rays, coloured by zone (red = touching,
   orange = near, green = none), with the live distance. The slots' *Live* fields show the
   assigned Sticky, connection state, wall distance and zone.

If you added the component before this change, right-click its header > **Reset** to pick up the
new zone defaults (or edit the zones by hand).

## Testing without hardware

The self-test (`Assets/Scripts/Editor/StickiesSelfTest.cs`) builds a throwaway avatar and walls
5 km above the scene, runs the component with simulated Stickies, real Unity physics and a
simulated clock, and checks all 19 behaviours: no buzz far away, one buzz on approach, silence
while standing still, the right Sticky for each side, body-relative directions, furniture, the
reminder option, Continuous Proximity, and the off switch. Nothing is saved to your scene.

* **In the Editor:** Tools > Stickies > **Run Self-Test (no hardware)**. Results go to the Console.
* **Headless, while the Editor stays open** (also what Claude runs):
  ```
  powershell -ExecutionPolicy Bypass -File Tools/StickiesSelfTest/run.ps1
  ```
  This copies the Stickies code into a small temporary Unity project (with stand-ins for VRIK,
  Normcore and XR Origin from `Tools/StickiesSelfTest/Stubs.cs`) and runs it in batch mode.
  About 35 s; exit code 0 means every check passed.
* **Headless against this project** (Unity must be closed on it):
  ```
  Unity.exe -batchmode -nographics -projectPath <this folder> -executeMethod StickiesSelfTest.RunBatch -logFile selftest.log
  ```

## Pairing Stickies to body sites

* **One at a time (no setup).** Leave *Device Filter* empty. Switch Stickies on in slot order;
  each one goes to the next empty slot. When a Sticky connects it pulses **its slot number** of
  times (chest 1, back 2, left arm 3, right arm 4), so you can check it is on the right spot.
* **Fixed mapping.** Put part of a Sticky's BLE name or address in *Device Filter* (run the
  bridge with `--list` to see names). Filtered slots always get their own Sticky.
* Right-click the component header for **Stickies > Pulse every connected Sticky** and
  **Stickies > Forget assignments (re-pair)**.

## Quest builds

* The Java plugin is `Assets/Plugins/Android/StickiesBle.java`. Unity compiles it into the APK.
  Select it once and check that **Android** is ticked in the Inspector.
* `Assets/Scripts/Editor/StickiesAndroidManifest.cs` adds `BLUETOOTH_SCAN`
  (`neverForLocation`), `BLUETOOTH_CONNECT` and legacy permissions to the generated manifest.
  No custom manifest template is needed.
* The first time the component starts on the headset, Horizon OS asks for "Nearby devices"
  permission. Accept it once in the headset. If it was denied, enable it under
  *Settings > Apps > (this app) > Permissions*.

## Hooks for data logging

* `WallProximityHaptics.Instance.ZoneChanged += (slot, zoneIndex, distance) => ...` fires on every
  zone change (`zoneIndex` is -1 when no wall is in range).
* `CommandSent += (slot, payload) => ...` fires for every vibration command actually sent, so
  `DataLogger` can record exactly when each participant was cued.
* `HapticsEnabled` turns all feedback off and on, for control conditions.

## BLE reference (from the Stickies docs and the StickyDebugger app)

| Item                    | Value                                                     |
|-------------------------|-----------------------------------------------------------|
| Advertised name         | starts with `STICKY`; feedback boards show `FB`           |
| Feedback service        | `00020000-7374-6963-6b79-535449434b59`                    |
| Motor Command (write)   | `00020001-7374-6963-6b79-535449434b59`                    |
| Play                    | `01, motor, u16 duration_ms, u8 count, u16 gap_ms, u8 intensity` (little-endian) |
| Stop                    | `00, motor` (`motor = 0xFF` stops all)                    |

## Not verified yet

This was built from the published API and the reference app source without Stickies on hand.
To check with real hardware:

* Exact advertised names, and whether one feedback board has more than one motor (`motorId`).
* That the cues feel distinct and comfortable for your participants (zone distances, pulse
  lengths, intensities).
