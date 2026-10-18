using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Assertions;
using UnityEngine.Rendering.RenderGraphModule;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.Rendering.Universal
{
    public class HZBOcclusionFrameData : IDisposable
    {
        private enum ReadbackState
        {
            Idle,
            Recording,
            Recorded,
            Uncertain,
        }

        private bool m_Disposed;
        private NativeArray<float> m_HZBuffer;
        private ReadbackState _readbackState;
        // Normal reuse happens only after the previous callback has completed. Cache that
        // callback until recovery releases a slot without receiving its callback, then advance
        // the generation so a delayed completion cannot affect a new request.
        private uint _readbackGeneration;
        private Action<AsyncGPUReadbackRequest> m_ReadbackCallback;
        
        public NativeArray<float> hzBuffer => m_HZBuffer;
        public int lastRenderFrameCount { get; private set; } = -1;
        public bool onFlight => _readbackState != ReadbackState.Idle;
        public bool valid => !m_Disposed && !onFlight && lastRenderFrameCount != -1;
        public Matrix4x4 viewProjMatrix { get; private set; }
        public Vector3 viewOriginWorldSpace { get; private set; }
        public Vector3 facingDirWorldSpace { get; private set; }
        public Vector3 radialXDirWorldSpace { get; private set; }
        public Vector3 radialYDirWorldSpace { get; private set; }

        public HZBOcclusionFrameData(int2 textureSize)
        {
            m_HZBuffer = new NativeArray<float>(textureSize.x * textureSize.y, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_ReadbackCallback = CreateReadbackCallback();
        }

        private Action<AsyncGPUReadbackRequest> CreateReadbackCallback()
        {
            uint generation = _readbackGeneration;
            return request => OnRequestCallback(generation, request);
        }

        bool UpdateFrameInfo(ref CameraData cameraData)
        {
#if UNITY_EDITOR
            // pause would lead to readback stuck.
            if (EditorApplication.isPaused)
                return false;
#endif

            // A caller must acquire an idle slot before recording a new request. Refuse direct
            // re-entry as well; replacing the destination while a previous native request is
            // still in flight would let two GPU writes race on the same NativeArray.
            if (m_Disposed || onFlight || !m_HZBuffer.IsCreated)
                return false;
            
            lastRenderFrameCount = Time.frameCount;
            var viewMatrix = cameraData.GetViewMatrix();
            var viewMatrixInverse = viewMatrix.inverse;
            var projMatrix = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
            viewMatrix.SetColumn(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            viewProjMatrix = projMatrix * viewMatrix;   // without translation
            viewOriginWorldSpace = viewMatrixInverse.GetColumn(3);
            radialXDirWorldSpace = ((Vector3)viewMatrixInverse.GetColumn(0)).normalized;
            radialYDirWorldSpace = ((Vector3)viewMatrixInverse.GetColumn(1)).normalized;
            facingDirWorldSpace = ((Vector3)viewMatrixInverse.GetColumn(2)).normalized;
            m_ReadbackCallback ??= CreateReadbackCallback();
            _readbackState = ReadbackState.Recording;
            return true;
        }
        
        public void RequestAsyncReadback(CommandBuffer cmd, GraphicsBuffer graphicsBuffer, ref CameraData cameraData)
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                return;
            if (!UpdateFrameInfo(ref cameraData)) return;
            try
            {
                cmd.RequestAsyncReadbackIntoNativeArray(
                    ref m_HZBuffer,
                    graphicsBuffer,
                    m_ReadbackCallback);
                // A defensive check handles an implementation that invokes the callback
                // synchronously for an already-completed/error request.
                if (_readbackState == ReadbackState.Recording)
                    _readbackState = ReadbackState.Recorded;
            }
            catch
            {
                // Unity does not guarantee that a throwing native recording call did not
                // partially enqueue a write to the destination array. Keep this slot in flight
                // until a callback proves otherwise; reusing or disposing it risks a GPU write
                // to an invalid NativeArray. The caller still returns the slot to the ring.
                if (_readbackState != ReadbackState.Idle)
                    _readbackState = ReadbackState.Uncertain;
                lastRenderFrameCount = -1;
                throw;
            }
        }

        // Keep the pre-GraphicsBuffer overload for package users that still own a ComputeBuffer.
        // URP's HZB backing resource itself is a GraphicsBuffer so RenderGraph can import it.
        public void RequestAsyncReadback(CommandBuffer cmd, ComputeBuffer computeBuffer, ref CameraData cameraData)
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                return;
            if (!UpdateFrameInfo(ref cameraData)) return;
            try
            {
                cmd.RequestAsyncReadbackIntoNativeArray(
                    ref m_HZBuffer,
                    computeBuffer,
                    m_ReadbackCallback);
                if (_readbackState == ReadbackState.Recording)
                    _readbackState = ReadbackState.Recorded;
            }
            catch
            {
                if (_readbackState != ReadbackState.Idle)
                    _readbackState = ReadbackState.Uncertain;
                lastRenderFrameCount = -1;
                throw;
            }
        }
        
        public void RequestAsyncReadback(CommandBuffer cmd, RenderTexture renderTexture, ref CameraData cameraData)
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                return;
            if (!UpdateFrameInfo(ref cameraData)) return;
            try
            {
                cmd.RequestAsyncReadbackIntoNativeArray(
                    ref m_HZBuffer,
                    renderTexture,
                    m_ReadbackCallback);
                if (_readbackState == ReadbackState.Recording)
                    _readbackState = ReadbackState.Recorded;
            }
            catch
            {
                if (_readbackState != ReadbackState.Idle)
                    _readbackState = ReadbackState.Uncertain;
                lastRenderFrameCount = -1;
                throw;
            }
        }
        
        private void OnRequestCallback(uint generation, AsyncGPUReadbackRequest request)
        {
            // A recovery wait may have completed the native request before Unity dispatches its
            // managed callback. The generation check prevents that late callback from changing
            // a slot that has already been safely reused for another request.
            if (generation != _readbackGeneration)
                return;

            _readbackState = ReadbackState.Idle;
            if (m_Disposed)
            {
                // A frame can be retired while its readback is still queued.  Keep the
                // completion state observable by the owning pyramid so that the source
                // GraphicsBuffer is not released until this callback has run.
                if (m_HZBuffer.IsCreated)
                    m_HZBuffer.Dispose();
                HZBOcclusionData.PollRetiredPyramidResources();
                return;
            }

            if (!request.done || request.hasError)
            {
                if (!m_HZBuffer.IsCreated)
                    return;
                lastRenderFrameCount = -1;
            }
            HZBOcclusionData.PollRetiredPyramidResources();
        }

        internal bool NeedsReadbackRecovery(bool includeRecorded)
        {
            if (_readbackState == ReadbackState.Uncertain)
                return true;

            return includeRecorded && _readbackState != ReadbackState.Idle;
        }

        /// <summary>
        /// Resolves a request after AsyncGPUReadback.WaitAllRequests has returned. Waiting is
        /// required before changing the state: a throwing command-buffer recording call may
        /// have handed the NativeArray to native code even though no callback was delivered.
        /// </summary>
        internal void ResolveReadbackAfterWait()
        {
            if (_readbackState == ReadbackState.Idle)
                return;

            // The request either failed to reach the GPU or completed without a callback that
            // could validate its contents. Never publish this slot as a valid CPU snapshot after
            // recovery; the next request must overwrite it first.
            lastRenderFrameCount = -1;
            _readbackState = ReadbackState.Idle;
            unchecked
            {
                ++_readbackGeneration;
            }
            m_ReadbackCallback = null;

            if (m_Disposed && m_HZBuffer.IsCreated)
                m_HZBuffer.Dispose();
        }

        public void SetDirty()
        {
            lastRenderFrameCount = -1;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (m_Disposed)
                return;
            
            m_Disposed = true;
            // if we are on flight, we need to wait for the readback to complete and dispose it in callback function.
            if (disposing && !onFlight)
                m_HZBuffer.Dispose();
        }
    }
    
    public class HZBOcclusionData : IDisposable
    {
        // Some android devices read back texture too slow on render thread, so we use compute buffer to read back texture.
        public static readonly bool k_UseComputeBuffer = true;
        public static Dictionary<int, HZBOcclusionData> OcclusionData = new Dictionary<int, HZBOcclusionData>();
        
        public readonly static int k_MaxReadbackBufferCount = Application.isMobilePlatform ? 4 : 3;
        public const int k_MaxOccluderMips = 7;
        private const int k_MaxDepthPixel = 1 << k_MaxOccluderMips;

        private int m_CameraInstanceID = 0;
        private RTHandle m_OccluderDepthPyramid;
        private GraphicsBuffer _depthPyramidBuffer;
        private int2 m_DepthBufferSize;
        private Matrix4x4 _gpuViewProjectionMatrix;
        private Matrix4x4 _gpuViewProjectionNoJitterMatrix;
        private int _gpuMsaaSamples;
        private bool _gpuXr;
        private bool _gpuIsDefaultViewport;
        private Matrix4x4 _pendingGpuViewProjectionMatrix;
        private Matrix4x4 _pendingGpuViewProjectionNoJitterMatrix;
        private int _pendingGpuMsaaSamples;
        private bool _pendingGpuXr;
        private bool _pendingGpuIsDefaultViewport;
        private int _pendingGpuDepthPyramidFrameCount = -1;
        private bool _hasPendingGpuSnapshot;
        private GraphicsBuffer _pendingGpuSnapshotBuffer;
        private GraphicsFence? _pendingGpuGenerationFence;
        private bool _pendingGpuGenerationSubmitted;
        private int _configurationFrameCount = -1;
        // RenderHZB performs a resource preflight from OnCameraPreCull so the provider can claim
        // the camera before Fraxinus receives its camera-pass callback. Remember a failed attempt
        // for the same camera/size during this frame so all consumers make one deterministic
        // fail-open decision for that invocation.
        private int _setupAttemptFrame = -1;
        private int _setupAttemptCamera;
        private int2 _setupAttemptSize;
        private bool _setupAttemptSucceeded;
        private RenderGraph _renderGraph;
        private BufferHandle _renderGraphDepthPyramid;
        private bool _hasRenderGraphDepthPyramid;
        private RetiredPyramidResource _renderGraphPyramidResource;

        // RenderGraph imports and asynchronous readbacks can outlive the managed camera data
        // object. Keep detached pyramid resources in a process-wide retirement list until both
        // GPU completion proof and every readback that references the old allocation have
        // completed.
        private sealed class RetiredPyramidResource
        {
            private GraphicsBuffer _buffer;
            private RTHandle _texture;
            private readonly List<HZBOcclusionFrameData> _readbackFrames =
                new List<HZBOcclusionFrameData>(k_MaxReadbackBufferCount);
            private GraphicsFence _fence;
            private bool _hasFence;
            private bool _fencePendingSubmission;
            private bool _gpuUseRecorded;
            private bool _graphUsePending;
            private bool _graphFencePendingSubmission;
            // A fence is emitted into the camera command buffer before Submit. If that
            // submission fails, the fence must never be cleared by a later camera's global
            // success notification. Keep the resource quarantined until a subsequent graph
            // recording emits a replacement fence.
            private bool _graphFenceSubmissionInvalidated;
            private bool _retirementProofPending;
            private bool _retirementProofSucceeded;
            private AsyncGPUReadbackRequest _retirementProofRequest;
            private bool _released;
            private int _retiredFrame = -1;

            private const int k_retirementSentinelSize = sizeof(float);

            internal RetiredPyramidResource(GraphicsBuffer buffer, RTHandle texture)
            {
                _buffer = buffer;
                _texture = texture;
            }

            internal bool hasResource => _buffer != null || _texture != null;

            internal void AddReadbackFrame(HZBOcclusionFrameData frameData)
            {
                if (frameData != null)
                    _readbackFrames.Add(frameData);
            }

            internal bool NeedsReadbackRecovery(bool includeRecorded)
            {
                for (int index = 0; index < _readbackFrames.Count; ++index)
                {
                    HZBOcclusionFrameData frameData = _readbackFrames[index];
                    if (frameData != null && frameData.NeedsReadbackRecovery(includeRecorded))
                        return true;
                }

                return false;
            }

            internal void CollectReadbackRecoveryFrames(
                List<HZBOcclusionFrameData> frames, bool includeRecorded)
            {
                if (frames == null)
                    return;

                for (int index = 0; index < _readbackFrames.Count; ++index)
                {
                    HZBOcclusionFrameData frameData = _readbackFrames[index];
                    if (frameData != null && frameData.NeedsReadbackRecovery(includeRecorded))
                        frames.Add(frameData);
                }
            }

            internal void MarkGpuUse()
            {
                InvalidateRetirementProofForNewUse();
                // A fence from an earlier generation cannot cover this new command. Clear it
                // before recording the replacement so a failed RecordFence cannot accidentally
                // authorize release using the stale completion point.
                _hasFence = false;
                _fence = default;
                _fencePendingSubmission = false;
                _graphFenceSubmissionInvalidated = false;
                _gpuUseRecorded = true;
            }

            internal void MarkGraphUse()
            {
                InvalidateRetirementProofForNewUse();
                // RenderGraph may bind the allocation before its execution callback runs. The
                // previous fence is therefore no longer sufficient once this declaration is
                // staged; CompleteGraphUse must install a fence for this use.
                _hasFence = false;
                _fence = default;
                _fencePendingSubmission = false;
                _graphFenceSubmissionInvalidated = false;
                _gpuUseRecorded = true;
                _graphUsePending = true;
            }

            internal void Retire(int frame)
            {
                _retiredFrame = frame;
                // A retirement record must never inherit a completion ticket from an earlier
                // use of the same managed wrapper. The first successful Submit after this point
                // will enqueue a fresh sentinel (on graphics-only devices).
                _retirementProofPending = false;
                _retirementProofSucceeded = false;
            }

            internal bool RecordFence(CommandBuffer commands)
            {
                return RecordFence(commands, out _);
            }

            internal bool RecordFence(
                CommandBuffer commands, out GraphicsFence recordedFence)
            {
                recordedFence = default;
                if (_released || !_gpuUseRecorded || commands == null ||
                    !SystemInfo.supportsGraphicsFence)
                    return false;

                try
                {
                    // Do not clear an already valid fence before the replacement has been
                    // created. If the outer command buffer rejects fence creation, the fence
                    // emitted by the HZB pass is still the best available completion proof.
                    GraphicsFence fence = commands.CreateGraphicsFence(
                        GetFenceType(),
                        SynchronisationStageFlags.AllGPUOperations);
                    recordedFence = fence;
                    _fence = fence;
                    _hasFence = true;
                    _fencePendingSubmission = true;
                    // A newly recorded fence supersedes any fence from a failed submission,
                    // including a direct (non-RenderGraph) recording where graph-use tracking is
                    // not active.
                    _graphFenceSubmissionInvalidated = false;
                    // Every fence is recorded into a command buffer that still needs an
                    // enclosing context submission. Track direct callers as well as the
                    // RenderGraph declaration path; otherwise a direct RecordFence could be
                    // mistaken for a completed proof after a failed Submit.
                    _graphFencePendingSubmission = true;
                    return true;
                }
                catch (Exception)
                {
                    // Keep the resource alive if the graphics API/device cannot create a fence.
                    return false;
                }
            }

            internal void CompleteGraphUse(CommandBuffer commands)
            {
                if (!_graphUsePending)
                    return;

                if (RecordFence(commands))
                {
                    _graphUsePending = false;
                    _graphFencePendingSubmission = true;
                }
            }

            internal bool IsReady(int currentFrame)
            {
                PollRetirementProof();
                if (_released || _graphUsePending || _graphFencePendingSubmission ||
                    _fencePendingSubmission)
                    return false;

                // A resource that was only allocated (and never bound) has no GPU lifetime to
                // fence. It can be released as soon as any queued readbacks are complete. Every
                // bound resource requires an explicit, passed fence.
                if (!_gpuUseRecorded)
                    return ReadbacksCompleted();

                if (currentFrame <= _retiredFrame || !ReadbacksCompleted())
                    return false;

                // AsyncQueueSynchronisation fences cannot be queried on graphics-only devices
                // (notably D3D11). A CPU fence is not a portable substitute: Unity 2022 only
                // guarantees the AsyncQueue fence creation path. On those devices the readback
                // request is queued after the successful Submit and therefore acts as the
                // retirement proof for every prior use of this allocation.
                if (!SystemInfo.supportsAsyncCompute)
                {
                    // Direct users may not participate in the RenderGraph submitted callback.
                    // Once the one-frame retirement guard has elapsed, requesting the sentinel
                    // here is still ordered after the prior context submission and prevents a
                    // valid graphics-only resource from being quarantined forever.
                    if (!_retirementProofPending && !_retirementProofSucceeded)
                        RequestRetirementProof();
                    if (_retirementProofPending || !_retirementProofSucceeded)
                        return false;

                    return true;
                }

                // A readback sentinel is also a valid fallback when fence creation failed on an
                // otherwise async-capable device. Prefer the native fence when available, but
                // never release solely because a non-queryable/invalid fence object exists.
                if (_hasFence)
                {
                    bool fencePassed;
                    if (TryFencePassed(_fence, out fencePassed))
                    {
                        if (!fencePassed)
                            return false;
                    }
                    else
                    {
                        // Some drivers expose GraphicsFence creation but throw when the
                        // completion state is queried. Drop that unusable proof and request a
                        // post-submit readback sentinel instead; otherwise this resource would
                        // remain quarantined forever even though a safe fallback is available.
                        _hasFence = false;
                        _fence = default;
                        RequestRetirementProof();
                    }
                }

                if (!_hasFence && !_retirementProofSucceeded)
                {
                    // Fence creation can fail even on an async-compute device (for example
                    // during a transient device loss, or when a graph submission was rejected
                    // after the resource had already been bound).  Do not quarantine the
                    // allocation forever in that case: once all graph/fence submission markers
                    // above have cleared, an AsyncGPUReadback request is ordered after the last
                    // submitted use and is a valid completion proof.  RequestRetirementProof is
                    // idempotent while the ticket is pending and remains conservative when the
                    // platform cannot enqueue a readback.
                    if (!_retirementProofPending)
                        RequestRetirementProof();
                    if (!_retirementProofSucceeded)
                        return false;
                }

                return true;
            }

            internal void MarkGraphRecordingSubmitted()
            {
                if (_graphFencePendingSubmission && !_graphFenceSubmissionInvalidated)
                    _graphFencePendingSubmission = false;
                if (_fencePendingSubmission && !_graphFenceSubmissionInvalidated)
                    _fencePendingSubmission = false;

                // If fence recording itself failed, a successful Submit still gives the
                // post-submit readback sentinel an ordering point. Do not leave the graph-use
                // marker latched forever on devices where that sentinel is available.
                if (_graphUsePending && SystemInfo.supportsAsyncGPUReadback)
                    _graphUsePending = false;

                // Request this only after the command buffer carrying all graph uses has been
                // accepted by ScriptableRenderContext.Submit. Issuing it while recording would
                // leave a window in which the old buffer could still be in use by the GPU.
                RequestRetirementProof();
            }

            internal void MarkGraphRecordingFailed()
            {
                bool hadGraphUse = _graphFencePendingSubmission || _graphUsePending ||
                    _fencePendingSubmission;
                if (!hadGraphUse)
                    return;

                _graphFenceSubmissionInvalidated = true;
                // A fence emitted into a command buffer that was not submitted is not a
                // completion proof. Drop both graph markers and the fence itself; if this
                // allocation is later retired, its post-submit readback/fence proof can be
                // requested again after the last known submitted work.
                _graphFencePendingSubmission = false;
                _graphUsePending = false;
                _fencePendingSubmission = false;
                _hasFence = false;
                _fence = default;

                // A proof is normally requested only after Submit succeeds. Invalidate a
                // pending ticket defensively anyway so a late callback from a partially failed
                // native request cannot authorize release of this resource.
                if (_retirementProofPending || _retirementProofSucceeded)
                {
                    _retirementProofPending = false;
                    _retirementProofSucceeded = false;
                }
            }

            internal void Release()
            {
                if (_released)
                    return;

                _released = true;
                _retirementProofPending = false;
                try
                {
                    _buffer?.Dispose();
                }
                finally
                {
                    _buffer = null;
                    try
                    {
                        _texture?.Release();
                    }
                    finally
                    {
                        _texture = null;
                        _readbackFrames.Clear();
                    }
                }
            }

            private bool ReadbacksCompleted()
            {
                for (int index = 0; index < _readbackFrames.Count; ++index)
                {
                    HZBOcclusionFrameData frameData = _readbackFrames[index];
                    if (frameData != null && frameData.onFlight)
                        return false;
                }
                return true;
            }

            internal void RequestRetirementProof()
            {
                PollRetirementProof();
                if (_released || _retiredFrame < 0 || !_gpuUseRecorded ||
                    _retirementProofPending || _retirementProofSucceeded ||
                    _graphUsePending || _graphFencePendingSubmission ||
                    _fencePendingSubmission ||
                    !SystemInfo.supportsAsyncGPUReadback)
                    return;

                // On async-compute devices a queryable fence is the cheaper proof. Use a
                // sentinel only when that fence cannot be queried, or when fence creation was
                // unavailable and a readback is the only safe completion signal.
                if (SystemInfo.supportsAsyncCompute && _hasFence)
                    return;

                GraphicsBuffer buffer = _buffer;
                RTHandle texture = _texture;
                if (buffer == null && texture == null)
                    return;

                // A retired allocation has no future legitimate consumers. MarkGraphUse and
                // MarkGpuUse invalidate this ticket defensively if an already-recorded graph
                // still reaches it; the next successful Submit then requests a newer proof.
                _retirementProofSucceeded = false;
                _retirementProofPending = true;
                try
                {
                    if (buffer != null)
                    {
                        if (!IsGraphicsBufferValid(buffer))
                        {
                            _retirementProofPending = false;
                            return;
                        }

                        _retirementProofRequest = AsyncGPUReadback.Request(
                            buffer,
                            k_retirementSentinelSize,
                            0);
                    }
                    else
                    {
                        RenderTexture renderTexture = texture?.rt;
                        if (renderTexture == null || !renderTexture.IsCreated())
                        {
                            _retirementProofPending = false;
                            return;
                        }

                        // The texture path is retained for platforms/configurations that do not
                        // use the packed GraphicsBuffer. Read one texel from mip zero; the
                        // request's ordering, not its value, is the completion sentinel.
                        _retirementProofRequest = AsyncGPUReadback.Request(
                            renderTexture,
                            0,
                            0,
                            1,
                            0,
                            1,
                            0,
                            1);
                    }
                }
                catch (Exception)
                {
                    // A native request may have been partially enqueued before throwing. Drop
                    // this ticket; the next successful Submit may safely try again.
                    _retirementProofPending = false;
                }
            }

            private void PollRetirementProof()
            {
                if (!_retirementProofPending)
                    return;

                try
                {
                    if (!_retirementProofRequest.done)
                        return;

                    _retirementProofPending = false;
                    _retirementProofSucceeded = !_retirementProofRequest.hasError;
                }
                catch (Exception)
                {
                    _retirementProofPending = false;
                    _retirementProofSucceeded = false;
                }
            }

            private void InvalidateRetirementProofForNewUse()
            {
                if (_retiredFrame < 0 ||
                    (!_retirementProofPending && !_retirementProofSucceeded))
                    return;

                _retirementProofPending = false;
                _retirementProofSucceeded = false;
            }

            private static bool TryFencePassed(GraphicsFence fence, out bool passed)
            {
                passed = false;
                try
                {
                    passed = fence.passed;
                    return true;
                }
                catch (Exception)
                {
                    // An invalid/lost fence is not completion proof.
                    return false;
                }
            }

            private static GraphicsFenceType GetFenceType()
            {
                // Unity's RenderGraph and command-buffer implementations support this fence
                // type on graphics-only devices, but GraphicsFence.passed is intentionally not
                // queryable there. D3D11 retirement uses the readback sentinel instead.
                return GraphicsFenceType.AsyncQueueSynchronisation;
            }

        }

        private static readonly List<RetiredPyramidResource> s_retiredPyramidResources =
            new List<RetiredPyramidResource>(4);
        private static bool s_drainingRetiredPyramidResources;
        private static bool s_readbackRecoveryPending;

        private RetiredPyramidResource _activePyramidResource;

        public int2 totalSize { get; private set; }
        public int2 depthBufferSize => m_DepthBufferSize;
        public int2 topMipSize { get; private set; }
        public int firstDepthMipIndex { get; private set; }
        public int depthMips { get; private set; }
        
        public int4[] occluderMipBounds { get; private set; } = new int4[k_MaxOccluderMips];

        /// <summary>
        /// Borrowed GPU packed depth pyramid generated by the latest HZB pass. Callers must not
        /// dispose this buffer.
        /// </summary>
        public GraphicsBuffer gpuDepthPyramid => _depthPyramidBuffer;

        /// <summary>The frame in which <see cref="gpuDepthPyramid"/> was generated.</summary>
        public int gpuDepthPyramidFrameCount { get; private set; } = -1;

        public Matrix4x4 gpuViewProjectionMatrix => _gpuViewProjectionMatrix;
        public Matrix4x4 gpuViewProjectionNoJitterMatrix => _gpuViewProjectionNoJitterMatrix;
        public int gpuMsaaSamples => _gpuMsaaSamples;
        public bool gpuXr => _gpuXr;
        public bool gpuIsDefaultViewport => _gpuIsDefaultViewport;

        private Queue<HZBOcclusionFrameData> m_RingBuffer = new Queue<HZBOcclusionFrameData>();
        
        internal HZBOcclusionData()
        {
        }

        public HZBOcclusionFrameData GetLatestFrameData()
        {
            HZBOcclusionFrameData result = null;
            foreach (var frameData in m_RingBuffer)
            {
                if (!frameData.valid)
                    continue;
                
                if (result == null || result.lastRenderFrameCount < frameData.lastRenderFrameCount)
                    result = frameData;
            }
            
            return result;
        }
        
        internal void RegisterCamera(int cameraInstanceID)
        {
            if (cameraInstanceID == 0)
                return;

            if (m_CameraInstanceID != 0 && m_CameraInstanceID != cameraInstanceID &&
                OcclusionData.TryGetValue(m_CameraInstanceID, out HZBOcclusionData oldData) &&
                ReferenceEquals(oldData, this))
            {
                OcclusionData.Remove(m_CameraInstanceID);
            }

            m_CameraInstanceID = cameraInstanceID;
            _configurationFrameCount = Time.frameCount;
            OcclusionData[cameraInstanceID] = this;
        }

        private void UnregisterCamera()
        {
            if (m_CameraInstanceID != 0 &&
                OcclusionData.TryGetValue(m_CameraInstanceID, out HZBOcclusionData data) &&
                ReferenceEquals(data, this))
            {
                OcclusionData.Remove(m_CameraInstanceID);
            }

            m_CameraInstanceID = 0;
            _configurationFrameCount = -1;
        }

        internal void RevokeCurrentFrameClaim(int cameraInstanceID, int2 depthBufferSize)
        {
            if (cameraInstanceID == 0 || m_CameraInstanceID != cameraInstanceID ||
                _configurationFrameCount != Time.frameCount)
                return;

            // Keep the allocation and completed history alive for the next frame. Only remove
            // this frame's provider claim, and make a same-frame Setup retry fail closed so a
            // late AddRenderPasses callback cannot reclaim a camera that already selected a
            // different/fail-open path.
            UnregisterCamera();
            _setupAttemptFrame = Time.frameCount;
            _setupAttemptCamera = cameraInstanceID;
            _setupAttemptSize = depthBufferSize;
            _setupAttemptSucceeded = false;
        }

        internal void RevokeCurrentFrameClaim()
        {
            RevokeCurrentFrameClaim(m_CameraInstanceID, m_DepthBufferSize);
        }

        /// <summary>
        /// Revokes this frame's URP provider claim after a RenderGraph HZB failure. The backing
        /// allocation is intentionally retained so the next frame can retry without a realloc.
        /// The global registration remains alive until this graph receives its submit/failure
        /// notification because it also owns the external-buffer lifetime tracking.
        /// </summary>
        internal void MarkProviderFailure(int cameraInstanceID, int2 depthBufferSize)
        {
            InvalidateGpuSnapshot();
            if (cameraInstanceID == 0)
                return;

            // Keep the dictionary entry until this camera's graph reaches its submit/failure
            // notification. It doubles as the lifetime registry for imported external buffers.
            // Clearing only the frame stamp revokes provider ownership without losing the fence
            // callback. Setup can immediately reuse the retained registration next frame.
            if (m_CameraInstanceID == cameraInstanceID)
                _configurationFrameCount = -1;

            // Setup is called from both OnCameraPreCull and AddRenderPasses. Make the failed
            // decision sticky for the current frame; the next frame retries normally.
            _setupAttemptFrame = Time.frameCount;
            _setupAttemptCamera = cameraInstanceID;
            _setupAttemptSize = depthBufferSize;
            _setupAttemptSucceeded = false;
        }

        internal void MarkProviderFailure()
        {
            MarkProviderFailure(m_CameraInstanceID, m_DepthBufferSize);
        }

        internal bool IsConfiguredForCurrentFrame(int cameraInstanceID)
        {
            if (cameraInstanceID == 0 || m_CameraInstanceID != cameraInstanceID ||
                _configurationFrameCount != Time.frameCount ||
                !OcclusionData.TryGetValue(cameraInstanceID, out HZBOcclusionData data) ||
                !ReferenceEquals(data, this) || m_RingBuffer.Count == 0)
                return false;

            return k_UseComputeBuffer
                ? IsGraphicsBufferValid(_depthPyramidBuffer)
                : IsTextureHandleValid(m_OccluderDepthPyramid);
        }

        internal bool Setup(int cameraInstanceID, int2 depthBufferSize)
        {
            // A pre-cull failure is intentionally sticky for the matching input until the end
            // of this frame. Fraxinus has already observed the provider state and selected its
            // fail-open path; reclaiming the camera later in the same invocation would make the
            // published provider/debug state non-deterministic.
            if (_setupAttemptFrame == Time.frameCount &&
                _setupAttemptCamera == cameraInstanceID &&
                math.all(_setupAttemptSize == depthBufferSize) &&
                !_setupAttemptSucceeded)
                return false;

            _setupAttemptFrame = Time.frameCount;
            _setupAttemptCamera = cameraInstanceID;
            _setupAttemptSize = depthBufferSize;
            _setupAttemptSucceeded = false;

            CommitPendingGpuSnapshot();
            PollRetiredPyramidResources();
            if (depthBufferSize.x <= 0 || depthBufferSize.y <= 0)
            {
                ClearResources();
                UnregisterCamera();
                return false;
            }

            bool hasBackingResource = k_UseComputeBuffer
                ? IsGraphicsBufferValid(_depthPyramidBuffer)
                : IsTextureHandleValid(m_OccluderDepthPyramid);
            if (math.all(m_DepthBufferSize == depthBufferSize) &&
                hasBackingResource && m_RingBuffer.Count > 0)
            {
                EnsureActivePyramidResource();
                RegisterCamera(cameraInstanceID);
                _setupAttemptSucceeded = true;
                return true;
            }

            // Keep the completed snapshot alive while the next frame is being recorded when the
            // backing allocation is unchanged.  Fraxinus records its build before this frame's
            // HZB reduction, so clearing the frame stamp here would make every consumer miss the
            // previous-frame pyramid and permanently fall back to frustum-only culling.  A size
            // or allocation change takes the ClearResources path below, which invalidates the
            // snapshot before retiring the old buffer.
            // Remove the previous claim before tearing down the old allocation. If native
            // allocation fails below, the current frame remains unclaimed and consumers fail
            // open without sampling the retired buffer.
            UnregisterCamera();
            ClearResources();
            m_DepthBufferSize = depthBufferSize;
            // Mip zero is the source depth texture. The reduction writes the first packed
            // destination at absolute mip one, so never expose an unwritten packed mip zero.
            firstDepthMipIndex = 1;
            int firstMipPixelSize = 1 << firstDepthMipIndex;
            topMipSize = (depthBufferSize + (firstMipPixelSize - 1) * 1) / firstMipPixelSize;
            depthMips = k_MaxOccluderMips;
            while (firstDepthMipIndex < 4 && (topMipSize.x >= k_MaxDepthPixel || topMipSize.y >= k_MaxDepthPixel))
            {
                firstDepthMipIndex++;
                int occluderPixelSize = 1 << firstDepthMipIndex;
                topMipSize = (depthBufferSize + (occluderPixelSize - 1) * 1) / occluderPixelSize;
            }
            
            totalSize = 0;
            int2 mipOffset = 0;
            int2 mipSize = topMipSize;

            for (int mipIndex = 0; mipIndex < depthMips; ++mipIndex)
            {
                occluderMipBounds[mipIndex] = new int4(mipOffset.x, mipOffset.y, mipSize.x, mipSize.y);
                totalSize = new int2(Mathf.Max(totalSize.x, mipOffset.x + mipSize.x), Mathf.Max(totalSize.y, mipOffset.y + mipSize.y));
                if (mipIndex == 0)
                {
                    mipOffset.x = 0;
                    mipOffset.y += mipSize.y;
                }
                else
                {
                    mipOffset.x += mipSize.x;
                }
                mipSize.x = (mipSize.x + 1) / 2;
                mipSize.y = (mipSize.y + 1) / 2;
            }

            try
            {
                if (k_UseComputeBuffer)
                {
                    long bufferElementCount = (long)totalSize.x * totalSize.y;
                    if (bufferElementCount <= 0 || bufferElementCount > int.MaxValue)
                    {
                        ClearResources();
                        UnregisterCamera();
                        return false;
                    }

                    _depthPyramidBuffer = new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured, (int)bufferElementCount, sizeof(float));
                    _activePyramidResource = new RetiredPyramidResource(
                        _depthPyramidBuffer, null);
                }
                else
                {
                    RenderTextureDescriptor textureDesc =
                        new RenderTextureDescriptor(totalSize.x, totalSize.y, RenderTextureFormat.RFloat, 0, 1)
                        {
                            enableRandomWrite = true,
                        };

                    RenderingUtils.ReAllocateHandleIfNeeded(ref m_OccluderDepthPyramid, textureDesc,
                        FilterMode.Point, TextureWrapMode.Clamp);
                    _activePyramidResource = new RetiredPyramidResource(
                        null, m_OccluderDepthPyramid);
                }

                for (int i = 0; i < k_MaxReadbackBufferCount; ++i)
                    m_RingBuffer.Enqueue(new HZBOcclusionFrameData(totalSize));
            }
            catch (Exception)
            {
                // Setup is a capability probe as well as an allocator. Keep a partially
                // allocated resource quarantined, fail open for this frame, and never leave a
                // stale camera claim behind.
                ClearResources();
                UnregisterCamera();
                return false;
            }

            // Claim ownership only after the backing allocation and every readback slot exist.
            // A failed setup therefore leaves IsHZBOcclusionConfigured false so consumers fail
            // open for this camera invocation.
            RegisterCamera(cameraInstanceID);
            _setupAttemptSucceeded = true;
            return true;
        }

        private static class ShaderIDs
        {
            public static readonly int _SrcDepth = Shader.PropertyToID(nameof(_SrcDepth));
            public static readonly int _DstDepthBuffer = Shader.PropertyToID(nameof(_DstDepthBuffer));
            public static readonly int _DstDepthTexture = Shader.PropertyToID(nameof(_DstDepthTexture));
            internal static readonly int srcMsaaSamplesId = Shader.PropertyToID("_SrcMsaaSamples");
            public static readonly int _MipCount = Shader.PropertyToID(nameof(_MipCount)); 
            public static readonly int _DepthPyramidSize = Shader.PropertyToID(nameof(_DepthPyramidSize));
            public static readonly int[] _MipOffsetAndSize = new int[5]; 

            static ShaderIDs()
            {
                for (int i = 0; i < _MipOffsetAndSize.Length; ++i)
                    _MipOffsetAndSize[i] = Shader.PropertyToID($"_MipOffsetAndSize{i}");
            }
        }

        private HZBOcclusionFrameData AcquireReadbackFrame()
        {
            // GPU reduction is independent of CPU readback. Walk the small ring so one slow
            // request (or a quarantined native exception) cannot block a different free slot.
            int frameCount = m_RingBuffer.Count;
            for (int index = 0; index < frameCount; ++index)
            {
                HZBOcclusionFrameData frameData = m_RingBuffer.Dequeue();
                if (frameData != null && !frameData.onFlight && frameData.hzBuffer.IsCreated)
                    return frameData;

                m_RingBuffer.Enqueue(frameData);
            }

            return null;
        }

        private bool NeedsReadbackRecovery(bool includeRecorded)
        {
            foreach (HZBOcclusionFrameData frameData in m_RingBuffer)
            {
                if (frameData != null && frameData.NeedsReadbackRecovery(includeRecorded))
                    return true;
            }

            return false;
        }

        private void CollectReadbackRecoveryFrames(
            List<HZBOcclusionFrameData> frames, bool includeRecorded)
        {
            if (frames == null)
                return;

            foreach (HZBOcclusionFrameData frameData in m_RingBuffer)
            {
                if (frameData != null && frameData.NeedsReadbackRecovery(includeRecorded))
                    frames.Add(frameData);
            }
        }

        internal bool RenderAndRequestReadbackAsync(
            CommandBuffer cmd, ComputeShader shader, int kernalIndex,
            RenderTargetIdentifier cameraDepthTexture,
            GraphicsBuffer expectedDepthPyramid,
            int sourceMsaaSamples,
            ref CameraData cameraData)
        {
            bool resourceMismatch = k_UseComputeBuffer
                ? !IsGraphicsBufferValid(_depthPyramidBuffer) ||
                    !ReferenceEquals(_depthPyramidBuffer, expectedDepthPyramid)
                : !IsTextureHandleValid(m_OccluderDepthPyramid);
            if (sourceMsaaSamples < 1 || sourceMsaaSamples > 8 ||
                (sourceMsaaSamples > 1 && (sourceMsaaSamples & (sourceMsaaSamples - 1)) != 0) ||
                cmd == null || shader == null || cameraData.camera == null ||
                totalSize.x <= 0 || totalSize.y <= 0 || depthMips <= 0 || resourceMismatch)
            {
                if (resourceMismatch)
                    InvalidateGpuSnapshot();
                return false;
            }

            EnsureActivePyramidResource();
            RetiredPyramidResource activeResource = _activePyramidResource;
            if (activeResource == null || !activeResource.hasResource)
                return false;

            // GPU publication is independent of the legacy CPU-readable snapshot. Devices that
            // expose no asynchronous readback must still be able to use the packed pyramid for
            // GPU Indirect; simply omit the optional readback on those devices.
            HZBOcclusionFrameData frameData = SystemInfo.supportsAsyncGPUReadback
                ? AcquireReadbackFrame()
                : null;
            try
            {
                // The resource is now referenced by one or more commands in this pass.  Its
                // retirement record will require a real fence before it can be released.
                activeResource.MarkGpuUse();
                var bufferKeyword = new LocalKeyword(shader, "USE_BUFFER");
                var srcKeyword = new LocalKeyword(shader, "USE_SRC");
                var srcIsMsaaKeyword = new LocalKeyword(shader, "SRC_IS_MSAA");

                int mipCount = firstDepthMipIndex + depthMips;
                cmd.SetComputeVectorParam(shader, ShaderIDs._DepthPyramidSize, new Vector4(totalSize.x, totalSize.y, 0, 0));
                for (int mipIndexBase = 0; mipIndexBase < mipCount - 1; mipIndexBase += 4)
                {
                    if (k_UseComputeBuffer)
                        cmd.SetComputeBufferParam(shader, kernalIndex, ShaderIDs._DstDepthBuffer, _depthPyramidBuffer);
                    else
                        cmd.SetComputeTextureParam(shader, kernalIndex, ShaderIDs._DstDepthTexture, m_OccluderDepthPyramid);

                    bool useSrc = (mipIndexBase == 0);
                    cmd.SetKeyword(shader, srcKeyword, useSrc);
                    bool sourceIsMsaa = useSrc && sourceMsaaSamples > 1;
                    // Explicitly set both source keywords on every dispatch because this shared
                    // shader instance can be used by another camera in the same command buffer.
                    cmd.SetKeyword(shader, srcIsMsaaKeyword, sourceIsMsaa);
                    cmd.SetKeyword(shader, bufferKeyword, k_UseComputeBuffer);
                    cmd.SetComputeIntParam(shader, ShaderIDs.srcMsaaSamplesId,
                        sourceIsMsaa ? sourceMsaaSamples : 1);
                    if (useSrc)
                        cmd.SetComputeTextureParam(shader, kernalIndex, ShaderIDs._SrcDepth, cameraDepthTexture);

                    cmd.SetComputeIntParam(shader, ShaderIDs._MipCount, Mathf.Min(mipCount - 1 - mipIndexBase, 4));

                    int2 srcSize = 0;
                    for (int i = 0; i < 5; ++i)
                    {
                        int2 offset = 0;
                        int2 size = 0;
                        int mipIndex = mipIndexBase + i;
                        if (mipIndex == 0)
                        {
                            size = m_DepthBufferSize;
                        }
                        else
                        {
                            int occMipIndex = mipIndex - firstDepthMipIndex;
                            if (0 <= occMipIndex && occMipIndex < depthMips)
                            {
                                offset = occluderMipBounds[occMipIndex].xy;
                                size = occluderMipBounds[occMipIndex].zw;
                            }
                        }
                        if (i == 0)
                            srcSize = size;

                        cmd.SetComputeVectorParam(shader, ShaderIDs._MipOffsetAndSize[i], new Vector4(offset.x, offset.y, size.x, size.y));
                    }

                    cmd.DispatchCompute(shader, kernalIndex, (srcSize.x + 15) / 16, (srcSize.y + 15) / 16, 1);
                }

                if (frameData != null)
                {
                    try
                    {
                        if (k_UseComputeBuffer)
                            frameData.RequestAsyncReadback(
                                cmd, _depthPyramidBuffer, ref cameraData);
                        else
                            frameData.RequestAsyncReadback(
                                cmd, m_OccluderDepthPyramid, ref cameraData);
                    }
                    catch (Exception exception)
                    {
                        // CPU readback is optional for GPU consumers. The frame slot keeps its
                        // conservative in-flight state, while the reduction can still produce a
                        // submitted GPU snapshot below.
                        Debug.LogException(exception);
                    }
                }

                Assert.IsTrue(m_CameraInstanceID == 0 || m_CameraInstanceID == cameraData.camera.GetInstanceID());
                RegisterCamera(cameraData.camera.GetInstanceID());
                // The reduction reads the camera depth texture, whose projection uses the render
                // texture convention (the same convention used by UpdateFrameInfo). Keep the exact
                // jittered matrix so consumers can reject stale camera history instead of silently
                // projecting it with the next frame's non-jittered matrix.
                Matrix4x4 viewMatrix = cameraData.GetViewMatrix();
                Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(
                    cameraData.GetProjectionMatrix(), true) * viewMatrix;
                Matrix4x4 viewProjectionNoJitter = GL.GetGPUProjectionMatrix(
                    cameraData.GetProjectionMatrixNoJitter(), true) * viewMatrix;
                int msaaSamples = cameraData.cameraTargetDescriptor.msaaSamples;
                bool xr = cameraData.xr.enabled;
                bool isDefaultViewport = cameraData.isDefaultViewport;

                // Record the ticket after every GPU command and every operation that can throw.
                // A ticket therefore exists only when this render function reached its normal
                // end. Unity 2022 can swallow a RenderGraph execution exception, so submission
                // is confirmed separately by the pipeline before the metadata is published.
                if (!activeResource.RecordFence(cmd, out GraphicsFence generationFence))
                {
                    InvalidateGpuSnapshot();
                    return false;
                }

                // CPU readback is an independent consumer and can complete several frames later.
                // GPU Indirect only needs proof that this generation was recorded and submitted;
                // same-queue ordering plus the RenderGraph buffer dependency protects its read.
                _pendingGpuViewProjectionMatrix = viewProjection;
                _pendingGpuViewProjectionNoJitterMatrix = viewProjectionNoJitter;
                _pendingGpuMsaaSamples = msaaSamples;
                _pendingGpuXr = xr;
                _pendingGpuIsDefaultViewport = isDefaultViewport;
                _pendingGpuDepthPyramidFrameCount = Time.frameCount;
                _pendingGpuSnapshotBuffer = _depthPyramidBuffer;
                _pendingGpuGenerationFence = generationFence;
                _pendingGpuGenerationSubmitted = false;
                _hasPendingGpuSnapshot = true;
                return true;
            }
            finally
            {
                // Keep a borrowed ring slot reachable even when native recording throws. An
                // uncertain readback remains onFlight until its callback; reduction can continue
                // without CPU readback if no other slot is available next frame.
                if (frameData != null)
                    m_RingBuffer.Enqueue(frameData);
            }
        }

        internal bool Setup(int2 depthBufferSize)
        {
            return Setup(m_CameraInstanceID, depthBufferSize);
        }

        internal void BeginRenderGraphRecording(RenderGraph renderGraph)
        {
            PollRetiredPyramidResources();
            _renderGraph = renderGraph;
            _renderGraphDepthPyramid = default;
            _hasRenderGraphDepthPyramid = false;
            _renderGraphPyramidResource = null;
        }

        /// <summary>
        /// Imports the current packed pyramid once for a graph recording. This preflight is kept
        /// separate from <see cref="TryDeclareRenderGraphRead"/> so callers can avoid creating a
        /// pass when the external handle cannot be imported.
        /// </summary>
        internal bool TryGetRenderGraphDepthPyramid(
            RenderGraph renderGraph, out BufferHandle depthPyramid)
        {
            depthPyramid = default;
            if (renderGraph == null || !IsGraphicsBufferValid(_depthPyramidBuffer))
                return false;

            EnsureActivePyramidResource();

            try
            {
                if (!_hasRenderGraphDepthPyramid || !ReferenceEquals(_renderGraph, renderGraph))
                {
                    _renderGraph = renderGraph;
                    _renderGraphDepthPyramid = renderGraph.ImportBuffer(
                        _depthPyramidBuffer, forceRelease: false);
                    _hasRenderGraphDepthPyramid = _renderGraphDepthPyramid.IsValid();
                    _renderGraphPyramidResource = _activePyramidResource;
                }

                if (!_hasRenderGraphDepthPyramid)
                {
                    _renderGraphDepthPyramid = default;
                    return false;
                }

                if (_renderGraphPyramidResource == null)
                    _renderGraphPyramidResource = _activePyramidResource;
                depthPyramid = _renderGraphDepthPyramid;
                return true;
            }
            catch (Exception)
            {
                // ImportBuffer can throw when a resource was invalidated between camera-pass
                // preparation and graph recording. Treat that as an occlusion miss instead of
                // aborting the whole camera.
                _renderGraphDepthPyramid = default;
                _hasRenderGraphDepthPyramid = false;
                _renderGraphPyramidResource = null;
                return false;
            }
        }

        internal bool TryDeclareRenderGraphRead(
            RenderGraph renderGraph, IBaseRenderGraphBuilder builder)
        {
            if (builder == null || !TryGetRenderGraphDepthPyramid(
                    renderGraph, out BufferHandle depthPyramid))
                return false;

            try
            {
                // Mark before entering the native builder.  A partially applied UseBuffer call
                // can throw after registering the external handle; conservative lifetime
                // tracking is safer than releasing the allocation without a fence.
                _renderGraphPyramidResource?.MarkGraphUse();
                builder.UseBuffer(depthPyramid, AccessFlags.Read);
                return true;
            }
            catch (Exception)
            {
                // Keep the external resource unbound when the builder rejects the declaration.
                return false;
            }
        }

        internal void MarkRenderGraphDepthPyramidUse()
        {
            _renderGraphPyramidResource?.MarkGraphUse();
        }

        internal void InvalidateGpuSnapshot()
        {
            gpuDepthPyramidFrameCount = -1;
            InvalidatePendingGpuSnapshot();
        }

        private void InvalidatePendingGpuSnapshot()
        {
            _pendingGpuDepthPyramidFrameCount = -1;
            _pendingGpuSnapshotBuffer = null;
            _pendingGpuGenerationFence = null;
            _pendingGpuGenerationSubmitted = false;
            _hasPendingGpuSnapshot = false;
        }

        private void PublishGpuSnapshot(
            Matrix4x4 viewProjection,
            Matrix4x4 viewProjectionNoJitter,
            int msaaSamples,
            bool xr,
            bool isDefaultViewport,
            int frameCount)
        {
            _gpuViewProjectionMatrix = viewProjection;
            _gpuViewProjectionNoJitterMatrix = viewProjectionNoJitter;
            _gpuMsaaSamples = msaaSamples;
            _gpuXr = xr;
            _gpuIsDefaultViewport = isDefaultViewport;
            gpuDepthPyramidFrameCount = frameCount;
        }

        internal void CommitPendingGpuSnapshot()
        {
            if (!_hasPendingGpuSnapshot || !_pendingGpuGenerationFence.HasValue ||
                !_pendingGpuGenerationSubmitted)
                return;

            bool sameBuffer = !k_UseComputeBuffer ||
                (ReferenceEquals(_depthPyramidBuffer, _pendingGpuSnapshotBuffer) &&
                    IsGraphicsBufferValid(_pendingGpuSnapshotBuffer));
            if (!sameBuffer)
            {
                InvalidateGpuSnapshot();
                return;
            }

            PublishGpuSnapshot(
                _pendingGpuViewProjectionMatrix,
                _pendingGpuViewProjectionNoJitterMatrix,
                _pendingGpuMsaaSamples,
                _pendingGpuXr,
                _pendingGpuIsDefaultViewport,
                _pendingGpuDepthPyramidFrameCount);
            InvalidatePendingGpuSnapshot();
        }

        private void MarkPendingGpuSnapshotSubmitted()
        {
            if (_hasPendingGpuSnapshot && _pendingGpuGenerationFence.HasValue)
                _pendingGpuGenerationSubmitted = true;
        }

        internal static void BeginAllRenderGraphRecording(RenderGraph renderGraph)
        {
            PollRetiredPyramidResources();
            if (renderGraph == null || OcclusionData == null)
                return;

            foreach (HZBOcclusionData data in OcclusionData.Values)
                data?.BeginRenderGraphRecording(renderGraph);
        }

        private static bool RecoverReadbackRequests(
            bool includeRecorded, bool requestsAlreadyWaited = false)
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                return true;

            bool needsRecovery = false;
            if (OcclusionData != null)
            {
                foreach (HZBOcclusionData data in OcclusionData.Values)
                {
                    if (data != null && data.NeedsReadbackRecovery(includeRecorded))
                    {
                        needsRecovery = true;
                        break;
                    }
                }
            }

            if (!needsRecovery)
            {
                for (int index = 0; index < s_retiredPyramidResources.Count; ++index)
                {
                    RetiredPyramidResource resource = s_retiredPyramidResources[index];
                    if (resource != null && resource.NeedsReadbackRecovery(includeRecorded))
                    {
                        needsRecovery = true;
                        break;
                    }
                }
            }

            if (!needsRecovery)
                return true;

            // A RequestAsyncReadbackIntoNativeArray call can throw after handing the destination
            // NativeArray to native code. Wait only on this exceptional recovery path; normal
            // successful frames remain fully asynchronous and never enter this stall.
            if (!requestsAlreadyWaited)
            {
                try
                {
                    AsyncGPUReadback.WaitAllRequests();
                }
                catch (Exception)
                {
                    // Keep the slot quarantined when the graphics device cannot complete the
                    // wait. Releasing it here would reintroduce the same use-after-free hazard
                    // that this recovery path is intended to prevent.
                    return false;
                }
            }

            var recoveredFrames = new List<HZBOcclusionFrameData>();
            if (OcclusionData != null)
            {
                foreach (HZBOcclusionData data in OcclusionData.Values)
                    data?.CollectReadbackRecoveryFrames(recoveredFrames, includeRecorded);
            }

            for (int index = 0; index < s_retiredPyramidResources.Count; ++index)
                s_retiredPyramidResources[index]?.CollectReadbackRecoveryFrames(
                    recoveredFrames, includeRecorded);

            // WaitAllRequests synchronizes the native writes. Invalidate each callback
            // generation before allowing the ring slot to be acquired again; Unity may dispatch
            // a callback on a later player-loop tick even after the wait has returned.
            for (int index = 0; index < recoveredFrames.Count; ++index)
                recoveredFrames[index]?.ResolveReadbackAfterWait();

            return true;
        }

        /// <summary>
        /// Appends completion fences for external HZB buffers touched by the just-recorded
        /// RenderGraph. The caller invokes this after EndRecordingAndExecute and before
        /// submitting the camera command buffer, which also covers read-only consumers that do
        /// not run the URP reduction pass.
        /// </summary>
        internal static void EndAllRenderGraphRecording(CommandBuffer commands)
        {
            if (commands == null || s_drainingRetiredPyramidResources)
                return;

            if (OcclusionData != null)
            {
                foreach (HZBOcclusionData data in OcclusionData.Values)
                    data?._activePyramidResource?.CompleteGraphUse(commands);
            }

            for (int index = 0; index < s_retiredPyramidResources.Count; ++index)
                s_retiredPyramidResources[index]?.CompleteGraphUse(commands);
        }

        /// <summary>
        /// Confirms that the command buffer carrying the RenderGraph completion fences was
        /// accepted by the scriptable render context.  A fence recorded into a command buffer
        /// that never reaches Submit is not a safe release point.
        /// </summary>
        internal static void MarkAllRenderGraphRecordingSubmitted()
        {
            if (s_drainingRetiredPyramidResources)
                return;

            if (OcclusionData != null)
            {
                foreach (HZBOcclusionData data in OcclusionData.Values)
                {
                    data?._activePyramidResource?.MarkGraphRecordingSubmitted();
                    data?.MarkPendingGpuSnapshotSubmitted();
                    data?.CommitPendingGpuSnapshot();
                }
            }

            for (int index = 0; index < s_retiredPyramidResources.Count; ++index)
                s_retiredPyramidResources[index]?.MarkGraphRecordingSubmitted();

            // A native readback recording exception enters the Uncertain state even when the
            // enclosing graph is eventually submitted successfully.  Always scan that state on
            // a successful Submit so a slot whose native call threw cannot remain quarantined
            // forever.  Healthy Recorded requests are intentionally excluded when there is no
            // graph-failure recovery pending; they remain callback-driven and never stall the
            // normal frame path.  When a graph failed, includeRecorded is true and the same
            // wait also resolves requests that were recorded into the failed command buffer.
            if (RecoverReadbackRequests(s_readbackRecoveryPending))
                s_readbackRecoveryPending = false;
        }

        /// <summary>
        /// Invalidates completion fences emitted for a graph whose command buffer was not
        /// submitted. A later camera may still call the global submitted notification, so the
        /// invalidated records remain quarantined until a replacement fence is emitted.
        /// </summary>
        internal static void InvalidateAllRenderGraphRecording()
        {
            if (s_drainingRetiredPyramidResources)
                return;

            if (OcclusionData != null)
            {
                foreach (HZBOcclusionData data in OcclusionData.Values)
                    data?._activePyramidResource?.MarkGraphRecordingFailed();
            }

            for (int index = 0; index < s_retiredPyramidResources.Count; ++index)
                s_retiredPyramidResources[index]?.MarkGraphRecordingFailed();

            // A failed recording/submission has no callback guarantee even for requests whose
            // command was recorded successfully. The command buffer is still alive while this
            // callback runs (it is released by the caller's finally block), so defer all
            // NativeArray state recovery until a later successful Submit or teardown.
            s_readbackRecoveryPending = true;
        }

        internal static void InvalidateAllGpuSnapshots()
        {
            if (OcclusionData == null)
                return;

            foreach (HZBOcclusionData data in OcclusionData.Values)
                data?.InvalidateGpuSnapshot();
        }

        private static bool IsGraphicsBufferValid(GraphicsBuffer buffer)
        {
            if (buffer == null)
                return false;

            try
            {
                return buffer.IsValid();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsTextureHandleValid(RTHandle texture)
        {
            try
            {
                return texture != null && texture.rt != null && texture.rt.IsCreated();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void EnsureActivePyramidResource()
        {
            if (_activePyramidResource != null)
                return;

            if (IsGraphicsBufferValid(_depthPyramidBuffer))
                _activePyramidResource = new RetiredPyramidResource(
                    _depthPyramidBuffer, null);
            else if (IsTextureHandleValid(m_OccluderDepthPyramid))
                _activePyramidResource = new RetiredPyramidResource(
                    null, m_OccluderDepthPyramid);
        }

        private void RetireActivePyramidResource()
        {
            RetiredPyramidResource resource = _activePyramidResource;
            if (resource == null && (_depthPyramidBuffer != null ||
                m_OccluderDepthPyramid != null))
            {
                resource = new RetiredPyramidResource(
                    _depthPyramidBuffer, m_OccluderDepthPyramid);
            }

            if (resource == null)
            {
                foreach (HZBOcclusionFrameData frameData in m_RingBuffer)
                    frameData?.Dispose();
                m_RingBuffer.Clear();
                return;
            }

            foreach (HZBOcclusionFrameData frameData in m_RingBuffer)
            {
                resource.AddReadbackFrame(frameData);
                frameData?.Dispose();
            }
            m_RingBuffer.Clear();
            resource.Retire(Time.frameCount);
            if (resource.hasResource)
                s_retiredPyramidResources.Add(resource);
            else
                resource.Release();

            _activePyramidResource = null;
        }

        internal static void PollRetiredPyramidResources()
        {
            if (s_drainingRetiredPyramidResources || s_retiredPyramidResources.Count == 0)
                return;

            int currentFrame = Time.frameCount;
            for (int index = s_retiredPyramidResources.Count - 1; index >= 0; --index)
            {
                RetiredPyramidResource resource = s_retiredPyramidResources[index];
                if (resource == null)
                {
                    s_retiredPyramidResources.RemoveAt(index);
                    continue;
                }

                if (!resource.IsReady(currentFrame))
                    continue;

                resource.Release();
                s_retiredPyramidResources.RemoveAt(index);
            }
        }

        private static void DrainRetiredPyramidResources()
        {
            if (s_drainingRetiredPyramidResources)
                return;

            s_drainingRetiredPyramidResources = true;
            try
            {
                // Readback completion is a prerequisite for releasing a source buffer. This may
                // stall during pipeline teardown, but avoids a use-after-free when a camera or
                // renderer is destroyed while a request is still in flight.
                for (int index = 0; index < s_retiredPyramidResources.Count; ++index)
                    s_retiredPyramidResources[index]?.RequestRetirementProof();

                bool readbackWaitCompleted = false;
                try
                {
                    AsyncGPUReadback.WaitAllRequests();
                    readbackWaitCompleted = true;
                }
                catch (Exception)
                {
                    // Keep quarantined resources if the graphics device is already shutting
                    // down and cannot complete the wait.
                }

                // WaitAllRequests also covers slots whose command-buffer recording threw before
                // a managed callback was registered. Resolve those states now so teardown does
                // not leave every retired allocation permanently quarantined merely because the
                // callback dispatch is deferred until the next player-loop tick.
                if (readbackWaitCompleted)
                    RecoverReadbackRequests(true, true);

                int currentFrame = Time.frameCount + 1;
                for (int index = s_retiredPyramidResources.Count - 1; index >= 0; --index)
                {
                    RetiredPyramidResource resource = s_retiredPyramidResources[index];
                    if (resource == null)
                    {
                        s_retiredPyramidResources.RemoveAt(index);
                        continue;
                    }

                    // IsReady deliberately remains conservative for a pending graph use or an
                    // invalid fence. Such resources are left in the static quarantine rather
                    // than being released unsafely during shutdown.
                    if (!resource.IsReady(currentFrame))
                        continue;

                    resource.Release();
                    s_retiredPyramidResources.RemoveAt(index);
                }
            }
            finally
            {
                s_drainingRetiredPyramidResources = false;
            }
        }

        private void ClearResources()
        {
            PollRetiredPyramidResources();
            RetireActivePyramidResource();
            m_OccluderDepthPyramid = null;
            _depthPyramidBuffer = null;
            _renderGraph = null;
            _renderGraphDepthPyramid = default;
            _hasRenderGraphDepthPyramid = false;
            _renderGraphPyramidResource = null;
            m_DepthBufferSize = default;
            totalSize = default;
            topMipSize = default;
            firstDepthMipIndex = 0;
            depthMips = 0;
            InvalidateGpuSnapshot();
            _gpuViewProjectionMatrix = default;
            _gpuViewProjectionNoJitterMatrix = default;
            _gpuMsaaSamples = 0;
            _gpuXr = false;
            _gpuIsDefaultViewport = false;
            Array.Clear(occluderMipBounds, 0, occluderMipBounds.Length);
        }

        internal void ClearData()
        {
            ClearResources();
            UnregisterCamera();
            _setupAttemptFrame = -1;
            _setupAttemptCamera = 0;
            _setupAttemptSize = default;
            _setupAttemptSucceeded = false;
        }

        internal static void ClearAllData()
        {
            if (OcclusionData != null && OcclusionData.Count != 0)
            {
                // ClearData removes its own entry, so iterate a stable copy. This also catches a
                // camera that produced an HZB and was disabled before the pipeline was disposed.
                var registeredData = new List<HZBOcclusionData>(OcclusionData.Values);
                for (int index = 0; index < registeredData.Count; ++index)
                    registeredData[index]?.ClearData();

                OcclusionData.Clear();
            }

            // Pipeline teardown is the one place where a synchronous readback wait is acceptable.
            // Any resource whose fence is still unknown remains quarantined for safety.
            DrainRetiredPyramidResources();
        }
        
        public void Dispose()
        {
            ClearData();
        }
    }
}
