using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Rendering modes for Universal renderer.
    /// </summary>
    public enum RenderingMode
    {
        /// <summary>Render all objects and lighting in one pass, with a hard limit on the number of lights that can be applied on an object.</summary>
        Forward = 0,
        /// <summary>Render all objects and lighting in one pass using a clustered data structure to access lighting data.</summary>
        [InspectorName("Forward+")]
        ForwardPlus = 2,
        /// <summary>Render all objects first in a g-buffer pass, then apply all lighting in a separate pass using deferred shading.</summary>
        Deferred = 1
    };

    /// <summary>
    /// When the Universal Renderer should use Depth Priming in Forward mode.
    /// </summary>
    public enum DepthPrimingMode
    {
        /// <summary>Depth Priming will never be used.</summary>
        Disabled,
        /// <summary>Depth Priming will only be used if there is a depth prepass needed by any of the render passes.</summary>
        Auto,
        /// <summary>A depth prepass will be explicitly requested so Depth Priming can be used.</summary>
        Forced,
    }

    /// <summary>
    /// Default renderer for Universal RP.
    /// This renderer is supported on all Universal RP supported platforms.
    /// It uses a classic forward rendering strategy with per-object light culling.
    /// </summary>
    public sealed partial class UniversalRenderer : ScriptableRenderer
    {
#if UNITY_SWITCH || UNITY_ANDROID || UNITY_EMBEDDED_LINUX || UNITY_QNX
        const GraphicsFormat k_DepthStencilFormatDefault = GraphicsFormat.D24_UNorm_S8_UInt;
#else
        const GraphicsFormat k_DepthStencilFormatDefault = GraphicsFormat.D32_SFloat_S8_UInt;
        #endif

        const int k_FinalBlitPassQueueOffset = 1;
        const int k_AfterFinalBlitPassQueueOffset = k_FinalBlitPassQueueOffset + 1;

        static readonly List<ShaderTagId> k_DepthNormalsOnly = new List<ShaderTagId> { new ShaderTagId("DepthNormalsOnly") };

        /// <inheritdoc/>
        public override int SupportedCameraStackingTypes()
        {
            switch (m_RenderingMode)
            {
                case RenderingMode.Forward:
                case RenderingMode.ForwardPlus:
                    return 1 << (int)CameraRenderType.Base | 1 << (int)CameraRenderType.Overlay;
                case RenderingMode.Deferred:
                    return 1 << (int)CameraRenderType.Base;
                default:
                    return 0;
            }
        }

        /// <inheritdoc/>
        protected internal override bool SupportsMotionVectors()
        {
            // Motion vector pass for TAA and per-object motion blur (etc.) is available.
            return true;
        }

        // Rendering mode setup from UI. The final rendering mode used can be different. See renderingModeActual.
        internal RenderingMode renderingModeRequested => m_RenderingMode;

        // Actual rendering mode, which may be different (ex: wireframe rendering, hardware not capable of deferred rendering).
        internal RenderingMode renderingModeActual => renderingModeRequested == RenderingMode.Deferred && (GL.wireframe || (DebugHandler != null && DebugHandler.IsActiveModeUnsupportedForDeferred) || m_DeferredLights == null || !m_DeferredLights.IsRuntimeSupportedThisFrame() || m_DeferredLights.IsOverlay)
        ? RenderingMode.Forward
        : this.renderingModeRequested;

        bool m_Clustering;

        internal bool accurateGbufferNormals => m_DeferredLights != null ? m_DeferredLights.AccurateGbufferNormals : false;

#if ADAPTIVE_PERFORMANCE_2_1_0_OR_NEWER
        internal bool needTransparencyPass { get { return !UniversalRenderPipeline.asset.useAdaptivePerformance || !AdaptivePerformance.AdaptivePerformanceRenderSettings.SkipTransparentObjects;; } }
#endif
        /// <summary>Property to control the depth priming behavior of the forward rendering path.</summary>
        public DepthPrimingMode depthPrimingMode { get { return m_DepthPrimingMode; } set { m_DepthPrimingMode = value; } }
        DepthOnlyPass m_DepthPrepass;
        DepthNormalOnlyPass m_DepthNormalPrepass;
        MotionVectorRenderPass m_MotionVectorPass;
        MainLightShadowCasterPass m_MainLightShadowCasterPass;
        AdditionalLightsShadowCasterPass m_AdditionalLightsShadowCasterPass;
        GBufferPass m_GBufferPass;
        DeferredPass m_DeferredPass;
        DrawObjectsPass m_RenderOpaqueForwardOnlyPass;
        DrawObjectsPass m_RenderOpaqueForwardPass;
        DrawObjectsWithRenderingLayersPass m_RenderOpaqueForwardWithRenderingLayersPass;
        DrawSkyboxPass m_DrawSkyboxPass;
        CopyDepthPass m_CopyDepthPass;
        CopyColorPass m_CopyColorPass;
        bool m_ShouldTransparentsReceiveShadows;
        DrawObjectsPass m_RenderTransparentForwardPass;
        InvokeOnRenderObjectCallbackPass m_OnRenderObjectCallbackPass;
        FinalBlitPass m_FinalBlitPass;
        CapturePass m_CapturePass;
#if ENABLE_VR && ENABLE_XR_MODULE
        XROcclusionMeshPass m_XROcclusionMeshPass;
        CopyDepthPass m_XRCopyDepthPass;
        XRDepthMotionPass m_XRDepthMotionPass;
#endif
#if UNITY_EDITOR
        CopyDepthPass m_FinalDepthCopyPass;
        ProbeVolumeDebugPass m_ProbeVolumeDebugPass;
#endif
        DrawScreenSpaceUIPass m_DrawOffscreenUIPass;
        DrawScreenSpaceUIPass m_DrawOverlayUIPass;

        CopyColorPass m_HistoryRawColorCopyPass;
        CopyDepthPass m_HistoryRawDepthCopyPass;

        RTHandle m_TargetColorHandle;
        RTHandle m_TargetDepthHandle;

        ForwardLights m_ForwardLights;
        DeferredLights m_DeferredLights;
        RenderingMode m_RenderingMode;
        DepthPrimingMode m_DepthPrimingMode;
        CopyDepthMode m_CopyDepthMode;
        DepthFormat m_CameraDepthAttachmentFormat;
        DepthFormat m_CameraDepthTextureFormat;
        bool m_DepthPrimingRecommended;
        StencilState m_DefaultStencilState;
        LightCookieManager m_LightCookieManager;
        IntermediateTextureMode m_IntermediateTextureMode;
        bool m_VulkanEnablePreTransform;

        // Materials used in URP Scriptable Render Passes
        Material m_BlitMaterial = null;
        Material m_BlitHDRMaterial = null;
        Material m_SamplingMaterial = null;
        Material m_StencilDeferredMaterial = null;
        Material m_CameraMotionVecMaterial = null;

        PostProcessPasses m_PostProcessPasses;
        internal ColorGradingLutPass colorGradingLutPass { get => m_PostProcessPasses.colorGradingLutPass; }
        internal PostProcessPass postProcessPass { get => m_PostProcessPasses.postProcessPass; }
        internal PostProcessPass finalPostProcessPass { get => m_PostProcessPasses.finalPostProcessPass; }
        internal RTHandle colorGradingLut { get => m_PostProcessPasses.colorGradingLut; }
        internal DeferredLights deferredLights { get => m_DeferredLights; }
        internal LayerMask opaqueLayerMask { get; set; }
        internal LayerMask transparentLayerMask { get; set; }

        internal GraphicsFormat cameraDepthTextureFormat { get => (m_CameraDepthTextureFormat != DepthFormat.Default) ? (GraphicsFormat)m_CameraDepthTextureFormat : k_DepthStencilFormatDefault; }
        internal GraphicsFormat cameraDepthAttachmentFormat { get => (m_CameraDepthAttachmentFormat != DepthFormat.Default) ? (GraphicsFormat)m_CameraDepthAttachmentFormat : k_DepthStencilFormatDefault; }

        /// <summary>
        /// Constructor for the Universal Renderer.
        /// </summary>
        /// <param name="data">The settings to create the renderer with.</param>
        public UniversalRenderer(UniversalRendererData data) : base(data)
        {
            // Query and cache runtime platform info first before setting up URP.
            PlatformAutoDetect.Initialize();

#if ENABLE_VR && ENABLE_XR_MODULE
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UniversalRenderPipelineRuntimeXRResources>(out var xrResources))
            {
                Experimental.Rendering.XRSystem.Initialize(XRPassUniversal.Create, xrResources.xrOcclusionMeshPS, xrResources.xrMirrorViewPS);
                m_XRDepthMotionPass = new XRDepthMotionPass(RenderPassEvent.BeforeRenderingPrePasses, xrResources.xrMotionVector);
            }
#endif
            Shader coreBlitPS = null;
            Shader blitHDROverlayPS = null;
            Shader samplingPS = null;
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UniversalRenderPipelineRuntimeShaders>(
                    out var shadersResources))
            {
                coreBlitPS = shadersResources.coreBlitPS;
                blitHDROverlayPS = shadersResources.blitHDROverlay;
                samplingPS = shadersResources.samplingPS;
            }

            // GraphicsSettings remain the Unity 6 primary path. Older renderer
            // assets can still provide the same resources through their 2022 field.
            if (data.shaders != null)
            {
                coreBlitPS = coreBlitPS != null ? coreBlitPS : data.shaders.coreBlitPS;
                blitHDROverlayPS = blitHDROverlayPS != null ? blitHDROverlayPS : data.shaders.blitHDROverlay;
                samplingPS = samplingPS != null ? samplingPS : data.shaders.samplingPS;
            }

            if (coreBlitPS != null)
                m_BlitMaterial = CoreUtils.CreateEngineMaterial(coreBlitPS);
            if (blitHDROverlayPS != null)
                m_BlitHDRMaterial = CoreUtils.CreateEngineMaterial(blitHDROverlayPS);
            if (samplingPS != null)
                m_SamplingMaterial = CoreUtils.CreateEngineMaterial(samplingPS);

            Shader copyDephPS = null;
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UniversalRendererResources>(
                    out var universalRendererShaders))
            {
                copyDephPS = universalRendererShaders.copyDepthPS;
                m_StencilDeferredMaterial = CoreUtils.CreateEngineMaterial(universalRendererShaders.stencilDeferredPS);
                m_CameraMotionVecMaterial = CoreUtils.CreateEngineMaterial(universalRendererShaders.cameraMotionVector);
            }

            if (data.shaders != null)
            {
                copyDephPS = copyDephPS != null ? copyDephPS : data.shaders.copyDepthPS;
                if (m_StencilDeferredMaterial == null && data.shaders.stencilDeferredPS != null)
                    m_StencilDeferredMaterial = CoreUtils.CreateEngineMaterial(data.shaders.stencilDeferredPS);
                if (m_CameraMotionVecMaterial == null && data.shaders.cameraMotionVector != null)
                    m_CameraMotionVecMaterial = CoreUtils.CreateEngineMaterial(data.shaders.cameraMotionVector);
            }

            StencilStateData stencilData = data.defaultStencilState;
            m_DefaultStencilState = StencilState.defaultValue;
            m_DefaultStencilState.enabled = stencilData.overrideStencilState;
            m_DefaultStencilState.SetCompareFunction(stencilData.stencilCompareFunction);
            m_DefaultStencilState.SetPassOperation(stencilData.passOperation);
            m_DefaultStencilState.SetFailOperation(stencilData.failOperation);
            m_DefaultStencilState.SetZFailOperation(stencilData.zFailOperation);

            m_IntermediateTextureMode = data.intermediateTextureMode;
            opaqueLayerMask = data.opaqueLayerMask;
            transparentLayerMask = data.transparentLayerMask;

            if (UniversalRenderPipeline.asset?.supportsLightCookies ?? false)
            {
                var settings = LightCookieManager.Settings.Create();
                var asset = UniversalRenderPipeline.asset;
                if (asset)
                {
                    settings.atlas.format = asset.additionalLightsCookieFormat;
                    settings.atlas.resolution = asset.additionalLightsCookieResolution;
                }

                m_LightCookieManager = new LightCookieManager(ref settings);
            }

            this.stripShadowsOffVariants = true;
            this.stripAdditionalLightOffVariants = true;
