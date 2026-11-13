// Run in the open Editor without changing its scene or entering Play mode:
// unity command run_script --file Reports/GrabPullMovement/MovementChecks.cs --entry GrabPullChecks.Run --json
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using Unity.XR.CoreUtils;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.ProviderImplementation;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using Object = UnityEngine.Object;

public class GrabPullSyntheticHands : XRHandSubsystemProvider
{
    public static Vector3 Left = new Vector3(-.2f, .15f, .4f), Right = new Vector3(.2f, .15f, .4f);
    public static bool LeftTracked = true, RightTracked = false, Fist;
    public static float LeftPinch = .02f, RightPinch = .02f;
    public override void Start() { }
    public override void Stop() { }
    public override void Destroy() { }
    public override void GetHandLayout(NativeArray<bool> joints) { for (int i = 0; i < joints.Length; i++) joints[i] = true; }
    static void Fill(NativeArray<XRHandJoint> joints, Handedness side, Vector3 position, float pinch)
    {
        for (int i = 0; i < joints.Length; i++)
            joints[i] = XRHandProviderUtility.CreateJoint(side, XRHandJointTrackingState.Pose, XRHandJointIDUtility.FromIndex(i), new Pose(position, Quaternion.identity));
        foreach (var ids in new[] {
            new[] { XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip },
            new[] { XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip },
            new[] { XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip } })
        {
            var offsets = Fist ? new[] { Vector3.zero, new Vector3(0,0,.03f), new Vector3(0,.02f,.03f), new Vector3(0,.02f,0) }
                               : new[] { Vector3.zero, new Vector3(0,0,.02f), new Vector3(0,0,.04f), new Vector3(0,0,.06f) };
            for (int i = 0; i < 4; i++) joints[ids[i].ToIndex()] = XRHandProviderUtility.CreateJoint(side, XRHandJointTrackingState.Pose, ids[i], new Pose(position + offsets[i], Quaternion.identity));
        }
        var index = joints[XRHandJointID.IndexTip.ToIndex()]; index.TryGetPose(out var indexPose);
        joints[XRHandJointID.ThumbTip.ToIndex()] = XRHandProviderUtility.CreateJoint(side, XRHandJointTrackingState.Pose, XRHandJointID.ThumbTip, new Pose(indexPose.position + Vector3.right * pinch, Quaternion.identity));
    }
    public override XRHandSubsystem.UpdateSuccessFlags TryUpdateHands(XRHandSubsystem.UpdateType type,
        ref Pose leftRoot, NativeArray<XRHandJoint> left, ref Pose rightRoot, NativeArray<XRHandJoint> right)
    {
        Fill(left, Handedness.Left, Left, LeftPinch); Fill(right, Handedness.Right, Right, RightPinch);
        leftRoot = new Pose(Left, Quaternion.identity); rightRoot = new Pose(Right, Quaternion.identity);
        var flags = XRHandSubsystem.UpdateSuccessFlags.None;
        if (LeftTracked) flags |= XRHandSubsystem.UpdateSuccessFlags.LeftHandRootPose | XRHandSubsystem.UpdateSuccessFlags.LeftHandJoints;
        if (RightTracked) flags |= XRHandSubsystem.UpdateSuccessFlags.RightHandRootPose | XRHandSubsystem.UpdateSuccessFlags.RightHandJoints;
        return flags;
    }
}

