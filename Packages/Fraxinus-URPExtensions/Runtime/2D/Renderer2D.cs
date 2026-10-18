using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    internal sealed partial class Renderer2D : ScriptableRenderer
    {
        #if UNITY_SWITCH || UNITY_EMBEDDED_LINUX || UNITY_QNX || UNITY_ANDROID
        const GraphicsFormat k_DepthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt;
        #else
        const GraphicsFormat k_DepthStencilFormat = GraphicsFormat.D32_SFloat_S8_UInt;
        #endif

        const int k_FinalBlitPassQueueOffset = 1;
        const int k_AfterFinalBlitPassQueueOffset = k_FinalBlitPassQueueOffset + 1;

        UpscalePass m_UpscalePass;
        CopyDepthPass m_CopyDepthPass;
        CopyCameraSortingLayerPass m_CopyCameraSortingLayerPass;
        FinalBlitPass m_FinalBlitPass;
        DrawScreenSpaceUIPass m_DrawOffscreenUIPass;
        DrawScreenSpaceUIPass m_DrawOverlayUIPass; // from HDRP code

        bool m_UseDepthStencilBuffer = true;
        bool m_CreateColorTexture;
        bool m_CreateDepthTexture;

        Material m_BlitMaterial;
        Material m_BlitHDRMaterial;

        Renderer2DData m_Renderer2DData;

        internal bool createColorTexture => m_CreateColorTexture;
        internal bool createDepthTexture => m_CreateDepthTexture;

        PostProcessPasses m_PostProcessPasses;
        internal ColorGradingLutPass colorGradingLutPass { get => m_PostProcessPasses.colorGradingLutPass; }
        internal PostProcessPass postProcessPass { get => m_PostProcessPasses.postProcessPass; }
        internal PostProcessPass finalPostProcessPass { get => m_PostProcessPasses.finalPostProcessPass; }
        internal RTHandle afterPostProcessColorHandle { get => m_PostProcessPasses.afterPostProcessColor; }
        internal RTHandle colorGradingLutHandle { get => m_PostProcessPasses.colorGradingLut; }

        /// <inheritdoc/>
        public override int SupportedCameraStackingTypes()
        {
            return 1 << (int)CameraRenderType.Base | 1 << (int)CameraRenderType.Overlay;
        }

        public Renderer2D(Renderer2DData data) : base(data)
        {
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UniversalRenderPipelineRuntimeShaders>(
                out var shadersResources))
            {
                m_BlitMaterial = CoreUtils.CreateEngineMaterial(shadersResources.coreBlitPS);
                m_BlitHDRMaterial = CoreUtils.CreateEngineMaterial(shadersResources.blitHDROverlay);
            }

            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<Renderer2DResources>(out var renderer2DResources))
            {
                m_CopyDepthPass = new CopyDepthPass(
                    RenderPassEvent.AfterRenderingTransparents,
                    renderer2DResources.copyDepthPS,
                    shouldClear: true,
                    copyResolvedDepth: RenderingUtils.MultisampleDepthResolveSupported());
            }

            m_UpscalePass = new UpscalePass(m_BlitMaterial);
            m_CopyCameraSortingLayerPass = new CopyCameraSortingLayerPass(m_BlitMaterial);
            m_FinalBlitPass = new FinalBlitPass(RenderPassEvent.AfterRendering + k_FinalBlitPassQueueOffset, m_BlitMaterial, m_BlitHDRMaterial);

            m_DrawOffscreenUIPass = new DrawScreenSpaceUIPass(RenderPassEvent.BeforeRenderingPostProcessing);
            m_DrawOverlayUIPass = new DrawScreenSpaceUIPass(RenderPassEvent.AfterRendering + k_AfterFinalBlitPassQueueOffset); // after m_FinalBlitPass

            var ppParams = PostProcessParams.Create();
            ppParams.blitMaterial = m_BlitMaterial;
            ppParams.requestColorFormat = GraphicsFormat.B10G11R11_UFloatPack32;
            m_PostProcessPasses = new PostProcessPasses(data.postProcessData, ref ppParams);

            m_UseDepthStencilBuffer = data.useDepthStencilBuffer;

            m_Renderer2DData = data;

            supportedRenderingFeatures = new RenderingFeatures();

            m_Renderer2DData.lightCullResult = new Light2DCullResult();

            LensFlareCommonSRP.mergeNeeded = 0;
            LensFlareCommonSRP.maxLensFlareWithOcclusionTemporalSample = 1;
            LensFlareCommonSRP.Initialize();

            Light2DManager.Initialize();
        }

        protected override void Dispose(bool disposing)
        {
            m_Renderer2DData.Dispose();
            m_PostProcessPasses.Dispose();
            ReleaseRenderTargets();
            m_CopyDepthPass?.Dispose();
            m_FinalBlitPass?.Dispose();
            Light2DManager.Dispose();

            CoreUtils.Destroy(m_BlitMaterial);
            CoreUtils.Destroy(m_BlitHDRMaterial);

            CleanupRenderGraphResources();

            base.Dispose(disposing);
        }

        internal override void ReleaseRenderTargets()
        {
            m_PostProcessPasses.ReleaseRenderTargets();
        }

        public Renderer2DData GetRenderer2DData()
        {
            return m_Renderer2DData;
        }

        internal static short GetCameraSortingLayerBoundsIndex(Renderer2DData rendererData)
        {
            SortingLayer[] sortingLayers = Light2DManager.GetCachedSortingLayer();
            for (short i = 0; i < sortingLayers.Length; i++)
            {
                if (sortingLayers[i].id == rendererData.cameraSortingLayerTextureBound)
                    return (short)sortingLayers[i].value;
            }

            return short.MinValue;
        }

        private struct RenderPassInputSummary
        {
            internal bool requiresDepthTexture;
            internal bool requiresColorTexture;
        }

        private RenderPassInputSummary GetRenderPassInputs(UniversalCameraData cameraData)
        {
            RenderPassInputSummary inputSummary = new RenderPassInputSummary();

            for (int i = 0; i < activeRenderPassQueue.Count; ++i)
            {
                ScriptableRenderPass pass = activeRenderPassQueue[i];
                bool needsDepth = (pass.input & ScriptableRenderPassInput.Depth) != ScriptableRenderPassInput.None;
                bool needsColor = (pass.input & ScriptableRenderPassInput.Color) != ScriptableRenderPassInput.None;

                inputSummary.requiresDepthTexture |= needsDepth;
                inputSummary.requiresColorTexture |= needsColor;
            }

            inputSummary.requiresColorTexture |= cameraData.postProcessEnabled
                    || cameraData.isHdrEnabled
                    || cameraData.isSceneViewCamera
                    || !cameraData.isDefaultViewport
                    || cameraData.requireSrgbConversion
                    || !cameraData.resolveFinalTarget
                    || m_Renderer2DData.useCameraSortingLayerTexture
                    || !Mathf.Approximately(cameraData.renderScale, 1.0f)
                    || (DebugHandler != null && DebugHandler.WriteToDebugScreenTexture(cameraData.resolveFinalTarget));

            inputSummary.requiresDepthTexture |= (!cameraData.resolveFinalTarget && m_UseDepthStencilBuffer);

            return inputSummary;
        }

        public override void SetupCullingParameters(ref ScriptableCullingParameters cullingParameters, ref CameraData cameraData)
        {
            cullingParameters.cullingOptions = CullingOptions.None;
            cullingParameters.isOrthographic = cameraData.camera.orthographic;
            cullingParameters.shadowDistance = 0.0f;
            var cullResult = m_Renderer2DData.lightCullResult as Light2DCullResult;
            cullResult.SetupCulling(ref cullingParameters, cameraData.camera);
        }

        internal static bool IsGLDevice()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore;
        }

        internal static bool supportsMRT
        {
            get => !IsGLDevice();
        }

        internal override bool supportsNativeRenderPassRendergraphCompiler => true;
    }
}
