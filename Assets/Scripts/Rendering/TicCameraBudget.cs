using UnityEngine;

/// <summary>
/// Renders the TIC camera only while its screen is visible, and on Android at a
/// reduced rate and resolution. The render texture keeps its last frame in between.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class TicCameraBudget : MonoBehaviour
{
    [Tooltip("The TIC display. The camera is skipped while no camera can see it.")]
    [SerializeField] private Renderer screen;

    [Tooltip("Android only. 2 = render every other frame (36 Hz at 72 Hz).")]
    [SerializeField, Min(1)] private int androidFrameInterval = 2;

    [Tooltip("Android only. Square size of the thermal render texture; 0 keeps the asset size.")]
    [SerializeField, Min(0)] private int androidResolution = 512;

    private Camera ticCamera;
    private int frameInterval = 1;

    private void Awake()
    {
        ticCamera = GetComponent<Camera>();
        if (Application.platform != RuntimePlatform.Android)
            return;

        frameInterval = androidFrameInterval;
        ResizeTarget(ticCamera.targetTexture, androidResolution);
    }

    private void LateUpdate()
    {
        bool visible = screen == null || screen.isVisible;
        ticCamera.enabled = visible && Time.frameCount % frameInterval == 0;
    }

    private void OnDisable()
    {
        if (ticCamera != null)
            ticCamera.enabled = true;
    }

    private static void ResizeTarget(RenderTexture target, int size)
    {
        if (target == null || size <= 0 || (target.width <= size && target.height <= size))
            return;

        target.Release();
        target.width = size;
        target.height = size;
        target.Create();
    }
}
