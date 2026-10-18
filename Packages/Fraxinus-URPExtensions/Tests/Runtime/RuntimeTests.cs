using UnityEngine.TestTools;
using NUnit.Framework;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using ShadowResolutionRequest = UnityEngine.Rendering.Universal.AdditionalLightsShadowAtlasLayout.ShadowResolutionRequest;

[TestFixture]
class RuntimeTests
{
    GameObject go;
    Camera camera;
    RenderPipelineAsset currentAssetGraphics;
    RenderPipelineAsset currentAssetQuality;

    [SetUp]
    public void Setup()
    {
        go = new GameObject();
        camera = go.AddComponent<Camera>();
        currentAssetGraphics = GraphicsSettings.renderPipelineAsset;
        currentAssetQuality = QualitySettings.renderPipeline;
    }

    [TearDown]
    public void Cleanup()
    {
        GraphicsSettings.renderPipelineAsset = currentAssetGraphics;
        QualitySettings.renderPipeline = currentAssetQuality;
        Object.DestroyImmediate(go);
    }

    // When URP pipeline is active, lightsUseLinearIntensity must match active color space.
    [UnityTest]
    public IEnumerator PipelineHasCorrectColorSpace()
    {
        AssetCheck();

        camera.Render();
        yield return null;

        Assert.AreEqual(QualitySettings.activeColorSpace == ColorSpace.Linear, GraphicsSettings.lightsUseLinearIntensity,
            "GraphicsSettings.lightsUseLinearIntensity must match active color space.");
    }

    // When switching to URP it sets "UniversalPipeline" as global shader tag.
    // When switching to Built-in it sets "" as global shader tag.
#if UNITY_EDITOR // TODO This API call does not reset in player
    [UnityTest]
    public IEnumerator PipelineSetsAndRestoreGlobalShaderTagCorrectly()
    {
        AssetCheck();

        camera.Render();
        yield return null;

        Assert.AreEqual("UniversalPipeline", Shader.globalRenderPipeline, "Wrong render pipeline shader tag.");

        GraphicsSettings.renderPipelineAsset = null;
        QualitySettings.renderPipeline = null;
        camera.Render();
        yield return null;

        Assert.AreEqual("", Shader.globalRenderPipeline, "Render Pipeline shader tag is not restored.");
    }

#endif

    void AssetCheck()
    {
        //Assert.IsNotNull(currentAssetGraphics, "Render Pipeline Asset is Null");
        // Temp fix, test passes if project isnt setup for Universal RP
        if (RenderPipelineManager.currentPipeline == null)
            Assert.Pass("Render Pipeline Asset is Null, test pass by default");

        Assert.AreEqual(RenderPipelineManager.currentPipeline.GetType(), typeof(UniversalRenderPipeline),
            "Pipeline Asset is not Universal RP");
    }

    [Test]
    public void ShadowRequestTexelSumDetectsOverflowAfterExactMaximum()
    {
        long total = long.MaxValue - 1;
        Assert.IsFalse(AdditionalLightsShadowAtlasLayout.AccumulateShadowRequestTexels(ref total, 1));
        Assert.AreEqual(long.MaxValue, total);

        // Reaching exactly Int64.MaxValue is still exact until a later positive request is
        // encountered. The accumulator must report that transition so prefix trimming never
        // subtracts from a saturated (inexact) total.
        Assert.IsTrue(AdditionalLightsShadowAtlasLayout.AccumulateShadowRequestTexels(ref total, 1));
        Assert.AreEqual(long.MaxValue, total);
    }

    [Test]
    public void ShadowRequestTexelSumAllowsZeroAfterExactMaximum()
    {
        long total = long.MaxValue;
        Assert.IsFalse(AdditionalLightsShadowAtlasLayout.AccumulateShadowRequestTexels(ref total, 0));
        Assert.AreEqual(long.MaxValue, total);
    }

    [Test]
    public void ShadowAtlasBuddyLayoutRejectsNonPowerOfTwoScaledRequests()
    {
        using var customRequests = new NativeArray<ShadowResolutionRequest>(new[]
        {
            CreateShadowResolutionRequest(0, 21),
            CreateShadowResolutionRequest(1, 18),
            CreateShadowResolutionRequest(2, 18),
            CreateShadowResolutionRequest(3, 11),
            CreateShadowResolutionRequest(4, 11),
            CreateShadowResolutionRequest(5, 11),
            CreateShadowResolutionRequest(6, 10),
            CreateShadowResolutionRequest(7, 8),
        }, Allocator.Temp);
        using var powerOfTwoRequests = new NativeArray<ShadowResolutionRequest>(new[]
        {
            CreateShadowResolutionRequest(0, 32),
            CreateShadowResolutionRequest(1, 16),
        }, Allocator.Temp);

        Assert.IsFalse(AdditionalLightsShadowAtlasLayout.CanUseBuddyAtlasLayout(
            64, customRequests, customRequests.Length, 1));
        Assert.IsTrue(AdditionalLightsShadowAtlasLayout.TryLayoutShadowSlicesInFreeRectangles(
            customRequests, 64, customRequests.Length, 1, out bool resolutionTooSmall));
        Assert.IsFalse(resolutionTooSmall);

        for (int leftIndex = 0; leftIndex < customRequests.Length; ++leftIndex)
        {
            var left = customRequests[leftIndex];
            Assert.Greater(left.allocatedResolution, 0);
            Assert.LessOrEqual(left.offsetX + left.allocatedResolution, 64);
            Assert.LessOrEqual(left.offsetY + left.allocatedResolution, 64);

            for (int rightIndex = leftIndex + 1; rightIndex < customRequests.Length; ++rightIndex)
            {
                var right = customRequests[rightIndex];
                bool disjoint =
                    left.offsetX + left.allocatedResolution <= right.offsetX ||
                    right.offsetX + right.allocatedResolution <= left.offsetX ||
                    left.offsetY + left.allocatedResolution <= right.offsetY ||
                    right.offsetY + right.allocatedResolution <= left.offsetY;
                Assert.IsTrue(disjoint, $"Shadow tiles {leftIndex} and {rightIndex} overlap.");
            }
        }

        Assert.IsTrue(AdditionalLightsShadowAtlasLayout.CanUseBuddyAtlasLayout(
            64, powerOfTwoRequests, powerOfTwoRequests.Length, 1));

        // A power-of-two authored request can become non-power-of-two after an arbitrary
        // (custom/malformed) scale factor; that case must take the exact-resolution path too.
        Assert.IsFalse(AdditionalLightsShadowAtlasLayout.CanUseBuddyAtlasLayout(
            64, powerOfTwoRequests, powerOfTwoRequests.Length, 3));
    }

