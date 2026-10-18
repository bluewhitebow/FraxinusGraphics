using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    public sealed partial class UniversalRenderPipeline
    {
        static void RecordRenderGraph(RenderGraph renderGraph, ScriptableRenderContext context, ScriptableRenderer renderer)
        {
            renderer.RecordRenderGraph(renderGraph, context);
        }

        static void ExecuteRenderGraph(RenderGraph renderGraph, ScriptableRenderContext context, ScriptableRenderer renderer, CommandBuffer cmd, Camera camera, string cameraName)
        {
            RenderGraphParameters rgParams = new RenderGraphParameters
            {
                executionName = cameraName,
                commandBuffer = cmd,
                scriptableRenderContext = context,
                currentFrameIndex = Time.frameCount,
            };

            // Match the URP 6 recording contract directly. The backported RPCore exposes
            // these native entry points.
            renderGraph.BeginRecording(rgParams);

            // Reset camera-owned external HZB imports for this graph execution. The same
            // RenderGraph instance is reused across stacked cameras and frames, while its
            // BufferHandles are execution-local.
            HZBOcclusionData.BeginAllRenderGraphRecording(renderGraph);

            RecordRenderGraph(renderGraph, context, renderer);

            renderGraph.EndRecordingAndExecute();
            renderer?.FinishRenderGraphRendering(cmd);

            HZBOcclusionData.EndAllRenderGraphRecording(cmd);
        }
    }
}
