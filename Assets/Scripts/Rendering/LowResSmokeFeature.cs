using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Draws the house smoke at a lower resolution than the rest of the frame, then
/// lays it over the frame. Up close every smoke card covers most of the view and
/// dozens of them stack on each pixel, so the smoke is limited by how many
/// pixels it fills: at half the resolution it fills a quarter as many.
/// </summary>
/// <remarks>
/// Only renderers on Smoke Layer are drawn this way. LowResSmokeEffects moves
/// the Ignis smoke effects onto it in Quest builds; the renderer's own
/// Transparent Layer Mask must leave that layer out, so the smoke is not drawn
/// twice. Nothing is on the layer on PC, so the feature does nothing there.
///
/// Before the main pass starts, the opaque scene is drawn into a small depth
/// buffer, depth only, and the smoke after it with the usual depth test, so
/// the stove, the furniture and the hands hide the smoke behind them as before.
/// Doing this ahead of the main pass keeps that one render pass on the
/// headset's tiled GPU. After the transparent objects, where the Ignis smoke
/// drew before (it sorts after the flames and the hose water), the smoke is
/// laid over the frame in one full-screen draw.
/// </remarks>
[DisallowMultipleRendererFeature("Low Res Smoke")]
public sealed class LowResSmokeFeature : ScriptableRendererFeature
{
    [Tooltip("Renderers drawn at the lower resolution. Leave this layer out of the renderer's Transparent Layer Mask.")]
    [SerializeField] LayerMask smokeLayer;

    [Tooltip("Far scenery left out of the depth-only copy of the scene the smoke is tested against, because it never stands in front of the smoke. QuestPerformanceSettings puts the skyline on it.")]
    [SerializeField] LayerMask farSceneryLayer;

    [Tooltip("Width and height of the smoke as a share of the frame's.")]
    [SerializeField, Range(0.2f, 1f)] float resolutionScale = 0.35f;

    [SerializeField] Shader compositeShader;

    /// <summary>Set while any smoke renderer on the layer is visible; nothing is drawn otherwise.</summary>
    public static bool HasSmoke;

    /// <summary>False draws the smoke layer at full resolution, as it was drawn before.</summary>
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

    // Made in Create: Unity does not allow ShaderTagIds in static initializers.
    static ShaderTagId[] smokeTags;
    static ShaderTagId[] depthTags;

    static readonly int SmokeTextureId = Shader.PropertyToID("_LowResSmokeTexture");
    static readonly int FrameTexelId = Shader.PropertyToID("_LowResSmokeFrameTexel");

    SmokePass smokePass;
    CompositePass compositePass;
    FullResolutionPass fullResolutionPass;
    Material compositeMaterial;

    public override void Create()
    {
        smokeTags ??= new[]
        {
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
        };
        depthTags ??= new[] { new ShaderTagId("DepthOnly") };

        smokePass = new SmokePass { renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses };
        compositePass = new CompositePass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
        fullResolutionPass = new FullResolutionPass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!(HasSmoke || DebugForceOn) || smokeLayer.value == 0)
            return;

        CameraType cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;

        fullResolutionPass.smokeLayer = smokeLayer;
        if (!LowRes || cameraType != CameraType.Game || !EnsureMaterial())
        {
            renderer.EnqueuePass(fullResolutionPass);
            return;
        }

        smokePass.smokeLayer = smokeLayer;
        // Everything the camera draws can stand in front of the smoke, except
        // the far scenery. The skyline alone is 1.3M triangles.
        smokePass.occluderLayers = renderingData.cameraData.camera.cullingMask & ~smokeLayer.value
            & (SkipFarScenery ? ~farSceneryLayer.value : ~0);
        smokePass.scale = Mathf.Clamp(ScaleOverride > 0f ? ScaleOverride : resolutionScale, 0.1f, 1f);
        compositePass.material = compositeMaterial;

