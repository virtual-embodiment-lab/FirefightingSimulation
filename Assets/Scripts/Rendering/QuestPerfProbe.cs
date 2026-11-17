using System.Collections.Generic;
using Ignis;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.VFX;
using UnityEngine.XR;

/// <summary>
/// Measures the frame in a Quest build and writes it to logcat (tag Unity,
/// lines starting [QuestPerf]), for tuning performance on the headset.
/// QuestPerformanceSettings adds it only when the app was launched with an
/// intent extra, so normal runs never pay for it:
///   adb shell am start -n &lt;package&gt;/&lt;activity&gt; -e perf log
///     every 2 s: FPS and the CPU main thread, CPU render thread and GPU time
///   ... -e perf probe
///     also turns likely costs off one at a time, with the untouched scene
///     measured between each, and logs what each one saves. The view is held
///     about 2 m from the fire at standing eye height, facing it, so the
///     headset can rest on a table (with its proximity sensor off or covered)
/// GPU and render thread times need Frame Timing Stats in Player Settings.
/// </summary>
public sealed class QuestPerfProbe : MonoBehaviour
{
    const float LogSeconds = 2f;
    // Each probe step settles for this long, then is measured for this long.
    const float SettleSeconds = 2f;
    const float MeasureSeconds = 4f;
    // Time for the smoke to build around the fire before measuring.
    const float ProbeStartSeconds = 30f;
    const int ProbeRounds = 2;

    readonly FrameTiming[] timings = new FrameTiming[1];
    readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
    bool probe;
    Window log;
    Window step;
    int stepIndex = -1;
    float stepStart;
    System.Action undo;

    struct Window
    {
        public int frames;
        public double seconds, cpuMain, cpuRender, gpu;

        public void Add(float delta, FrameTiming timing)
        {
            frames++;
            seconds += delta;
            cpuMain += timing.cpuMainThreadFrameTime;
            cpuRender += timing.cpuRenderThreadFrameTime;
            gpu += timing.gpuFrameTime;
        }

        public string Describe()
        {
            if (frames == 0)
                return "no frames";
            return "fps=" + (frames / seconds).ToString("F1") +
                   " cpuMain=" + (cpuMain / frames).ToString("F2") +
                   " cpuRender=" + (cpuRender / frames).ToString("F2") +
                   " gpu=" + (gpu / frames).ToString("F2") + " ms";
        }
    }

    struct Step
    {
        public string label;
        public System.Func<QuestPerfProbe, System.Action> apply;
    }

    // Measured standing in the burning room, where the frame rate drops: what
    // each kind of fire effect costs, and what each candidate budget saves.
    static readonly Step[] Steps =
    {
        new Step { label = "baseline", apply = p => null },
        new Step { label = "smoke-cap-1.5", apply = p => p.WithSmokeNearBudget(1.5f, 0f) },
        new Step { label = "baseline", apply = p => null },
        new Step { label = "smoke-cap-1.0", apply = p => p.WithSmokeNearBudget(1f, 0f) },
        new Step { label = "baseline", apply = p => null },
        new Step { label = "smoke-cap-0.6", apply = p => p.WithSmokeNearBudget(0.6f, 0f) },
        new Step { label = "baseline", apply = p => null },
        new Step { label = "no-plume", apply = p => p.HideEffects("Only_smoke_plume") },
        new Step { label = "baseline", apply = p => null },
        new Step { label = "flames-0.35", apply = p => p.WithFlames(0.35f, 1.55f) },
        new Step { label = "baseline", apply = p => null },
        new Step { label = "cap-1.0+flames+foveation", apply = p => p.All(p.WithSmokeNearBudget(1f, 0f), p.WithFlames(0.35f, 1.55f), p.WithFoveation(1f)) },
        new Step { label = "baseline", apply = p => null },
        new Step { label = "cap-0.6+flames+foveation", apply = p => p.All(p.WithSmokeNearBudget(0.6f, 0f), p.WithFlames(0.35f, 1.55f), p.WithFoveation(1f)) },
    };

    System.Action All(params System.Action[] undos)
    {
        return () =>
        {
            for (int i = undos.Length - 1; i >= 0; i--)
                undos[i]?.Invoke();
        };
    }

    public static void StartIfRequested(GameObject host)
    {
        string mode = LaunchExtra("perf");
        if (string.IsNullOrEmpty(mode))
            return;

        var probe = host.AddComponent<QuestPerfProbe>();
        probe.probe = mode == "probe";
        Debug.Log("[QuestPerf] started, mode=" + mode + ", frame timing stats " + (FrameTimingManager.IsFeatureEnabled() ? "on" : "off"));
    }

    static string LaunchExtra(string key)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = activity.Call<AndroidJavaObject>("getIntent");
            return intent.Call<string>("getStringExtra", key);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[QuestPerf] could not read the launch intent: " + e.Message);
        }
