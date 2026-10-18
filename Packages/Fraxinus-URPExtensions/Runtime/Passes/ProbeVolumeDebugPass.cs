using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Uses a compute shader to capture the depth and normal of the pixel under the cursor.
    /// </summary>
    internal partial class ProbeVolumeDebugPass : ScriptableRenderPass
    {
        ComputeShader m_ComputeShader;

        /// <summary>
        /// Creates a new <c>ProbeVolumeDebugPass</c> instance.
        /// </summary>
        public ProbeVolumeDebugPass(RenderPassEvent evt, ComputeShader computeShader)
        {
            base.profilingSampler = new ProfilingSampler("Dispatch APV Debug");
            renderPassEvent = evt;
            m_ComputeShader = computeShader;
        }

        class WriteApvData
        {
            public ComputeShader computeShader;
            public int kernel;
            public BufferHandle resultBuffer;
            public Vector2 clickCoordinates;
            public TextureHandle depthBuffer;
            public TextureHandle normalBuffer;
        }

        internal static bool IsAPRTDebugCameraEligible(UniversalCameraData cameraData)
        {
            if (!APRTRelightPass.CanUpdate(cameraData))
                return false;

            var dimension = cameraData.cameraTargetDescriptor.dimension;
            if (dimension == TextureDimension.Cube || dimension == TextureDimension.CubeArray)
                return false;
            var target = cameraData.camera.targetTexture;
            return target == null || (target.dimension != TextureDimension.Cube && target.dimension != TextureDimension.CubeArray);
        }

        /// <summary>
        /// Render graph entry point
        /// </summary>
        /// <param name="renderGraph"></param>
        /// <param name="renderingData"></param>
        /// <param name="depthPyramidBuffer"></param>
        /// <param name="normalBuffer"></param>
        internal void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle depthPyramidBuffer, TextureHandle normalBuffer)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            var asset = UniversalRenderPipeline.asset;
            // APRT owns its probe diagnostics; this surface-picking kernel is APV-specific.
            if (asset == null || !asset.supportAPV)
                return;

            if (m_ComputeShader == null || !depthPyramidBuffer.IsValid() || !normalBuffer.IsValid() ||
                !ProbeReferenceVolumeProvider.instance.isInitialized)
                return;

            if (ProbeReferenceVolumeProvider.instance.GetProbeSamplingDebugResources(cameraData.camera, out var resultBuffer, out Vector2 coords) &&
                resultBuffer != null && resultBuffer.IsValid() && resultBuffer.count >= 2 && resultBuffer.stride == 16)
            {
                using (var builder = renderGraph.AddComputePass<WriteApvData>(passName, out var passData, profilingSampler))
                {
                    passData.clickCoordinates = coords;
                    passData.computeShader = m_ComputeShader;
                    passData.kernel = m_ComputeShader.FindKernel("ComputePositionNormal");

                    passData.resultBuffer = renderGraph.ImportBuffer(resultBuffer);
                    passData.depthBuffer = depthPyramidBuffer;
                    passData.normalBuffer = normalBuffer;

                    builder.UseBuffer(passData.resultBuffer, AccessFlags.Write);
                    builder.UseTexture(passData.depthBuffer, AccessFlags.Read);
                    builder.UseTexture(passData.normalBuffer, AccessFlags.Read);
                    // Core consumes this imported buffer after the graph has executed.
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((WriteApvData data, ComputeGraphContext ctx) =>
                    {
                        int kernel = data.kernel;

                        ctx.cmd.SetComputeTextureParam(data.computeShader, kernel, "_CameraDepthTexture", data.depthBuffer);
                        ctx.cmd.SetComputeTextureParam(data.computeShader, kernel, "_NormalBufferTexture", data.normalBuffer);
                        ctx.cmd.SetComputeVectorParam(data.computeShader, "_positionSS", new Vector4(data.clickCoordinates.x, data.clickCoordinates.y, 0.0f, 0.0f));
                        ctx.cmd.SetComputeBufferParam(data.computeShader, kernel, "_ResultBuffer", data.resultBuffer);
                        ctx.cmd.DispatchCompute(data.computeShader, kernel, 1, 1, 1);
                    });
                }
            }
        }
    }
}
