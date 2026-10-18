using System;
using UnityEngine.Experimental.GlobalIllumination;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using Unity.Collections;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal.Internal
{
    // Render all tiled-based deferred lights.
    internal class GBufferPass : ScriptableRenderPass
    {
        // Statics
        private static readonly int s_CameraNormalsTextureID = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int s_CameraRenderingLayersTextureID = Shader.PropertyToID("_CameraRenderingLayersTexture");
        private static readonly ShaderTagId s_ShaderTagLit = new ShaderTagId("Lit");
        private static readonly ShaderTagId s_ShaderTagSimpleLit = new ShaderTagId("SimpleLit");
        private static readonly ShaderTagId s_ShaderTagUnlit = new ShaderTagId("Unlit");
        private static readonly ShaderTagId s_ShaderTagComplexLit = new ShaderTagId("ComplexLit");
        private static readonly ShaderTagId s_ShaderTagUniversalGBuffer = new ShaderTagId("UniversalGBuffer");
        private static readonly ShaderTagId s_ShaderTagUniversalMaterialType = new ShaderTagId("UniversalMaterialType");

        DeferredLights m_DeferredLights;

        static ShaderTagId[] s_ShaderTagValues;
        static RenderStateBlock[] s_RenderStateBlocks;

        FilteringSettings m_FilteringSettings;
        RenderStateBlock m_RenderStateBlock;

        public GBufferPass(RenderPassEvent evt, RenderQueueRange renderQueueRange, LayerMask layerMask, StencilState stencilState, int stencilReference, DeferredLights deferredLights)
        {
            base.profilingSampler = new ProfilingSampler("Draw GBuffer");
            base.renderPassEvent = evt;
            m_DeferredLights = deferredLights;
            m_FilteringSettings = new FilteringSettings(renderQueueRange, layerMask);
            m_RenderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);

            m_RenderStateBlock.stencilState = stencilState;
            m_RenderStateBlock.stencilReference = stencilReference;
            m_RenderStateBlock.mask = RenderStateMask.Stencil;

            if (s_ShaderTagValues == null)
            {
                s_ShaderTagValues = new ShaderTagId[5];
                s_ShaderTagValues[0] = s_ShaderTagLit;
                s_ShaderTagValues[1] = s_ShaderTagSimpleLit;
                s_ShaderTagValues[2] = s_ShaderTagUnlit;
                s_ShaderTagValues[3] = s_ShaderTagComplexLit;
                s_ShaderTagValues[4] = new ShaderTagId(); // Special catch all case for materials where UniversalMaterialType is not defined or the tag value doesn't match anything we know.
            }

            if (s_RenderStateBlocks == null)
            {
                s_RenderStateBlocks = new RenderStateBlock[5];
                s_RenderStateBlocks[0] = DeferredLights.OverwriteStencil(m_RenderStateBlock, (int)StencilUsage.MaterialMask, (int)StencilUsage.MaterialLit);
                s_RenderStateBlocks[1] = DeferredLights.OverwriteStencil(m_RenderStateBlock, (int)StencilUsage.MaterialMask, (int)StencilUsage.MaterialSimpleLit);
                s_RenderStateBlocks[2] = DeferredLights.OverwriteStencil(m_RenderStateBlock, (int)StencilUsage.MaterialMask, (int)StencilUsage.MaterialUnlit);
                s_RenderStateBlocks[3] = DeferredLights.OverwriteStencil(m_RenderStateBlock, (int)StencilUsage.MaterialMask, (int)StencilUsage.MaterialUnlit);  // Fill GBuffer, but skip lighting pass for ComplexLit
                s_RenderStateBlocks[4] = s_RenderStateBlocks[0];
            }
        }

        public void Dispose()
        {
            m_DeferredLights?.ReleaseGbufferResources();
        }

        private static void ExecuteRenderGraphPass(RasterCommandBuffer cmd, PassData data, RendererList rendererList, RendererList errorRendererList)
        {
            bool usesRenderingLayers = data.deferredLights.UseRenderingLayers && !data.deferredLights.HasRenderingLayerPrepass;
            if (usesRenderingLayers)
                cmd.SetKeyword(ShaderGlobalKeywords.WriteRenderingLayers, true);

            if (data.deferredLights.IsOverlay)
                data.deferredLights.ClearStencilPartial(cmd);

            cmd.DrawRendererList(rendererList);

            // Render objects that did not match any shader pass with error shader
            RenderingUtils.DrawRendererListObjectsWithError(cmd, ref errorRendererList);

            // Clean up
            if (usesRenderingLayers)
                cmd.SetKeyword(ShaderGlobalKeywords.WriteRenderingLayers, false);
        }

        /// <summary>
        /// Shared pass data
        /// </summary>
        private class PassData
        {
            internal TextureHandle[] gbuffer;
            internal TextureHandle depth;

            internal DeferredLights deferredLights;

            internal RendererListHandle rendererListHdl;
            internal RendererListHandle objectsWithErrorRendererListHdl;
        }
        private void InitRendererLists(ref PassData passData, RenderGraph renderGraph, UniversalRenderingData renderingData, UniversalCameraData cameraData, UniversalLightData lightData)
        {
            // User can stack several scriptable renderers during rendering but deferred renderer should only lit pixels added by this gbuffer pass.
            // If we detect we are in such case (camera is in overlay mode), we clear the highest bits of stencil we have control of and use them to
            // mark what pixel to shade during deferred pass. Gbuffer will always mark pixels using their material types.
            ShaderTagId lightModeTag = s_ShaderTagUniversalGBuffer;
            var drawingSettings = CreateDrawingSettings(lightModeTag, renderingData, cameraData, lightData, cameraData.defaultOpaqueSortFlags);
            var filterSettings = m_FilteringSettings;
            NativeArray<ShaderTagId> tagValues = new NativeArray<ShaderTagId>(s_ShaderTagValues, Allocator.Temp);
            NativeArray<RenderStateBlock> stateBlocks = new NativeArray<RenderStateBlock>(s_RenderStateBlocks, Allocator.Temp);
            var param = new RendererListParams(renderingData.cullResults, drawingSettings, filterSettings)
            {
                tagValues = tagValues,
                stateBlocks = stateBlocks,
                tagName = s_ShaderTagUniversalMaterialType,
                isPassTagName = false
            };
            passData.rendererListHdl = renderGraph.CreateRendererList(param);
            tagValues.Dispose();
            stateBlocks.Dispose();

            RenderingUtils.CreateRendererListObjectsWithError(renderGraph, ref renderingData.cullResults, cameraData.camera, filterSettings, SortingCriteria.None, ref passData.objectsWithErrorRendererListHdl);
        }

        internal void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle cameraColor, TextureHandle cameraDepth, bool setGlobalTextures)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();

            TextureHandle[] gbuffer;

            bool useCameraRenderingLayersTexture = m_DeferredLights.UseRenderingLayers && !m_DeferredLights.UseLightLayers;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
            {
                APRTRelightPass.UseLightingResources(builder, frameData);
                // Note: This code is pretty confusing as passData.gbuffer[i] and gbuffer[i] actually point to the same array but seem to be mixed in this code.
                passData.gbuffer = gbuffer = m_DeferredLights.GbufferTextureHandles;
                for (int i = 0; i < m_DeferredLights.GBufferSliceCount; i++)
                {
                    var gbufferSlice = cameraData.cameraTargetDescriptor;
                    gbufferSlice.depthStencilFormat = GraphicsFormat.None; // make sure no depth surface is actually created
                    gbufferSlice.stencilFormat = GraphicsFormat.None;

                    if (i == m_DeferredLights.GBufferNormalSmoothnessIndex && m_DeferredLights.HasNormalPrepass)
                        gbuffer[i] = resourceData.cameraNormalsTexture;
                    else if (i == m_DeferredLights.GBufferRenderingLayers && useCameraRenderingLayersTexture)
                        gbuffer[i] = resourceData.renderingLayersTexture;
                    else if (i != m_DeferredLights.GBufferLightingIndex)
                    {
                        gbufferSlice.graphicsFormat = m_DeferredLights.GetGBufferFormat(i);
                        gbuffer[i] = UniversalRenderer.CreateRenderGraphTexture(renderGraph, gbufferSlice, DeferredLights.k_GBufferNames[i], true);
                    }
                    else
                        gbuffer[i] = cameraColor;

                    // Note: We don't store the returned handle here it is a versioned handle.
                    // In general it should be fine to use unversioned handles anyway especially unversioned resources
                    // should be registered in the frame data
                    builder.SetRenderAttachment(gbuffer[i], i, AccessFlags.Write);
                }

                RenderGraphUtils.UseDBufferIfValid(builder, resourceData);
                resourceData.gBuffer = gbuffer;

                passData.depth = cameraDepth;
                builder.SetRenderAttachmentDepth(cameraDepth, AccessFlags.Write);
                passData.deferredLights = m_DeferredLights;

                InitRendererLists(ref passData, renderGraph, renderingData, cameraData, lightData);
                builder.UseRendererList(passData.rendererListHdl);
                builder.UseRendererList(passData.objectsWithErrorRendererListHdl);

                if (setGlobalTextures)
                {
                    builder.SetGlobalTextureAfterPass(resourceData.cameraNormalsTexture, s_CameraNormalsTextureID);

                    if (useCameraRenderingLayersTexture)
                        builder.SetGlobalTextureAfterPass(resourceData.renderingLayersTexture, s_CameraRenderingLayersTextureID);
                }

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    ExecuteRenderGraphPass(context.cmd, data, data.rendererListHdl, data.objectsWithErrorRendererListHdl);
                });
            }
        }
    }
}
