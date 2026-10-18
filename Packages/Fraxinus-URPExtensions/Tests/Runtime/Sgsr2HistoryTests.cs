#if UNITY_INCLUDE_TESTS
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace UnityEngine.Rendering.Universal.Tests
{
    /// <summary>
    /// Verifies the SGSR2 history contract without requiring a camera render.
    /// The test exercises the same allocation/reset API used by both the
    /// compatibility and RenderGraph paths.
    /// </summary>
    [TestFixture]
    public class Sgsr2HistoryTests
    {
        private static Color ReadCenterPixel(RTHandle handle)
        {
            Assert.IsNotNull(handle);
            Assert.IsNotNull(handle.rt);

            RenderTexture previousActive = RenderTexture.active;
            var readback = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = handle.rt;
                readback.ReadPixels(new Rect(handle.rt.width / 2, handle.rt.height / 2, 1, 1), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(readback);
            }
        }

        [Test]
        public void UpdateAllocatesOutputResolutionAndReallocatesOnDescriptorChange()
        {
            var storage = new BufferedRTHandleSystem();
            var history = new Sgsr2History();
            history.OnCreate(storage, 0);

            try
            {
                var descriptor = new RenderTextureDescriptor(
                    320, 180, GraphicsFormat.R16G16B16A16_SFloat, 0)
                {
                    dimension = TextureDimension.Tex2D,
                    msaaSamples = 1,
                    depthStencilFormat = GraphicsFormat.None
                };

                Assert.IsTrue(history.Update(ref descriptor, 640, 360));
                var current = history.GetCurrentTexture();
                var previous = history.GetPreviousTexture();

                Assert.IsNotNull(current, "SGSR2 current history was not allocated.");
                Assert.IsNotNull(previous, "SGSR2 previous history was not allocated.");
                Assert.AreNotSame(current, previous, "SGSR2 history must be double buffered.");
                Assert.IsNotNull(current.rt, "Current SGSR2 history has no backing RenderTexture.");
                Assert.AreEqual(640, current.rt.width);
                Assert.AreEqual(360, current.rt.height);

                Assert.IsFalse(history.Update(ref descriptor, 640, 360),
                    "An unchanged SGSR2 descriptor should not force reallocation.");

                descriptor.graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm;
                Assert.IsTrue(history.Update(ref descriptor, 640, 360),
                    "Changing the history format must invalidate the allocation.");
                Assert.IsNotNull(history.GetCurrentTexture());
                Assert.IsNotNull(history.GetPreviousTexture());
            }
            finally
            {
                history.Reset();
                storage.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator PipelineExecutesSgsr2AcrossTwoFramesAndRotatesHistory()
        {
            RenderPipelineAsset previousGraphicsAsset = GraphicsSettings.renderPipelineAsset;
            RenderPipelineAsset previousQualityAsset = QualitySettings.renderPipeline;
            var sourceAsset = previousQualityAsset as UniversalRenderPipelineAsset
                ?? previousGraphicsAsset as UniversalRenderPipelineAsset;
            if (sourceAsset == null)
                Assert.Ignore("The SGSR2 integration test requires an active UniversalRenderPipelineAsset.");

            Shader shader = Shader.Find("Hidden/Universal Render Pipeline/Extensions/SGSR2");
            Assert.IsNotNull(shader, "The SGSR2 shader was not imported.");
            Assert.GreaterOrEqual(shader.passCount, TAAU_SGSR2.CopyHistoryPass + 1,
                "The SGSR2 shader must provide convert, upscale, and history-copy passes.");

            UniversalRenderPipelineAsset testAsset = null;
            GameObject cameraObject = null;
            RenderTexture target = null;
            try
            {
                testAsset = Object.Instantiate(sourceAsset);
                testAsset.name = "SGSR2 Runtime Test Pipeline";
                testAsset.hideFlags = HideFlags.HideAndDontSave;
                testAsset.renderScale = 0.5f;
                testAsset.upscalingFilter = UpscalingFilterSelection.SGSR2;

                GraphicsSettings.renderPipelineAsset = testAsset;
                QualitySettings.renderPipeline = testAsset;
                yield return null;

                cameraObject = new GameObject("SGSR2 Runtime Test Camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.8f, 0.1f, 0.1f, 1.0f);
                camera.allowHDR = true;

                var additionalData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
                additionalData.renderPostProcessing = true;
                additionalData.antialiasing = AntialiasingMode.None;

                target = new RenderTexture(160, 90, 24, RenderTextureFormat.ARGBHalf)
                {
                    name = "SGSR2 Runtime Test Target",
                    hideFlags = HideFlags.HideAndDontSave
                };
                Assert.IsTrue(target.Create(), "Could not create the SGSR2 test target.");
                camera.targetTexture = target;

                camera.Render();

                Sgsr2History history = additionalData.historyManager.GetHistoryForRead<Sgsr2History>();
                Assert.IsNotNull(history, "The first SGSR2 frame did not create camera history.");
                RTHandle firstWrittenHistory = history.GetCurrentTexture();
                Assert.IsNotNull(firstWrittenHistory, "The first SGSR2 frame did not allocate its current history target.");
                Assert.AreEqual(target.width, firstWrittenHistory.rt.width);
                Assert.AreEqual(target.height, firstWrittenHistory.rt.height);
                int firstAccumulationVersion = history.GetAccumulationVersion();
                Assert.GreaterOrEqual(firstAccumulationVersion, 0,
                    "The SGSR2 upscale pass did not mark the first history frame as written.");

                Color firstHistoryColor = ReadCenterPixel(firstWrittenHistory);
                Assert.Greater(firstHistoryColor.r, firstHistoryColor.g + 0.05f,
                    "The first SGSR2 history copy did not contain the rendered red frame.");

                // Advance Time.frameCount so the temporal path records a new
                // accumulation version instead of treating this as a repeated
                // render of the same camera frame.
                yield return null;

                camera.backgroundColor = new Color(0.1f, 0.1f, 0.8f, 1.0f);
                camera.Render();

                history = additionalData.historyManager.GetHistoryForRead<Sgsr2History>();
                Assert.IsNotNull(history);
                Assert.AreSame(firstWrittenHistory, history.GetPreviousTexture(),
                    "The first output was not rotated into the previous-frame SGSR2 history slot.");
                Assert.AreNotEqual(firstAccumulationVersion, history.GetAccumulationVersion(),
                    "The second SGSR2 frame did not execute the temporal accumulation path.");

                Color retainedHistoryColor = ReadCenterPixel(history.GetPreviousTexture());
                Assert.That(retainedHistoryColor.r, Is.EqualTo(firstHistoryColor.r).Within(2.0f / 255.0f));
                Assert.That(retainedHistoryColor.g, Is.EqualTo(firstHistoryColor.g).Within(2.0f / 255.0f));
                Assert.That(retainedHistoryColor.b, Is.EqualTo(firstHistoryColor.b).Within(2.0f / 255.0f));

                Color secondHistoryColor = ReadCenterPixel(history.GetCurrentTexture());
                Assert.IsFalse(float.IsNaN(secondHistoryColor.r) ||
                               float.IsNaN(secondHistoryColor.g) ||
                               float.IsNaN(secondHistoryColor.b),
                    "The second SGSR2 history frame contained NaN values.");
            }
            finally
            {
                GraphicsSettings.renderPipelineAsset = previousGraphicsAsset;
                QualitySettings.renderPipeline = previousQualityAsset;

                if (cameraObject != null)
                    Object.DestroyImmediate(cameraObject);
                if (target != null)
                {
                    target.Release();
                    Object.DestroyImmediate(target);
                }
                if (testAsset != null)
                    Object.DestroyImmediate(testAsset);
            }
        }
    }
}
#endif