#endif
        return null;
    }

    // Where the probe holds the viewer: standing eye height, facing the fire.
    const float EyeHeight = 1.7f;
    const float FireDistance = 2.2f;
    XROrigin origin;
    bool viewPlaced;
    Vector3 viewPoint;
    Vector3 viewTarget;
    float nextViewSearch;

    void Update()
    {
        if (probe)
            HoldViewAtFire();

        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, timings) < 1)
            timings[0] = default;

        float delta = Time.unscaledDeltaTime;
        log.Add(delta, timings[0]);
        if (log.seconds >= LogSeconds)
        {
            Debug.Log("[QuestPerf] " + log.Describe());
            log = default;
        }

        if (probe)
            AdvanceProbe(delta);
    }

    /// <summary>
    /// Keeps the camera at a fixed spot facing the burning fire, by moving and
    /// turning the XR rig every frame, so the measurement does not depend on
    /// where the headset is or whether anyone is wearing it.
    /// </summary>
    void HoldViewAtFire()
    {
        if (!viewPlaced)
        {
            if (Time.realtimeSinceStartup < nextViewSearch)
                return;
            nextViewSearch = Time.realtimeSinceStartup + 1f;
            origin = FindAnyObjectByType<XROrigin>();
            if (origin == null || origin.Camera == null || !TryFindViewOfFire(out viewPoint, out viewTarget))
                return;
            viewPlaced = true;
            Debug.Log("[QuestPerf] holding the view at " + viewPoint.ToString("F2") + ", facing " + viewTarget.ToString("F2"));
        }

        if (origin == null || origin.Camera == null)
            return;

        Vector3 wanted = Vector3.ProjectOnPlane(viewTarget - viewPoint, Vector3.up);
        Vector3 facing = Vector3.ProjectOnPlane(origin.Camera.transform.forward, Vector3.up);
        if (wanted.sqrMagnitude > 0.0001f && facing.sqrMagnitude > 0.0001f)
        {
            float turn = Vector3.SignedAngle(facing, wanted, Vector3.up);
            if (Mathf.Abs(turn) > 0.5f)
                origin.RotateAroundCameraUsingOriginUp(turn);
        }
        origin.MoveCameraToWorldLocation(viewPoint);
    }

    /// <summary>
    /// A spot in the open part of the burning room, a couple of meters from the
    /// first burning fire, at standing eye height: the way out of the fire with
    /// the most open floor, found with level rays.
    /// </summary>
    static bool TryFindViewOfFire(out Vector3 eye, out Vector3 target)
    {
        eye = target = Vector3.zero;
        FlammableObject fire = null;
        foreach (FlammableObject candidate in FindObjectsByType<FlammableObject>(FindObjectsSortMode.None))
        {
            if (!candidate.onFire)
                continue;
            fire = candidate;
            if (candidate.setThisOnFireOnStart)
                break;
        }
        if (fire == null)
            return false;

        // A ray that starts inside the burning object passes through it, so
        // the first hit below its middle is the floor it stands on.
        Vector3 center = fire.transform.position;
        float floorY = Physics.Raycast(center + Vector3.up * 0.05f, Vector3.down, out RaycastHit floorHit, 5f, ~0, QueryTriggerInteraction.Ignore)
            ? floorHit.point.y
            : center.y - 0.5f;
        Vector3 from = new Vector3(center.x, floorY + 1.2f, center.z);

        Vector3 bestDirection = Vector3.forward;
        float bestFree = -1f;
        for (int i = 0; i < 16; i++)
        {
            Vector3 direction = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
            float free = Physics.Raycast(from, direction, out RaycastHit hit, 6f, ~0, QueryTriggerInteraction.Ignore) ? hit.distance : 6f;
            if (free > bestFree)
            {
                bestFree = free;
                bestDirection = direction;
            }
        }

        eye = from + bestDirection * Mathf.Clamp(bestFree - 0.6f, 1f, FireDistance);
        eye.y = floorY + EyeHeight;
        target = new Vector3(center.x, floorY + 0.8f, center.z);
        return true;
    }

    void AdvanceProbe(float delta)
    {
        float now = Time.realtimeSinceStartup;
        if (stepIndex < 0)
        {
            if (now < ProbeStartSeconds)
                return;
            BeginStep(0, now);
            return;
        }

        if (now - stepStart >= SettleSeconds)
            step.Add(delta, timings[0]);
        if (now - stepStart < SettleSeconds + MeasureSeconds)
            return;

        Debug.Log("[QuestPerf] step " + (stepIndex / Steps.Length + 1) + "/" + ProbeRounds + " " + Steps[stepIndex % Steps.Length].label + ": " + step.Describe());
        undo?.Invoke();
        undo = null;

        int next = stepIndex + 1;
        if (next >= Steps.Length * ProbeRounds)
        {
            Debug.Log("[QuestPerf] probe finished");
            probe = false;
            return;
        }

        BeginStep(next, now);
    }

    void BeginStep(int index, float now)
    {
        stepIndex = index;
        stepStart = now;
        step = default;
        undo = Steps[index % Steps.Length].apply(this);
    }

    System.Action WithoutPostProcessing()
    {
        Camera camera = Camera.main;
        if (camera == null)
            return null;

        UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
        bool before = data.renderPostProcessing;
        data.renderPostProcessing = false;
        return () => data.renderPostProcessing = before;
    }

    System.Action Hide(params string[] roots)
    {
        var restore = new List<Renderer>();
        foreach (string root in roots)
        {
            GameObject found = GameObject.Find(root);
            if (found == null)
                continue;
            foreach (Renderer renderer in found.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled)
                    continue;
                renderer.enabled = false;
                restore.Add(renderer);
            }
        }

        return () =>
        {
            foreach (Renderer renderer in restore)
                if (renderer != null)
                    renderer.enabled = true;
        };
    }

    System.Action WithFlames(float share, float brightness)
    {
        var changed = new List<(IgnisSmokeDiffusion diffusion, float share, float brightness)>();
        foreach (IgnisSmokeDiffusion diffusion in FindObjectsByType<IgnisSmokeDiffusion>(FindObjectsSortMode.None))
        {
            changed.Add((diffusion, diffusion.flameParticles, diffusion.flameBrightness));
            diffusion.flameParticles = share;
            diffusion.flameBrightness = brightness;
        }

        return () =>
        {
            foreach (var (diffusion, before, beforeBrightness) in changed)
            {
                if (diffusion == null)
                    continue;
                diffusion.flameParticles = before;
                diffusion.flameBrightness = beforeBrightness;
            }
        };
    }

    System.Action WithSmokeNearBudget(float cardWidth, float skip)
    {
        int id = Shader.PropertyToID("_SmokeNearBudget");
        Vector4 before = Shader.GetGlobalVector(id);
        Shader.SetGlobalVector(id, new Vector4(cardWidth, skip, 0f, 0f));
        return () => Shader.SetGlobalVector(id, before);
    }

    System.Action WithoutBloom()
    {
        var changed = new List<UnityEngine.Rendering.Universal.Bloom>();
        foreach (UnityEngine.Rendering.Volume volume in FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsSortMode.None))
        {
            UnityEngine.Rendering.VolumeProfile profile = volume.HasInstantiatedProfile() ? volume.profile : volume.sharedProfile;
            if (profile != null && profile.TryGet(out UnityEngine.Rendering.Universal.Bloom bloom) && bloom.active)
            {
                bloom.active = false;
                changed.Add(bloom);
            }
        }

        return () =>
        {
            foreach (var bloom in changed)
                bloom.active = true;
        };
    }

    System.Action HideEffects(string assetNamePart)
    {
        var restore = new List<Renderer>();
        foreach (VisualEffect effect in FindObjectsByType<VisualEffect>(FindObjectsSortMode.None))
        {
            if (effect.visualEffectAsset == null || effect.visualEffectAsset.name.IndexOf(assetNamePart, System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            Renderer renderer = effect.GetComponent<Renderer>();
            if (renderer == null || !renderer.enabled)
                continue;
            renderer.enabled = false;
            restore.Add(renderer);
        }

        return () =>
        {
            foreach (Renderer renderer in restore)
                if (renderer != null)
                    renderer.enabled = true;
        };
    }

    System.Action WithoutSunShadows()
    {
        Light sun = RenderSettings.sun;
        if (sun == null)
            return null;

        LightShadows before = sun.shadows;
        sun.shadows = LightShadows.None;
        return () => sun.shadows = before;
    }

    System.Action WithoutThermalCamera()
    {
        var paused = new List<TicCameraBudget>();
        var cameras = new List<Camera>();
        foreach (ThermalTemperatureDisplay display in FindObjectsByType<ThermalTemperatureDisplay>(FindObjectsSortMode.None))
        {
            Camera camera = display.GetComponent<Camera>();
            if (camera == null)
                continue;
            TicCameraBudget budget = camera.GetComponent<TicCameraBudget>();
            if (budget != null && budget.enabled)
            {
                budget.enabled = false;
                paused.Add(budget);
            }
            camera.enabled = false;
            cameras.Add(camera);
        }

        return () =>
        {
            foreach (Camera camera in cameras)
                if (camera != null)
                    camera.enabled = true;
            foreach (TicCameraBudget budget in paused)
                if (budget != null)
                    budget.enabled = true;
        };
    }

    System.Action WithEyeResolution(float scale)
    {
        float before = XRSettings.eyeTextureResolutionScale;
        XRSettings.eyeTextureResolutionScale = scale;
        return () => XRSettings.eyeTextureResolutionScale = before;
    }

    System.Action WithFoveation(float level)
    {
        SubsystemManager.GetSubsystems(displays);
        var changed = new List<(XRDisplaySubsystem display, float level)>();
        foreach (XRDisplaySubsystem display in displays)
        {
            if (!display.running)
                continue;
            changed.Add((display, display.foveatedRenderingLevel));
            display.foveatedRenderingLevel = level;
        }

        return () =>
        {
            foreach (var (display, before) in changed)
                display.foveatedRenderingLevel = before;
        };
    }
}
