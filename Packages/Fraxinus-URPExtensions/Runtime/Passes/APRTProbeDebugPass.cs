using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    // Probe spheres read the current camera's relight buffers directly. Keep
    // their dependencies in the graph, rather than emitting temporary renderers.
    internal static class APRTProbeDebugPass
    {
        static readonly ProfilingSampler s_Sampler = new ProfilingSampler("APRT Probe Debug");

        sealed class PassData
        {
            internal APRTProbeReferenceVolume volume;
            internal APRTProbeReferenceVolume.ProbeDebugDraw draw;
        }

        internal static void Render(RenderGraph renderGraph, ContextContainer frameData,
            TextureHandle colorTarget, TextureHandle depthTarget)
        {
            var asset = UniversalRenderPipeline.asset;
            if (asset == null || !asset.supportAPRT || !colorTarget.IsValid() || !depthTarget.IsValid())
                return;
            var cameraData = frameData.Get<UniversalCameraData>();
            if (!APRTRelightPass.CanUpdate(cameraData) || !APRTRelightPass.HasLightingResources(frameData))
                return;
            var volume = APRTProbeReferenceVolume.instance;
            if (!volume.showProbeDebug) return;

            var runtimes = volume.GetRenderingBakingSetRuntimes(cameraData.camera);
            for (int i = 0; i < runtimes.Count; ++i)
            {
                var runtime = runtimes[i];
                var settings = APRTRelightPass.ResolveSettings(VolumeManager.instance.stack, runtime.currentBakingSet);
                if (!runtime.TryGetProbeDebugDraw(cameraData.camera, settings, out var draw)) continue;
                draw.properties.SetMatrix("_APRTProbeDebugViewProjection",
                    cameraData.GetGPUProjectionMatrix() * cameraData.GetViewMatrix());
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("APRT Probe Debug", out var data, s_Sampler))
                {
                    data.volume = runtime;
                    data.draw = draw;
                    builder.SetRenderAttachment(colorTarget, 0, AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Read);
                    // Reuse the handles that declared this camera's compute writes.
                    // Importing the same native buffer again creates another graph
                    // resource and would hide the relight-to-debug dependency.
                    APRTRelightPass.UseLightingResources(builder, frameData);
                    builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
                        pass.volume.DrawProbeDebug(context.cmd, pass.draw));
                }
            }
        }
    }
}