#if ENABLE_VR && ENABLE_VR_MODULE
#if PLATFORM_WINRT || PLATFORM_ANDROID
            // AdditionalLightOff variant is available on HL&Quest platform due to performance consideration.
            this.stripAdditionalLightOffVariants = !PlatformAutoDetect.isXRMobile;
#endif
#endif

            ForwardLights.InitParams forwardInitParams;
            forwardInitParams.lightCookieManager = m_LightCookieManager;
            forwardInitParams.forwardPlus = data.renderingMode == RenderingMode.ForwardPlus;
            m_Clustering = data.renderingMode == RenderingMode.ForwardPlus;
            m_ForwardLights = new ForwardLights(forwardInitParams);
            //m_DeferredLights.LightCulling = data.lightCulling;
            this.m_RenderingMode = data.renderingMode;
            this.m_DepthPrimingMode = data.depthPrimingMode;
            this.m_CopyDepthMode = data.copyDepthMode;
            this.m_CameraDepthAttachmentFormat = data.depthAttachmentFormat;
            this.m_CameraDepthTextureFormat = data.depthTextureFormat;
            useRenderPassEnabled = data.useNativeRenderPass;

#if UNITY_ANDROID || UNITY_IOS || UNITY_TVOS || UNITY_EMBEDDED_LINUX
            this.m_DepthPrimingRecommended = false;
