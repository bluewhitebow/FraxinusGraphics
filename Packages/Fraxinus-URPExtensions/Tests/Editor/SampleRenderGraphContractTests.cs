using System.IO;
using NUnit.Framework;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class SampleRenderGraphContractTests
    {
        const string k_SampleRoot = "Samples~/URPPackageSamples/RendererFeatures";

        [TestCase("KeepFrame/KeepFrameFeature.cs")]
        [TestCase("DistortTunnel/DistortTunnelPass_CopyColor.cs")]
        [TestCase("DistortTunnel/DistortTunnelPass_Distort.cs")]
        [TestCase("DistortTunnel/DistortTunnelPass_Tunnel.cs")]
        [TestCase("BlitToRTHandle/BlitToRTHandlePass.cs")]
        public void RenderGraphSamples_PreserveUnity6RecordEntryPoint(string relativePath)
        {
            string source = ReadSampleSource(relativePath);

            Assert.That(source, Does.Contain(
                "public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)"));
        }

        [Test]
        public void KeepFrame_PreservesPersistentTextureRasterPass()
        {
            string source = ReadSampleSource("KeepFrame/KeepFrameFeature.cs");

            Assert.That(source, Does.Contain("class PassData"));
            Assert.That(source, Does.Contain("AddRasterRenderPass<PassData>"));
            Assert.That(source, Does.Contain("CommandBufferHelpers.GetRasterCommandBuffer"));
            Assert.That(source, Does.Contain("AllowGlobalStateModification(true)"));
            Assert.That(source, Does.Contain("CoreRenderGraphUtils.BlitMaterialParameters"));
        }

        [Test]
        public void DistortTunnel_PreservesSharedFrameDataAndTextureArraySlices()
        {
            string feature = ReadSampleSource("DistortTunnel/DistortTunnelRendererFeature.cs");
            string copy = ReadSampleSource("DistortTunnel/DistortTunnelPass_CopyColor.cs");
            string distort = ReadSampleSource("DistortTunnel/DistortTunnelPass_Distort.cs");
            string tunnel = ReadSampleSource("DistortTunnel/DistortTunnelPass_Tunnel.cs");

            Assert.That(feature, Does.Contain("class TexRefData : ContextItem"));
            Assert.That(feature, Does.Contain("TextureDimension.Tex2DArray"));
            Assert.That(feature, Does.Contain("desc.volumeDepth = 2"));
            Assert.That(copy, Does.Contain("GetOrCreate<DistortTunnelRendererFeature.TexRefData>"));
            Assert.That(copy, Does.Contain("destinationSlice = m_Slice"));
            Assert.That(distort, Does.Contain("Get<DistortTunnelRendererFeature.TexRefData>"));
            Assert.That(tunnel, Does.Contain("AddRasterRenderPass<PassData>"));
            Assert.That(tunnel, Does.Contain("depthSlice: m_Slice"));
        }

        [Test]
        public void BlitToRTHandle_PreservesImportedPersistentTargetFlow()
        {
            string source = ReadSampleSource("BlitToRTHandle/BlitToRTHandlePass.cs");

            Assert.That(source, Does.Contain("class PassData"));
            Assert.That(source, Does.Contain("renderGraph.ImportTexture(m_OutputHandle)"));
            Assert.That(source, Does.Contain("CoreRenderGraphUtils.BlitMaterialParameters"));
            Assert.That(source, Does.Contain("resourceData.cameraColor = destination"));
        }

        static string ReadSampleSource(string relativePath)
        {
            UnityEditor.PackageManager.PackageInfo packageInfo =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UniversalRenderPipeline).Assembly);
            Assert.That(packageInfo, Is.Not.Null, "Could not resolve the URP package root.");

            string sourcePath = Path.Combine(
                packageInfo.resolvedPath,
                k_SampleRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.That(File.Exists(sourcePath), Is.True, sourcePath);
            return File.ReadAllText(sourcePath);
        }
    }
}
