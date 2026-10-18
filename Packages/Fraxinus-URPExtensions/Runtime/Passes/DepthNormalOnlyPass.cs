using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Render all objects that have a 'DepthNormals' and/or 'DepthNormalsOnly' pass into the given depth and normal buffers.
    /// </summary>
    public class DepthNormalOnlyPass : ScriptableRenderPass
    {
        // GPU-driven streams can contribute to the normal prepass while the graph-owned MRT is
        // bound through this RenderGraph-native extension point.
        public static event Action<ShadowUtils.DepthNormalOnlyRenderGraphContext>
            indirectDepthNormalsRenderGraph
        {
            add
            {
                s_IndirectDepthNormalsRenderGraph += value;
                s_IndirectDepthNormalsRenderGraphSubscribers =
                    s_IndirectDepthNormalsRenderGraph?.GetInvocationList();
            }
            remove
            {
                s_IndirectDepthNormalsRenderGraph -= value;
                s_IndirectDepthNormalsRenderGraphSubscribers =
                    s_IndirectDepthNormalsRenderGraph?.GetInvocationList();
            }
        }
        static Action<ShadowUtils.DepthNormalOnlyRenderGraphContext>
            s_IndirectDepthNormalsRenderGraph;
        static Delegate[] s_IndirectDepthNormalsRenderGraphSubscribers;

        /// <summary>
        /// Runs before the normals raster pass is opened. Record compute preparation here;
        /// the null-builder context collects dependencies and draws for the owning raster pass.
        /// </summary>
        public static event Action<ShadowUtils.DepthNormalOnlyRenderGraphContext> indirectDepthNormalsRenderGraphPrepare
        {
            add
            {
                s_IndirectDepthNormalsRenderGraphPrepare += value;
                s_IndirectDepthNormalsRenderGraphPrepareSubscribers = s_IndirectDepthNormalsRenderGraphPrepare?.GetInvocationList();
            }
            remove
            {
                s_IndirectDepthNormalsRenderGraphPrepare -= value;
                s_IndirectDepthNormalsRenderGraphPrepareSubscribers = s_IndirectDepthNormalsRenderGraphPrepare?.GetInvocationList();
            }
        }
        static Action<ShadowUtils.DepthNormalOnlyRenderGraphContext> s_IndirectDepthNormalsRenderGraphPrepare;
        static Delegate[] s_IndirectDepthNormalsRenderGraphPrepareSubscribers;

        internal List<ShaderTagId> shaderTagIds { get; set; }

        internal bool enableRenderingLayers { get; set; } = false;
        internal RenderingLayerUtils.MaskSize renderingLayersMaskSize { get; set; }
        private FilteringSettings m_FilteringSettings;

        // Statics
        private static readonly List<ShaderTagId> k_DepthNormals = new List<ShaderTagId> { new ShaderTagId("DepthNormals"), new ShaderTagId("DepthNormalsOnly") };
        private static readonly int s_CameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int s_CameraNormalsTextureID = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int s_CameraRenderingLayersTextureID = Shader.PropertyToID("_CameraRenderingLayersTexture");

        /// <summary>
        /// Creates a new <c>DepthNormalOnlyPass</c> instance.
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <param name="renderQueueRange">The <c>RenderQueueRange</c> to use for creating filtering settings that control what objects get rendered.</param>
        /// <param name="layerMask">The layer mask to use for creating filtering settings that control what objects get rendered.</param>
        /// <seealso cref="RenderPassEvent"/>
        /// <seealso cref="RenderQueueRange"/>
        /// <seealso cref="LayerMask"/>
        public DepthNormalOnlyPass(RenderPassEvent evt, RenderQueueRange renderQueueRange, LayerMask layerMask)
        {
            profilingSampler = ProfilingSampler.Get(URPProfileId.DrawDepthNormalPrepass);
            m_FilteringSettings = new FilteringSettings(renderQueueRange, layerMask);
            renderPassEvent = evt;
            this.shaderTagIds = k_DepthNormals;
        }

        /// <summary>
        /// Finds the format to use for the normals texture.
        /// </summary>
        /// <returns>The GraphicsFormat to use with the Normals texture.</returns>
        public static GraphicsFormat GetGraphicsFormat()
        {
            if (GraphicsFormatCompatibility.IsFormatSupported(GraphicsFormat.R8G8B8A8_SNorm, GraphicsFormatUsage.Render))
                return GraphicsFormat.R8G8B8A8_SNorm; // Preferred format
            else if (GraphicsFormatCompatibility.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormatUsage.Render))
                return GraphicsFormat.R16G16B16A16_SFloat; // fallback
            else
                return GraphicsFormat.R32G32B32A32_SFloat; // fallback
        }

        private static void ExecutePass(RasterCommandBuffer cmd, PassData passData, RendererList rendererList)
        {
            // Enable Rendering Layers
            if (passData.enableRenderingLayers)
                cmd.SetKeyword(ShaderGlobalKeywords.WriteRenderingLayers, true);

            // Draw
            cmd.DrawRendererList(rendererList);

            for (int index = 0; index < passData.renderGraphFuncs.Count; ++index)
            {
                try
                {
                    passData.renderGraphFuncs[index]?.Invoke(cmd);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            // Clean up
            if (passData.enableRenderingLayers)
                cmd.SetKeyword(ShaderGlobalKeywords.WriteRenderingLayers, false);
        }

        // RenderGraph owns pass lifetime.  Cleanup is invoked explicitly by the
        // renderer after graph execution, so this pass has no compatibility
        // ScriptableRenderPass lifecycle callback.
        internal void CleanupRenderGraph()
        {
            // This needs to be reset as the renderer might change this in runtime (UUM-36069)
            shaderTagIds = k_DepthNormals;
        }

        /// <summary>
        /// Shared pass data
        /// </summary>
        private class PassData
        {
            internal TextureHandle cameraDepthTexture;
            internal TextureHandle cameraNormalsTexture;
            internal bool enableRenderingLayers;
            internal RenderingLayerUtils.MaskSize maskSize;
            internal RendererListHandle rendererList;
            internal readonly List<Action<RasterCommandBuffer>> renderGraphFuncs = new();
            internal readonly List<ShadowUtils.RenderGraphBufferUse> bufferUses = new();
            internal readonly List<ShadowUtils.RenderGraphTextureUse> textureUses = new();

            internal void ClearRecordingLists()
            {
                renderGraphFuncs.Clear();
                bufferUses.Clear();
                textureUses.Clear();
            }
        }

        private sealed class PreparedPassData : PassData, IDisposable
        {
            public void Dispose()
            {
                ClearRecordingLists();
                GenericPool<PreparedPassData>.Release(this);
            }
        }

        private RendererListParams InitRendererListParams(UniversalRenderingData renderingData, UniversalCameraData cameraData, UniversalLightData lightData)
        {
            var sortFlags = cameraData.defaultOpaqueSortFlags;
            var drawSettings = RenderingUtils.CreateDrawingSettings(this.shaderTagIds, renderingData, cameraData, lightData, sortFlags);
            drawSettings.perObjectData = PerObjectData.None;
            return new RendererListParams(renderingData.cullResults, drawSettings, m_FilteringSettings);
        }

        internal void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle cameraNormalsTexture, TextureHandle cameraDepthTexture, TextureHandle renderingLayersTexture, uint batchLayerMask, bool setGlobalDepth, bool setGlobalTextures)
        {
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();

            Delegate[] prepareSubscribers = s_IndirectDepthNormalsRenderGraphPrepareSubscribers;
            using var preparation = prepareSubscribers != null ? GenericPool<PreparedPassData>.Get() : null;
            if (preparation != null)
            {
                RecordIndirectDepthNormalsRenderGraph(renderGraph, null, frameData, cameraDepthTexture, cameraNormalsTexture, renderingLayersTexture,
                    enableRenderingLayers, batchLayerMask, preparation.renderGraphFuncs, prepareSubscribers, preparation.bufferUses, preparation.textureUses);
            }

            Delegate[] subscribers = s_IndirectDepthNormalsRenderGraphSubscribers;
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
            {
                passData.ClearRecordingLists();
                if (preparation != null)
                {
                    passData.renderGraphFuncs.AddRange(preparation.renderGraphFuncs);
                    passData.bufferUses.AddRange(preparation.bufferUses);
                    passData.textureUses.AddRange(preparation.textureUses);
                }
                ShadowUtils.ApplyRenderGraphBufferUses(builder, passData.bufferUses);
                ShadowUtils.ApplyRenderGraphTextureUses(builder, passData.textureUses);
                passData.cameraNormalsTexture = cameraNormalsTexture;
                builder.SetRenderAttachment(cameraNormalsTexture, 0, AccessFlags.Write);
                passData.cameraDepthTexture = cameraDepthTexture;
                builder.SetRenderAttachmentDepth(cameraDepthTexture, AccessFlags.Write);

                passData.enableRenderingLayers = enableRenderingLayers;

                if (passData.enableRenderingLayers)
                {
                    builder.SetRenderAttachment(renderingLayersTexture, 1, AccessFlags.Write);
                    passData.maskSize = renderingLayersMaskSize;
                }

                var param = InitRendererListParams(renderingData, cameraData, lightData);
#if UNITY_6000_0_OR_NEWER
                param.filteringSettings.batchLayerMask = batchLayerMask;
#endif
                passData.rendererList = renderGraph.CreateRendererList(param);
                builder.UseRendererList(passData.rendererList);
                if (cameraData.xr.enabled)
                    builder.EnableFoveatedRasterization(cameraData.xr.supportsFoveatedRendering && cameraData.xrUniversal.canFoveateIntermediatePasses);

                if (setGlobalTextures)
                {
                    builder.SetGlobalTextureAfterPass(cameraNormalsTexture, s_CameraNormalsTextureID);

                    if (passData.enableRenderingLayers)
                        builder.SetGlobalTextureAfterPass(renderingLayersTexture, s_CameraRenderingLayersTextureID);
                }

                if (setGlobalDepth)
                    builder.SetGlobalTextureAfterPass(cameraDepthTexture, s_CameraDepthTextureID);

                RecordIndirectDepthNormalsRenderGraph(renderGraph, builder, frameData, cameraDepthTexture, cameraNormalsTexture, renderingLayersTexture,
                    passData.enableRenderingLayers, batchLayerMask, passData.renderGraphFuncs, subscribers);

                //  TODO RENDERGRAPH: culling? force culling off for testing
                builder.AllowPassCulling(false);
                // Required here because of RenderingLayerUtils.SetupProperties
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    RenderingLayerUtils.SetupProperties(context.cmd, data.maskSize);
                    ExecutePass(context.cmd, data, data.rendererList);
                });
            }
        }

        private static void RecordIndirectDepthNormalsRenderGraph(
            RenderGraph renderGraph,
            IRasterRenderGraphBuilder builder,
            ContextContainer frameData,
            TextureHandle cameraDepthTexture,
            TextureHandle cameraNormalsTexture,
            TextureHandle renderingLayersTexture,
            bool writeRenderingLayers,
            uint batchLayerMask,
            List<Action<RasterCommandBuffer>> renderGraphFuncs,
            Delegate[] subscribers,
            List<ShadowUtils.RenderGraphBufferUse> bufferUses = null,
            List<ShadowUtils.RenderGraphTextureUse> textureUses = null)
        {
            if (subscribers == null)
                return;

            var callbackData = new ShadowUtils.DepthNormalOnlyRenderGraphContext(
                renderGraph, builder, frameData, cameraDepthTexture,
                cameraNormalsTexture, renderingLayersTexture, writeRenderingLayers,
                batchLayerMask, renderGraphFuncs, bufferUses, textureUses);
            for (int index = 0; index < subscribers.Length; ++index)
            {
                try
                {
                    ((Action<ShadowUtils.DepthNormalOnlyRenderGraphContext>)subscribers[index])(
                        callbackData);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

    }
}
