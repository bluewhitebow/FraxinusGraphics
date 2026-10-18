using System;
using System.Collections.Generic;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Render all objects that have a 'DepthOnly' pass into the given depth buffer.
    ///
    /// You can use this pass to prime a depth buffer for subsequent rendering.
    /// Use it as a z-prepass, or use it to generate a depth buffer.
    /// </summary>
    public class DepthOnlyPass : ScriptableRenderPass
    {
        // GPU-driven renderers can append their draws while the graph-owned depth attachment is
        // still bound through this RenderGraph-native extension point.
        public static event Action<ShadowUtils.DepthOnlyRenderGraphContext>
            indirectDepthRenderGraph
        {
            add
            {
                s_IndirectDepthRenderGraph += value;
                s_IndirectDepthRenderGraphSubscribers =
                    s_IndirectDepthRenderGraph?.GetInvocationList();
            }
            remove
            {
                s_IndirectDepthRenderGraph -= value;
                s_IndirectDepthRenderGraphSubscribers =
                    s_IndirectDepthRenderGraph?.GetInvocationList();
            }
        }
        static Action<ShadowUtils.DepthOnlyRenderGraphContext> s_IndirectDepthRenderGraph;
        static Delegate[] s_IndirectDepthRenderGraphSubscribers;

        /// <summary>
        /// Runs before the raster pass is opened, so indirect renderers can record compute
        /// preparation. The context builder is null; dependencies and draws are replayed on
        /// the owning depth raster pass, matching the shadow-slice prepare contract.
        /// </summary>
        public static event Action<ShadowUtils.DepthOnlyRenderGraphContext> indirectDepthRenderGraphPrepare
        {
            add
            {
                s_IndirectDepthRenderGraphPrepare += value;
                s_IndirectDepthRenderGraphPrepareSubscribers = s_IndirectDepthRenderGraphPrepare?.GetInvocationList();
            }
            remove
            {
                s_IndirectDepthRenderGraphPrepare -= value;
                s_IndirectDepthRenderGraphPrepareSubscribers = s_IndirectDepthRenderGraphPrepare?.GetInvocationList();
            }
        }
        static Action<ShadowUtils.DepthOnlyRenderGraphContext> s_IndirectDepthRenderGraphPrepare;
        static Delegate[] s_IndirectDepthRenderGraphPrepareSubscribers;

        internal ShaderTagId shaderTagId { get; set; } = k_ShaderTagId;

        FilteringSettings m_FilteringSettings;

        // Statics
        private static readonly ShaderTagId k_ShaderTagId = new ShaderTagId("DepthOnly");
        private static readonly int s_CameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");

        /// <summary>
        /// Creates a new <c>DepthOnlyPass</c> instance.
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <param name="renderQueueRange">The <c>RenderQueueRange</c> to use for creating filtering settings that control what objects get rendered.</param>
        /// <param name="layerMask">The layer mask to use for creating filtering settings that control what objects get rendered.</param>
        /// <seealso cref="RenderPassEvent"/>
        /// <seealso cref="RenderQueueRange"/>
        /// <seealso cref="LayerMask"/>
        public DepthOnlyPass(RenderPassEvent evt, RenderQueueRange renderQueueRange, LayerMask layerMask)
        {
            profilingSampler = new ProfilingSampler("Draw Depth Only");
            m_FilteringSettings = new FilteringSettings(renderQueueRange, layerMask);
            renderPassEvent = evt;
            this.shaderTagId = k_ShaderTagId;
        }

        private static void ExecutePass(RasterCommandBuffer cmd, RendererList rendererList)
        {
            using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.DepthPrepass)))
            {
                cmd.DrawRendererList(rendererList);
            }
        }

        private class PassData
        {
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
            var drawSettings = RenderingUtils.CreateDrawingSettings(this.shaderTagId, renderingData, cameraData, lightData, sortFlags);
            drawSettings.perObjectData = PerObjectData.None;
            return new RendererListParams(renderingData.cullResults, drawSettings, m_FilteringSettings);
        }

        internal void Render(RenderGraph renderGraph, ContextContainer frameData, ref TextureHandle cameraDepthTexture, uint batchLayerMask, bool setGlobalDepth)
        {
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();

            Delegate[] prepareSubscribers = s_IndirectDepthRenderGraphPrepareSubscribers;
            using var preparation = prepareSubscribers != null ? GenericPool<PreparedPassData>.Get() : null;
            if (preparation != null)
            {
                RecordIndirectDepthRenderGraph(renderGraph, null, frameData, cameraDepthTexture, batchLayerMask,
                    preparation.renderGraphFuncs, prepareSubscribers, preparation.bufferUses, preparation.textureUses);
            }

            Delegate[] subscribers = s_IndirectDepthRenderGraphSubscribers;
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
                var param = InitRendererListParams(renderingData, cameraData, lightData);
#if UNITY_6000_0_OR_NEWER
                param.filteringSettings.batchLayerMask = batchLayerMask;
#endif
                passData.rendererList = renderGraph.CreateRendererList(param);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderAttachmentDepth(cameraDepthTexture, AccessFlags.Write);

                if (setGlobalDepth)
                    builder.SetGlobalTextureAfterPass(cameraDepthTexture, s_CameraDepthTextureID);

                RecordIndirectDepthRenderGraph(renderGraph, builder, frameData, cameraDepthTexture, batchLayerMask,
                    passData.renderGraphFuncs, subscribers);

                //  TODO RENDERGRAPH: culling? force culling off for testing
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                if (cameraData.xr.enabled)
                    builder.EnableFoveatedRasterization(cameraData.xr.supportsFoveatedRendering && cameraData.xrUniversal.canFoveateIntermediatePasses);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    ExecutePass(context.cmd, data.rendererList);
                    for (int index = 0; index < data.renderGraphFuncs.Count; ++index)
                    {
                        try
                        {
                            data.renderGraphFuncs[index]?.Invoke(context.cmd);
                        }
                        catch (Exception exception)
                        {
                            Debug.LogException(exception);
                        }
                    }
                });
            }
        }

        private static void RecordIndirectDepthRenderGraph(
            RenderGraph renderGraph,
            IRasterRenderGraphBuilder builder,
            ContextContainer frameData,
            TextureHandle cameraDepthTexture,
            uint batchLayerMask,
            List<Action<RasterCommandBuffer>> renderGraphFuncs,
            Delegate[] subscribers,
            List<ShadowUtils.RenderGraphBufferUse> bufferUses = null,
            List<ShadowUtils.RenderGraphTextureUse> textureUses = null)
        {
            if (subscribers == null)
                return;

            var callbackData = new ShadowUtils.DepthOnlyRenderGraphContext(
                renderGraph, builder, frameData, cameraDepthTexture,
                batchLayerMask, renderGraphFuncs, bufferUses, textureUses);
            for (int index = 0; index < subscribers.Length; ++index)
            {
                try
                {
                    ((Action<ShadowUtils.DepthOnlyRenderGraphContext>)subscribers[index])(
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
