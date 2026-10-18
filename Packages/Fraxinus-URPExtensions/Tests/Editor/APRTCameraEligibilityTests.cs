using System.Collections;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class APRTCameraEligibilityTests
    {
        [Test]
        public void APRTDisablesStreamingParametersWithoutChangingAPVConfiguration()
        {
            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            try
            {
                pipeline.supportProbeVolumeGPUStreaming = true;
                pipeline.supportProbeVolumeDiskStreaming = true;
                pipeline.supportProbeVolumeScenarios = true;
                pipeline.supportProbeVolumeScenarioBlending = true;
                pipeline.probeVolumeBlendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.None;
                pipeline.lightProbeSystem = LightProbeSystem.AdaptivePRTVolumes;
                var aprt = pipeline.GetProbeVolumeSystemParameters();
                Assert.That(aprt.enableAPRT, Is.True);
                Assert.That(aprt.supportGPUStreaming, Is.False);
                Assert.That(aprt.supportDiskStreaming, Is.False);
                Assert.That(aprt.supportScenarios, Is.True);
                Assert.That(aprt.supportScenarioBlending, Is.True,
                    "An APV blending budget of None must not disable APRT scenario blending.");

                pipeline.lightProbeSystem = LightProbeSystem.ProbeVolumes;
                var apv = pipeline.GetProbeVolumeSystemParameters();
                Assert.That(apv.enableAPV, Is.True);
                Assert.That(apv.supportGPUStreaming, Is.True);
                Assert.That(apv.supportDiskStreaming, Is.True);
                Assert.That(apv.blendingMemoryBudget, Is.EqualTo(ProbeVolumeBlendingTextureMemoryBudget.None));
                Assert.That(pipeline.supportProbeVolumeGPUStreaming, Is.True,
                    "Switching backends must preserve APV's serialized configuration.");
            }
            finally { Object.DestroyImmediate(pipeline); }
        }

        [TestCase(CameraRenderType.Base, CameraType.Game, true)]
        [TestCase(CameraRenderType.Base, CameraType.SceneView, true)]
        [TestCase(CameraRenderType.Base, CameraType.VR, true)]
        [TestCase(CameraRenderType.Base, CameraType.Reflection, false)]
        [TestCase(CameraRenderType.Base, CameraType.Preview, false)]
        [TestCase(CameraRenderType.Overlay, CameraType.Game, false)]
        public void OnlyLightingBaseCamerasCanUpdateSharedProbes(CameraRenderType renderType, CameraType cameraType, bool expected)
        {
            var owner = new GameObject(nameof(APRTCameraEligibilityTests));
            try
            {
                var data = new UniversalCameraData
                {
                    camera = owner.AddComponent<Camera>(),
                    cameraType = cameraType,
                    renderType = renderType
                };
                Assert.That(APRTRelightPass.CanUpdate(data), Is.EqualTo(expected));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [TestCase(CameraRenderType.Base, CameraType.Game, true)]
        [TestCase(CameraRenderType.Base, CameraType.SceneView, true)]
        [TestCase(CameraRenderType.Base, CameraType.VR, true)]
        [TestCase(CameraRenderType.Base, CameraType.Reflection, false)]
        [TestCase(CameraRenderType.Base, CameraType.Preview, false)]
        [TestCase(CameraRenderType.Overlay, CameraType.Game, false)]
        public void ProbeDebugUsesTheLightingCameraBoundary(CameraRenderType renderType, CameraType cameraType, bool expected)
        {
            var owner = new GameObject(nameof(APRTCameraEligibilityTests));
            try
            {
                var data = new UniversalCameraData
                {
                    camera = owner.AddComponent<Camera>(),
                    cameraType = cameraType,
                    renderType = renderType
                };
                Assert.That(ProbeVolumeDebugPass.IsAPRTDebugCameraEligible(data), Is.EqualTo(expected));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [TestCase(TextureDimension.Tex2D, RenderTextureFormat.ARGB32, true)]
        [TestCase(TextureDimension.Tex2D, RenderTextureFormat.Depth, false)]
        [TestCase(TextureDimension.Cube, RenderTextureFormat.ARGB32, false)]
        [TestCase(TextureDimension.CubeArray, RenderTextureFormat.ARGB32, false)]
        public void ProbeDebugDoesNotEnterBakeCubeOrDepthTargets(TextureDimension dimension, RenderTextureFormat format, bool expected)
        {
            var owner = new GameObject("APRT debug target camera");
            var target = new RenderTexture(16, 16, 24, format)
            {
                dimension = dimension,
                volumeDepth = dimension == TextureDimension.CubeArray ? 6 : 1
            };
            try
            {
                var camera = owner.AddComponent<Camera>();
                camera.targetTexture = target;
                var data = new UniversalCameraData { camera = camera, cameraType = CameraType.Game, renderType = CameraRenderType.Base };
                Assert.That(ProbeVolumeDebugPass.IsAPRTDebugCameraEligible(data), Is.EqualTo(expected));
            }
            finally { Object.DestroyImmediate(owner); Object.DestroyImmediate(target); }
        }

        [Test]
        public void MissingCameraCannotRenderProbeDebug()
        {
            Assert.That(ProbeVolumeDebugPass.IsAPRTDebugCameraEligible(null), Is.False);
            Assert.That(ProbeVolumeDebugPass.IsAPRTDebugCameraEligible(new UniversalCameraData()), Is.False);
        }

        [TestCase(TextureDimension.Cube)]
        [TestCase(TextureDimension.CubeArray)]
        public void CaptureDescriptorWithoutATargetCannotRenderProbeDebug(TextureDimension dimension)
        {
            var owner = new GameObject("APRT internal cube capture camera");
            try
            {
                var data = new UniversalCameraData
                {
                    camera = owner.AddComponent<Camera>(), cameraType = CameraType.Game, renderType = CameraRenderType.Base,
                    cameraTargetDescriptor = new RenderTextureDescriptor(16, 16) { dimension = dimension }
                };
                Assert.That(ProbeVolumeDebugPass.IsAPRTDebugCameraEligible(data), Is.False);
                Assert.That(APRTRelightPass.CanUpdate(data), Is.False);
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [Test]
        public void DepthOnlyCameraCannotClaimTheFrameBeforeColorCamera()
        {
            var depthOwner = new GameObject("APRT depth camera");
            var colorOwner = new GameObject("APRT color camera");
            var depthTarget = new RenderTexture(16, 16, 24, RenderTextureFormat.Depth);
            var colorTarget = new RenderTexture(16, 16, 24, RenderTextureFormat.ARGB32);
            try
            {
                var depthCamera = depthOwner.AddComponent<Camera>();
                depthCamera.targetTexture = depthTarget;
                var depthData = new UniversalCameraData { camera = depthCamera, cameraType = CameraType.Game, renderType = CameraRenderType.Base };
                var colorCamera = colorOwner.AddComponent<Camera>();
                colorCamera.targetTexture = colorTarget;
                var colorData = new UniversalCameraData { camera = colorCamera, cameraType = CameraType.Game, renderType = CameraRenderType.Base };

                Assert.That(APRTRelightPass.CanUpdate(depthData), Is.False, "A depth camera renders before APRT's relight pass and must not claim residency.");
                Assert.That(APRTRelightPass.CanUpdate(colorData), Is.True, "The subsequent color camera must still be able to update probes.");

                colorCamera.targetTexture = null;
                Assert.That(APRTRelightPass.CanUpdate(colorData), Is.True, "Backbuffer rendering also updates probes.");
            }
            finally
            {
                Object.DestroyImmediate(depthOwner);
                Object.DestroyImmediate(colorOwner);
                Object.DestroyImmediate(depthTarget);
                Object.DestroyImmediate(colorTarget);
            }
        }

        [Test]
        public void MissingFrameResourcesDoNotDispatchRelighting()
        {
            APRTRelightPass.Render(null, null);
            APRTRelightPass.UseLightingResources(null, null);
        }

        [Test]
        public void MissingCameraCannotUpdateSharedProbes()
        {
            Assert.That(APRTRelightPass.CanUpdate(null), Is.False);
            Assert.That(APRTRelightPass.CanUpdate(new UniversalCameraData()), Is.False);
        }

        [Test]
        public void ReusedLightInputsClearRemovedLightsWithoutAllocating()
        {
            var collect = (System.Action<UniversalLightData, Vector4[], Vector4[]>)System.Delegate.CreateDelegate(
                typeof(System.Action<UniversalLightData, Vector4[], Vector4[]>),
                typeof(APRTRelightPass).GetMethod("CollectDirectionalLights", BindingFlags.Static | BindingFlags.NonPublic));
            var colors = new Vector4[4];
            var directions = new Vector4[4];
            using var visible = new NativeArray<VisibleLight>(2, Allocator.Temp);
            var data = new UniversalLightData { visibleLights = visible, mainLightIndex = 1 };
            data.visibleLights[0] = new VisibleLight { lightType = LightType.Directional, finalColor = Color.red,
                localToWorldMatrix = Matrix4x4.identity };
            data.visibleLights[1] = new VisibleLight { lightType = LightType.Directional, finalColor = Color.green,
                localToWorldMatrix = Matrix4x4.Rotate(Quaternion.Euler(0, 90, 0)) };
            collect(data, colors, directions);
            Assert.That(colors[0], Is.EqualTo(new Vector4(0, 1, 0, 1)), "The main sun keeps priority.");
            Assert.That(colors[1], Is.EqualTo(new Vector4(1, 0, 0, 1)));

            data.visibleLights[1] = new VisibleLight { lightType = LightType.Point };
            collect(data, colors, directions);
            Assert.That(colors[0], Is.EqualTo(new Vector4(1, 0, 0, 1)));
            Assert.That(colors[1], Is.EqualTo(Vector4.zero), "The removed directional light must not survive scratch reuse.");
            Assert.That(directions[1], Is.EqualTo(Vector4.zero));

            for (int i = 0; i < 16; ++i) collect(data, colors, directions);
            using var recorder = new APRTAllocationRecorder();
            recorder.Begin();
            for (int i = 0; i < 64; ++i) collect(data, colors, directions);
            int allocated = recorder.End();
            Assert.That(allocated, Is.Zero);

            data.visibleLights = default;
            collect(data, colors, directions);
            for (int i = 0; i < colors.Length; ++i)
            {
                Assert.That(colors[i], Is.EqualTo(Vector4.zero));
                Assert.That(directions[i], Is.EqualTo(Vector4.zero));
            }
        }

        [Test]
        public void FallbackRenderGraphRecordingReusesStorageAndClearsPreviousCameraLighting()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Executing the recorded global bindings requires a graphics device.");
            if (APRTProbeReferenceVolume.instance.loadedBakingSetRuntimes.Count != 0)
                Assert.Ignore("Run without scene-owned APRT sets so the test leaves their loading state untouched.");

            var previousDefault = GraphicsSettings.defaultRenderPipeline;
            var previousQuality = QualitySettings.renderPipeline;
            string[] floatGlobals = { "_EnableAPRT", "_APRTGIIntensity", "_APRTAddAmbientLighting" };
            string[] intGlobals = { "_APRTSamplingSetCount", "_APRTDebugMode", "_APRTProbeCount", "_APRTLightingModel" };
            var previousFloats = new float[floatGlobals.Length];
            var previousInts = new int[intGlobals.Length];
            for (int i = 0; i < floatGlobals.Length; ++i) previousFloats[i] = Shader.GetGlobalFloat(floatGlobals[i]);
            for (int i = 0; i < intGlobals.Length; ++i) previousInts[i] = Shader.GetGlobalInt(intGlobals[i]);

            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            var firstOwner = new GameObject("APRT reflection recording test");
            var secondOwner = new GameObject("APRT preview recording test");
            firstOwner.SetActive(false); secondOwner.SetActive(false);
            var firstCamera = firstOwner.AddComponent<Camera>();
            var secondCamera = secondOwner.AddComponent<Camera>();
            var frameData = new ContextContainer();
            var commands = new CommandBuffer();
            var graph = new RenderGraph("APRT fallback recording test") { nativeRenderPassesEnabled = false };
            try
            {
                pipeline.lightProbeSystem = LightProbeSystem.AdaptivePRTVolumes;
                QualitySettings.renderPipeline = null;
                GraphicsSettings.defaultRenderPipeline = pipeline;
                Assert.That(UniversalRenderPipeline.asset, Is.SameAs(pipeline));
                Assert.That(pipeline.supportAPRT, Is.True, "The test must record the APRT pass instead of taking the asset early exit.");

                using var recorder = new APRTAllocationRecorder();
                long allocated = 0;
                const int warmup = 8, samples = 32;
                for (int index = 0; index < warmup + samples; ++index)
                {
                    commands.Clear();
                    frameData.Dispose();
                    var cameraData = frameData.Create<UniversalCameraData>();
                    cameraData.camera = (index & 1) == 0 ? firstCamera : secondCamera;
                    cameraData.cameraType = (index & 1) == 0 ? CameraType.Reflection : CameraType.Preview;
                    cameraData.renderType = CameraRenderType.Base;
                    frameData.Create<UniversalLightData>();
                    graph.BeginRecording(new RenderGraphParameters
                    {
                        executionName = "APRT fallback recording test", currentFrameIndex = index,
                        commandBuffer = commands, invalidContextForTesting = true
                    });

                    recorder.Begin();
                    APRTRelightPass.Render(graph, frameData);
                    int recordingAllocations = recorder.End();
                    if (index >= warmup) allocated += recordingAllocations;
                    Assert.That(APRTRelightPass.HasLightingResources(frameData), Is.False);

                    // Model an earlier camera's enabled GI. Only executing the
                    // recorded callback may replace these stale shader bindings.
                    Shader.SetGlobalFloat("_EnableAPRT", 1);
                    Shader.SetGlobalFloat("_APRTGIIntensity", 2);
                    Shader.SetGlobalFloat("_APRTAddAmbientLighting", 0);
                    Shader.SetGlobalInt("_APRTSamplingSetCount", 3);
                    Shader.SetGlobalInt("_APRTProbeCount", 24);
                    graph.EndRecordingAndExecute();
                    Graphics.ExecuteCommandBuffer(commands);
                    Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.Zero);
                    Assert.That(Shader.GetGlobalFloat("_APRTGIIntensity"), Is.Zero);
                    Assert.That(Shader.GetGlobalFloat("_APRTAddAmbientLighting"), Is.EqualTo(1));
                    Assert.That(Shader.GetGlobalInt("_APRTSamplingSetCount"), Is.Zero);
                    Assert.That(Shader.GetGlobalInt("_APRTProbeCount"), Is.Zero);
                }
                Assert.That(allocated, Is.Zero, "Warmed real RenderGraph recording must reuse the APRT pass's storage.");
            }
            finally
            {
                graph.Cleanup(); commands.Release(); frameData.Dispose();
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                Object.DestroyImmediate(firstOwner); Object.DestroyImmediate(secondOwner); Object.DestroyImmediate(pipeline);
                for (int i = 0; i < floatGlobals.Length; ++i) Shader.SetGlobalFloat(floatGlobals[i], previousFloats[i]);
                for (int i = 0; i < intGlobals.Length; ++i) Shader.SetGlobalInt(intGlobals[i], previousInts[i]);
            }
        }

        [UnityTest]
        public IEnumerator SwitchingActiveScenesInvalidatesTheDisabledAtmosphereSunCache()
        {
            // Runtime scene creation preserves the test runner's unsaved editor
            // scene without saving or replacing it.
            yield return new EnterPlayMode();
            var original = SceneManager.GetActiveScene(); var previousSun = RenderSettings.sun;
            Scene first = default, second = default;
            var find = typeof(APRTRelightPass).GetMethod("FindSkySun", BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                first = SceneManager.CreateScene("APRT atmosphere cache first scene");
                second = SceneManager.CreateScene("APRT atmosphere cache second scene");
                Assert.That(find, Is.Not.Null);
                SceneManager.SetActiveScene(first); RenderSettings.sun = null;
                var firstSun = new GameObject("Disabled atmosphere sun in first scene").AddComponent<Light>();
                firstSun.type = LightType.Directional; firstSun.intensity = 100; firstSun.enabled = false;
                Assert.That(find.Invoke(null, null), Is.SameAs(firstSun));

                SceneManager.SetActiveScene(second); RenderSettings.sun = null;
                var secondSun = new GameObject("Disabled atmosphere sun in second scene").AddComponent<Light>();
                secondSun.type = LightType.Directional; secondSun.intensity = 1; secondSun.enabled = false;
                Assert.That(find.Invoke(null, null), Is.SameAs(secondSun),
                    "The active scene's atmosphere must replace the cached brighter sun of the previous scene.");

                SceneManager.SetActiveScene(first); RenderSettings.sun = null;
                Assert.That(find.Invoke(null, null), Is.SameAs(firstSun), "Switching back must also invalidate the source orientation cache.");
            }
            finally
            {
                if (original.IsValid() && original.isLoaded) { SceneManager.SetActiveScene(original); RenderSettings.sun = previousSun; }
                if (first.IsValid() && first.isLoaded) SceneManager.UnloadSceneAsync(first);
                if (second.IsValid() && second.isLoaded) SceneManager.UnloadSceneAsync(second);
                APRTRelightPass.Cleanup();
            }
            yield return new ExitPlayMode();
        }

        [Test]
        public void PackedAmbientProbeMatchesUnityIrradianceEvaluation()
        {
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(new Color(.1f, .2f, .3f));
            probe.AddDirectionalLight(new Vector3(.3f, .8f, -.2f).normalized, new Color(.7f, .3f, .1f), 1.7f);
            var packed = APRTRelightPass.PackAmbientProbe(probe);
            var directions = new[] { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back,
                new Vector3(.4f, -.3f, .8f).normalized };
            var expected = new Color[directions.Length];
            probe.Evaluate(directions, expected);
            for (int i = 0; i < directions.Length; ++i)
            {
                Vector3 n = directions[i];
                var linear = new Vector4(n.x, n.y, n.z, 1);
                var quadratic = new Vector4(n.x * n.y, n.y * n.z, n.z * n.z, n.z * n.x);
                float difference = n.x * n.x - n.y * n.y;
                for (int channel = 0; channel < 3; ++channel)
                {
                    float actual = Vector4.Dot(packed[channel], linear) + Vector4.Dot(packed[channel + 3], quadratic)
                        + packed[6][channel] * difference;
                    Assert.That(actual, Is.EqualTo(expected[i][channel]).Within(.00001f),
                        "The runtime sky must use Unity's irradiance SH normalization.");
                }
            }
        }
    }
}
