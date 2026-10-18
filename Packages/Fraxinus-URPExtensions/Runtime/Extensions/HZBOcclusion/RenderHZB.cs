using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.Universal
{
    public class RenderHZB : ScriptableRendererFeature
    {
        [SerializeField] [HideInInspector] [Reload("Runtime/Extensions/HZBOcclusion/DepthPyramidKernels.compute")]
        private ComputeShader m_Shader;

        private RenderHZBPass m_RenderPass = null;

        public override void Create()
        {
#if UNITY_EDITOR
            ResourceReloader.TryReloadAllNullIn(this, UniversalRenderPipelineAsset.packagePath);
#endif
            // Create the pass...
            if (m_RenderPass == null)
                m_RenderPass = new RenderHZBPass();
        }

        public override void OnCameraPreCull(
            ScriptableRenderer renderer, in CameraData cameraData)
        {
            if (renderer == null || m_RenderPass == null || m_Shader == null ||
                cameraData.camera == null || !IsSupportedCamera(renderer, in cameraData) ||
                UniversalRenderPipeline.IsRenderRequestActive ||
                !cameraData.camera.TryGetComponent<UniversalAdditionalCameraData>(
                    out UniversalAdditionalCameraData additionalCameraData))
                return;

            // Prepare the backing allocation before URP notifies camera-pass extensions. The
            // Fraxinus callback runs before AddRenderPasses, so Setup publishes a provider claim
            // only after all resources and readback slots are ready; otherwise the consumer
            // remains fail-open for this invocation.
            additionalCameraData.hzbOcclusionData?.Setup(
                cameraData.camera.GetInstanceID(), new int2(
                    cameraData.cameraTargetDescriptor.width,
                    cameraData.cameraTargetDescriptor.height));
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            if (camera == null)
                return;

            int2 depthBufferSize = new int2(
                renderingData.cameraData.cameraTargetDescriptor.width,
                renderingData.cameraData.cameraTargetDescriptor.height);
            if (!camera.TryGetComponent<UniversalAdditionalCameraData>(
                    out var additionalCameraData))
                return;

            if (renderer == null || m_RenderPass == null || m_Shader == null ||
                !IsSupportedCamera(renderer, in renderingData.cameraData) ||
                UniversalRenderPipeline.IsRenderRequestActive)
            {
                additionalCameraData.hzbOcclusionData?.RevokeCurrentFrameClaim(
                    camera.GetInstanceID(), depthBufferSize);
                return;
            }

            bool shouldAdd = m_RenderPass.Setup(ref renderer, ref m_Shader, ref renderingData,
                additionalCameraData.hzbOcclusionData);
            if (shouldAdd)
                renderer.EnqueuePass(m_RenderPass);
            else
                additionalCameraData.hzbOcclusionData?.RevokeCurrentFrameClaim(
                    camera.GetInstanceID(), depthBufferSize);
        }

        private static bool IsSupportedCamera(
            ScriptableRenderer renderer, in CameraData cameraData)
        {
            Camera camera = cameraData.camera;
            // DepthPyramidKernels.compute uses an explicit #pragma only_renderers list. Do not
            // claim this camera when the reduction kernel cannot execute; Fraxinus remains
            // frustum/LOD-only on that device.
            if (!(renderer is UniversalRenderer) ||
                !SystemInfo.supportsComputeShaders ||
                 !SystemInfo.supportsGraphicsFence ||
                (!SystemInfo.supportsAsyncCompute &&
                    !SystemInfo.supportsAsyncGPUReadback) ||
                !IsSupportedShaderRenderer())
                return false;

            return cameraData.renderType == CameraRenderType.Base && camera != null &&
                camera.cameraType != CameraType.Preview &&
                camera.cameraType != CameraType.Reflection &&
                (camera.targetTexture == null ||
                 camera.targetTexture.format != RenderTextureFormat.Depth) &&
                !camera.allowDynamicResolution &&
                cameraData.isDefaultViewport && !cameraData.xr.enabled &&
                cameraData.cameraTargetDescriptor.dimension == TextureDimension.Tex2D &&
                cameraData.cameraTargetDescriptor.volumeDepth == 1 &&
                IsSupportedMsaaSampleCount(
                    Mathf.Max(cameraData.cameraTargetDescriptor.msaaSamples, 1)) &&
                cameraData.cameraTargetDescriptor.width > 0 &&
                cameraData.cameraTargetDescriptor.height > 0;
        }

        private static bool IsSupportedShaderRenderer()
        {
            GraphicsDeviceType graphicsDeviceType = SystemInfo.graphicsDeviceType;
            if (graphicsDeviceType == GraphicsDeviceType.Direct3D12 ||
                graphicsDeviceType == GraphicsDeviceType.OpenGLCore ||
                graphicsDeviceType == GraphicsDeviceType.OpenGLES3)
                return false;
#if UNITY_2023_2_OR_NEWER
            if (graphicsDeviceType == GraphicsDeviceType.WebGPU)
                return false;
#endif
            return true;
        }

        private static bool IsSupportedMsaaSampleCount(int sampleCount)
        {
            return sampleCount == 1 || sampleCount == 2 || sampleCount == 4 || sampleCount == 8;
        }

        internal class RenderHZBPass : ScriptableRenderPass
        {
            private class RenderGraphPassData
            {
                internal TextureHandle activeDepthTexture;
                internal GraphicsBuffer depthPyramid;
                internal int sourceMsaaSamples;
                internal ComputeShader shader;
                internal HZBOcclusionData occlusionData;
                internal CameraData cameraData;
            }

            private ComputeShader m_Shader;
            private HZBOcclusionData m_OcclusionData;
            private ProfilingSampler m_ProfilingSampler = new ProfilingSampler(nameof(RenderHZBPass));

            public bool Setup(ref ScriptableRenderer renderer, ref ComputeShader shader,
                ref RenderingData renderingData, HZBOcclusionData hzbOcclusionData)
            {
                if (renderer == null || shader == null || hzbOcclusionData == null)
                    return false;

                m_Shader = shader;
                m_OcclusionData = hzbOcclusionData;
                if (!m_OcclusionData.Setup(renderingData.cameraData.camera.GetInstanceID(), new int2(
                        renderingData.cameraData.cameraTargetDescriptor.width,
                        renderingData.cameraData.cameraTargetDescriptor.height)))
                    return false;

                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 1);
                // The pass samples the final active depth attachment directly.  Requiring the
                // camera depth texture here would schedule an unnecessary prepass/copy and could
                // leave the texture containing only the pre-opaque depth snapshot.  The
                // intermediate-target requirement below makes the active attachment sampleable
                // while RenderGraph orders this pass after all opaque depth writers.
                requiresIntermediateTexture = true;
                return true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph,
                ContextContainer frameData)
            {
                if (m_Shader == null || m_OcclusionData == null)
                    return;

                UniversalResourceData resourceData;
                RenderingData renderingData;
                try
                {
                    resourceData = frameData.Get<UniversalResourceData>();
                    renderingData = new RenderingData(frameData);
                }
                catch
                {
                    // Missing frame data means this optional provider cannot participate in
                    // this recording. Keep the camera graph alive and fail open.
                    m_OcclusionData.MarkProviderFailure();
                    return;
                }

                TextureHandle activeDepthTexture = resourceData.activeDepthTexture;
                if (!activeDepthTexture.IsValid())
                {
                    MarkProviderFailure(m_OcclusionData, in renderingData.cameraData);
                    return;
                }

                // The camera descriptor may still report the color target's MSAA count even
                // after URP has resolved the depth attachment into a single-sample resource. Read
                // the actual graph resource descriptor so the compute shader never binds a
                // Texture2DMS view to a resolved Texture2D resource.
                TextureDesc activeDepthDescriptor;
                try
                {
                    activeDepthDescriptor = activeDepthTexture.GetDescriptor(renderGraph);
                }
                catch (System.Exception)
                {
                    // Imported/backbuffer handles can become invalid while a camera is being
                    // resized or torn down.  A descriptor query is part of pass setup, so turn
                    // this frame into a conservative occlusion miss instead of aborting the
                    // whole RenderGraph recording.
                    MarkProviderFailure(m_OcclusionData, in renderingData.cameraData);
                    return;
                }

                // The active depth handle can differ from the camera descriptor when URP
                // switches to an intermediate target, resolves MSAA, or imports a backbuffer.
                // HZB allocation is keyed by the descriptor captured during Setup, so dispatch
                // only when the graph resource still has the exact expected extent and shape.
                int2 expectedDepthSize = m_OcclusionData.depthBufferSize;
                if (activeDepthDescriptor.width != expectedDepthSize.x ||
                    activeDepthDescriptor.height != expectedDepthSize.y ||
                    activeDepthDescriptor.dimension != TextureDimension.Tex2D ||
                    activeDepthDescriptor.slices != 1)
                {
                    MarkProviderFailure(m_OcclusionData, in renderingData.cameraData);
                    return;
                }

                bool sourceIsMsaa = activeDepthDescriptor.bindTextureMS &&
                    activeDepthDescriptor.msaaSamples != MSAASamples.None;
                int sourceMsaaSamples = sourceIsMsaa
                    ? (int)activeDepthDescriptor.msaaSamples
                    : 1;
                if (!IsSupportedMsaaSampleCount(sourceMsaaSamples))
                {
                    // Do not bind a Texture2DMS view with an unknown sample count. This should
                    // already be rejected by the pre-cull capability check on normal cameras;
                    // keep the graph pass fail-open for a resize/backbuffer race as well.
                    MarkProviderFailure(m_OcclusionData, in renderingData.cameraData);
                    return;
                }

                // Import before creating the pass. If the external allocation was retired or
                // invalidated between Setup and graph recording, there must be no executable HZB
                // pass at all. Revoke this invocation's claim and retry on the next frame.
                if (!m_OcclusionData.TryGetRenderGraphDepthPyramid(
                        renderGraph, out BufferHandle depthPyramid))
                {
                    MarkProviderFailure(m_OcclusionData, in renderingData.cameraData);
                    return;
                }

                IUnsafeRenderGraphBuilder builder;
                RenderGraphPassData passData;
                try
                {
                    builder = renderGraph.AddUnsafePass<RenderGraphPassData>(
                        nameof(RenderHZBPass), out passData, m_ProfilingSampler);
                }
                catch
                {
                    // RenderGraph can reject pass creation while a camera is being rebuilt.
                    // HZB is optional, so release this provider claim and keep the frame
                    // fail-open instead of failing the entire camera graph.
                    MarkProviderFailure(m_OcclusionData, in renderingData.cameraData);
                    return;
                }

                using (builder)
                {
                    passData.activeDepthTexture = activeDepthTexture;
                    passData.sourceMsaaSamples = sourceMsaaSamples;
                    // Capture the exact allocation used by the imported handle. If a later
                    // camera resize retires this buffer before the graph callback executes, the
                    // callback must become a no-op instead of rebinding a newly allocated buffer
                    // against the old RenderGraph declaration.
                    passData.depthPyramid = m_OcclusionData.gpuDepthPyramid;
                    passData.shader = m_Shader;
                    passData.occlusionData = m_OcclusionData;
                    passData.cameraData = renderingData.cameraData;

                    // Use the pass-owned data directly.  The global camera dictionary is
                    // populated only after the first reduction executes, so looking it up here
                    // would omit the write dependency on a camera's first frame (and after a
                    // resize, when Setup has intentionally cleared the old registration).
                    bool declared = false;
                    try
                    {
                        // Keep both declarations in the same guarded block. A validity check
                        // can reject the active depth handle during a resize/teardown race;
                        // in that case install a no-op render function below so the graph stays
                        // structurally valid and occlusion remains fail-open.
                        builder.UseTexture(activeDepthTexture, AccessFlags.Read);
                        // Mark the external allocation before the native builder call. If
                        // UseBuffer partially registers the handle and then throws, the
                        // retirement path must still keep the buffer fenced.
                        passData.occlusionData.MarkRenderGraphDepthPyramidUse();
                        builder.UseBuffer(depthPyramid, AccessFlags.ReadWrite);
                        declared = true;
                    }
                    catch (System.Exception)
                    {
                        // A declaration can still fail if RenderGraph rejects an external
                        // handle while a camera is being torn down. Keep the pass cullable and
                        // provide a no-op function so the graph remains structurally valid.
                    }

                    if (!declared)
                    {
                        // Import/declaration can fail if the backing buffer was retired between
                        // pass setup and graph recording. Do not run a compute dispatch without
                        // a matching RenderGraph write declaration; this frame remains fail-open
                        // and the URP provider retries on the next frame.
                        MarkProviderFailure(passData.occlusionData, in passData.cameraData);
                        builder.SetRenderFunc(static (RenderGraphPassData _, UnsafeGraphContext __) => { });
                        return;
                    }

                    // The async readback is an external side effect and must not be culled
                    // just because the pass does not produce another RenderGraph resource.
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((RenderGraphPassData data, UnsafeGraphContext context) =>
                    {
                        // Setup/import and execution are separated by graph recording. A camera
                        // resize or teardown can replace the persistent allocation in between;
                        // never dispatch against a new buffer through the old imported handle.
                        bool sameAllocation = ReferenceEquals(
                            data.occlusionData.gpuDepthPyramid, data.depthPyramid);
                        try
                        {
                            sameAllocation &= data.depthPyramid != null &&
                                data.depthPyramid.IsValid();
                        }
                        catch (System.Exception)
                        {
                            sameAllocation = false;
                        }

                        if (!sameAllocation)
                        {
                            MarkProviderFailure(data.occlusionData, in data.cameraData);
                            return;
                        }

                        try
                        {
                            bool generated = data.occlusionData.RenderAndRequestReadbackAsync(
                                CommandBufferHelpers.GetNativeCommandBuffer(context.cmd),
                                data.shader,
                                0,
                                data.activeDepthTexture,
                                data.depthPyramid,
                                data.sourceMsaaSamples,
                                ref data.cameraData);
                            if (!generated)
                                MarkProviderFailure(data.occlusionData, in data.cameraData);
                        }
                        catch (System.Exception exception)
                        {
                            // A failed execution must not leave the previous GPU snapshot
                            // publishable on the next camera. HZB is an optional provider, so
                            // report the failure and let the camera graph continue fail-open.
                            MarkProviderFailure(data.occlusionData, in data.cameraData);
                            Debug.LogException(exception);
                            return;
                        }
                    });
                }
            }

            private static void MarkProviderFailure(
                HZBOcclusionData occlusionData, in CameraData cameraData)
            {
                if (occlusionData == null)
                    return;

                Camera camera = cameraData.camera;
                int cameraInstanceID = camera != null ? camera.GetInstanceID() : 0;
                occlusionData.MarkProviderFailure(cameraInstanceID, new int2(
                    cameraData.cameraTargetDescriptor.width,
                    cameraData.cameraTargetDescriptor.height));
            }

        }
    }
}