public static class GrabPullChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static object Call(object target, string name, params object[] args)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            var method = type.GetMethod(name, Flags | BindingFlags.DeclaredOnly);
            if (method != null) return method.Invoke(target, args);
        }
        throw new Exception("Missing method: " + name);
    }
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    static void Check(List<string> passes, string name, bool condition)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        passes.Add(name);
    }
    public static object Run()
    {
        var passes = new List<string>();
        var initialScene = SceneManager.GetActiveScene(); bool initialDirty = initialScene.isDirty;
        var preview = EditorSceneManager.NewPreviewScene(); XRHandSubsystem hands = null; XRBodyTransformer body = null;
        bool[] spraying = (bool[])typeof(HandSprayGesture).GetField("s_Spraying", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var oldSpraying = (bool[])spraying.Clone();
        try
        {
            var descriptors = new List<XRHandSubsystemDescriptor>();
            string descriptorId = "FirefighterGrabPullChecks-" + Guid.NewGuid();
            XRHandSubsystemDescriptor.Register(new XRHandSubsystemDescriptor.Cinfo {
                id = descriptorId, providerType = typeof(GrabPullSyntheticHands) });
            SubsystemManager.GetSubsystemDescriptors(descriptors);
            hands = descriptors.Single(d => d.id == descriptorId).Create(); hands.Start();
            var existing = new List<XRHandSubsystem>(); SubsystemManager.GetSubsystems(existing);
            if (existing.Any(s => s != hands && s.running)) throw new Exception("Disconnect live hand tracking before running synthetic checks.");
            GameObject Create(string name, Transform parent = null)
            {
                var go = new GameObject(name); SceneManager.MoveGameObjectToScene(go, preview);
                if (parent != null) go.transform.SetParent(parent, false); return go;
            }
            var root = Create("Test XR Origin"); var origin = root.AddComponent<XROrigin>(); origin.Origin = root;
            var offset = Create("Camera Offset", root.transform); origin.CameraFloorOffsetObject = offset;
            var head = Create("Head", offset.transform); origin.Camera = head.AddComponent<Camera>();
            var locomotion = Create("Locomotion", root.transform);
            var mediator = locomotion.AddComponent<LocomotionMediator>(); Call(mediator, "Awake"); mediator.xrOrigin = origin;
            body = locomotion.GetComponent<XRBodyTransformer>(); Call(body, "OnEnable");
            var continuous = Create("Move", locomotion.transform).AddComponent<ContinuousMoveProvider>(); continuous.mediator = mediator;
            var walking = Create("Walking Stick", locomotion.transform).AddComponent<HandWalkingStickLocomotion>();
            Set(walking, "moveProvider", continuous); Set(walking, "head", head.transform); Set(walking, "trackingSpace", offset.transform);
            Call(walking, "Awake");
            var grab = walking.GetComponent<HandTrackingGrabMoveProvider>(); Call(grab, "Awake");
            Set(grab, "m_HandPositionSmoothing", 0f);
            Check(passes, "Existing rigs automatically gain exactly one grab provider", walking.GetComponents<HandTrackingGrabMoveProvider>().Length == 1 && grab.mediator == mediator);
            Check(passes, "Only continuous movement supplies gravity; grab stays horizontal", continuous.useGravity && !grab.useGravity && !grab.enableFreeYMovement);
            Vector3 Step(float height)
            {
                head.transform.localPosition = new Vector3(0, height - offset.transform.localPosition.y, 0);
                Call(walking, "UpdatePosture"); hands.TryUpdateHands(XRHandSubsystem.UpdateType.Dynamic);
                return (Vector3)Call(grab, "ComputeDesiredMove", new object[] { false });
            }
            void Reset() => Call(grab, "ResetMovementState");
            bool Zero(Vector3 v) => v.sqrMagnitude < .00000001f;
            GrabPullSyntheticHands.LeftTracked = true; GrabPullSyntheticHands.RightTracked = false;
            GrabPullSyntheticHands.LeftPinch = .02f; GrabPullSyntheticHands.RightPinch = .02f; GrabPullSyntheticHands.Fist = false;
            Check(passes, "Standing pinch cannot pull", Zero(Step(1.7f)) && !walking.IsCrawling);
            GrabPullSyntheticHands.Left += Vector3.back * .1f;
            Check(passes, "Standing hand motion cannot pull", Zero(Step(1.7f)));
            Set(walking, "_move", .8f);
            Check(passes, "Crouching clears swing momentum and anchors without a jump", Zero(Step(.8f)) && walking.IsCrawling && walking.MoveInput == 0 && continuous.leftHandMoveInput.manualValue == Vector2.zero);
            GrabPullSyntheticHands.Left += Vector3.back * .02f;
            var pull = Step(.8f);
            Check(passes, "Left-hand backward pull moves forward (actual " + pull.ToString("F4") + ")", pull.z > .08f && Mathf.Abs(pull.x) < .0001f && pull.y == 0);
            GrabPullSyntheticHands.Left += Vector3.right * .02f;
            Check(passes, "Sideways pull moves opposite the hand", Step(.8f).x < -.08f);
            GrabPullSyntheticHands.Left += Vector3.up * .25f;
            Check(passes, "Vertical hand motion cannot lift the rig", Zero(Step(.8f)));
            Check(passes, "Kneel/stand hysteresis retains crawling at 1.05m", Zero(Step(1.05f)) && walking.IsCrawling);
            Check(passes, "Standing at 1.1m stops grab", Zero(Step(1.1f)) && !walking.IsCrawling);
            Step(1.05f); Check(passes, "Hysteresis retains standing at 1.05m", !walking.IsCrawling);
            Check(passes, "Reentering crawl reanchors", Zero(Step(.8f)));
            GrabPullSyntheticHands.LeftPinch = .045f; GrabPullSyntheticHands.Left += Vector3.back * .02f;
            Check(passes, "Pinch remains latched between enter/release thresholds", Step(.8f).z > .08f);
            GrabPullSyntheticHands.LeftPinch = .06f; GrabPullSyntheticHands.Left += Vector3.back * .2f;
            Check(passes, "Opening the hand stops immediately", Zero(Step(.8f)));
            GrabPullSyntheticHands.LeftPinch = .045f;
            Check(passes, "A released pinch must close past the start threshold", Zero(Step(.8f)));
            GrabPullSyntheticHands.LeftPinch = .02f;
            Check(passes, "Closing again anchors without a jump", Zero(Step(.8f)));
            GrabPullSyntheticHands.RightTracked = true; GrabPullSyntheticHands.Right = new Vector3(.8f,.15f,.9f);
            Check(passes, "Adding the second hand reanchors instead of jumping", Zero(Step(.8f)));
            GrabPullSyntheticHands.Left += Vector3.back * .02f; GrabPullSyntheticHands.Right += Vector3.back * .02f;
            Check(passes, "Both pinching hands drive midpoint movement", Step(.8f).z > .08f);
            GrabPullSyntheticHands.LeftTracked = false;
            Check(passes, "Losing one hand reanchors the remaining hand", Zero(Step(.8f)));
            GrabPullSyntheticHands.Right += Vector3.back * .02f;
            Check(passes, "Right hand can pull on its own", Step(.8f).z > .08f);
            GrabPullSyntheticHands.RightTracked = false;
            Check(passes, "Losing all tracking stops movement", Zero(Step(.8f)));
            GrabPullSyntheticHands.LeftTracked = true; GrabPullSyntheticHands.Left += Vector3.back * 2f;
            Check(passes, "Tracking recovery reanchors", Zero(Step(.8f)));
            spraying[(int)Handedness.Left] = true; GrabPullSyntheticHands.Left += Vector3.back * .02f;
            Check(passes, "Spraying hand cannot pull", Zero(Step(.8f))); spraying[(int)Handedness.Left] = false;
            Check(passes, "Finishing spray reanchors", Zero(Step(.8f)));
            var interactor = Create("Holding Left Hand", root.transform).AddComponent<XRDirectInteractor>(); interactor.handedness = InteractorHandedness.Left;
            var item = Create("Held equipment", root.transform).AddComponent<XRGrabInteractable>();
            interactor.interactablesSelected.Add(item);
            typeof(XRBaseInteractor).GetProperty("hasSelection").SetValue(interactor, true);
            GrabPullSyntheticHands.Left += Vector3.back * .02f;
            Check(passes, "Hand holding equipment cannot pull", Zero(Step(.8f)));
            interactor.interactablesSelected.Clear();
            typeof(XRBaseInteractor).GetProperty("hasSelection").SetValue(interactor, false);
            Check(passes, "Releasing equipment reanchors", Zero(Step(.8f)));
            Reset(); Step(.8f); float slowDistance = 0;
            for (int i = 0; i < 90; i++) { GrabPullSyntheticHands.Left += Vector3.back * (.09f / 90f); slowDistance += Step(.8f).z; }
            Check(passes, "Slow 0.09m pull at 90Hz is accumulated", slowDistance > .37f && slowDistance < .42f);
            GrabPullSyntheticHands.Left += Vector3.back * 5f;
            Check(passes, "Tracking spike is capped at 0.4m/frame", Mathf.Abs(Step(.8f).magnitude - .4f) < .001f);
            Reset(); Step(.8f); GrabPullSyntheticHands.Left += Vector3.right * .001f;
            Check(passes, "Sub-deadzone jitter does not move", Zero(Step(.8f)));
            root.transform.position = new Vector3(3, 8, 5); Step(.8f);
            Check(passes, "Upper-floor rig position does not change posture selection", walking.IsCrawling);
            root.transform.rotation = Quaternion.Euler(0,90,0); Reset(); Step(.8f); GrabPullSyntheticHands.Left += Vector3.back * .02f;
            pull = Step(.8f); Check(passes, "Pull respects rotated rig coordinates", pull.x > .08f && Mathf.Abs(pull.z) < .001f);
            offset.transform.localRotation = Quaternion.Euler(0,90,0); Reset(); Step(.8f); GrabPullSyntheticHands.Left += Vector3.back * .02f;
            pull = Step(.8f); Check(passes, "Pull respects rotated camera-offset tracking space", pull.z < -.08f && Mathf.Abs(pull.x) < .001f);
            offset.transform.localRotation = Quaternion.identity; root.transform.rotation = Quaternion.identity;
            offset.transform.localPosition = Vector3.up * .2f; Step(1.15f);
            Check(passes, "Posture uses the floor, not the camera offset height", !walking.IsCrawling);
            offset.transform.localPosition = Vector3.zero;
            Step(1.7f); GrabPullSyntheticHands.Fist = true; GrabPullSyntheticHands.LeftPinch = .1f;
            for (int i = 0; i < 90; i++)
            {
                GrabPullSyntheticHands.Left.z = .4f + Mathf.Sin(i * .2f) * .2f;
                hands.TryUpdateHands(XRHandSubsystem.UpdateType.Dynamic); Call(walking, "EvaluateMovement", 1f / 90f);
            }
            Check(passes, "Standing fist swings still produce walking input", walking.MoveInput > .1f && continuous.leftHandMoveInput.manualValue.y > .1f);
            Step(.65f);
            for (int i = 0; i < 30; i++)
            {
                GrabPullSyntheticHands.Left.z = .4f + Mathf.Sin(i * .2f) * .2f;
                hands.TryUpdateHands(XRHandSubsystem.UpdateType.Dynamic); Call(walking, "EvaluateMovement", 1f / 90f);
            }
            Check(passes, "Crawling fist swings cannot produce walking or unpinched pull input", walking.MoveInput == 0 && continuous.leftHandMoveInput.manualValue == Vector2.zero && Zero(Step(.65f)));
            GrabPullSyntheticHands.Fist = false; GrabPullSyntheticHands.LeftPinch = .02f;
            // Exercise the default smoothing with a deterministic headset frame interval.
            Set(grab, "m_HandPositionSmoothing", .02f);
            foreach (int refreshRate in new[] { 72, 90, 120 })
            {
                Reset(); float distance = 0;
                for (int frame = 0; frame <= refreshRate; frame++)
                {
                    var handPosition = new Vector3(0,.15f,.4f - .09f * frame / refreshRate);
                    distance += ((Vector3)Call(grab, "ComputePull", new object[] { handPosition, 1, root.transform, 1f / refreshRate, false })).z;
                }
                Check(passes, "Default smoothing preserves deliberate slow pulls at " + refreshRate + "Hz", distance > .36f && distance < .42f);
            }
            Set(grab, "m_HandPositionSmoothing", 0f); Reset();
            var config = root.AddComponent<LocomotionModeConfigurator>(); config.Apply(LocomotionModeConfigurator.Mode.Teleport);
            Check(passes, "Teleport mode disables swing and grab providers", !walking.enabled && !grab.enabled && !continuous.enabled);
            config.Apply(LocomotionModeConfigurator.Mode.Natural);
            Check(passes, "Natural mode disables swing and grab providers", !walking.enabled && !grab.enabled && !continuous.enabled);
            config.Apply(LocomotionModeConfigurator.Mode.WalkingStick);
            Check(passes, "Walking-stick mode enables both providers", walking.enabled && grab.enabled && continuous.enabled);
            Step(.8f); GrabPullSyntheticHands.Left += Vector3.back * .1f; walking.enabled = false; Call(walking, "OnDisable");
            Check(passes, "Disabling locomotion clears posture and prevents stale pulls", Zero((Vector3)Call(grab, "ComputeDesiredMove", new object[] { false })) && !walking.IsCrawling);
            walking.enabled = true; Check(passes, "Reenabling while low reanchors", Zero(Step(.8f)));
            // Apply the real provider through XRI's body transformer, not a hand-written translation.
            GrabPullSyntheticHands.Left += Vector3.back * .02f; pull = Step(.8f); var before = root.transform.position;
            Call(grab, "MoveRig", pull); Call(body, "Update");
            Check(passes, "XRI body transformer actually moves the origin forward", root.transform.position.z > before.z + .08f);
            Check(passes, "XRI pull leaves origin height unchanged", Mathf.Abs(root.transform.position.y - before.y) < .0001f);
            var controller = root.AddComponent<CharacterController>(); controller.height = 1f; controller.radius = .2f;
            controller.center = Vector3.up * .5f; controller.skinWidth = .01f;
            Call(body, "OnDisable"); Call(body, "OnEnable");
            var wall = Create("Collision test wall"); wall.transform.position = root.transform.position + new Vector3(0,.5f,.5f);
            var wallCollider = wall.AddComponent<BoxCollider>(); wallCollider.size = new Vector3(4,4,.1f);
            Physics.SyncTransforms(); before = root.transform.position;
            for (int i = 0; i < 30; i++)
            {
                GrabPullSyntheticHands.Left += Vector3.back * .02f;
                pull = Step(.8f); Call(grab, "MoveRig", pull); Call(body, "Update"); Physics.SyncTransforms();
            }
            Check(passes, "Character controller stops repeated pulls at a wall", root.transform.position.z > before.z + .1f && root.transform.position.z < wall.transform.position.z - .2f);
            // The two shipping study scenes already reference the enhanced component; no rebuild needed.
            foreach (string path in new[] { "Assets/Scenes/Scenarios/Base.unity", "Assets/Scenes/Scenarios/Demo 10.23/Scenario 1.unity" })
            {
                var text = File.ReadAllText(path);
                Check(passes, Path.GetFileName(path) + " contains the enhanced movement component", text.Contains("guid: 70ea3414e0e559742a9dbf28c962878d") && text.Contains("moveProvider: {fileID: 1395329950}") && text.Contains("head: {fileID: 1395329944}"));
            }
        }
        finally
        {
            Array.Copy(oldSpraying, spraying, spraying.Length);
            if (body != null) Call(body, "OnDisable");
            if (hands != null) { hands.Stop(); hands.Destroy(); }
            EditorSceneManager.ClosePreviewScene(preview);
        }
        Check(passes, "User's active scene and unsaved state are preserved", SceneManager.GetActiveScene() == initialScene && initialScene.isDirty == initialDirty);
        var report = new { passed = true, checks = passes.Count, cases = passes,
            scope = "Actual Unity XR Hands synthetic subsystem, locomotion providers, XRI body transformation and CharacterController wall collision in an isolated Editor preview scene. Equipment selection/spray state is simulated. Physical headset tracking, networking and comfort require an in-headset check." };
        Directory.CreateDirectory("Reports/GrabPullMovement");
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented);
        File.WriteAllText("Reports/GrabPullMovement/results.json", json);
        return report;
    }
}
