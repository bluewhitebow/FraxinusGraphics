using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class HZBOcclusionAllocationTests
    {
        static object s_AllocationControl;

        [Test]
        public void ReadbackRecovery_IgnoresOldCallbackAfterSlotReuse()
        {
            var frame = new HZBOcclusionFrameData(new int2(1, 1));
            var context = new ContextContainer();
            bool previousPause = EditorApplication.isPaused;
            try
            {
                EditorApplication.isPaused = false;
                var data = context.Create<UniversalCameraData>();
                data.xr = new XRPass();
                data.SetViewAndProjectionMatrix(Matrix4x4.identity, Matrix4x4.identity);
                var arguments = new object[] { new CameraData(context) };
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var begin = typeof(HZBOcclusionFrameData).GetMethod("UpdateFrameInfo", flags);
                var callback = typeof(HZBOcclusionFrameData).GetField("m_ReadbackCallback", flags);

                // Simulate a recording that never reached the GPU. Recovery releases the
                // slot, but Unity may still deliver its managed callback after reuse.
                Assert.That(begin.Invoke(frame, arguments), Is.True);
                var oldCallback = (Action<AsyncGPUReadbackRequest>)callback.GetValue(frame);
                frame.ResolveReadbackAfterWait();
                Assert.That(frame.valid, Is.False);
                Assert.That(begin.Invoke(frame, arguments), Is.True);
                Assert.That(frame.onFlight, Is.True);
                int newFrameCount = frame.lastRenderFrameCount;
                oldCallback(default);
                Assert.That(frame.onFlight, Is.True, "An old completion must not release the new request.");
                Assert.That(frame.lastRenderFrameCount, Is.EqualTo(newFrameCount));

                // A stale callback must also leave the destination alive when disposal is
                // deferred until the new request completes.
                frame.Dispose();
                oldCallback(default);
                Assert.That(frame.hzBuffer.IsCreated, Is.True);
                frame.ResolveReadbackAfterWait();
                Assert.That(frame.hzBuffer.IsCreated, Is.False);
            }
            finally
            {
                frame.ResolveReadbackAfterWait();
                frame.Dispose();
                context.Dispose();
                EditorApplication.isPaused = previousPause;
            }
        }
        [TestCase(1920, 1080)]
        [TestCase(64, 64)]
        public void ReductionAndReadback_WarmedRecordingDoesNotAllocate(int width, int height)
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback ||
                !SystemInfo.supportsGraphicsFence)
                Assert.Ignore("Requires a graphics device with compute, readback and fences.");

            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.unity.render-pipelines.universal/Runtime/Extensions/HZBOcclusion/DepthPyramidKernels.compute");
            Assert.That(shader, Is.Not.Null);
            var kernel = shader.FindKernel("OccluderDepthDownscale");
            var gameObject = new GameObject("HZB allocation test");
            gameObject.SetActive(false);
            var camera = gameObject.AddComponent<Camera>();
            var source = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat);
            var commands = new CommandBuffer();
            var context = new ContextContainer();
            var hzb = new HZBOcclusionData();
            var recorder = Recorder.Get("GC.Alloc");
            try
            {
                Assert.That(source.Create(), Is.True);
                var data = context.Create<UniversalCameraData>();
                data.camera = camera;
                data.cameraTargetDescriptor = source.descriptor;
                data.xr = new XRPass();
                data.isDefaultViewport = true;
                data.SetViewAndProjectionMatrix(camera.worldToCameraMatrix, camera.projectionMatrix);
                var cameraData = new CameraData(context);
                Assert.That(hzb.Setup(camera.GetInstanceID(), new int2(width, height)), Is.True);
                var sourceId = new RenderTargetIdentifier(source);
                commands.SetRenderTarget(source);
                commands.ClearRenderTarget(false, true, new Color(0.25f, 0, 0, 0));
                Graphics.ExecuteCommandBuffer(commands);

                recorder.enabled = false;
                recorder.FilterToCurrentThread();
                recorder.enabled = true;
                s_AllocationControl = new byte[1024];
                recorder.enabled = false;
                Assert.That(recorder.sampleBlockCount, Is.GreaterThan(0), "GC recording must detect the positive control.");
                s_AllocationControl = null;

                long allocated = 0;
                const int warmup = 8;
                const int samples = 16;
                for (int index = 0; index < warmup + samples; ++index)
                {
                    commands.Clear();
                    bool generated;
                    recorder.enabled = true;
                    try
                    {
                        generated = hzb.RenderAndRequestReadbackAsync(commands, shader, kernel,
                            sourceId, hzb.gpuDepthPyramid, 1, ref cameraData);
                    }
                    finally
                    {
                        recorder.enabled = false;
                    }
                    if (index >= warmup)
                        allocated += recorder.sampleBlockCount;
                    Assert.That(generated, Is.True);
                    Graphics.ExecuteCommandBuffer(commands);
                    HZBOcclusionData.MarkAllRenderGraphRecordingSubmitted();
                    AsyncGPUReadback.WaitAllRequests();
                    var readback = hzb.GetLatestFrameData();
                    Assert.That(readback, Is.Not.Null, "The allocation check must exercise CPU readback.");
                    Assert.That(readback.hzBuffer[0], Is.EqualTo(0.25f).Within(0.0001f));
                }

                TestContext.WriteLine($"HZB {width}x{height}: {allocated / samples} GC allocations per warmed recording ({samples} samples).");
                Assert.That(allocated, Is.Zero, "HZB reduction and readback should not allocate after warmup.");
            }
            finally
            {
                recorder.enabled = false;
                recorder.CollectFromAllThreads();
                s_AllocationControl = null;
                AsyncGPUReadback.WaitAllRequests();
                commands.Release();
                hzb.Dispose();
                HZBOcclusionData.PollRetiredPyramidResources();
                AsyncGPUReadback.WaitAllRequests();
                HZBOcclusionData.PollRetiredPyramidResources();
                context.Dispose();
                source.Release();
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
    }
}