#else
            this.m_DepthPrimingRecommended = true;
#endif

            // Note: Since all custom render passes inject first and we have stable sort,
            // we inject the builtin passes in the before events.
            m_MainLightShadowCasterPass = new MainLightShadowCasterPass(RenderPassEvent.BeforeRenderingShadows);
            m_AdditionalLightsShadowCasterPass = new AdditionalLightsShadowCasterPass(RenderPassEvent.BeforeRenderingShadows);

#if ENABLE_VR && ENABLE_XR_MODULE
            m_XROcclusionMeshPass = new XROcclusionMeshPass(RenderPassEvent.BeforeRenderingOpaques);
            // Schedule XR copydepth right after m_FinalBlitPass
            m_XRCopyDepthPass = new CopyDepthPass(RenderPassEvent.AfterRendering + k_AfterFinalBlitPassQueueOffset, copyDephPS);
#endif
            m_DepthPrepass = new DepthOnlyPass(RenderPassEvent.BeforeRenderingPrePasses, RenderQueueRange.opaque, data.opaqueLayerMask);
            m_DepthNormalPrepass = new DepthNormalOnlyPass(RenderPassEvent.BeforeRenderingPrePasses, RenderQueueRange.opaque, data.opaqueLayerMask);

            if (this.renderingModeRequested == RenderingMode.Deferred)
            {
                var deferredInitParams = new DeferredLights.InitParams();
                deferredInitParams.stencilDeferredMaterial = m_StencilDeferredMaterial;
                deferredInitParams.lightCookieManager = m_LightCookieManager;
                m_DeferredLights = new DeferredLights(deferredInitParams, useRenderPassEnabled);
                m_DeferredLights.AccurateGbufferNormals = data.accurateGbufferNormals;

                m_GBufferPass = new GBufferPass(RenderPassEvent.BeforeRenderingGbuffer, RenderQueueRange.opaque, data.opaqueLayerMask, m_DefaultStencilState, stencilData.stencilReference, m_DeferredLights);
                // Forward-only pass only runs if deferred renderer is enabled.
                // It allows specific materials to be rendered in a forward-like pass.
                // We render both gbuffer pass and forward-only pass before the deferred lighting pass so we can minimize copies of depth buffer and
                // benefits from some depth rejection.
                // - If a material can be rendered either forward or deferred, then it should declare a UniversalForward and a UniversalGBuffer pass.
                // - If a material cannot be lit in deferred (unlit, bakedLit, special material such as hair, skin shader), then it should declare UniversalForwardOnly pass
                // - Legacy materials have unamed pass, which is implicitely renamed as SRPDefaultUnlit. In that case, they are considered forward-only too.
                // TO declare a material with unnamed pass and UniversalForward/UniversalForwardOnly pass is an ERROR, as the material will be rendered twice.
                StencilState forwardOnlyStencilState = DeferredLights.OverwriteStencil(m_DefaultStencilState, (int)StencilUsage.MaterialMask);
                ShaderTagId[] forwardOnlyShaderTagIds = new ShaderTagId[]
                {
                    new ShaderTagId("UniversalForwardOnly"),
                    new ShaderTagId("SRPDefaultUnlit"), // Legacy shaders (do not have a gbuffer pass) are considered forward-only for backward compatibility
                    new ShaderTagId("LightweightForward") // Legacy shaders (do not have a gbuffer pass) are considered forward-only for backward compatibility
                };
                int forwardOnlyStencilRef = stencilData.stencilReference | (int)StencilUsage.MaterialUnlit;
                m_DeferredPass = new DeferredPass(RenderPassEvent.BeforeRenderingDeferredLights, m_DeferredLights);
                m_RenderOpaqueForwardOnlyPass = new DrawObjectsPass("Draw Opaques Forward Only", forwardOnlyShaderTagIds, true, RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque, data.opaqueLayerMask, forwardOnlyStencilState, forwardOnlyStencilRef);
            }

            // Always create this pass even in deferred because we use it for wireframe rendering in the Editor or offscreen depth texture rendering.
            m_RenderOpaqueForwardPass = new DrawObjectsPass(URPProfileId.DrawOpaqueObjects, true, RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque, data.opaqueLayerMask, m_DefaultStencilState, stencilData.stencilReference);
            m_RenderOpaqueForwardWithRenderingLayersPass = new DrawObjectsWithRenderingLayersPass(URPProfileId.DrawOpaqueObjects, true, RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque, data.opaqueLayerMask, m_DefaultStencilState, stencilData.stencilReference);

            bool copyDepthAfterTransparents = m_CopyDepthMode == CopyDepthMode.AfterTransparents;
            RenderPassEvent copyDepthEvent = copyDepthAfterTransparents ? RenderPassEvent.AfterRenderingTransparents : RenderPassEvent.AfterRenderingSkybox;

            m_CopyDepthPass = new CopyDepthPass(
                copyDepthEvent,
                copyDephPS,
                shouldClear: true,
                copyResolvedDepth: RenderingUtils.MultisampleDepthResolveSupported() && copyDepthAfterTransparents);

            // Motion vectors depend on the (copy) depth texture. Depth is reprojected to calculate motion vectors.
            m_MotionVectorPass = new MotionVectorRenderPass(copyDepthEvent + 1, m_CameraMotionVecMaterial, data.opaqueLayerMask);

            m_DrawSkyboxPass = new DrawSkyboxPass(RenderPassEvent.BeforeRenderingSkybox);
            m_CopyColorPass = new CopyColorPass(RenderPassEvent.AfterRenderingSkybox, m_SamplingMaterial, m_BlitMaterial);
