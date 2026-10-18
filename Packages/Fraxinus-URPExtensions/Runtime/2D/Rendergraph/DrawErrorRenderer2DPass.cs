using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Draws renderers whose materials contain no shader pass supported by URP.
    /// </summary>
    internal static class DrawErrorRenderer2DPass
    {
        private const string k_PassName = "Draw 2D Objects With Error";
        private static readonly ProfilingSampler s_ProfilingSampler =
            new ProfilingSampler(k_PassName);

        private sealed class PassData
        {
            internal RendererListHandle rendererList;
        }

        internal static void Render(RenderGraph renderGraph, ContextContainer frameData,
            TextureHandle colorTarget, TextureHandle depthTarget)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            FilteringSettings filterSettings = FilteringSettings.defaultValue;
            filterSettings.renderQueueRange = RenderQueueRange.all;
            filterSettings.layerMask = -1;
            filterSettings.renderingLayerMask = uint.MaxValue;
            filterSettings.sortingLayerRange = SortingLayerRange.all;

            RendererListHandle rendererList = default;
            RenderingUtils.CreateRendererListObjectsWithError(renderGraph,
                ref renderingData.cullResults,
                cameraData.camera,
                filterSettings,
                SortingCriteria.None,
                ref rendererList);

            if (!rendererList.IsValid())
                return;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                       k_PassName, out var passData, s_ProfilingSampler))
            {
                passData.rendererList = rendererList;
                builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.ReadWrite);
                builder.UseRendererList(rendererList);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
#endif
        }
    }
}
