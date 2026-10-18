using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Copies the deferred rendering-layer GBuffer slice into the persistent camera-stack
    /// texture consumed by forward overlay cameras.
    /// </summary>
    internal static class CopyRenderingLayersPass
    {
        private const string k_PassName = "Copy Rendering Layers";
        private static readonly ProfilingSampler s_ProfilingSampler =
            new ProfilingSampler(k_PassName);

        private sealed class PassData
        {
            internal TextureHandle source;
            internal TextureHandle destination;
            internal int firstSlice;
            internal int sliceCount;
        }

        private static void GetSliceRange(ref RenderingData renderingData,
            out int firstSlice, out int sliceCount)
        {
            firstSlice = 0;
            sliceCount = 1;

#if ENABLE_VR && ENABLE_XR_MODULE
            ref CameraData cameraData = ref renderingData.cameraData;
            if (!cameraData.xr.enabled)
                return;

            if (cameraData.xr.singlePassEnabled)
            {
                // Single-pass renders every eye into the array in one graph execution. The
                // XR descriptor is authoritative for the number of slices in both the GBuffer
                // and the persistent destination.
                sliceCount = Mathf.Max(1, cameraData.cameraTargetDescriptor.volumeDepth);
            }
            else
            {
                // Multipass executes one graph per eye. Copy only the eye that was rendered;
                // copying the whole array here could overwrite the other eye with stale data.
                firstSlice = cameraData.xr.GetTextureArraySlice();
            }
#endif
        }

        private static void CopySlices(CommandBuffer commandBuffer,
            RenderTargetIdentifier source, RenderTargetIdentifier destination,
            int firstSlice, int sliceCount)
        {
            for (int slice = firstSlice; slice < firstSlice + sliceCount; ++slice)
                commandBuffer.CopyTexture(source, slice, 0, destination, slice, 0);
        }

        internal static void Render(RenderGraph renderGraph, TextureHandle source,
            TextureHandle destination, ref RenderingData renderingData)
        {
            if (!source.IsValid() || !destination.IsValid())
                return;

            GetSliceRange(ref renderingData, out int firstSlice, out int sliceCount);

            using (var builder = renderGraph.AddUnsafePass<PassData>(
                k_PassName, out var passData, s_ProfilingSampler))
            {
                passData.source = source;
                passData.destination = destination;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(destination, AccessFlags.Write);
                passData.firstSlice = firstSlice;
                passData.sliceCount = sliceCount;
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                {
                    CopySlices(CommandBufferHelpers.GetNativeCommandBuffer(context.cmd),
                        data.source, data.destination,
                        data.firstSlice, data.sliceCount);
                });
            }
        }
    }
}
