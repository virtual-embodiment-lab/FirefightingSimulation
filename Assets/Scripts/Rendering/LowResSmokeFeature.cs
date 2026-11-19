using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Draws the house smoke and the flames at a lower resolution than the rest of
/// the frame. Up close every smoke card covers most of the view and dozens of
/// them stack on each pixel, so the smoke is limited by how many pixels it
/// fills: at 0.35 of the resolution it fills an eighth as many.
/// </summary>
/// <remarks>
/// Only renderers on Smoke Layer and Flame Layer are drawn this way.
/// LowResSmokeEffects moves the Ignis effects onto them in Quest builds; the
/// renderer's own Transparent Layer Mask must leave both layers out, so they
/// are not drawn twice. Nothing is on the layers on PC, so the feature does
/// nothing there.
///
/// Before the main pass starts, the opaque scene is drawn into a small depth
/// buffer, depth only, and the effects after it with the usual depth test, so
/// the stove, the furniture and the hands hide the smoke behind them as before.
/// Doing this ahead of the main pass keeps that one render pass on the
/// headset's tiled GPU. The results are left as global textures, and two
/// full-screen triangles of LowResSmokeEffects lay them over the frame from
/// inside URP's own transparent pass, where the effects drew before (they sort
/// after the hose water). A pass of our own there split the frame's render
/// pass: the whole multisampled frame went out to memory and back, ~5 ms.
///
/// The Ignis flames are laid over the frame before the smoke, as they drew
/// before (their queue is 3000, the smoke's 3010). Their flames and embers add
/// light: they go into a texture of their own, in 8 bits like the frame, which
/// is added to the frame first, so it saturates there before the smoke over it
/// dims it, as before. (In the smoke's float texture, flames stacked far past
/// white shone through the smoke.) The steam and smoke in the same effect blend
/// by alpha and go into the smoke texture ahead of the smoke. The two kinds are
/// told apart by pass: the additive Shader Graph outputs have no LightMode tag,
/// the alpha ones are UniversalForwardOnly.
/// </remarks>
[DisallowMultipleRendererFeature("Low Res Smoke")]
public sealed class LowResSmokeFeature : ScriptableRendererFeature
{
    [Tooltip("Renderers drawn at the lower resolution. Leave this layer out of the renderer's Transparent Layer Mask.")]
    [SerializeField] LayerMask smokeLayer;

    [Tooltip("Flame effects drawn at the lower resolution, before the smoke: their untagged passes add light, their other passes blend by alpha. Leave this layer out of the renderer's Transparent Layer Mask.")]
    [SerializeField] LayerMask flameLayer;

    [Tooltip("Far scenery left out of the depth-only copy of the scene the smoke is tested against, because it never stands in front of the smoke. QuestPerformanceSettings puts the skyline on it.")]
    [SerializeField] LayerMask farSceneryLayer;

    [Tooltip("Width and height of the smoke as a share of the frame's.")]
    [SerializeField, Range(0.2f, 1f)] float resolutionScale = 0.35f;

    [Tooltip("Hidden/LowResSmoke/Composite, referenced here so it is in the build. LowResSmokeEffects draws with it.")]
    [SerializeField] Shader compositeShader;

    /// <summary>Set while any smoke or flame renderer on the layers is visible; nothing is drawn otherwise.</summary>
    public static bool HasSmoke;

    /// <summary>Set while any flame renderer on Flame Layer is visible; the flame texture is skipped otherwise.</summary>
    public static bool HasFlames;

    /// <summary>
    /// False puts the effects back where they drew before, at full resolution:
    /// LowResSmokeEffects moves them back to their own layers.
    /// </summary>
    public static bool LowRes = true;

    /// <summary>Above zero, replaces Resolution Scale.</summary>
    public static float ScaleOverride;

    /// <summary>False draws Far Scenery into the depth-only copy too.</summary>
    public static bool SkipFarScenery = true;

