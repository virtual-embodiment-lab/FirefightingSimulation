using System.Collections.Generic;
using Ignis;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Applies standalone Quest rendering budgets at startup. Only runs in Android
/// player builds, so PC / Quest Link sessions keep their current look.
/// </summary>
[DisallowMultipleComponent]
public sealed class QuestPerformanceSettings : MonoBehaviour
{
    [Header("Foveated Rendering")]
    [Tooltip("0 = off, 1 = strongest. Needs the OpenXR Foveated Rendering feature set to SRP Foveation.")]
    [SerializeField, Range(0f, 1f)] private float foveationLevel = 0.5f;

    [Header("OAVA Fire VFX")]
    [SerializeField] private FlameEngine flameEngine;

    [Tooltip("Lively is OAVA's lightweight textured flame (~6.6k particles per fire vs ~31.6k for Wild).")]
    [SerializeField] private FlameEngine.FireVFXVariant fireVariant = FlameEngine.FireVFXVariant.Lively;

    [SerializeField, Range(0.1f, 1f)] private float fireParticleMultiplier = 0.6f;
    [SerializeField, Min(5f)] private float flameCullDistance = 40f;

    private readonly List<XRDisplaySubsystem> displays = new();

    private void Awake()
    {
        if (Application.platform != RuntimePlatform.Android)
        {
            enabled = false;
            return;
        }

        ApplyFireBudget();
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