        smokePass.drawOccluders = !DebugSkipOccluders;
        renderer.EnqueuePass(smokePass);
        if (!DebugSkipComposite)
            renderer.EnqueuePass(compositePass);
    }

    bool EnsureMaterial()
    {
        if (compositeMaterial == null && compositeShader != null && compositeShader.isSupported)
            compositeMaterial = CoreUtils.CreateEngineMaterial(compositeShader);
        return compositeMaterial != null;
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(compositeMaterial);
        compositeMaterial = null;
    }

    /// <summary>
    /// Whether URP's own passes into the frame are foveated, so a pass of ours
    /// matches them. A pass that differs ends the render pass there: on the
    /// headset's tiled GPU the whole multisampled frame is then written out to
    /// memory and read back, which costs milliseconds. URP foveates the frame
    /// when it is the headset's eye buffer, and also when it is an intermediate
    /// texture, except on standalone headsets with a scaled render viewport.
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

    /// <summary>The low-resolution smoke of this camera's frame, from SmokePass to CompositePass.</summary>
    sealed class SmokeFrame : ContextItem
    {
        public TextureHandle smoke;

        public override void Reset()
        {
            smoke = TextureHandle.nullHandle;
        }
    }

    sealed class SmokePass : ScriptableRenderPass
    {
        public LayerMask smokeLayer;
        public int occluderLayers;
        public float scale;
        public bool drawOccluders = true;

        // URP's depth-only passes also write depth into the red channel; that
        // is the smoke texture here, so they write no color at all.
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

        sealed class PassData
        {
            public RendererListHandle occluders;
            public RendererListHandle smoke;
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

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Low Res Smoke", out PassData data, profilingSampler))
            {
                data.occluders = Renderers(renderGraph, frameData, depthTags, RenderQueueRange.opaque,
                    SortingCriteria.CommonOpaque, drawOccluders ? occluderLayers : 0, DepthOnlyState);
                data.smoke = Renderers(renderGraph, frameData, smokeTags, RenderQueueRange.transparent,
                    SortingCriteria.CommonTransparent, smokeLayer.value, SmokeState);
                builder.UseRendererList(data.occluders);
                builder.UseRendererList(data.smoke);
                builder.SetRenderAttachment(smoke, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData passData, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(passData.occluders);
                    context.cmd.DrawRendererList(passData.smoke);
                });
            }

            frameData.GetOrCreate<SmokeFrame>().smoke = smoke;
        }
    }

    sealed class CompositePass : ScriptableRenderPass
    {
        public Material material;

        sealed class PassData
        {
            public Material material;
            public MaterialPropertyBlock properties;
            public TextureHandle smoke;
            public Vector4 frameTexel;
        }

        readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();

        public CompositePass()
        {
            profilingSampler = new ProfilingSampler("Low Res Smoke Composite");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!frameData.Contains<SmokeFrame>())
                return;
            TextureHandle smoke = frameData.Get<SmokeFrame>().smoke;
            if (!smoke.IsValid())
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Low Res Smoke Composite", out PassData data, profilingSampler))
            {
                data.material = material;
                data.properties = properties;
                data.smoke = smoke;
                RenderTextureDescriptor target = cameraData.cameraTargetDescriptor;
                data.frameTexel = new Vector4(1f / target.width, 1f / target.height, target.width, target.height);

                builder.UseTexture(smoke, AccessFlags.Read);
                // The same attachments and foveation as URP's transparent pass,
                // so this draw joins it in one render pass.
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.EnableFoveatedRasterization(FoveatesFrame(cameraData, resourceData));

                builder.SetRenderFunc((PassData passData, RasterGraphContext context) =>
                {
                    passData.properties.SetTexture(SmokeTextureId, passData.smoke);
                    passData.properties.SetVector(FrameTexelId, passData.frameTexel);
                    context.cmd.DrawProcedural(Matrix4x4.identity, passData.material, 0, MeshTopology.Triangles, 3, 1, passData.properties);
                });
            }
        }
    }

    /// <summary>Draws the smoke layer straight into the frame, as URP's transparent pass would.</summary>
    sealed class FullResolutionPass : ScriptableRenderPass
    {
        public LayerMask smokeLayer;

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
                    SortingCriteria.CommonTransparent, smokeLayer.value, null);
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
