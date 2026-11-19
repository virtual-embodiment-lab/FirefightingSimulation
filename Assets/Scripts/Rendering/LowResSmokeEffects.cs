using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;

/// <summary>
/// Moves the Ignis smoke effects onto the LowResSmoke layer as they appear, so
/// LowResSmokeFeature draws them at a lower resolution, and tells the feature
/// whether any of them is in view. QuestPerformanceSettings adds it in Quest
/// builds; disabled, it puts every effect back on its own layer.
/// </summary>
/// <remarks>
/// Only the smoke effects ContainedSmokeBuilder builds are moved: the feature
/// blends everything on the layer by its alpha, and every output of these does.
/// The flames stay at full resolution: they are additive and share an effect
/// with steam that is not. Effects with a collider are left alone, since the
/// layer would change what they collide with.
/// </remarks>
[DisallowMultipleComponent]
public sealed class LowResSmokeEffects : MonoBehaviour
{
    public const string LayerName = "LowResSmoke";

    static readonly string[] SmokeEffects =
    {
        "Only_smoke_plume",
        "Only_smoke_lingering",
        "Only_smoke_contained",
        "Window_smoke_vent",
    };

    const float ScanSeconds = 0.25f;

    readonly List<Renderer> renderers = new List<Renderer>();
    readonly List<int> originalLayers = new List<int>();
    readonly HashSet<int> seen = new HashSet<int>();
    int layer = -1;
    float nextScan;

    void OnEnable()
    {
        layer = LayerMask.NameToLayer(LayerName);
        if (layer < 0)
        {
            Debug.LogWarning("LowResSmokeEffects: there is no " + LayerName + " layer, so the smoke stays at full resolution.", this);
            enabled = false;
            return;
        }

        nextScan = 0f;
    }

    void OnDisable()
    {
        LowResSmokeFeature.HasSmoke = false;
        for (int i = 0; i < renderers.Count; i++)
        {
            if (renderers[i] != null)
                renderers[i].gameObject.layer = originalLayers[i];
        }
        renderers.Clear();
        originalLayers.Clear();
        seen.Clear();
    }

    void Update()
    {
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + ScanSeconds;
            Scan();
        }

        bool visible = false;
        for (int i = renderers.Count - 1; i >= 0; i--)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
            {
                renderers.RemoveAt(i);
                originalLayers.RemoveAt(i);
                continue;
            }

            // isVisible is false while the object is inactive or its renderer off.
            if (renderer.isVisible)
                visible = true;
        }

        LowResSmokeFeature.HasSmoke = visible;
    }

    void Scan()
    {
        foreach (VisualEffect effect in FindObjectsByType<VisualEffect>(FindObjectsSortMode.None))
        {
            if (seen.Contains(effect.GetInstanceID()) || !IsSmoke(effect.visualEffectAsset) || effect.GetComponent<Collider>() != null)
                continue;

            Renderer renderer = effect.GetComponent<Renderer>();
            if (renderer == null)
                continue;

            seen.Add(effect.GetInstanceID());
            renderers.Add(renderer);
            originalLayers.Add(effect.gameObject.layer);
            effect.gameObject.layer = layer;
        }
    }

    static bool IsSmoke(VisualEffectAsset asset)
    {
        if (asset == null)
            return false;

        foreach (string name in SmokeEffects)
        {
            if (asset.name == name)
                return true;
        }

        return false;
    }
}