#if ADAPTIVE_PERFORMANCE_2_1_0_OR_NEWER
            if (needTransparencyPass)
#endif
            {
                m_ShouldTransparentsReceiveShadows = data.shadowTransparentReceive;
                m_RenderTransparentForwardPass = new DrawObjectsPass(URPProfileId.DrawTransparentObjects, false, RenderPassEvent.BeforeRenderingTransparents, RenderQueueRange.transparent, data.transparentLayerMask, m_DefaultStencilState, stencilData.stencilReference);
            }
            m_OnRenderObjectCallbackPass = new InvokeOnRenderObjectCallbackPass(RenderPassEvent.BeforeRenderingPostProcessing);

            // History generation passes for "raw color/depth". These execute only if explicitly requested by users.
            // VFX system particles uses these. See RawColorHistory.cs.
            m_HistoryRawColorCopyPass = new CopyColorPass(RenderPassEvent.BeforeRenderingPostProcessing, m_SamplingMaterial, m_BlitMaterial, customPassName: "Copy Color Raw History");
            m_HistoryRawDepthCopyPass = new CopyDepthPass(RenderPassEvent.BeforeRenderingPostProcessing, copyDephPS, false, RenderingUtils.MultisampleDepthResolveSupported(), customPassName: "Copy Depth Raw History");

            m_DrawOffscreenUIPass = new DrawScreenSpaceUIPass(RenderPassEvent.BeforeRenderingPostProcessing);
            m_DrawOverlayUIPass = new DrawScreenSpaceUIPass(RenderPassEvent.AfterRendering + k_AfterFinalBlitPassQueueOffset); // after m_FinalBlitPass

            {
                // URP post-processing format follows the back-buffer format.
                var postProcessParams = PostProcessParams.Create();
                postProcessParams.blitMaterial = m_BlitMaterial;
                postProcessParams.requestColorFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                var asset = UniversalRenderPipeline.asset;
                if (asset)
                    postProcessParams.requestColorFormat = UniversalRenderPipeline.MakeRenderTextureGraphicsFormat(asset.supportsHDR, asset.hdrColorBufferPrecision, false);

                m_PostProcessPasses = new PostProcessPasses(data.postProcessData, ref postProcessParams);
            }

            m_CapturePass = new CapturePass(RenderPassEvent.AfterRendering);
            m_FinalBlitPass = new FinalBlitPass(RenderPassEvent.AfterRendering + k_FinalBlitPassQueueOffset, m_BlitMaterial, m_BlitHDRMaterial);

