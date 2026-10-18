using System;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Copy the given color target to the current camera target
    ///
    /// You can use this pass to copy the result of rendering to
    /// the camera target. The pass takes the screen viewport into
    /// consideration.
    /// </summary>
    public class FinalBlitPass : ScriptableRenderPass
    {
        static readonly int s_CameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");

        // Use specialed URP fragment shader pass for debug draw support and color space conversion/encoding support.
        // See CoreBlit.shader and BlitHDROverlay.shader
        static class BlitPassNames
        {
            public const string NearestSampler = "NearestDebugDraw";
            public const string BilinearSampler = "BilinearDebugDraw";
        }

        enum BlitType
        {
            Core = 0, // Core blit
            HDR = 1, // Blit with HDR encoding and overlay UI compositing
            Count = 2
        }

        struct BlitMaterialData
        {
            public Material material;
            public int nearestSamplerPass;
            public int bilinearSamplerPass;
        }

        BlitMaterialData[] m_BlitMaterialData;

        /// <summary>
        /// Creates a new <c>FinalBlitPass</c> instance.
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <param name="blitMaterial">The <c>Material</c> to use for copying the executing the final blit.</param>
        /// <param name="blitHDRMaterial">The <c>Material</c> to use for copying the executing the final blit when HDR output is active.</param>
        /// <seealso cref="RenderPassEvent"/>
        public FinalBlitPass(RenderPassEvent evt, Material blitMaterial, Material blitHDRMaterial)
        {
            profilingSampler = ProfilingSampler.Get(URPProfileId.BlitFinalToBackBuffer);
            renderPassEvent = evt;

            // Find sampler passes by name
            const int blitTypeCount = (int)BlitType.Count;
            m_BlitMaterialData = new BlitMaterialData[blitTypeCount];
            for (int i = 0; i < blitTypeCount; ++i)
            {
                m_BlitMaterialData[i].material = i == (int)BlitType.Core ? blitMaterial : blitHDRMaterial;
                m_BlitMaterialData[i].nearestSamplerPass = m_BlitMaterialData[i].material?.FindPass(BlitPassNames.NearestSampler) ?? -1;
                m_BlitMaterialData[i].bilinearSamplerPass = m_BlitMaterialData[i].material?.FindPass(BlitPassNames.BilinearSampler) ?? -1;
            }
        }

        /// <summary>
        /// Cleans up resources used by the pass.
        /// </summary>
        public void Dispose()
        {

        }

        static void SetupHDROutput(ColorGamut hdrDisplayColorGamut, Material material, HDROutputUtils.Operation hdrOperation, Vector4 hdrOutputParameters, bool rendersOverlayUI)
        {
            material.SetVector(ShaderPropertyId.hdrOutputLuminanceParams, hdrOutputParameters);
            HDROutputUtils.ConfigureHDROutput(material, hdrDisplayColorGamut, hdrOperation);
            CoreUtils.SetKeyword(material, ShaderKeywordStrings.HDROverlay, rendersOverlayUI);
        }

        private static void ExecuteRenderGraphPass(RasterCommandBuffer cmd, PassData data,
            RTHandle source, RTHandle destination, UniversalCameraData cameraData)
        {
            bool isRenderToBackBufferTarget = !cameraData.isSceneViewCamera;
#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
                isRenderToBackBufferTarget = new RenderTargetIdentifier(destination.nameID, 0, CubemapFace.Unknown, -1) ==
                    new RenderTargetIdentifier(cameraData.xr.renderTarget, 0, CubemapFace.Unknown, -1);
#endif
            Vector4 scaleBias = RenderingUtils.GetFinalBlitScaleBias(source, destination, cameraData);
            if (isRenderToBackBufferTarget)
                cmd.SetViewport(cameraData.pixelRect);

            // Final blits must not inherit the editor wireframe state.
            cmd.SetWireframe(false);

            CoreUtils.SetKeyword(data.blitMaterialData.material,
                ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, data.enableAlphaOutput);

            int shaderPassIndex = source.rt?.filterMode == FilterMode.Bilinear
                ? data.blitMaterialData.bilinearSamplerPass
                : data.blitMaterialData.nearestSamplerPass;
            Blitter.BlitTexture(cmd, source, scaleBias, data.blitMaterialData.material, shaderPassIndex);
        }

        private class PassData
        {
            internal TextureHandle source;
            internal TextureHandle destination;
            internal int sourceID;
            internal Vector4 hdrOutputLuminanceParams;
            internal bool requireSrgbConversion;
            internal bool enableAlphaOutput;
            internal BlitMaterialData blitMaterialData;
            internal UniversalCameraData cameraData;
        }

        /// <summary>
        /// Initialize the shared pass data.
        /// </summary>
        /// <param name="passData"></param>
        private void InitPassData(UniversalCameraData cameraData, ref PassData passData, BlitType blitType, bool enableAlphaOutput)
        {
            passData.cameraData = cameraData;
            passData.requireSrgbConversion = cameraData.requireSrgbConversion;
            passData.enableAlphaOutput = enableAlphaOutput;

            passData.blitMaterialData = m_BlitMaterialData[(int)blitType];
        }

        internal void Render(RenderGraph renderGraph, ContextContainer frameData, UniversalCameraData cameraData, in TextureHandle src, in TextureHandle dest, TextureHandle overlayUITexture)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                // Only the UniversalRenderer guarantees that global textures will be available at this point
                bool isUniversalRenderer = (cameraData.renderer as UniversalRenderer) != null;

                if (cameraData.requiresDepthTexture && isUniversalRenderer)
                    builder.UseGlobalTexture(s_CameraDepthTextureID);

                bool outputsToHDR = cameraData.isHDROutputActive;
                bool outputsAlpha = cameraData.isAlphaOutputEnabled;
                InitPassData(cameraData, ref passData, outputsToHDR ? BlitType.HDR : BlitType.Core, outputsAlpha);

                passData.sourceID = ShaderPropertyId.sourceTex;
                passData.source = src;
                builder.UseTexture(src, AccessFlags.Read);
                passData.destination = dest;
                builder.SetRenderAttachment(dest, 0, AccessFlags.Write);

#if ENABLE_VR && ENABLE_XR_MODULE
                // This is a screen-space pass, make sure foveated rendering is disabled for non-uniform renders
                bool passSupportsFoveation = (XRSystem.foveatedRenderingCaps & FoveatedRenderingCaps.NonUniformRaster) == 0;
                builder.EnableFoveatedRasterization(cameraData.xr.supportsFoveatedRendering && passSupportsFoveation);
#endif

                if (outputsToHDR && overlayUITexture.IsValid())
                {
                    VolumeStack stack = VolumeManager.instance.stack;
                    Tonemapping tonemapping = stack.GetComponent<Tonemapping>();
                    UniversalRenderPipeline.GetHDROutputLuminanceParameters(passData.cameraData.hdrDisplayInformation, passData.cameraData.hdrDisplayColorGamut, tonemapping, out passData.hdrOutputLuminanceParams);

                    builder.UseTexture(overlayUITexture, AccessFlags.Read);
                }
                else
                {
                    passData.hdrOutputLuminanceParams = new Vector4(-1.0f, -1.0f, -1.0f, -1.0f);
                }

                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    data.blitMaterialData.material.enabledKeywords = null;
                    CoreUtils.SetKeyword(data.blitMaterialData.material,
                        ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT,
                        data.enableAlphaOutput);

                    context.cmd.SetKeyword(ShaderGlobalKeywords.LinearToSRGBConversion, data.requireSrgbConversion);
                    data.blitMaterialData.material.SetTexture(data.sourceID, data.source);

                    DebugHandler debugHandler = GetActiveDebugHandler(data.cameraData);
                    bool resolveToDebugScreen = debugHandler != null && debugHandler.WriteToDebugScreenTexture(data.cameraData.resolveFinalTarget);

                    // TODO RENDERGRAPH: this should ideally be shared in ExecutePass to avoid code duplication
                    if (data.hdrOutputLuminanceParams.w >= 0)
                    {
                        HDROutputUtils.Operation hdrOperation = HDROutputUtils.Operation.None;
                        // If the HDRDebugView is on, we don't want the encoding
                        if (debugHandler == null || !debugHandler.HDRDebugViewIsActive(data.cameraData.resolveFinalTarget))
                            hdrOperation |= HDROutputUtils.Operation.ColorEncoding;

                        // Color conversion may have happened in the Uber post process through color grading, so we don't want to reapply it
                        if (!data.cameraData.postProcessEnabled)
                            hdrOperation |= HDROutputUtils.Operation.ColorConversion;

                        SetupHDROutput(data.cameraData.hdrDisplayColorGamut, data.blitMaterialData.material, hdrOperation, data.hdrOutputLuminanceParams, data.cameraData.rendersOverlayUI);
                    }

                    if (resolveToDebugScreen)
                    {
                        RTHandle sourceTex = data.source;
                        Vector2 viewportScale = sourceTex.useScaling ? new Vector2(sourceTex.rtHandleProperties.rtHandleScale.x, sourceTex.rtHandleProperties.rtHandleScale.y) : Vector2.one;

                        int shaderPassIndex = sourceTex.rt?.filterMode == FilterMode.Bilinear ? data.blitMaterialData.bilinearSamplerPass : data.blitMaterialData.nearestSamplerPass;
                        Blitter.BlitTexture(context.cmd, sourceTex, viewportScale, data.blitMaterialData.material, shaderPassIndex);
                    }
                    else
                        ExecuteRenderGraphPass(context.cmd, data, data.source, data.destination, data.cameraData);
                });
            }
        }
    }
}
