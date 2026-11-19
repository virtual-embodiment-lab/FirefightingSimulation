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

## Session 2 (2026-11-19): low-resolution smoke

The decision above (half-res pass vs lighter smoke) was settled for the
half-res pass: it keeps the approved look. State found at the start: no fire or
smoke work was uncommitted. The working tree held only the user's own edits
(their "apk optimization" build renamed the app in ProjectSettings, Base.unity,
Bed/Cabinets/Couch prefabs, deleted Base occlusion data), line-ending-only
changes on several assets, and build leftovers. All of those are left alone.

What was built (Quest only; PC renders exactly as before):
- `LowResSmokeFeature` (URP RenderGraph renderer feature on `Mobile_Renderer`):
  before the main pass, it draws the opaque scene depth-only into a small
  depth buffer, then the smoke against it with the normal depth test, into a
  half-resolution RGBA16F texture (premultiplied color + opacity; blend state
  overridden to SrcAlpha/OneMinusSrcAlpha, One/OneMinusSrcAlpha). After the
  transparents (where the Ignis smoke drew before: smoke queue 3010 sorts after
  flames and hose water at 3000), `Hidden/LowResSmoke/Composite` lays it over
  the frame in one full-screen draw (Blend One OneMinusSrcAlpha). Both passes
  keep the main pass one native render pass (the composite declares the same
  color/depth attachments and foveation as URP's transparent pass). Statics for
  tuning: `LowRes` (false = old full-res path), `ScaleOverride`,
  `HighPrecision` (default true), `HasSmoke`.
- Layer 12 `LowResSmoke`; `Mobile_Renderer` Transparent Layer Mask leaves it out.
- `LowResSmokeEffects` (added by QuestPerformanceSettings on Android): every
  0.25 s moves VisualEffects playing Only_smoke_plume / _lingering /
  _contained / Window_smoke_vent onto the layer (not ones with a collider) and
  sets `HasSmoke` while any is visible; nothing runs when there is no smoke.
- `QuestPerformanceSettings`: `lowResSmoke` (default on), `smokeResolution`
  (0 = the feature's 0.5).
- Flames stay full resolution: Flame_box_wild_contained mixes additive flames
  with an alpha-blended Steam output in one renderer, so one blend override
  cannot fit both.

Rejected on the way (do not retry): depth slices composited at fixed depths.
The plume's 4x cards behind the stove were drawn over the stove and its flames.
A per-pixel scene depth is required; the depth-only prepass at low res is it.

Checked in the editor (same frame, 1280x960, S1 11.18 at the stove): the
half-res RGBA16F smoke matches the full-res smoke to about 1% in every region,
and the stove, grates, counters and flames stay in front of it. 8-bit
(R8G8B8A8_SRGB) comes out about 3% lighter and slightly warm (rounding of many
faint layers), hence 16-bit by default.

Plume dialed back on Quest (user, 2026-11-19: "i don't think the plume needs
to be that intense"): `IgnisSmokeDiffusion.plumeParticleSize` (default 1, PC
unchanged; `SetPlumeParticleSize` also updates a burning plume) and
`QuestPerformanceSettings.questPlumeParticleSize` = 0.6. In the editor 0.6
reads as a defined, billowing column over the stove with the flames visible;
1.0 is a uniform wall. The plume's smoke rate is no lever: at a fifth of it the
plume still holds 249 of its 250 particles.

Also found: 8 room lamps (`lamp_light`, point, intensity 0.2, range 10) cast
soft shadows (32 shadow maps a frame after URP drops 16). Measured: turning
them off changes nothing on the headset, so they stay.

### Headset runs (2.2 m from the stove, probe view; FPS from the probe, GPU ms from VrApi App=)

Run 1 (build 1: half-res 0.5, plume as on PC):
full-res smoke (old path) 13 FPS; half-res 0.5 ~18; 0.35 ~23; 0.25 ~25;
no room smoke 33; no flames +1.5; no fire effects at all 67 (GPU 11.8 ms,
CPU-bound: main thread ~10.7 ms). 8-bit vs 16-bit, window vent, lamp shadows:
no difference.

Run 2 (build 2: + plume 0.6 + pass flags matched to URP's):
| step | FPS | GPU ms |
|---|---|---|
| baseline (half-res 0.5, plume 0.6) | 25-27 | 29-37 |
| plume as on PC (1.0) | 23 | 37 |
| plume 0.4 | 27 | 30 |
| smoke 0.35 / 0.25 | 29 / 31 | 31 / 26 |
| smoke full-res (old path) | 12 | 71 |
| no plume | 30 | 26 |
| no flames | 29 | 30 |
| no room smoke (vent + flames remain) | 37 | 22 |
| no fire effects | 67 | 10.5 |
| pass with nothing to draw | 47 | 18 |

Rough split of the ~25 ms the fire adds at the stove: depth-only scene copy
~4.6, composite ~2, flames ~3.5, window vent ~2, lingering smoke ~6, plume ~7.
(`lowres-no-composite` came out at 44 FPS, too fast to be additive: the smoke
draws seem to be dropped when nothing reads the texture. Don't trust that step.)
Run 2 was hands-free: the headset sat on the desk with `prox_close` while
Quest Link was in front; run 1's first attempt was blocked by the lock/account
screen, which only the user can clear.

Run 3 (build 3, the "quick wins" the user approved): the skyline
(`ENVIRONMENT/Silhouettes`, 252 renderers, 1.34M tris, no colliders) moves to
layer 14 `FarScenery` on Quest (`QuestPerformanceSettings.farSceneryRoots`),
which the depth-only copy skips (`LowResSmokeFeature.farSceneryLayer`); smoke
at 0.35 (feature asset); plume 0.4 (`questPlumeParticleSize`). The neighbor
houses (674k tris) stay in the copy: from the street they can stand in front of
the smoke venting from a window. In the editor 0.35 matches full res except a
slightly stair-stepped smoke edge along counter and stove tops.
| step | FPS |
|---|---|
| baseline (all three) | 27.4 at the start, 24-25 later |
| skyline back in the copy | 25.0 |
| smoke at 0.5 | 22.0 |
| plume at 0.6 | 23.7 |
| smoke at 0.25 / near cap 1.0 | 27.8 / 27.0 |
| no plume / no room smoke / no flames | 27.3 / 35.2 / 30.0 |
| no fire effects | 67.3 |
| pass with nothing to draw | 50.5 (GPU 16.1 ms; run 2: 17.8) |
The steps inside the run add up to ~5 FPS for the three changes, but the
baseline only matches run 2's: the smoke and fire grow during each run, so
compare steps within a run, not baselines across builds. From frame times at
the stove now: lingering room smoke ~8 ms, flames ~6.7, the pass itself ~5
(depth copy ~3, composite ~2), plume ~3.4.

The user played build 3 in the headset (2026-11-19): "the narrower plume feels
pretty good", they did not notice the stair-stepped edge, and overall "much
much better than before". This state is committed as the fallback point.

## Session 2, part 2: flames at low resolution, composite inside URP's pass

The user (2026-11-19): 67 FPS "is super fine, its very buttery enough"; the goal
is to get the fire and smoke as close to that as possible, changing the look
little.

- Flames: the contained Wild flames go on layer 15 `LowResFlames` (left out of
  Mobile_Renderer's transparents). Their additive outputs (Shader Graph, no
  LightMode tag = SRPDefaultUnlit) go into a separate 8-bit texture, added to
  the frame before the smoke; their alpha outputs (Steam/Smoke, built-in,
  UniversalForwardOnly) go into the smoke texture ahead of the smoke. The flame
  texture must be 8-bit: in the 16-bit smoke texture, flames stacked past white
  shone through the smoke (stove-top flames far too bright and orange). With
  the separate texture the stove-top region matches to 0.001 in the editor.
- The composite was a render-graph pass of its own after the transparents.
  Probe run 5 showed it split the frame's render pass: ~4.7 ms with the pass
  present but drawing nothing, ~6 ms with the draw; the depth-only copy costs
  ~0.5 ms. Now the feature only leaves its results as global textures
  (`SetGlobalTextureAfterPass`), and two full-screen triangles of
  `LowResSmokeEffects` (`Hidden/LowResSmoke/Composite`, queues 3011 flames add,
  3012 smoke over) draw them inside URP's transparent pass, only for the
  headset camera (`RenderPipelineManager.beginCameraRendering`).
- `LowResSmokeFeature.LowRes = false` now moves the effects back to their own
  layers (URP draws them natively); the feature's own full-res pass is only
  for the editor's Scene view (it would split the pass too).
- Not done (#3 of the plan): the neighbor houses can't leave the depth copy:
  412 of their 439 renderers carry colliders, so their layer can't change, and
  the copy turned out to cost ~0.5 ms in all.

Runs at the stove (FPS):
| step | run 4 (flames low-res) | run 6 (+ composite in URP's pass) |
|---|---|---|
| baseline | 29-31 | 34-38 |
| flames back at full res | 26 | 30 |
| everything native at full res | - | 15 (GPU 70 ms) |
| no flames / no plume | 32 / 33 | 41 / 41 |
| no room smoke | 42 | 54 |
| no fire effects | 67 | 67 |
| smoke and flames at 0.25 | 34 | 43 |
| near-eye cap 1.0 | 31 | 37 |
| pass with nothing to draw | 52 (GPU 16.3) | 67 (GPU 10.6) |
Run 5 (diagnostic): no fire 67 / GPU 10.2; empty pass 51 / 16.5; empty without
the depth copy 53 / 16; empty without the composite pass 67 / 10.3; empty with
the composite pass drawing nothing 56 / ~15.

Probe hiccup: the first launch after the user signed back into the headset
found no fire: Normcore could not connect yet (Wi-Fi), and the scenario's fire
start waits on the multiplayer quorum. Check for the "holding the view" line
before trusting a run.

## Next steps

At the stove the frame is now ~35 FPS; the fire adds about 10 ms of room
smoke (plume ~4, lingering ~6), ~4 ms of flames and ~4 ms of the rest.
1. Smoke and flames at 0.25 instead of 0.35: +8 FPS (43). Softer, and the
   occlusion stair-steps get coarser: the user should judge it in the headset.
   Separate scales for smoke and flames would need a second depth copy
   (~0.5 ms).
2. Near-eye card cap 1.0: +3 FPS, smaller cards right at the face.
3. CPU: profile a development build (main thread ~10.5 ms with no fire).
4. Only a cheaper kind of room smoke (one analytic smoke layer per room on
   Quest, keeping the Ignis plume and vent) gets near 67 up close. That changes
   the approved lingering smoke look: the user decides.

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
- Building from the MCP: call `BuildPipeline.BuildPlayer` directly in the
  command (about 1.5-3 min; the call reports the build's warnings as an
  "error", read the logged result). `EditorApplication.delayCall` from a
  command never fires. Back up `ProjectSettings.asset` first and copy it back
  after; Unity also leaves or drops two XR `preloadedAssets` lines around builds.
- Editor captures: `RenderPipeline.SubmitRenderRequest(Camera.main, new
  StandardRequest { destination = rt })` (Unity_Camera_Capture fails on the XR
  camera). In Play mode the editor uses Mobile_RPAsset, so the feature runs.
- Never edit a .cs while the editor plays (the MCP locks until Stop).
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
