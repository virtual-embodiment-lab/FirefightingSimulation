using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RootMotion.FinalIK;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Uses the actual avatar and VRIK in an isolated preview scene. No headset, room, or Play session.
[InitializeOnLoad]
public static class AvatarNeckTurnTest
{
    const string Request = "Temp/AvatarNeckTurnTest.request";
    public const string Results = "Reports/FirefighterNeckFlip/rig-tests";

    static AvatarNeckTurnTest() { EditorApplication.update += Poll; }

    static void Poll()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying || !File.Exists(Request)) return;
        string command = File.ReadAllText(Request).Trim();
        File.Delete(Request);
        try
        {
            if (command == "window") AvatarNeckTurnWindow.Open();
            else Run(command == "baseline" ? "baseline" : "fixed");
        }
        catch (Exception e)
        {
            Directory.CreateDirectory(Results);
            File.WriteAllText(Path.Combine(Results, "error.txt"), e.ToString());
            Debug.LogException(e);
        }
    }

    [MenuItem("Tools/Avatar/Run Neck Turn Checks")]
    public static void RunMenu() { Run("fixed"); }

    [Serializable]
    public class Result
    {
        public string pose;
        public float inputYaw, rootYaw, pelvisYaw, pelvisHeadingError, actualHeadYaw, actualPelvisYaw, headTargetRotationError, kneelWeight;
        public bool finite, trackedInputUnchanged;
    }

    [Serializable]
    class Report
    {
        public string label;
        public bool passed;
        public string scope = "Actual avatar + VRIK in an isolated Editor preview; procedural stepping, headset tracking and networking are not exercised.";
        public List<Result> cases = new List<Result>();
        public List<string> failures = new List<string>();
    }

    public static void Run(string label)
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play mode to run the isolated avatar checks.");
        Directory.CreateDirectory(Results);
        var report = new Report { label = label };
        foreach (var pose in new[] { ("standing", 1.7f, 0f), ("kneeling", .85f, 0f), ("crawling", .45f, 60f), ("looking-down", .65f, 90f) })
        {
            using (var fixture = new AvatarNeckTurnFixture())
            {
                for (int frame = 0; frame < 60; frame++) fixture.Step(pose.Item2, 0, pose.Item3);
                foreach (float endYaw in new[] { 90f, 180f, 270f, 360f })
                {
                    for (int frame = 0; frame < 90; frame++) fixture.Step(pose.Item2, endYaw - 90 + frame + 1, pose.Item3);
                    for (int frame = 0; frame < 30; frame++) fixture.Step(pose.Item2, endYaw, pose.Item3);
                    var result = fixture.Measure(pose.Item1, endYaw);
                    report.cases.Add(result);
                    if (!result.finite || !result.trackedInputUnchanged) report.failures.Add(pose.Item1 + " invalid pose or changed tracked input at " + endYaw);
                    if (result.headTargetRotationError > 20) report.failures.Add($"{pose.Item1} yaw={endYaw}: solved head misses its target by {result.headTargetRotationError:F1} degrees");
                    if (pose.Item1 != "standing" && result.pelvisHeadingError > fixture.RootAllowance + 2)
                        report.failures.Add($"{pose.Item1} yaw={endYaw}: pelvis/head heading mismatch {result.pelvisHeadingError:F1} degrees");
                    if (pose.Item1 == "standing" && result.kneelWeight > .001f) report.failures.Add("Standing pose has a crawl override.");
                    if (endYaw == 180) fixture.SaveImage(Path.Combine(Results, label + "-" + pose.Item1 + ".png"));
                }
                if (pose.Item1 != "standing")
                {
                    foreach (float endYaw in new[] { 270f, 180f, 90f, 0f })
                    {
                        for (int frame = 0; frame < 90; frame++) fixture.Step(pose.Item2, endYaw + 90 - frame - 1, pose.Item3);
                        for (int frame = 0; frame < 30; frame++) fixture.Step(pose.Item2, endYaw, pose.Item3);
                        var result = fixture.Measure(pose.Item1 + "-reverse", endYaw); report.cases.Add(result);
                        if (!result.finite || !result.trackedInputUnchanged || result.pelvisHeadingError > fixture.RootAllowance + 2 || result.headTargetRotationError > 20)
                            report.failures.Add($"{result.pose} yaw={endYaw}: reverse turn failed");
                    }
                }
            }
        }
        using (var fixture = new AvatarNeckTurnFixture())
        {
            for (int i = 0; i < 60; i++) fixture.Step(.85f, 0, 0);
            for (int i = 0; i < 60; i++) fixture.Step(.85f, 20, 0);
            var glance = fixture.Measure("small-glance", 20); report.cases.Add(glance);
            if (Mathf.Abs(Mathf.DeltaAngle(0, glance.pelvisYaw)) > 1 || !glance.finite || !glance.trackedInputUnchanged)
                report.failures.Add("A small head glance unexpectedly turns the kneeling pelvis.");
            for (int frame = 0; frame < 180; frame++)
            {
                float t = frame / 179f;
                fixture.Step(Mathf.Lerp(.45f, 1.7f, t), Mathf.Lerp(20, 180, t), Mathf.Lerp(60, 0, t));
                var transition = fixture.Measure("standing-up", Mathf.Lerp(20, 180, t));
                if (!transition.finite || !transition.trackedInputUnchanged) report.failures.Add("Invalid pose during stand-up transition.");
            }
            for (int i = 0; i < 60; i++) fixture.Step(1.7f, 180, 0);
            var standing = fixture.Measure("standing-again", 180); report.cases.Add(standing);
            if (standing.kneelWeight > .001f || Mathf.Abs(fixture.MinimumHeadHeight - .8f) > .001f) report.failures.Add("Standing crawl overrides did not reset.");
            for (int i = 0; i < 60; i++) fixture.Step(.65f, 270, 60);
            var reentry = fixture.Measure("crawl-reentry", 270); report.cases.Add(reentry);
            if (reentry.kneelWeight < .99f || reentry.pelvisHeadingError > fixture.RootAllowance + 2 || !reentry.finite || !reentry.trackedInputUnchanged)
                report.failures.Add("Crawl heading did not reset correctly on re-entry.");
        }
        report.passed = report.failures.Count == 0;
        File.WriteAllText(Path.Combine(Results, label + ".json"), JsonUtility.ToJson(report, true));
        File.WriteAllText(Path.Combine(Results, "status.txt"), (report.passed ? "PASS " : "FAIL ") + label + "\n" + string.Join("\n", report.failures) + "\n");
        Debug.Log($"AVATAR_NECK_CHECK {label}: {(report.passed ? "PASS" : "FAIL")} ({report.cases.Count} poses). {Path.GetFullPath(Results)}");
    }
}