#if UNITY_EDITOR
            m_FinalDepthCopyPass = new CopyDepthPass(RenderPassEvent.AfterRendering + 9, copyDephPS, false, true, customPassName: "Copy Final Depth");
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UniversalRenderPipelineDebugShaders>(out var debugShaders))
                m_ProbeVolumeDebugPass = new ProbeVolumeDebugPass(RenderPassEvent.BeforeRenderingTransparents, debugShaders.probeVolumeSamplingDebugComputeShader);
#endif

            supportedRenderingFeatures = new RenderingFeatures();

            LensFlareCommonSRP.mergeNeeded = 0;
            LensFlareCommonSRP.maxLensFlareWithOcclusionTemporalSample = 1;
            LensFlareCommonSRP.Initialize();

            m_VulkanEnablePreTransform = GraphicsSettings.HasShaderDefine(BuiltinShaderDefine.UNITY_PRETRANSFORM_TO_DISPLAY_ORIENTATION);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            m_ForwardLights.Cleanup();
            m_GBufferPass?.Dispose();
            m_PostProcessPasses.Dispose();

            m_FinalBlitPass?.Dispose();

            m_CopyDepthPass?.Dispose();
#if UNITY_EDITOR
            m_FinalDepthCopyPass?.Dispose();
#endif
            m_HistoryRawDepthCopyPass?.Dispose();

#if ENABLE_VR && ENABLE_XR_MODULE
            m_XRCopyDepthPass?.Dispose();
            m_XRDepthMotionPass?.Dispose();
#endif

            m_TargetColorHandle?.Release();
            m_TargetDepthHandle?.Release();
            ReleaseRenderTargets();

            base.Dispose(disposing);
            CoreUtils.Destroy(m_BlitMaterial);
            CoreUtils.Destroy(m_BlitHDRMaterial);
            CoreUtils.Destroy(m_SamplingMaterial);
            CoreUtils.Destroy(m_StencilDeferredMaterial);
            CoreUtils.Destroy(m_CameraMotionVecMaterial);

            CleanupRenderGraphResources();

            LensFlareCommonSRP.Dispose();

#if ENABLE_VR && ENABLE_XR_MODULE
            Experimental.Rendering.XRSystem.Dispose();
