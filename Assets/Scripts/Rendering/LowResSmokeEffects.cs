using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.VFX;

/// <summary>
/// Moves the Ignis smoke effects onto the LowResSmoke layer and the contained
/// flames onto the LowResFlames layer as they appear, so LowResSmokeFeature
/// draws them at a lower resolution, tells the feature whether any of them is
/// in view, and lays the feature's results over the frame. QuestPerformanceSettings
/// adds it in Quest builds; disabled, it puts every effect back on its own layer.
/// </summary>
/// <remarks>
/// Only the effects ContainedSmokeBuilder builds are moved: the feature blends
/// everything on the smoke layer by its alpha, which every output of the smoke
/// effects does, and tells the flames' additive outputs from their alpha ones
/// (steam) by pass. Effects with a collider are left alone, since the layer
/// would change what they collide with.
///
/// The results are laid over the frame by two full-screen triangles drawn in
/// URP's own transparent pass (Hidden/LowResSmoke/Composite), the flames first
/// and then the smoke, right after the queue the effects drew in. They draw
/// only for the headset's camera, and only in frames the feature drew in.
/// </remarks>
[DisallowMultipleComponent]
public sealed class LowResSmokeEffects : MonoBehaviour
{
    public const string SmokeLayerName = "LowResSmoke";
    public const string FlameLayerName = "LowResFlames";
    const string CompositeShaderName = "Hidden/LowResSmoke/Composite";

    /// <summary>False puts the flames back at full resolution, where they drew before.</summary>
    public static bool LowResFlames = true;

    static readonly string[] SmokeEffects =
    {
        "Only_smoke_plume",
        "Only_smoke_lingering",
        "Only_smoke_contained",
        "Window_smoke_vent",
    };

    static readonly string[] FlameEffects =
    {
        "Flame_box_wild_contained",
    };

    const float ScanSeconds = 0.25f;
    // The flames drew in queue 3000 and the smoke in 3010; both composites come
    // right after the smoke, the flames' first.
    const int FlameCompositeQueue = 3011;
    const int SmokeCompositeQueue = 3012;

    struct Moved
    {
        public Renderer renderer;
        public int originalLayer;
        public bool flame;
    }

    readonly List<Moved> moved = new List<Moved>();
    readonly HashSet<int> seen = new HashSet<int>();
    int smokeLayer = -1;
    int flameLayer = -1;
    bool smokeLowRes;
    bool flamesLowRes;
    float nextScan;

    Mesh fullScreenTriangle;
    Material smokeComposite;
    Material flameComposite;
    MeshRenderer smokeCompositeRenderer;
    MeshRenderer flameCompositeRenderer;
    Camera headsetCamera;

    void OnEnable()
    {
        smokeLayer = LayerMask.NameToLayer(SmokeLayerName);
        flameLayer = LayerMask.NameToLayer(FlameLayerName);
        Shader composite = Shader.Find(CompositeShaderName);
        if (smokeLayer < 0 || composite == null || !composite.isSupported)
        {
            Debug.LogWarning("LowResSmokeEffects: the " + SmokeLayerName + " layer or the " + CompositeShaderName + " shader is missing, so the smoke stays at full resolution.", this);
            enabled = false;
            return;
        }

        CreateComposites(composite);
        smokeLowRes = LowResSmokeFeature.LowRes;
        flamesLowRes = smokeLowRes && LowResFlames && flameLayer >= 0;
        nextScan = 0f;
        RenderPipelineManager.beginCameraRendering += ShowCompositesFor;
        RenderPipelineManager.endCameraRendering += HideComposites;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= ShowCompositesFor;
        RenderPipelineManager.endCameraRendering -= HideComposites;
        LowResSmokeFeature.HasSmoke = false;
        LowResSmokeFeature.HasFlames = false;
        foreach (Moved entry in moved)
        {
            if (entry.renderer != null)
                entry.renderer.gameObject.layer = entry.originalLayer;
        }
        moved.Clear();
        seen.Clear();
        DestroyComposites();
    }

    void Update()
    {
        bool wantSmokeLowRes = LowResSmokeFeature.LowRes;
        bool wantFlamesLowRes = wantSmokeLowRes && LowResFlames && flameLayer >= 0;
        if (wantSmokeLowRes != smokeLowRes || wantFlamesLowRes != flamesLowRes)
        {
            smokeLowRes = wantSmokeLowRes;
            flamesLowRes = wantFlamesLowRes;
            foreach (Moved entry in moved)
            {
                if (entry.renderer != null)
                    entry.renderer.gameObject.layer = LayerFor(entry.flame, entry.originalLayer);
            }
        }

        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + ScanSeconds;
            Scan();
        }

