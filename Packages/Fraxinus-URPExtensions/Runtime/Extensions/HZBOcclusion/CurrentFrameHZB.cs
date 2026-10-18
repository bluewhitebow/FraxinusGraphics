using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// A depth pyramid belonging to one camera RenderGraph recording. It never publishes
    /// history or owns the backing GraphicsBuffer; consumers must declare their graph read.
    /// </summary>
    public sealed class CurrentFrameHZB
    {
        internal const int PackedMipCount = 7;
        private const string KernelName = "OccluderDepthDownscale";
        private static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler("Current Frame HZB");

        public BufferHandle buffer { get; private set; }
        public int2 depthSize { get; private set; }
        public int2 packedSize { get; private set; }
        public int firstMip => 1;
        public int mipCount => PackedMipCount;
        public int4[] mipBounds { get; } = new int4[PackedMipCount];

        /// <summary>World to GPU clip space for the source depth, before texture UV conversion.</summary>
        public Matrix4x4 viewProjection { get; private set; }

        /// <summary>
        /// True only after both reduction dispatches have been recorded during graph execution.
        /// Consumers must test this in their execution callback, not while recording the graph.
        /// </summary>
        public bool isReady { get; private set; }

        private RenderGraph m_Graph;
        private TextureHandle m_SourceDepth;
        private ComputeShader m_Shader;

        private sealed class FrameCache : ContextItem
        {
            public readonly List<CurrentFrameHZB> pyramids = new List<CurrentFrameHZB>();
            public int activeCount;

            public CurrentFrameHZB GetNextSlot()
            {
                if (activeCount == pyramids.Count)
                    pyramids.Add(new CurrentFrameHZB());
                return pyramids[activeCount];
            }

            public override void Reset()
            {
                for (int index = 0; index < activeCount; ++index)
                {
                    CurrentFrameHZB pyramid = pyramids[index];
                    pyramid.isReady = false;
                    pyramid.buffer = default;
                    pyramid.m_Graph = null;
                    pyramid.m_SourceDepth = default;
                    pyramid.m_Shader = null;
                }
                // ContextContainer resets only after graph execution. Retain each recording
                // slot and its bounds array, but never reuse an active slot for another source.
                activeCount = 0;
            }
        }

        private sealed class PassData
        {
            public CurrentFrameHZB pyramid;
            public ComputeShader shader;
            public TextureHandle sourceDepth;
            public int kernel;
            public int sourceMsaaSamples;
        }

        private static class ShaderIDs
        {
            public static readonly int sourceDepth = Shader.PropertyToID("_SrcDepth");
            public static readonly int destination = Shader.PropertyToID("_DstDepthBuffer");
            public static readonly int pyramidSize = Shader.PropertyToID("_DepthPyramidSize");
            public static readonly int mipCount = Shader.PropertyToID("_MipCount");
            public static readonly int samples = Shader.PropertyToID("_SrcMsaaSamples");
            public static readonly int[] bounds =
            {
                Shader.PropertyToID("_MipOffsetAndSize0"),
                Shader.PropertyToID("_MipOffsetAndSize1"),
                Shader.PropertyToID("_MipOffsetAndSize2"),
                Shader.PropertyToID("_MipOffsetAndSize3"),
                Shader.PropertyToID("_MipOffsetAndSize4")
            };
        }

        private CurrentFrameHZB() { }

        /// <summary>Declare the dependency before using <see cref="buffer"/> in a graph callback.</summary>
        public void DeclareRead(IBaseRenderGraphBuilder builder)
        {
            builder.UseBuffer(buffer, AccessFlags.Read);
        }

        /// <summary>
        /// Record reduction of an already scheduled current-camera depth prepass. Callers must
        /// request a depth prepass and supply its cameraDepthTexture, never an earlier frame.
        /// Repeated calls with the same frame context, graph and source reuse the same pyramid.
        /// </summary>
        public static bool TryRecord(RenderGraph graph, ContextContainer frameData,
            TextureHandle sourceDepth, ComputeShader shader, out CurrentFrameHZB pyramid)
        {
            pyramid = null;
            if (graph == null || frameData == null || shader == null ||
                !sourceDepth.IsValid() || !SystemInfo.supportsComputeShaders ||
                !IsSupportedGraphicsAPI() || !frameData.Contains<UniversalCameraData>())
                return false;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            Camera camera = cameraData.camera;
            if (camera == null || cameraData.renderType != CameraRenderType.Base ||
                (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView) ||
                !cameraData.isDefaultViewport || camera.allowDynamicResolution ||
                (cameraData.xr != null && cameraData.xr.enabled) ||
                cameraData.cameraTargetDescriptor.msaaSamples > 1 ||
                cameraData.antialiasing == AntialiasingMode.TemporalAntiAliasing ||
                cameraData.GetProjectionMatrix() != cameraData.GetProjectionMatrixNoJitter())
                return false;

            // A single-sample prepass cannot conservatively represent every MSAA color sample.
            // TAA is also excluded until native and indirect passes share one jitter convention.
            TextureDesc descriptor = sourceDepth.GetDescriptor(graph);
            if (descriptor.sizeMode != TextureSizeMode.Explicit ||
                descriptor.width <= 0 || descriptor.height <= 0 ||
                descriptor.dimension != TextureDimension.Tex2D || descriptor.slices != 1 ||
                descriptor.useDynamicScale || descriptor.useDynamicScaleExplicit)
                return false;

            int samples = descriptor.bindTextureMS ? (int)descriptor.msaaSamples : 1;
            if (samples != 1 && samples != 2 && samples != 4 && samples != 8)
                return false;

            if (!shader.HasKernel(KernelName))
                return false;
            int kernel = shader.FindKernel(KernelName);
            if (!shader.IsSupported(kernel))
                return false;

            FrameCache cache = frameData.GetOrCreate<FrameCache>();
            for (int index = 0; index < cache.activeCount; ++index)
            {
                CurrentFrameHZB cached = cache.pyramids[index];
                if (ReferenceEquals(cached.m_Graph, graph) &&
                    cached.m_SourceDepth.handle.Equals(sourceDepth.handle) &&
                    cached.m_SourceDepth.IsBuiltin() == sourceDepth.IsBuiltin() &&
                    cached.m_Shader == shader && cached.buffer.IsValid())
                {
                    pyramid = cached;
                    return true;
                }
            }

            int2 size = new int2(descriptor.width, descriptor.height);
            CurrentFrameHZB result = cache.GetNextSlot();
            int2 total = CalculateLayout(size, result.mipBounds);
            long elementCount = (long)total.x * total.y;
            if (elementCount <= 0 || elementCount > int.MaxValue / sizeof(float))
                return false;

            result.m_Graph = graph;
            result.m_SourceDepth = sourceDepth;
            result.m_Shader = shader;
            result.depthSize = size;
            result.packedSize = total;
            result.isReady = false;
            // Depth prepasses render into a texture; preserve the same jittered projection
            // passed to ScriptableRenderer.SetCameraMatrices, including GPU Y/reversed Z.
            result.viewProjection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true) *
                cameraData.GetViewMatrix();
            result.buffer = graph.CreateBuffer(new BufferDesc((int)elementCount, sizeof(float),
                GraphicsBuffer.Target.Structured) { name = "Current Frame HZB" });
            // Reserve the slot before recording the pass. Even if graph recording throws,
            // Reset will invalidate its handles and a later source cannot overwrite it.
            ++cache.activeCount;

            using (var builder = graph.AddComputePass<PassData>("Current Frame HZB", out var passData,
                s_ProfilingSampler))
            {
                passData.pyramid = result;
                passData.shader = shader;
                passData.sourceDepth = sourceDepth;
                passData.kernel = kernel;
                passData.sourceMsaaSamples = samples;
                builder.UseTexture(sourceDepth, AccessFlags.Read);
                // Dispatch two reads the mip written by dispatch one within this same pass.
                builder.UseBuffer(result.buffer, AccessFlags.ReadWrite);
                // This backport validates local ComputeShader keyword changes as global state.
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData data, ComputeGraphContext context) => Execute(data, context));
            }

            pyramid = result;
            return true;
        }

        internal static int2 CalculateLayout(int2 sourceSize, int4[] bounds)
        {
            int2 size = (sourceSize + 1) / 2;
            int2 offset = 0;
            int2 total = 0;
            for (int mip = 0; mip < PackedMipCount; ++mip)
            {
                bounds[mip] = new int4(offset, size);
                total = math.max(total, offset + size);
                if (mip == 0)
                    offset.y += size.y;
                else
                    offset.x += size.x;
                size = (size + 1) / 2;
            }
            return total;
        }

        private static bool IsSupportedGraphicsAPI()
        {
            // Match the tested renderer variants in DepthPyramidKernels.compute.
            GraphicsDeviceType api = SystemInfo.graphicsDeviceType;
            return api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Vulkan ||
                api == GraphicsDeviceType.Metal;
        }

        private static void Execute(PassData data, ComputeGraphContext context)
        {
            CurrentFrameHZB pyramid = data.pyramid;
            pyramid.isReady = false;
            if (data.shader == null)
                return;

            try
            {
                ComputeCommandBuffer cmd = context.cmd;
                ComputeShader shader = data.shader;
                var sourceKeyword = new LocalKeyword(shader, "USE_SRC");
                var bufferKeyword = new LocalKeyword(shader, "USE_BUFFER");
                var msaaKeyword = new LocalKeyword(shader, "SRC_IS_MSAA");
                cmd.SetComputeVectorParam(shader, ShaderIDs.pyramidSize,
                    new Vector4(pyramid.packedSize.x, pyramid.packedSize.y, 0, 0));

                for (int baseMip = 0; baseMip < PackedMipCount; baseMip += 4)
                {
                    bool useSource = baseMip == 0;
                    bool useMsaa = useSource && data.sourceMsaaSamples > 1;
                    cmd.SetKeyword(shader, sourceKeyword, useSource);
                    cmd.SetKeyword(shader, bufferKeyword, true);
                    cmd.SetKeyword(shader, msaaKeyword, useMsaa);
                    cmd.SetComputeIntParam(shader, ShaderIDs.samples, useMsaa ? data.sourceMsaaSamples : 1);
                    cmd.SetComputeIntParam(shader, ShaderIDs.mipCount, math.min(4, PackedMipCount - baseMip));
                    cmd.SetComputeBufferParam(shader, data.kernel, ShaderIDs.destination, pyramid.buffer);
                    if (useSource)
                        cmd.SetComputeTextureParam(shader, data.kernel, ShaderIDs.sourceDepth, data.sourceDepth);

                    int2 sourceSize = 0;
                    for (int parameter = 0; parameter < 5; ++parameter)
                    {
                        int absoluteMip = baseMip + parameter;
                        int4 bounds = absoluteMip == 0
                            ? new int4(0, 0, pyramid.depthSize.x, pyramid.depthSize.y)
                            : absoluteMip <= PackedMipCount ? pyramid.mipBounds[absoluteMip - 1] : default;
                        cmd.SetComputeVectorParam(shader, ShaderIDs.bounds[parameter],
                            new Vector4(bounds.x, bounds.y, bounds.z, bounds.w));
                        if (parameter == 0)
                            sourceSize = bounds.zw;
                    }
                    cmd.DispatchCompute(shader, data.kernel, (sourceSize.x + 15) / 16,
                        (sourceSize.y + 15) / 16, 1);
                }
                pyramid.isReady = true;
            }
            catch (Exception exception)
            {
                // Optional occlusion must fail open if recording a reduction fails. Graph still
                // owns the allocation; the consumer ignores it because isReady remains false.
                Debug.LogException(exception);
            }
        }
    }
}
