# Testing the crawl-turn fix

The fix is in `Assets/Scripts/KneelCrawlPose.cs`. It uses the avatar's calibrated head axes to maintain a kneeling heading, allowing small head glances and turning the pelvis when the existing VRIK turn allowance is exceeded. The current rig's allowance is 25°. Standing behavior and the tracked camera are untouched. No prefab wiring is required for the existing demo avatar.

**Without a headset:**

1. Exit Play mode and open **Tools → Avatar → Neck Turn Preview (No Headset)**.
2. Set **Head height** to **0.65 m**. Drag **Turn** through **90°, 180° and 360°**, or enable **Turn automatically**.
3. Set **Look down** to **90°** and repeat. Raise the head to **1.7 m**, then lower it again while turning.
4. The body should follow the larger turns and the head should remain aligned with its target. A small glance is allowed before the body turns. The displayed body-target heading gap should stay around the configured 25° allowance when fully kneeling; this is a target-heading measurement, not an anatomical neck-angle limit.
5. Click **Run automated turn checks**. The window should show **PASS fixed**. Results and before/after images are in `Reports/FirefighterNeckFlip/rig-tests`.

The preview loads a separate instance of the actual `Avatar+Can HandTracking` rig and runs its actual Final IK solver in an isolated Editor scene. It does not enter Play, connect to Normcore, start the house simulation or move the real XR camera. Equipment renderers are hidden in the preview so they cannot obscure the body. Procedural stepping is disabled for this deterministic pose test. The preview is disposed before entering Play mode.

**Results measured here:**

- The original rig failed the forward-turn checks when kneeling, crawling and looking down: its pelvis target remained facing forward during a 180° tracked-head turn.
- The fix passed **31 recorded pose checks**, including clockwise and reverse turns, downward gaze, a small glance, standing up and re-entering crawl. The transition also checks input preservation and finite bone poses over 180 frames.
- In the 180° kneeling case, solved head-to-target rotation error improved from **153.69° to 8.23°**. The pelvis target's heading gap improved from **180° to 25°**.
- In the tested 180° crawl and look-down cases, solved head-to-target error improved from approximately **148.02° / 141.99° to 0°**.
- The four original standing test results are unchanged. Simulated tracked input and the avatar's tracking target remain unchanged by the solver in all checked cases.

See [baseline results](rig-tests/baseline.json), [fixed results](rig-tests/fixed.json), [before](rig-tests/baseline-kneeling.png) and [after](rig-tests/fixed-kneeling.png).

**On the headsets before another demo:**

1. Build and install a fresh APK containing this change. An already-installed demo build will retain the old behavior.
2. Join the same room with two players. Have one observe the firefighter while the other stands, looks down, kneels, turns both directions, crawls and stands back up.
3. Check both the observer's avatar view and the wearer's first-person view. Record whether a failure affects the body only or the headset's view as well.
4. Repeat after a normal recenter and headset sleep/wake or tracking reacquisition. Those test tracking recovery, which the isolated Editor check cannot cover.

This fixes the demonstrated crawl-heading lock. It does not implement the report's separate tracking-health/recovery layer, and the exact demo incident still needs verification on the hardware. If the first-person camera itself flips, that remains a separate tracking/origin investigation.