    /// <summary>
    /// Keeps the smoke in 16-bit floats instead of 8 bits per channel. Dozens
    /// of faint cards add up on each pixel, and in 8 bits each one's share is
    /// rounded: the smoke then comes out about 3% lighter and a little warmer
    /// than when drawn straight into the frame. In 16 bits it matches to 1%.
    /// </summary>
    public static bool HighPrecision = true;

    // For measuring what each part costs on the headset (QuestPerfProbe); they
    // change the look, so leave them off otherwise.
    /// <summary>Runs the passes even when no smoke is visible.</summary>
    public static bool DebugForceOn;
    /// <summary>Skips the depth-only scene, so the smoke draws over everything.</summary>
    public static bool DebugSkipOccluders;
    /// <summary>Skips laying the smoke over the frame, so it is not seen.</summary>
    public static bool DebugSkipComposite;

    /// <summary>The global textures the composite reads.</summary>
    public static readonly int SmokeTextureId = Shader.PropertyToID("_LowResSmokeTexture");
    public static readonly int FlamesTextureId = Shader.PropertyToID("_LowResFlamesTexture");

    /// <summary>
    /// Whether this frame's camera gets the low-resolution effects, so the
    /// composite triangles know whether to draw. Decided before culling, so it
    /// repeats the checks of AddRenderPasses that it can.
    /// </summary>
    public static bool DrawsLowRes(Camera camera)
    {
        return (HasSmoke || DebugForceOn) && LowRes && camera != null && camera.cameraType == CameraType.Game;
    }

    // Made in Create: Unity does not allow ShaderTagIds in static initializers.
    static ShaderTagId[] smokeTags;
    static ShaderTagId[] depthTags;
    static ShaderTagId[] additiveFlameTags;
    static ShaderTagId[] alphaFlameTags;

    SmokePass smokePass;
    FullResolutionPass fullResolutionPass;

    public override void Create()
    {
        smokeTags ??= new[]
        {
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
        };
        depthTags ??= new[] { new ShaderTagId("DepthOnly") };
        // A pass with no LightMode tag counts as SRPDefaultUnlit.
        additiveFlameTags ??= new[] { new ShaderTagId("SRPDefaultUnlit") };
        alphaFlameTags ??= new[] { new ShaderTagId("UniversalForward"), new ShaderTagId("UniversalForwardOnly") };

        smokePass = new SmokePass { renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses };
        fullResolutionPass = new FullResolutionPass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!(HasSmoke || DebugForceOn) || smokeLayer.value == 0)
            return;

        CameraType cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;

        if (cameraType != CameraType.Game)
        {
            // The editor's Scene view: the effects at full resolution, in a
            // pass of their own (it may split the render pass; it is a preview).
            // Flames sort before the smoke by queue, so one list draws both.
            fullResolutionPass.effectLayers = smokeLayer.value | flameLayer.value;
            renderer.EnqueuePass(fullResolutionPass);
            return;
        }

        if (!LowRes)
            return;

