# Quest 3 APK optimization: handoff notes

Branch `APKOptimization`, started 2026-11-17 from `SmokeSpread` (c32cf5bd).
These notes carry the state of a Claude Code session so the work can continue
on another machine. Claude: read this whole file before continuing.

## Goal

Standalone Quest 3 APK (not Link) of `Assets/Scenes/Scenarios/building/S1 11.18.unity`
(the only scene in the build list). The user wants it **very smooth** (Quest 3 has
no 60 Hz mode: target 72 FPS, 13.9 ms per frame) while looking **as close as
possible to the PC / Link look**, which they like. The Ignis smoke look on PC is
approved and locked: on Quest it may be cheapened only as far as needed, and the
user judges the look in the headset.

## What is measured (Quest 3, release APK, Vulkan, 72 Hz)

All from logcat on the headset (see Tools below). GPU time is VrApi `App=`.

Outside, at spawn, facing the burning house:
- Starts ~65-72 FPS (GPU 11-15 ms); as smoke fills the house it drops to
  39-50 FPS (GPU 18-25 ms). Hiding all smoke: GPU ~10.5 ms, 67-68 FPS.
- Post-processing ~1.5-3 ms; the skyline (`Silhouettes`, 1.34M of the scene's
  2.3M triangles, 252 renderers) ~2 ms; sun shadows, neighbor houses, TIC
  camera: within noise.
- Fixed foveation is level 2 in both old and new builds; turning it off costs
  6-7 FPS, so it stays.

In the burning room, 2.2 m from the stove at eye height (worst case, where the
user sees the low FPS):
| setting | FPS | GPU ms |
|---|---|---|
| as is | 9 | ~110 |
| smoke card cap 1.5 | 10-11 | ~95 |
| smoke card cap 1.0 | 13-14 | ~70 |
| smoke card cap 0.6 | 20-21 | ~48 |
| plume hidden | 24-25 | ~40 |
| cap 0.6 + flames 0.35 + foveation max | 24 | ~41 |

- The **plume** (`Only_smoke_plume`, 250 cards, `smokeParticleSize` 4, i.e. 4x
  Ignis size) is the single biggest cost: ~65-70 ms. Up close each card covers
  the whole view, so the overdraw is enormous.
- Even without the plume this view is ~40 ms: room smoke ~10-15 ms, flames
  ~5-10 ms, the rest of the scene ~10 ms. The window vent (512 particles) is
  just outside the kitchen window next to this viewpoint and not yet isolated.
- Fewer flame particles (0.35 share, brighter) made no measurable difference.
- CPU main thread is ~12-13 ms per frame: also at the limit; not yet profiled
  (needs a development build with the profiler).

## Changes on this branch

- `HazardTemperature`: cold hazards no longer rewrite MaterialPropertyBlocks
  every frame (498 objects, thousands of native calls a frame); a cold object
  never gets a block, so it stays in URP's SRP Batcher. The thermal shaders
  already default to ambient (0 heat, 21 C), so the TIC looks the same.
- 71 textures used by the scene and by the generator's Resources prefabs:
  Android cap 1024 -> 2048, trilinear, aniso 4 (fixes the "squishy" textures;
  APK ~131 -> 163 MB). Fire/smoke/XR textures untouched.
- OpenXR (Android + Meta Quest feature): foveated rendering API Legacy -> SRP
  Foveation, so `XRDisplaySubsystem.foveatedRenderingLevel` works.