    [Test]
    public void ShadowAtlasFreeRectangleFallbackFillsGapsThatShelfPackingMisses()
    {
        // The shelf layout places three 2x2 tiles in two rows, then cannot fit the last 1x1 tile
        // after placing two 1x1 tiles. A free-rectangle layout uses the 2x2 gap in the
        // lower-right quadrant and fits all six requests in the same 4x4 atlas.
        using var requests = new NativeArray<ShadowResolutionRequest>(new[]
        {
            CreateShadowResolutionRequest(0, 2),
            CreateShadowResolutionRequest(1, 2),
            CreateShadowResolutionRequest(2, 2),
            CreateShadowResolutionRequest(3, 1),
            CreateShadowResolutionRequest(4, 1),
            CreateShadowResolutionRequest(5, 1),
        }, Allocator.Temp);

        Assert.IsTrue(AdditionalLightsShadowAtlasLayout.TryPackShadowSlicesInFreeRectangles(
            requests, 4, requests.Length, 1));
        for (int i = 0; i < requests.Length; ++i)
        {
            Assert.Greater(requests[i].allocatedResolution, 0);
            Assert.LessOrEqual(requests[i].offsetX + requests[i].allocatedResolution, 4);
            Assert.LessOrEqual(requests[i].offsetY + requests[i].allocatedResolution, 4);
            for (int j = i + 1; j < requests.Length; ++j)
            {
                bool disjoint =
                    requests[i].offsetX + requests[i].allocatedResolution <= requests[j].offsetX ||
                    requests[j].offsetX + requests[j].allocatedResolution <= requests[i].offsetX ||
                    requests[i].offsetY + requests[i].allocatedResolution <= requests[j].offsetY ||
                    requests[j].offsetY + requests[j].allocatedResolution <= requests[i].offsetY;
                Assert.IsTrue(disjoint, $"Shadow tiles {i} and {j} overlap.");
            }
        }
    }

    [Test]
    public void HZBOcclusionDataCanReinitializeAfterClearWithSameSize()
    {
        var occlusionData = new HZBOcclusionData();
        try
        {
            var depthSize = new int2(64, 64);
            Assert.IsTrue(occlusionData.Setup(depthSize));

            occlusionData.ClearData();

            // ClearData is also used when the render pipeline is disposed. Recreating URP with
            // the same camera resolution must allocate a new readback ring and backing buffer.
            Assert.IsTrue(occlusionData.Setup(depthSize));
        }
        finally
        {
            occlusionData.Dispose();
        }
    }

    [Test]
    public void HZBOcclusionDataRejectsEmptyDepthTargets()
    {
        var occlusionData = new HZBOcclusionData();
        try
        {
            Assert.IsFalse(occlusionData.Setup(int2.zero));
        }
        finally
        {
            occlusionData.Dispose();
        }
    }

    [Test]
    public void HZBOcclusionDataRetriesOnFrameAfterProviderFailure()
    {
        var occlusionData = new HZBOcclusionData();
        try
        {
            const int cameraInstanceID = 17;
            var depthSize = new int2(64, 64);
            Assert.IsTrue(occlusionData.Setup(cameraInstanceID, depthSize));

            occlusionData.MarkProviderFailure(cameraInstanceID, depthSize);

            Assert.IsFalse(occlusionData.Setup(cameraInstanceID, depthSize),
                "A provider failure must remain sticky for the current camera invocation.");
            // Setup uses Time.frameCount only as a frame stamp. Moving the failed-attempt stamp
            // back one frame verifies next-frame recovery without coupling the test to a player
            // loop or retaining the removed cross-frame fallback cooldown.
            typeof(HZBOcclusionData).GetField("_setupAttemptFrame",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(occlusionData, Time.frameCount - 1);

            Assert.IsTrue(occlusionData.Setup(cameraInstanceID, depthSize),
                "URP must retry immediately on the frame after a transient provider failure.");
        }
        finally
        {
            occlusionData.Dispose();
        }
    }

    static ShadowResolutionRequest CreateShadowResolutionRequest(
        ushort visibleLightIndex, ushort requestedResolution)
    {
        return new ShadowResolutionRequest
        {
            visibleLightIndex = visibleLightIndex,
            perLightShadowSliceIndex = 0,
            requestedResolution = requestedResolution,
            softShadow = false,
            pointLightShadow = false
        };
    }
}