#endif
        }

        internal override void ReleaseRenderTargets()
        {
            if (m_DeferredLights != null && !m_DeferredLights.UseFramebufferFetch)
                m_GBufferPass?.Dispose();

            m_PostProcessPasses.ReleaseRenderTargets();
            m_MainLightShadowCasterPass?.Dispose();
            m_AdditionalLightsShadowCasterPass?.Dispose();
        }

        /// <inheritdoc/>
        public override void SetupCullingParameters(ref ScriptableCullingParameters cullingParameters,
            ref CameraData cameraData)
        {
            // Forward+ handles blended reflection probes through its clustered list.
            if (renderingModeActual == RenderingMode.ForwardPlus &&
                UniversalRenderPipeline.asset.reflectionProbeBlending)
            {
                cullingParameters.cullingOptions |= CullingOptions.DisablePerObjectCulling;
            }

            // Skip shadow caster culling when shadows are disabled for this camera.
            bool isShadowCastingDisabled = !UniversalRenderPipeline.asset.supportsMainLightShadows &&
                !UniversalRenderPipeline.asset.supportsAdditionalLightShadows;
            bool isShadowDistanceZero = Mathf.Approximately(cameraData.maxShadowDistance, 0.0f);
            if (isShadowCastingDisabled || isShadowDistanceZero)
                cullingParameters.cullingOptions &= ~CullingOptions.ShadowCasters;

            if (renderingModeActual == RenderingMode.Deferred)
                cullingParameters.maximumVisibleLights = 0xFFFF;
            else if (renderingModeActual == RenderingMode.ForwardPlus)
            {
                cullingParameters.maximumVisibleLights = UniversalRenderPipeline.maxVisibleAdditionalLights;
                cullingParameters.reflectionProbeSortingCriteria = ReflectionProbeSortingCriteria.None;
            }
            else
                cullingParameters.maximumVisibleLights = UniversalRenderPipeline.maxVisibleAdditionalLights + 1;

            // RenderGraph also needs this before context.Cull: native cascade matrices
            // and split spheres must use URP's shadow distance, not the camera default.
            cullingParameters.shadowDistance = cameraData.maxShadowDistance;
            cullingParameters.conservativeEnclosingSphere = UniversalRenderPipeline.asset.conservativeEnclosingSphere;
            cullingParameters.numIterationsEnclosingSphere = UniversalRenderPipeline.asset.numIterationsEnclosingSphere;
        }


        /// <summary>
        /// Returns if the camera renders to a offscreen depth texture.
        /// </summary>
        /// <param name="cameraData">The camera data for the camera being rendered.</param>
        /// <returns>Returns true if the camera renders to depth without any color buffer. It will return false otherwise.</returns>
        public static bool IsOffscreenDepthTexture(ref CameraData cameraData) => IsOffscreenDepthTexture(cameraData.universalCameraData);

        /// <summary>
        /// Returns if the camera renders to a offscreen depth texture.
        /// </summary>
        /// <param name="cameraData">The camera data for the camera being rendered.</param>
        /// <returns>Returns true if the camera renders to depth without any color buffer. It will return false otherwise.</returns>
        public static bool IsOffscreenDepthTexture(UniversalCameraData cameraData) => cameraData.targetTexture != null && cameraData.targetTexture.format == RenderTextureFormat.Depth;

        bool IsDepthPrimingEnabled(UniversalCameraData cameraData)
        {
#if UNITY_EDITOR
            // We need to disable depth-priming for DrawCameraMode.Wireframe, since depth-priming forces ZTest to Equal
            // for opaques rendering, which breaks wireframe rendering.
            if (cameraData.isSceneViewCamera)
            {
                foreach (var sceneViewObject in UnityEditor.SceneView.sceneViews)
                {
                    var sceneView = sceneViewObject as UnityEditor.SceneView;
                    if (sceneView != null && sceneView.camera == cameraData.camera && sceneView.cameraMode.drawMode == UnityEditor.DrawCameraMode.Wireframe)
                        return false;
                }
            }
#endif

            // depth priming requires an extra depth copy, disable it on platforms not supporting it (like GLES when MSAA is on)
            if (!CanCopyDepth(cameraData))
                return false;

            // Depth Priming causes rendering errors with WebGL and WebGPU on Apple Arm64 GPUs.
            bool isNotWebGL = !IsWebGL();
            bool depthPrimingRequested = (m_DepthPrimingRecommended && m_DepthPrimingMode == DepthPrimingMode.Auto) || m_DepthPrimingMode == DepthPrimingMode.Forced;
            bool isForwardRenderingMode = m_RenderingMode == RenderingMode.Forward || m_RenderingMode == RenderingMode.ForwardPlus;
            bool isFirstCameraToWriteDepth = cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth;
            // Enabled Depth priming when baking Reflection Probes causes artefacts (UUM-12397)
            bool isNotReflectionCamera = cameraData.cameraType != CameraType.Reflection;
            // Depth is not rendered in a depth-only camera setup with depth priming (UUM-38158)
            bool isNotOffscreenDepthTexture = !IsOffscreenDepthTexture(cameraData);
            bool isNotMSAA = cameraData.cameraTargetDescriptor.msaaSamples == 1;

            return depthPrimingRequested && isForwardRenderingMode && isFirstCameraToWriteDepth && isNotReflectionCamera && isNotOffscreenDepthTexture && isNotWebGL && isNotMSAA;
        }

        bool IsWebGL()
        {
            // Both WebGL and WebGPU have issues with depth priming on Apple Arm64
#if PLATFORM_WEBGL
            return true;
#else
            return false;
#endif
        }

        bool IsGLESDevice()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;
        }

        bool IsGLDevice()
        {
            return IsGLESDevice() || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore;
        }

        /// <summary>
        /// Queries one camera's rendering-layer contract without leaking the temporary overlay
        /// mode into the renderer. The pipeline merges these results before rendering a stack.
        /// </summary>
        internal bool GetRenderingLayerRequirements(CameraRenderType cameraType,
            int msaaSampleCount, out RenderingLayerUtils.Event renderingLayersEvent,
            out RenderingLayerUtils.MaskSize renderingLayerMaskSize)
        {
            bool previousOverlay = m_DeferredLights != null && m_DeferredLights.IsOverlay;
            if (m_DeferredLights != null)
                m_DeferredLights.IsOverlay = cameraType == CameraRenderType.Overlay;

            try
            {
                bool requires = RenderingLayerUtils.RequireRenderingLayers(this,
                    rendererFeatures, msaaSampleCount, out renderingLayersEvent,
                    out renderingLayerMaskSize);
                return !IsGLDevice() && requires;
            }
            finally
            {
                if (m_DeferredLights != null)
                    m_DeferredLights.IsOverlay = previousOverlay;
            }
        }

        internal bool HasActiveRenderFeatures()
        {
            if (rendererFeatures.Count == 0)
                return false;

            foreach (var rf in rendererFeatures)
            {
                if (rf.isActive)
                    return true;
            }

            return false;
        }

        internal bool HasPassesRequiringIntermediateTexture()
        {
            if (activeRenderPassQueue.Count == 0)
                return false;

            foreach (var pass in activeRenderPassQueue)
            {
                if (pass.requiresIntermediateTexture)
                    return true;
            }

            return false;
        }

        private struct RenderPassInputSummary
        {
            internal bool requiresDepthTexture;
            internal bool requiresDepthPrepass;
            internal bool requiresNormalsTexture;
            internal bool requiresColorTexture;
            internal bool requiresColorTextureCreated;
            internal bool requiresMotionVectors;
            internal RenderPassEvent requiresDepthNormalAtEvent;
            internal RenderPassEvent requiresDepthTextureEarliestEvent;
        }

        private RenderPassInputSummary GetRenderPassInputs(bool isTemporalAAEnabled, bool postProcessingEnabled)
        {
            RenderPassInputSummary inputSummary = new RenderPassInputSummary();
            inputSummary.requiresDepthNormalAtEvent = RenderPassEvent.BeforeRenderingOpaques;
            inputSummary.requiresDepthTextureEarliestEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            for (int i = 0; i < activeRenderPassQueue.Count; ++i)
            {
                ScriptableRenderPass pass = activeRenderPassQueue[i];
                bool needsDepth = (pass.input & ScriptableRenderPassInput.Depth) != ScriptableRenderPassInput.None;
                bool needsNormals = (pass.input & ScriptableRenderPassInput.Normal) != ScriptableRenderPassInput.None;
                bool needsColor = (pass.input & ScriptableRenderPassInput.Color) != ScriptableRenderPassInput.None;
                bool needsMotion = (pass.input & ScriptableRenderPassInput.Motion) != ScriptableRenderPassInput.None;
                bool eventBeforeRenderingOpaques = pass.renderPassEvent < RenderPassEvent.AfterRenderingOpaques;

                // TODO: Need a better way to handle this, probably worth to recheck after render graph
                // DBuffer requires color texture created as it does not handle y flip correctly
                if (pass is DBufferRenderPass dBufferRenderPass)
                {
                    inputSummary.requiresColorTextureCreated = true;
                }

                inputSummary.requiresDepthTexture |= needsDepth;

                // A depth prepass is always required when normals are needed because URP's forward passes don't support rendering into the normals texture
                // If depth is needed without normals, we only need a prepass when the event consuming depth occurs before opaque rendering is completed.
                inputSummary.requiresDepthPrepass |= needsNormals || (needsDepth && eventBeforeRenderingOpaques);

                inputSummary.requiresNormalsTexture |= needsNormals;
                inputSummary.requiresColorTexture |= needsColor;
                inputSummary.requiresMotionVectors |= needsMotion;
                if (needsDepth)
                    inputSummary.requiresDepthTextureEarliestEvent = (RenderPassEvent)Mathf.Min((int)pass.renderPassEvent, (int)inputSummary.requiresDepthTextureEarliestEvent);
                if (needsNormals || needsDepth)
                    inputSummary.requiresDepthNormalAtEvent = (RenderPassEvent)Mathf.Min((int)pass.renderPassEvent, (int)inputSummary.requiresDepthNormalAtEvent);
            }

            // TAA in postprocess requires it to function.
            if (isTemporalAAEnabled)
                inputSummary.requiresMotionVectors = true;

            // Object motion blur requires motion vectors.
            if (postProcessingEnabled)
            {
                var motionBlur = VolumeManager.instance.stack.GetComponent<MotionBlur>();
                if(motionBlur != null && motionBlur.IsActive() && motionBlur.mode.value == MotionBlurMode.CameraAndObjects)
                    inputSummary.requiresMotionVectors = true;
            }

            // Motion vectors imply depth
            if (inputSummary.requiresMotionVectors)
            {
                inputSummary.requiresDepthTexture = true;
                inputSummary.requiresDepthTextureEarliestEvent = (RenderPassEvent)Mathf.Min((int)m_MotionVectorPass.renderPassEvent, (int)inputSummary.requiresDepthTextureEarliestEvent);
            }


            return inputSummary;
        }
        bool PlatformRequiresExplicitMsaaResolve()
        {
#if UNITY_EDITOR
            // In the editor play-mode we use a Game View Render Texture, with
            // samples count forced to 1 so we always need to do an explicit MSAA resolve.
            return true;
#else
            // On Metal/iOS the MSAA resolve is done implicitly as part of the renderpass, so we do not need an extra intermediate pass for the explicit autoresolve.
            // Note: On Vulkan Standalone, despite SystemInfo.supportsMultisampleAutoResolve being true, the backbuffer has only 1 sample, so we still require
            // the explicit resolve on non-mobile platforms with supportsMultisampleAutoResolve.
            return !(SystemInfo.supportsMultisampleAutoResolve && Application.isMobilePlatform)
                && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal;
#endif
        }

        /// <summary>
        /// Checks if the pipeline needs to create a intermediate render texture.
        /// </summary>
        /// <param name="cameraData">CameraData contains all relevant render target information for the camera.</param>
        /// <seealso cref="CameraData"/>
        /// <returns>Return true if pipeline needs to render to a intermediate render texture.</returns>
        bool RequiresIntermediateColorTexture(UniversalCameraData cameraData, ref RenderPassInputSummary renderPassInputs)
        {
            // When rendering a camera stack we always create an intermediate render texture to composite camera results.
            // We create it upon rendering the Base camera.
            if (cameraData.renderType == CameraRenderType.Base && !cameraData.resolveFinalTarget)
                return true;

            // Always force rendering into intermediate color texture if deferred rendering mode is selected.
            // Reason: without intermediate color texture, the target camera texture is y-flipped.
            // However, the target camera texture is bound during gbuffer pass and deferred pass.
            // Gbuffer pass will not be y-flipped because it is MRT (see ScriptableRenderContext implementation),
            // while deferred pass will be y-flipped, which breaks rendering.
            // This incurs an extra blit into at the end of rendering.
            if (this.renderingModeActual == RenderingMode.Deferred)
                return true;

            bool isSceneViewCamera = cameraData.isSceneViewCamera;
            var cameraTargetDescriptor = cameraData.cameraTargetDescriptor;
            int msaaSamples = cameraTargetDescriptor.msaaSamples;
            bool isScaledRender = cameraData.imageScalingMode != ImageScalingMode.None;
            bool isCompatibleBackbufferTextureDimension = cameraTargetDescriptor.dimension == TextureDimension.Tex2D;
            bool requiresExplicitMsaaResolve = msaaSamples > 1 && PlatformRequiresExplicitMsaaResolve();
            bool isOffscreenRender = cameraData.targetTexture != null && !isSceneViewCamera;
            bool isCapturing = cameraData.captureActions != null;

#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
            {
                isScaledRender = false;
                isCompatibleBackbufferTextureDimension = cameraData.xr.renderTargetDesc.dimension == cameraTargetDescriptor.dimension;
            }
#endif
            bool postProcessEnabled = cameraData.postProcessEnabled && m_PostProcessPasses.isCreated;
            bool requiresBlitForOffscreenCamera = postProcessEnabled || cameraData.requiresOpaqueTexture || requiresExplicitMsaaResolve || !cameraData.isDefaultViewport;
            if (isOffscreenRender)
                return requiresBlitForOffscreenCamera;

            return requiresBlitForOffscreenCamera || isScaledRender || cameraData.isHdrEnabled ||
                !isCompatibleBackbufferTextureDimension || isCapturing || cameraData.requireSrgbConversion ||
                renderPassInputs.requiresColorTexture || renderPassInputs.requiresColorTextureCreated;
        }

        bool CanCopyDepth(UniversalCameraData cameraData)
        {
            bool msaaEnabledForCamera = cameraData.cameraTargetDescriptor.msaaSamples > 1;
            bool supportsTextureCopy = SystemInfo.copyTextureSupport != CopyTextureSupport.None;
            bool supportsDepthTarget = RenderingUtils.SupportsRenderTextureFormat(RenderTextureFormat.Depth);
            bool supportsDepthCopy = !msaaEnabledForCamera && (supportsDepthTarget || supportsTextureCopy);

            bool msaaDepthResolve = msaaEnabledForCamera && SystemInfo.supportsMultisampledTextures != 0;

            // copying MSAA depth on GLES3 is giving invalid results. This won't be fixed by the driver team because it would introduce performance issues (more info in the Fogbugz issue 1339401 comments)
            if (IsGLESDevice() && msaaDepthResolve)
                return false;

            return supportsDepthCopy || msaaDepthResolve;
        }

        internal override bool supportsNativeRenderPassRendergraphCompiler => true;
    }
}