        smokePass.smokeLayer = smokeLayer;
        smokePass.flameLayer = flameLayer;
        smokePass.drawFlames = HasFlames && flameLayer.value != 0;
        // Everything the camera draws can stand in front of the smoke, except
        // the far scenery. The skyline alone is 1.3M triangles.
        smokePass.occluderLayers = renderingData.cameraData.camera.cullingMask & ~smokeLayer.value & ~flameLayer.value
            & (SkipFarScenery ? ~farSceneryLayer.value : ~0);
        smokePass.scale = Mathf.Clamp(ScaleOverride > 0f ? ScaleOverride : resolutionScale, 0.1f, 1f);
        smokePass.drawOccluders = !DebugSkipOccluders;
        renderer.EnqueuePass(smokePass);
    }

    /// <summary>
    /// Whether URP's own passes into the frame are foveated, so a pass of ours
    /// matches them. URP foveates the frame when it is the headset's eye
    /// buffer, and also when it is an intermediate texture, except on
    /// standalone headsets with a scaled render viewport.
    /// </summary>
    static bool FoveatesFrame(UniversalCameraData cameraData, UniversalResourceData resourceData)
    {
        if (!cameraData.xr.enabled || !cameraData.xr.supportsFoveatedRendering)
            return false;

        bool standaloneHeadset = Application.platform == RuntimePlatform.Android;
        bool intermediateFoveated = !standaloneHeadset || UnityEngine.Experimental.Rendering.XRSystem.GetRenderViewportScale() == 1f;
        return intermediateFoveated || resourceData.isActiveTargetBackBuffer;
    }

    static RendererListHandle Renderers(RenderGraph renderGraph, ContextContainer frameData, ShaderTagId[] tags,
        RenderQueueRange queues, SortingCriteria sorting, int layers, RenderStateBlock? state)
    {
        UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

        var desc = new RendererListDesc(tags, renderingData.cullResults, cameraData.camera)
        {
            sortingCriteria = sorting,
            renderQueueRange = queues,
            layerMask = layers,
            stateBlock = state,
        };
        return renderGraph.CreateRendererList(desc);
    }

    sealed class SmokePass : ScriptableRenderPass
    {
        public LayerMask smokeLayer;
        public LayerMask flameLayer;
        public int occluderLayers;
        public float scale;
        public bool drawOccluders = true;
        public bool drawFlames;

        // URP's depth-only passes also write depth into the red channel of
        // their target, here the flame texture, so they write no color at all.
        static readonly RenderStateBlock DepthOnlyState = new RenderStateBlock(RenderStateMask.Blend)
        {
            blendState = new BlendState(false, false)
            {
                blendState0 = new RenderTargetBlendState(0),
            },
        };

        // Every effect on the smoke layer blends by its alpha. Into a texture
        // that starts clear this leaves the smoke's color times its opacity, and
        // its opacity: what the composite needs to lay it over the frame.
        static readonly RenderStateBlock SmokeState = new RenderStateBlock(RenderStateMask.Blend)
        {
            blendState = new BlendState(false, false)
            {
                blendState0 = new RenderTargetBlendState(ColorWriteMask.All,
                    BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha,
                    BlendMode.One, BlendMode.OneMinusSrcAlpha,
                    BlendOp.Add, BlendOp.Add),
            },
        };

        // Flames add their light as they do in the frame (SrcAlpha, One) and
        // leave the opacity alone, so the frame behind them still shows.
        static readonly RenderStateBlock AdditiveState = new RenderStateBlock(RenderStateMask.Blend)
        {
            blendState = new BlendState(false, false)
            {
                blendState0 = new RenderTargetBlendState(ColorWriteMask.All,
                    BlendMode.SrcAlpha, BlendMode.One,
                    BlendMode.Zero, BlendMode.One,
                    BlendOp.Add, BlendOp.Add),
            },
        };

        sealed class PassData
        {
            public RendererListHandle occluders;
            public RendererListHandle additiveFlames;
            public RendererListHandle alphaFlames;
            public RendererListHandle smoke;
            public bool drawFlames;
        }

        public SmokePass()
        {
            profilingSampler = new ProfilingSampler("Low Res Smoke");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            RenderTextureDescriptor target = cameraData.cameraTargetDescriptor;
            int width = Mathf.Max(1, Mathf.RoundToInt(target.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(target.height * scale));

            var colorDesc = new TextureDesc(target)
            {
                name = "_LowResSmoke",
                width = width,
                height = height,
                format = HighPrecision ? GraphicsFormat.R16G16B16A16_SFloat
                    : QualitySettings.activeColorSpace == ColorSpace.Linear ? GraphicsFormat.R8G8B8A8_SRGB
                    : GraphicsFormat.R8G8B8A8_UNorm,
                msaaSamples = MSAASamples.None,
                bindTextureMS = false,
                useMipMap = false,
                autoGenerateMips = false,
                memoryless = RenderTextureMemoryless.None,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                clearBuffer = true,
                clearColor = Color.clear,
            };
            TextureHandle smoke = renderGraph.CreateTexture(colorDesc);

            var depthDesc = colorDesc;
            depthDesc.name = "_LowResSmokeDepth";
            depthDesc.format = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil);
            depthDesc.filterMode = FilterMode.Point;
            TextureHandle depth = renderGraph.CreateTexture(depthDesc);

            // The flames' light, in 8 bits like the frame: it saturates here as
            // it did in the frame, so the smoke over it dims the same amount.
            // In floats, flames stacked far past white would shine through.
            TextureHandle flames = TextureHandle.nullHandle;
            if (drawFlames)
            {
                var flameDesc = colorDesc;
                flameDesc.name = "_LowResFlames";
                flameDesc.format = QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? GraphicsFormat.R8G8B8A8_SRGB
                    : GraphicsFormat.R8G8B8A8_UNorm;
                flames = renderGraph.CreateTexture(flameDesc);
            }

            // First the depth-only scene, and the flames against it.
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Low Res Flames", out PassData data, profilingSampler))
            {
                data.occluders = Renderers(renderGraph, frameData, depthTags, RenderQueueRange.opaque,
                    SortingCriteria.CommonOpaque, drawOccluders ? occluderLayers : 0, DepthOnlyState);
                builder.UseRendererList(data.occluders);
                if (drawFlames)
                {
                    data.additiveFlames = Renderers(renderGraph, frameData, additiveFlameTags, RenderQueueRange.transparent,
                        SortingCriteria.CommonTransparent, flameLayer.value, AdditiveState);
                    builder.UseRendererList(data.additiveFlames);
                    builder.SetRenderAttachment(flames, 0, AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(flames, FlamesTextureId);
                }
                builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
                builder.AllowPassCulling(false);
                data.drawFlames = drawFlames;
                builder.SetRenderFunc((PassData passData, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(passData.occluders);
                    if (passData.drawFlames)
                        context.cmd.DrawRendererList(passData.additiveFlames);
                });
            }

            // Then the flames' steam and the smoke, against the same depth. The
            // render graph joins the two into one render pass, so the depth
            // never leaves the GPU's tile memory.
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Low Res Smoke", out PassData data, profilingSampler))
            {
                data.alphaFlames = Renderers(renderGraph, frameData, alphaFlameTags, RenderQueueRange.transparent,
                    SortingCriteria.CommonTransparent, drawFlames ? flameLayer.value : 0, SmokeState);
                data.smoke = Renderers(renderGraph, frameData, smokeTags, RenderQueueRange.transparent,
                    SortingCriteria.CommonTransparent, smokeLayer.value, SmokeState);
                builder.UseRendererList(data.alphaFlames);
                builder.UseRendererList(data.smoke);
                builder.SetRenderAttachment(smoke, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.Read);
                builder.SetGlobalTextureAfterPass(smoke, SmokeTextureId);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData passData, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(passData.alphaFlames);
                    context.cmd.DrawRendererList(passData.smoke);
                });
            }
        }
    }

    /// <summary>Draws the effect layers straight into the frame, as URP's transparent pass would.</summary>
    sealed class FullResolutionPass : ScriptableRenderPass
    {
        public int effectLayers;

        sealed class PassData
        {
            public RendererListHandle smoke;
        }

        public FullResolutionPass()
        {
            profilingSampler = new ProfilingSampler("Full Res Smoke");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Full Res Smoke", out PassData data, profilingSampler))
            {
                data.smoke = Renderers(renderGraph, frameData, smokeTags, RenderQueueRange.transparent,
                    SortingCriteria.CommonTransparent, effectLayers, null);
                builder.UseRendererList(data.smoke);
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.EnableFoveatedRasterization(FoveatesFrame(cameraData, resourceData));
                builder.SetRenderFunc((PassData passData, RasterGraphContext context) => context.cmd.DrawRendererList(passData.smoke));
            }
        }
    }
}
