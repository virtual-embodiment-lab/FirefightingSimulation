using System.Collections.Generic;
using Ignis;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

/// <summary>
/// Applies standalone Quest rendering budgets at startup. Only runs in Android
/// player builds, so PC / Quest Link sessions keep their current look.
/// </summary>
/// <remarks>
/// Every scene gets one: on Android it installs itself when a scene has none,
/// so a newly generated scene cannot lose it. An installed one leaves the fire
/// alone, since IgnisSmokeDiffusion already budgets the flames it contains;
/// only one placed in a scene by hand applies the fire budget below.
///
/// Launching the app with the intent extra perf (log or probe) also starts
/// QuestPerfProbe, which writes frame timings to logcat.
/// </remarks>
[DisallowMultipleComponent]
public sealed class QuestPerformanceSettings : MonoBehaviour
{
    [Header("Foveated Rendering")]
    [Tooltip("0 = off, 1 = strongest. Needs the OpenXR Foveated Rendering feature set to SRP Foveation.")]
    [SerializeField, Range(0f, 1f)] private float foveationLevel = 0.5f;

    [Header("OAVA Fire VFX")]
    [Tooltip("Switches the fire to the budget below. Off on the copy installed automatically.")]
    [SerializeField] private bool applyFireBudget = true;
    [SerializeField] private FlameEngine flameEngine;
    [Tooltip("Lively is OAVA's lightweight textured flame (~6.6k particles per fire vs ~31.6k for Wild).")]
    [SerializeField] private FlameEngine.FireVFXVariant fireVariant = FlameEngine.FireVFXVariant.Lively;
    [SerializeField, Range(0.1f, 1f)] private float fireParticleMultiplier = 0.6f;
    [SerializeField, Min(5f)] private float flameCullDistance = 40f;

    [Header("Smoke And Flames Up Close")]
    [Tooltip("A smoke card is drawn at most this many times as wide as its distance from the eyes, so a card right at the face covers part of the view instead of all of it. Inside a smoke layer dozens of such cards stack on every pixel. 0 leaves the smoke as on PC.")]
    [SerializeField, Min(0f)] private float smokeNearCardWidth;
    [Tooltip("Smoke cards closer to the eyes than this, in meters, are not drawn. 0 draws them all.")]
    [SerializeField, Min(0f)] private float smokeNearSkip;
    [Tooltip("Share of the contained Wild flame particles on Quest, in place of IgnisSmokeDiffusion's Flame Particles. Drawn at low resolution, the PC's 0.6 blurs into one solid sheet of flame; fewer leaves gaps between the tongues. 0 keeps that value.")]
    [SerializeField, Range(0f, 1f)] private float questFlameParticles = 0.3f;
    [Tooltip("Flame brightness on Quest, in place of IgnisSmokeDiffusion's Flame Brightness. 1 keeps the plugin's own; above it, stacked flames wash out to yellow. 0 keeps IgnisSmokeDiffusion's value.")]
    [SerializeField, Range(0f, 3f)] private float questFlameBrightness = 1f;
    [Tooltip("Size of the plume's particles on Quest, in place of IgnisSmokeDiffusion's Plume Particle Size: a narrower, more defined column over the fire that costs far less up close. 0 keeps that value.")]
    [SerializeField, Range(0f, 1f)] private float questPlumeParticleSize = 0.4f;
    [Tooltip("How opaque the plume's particles are on Quest, in place of IgnisSmokeDiffusion's Plume Opacity, so the narrower plume still hides the fire. Opacity costs no GPU time. 0 keeps that value.")]
    [SerializeField, Range(0f, 6f)] private float questPlumeOpacity = 4f;

    [Header("Smoke Resolution")]
    [Tooltip("Draws the house smoke at a lower resolution than the rest of the frame (LowResSmokeFeature on the Quest renderer), so up close it fills far fewer pixels. Soft smoke looks nearly the same.")]
    [SerializeField] private bool lowResSmoke = true;
    [Tooltip("Width and height of the smoke as a share of the frame's. 0 keeps the renderer feature's own setting.")]
    [SerializeField, Range(0f, 1f)] private float smokeResolution;
    [Tooltip("Scene objects, by name, whose renderers move to the FarScenery layer, which the low-resolution smoke's depth-only copy of the scene leaves out. Only scenery that can never stand in front of the smoke: the skyline. Not the neighbor houses, which can hide the smoke venting from a window. Objects with a collider are left alone.")]
    [SerializeField] private string[] farSceneryRoots = { "Silhouettes" };

    [Header("Post Processing")]
    [Tooltip("Turns Bloom off in the scene's volumes. Without HDR almost nothing is bright enough to bloom, but its blur passes still cost several full-screen draws.")]
    [SerializeField] private bool disableBloom;

    private const string FarSceneryLayer = "FarScenery";
    private static readonly int SmokeNearBudgetId = Shader.PropertyToID("_SmokeNearBudget");
    private readonly List<XRDisplaySubsystem> displays = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallIfMissing()
    {
        if (Application.platform != RuntimePlatform.Android || FindAnyObjectByType<QuestPerformanceSettings>() != null)
            return;

        // Inactive while it is set up, so Awake sees the fire budget turned off.
        var host = new GameObject("Quest Performance Settings");
        host.SetActive(false);
        host.AddComponent<QuestPerformanceSettings>().applyFireBudget = false;
        host.SetActive(true);
    }