- Player Settings: Frame Timing Stats on (for the probe).
- `QuestPerformanceSettings` now installs itself on Android when a scene has
  none (S1 11.18 had none), applies foveation 0.5, gives every TIC camera a
  `TicCameraBudget` (S1 11.18 had none, so the TIC re-rendered the whole scene
  every frame), disables controller-less Animators (33 on furniture), and holds
  the Quest budgets below. The auto-installed copy leaves the fire variant
  alone (IgnisSmokeDiffusion's contained Wild flames are the approved look).
  New fields, all neutral by default: `smokeNearCardWidth`, `smokeNearSkip`,
  `questFlameParticles`, `questFlameBrightness`, `disableBloom`.
- Near-eye smoke budget: `SmokeCardFacing.hlsl` reads a global
  `_SmokeNearBudget` (x: card width <= x * distance from the eyes; y: skip
  cards closer than y m). `ContainedSmokeBuilder` appends a `KeepNearSmokeCheap`
  Custom HLSL block to every smoke output; all smoke VFX + prefabs rebuilt
  (Tools > Smoke Containment > Build Contained Smoke). Zero on PC = unchanged.
- `QuestPerfProbe` (new): frame timing to logcat, and an A/B probe; only runs
  when launched with an adb intent extra (below).
- `S1 11.18.unity` and the build list (EditorBuildSettings) are committed so the
  build works on another machine. Quality Settings anti-aliasing 4 (user's).

Not on this branch / still elsewhere:
- `Mobile_Renderer` post-processing is **off** here (null post data). The heat
  vignette (PlayerHeatCue, URP Vignette) needs it on. The heat-cue detection
  code for IgnisSmokeDiffusion smoke and a post-on Mobile_Renderer are in the
  GitHub Desktop stash `!!GitHub_Desktop<dev>` on `dev` (machine-local).
- The user's other uncommitted edits (Base.unity, Bed/Cabinets/Couch prefabs,
  deleted Base occlusion data) were left out on purpose.

## Next step: decision pending

At the fire the frame is ~110 ms and must reach 13.9 ms. Options put to the user:
1. **Half-resolution fire/smoke pass** (closest to the PC look): render the
   smoke and flame VFX into a half-res offscreen target in their own URP
   RenderGraph pass (XR multiview: Tex2DArray, 2 slices; needs a depth copy for
   occlusion and premultiplied blending for the composite), excluding them from
   the main transparent pass by layer. ~4x cheaper fill. Alone likely 30-40 FPS
   up close; pair with a mild card cap (1.0) and foveation that rises only when
   the fire fills the view (adaptive, from frame time).
2. **Lighter smoke on Quest only**: cap ~0.6, fewer plume particles with more
   opacity (a Quest plume prefab with lower capacity, chosen by
   QuestPerformanceSettings), lighter room smoke near the viewer. Quick, but up
   close the smoke reads thinner/puffier than on PC.
Recommendation given: both, half-res pass first. Then CPU profiling.

## Tools and how-to

- adb (bundled with Unity):
  `C:/Program Files/Unity/Hub/Editor/6000.0.66f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe`
- Test builds used package `com.VirtualEmbodimentLab.questperf`, product name
  "Quest Perf Test", output `Builds/QuestPerf/questperf.apk` (git-ignored), so
  the user's own build stays installed. **Gotcha:** BuildPlayer saves Player
  Settings to disk mid-build, so a temporary identifier ends up in
  `ProjectSettings.asset` even after restoring it in memory. Restore the file
  too (or build with the real identifier).
- Probe: `adb shell am start -n <pkg>/com.unity3d.player.UnityPlayerGameActivity -e perf log`
  (timings every 2 s) or `-e perf probe` (A/B steps, 30 s warm-up, 2 rounds,
  results as `[QuestPerf] step ...` lines). In probe mode the view is held
  2.2 m from the burning fire at 1.7 m eye height, so the headset can sit on a
  table: the user turns the proximity sensor off in Meta Quest Developer Hub
  (or tapes it). Edit `QuestPerfProbe.Steps` to change what is measured.
- Capture: `adb logcat -c`, then `adb logcat -v time VrApi:I Unity:I *:S > log`
  while it runs. VrApi lines give FPS, `App=` (GPU ms), `Fov=` (foveation).
  Unity's own GPU timer reads 0 on this setup; use VrApi.
- Unity MCP `Unity_RunCommand`: no System.Reflection, no HashSet; Normcore/XRI
  types only via `System.Type.GetType`; a VFX change set via a global takes
  effect the next frame (output blocks run once per frame).

## Gotchas found

- VFX Graph Custom HLSL: an attribute counts as written only where the code
  assigns it (`attributes.size = ...`). Passed to a function it is read-only and
  the change is silently dropped. Dump generated code with
  `VisualEffectResource.GetShaderSource` (reachable from the builder assembly).
- The window vent graph (Ignis-derived) drops particles outside its `Spread`
  sphere; IgnisSmokeDiffusion sets `Spread_center/radius` on the strip.
- Rooms joined without a door header measure as one room (kitchen + hallway +
  bathroom in S1 11.18).
- `Mobile_Renderer`'s post data can be auto-filled in the editor while the file
  says null; builds use what is loaded in the editor.
