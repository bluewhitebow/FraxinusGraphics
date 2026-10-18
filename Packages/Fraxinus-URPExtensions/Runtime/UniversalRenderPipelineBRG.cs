using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Borrowed GPU view of the latest URP packed HZB. The buffer and mip layout are owned by
    /// the camera's HZB data and must not be disposed by consumers.
    /// </summary>
    public readonly struct HZBOcclusionGpuSnapshot
    {
        public GraphicsBuffer depthPyramid { get; }
        public int cameraInstanceID { get; }
        public int2 totalSize { get; }
        public int2 depthBufferSize { get; }
        public int2 topMipSize { get; }
        public int firstDepthMipIndex { get; }
        public int depthMips { get; }
        public FixedList128Bytes<int4> mipBounds { get; }
        public int generatedFrameCount { get; }
        public Matrix4x4 viewProjection { get; }
        public Matrix4x4 viewProjectionNoJitter { get; }
        public int msaaSamples { get; }
        public bool xr { get; }
        public bool defaultViewport { get; }

        public bool isValid
        {
            get
            {
                if (depthPyramid == null || cameraInstanceID == 0 ||
                    depthBufferSize.x <= 0 ||
                    depthBufferSize.y <= 0 || totalSize.x <= 0 || totalSize.y <= 0 ||
                    topMipSize.x <= 0 || topMipSize.y <= 0 || firstDepthMipIndex <= 0 ||
                    depthMips <= 0 || depthMips > 7 || depthMips > mipBounds.Length ||
                    generatedFrameCount < 0 ||
                    firstDepthMipIndex > 15 ||
                    firstDepthMipIndex + depthMips > 16)
                    return false;

                long requiredElementCount = (long)totalSize.x * totalSize.y;
                try
                {
                    if (!depthPyramid.IsValid() || depthPyramid.stride != sizeof(float) ||
                        requiredElementCount <= 0 || depthPyramid.count < requiredElementCount)
                        return false;
                }
                catch (Exception)
                {
                    // A resize or renderer teardown can invalidate the borrowed buffer between
                    // snapshot construction and validation. Treat that race as an occlusion miss.
                    return false;
                }

                for (int index = 0; index < depthMips; ++index)
                {
                    int4 bounds = mipBounds[index];
                    if (bounds.x < 0 || bounds.y < 0 || bounds.z <= 0 || bounds.w <= 0 ||
                        (long)bounds.x + bounds.z > totalSize.x ||
                        (long)bounds.y + bounds.w > totalSize.y)
                        return false;

                    long divisor = 1L << (firstDepthMipIndex + index);
                    int2 expectedSize = new int2(
                        (int)Math.Max(1L, ((long)depthBufferSize.x + divisor - 1) / divisor),
                        (int)Math.Max(1L, ((long)depthBufferSize.y + divisor - 1) / divisor));
                    if (bounds.z != expectedSize.x || bounds.w != expectedSize.y)
                        return false;

                    if (index == 0 && (bounds.x != 0 || bounds.y != 0 ||
                        bounds.z != topMipSize.x || bounds.w != topMipSize.y))
                        return false;
                }

                if (!IsFinite(viewProjection) || !IsFinite(viewProjectionNoJitter))
                    return false;

                return true;
            }
        }

        private static bool IsFinite(Matrix4x4 matrix)
        {
            for (int index = 0; index < 16; ++index)
            {
                float value = matrix[index];
                if (float.IsNaN(value) || float.IsInfinity(value))
                    return false;
            }

            return true;
        }

        internal HZBOcclusionGpuSnapshot(
            GraphicsBuffer depthPyramid,
            int cameraInstanceID,
            int2 totalSize,
            int2 depthBufferSize,
            int2 topMipSize,
            int firstDepthMipIndex,
            int depthMips,
            FixedList128Bytes<int4> mipBounds,
            int generatedFrameCount,
            Matrix4x4 viewProjection,
            Matrix4x4 viewProjectionNoJitter,
            int msaaSamples,
            bool xr,
            bool defaultViewport)
        {
            this.depthPyramid = depthPyramid;
            this.cameraInstanceID = cameraInstanceID;
            this.totalSize = totalSize;
            this.depthBufferSize = depthBufferSize;
            this.topMipSize = topMipSize;
            this.firstDepthMipIndex = firstDepthMipIndex;
            this.depthMips = depthMips;
            this.mipBounds = mipBounds;
            this.generatedFrameCount = generatedFrameCount;
            this.viewProjection = viewProjection;
            this.viewProjectionNoJitter = viewProjectionNoJitter;
            this.msaaSamples = msaaSamples;
            this.xr = xr;
            this.defaultViewport = defaultViewport;
        }
    }

    /// <summary>
    /// A non-owning snapshot of the latest CPU-readable hierarchical-Z data produced for a
    /// camera. Consumers may read the data while the snapshot is valid, but must not dispose the
    /// native array or retain it across camera-data or render-pipeline lifetime changes.
    /// </summary>
    public readonly struct HZBOcclusionSnapshot
    {
        /// <summary>The packed CPU depth pyramid. This array is owned by URP.</summary>
        public NativeArray<float> occluderDepthPyramid { get; }

        /// <summary>The offset and dimensions of each mip in the packed depth pyramid.</summary>
        public FixedList128Bytes<int4> occluderMipBounds { get; }

        /// <summary>The translation-free view-projection matrix used to build the pyramid.</summary>
        public float4x4 viewProjectionMatrix { get; }

        /// <summary>The world-space origin of the view that produced the pyramid.</summary>
        public float3 viewOriginWorldSpace { get; }

        /// <summary>The world-space facing direction of the view.</summary>
        public float3 facingDirectionWorldSpace { get; }

        /// <summary>The world-space radial X direction of the view.</summary>
        public float3 radialXDirectionWorldSpace { get; }

        /// <summary>The world-space radial Y direction of the view.</summary>
        public float3 radialYDirectionWorldSpace { get; }

        /// <summary>The top-mip dimensions and their reciprocals.</summary>
        public float4 depthSizeInOccluderPixels { get; }

        /// <summary>The packed-pyramid dimensions and their reciprocals.</summary>
        public float4 occluderDepthPyramidSize { get; }

        /// <summary>Whether the graphics device uses a reversed depth buffer.</summary>
        public bool isReversedZ { get; }

        /// <summary>The Unity frame in which the asynchronous readback was requested.</summary>
        public int lastRenderFrameCount { get; }

        /// <summary>Whether this snapshot contains readable depth data and at least one mip.</summary>
        public bool isValid =>
            occluderDepthPyramid.IsCreated && occluderMipBounds.Length > 0;

        internal HZBOcclusionSnapshot(
            NativeArray<float> occluderDepthPyramid,
            FixedList128Bytes<int4> occluderMipBounds,
            float4x4 viewProjectionMatrix,
            float3 viewOriginWorldSpace,
            float3 facingDirectionWorldSpace,
            float3 radialXDirectionWorldSpace,
            float3 radialYDirectionWorldSpace,
            float4 depthSizeInOccluderPixels,
            float4 occluderDepthPyramidSize,
            bool isReversedZ,
            int lastRenderFrameCount)
        {
            this.occluderDepthPyramid = occluderDepthPyramid;
            this.occluderMipBounds = occluderMipBounds;
            this.viewProjectionMatrix = viewProjectionMatrix;
            this.viewOriginWorldSpace = viewOriginWorldSpace;
            this.facingDirectionWorldSpace = facingDirectionWorldSpace;
            this.radialXDirectionWorldSpace = radialXDirectionWorldSpace;
            this.radialYDirectionWorldSpace = radialYDirectionWorldSpace;
            this.depthSizeInOccluderPixels = depthSizeInOccluderPixels;
            this.occluderDepthPyramidSize = occluderDepthPyramidSize;
            this.isReversedZ = isReversedZ;
            this.lastRenderFrameCount = lastRenderFrameCount;
        }
    }

    public sealed partial class UniversalRenderPipeline
    {
        private const int k_maxHZBOcclusionSnapshotAge = 8;
        private static int s_renderRequestDepth;

        /// <summary>
        /// Render requests temporarily replace the camera target and projection dimensions. HZB
        /// data is camera-owned, so provider registration and consumers must be disabled for the
        /// request instead of publishing that off-screen depth as the next game-camera frame.
        /// </summary>
        internal static bool IsRenderRequestActive => s_renderRequestDepth > 0;

        /// <summary>
        /// Tries to acquire the latest completed CPU hierarchical-Z snapshot for a camera.
        /// </summary>
        /// <param name="cameraInstanceID">The value returned by <see cref="Object.GetInstanceID"/> for the camera.</param>
        /// <param name="snapshot">The non-owning snapshot when one is available.</param>
        /// <returns><see langword="true"/> when a complete, recent readback is available.</returns>
        public static bool TryGetHZBOcclusionSnapshot(
            int cameraInstanceID, out HZBOcclusionSnapshot snapshot)
        {
            snapshot = default;
            if (IsRenderRequestActive || cameraInstanceID == 0 ||
                HZBOcclusionData.OcclusionData == null ||
                !HZBOcclusionData.OcclusionData.TryGetValue(
                    cameraInstanceID, out HZBOcclusionData hzbData) ||
                hzbData == null)
                return false;

            HZBOcclusionFrameData frameData = hzbData.GetLatestFrameData();
            if (frameData == null || !frameData.valid || !frameData.hzBuffer.IsCreated)
                return false;

            int frameAge = Time.frameCount - frameData.lastRenderFrameCount;
            if (frameAge < 0 || frameAge > k_maxHZBOcclusionSnapshotAge)
                return false;

            var mipBounds = new FixedList128Bytes<int4>();
            int depthMipCount = hzbData.depthMips;
            int4[] sourceMipBounds = hzbData.occluderMipBounds;
            if (depthMipCount <= 0 || depthMipCount > mipBounds.Capacity ||
                sourceMipBounds == null || sourceMipBounds.Length < depthMipCount ||
                hzbData.topMipSize.x <= 0 || hzbData.topMipSize.y <= 0 ||
                hzbData.totalSize.x <= 0 || hzbData.totalSize.y <= 0)
                return false;

            for (int index = 0; index < depthMipCount; ++index)
                mipBounds.Add(sourceMipBounds[index]);

            HZBOcclusionSnapshot candidate = new HZBOcclusionSnapshot(
                frameData.hzBuffer,
                mipBounds,
                ToMathematicsFloat4x4(frameData.viewProjMatrix),
                ToMathematicsFloat3(frameData.viewOriginWorldSpace),
                ToMathematicsFloat3(frameData.facingDirWorldSpace),
                ToMathematicsFloat3(frameData.radialXDirWorldSpace),
                ToMathematicsFloat3(frameData.radialYDirWorldSpace),
                new float4(
                    hzbData.topMipSize.x,
                    hzbData.topMipSize.y,
                    1.0f / hzbData.topMipSize.x,
                    1.0f / hzbData.topMipSize.y),
                new float4(
                    hzbData.totalSize.x,
                    hzbData.totalSize.y,
                    1.0f / hzbData.totalSize.x,
                    1.0f / hzbData.totalSize.y),
                SystemInfo.usesReversedZBuffer,
                frameData.lastRenderFrameCount);

            if (!candidate.isValid)
                return false;

            snapshot = candidate;
            return true;
        }

        /// <summary>
        /// Tries to acquire the GPU packed HZB produced by the URP RenderHZB feature. No
        /// allocation or readback is performed; the returned GraphicsBuffer is borrowed.
        /// </summary>
        public static bool TryGetHZBOcclusionGpuSnapshot(
            int cameraInstanceID, out HZBOcclusionGpuSnapshot snapshot)
        {
            snapshot = default;
            if (IsRenderRequestActive || cameraInstanceID == 0 ||
                HZBOcclusionData.OcclusionData == null ||
                !HZBOcclusionData.OcclusionData.TryGetValue(
                    cameraInstanceID, out HZBOcclusionData hzbData) ||
                hzbData == null)
                return false;

            hzbData.CommitPendingGpuSnapshot();
            if (hzbData.gpuDepthPyramid == null ||
                hzbData.gpuDepthPyramidFrameCount < 0 || hzbData.depthMips <= 0)
                return false;

            var mipBounds = new FixedList128Bytes<int4>();
            if (hzbData.depthMips > mipBounds.Capacity ||
                hzbData.occluderMipBounds == null ||
                hzbData.occluderMipBounds.Length < hzbData.depthMips)
                return false;

            for (int index = 0; index < hzbData.depthMips; ++index)
                mipBounds.Add(hzbData.occluderMipBounds[index]);

            HZBOcclusionGpuSnapshot candidate = new HZBOcclusionGpuSnapshot(
                hzbData.gpuDepthPyramid,
                cameraInstanceID,
                hzbData.totalSize,
                hzbData.depthBufferSize,
                hzbData.topMipSize,
                hzbData.firstDepthMipIndex,
                hzbData.depthMips,
                mipBounds,
                hzbData.gpuDepthPyramidFrameCount,
                hzbData.gpuViewProjectionMatrix,
                hzbData.gpuViewProjectionNoJitterMatrix,
                hzbData.gpuMsaaSamples,
                hzbData.gpuXr,
                hzbData.gpuIsDefaultViewport);
            if (!candidate.isValid)
                return false;

            snapshot = candidate;
            return true;
        }

        /// <summary>
        /// Returns whether the URP HZB feature claimed this camera for the current frame.
        /// A claimed provider may still have no completed snapshot; consumers must fail open
        /// in that case instead of creating a second pyramid.
        /// </summary>
        internal static bool IsHZBOcclusionConfigured(int cameraInstanceID)
        {
            if (cameraInstanceID == 0 || HZBOcclusionData.OcclusionData == null ||
                !HZBOcclusionData.OcclusionData.TryGetValue(
                    cameraInstanceID, out HZBOcclusionData hzbData) || hzbData == null)
                return false;

            return hzbData.IsConfiguredForCurrentFrame(cameraInstanceID);
        }

        internal static bool TryDeclareHZBOcclusionGpuRead(
            int cameraInstanceID, GraphicsBuffer expectedBuffer,
            RenderGraph renderGraph, IBaseRenderGraphBuilder builder)
        {
            if (cameraInstanceID == 0 || renderGraph == null || builder == null ||
                HZBOcclusionData.OcclusionData == null ||
                !HZBOcclusionData.OcclusionData.TryGetValue(
                    cameraInstanceID, out HZBOcclusionData hzbData) || hzbData == null ||
                !ReferenceEquals(hzbData.gpuDepthPyramid, expectedBuffer))
                return false;

            return hzbData.TryDeclareRenderGraphRead(renderGraph, builder);
        }

        private static float3 ToMathematicsFloat3(Vector3 value)
        {
            return new float3(value.x, value.y, value.z);
        }

        private static float4x4 ToMathematicsFloat4x4(Matrix4x4 value)
        {
            Vector4 c0 = value.GetColumn(0);
            Vector4 c1 = value.GetColumn(1);
            Vector4 c2 = value.GetColumn(2);
            Vector4 c3 = value.GetColumn(3);
            return new float4x4(
                new float4(c0.x, c0.y, c0.z, c0.w),
                new float4(c1.x, c1.y, c1.z, c1.w),
                new float4(c2.x, c2.y, c2.z, c2.w),
                new float4(c3.x, c3.y, c3.z, c3.w));
        }

        /// <summary>
        /// Extension point for renderers that need to enqueue camera-local passes only after URP
        /// has completed culling and built the final <see cref="RenderingData"/>. Keeping this
        /// hook in URP avoids a reverse assembly reference to an optional renderer package.
        /// </summary>
        internal delegate void CameraPassesReadyHandler(
            ScriptableRenderContext context,
            ScriptableRenderer renderer,
            in ScriptableCullingParameters cullingParameters,
            ref RenderingData renderingData);

        // A ready callback may veto a nested render request before URP calls
        // RenderSingleCameraInternal. This is required for extensions whose per-camera passes
        // share ScriptableRenderer's camera-scoped frame data and mutable queue, so merely
        // ignoring the later camera-pass callback is too late.
        internal delegate bool StandaloneCameraReadyHandler(
            ScriptableRenderContext context, Camera camera);

        internal delegate bool CameraRenderRequestReadyHandler(
            ScriptableRenderContext context, Camera camera);

        internal delegate void StandaloneCameraFinishedHandler(
            ScriptableRenderContext context, Camera camera);

        // GetInvocationList() allocates a fresh Delegate[] on every call. These
        // notifications sit on the camera/render-request hot path and the
        // optional FraxinusBRG renderer normally remains subscribed for the
        // lifetime of the pipeline. Rebuild the expanded invocation list when
        // subscriptions change, so even the first rendered camera only reads
        // an existing array. This preserves per-subscriber exception isolation
        // without a managed allocation in the camera hot path.
        static CameraPassesReadyHandler s_CameraPassesReady;
        static Delegate[] s_CameraPassesReadySubscribers;
        internal static event CameraPassesReadyHandler cameraPassesReady
        {
            add
            {
                s_CameraPassesReady += value;
                s_CameraPassesReadySubscribers =
                    s_CameraPassesReady?.GetInvocationList();
            }
            remove
            {
                s_CameraPassesReady -= value;
                s_CameraPassesReadySubscribers =
                    s_CameraPassesReady?.GetInvocationList();
            }
        }

        static StandaloneCameraReadyHandler s_StandaloneCameraReady;
        static Delegate[] s_StandaloneCameraReadySubscribers;
        internal static event StandaloneCameraReadyHandler standaloneCameraReady
        {
            add
            {
                s_StandaloneCameraReady += value;
                s_StandaloneCameraReadySubscribers =
                    s_StandaloneCameraReady?.GetInvocationList();
            }
            remove
            {
                s_StandaloneCameraReady -= value;
                s_StandaloneCameraReadySubscribers =
                    s_StandaloneCameraReady?.GetInvocationList();
            }
        }

        static CameraRenderRequestReadyHandler s_CameraRenderRequestReady;
        static Delegate[] s_CameraRenderRequestReadySubscribers;
        internal static event CameraRenderRequestReadyHandler cameraRenderRequestReady
        {
            add
            {
                s_CameraRenderRequestReady += value;
                s_CameraRenderRequestReadySubscribers =
                    s_CameraRenderRequestReady?.GetInvocationList();
            }
            remove
            {
                s_CameraRenderRequestReady -= value;
                s_CameraRenderRequestReadySubscribers =
                    s_CameraRenderRequestReady?.GetInvocationList();
            }
        }

        static StandaloneCameraFinishedHandler s_StandaloneCameraFinished;
        static Delegate[] s_StandaloneCameraFinishedSubscribers;
        internal static event StandaloneCameraFinishedHandler standaloneCameraFinished
        {
            add
            {
                s_StandaloneCameraFinished += value;
                s_StandaloneCameraFinishedSubscribers =
                    s_StandaloneCameraFinished?.GetInvocationList();
            }
            remove
            {
                s_StandaloneCameraFinished -= value;
                s_StandaloneCameraFinishedSubscribers =
                    s_StandaloneCameraFinished?.GetInvocationList();
            }
        }

        private static void NotifyCameraPassesReady(
            ScriptableRenderContext context,
            ScriptableRenderer renderer,
            in ScriptableCullingParameters cullingParameters,
            ref RenderingData renderingData)
        {
            Delegate[] subscribers = s_CameraPassesReadySubscribers;
            if (subscribers == null)
                return;
            if (subscribers.Length == 1)
            {
                try
                {
                    ((CameraPassesReadyHandler)subscribers[0])(
                        context, renderer, in cullingParameters, ref renderingData);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
                return;
            }

            for (int index = 0; index < subscribers.Length; ++index)
            {
                try
                {
                    ((CameraPassesReadyHandler)subscribers[index])(
                        context, renderer, in cullingParameters, ref renderingData);
                }
                catch (Exception exception)
                {
                    // An optional renderer must not prevent URP from rendering the camera or
                    // suppress another extension subscribed to the same validated-camera hook.
                    Debug.LogException(exception);
                }
            }
        }

        private static bool NotifyStandaloneCameraReady(
            ScriptableRenderContext context, Camera camera)
        {
            Delegate[] subscribers = s_StandaloneCameraReadySubscribers;
            if (subscribers == null)
                return true;

            bool allowRender = true;
            for (int index = 0; index < subscribers.Length; ++index)
            {
                try
                {
                    if (!((StandaloneCameraReadyHandler)subscribers[index])(context, camera))
                        allowRender = false;
                }
                catch (Exception exception)
                {
                    // A broken optional callback must not suppress an otherwise valid camera.
                    Debug.LogException(exception);
                }
            }
            return allowRender;
        }

        private static bool NotifyCameraRenderRequestReady(
            ScriptableRenderContext context, Camera camera)
        {
            Delegate[] subscribers = s_CameraRenderRequestReadySubscribers;
            if (subscribers == null)
                return true;

            bool allowRender = true;
            for (int index = 0; index < subscribers.Length; ++index)
            {
                try
                {
                    if (!((CameraRenderRequestReadyHandler)subscribers[index])(
                            context, camera))
                        allowRender = false;
                }
                catch (Exception exception)
                {
                    // Keep render-request behavior fail-open for optional extensions. A callback
                    // exception is logged, while a deliberate false return remains a veto.
                    Debug.LogException(exception);
                }
            }
            return allowRender;
        }

        private static void NotifyStandaloneCameraFinished(
            ScriptableRenderContext context, Camera camera)
        {
            Delegate[] subscribers = s_StandaloneCameraFinishedSubscribers;
            if (subscribers == null)
                return;
            for (int index = 0; index < subscribers.Length; ++index)
            {
                try
                {
                    ((StandaloneCameraFinishedHandler)subscribers[index])(context, camera);
                }
                catch (Exception exception)
                {
                    // Completion is bookkeeping for optional extensions. Never let it change
                    // the result of the standalone camera that has already been rendered.
                    Debug.LogException(exception);
                }
            }
        }
    }
}