public sealed class AvatarNeckTurnFixture : IDisposable
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    readonly PreviewRenderUtility preview;
    readonly GameObject avatar, origin;
    readonly Transform hmd;
    readonly VRIK ik;
    readonly KneelCrawlPose crawl;
    readonly Quaternion headBasis, pelvisBasis, leftHandBasis, rightHandBasis;
    readonly MethodInfo evaluate;
    readonly bool explicitDelta;
    readonly Material floorMaterial;
    float height = 1.7f, yaw, pitch;
    bool inputUnchanged = true;

    public float RootAllowance => ik.solver.spine.maxRootAngle;
    public float MinimumHeadHeight => ik.solver.spine.minHeadHeight;

    public AvatarNeckTurnFixture()
    {
        preview = new PreviewRenderUtility();
        try
        {
            var scene = preview.camera.gameObject.scene;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Avatars/Resources/Avatar+Can HandTracking.prefab");
            if (prefab == null) throw new Exception("Demo avatar prefab is missing.");
            avatar = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            avatar.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            ik = avatar.GetComponentInChildren<VRIK>(true);
            crawl = ik.GetComponent<KneelCrawlPose>();
            if (crawl == null) throw new Exception("Demo avatar has no crawl component.");
            foreach (var behaviour in avatar.GetComponentsInChildren<Behaviour>(true))
                if (!(behaviour is VRIK) && !(behaviour is KneelCrawlPose)) behaviour.enabled = false;
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>()) renderer.updateWhenOffscreen = true;
            // Equipment is unrelated to the neck test and its unsimulated holster can obscure the body.
            foreach (var renderer in avatar.GetComponentsInChildren<MeshRenderer>()) renderer.enabled = false;

            var r = ik.references;
            headBasis = Quaternion.Inverse(r.root.rotation) * r.head.rotation;
            pelvisBasis = Quaternion.Inverse(r.root.rotation) * r.pelvis.rotation;
            leftHandBasis = r.leftHand.rotation; rightHandBasis = r.rightHand.rotation;
            ik.solver.spine.headTarget.SetPositionAndRotation(r.head.position, r.head.rotation);
            if (typeof(KneelCrawlPose).GetField("_ik", Private).GetValue(crawl) == null)
                typeof(KneelCrawlPose).GetMethod("Awake", Private).Invoke(crawl, null);
            ik.solver.SetToReferences(r);
            ik.solver.locomotion.weight = 0;
            ik.solver.Initiate(r.root);
            evaluate = typeof(KneelCrawlPose).GetMethod("EvaluatePose", Private);
            explicitDelta = evaluate != null;
            if (!explicitDelta) evaluate = typeof(KneelCrawlPose).GetMethod("UpdatePose", Private);
            ik.solver.OnPreUpdate += EvaluateCrawl;

            origin = new GameObject("Simulated XR Origin");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(origin, scene);
            hmd = new GameObject("Simulated tracked HMD input").transform; hmd.SetParent(origin.transform, false);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(floor, scene);
            floor.transform.localScale = Vector3.one * .5f;
            floorMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            floorMaterial.color = new Color(.24f, .27f, .29f); floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;
            Object.DestroyImmediate(floor.GetComponent<Collider>());
            preview.camera.nearClipPlane = .03f; preview.camera.farClipPlane = 30;
            preview.camera.fieldOfView = 45;
            preview.camera.backgroundColor = new Color(.12f, .15f, .19f);
            preview.camera.clearFlags = CameraClearFlags.Color;
            preview.camera.transform.position = new Vector3(2.6f, 1.8f, 3.2f);
            preview.camera.transform.LookAt(new Vector3(0, .8f, 0));
            preview.lights[0].intensity = 1.3f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 35, 0);
            preview.lights[1].intensity = 1; preview.lights[1].transform.rotation = Quaternion.Euler(15, 210, 0);
            preview.ambientColor = new Color(.45f, .45f, .45f);
        }
        catch { preview.Cleanup(); throw; }
    }

    void EvaluateCrawl()
    {
        if (!explicitDelta)
        {
            // Baseline only: the old component has no deterministic time-step entry point.
            typeof(KneelCrawlPose).GetMethod("SetWeight", Private).Invoke(crawl, new object[] { height < 1 ? 1f : 0f });
        }
        evaluate.Invoke(crawl, explicitDelta ? new object[] { 1f / 90 } : null);
    }

    public void Step(float headHeight, float headYaw, float lookDown)
    {
        height = headHeight; yaw = headYaw; pitch = lookDown;
        hmd.localPosition = new Vector3(0, headHeight, 0);
        hmd.localRotation = Quaternion.Euler(lookDown, headYaw, 0);
        avatar.transform.SetPositionAndRotation(origin.transform.position, origin.transform.rotation);
        var head = ik.solver.spine.headTarget;
        head.SetPositionAndRotation(hmd.TransformPoint(new Vector3(-.05f, -.075f, -.15f)), hmd.rotation * new Quaternion(.5f, -.5f, -.5f, .5f));
        var handHeading = Quaternion.Euler(0, headYaw, 0);
        float handHeight = headHeight < 1 ? .12f : headHeight - .45f;
        ik.solver.leftArm.target.SetPositionAndRotation(handHeading * new Vector3(-.35f, handHeight, .4f), handHeading * leftHandBasis);
        ik.solver.rightArm.target.SetPositionAndRotation(handHeading * new Vector3(.35f, handHeight, .4f), handHeading * rightHandBasis);
        Vector3 before = hmd.position, headBefore = head.position;
        Quaternion rotationBefore = hmd.rotation, headRotationBefore = head.rotation;
        ik.solver.FixTransforms(); ik.solver.Update();
        inputUnchanged &= Vector3.Distance(before, hmd.position) < .000001f && Quaternion.Angle(rotationBefore, hmd.rotation) < .001f
            && Vector3.Distance(headBefore, head.position) < .000001f && Quaternion.Angle(headRotationBefore, head.rotation) < .001f;
    }

    public AvatarNeckTurnTest.Result Measure(string name, float inputYaw)
    {
        var pelvisTarget = ik.solver.spine.pelvisTarget;
        float pelvisYaw = FacingYaw(pelvisTarget.rotation * Quaternion.Inverse(pelvisBasis));
        return new AvatarNeckTurnTest.Result
        {
            pose = name, inputYaw = inputYaw, rootYaw = FacingYaw(ik.references.root.rotation), pelvisYaw = pelvisYaw,
            pelvisHeadingError = Mathf.Abs(Mathf.DeltaAngle(inputYaw, pelvisYaw)), kneelWeight = crawl.Weight,
            actualHeadYaw = FacingYaw(ik.references.head.rotation * Quaternion.Inverse(headBasis)),
            actualPelvisYaw = FacingYaw(ik.references.pelvis.rotation * Quaternion.Inverse(pelvisBasis)),
            headTargetRotationError = Quaternion.Angle(ik.references.head.rotation, ik.solver.spine.headTarget.rotation),
            finite = ik.references.GetTransforms().Where(t => t != null).All(t => Finite(t.position) && Finite(t.rotation)),
            trackedInputUnchanged = inputUnchanged
        };
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w);
    static float FacingYaw(Quaternion q)
    {
        Vector3 forward = Vector3.Cross(q * Vector3.right, Vector3.up) + q * Vector3.forward;
        return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
    }

    public void SaveImage(string path)
    {
        preview.BeginStaticPreview(new Rect(0, 0, 960, 720)); preview.Render(true, false);
        var texture = preview.EndStaticPreview();
        try { File.WriteAllBytes(path, texture.EncodeToPNG()); } finally { Object.DestroyImmediate(texture); }
    }

    public void Draw(Rect rect)
    {
        preview.BeginPreview(rect, GUIStyle.none); preview.Render(true, false);
        GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.ScaleToFit, false);
    }

    public void Dispose()
    {
        ik.solver.OnPreUpdate -= EvaluateCrawl;
        preview.Cleanup();
        if (floorMaterial != null) Object.DestroyImmediate(floorMaterial);
    }
}