        bool smokeVisible = false;
        bool flamesVisible = false;
        for (int i = moved.Count - 1; i >= 0; i--)
        {
            Moved entry = moved[i];
            if (entry.renderer == null)
            {
                moved.RemoveAt(i);
                continue;
            }

            // isVisible is false while the object is inactive or its renderer off.
            if (!entry.renderer.isVisible)
                continue;
            if (!entry.flame && smokeLowRes)
                smokeVisible = true;
            else if (entry.flame && flamesLowRes)
                flamesVisible = true;
        }

        LowResSmokeFeature.HasSmoke = smokeVisible || flamesVisible;
        LowResSmokeFeature.HasFlames = flamesVisible;
    }

    int LayerFor(bool flame, int originalLayer)
    {
        if (flame)
            return flamesLowRes ? flameLayer : originalLayer;
        return smokeLowRes ? smokeLayer : originalLayer;
    }

    void Scan()
    {
        foreach (VisualEffect effect in FindObjectsByType<VisualEffect>(FindObjectsSortMode.None))
        {
            if (seen.Contains(effect.GetInstanceID()) || effect.GetComponent<Collider>() != null)
                continue;

            bool smoke = Matches(effect.visualEffectAsset, SmokeEffects);
            bool flame = !smoke && flameLayer >= 0 && Matches(effect.visualEffectAsset, FlameEffects);
            if (!smoke && !flame)
                continue;

            Renderer renderer = effect.GetComponent<Renderer>();
            if (renderer == null)
                continue;

            seen.Add(effect.GetInstanceID());
            int originalLayer = effect.gameObject.layer;
            moved.Add(new Moved { renderer = renderer, originalLayer = originalLayer, flame = flame });
            effect.gameObject.layer = LayerFor(flame, originalLayer);
        }
    }

    static bool Matches(VisualEffectAsset asset, string[] names)
    {
        if (asset == null)
            return false;

        foreach (string name in names)
        {
            if (asset.name == name)
                return true;
        }

        return false;
    }

    void ShowCompositesFor(ScriptableRenderContext context, Camera camera)
    {
        if (headsetCamera == null)
            headsetCamera = Camera.main;

        bool show = camera == headsetCamera && LowResSmokeFeature.DrawsLowRes(camera) && !LowResSmokeFeature.DebugSkipComposite;
        smokeCompositeRenderer.enabled = show;
        flameCompositeRenderer.enabled = show && LowResSmokeFeature.HasFlames;
    }

    void HideComposites(ScriptableRenderContext context, Camera camera)
    {
        smokeCompositeRenderer.enabled = false;
        flameCompositeRenderer.enabled = false;
    }

    void CreateComposites(Shader shader)
    {
        // One triangle that covers the whole view; its vertices are in clip
        // space already. Huge bounds, so culling never drops it.
        fullScreenTriangle = new Mesh { name = "LowResSmokeFullScreen" };
        fullScreenTriangle.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(3f, -1f, 0f), new Vector3(-1f, 3f, 0f) };
        fullScreenTriangle.triangles = new[] { 0, 1, 2 };
        fullScreenTriangle.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);

        flameComposite = CompositeMaterial(shader, "LowResFlamesComposite", 1f,
            BlendMode.One, BlendMode.One, BlendMode.Zero, BlendMode.One, FlameCompositeQueue);
        smokeComposite = CompositeMaterial(shader, "LowResSmokeComposite", 0f,
            BlendMode.One, BlendMode.OneMinusSrcAlpha, BlendMode.One, BlendMode.OneMinusSrcAlpha, SmokeCompositeQueue);
        flameCompositeRenderer = CompositeRenderer("Low Res Flames Composite", flameComposite);
        smokeCompositeRenderer = CompositeRenderer("Low Res Smoke Composite", smokeComposite);
    }

    static Material CompositeMaterial(Shader shader, string name, float source,
        BlendMode src, BlendMode dst, BlendMode srcAlpha, BlendMode dstAlpha, int queue)
    {
        var material = new Material(shader) { name = name, renderQueue = queue };
        material.SetFloat("_Source", source);
        material.SetFloat("_SrcBlend", (float)src);
        material.SetFloat("_DstBlend", (float)dst);
        material.SetFloat("_SrcBlendAlpha", (float)srcAlpha);
        material.SetFloat("_DstBlendAlpha", (float)dstAlpha);
        return material;
    }

    MeshRenderer CompositeRenderer(string name, Material material)
    {
        var host = new GameObject(name);
        host.transform.SetParent(transform, false);
        host.AddComponent<MeshFilter>().sharedMesh = fullScreenTriangle;
        var renderer = host.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.allowOcclusionWhenDynamic = false;
        renderer.enabled = false;
        return renderer;
    }

    void DestroyComposites()
    {
        if (smokeCompositeRenderer != null)
            Destroy(smokeCompositeRenderer.gameObject);
        if (flameCompositeRenderer != null)
            Destroy(flameCompositeRenderer.gameObject);
        if (smokeComposite != null)
            Destroy(smokeComposite);
        if (flameComposite != null)
            Destroy(flameComposite);
        if (fullScreenTriangle != null)
            Destroy(fullScreenTriangle);
    }
}
