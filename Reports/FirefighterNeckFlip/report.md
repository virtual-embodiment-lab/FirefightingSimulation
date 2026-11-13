# Firefighter neck-flip investigation — 12 November 2026

**Implementation follow-up:** The crawl-heading fix has now been implemented and tested on the actual avatar/VRIK in an isolated Editor scene. The original rig reproduced the heading lock; the updated rig passed 31 recorded pose checks. See [testing instructions and measured results](TESTING.md). The investigation below describes the original behavior; its initial mathematical audit is retained as historical evidence. Hardware reproduction of the demo incident remains outstanding.

The strongest code-backed suspect is the kneeling/crawling body-heading logic. At full kneel weight, it can keep the torso facing its previous direction while the tracked head turns. There is also no project-level recovery layer for a stale or discontinuous head pose before it reaches the avatar. These are actionable findings, but the exact demo incident is unconfirmed without a recording or pose trace.

The distinction that decides the investigation is whether the firefighter's **headset view** flipped or only their **avatar neck** looked wrong. The active IK path reads the headset camera and does not rotate it. A visual neck fix alone would therefore not repair an inverted first-person view.

**Scope and evidence.** This review used `dev` at commit `41a7bacc`, Unity 6000.0.66f1, Input System 1.17.0, Normcore 3.5.1 and the installed Final IK source. `Base.unity` is the enabled build scene. The APK/build used during the demo was not provided. No gameplay scripts, prefabs or scene settings were changed for this investigation.

**The active pose path.** In the build scene, the HMD Main Camera uses Unity's Input System Tracked Pose Driver. Its child `Head VR Target` supplies the calibrated head pose to `RealtimeAvatarManager`. The scene selects `Avatar+Can HandTracking`, which inherits `Avatar+Can`. Normcore copies the target into the avatar's `Head`; VRIK uses that same transform as its head target. VRIK solves the body separately from the XR camera.

The scene references are at [Base.unity](/Users/riomari/FirefightingSimulation/Assets/Scenes/Scenarios/Base.unity:198207). The shared avatar target and solver settings are in [Avatar+Can.prefab](/Users/riomari/FirefightingSimulation/Assets/Prefabs/Avatars/Resources/Avatar+Can.prefab:430). Normcore's installed `RealtimeAvatar.cs`, lines 164–167, copies the supplied transform without a quaternion/plausibility check; its head-active flag comes from the GameObject's activity, not HMD tracking validity. This matches Normcore's documented transform-based avatar design. [Normcore avatar documentation](https://docs.normcore.io/realtime/xr/avatars).

| Finding | Evidence and significance | Confidence |
| --- | --- | --- |
| Crawling can lock body heading | `KneelCrawlPose` derives pelvis heading from the current body forward direction and raises pelvis rotation weight to 1. VRIK then chooses the pelvis as its facing anchor, removing head-driven root turning from that decision. | Confirmed logic; strongest incident hypothesis if the person was low/turning. |
| Tracking discontinuities have no avatar recovery policy | Unity's driver respects its tracking-state input, but downstream avatar/IK code has no last-good-pose policy, continuity check, reacquisition handling or tracking-health signal. | Confirmed gap; an actual tracking event during the demo is unproven. |
| Low head-height glitches can activate the crawl pose | The crawl component checks head existence and height, then blends toward a fully controlled pelvis. It does not check tracking validity or pose age. | Confirmed susceptibility, not evidence of a recorded glitch. |
| Current logs cannot isolate the failure | `PlayerMovementTracker` records the transform it is attached to; it does not record the complete HMD/target/pelvis/neck chain or tracking flags. | Confirmed logging limitation. |

**How the crawl-mode problem happens.** [KneelCrawlPose.cs:125](/Users/riomari/FirefightingSimulation/Assets/Scripts/KneelCrawlPose.cs:125) takes heading from `transform.forward`. At [line 163](/Users/riomari/FirefightingSimulation/Assets/Scripts/KneelCrawlPose.cs:163), pelvis rotation weight follows the kneel blend and reaches 1. In [IKSolverVRSpine.cs:402](</Users/riomari/FirefightingSimulation/Assets/Plugins/RootMotion/FinalIK/IK Solvers/IKSolverVRSpine.cs:402>), full pelvis weight selects the pelvis orientation as the anchor. The following root-facing calculation uses that anchor. Meanwhile, head rotation weight remains 1.

This creates a feedback loop: the body determines pelvis heading, and the pelvis determines body heading. Turning while kneeling can leave a large head-to-torso mismatch for the spine/neck solver to reconcile. Leaning changes the anatomical axes, so the resulting distortion need not look like a simple horizontal twist. The exact resulting bone pose needs a full-rig reproduction.

The included mathematical audit isolates this heading decision, starting at body yaw 0 with the configured 25-degree root allowance:

| Head yaw | Standing root correction | Fully kneeling root correction |
| --- | --- | --- |
| 45° | 20° | 0° |
| 90° | 65° | 0° |
| 135° | 110° | 0° |
| 180° | 155° | 0° |

These are checks of the facing calculation, not measured final neck rotations or a reproduction of the demo. The full-kneel result remains independent of head yaw for tested pelvis leans of 0, 45 and 90 degrees.

**What I would not diagnose as the cause from this review.** The older `HeadTracker`, `XRCameraTracker` and `IKTargetFollowVRRig` are not driving this active prefab/scene path. The unusual head-target quaternion `(0.5, -0.5, -0.5, 0.5)` is a unit quaternion used for bone-axis alignment. The calculated difference from the base prefab's neutral head orientation is approximately 8.70 degrees, not 180 degrees; replacing it with identity would require a proper recalibration. A quaternion changing from `q` to `-q` also represents the same orientation. `FirstPersonHeadHider` changes rendered mesh visibility, not head or camera rotation. There is no evidence here that network lag itself generated an inverted pose.

**Preventative changes, in priority order.**

1. **Preserve body turning during kneel/crawl.** Give the pelvis a heading that can follow validated head/body turning with a turn allowance, while retaining its crawl lean and position. Use the calibrated head-to-body basis and a stable horizontal heading near vertical gaze. Keep the last valid heading when projection is ambiguous. Verify this on both the owning player and remote observers.
2. **Add a validated avatar-head target between XR tracking and Normcore.** Check tracking validity, freshness, finite position/quaternion components, usable quaternion magnitude and contextual pose discontinuities. Preserve the last valid avatar pose briefly during loss; recover from confirmed relocalization instead of accepting a jump as normal motion or permanently rejecting a new tracking origin. Keep the headset camera under XR control. Unity already provides tracking-state gating; this would add avatar-level continuity and recovery. [Unity Tracked Pose Driver 1.17 documentation](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.17/api/UnityEngine.InputSystem.XR.TrackedPoseDriver.html).
3. **Add a body-relative neck fallback.** Evaluate orientation in the avatar's calibrated anatomical axes, then turn/redistribute the torso or blend the avatar toward its last valid pose if limits are exceeded. Tune limits to preserve natural downward gaze and crawling. `headClampWeight = 0.6` is a solver correction clamp, not a complete anatomical-limit/recovery system; changing that slider alone does not address the heading loop.
4. **Handle recenter, tracking reacquisition and reconnect explicitly.** Reestablish a sane tracking frame, clear stale avatar/crawl filter state, then reset/reblend IK. Final IK exposes `solver.Reset()` for solver state; it is not a headset calibration or a remedy for invalid target data. Keep XR Origin upright during normal locomotion. [Unity XR Origin documentation](https://docs.unity3d.com/Packages/com.unity.xr.core-utils@2.5/api/Unity.XR.CoreUtils.XROrigin.html).
5. **Record the pose chain around anomalies.** Capture client/owner ID, build revision, tracking flags, sample age, raw HMD pose, XR Origin rotation, calibrated target pose, transmitted/received avatar target, pelvis/neck/head poses and kneel weight. A short ring buffer on the owner and observer would show the first stage where inversion appears.

If the actual headset view flipped, priorities 2, 4 and 5 come first. Compare raw HMD local rotation with the camera's world rotation and its parent XR Origin rotation. A normal local HMD pose with an inverted world view points toward a parent/origin transformation; an inverted raw pose points earlier in tracking. If only the remote avatar flipped, compare owner and receiver targets before attributing it to transport, and inspect the crawl/IK stage.

**Validation before another demo.** Run the same rig on two clients, observing both the player's headset view and the remote avatar. Test standing look-down and turns; kneel/crawl turns through 90/180/360 degrees; looking down while standing up; headset removal/reseat, tracking loss/reacquisition, sleep/wake and recenter; and reconnect or delayed updates. Include deliberately invalid poses and `q`/`-q` equivalent rotations in controlled pose playback. Acceptance requires no persistent head-to-torso inversion, correct body following, stable recovery, and no IK or animation writes to the tracked camera.

**Checks completed for this report.** The read-only [audit script](audit.py) verifies scene/prefab target wiring, the HMD driver's tracking-state configuration, calibration quaternion properties and the isolated standing/kneeling heading calculation. Results are in [audit-results.json](audit-results.json). No hardware reproduction, two-client Play test or full VRIK regression test was performed. The demo's precise trigger remains unconfirmed.

**Saved smoke/collision work.** All prior tracked and untracked working changes were stashed as `wip/smoke-fire-collider-investigation-2026-11-12-unverified`, stash commit `2c79631a42b846381bf2faef12fdcb8edebd8476`. The working tree was clean after the stash. To recover that snapshot later:

```sh
git stash apply 2c79631a42b846381bf2faef12fdcb8edebd8476
```