public sealed class AvatarNeckTurnWindow : EditorWindow
{
    AvatarNeckTurnFixture fixture;
    float height = .65f, yaw, pitch;
    bool sweep;
    double lastTime;

    [MenuItem("Tools/Avatar/Neck Turn Preview (No Headset)")]
    public static void Open() { GetWindow<AvatarNeckTurnWindow>("Avatar Neck Test").Show(); }
    void OnEnable() { EditorApplication.update += Tick; EditorApplication.playModeStateChanged += PlayModeChanged; lastTime = EditorApplication.timeSinceStartup; }
    void OnDisable() { EditorApplication.update -= Tick; EditorApplication.playModeStateChanged -= PlayModeChanged; fixture?.Dispose(); fixture = null; }
    void PlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode) { sweep = false; fixture?.Dispose(); fixture = null; }
    }
    void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        if (!EditorApplication.isPlaying && sweep && fixture != null)
        {
            yaw = Mathf.Repeat(yaw + (float)(now - lastTime) * 45, 360);
            fixture.Step(height, yaw, pitch); Repaint();
        }
        lastTime = now;
    }
    void OnGUI()
    {
        if (EditorApplication.isPlaying) { EditorGUILayout.HelpBox("Exit Play mode for this isolated preview.", MessageType.Info); return; }
        EditorGUILayout.LabelField("Real avatar + Final IK. No headset or multiplayer connection.", EditorStyles.wordWrappedLabel);
        EditorGUI.BeginChangeCheck();
        height = EditorGUILayout.Slider("Head height (m)", height, .4f, 1.8f);
        yaw = EditorGUILayout.Slider("Turn", yaw, 0, 360);
        pitch = EditorGUILayout.Slider("Look down", pitch, 0, 90);
        sweep = EditorGUILayout.Toggle("Turn automatically", sweep);
        bool changed = EditorGUI.EndChangeCheck();
        if (fixture == null)
        {
            fixture = new AvatarNeckTurnFixture();
            for (int i = 0; i < 60; i++) fixture.Step(height, yaw, pitch);
        }
        else if (changed) for (int i = 0; i < 40; i++) fixture.Step(height, yaw, pitch);
        var measurement = fixture.Measure("preview", yaw);
        float difference = measurement.kneelWeight > .001f ? measurement.pelvisHeadingError : Mathf.Abs(Mathf.DeltaAngle(yaw, measurement.actualPelvisYaw));
        EditorGUILayout.LabelField($"Kneeling: {measurement.kneelWeight:P0}    Body target heading gap: {difference:F1}°");
        EditorGUILayout.LabelField($"Avatar head-to-target error: {measurement.headTargetRotationError:F1}°");
        if (GUILayout.Button("Run automated turn checks")) AvatarNeckTurnTest.RunMenu();
        string status = Path.Combine(AvatarNeckTurnTest.Results, "status.txt");
        if (File.Exists(status))
        {
            string line = File.ReadLines(status).FirstOrDefault() ?? "";
            EditorGUILayout.HelpBox(line, line.StartsWith("PASS") ? MessageType.Info : MessageType.Warning);
        }
        Rect rect = GUILayoutUtility.GetRect(400, 400, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        if (Event.current.type == EventType.Repaint) fixture.Draw(rect);
    }
}
