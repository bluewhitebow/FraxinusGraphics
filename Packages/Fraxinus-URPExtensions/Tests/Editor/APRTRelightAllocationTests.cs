using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class APRTRelightAllocationTests
    {
        [Test]
        public void EnabledAPRTRenderGraphRecordingDoesNotAllocateAfterWarmup()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("APRT recording requires compute buffers and a graphics device.");
            var volume = APRTProbeReferenceVolume.instance;
            if (volume.currentBakingSet != null || volume.loadedBakingSetRuntimes.Count != 0 ||
                Object.FindObjectsByType<ProbeVolumePerSceneData>(FindObjectsSortMode.None).Length != 0)
                Assert.Ignore("Run without scene-owned APRT sets to preserve their loading state.");

            var previousDefault = GraphicsSettings.defaultRenderPipeline;
            var previousQuality = QualitySettings.renderPipeline;
            bool wasInitialized = volume.isInitialized;
            var parametersField = typeof(APRTProbeReferenceVolume).GetField("m_SystemParameters", BindingFlags.Instance | BindingFlags.NonPublic);
            var previousParameters = (ProbeVolumeSystemParameters)parametersField.GetValue(volume);
            var endFrame = (Action<ScriptableRenderContext, List<Camera>>)Delegate.CreateDelegate(
                typeof(Action<ScriptableRenderContext, List<Camera>>), volume,
                typeof(APRTProbeReferenceVolume).GetMethod("OnFrameEnd", BindingFlags.Instance | BindingFlags.NonPublic));
            var cameras = new List<Camera>();
            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var owner = new GameObject("APRT enabled recording camera");
            owner.SetActive(false);
            var camera = owner.AddComponent<Camera>();
            var lightOwner = new GameObject("APRT enabled recording sun");
            var sun = lightOwner.AddComponent<Light>();
            sun.type = LightType.Directional;
            var frameData = new ContextContainer();
            var commands = new CommandBuffer();
            var graph = new RenderGraph("APRT enabled recording test") { nativeRenderPassesEnabled = false };
            var visibleLights = new NativeArray<VisibleLight>(1, Allocator.Temp);
            // Match culling's native Light reference without requiring an active render loop.
            object visibleSun = new VisibleLight { lightType = LightType.Directional, finalColor = sun.color.linear * sun.intensity,
                localToWorldMatrix = sun.transform.localToWorldMatrix };
            typeof(VisibleLight).GetField("m_InstanceId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(visibleSun, sun.GetInstanceID());
            visibleLights[0] = (VisibleLight)visibleSun;
            try
            {
                pipeline.lightProbeSystem = LightProbeSystem.AdaptivePRTVolumes;
                QualitySettings.renderPipeline = null;
                GraphicsSettings.defaultRenderPipeline = pipeline;
                set.CommitAPRTBakedData(CreateGrid());
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                var runtime = volume.GetBakingSetRuntime(set, true);
                volume.SetActiveBakingSet(set);
                Assert.That(runtime.PrepareRelight(camera), Is.True);
                Assert.That(runtime.lightingModel, Is.EqualTo(APRTLightingModel.APRTV));
                Assert.That(runtime.usesLightingScenarios, Is.False, "Exercise the live directional-light collection path.");

                using var recorder = new APRTAllocationRecorder();
                long allocated = 0;
                const int warmup = 8, samples = 32;
                for (int index = 0; index < warmup + samples; ++index)
                {
                    commands.Clear();
                    frameData.Dispose();
                    var cameraData = frameData.Create<UniversalCameraData>();
                    cameraData.camera = camera;
                    cameraData.cameraType = CameraType.Game;
                    cameraData.renderType = CameraRenderType.Base;
                    var lightData = frameData.Create<UniversalLightData>();
                    lightData.visibleLights = visibleLights;
                    lightData.mainLightIndex = 0;
                    graph.BeginRecording(new RenderGraphParameters
                    {
                        executionName = "APRT enabled recording test", currentFrameIndex = index,
                        commandBuffer = commands, invalidContextForTesting = true
                    });
                    recorder.Begin();
                    APRTRelightPass.Render(graph, frameData);
                    int recordingAllocations = recorder.End();
                    if (index >= warmup) allocated += recordingAllocations;
                    bool hasResources = APRTRelightPass.HasLightingResources(frameData);
                    graph.EndRecordingAndExecute();
                    // Execute callbacks but do not submit their shader-global changes.
                    endFrame(default, cameras);
                    Assert.That(hasResources, Is.True, "The measured pass must import actual APRT buffers.");
                }
                TestContext.WriteLine($"Enabled APRTV recording: {allocated} GC allocations / {samples} warmed samples.");
                Assert.That(allocated, Is.Zero, "Enabled APRTV recording must reuse pass storage and light inputs.");
            }
            finally
            {
                graph.Cleanup(); commands.Release(); frameData.Dispose(); visibleLights.Dispose();
                endFrame(default, cameras);
                volume.Cleanup();
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                Object.DestroyImmediate(owner); Object.DestroyImmediate(lightOwner);
                Object.DestroyImmediate(set); Object.DestroyImmediate(pipeline);
                if (wasInitialized) volume.Initialize(previousParameters);
            }
        }

        static APRTBakedData CreateGrid()
        {
            var cell = new APRTBakedCell
            {
                index = 0, probeCount = 8, probePositions = new Vector3[8],
                axisData = new Vector4[48], visibilityMoments = new Vector4[48], probeValidity = new float[8]
            };
            for (int i = 0; i < 8; ++i)
            {
                cell.probePositions[i] = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
                cell.probeValidity[i] = 1;
            }
            for (int i = 0; i < 48; ++i)
            {
                cell.axisData[i] = new Vector4(.5f, .5f, .5f, 1);
                cell.visibilityMoments[i] = new Vector4(100, 10000, 0, 0);
            }
            return new APRTBakedData
            {
                formatVersion = APRTBakedData.k_CurrentVersion, lightingModel = APRTLightingModel.APRTV,
                bakedMinDistanceBetweenProbes = 1, bakedSimplificationLevels = 0,
                globalBounds = new Bounds(Vector3.one * .5f, Vector3.one), cells = new List<APRTBakedCell> { cell },
                regularGrid = true, regularGridDimensions = new Vector3Int(2, 2, 2), regularGridRotation = Quaternion.identity,
                regularGridSpacing = 1, regularGridProbeIndices = new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, hasLeakReductionData = true
            };
        }
    }
}