    private void Awake()
    {
        if (Application.platform != RuntimePlatform.Android)
        {
            enabled = false;
            return;
        }

        if (applyFireBudget)
            ApplyFireBudget();
        ApplyCloseUpBudget();
        ApplySmokeResolution();
        if (disableBloom)
            DisableBloom();
        BudgetThermalCameras();
        DisableEmptyAnimators();
        QuestPerfProbe.StartIfRequested(gameObject);
    }

    private void Update()
    {
        // The XR display can come up a few frames after the scene loads.
        if (TryApplyFoveation())
            enabled = false;
    }

    private void ApplyFireBudget()
    {
        FlameEngine engine = flameEngine != null ? flameEngine : FlameEngine.instance;
        if (engine == null)
            return;

        engine.fireVFXVariant = fireVariant;
        engine.globalFireVFXMultiplier = fireParticleMultiplier;
        engine.flameLodCullDistance = flameCullDistance;
    }

    /// <summary>
    /// Limits what smoke and flames cost right in front of the eyes, where they
    /// cover the whole view: the smoke effects read the near budget as a global
    /// (SmokeNearBudget in SmokeCardFacing.hlsl), and the contained flames take
    /// their particle share and brightness from IgnisSmokeDiffusion.
    /// </summary>
    private void ApplyCloseUpBudget()
    {
        Shader.SetGlobalVector(SmokeNearBudgetId, new Vector4(smokeNearCardWidth, smokeNearSkip, 0f, 0f));

        if (questFlameParticles <= 0f && questFlameBrightness <= 0f && questPlumeParticleSize <= 0f && questPlumeOpacity <= 0f)
            return;

        foreach (IgnisSmokeDiffusion diffusion in FindObjectsByType<IgnisSmokeDiffusion>(FindObjectsSortMode.None))
        {
            if (questFlameParticles > 0f)
                diffusion.flameParticles = questFlameParticles;
            if (questFlameBrightness > 0f)
                diffusion.flameBrightness = questFlameBrightness;
            if (questPlumeParticleSize > 0f)
                diffusion.SetPlumeParticleSize(questPlumeParticleSize);
            // Read whenever a volume's opacity is applied, so a burning plume takes it too.
            if (questPlumeOpacity > 0f)
                diffusion.plumeOpacity = questPlumeOpacity;
        }
    }

    private void ApplySmokeResolution()
    {
        LowResSmokeFeature.LowRes = true;
        if (smokeResolution > 0f)
            LowResSmokeFeature.ScaleOverride = smokeResolution;

        if (!lowResSmoke)
            return;

        if (GetComponent<LowResSmokeEffects>() == null)
            gameObject.AddComponent<LowResSmokeEffects>();
        MoveFarScenery();
    }

    private void MoveFarScenery()
    {
        int layer = LayerMask.NameToLayer(FarSceneryLayer);
        if (layer < 0 || farSceneryRoots == null)
            return;

        foreach (string rootName in farSceneryRoots)
        {
            GameObject root = string.IsNullOrEmpty(rootName) ? null : GameObject.Find(rootName);
            if (root == null)
                continue;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.GetComponent<Collider>() == null)
                    renderer.gameObject.layer = layer;
            }
        }
    }

    private static void DisableBloom()
    {
        foreach (Volume volume in FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            VolumeProfile profile = volume.HasInstantiatedProfile() ? volume.profile : volume.sharedProfile;
            if (profile != null && profile.TryGet(out Bloom bloom))
                bloom.active = false;
        }
    }

    /// <summary>
    /// Gives every TIC camera a TicCameraBudget. Without one it renders the
    /// whole scene again every frame, whether or not its screen is in view.
    /// </summary>
    private static void BudgetThermalCameras()
    {
        Renderer[] renderers = null;
        foreach (ThermalTemperatureDisplay display in FindObjectsByType<ThermalTemperatureDisplay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Camera camera = display.GetComponent<Camera>();
            if (camera == null || camera.GetComponent<TicCameraBudget>() != null)
                continue;

            renderers ??= FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            camera.gameObject.AddComponent<TicCameraBudget>().Screen = FindScreen(renderers, camera.targetTexture);
        }
    }

    /// <summary>The renderer that shows a render texture, if any.</summary>
    private static Renderer FindScreen(Renderer[] renderers, Texture shown)
    {
        if (shown == null)
            return null;

        var ids = new List<int>();
        foreach (Renderer renderer in renderers)
        {
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null)
                    continue;

                material.GetTexturePropertyNameIDs(ids);
                foreach (int id in ids)
                {
                    if (material.GetTexture(id) == shown)
                        return renderer;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Imported furniture models carry Animators with no controller. They do
    /// nothing but still cost an update each frame.
    /// </summary>
    private static void DisableEmptyAnimators()
    {
        foreach (Animator animator in FindObjectsByType<Animator>(FindObjectsSortMode.None))
        {
            if (animator.runtimeAnimatorController == null)
                animator.enabled = false;
        }
    }

    private bool TryApplyFoveation()
    {
        SubsystemManager.GetSubsystems(displays);
        bool applied = false;
        foreach (XRDisplaySubsystem display in displays)
        {
            if (!display.running)
                continue;

            display.foveatedRenderingLevel = foveationLevel;
            display.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.GazeAllowed;
            applied = true;
        }

        return applied;
    }
}
