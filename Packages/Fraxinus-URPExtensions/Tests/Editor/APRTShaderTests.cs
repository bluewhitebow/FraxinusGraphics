using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class APRTShaderTests
    {
        [Test]
        public void APRTMaterialAndCaptureVariantsCompileOnTheActiveDevice()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Shader compilation requires a real graphics device.");
            var collection = new ShaderVariantCollection();
            try
            {
                foreach (string name in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Baked Lit" })
                {
                    var shader = Shader.Find(name);
                    Assert.That(shader, Is.Not.Null, name);
                    collection.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.ScriptableRenderPipeline, "PROBE_VOLUMES_APRT"));
                    collection.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.ScriptableRenderPipeline, "PROBE_VOLUMES_APRT", "_NORMALMAP"));
                    if (name != "Universal Render Pipeline/Baked Lit")
                    {
                        collection.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.ScriptableRenderPipeline, "PROBE_VOLUMES_APRT", "_MAIN_LIGHT_SHADOWS"));
                        collection.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.ScriptableRenderPipeline, "PROBE_VOLUMES_APRT", "_GBUFFER_NORMALS_OCT"));
                        collection.Add(new ShaderVariantCollection.ShaderVariant(shader, PassType.ScriptableRenderPipeline, "PROBE_VOLUMES_APRT", "_NORMALMAP", "_GBUFFER_NORMALS_OCT"));
                    }
                }
                var capture = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.unity.render-pipelines.core/Editor/Lighting/APRT/APRTProbeCapture.shader");
                Assert.That(capture, Is.Not.Null);
                collection.Add(new ShaderVariantCollection.ShaderVariant(capture, PassType.Normal));
                collection.WarmUp();
                foreach (string name in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Baked Lit", "Hidden/Fraxinus/APRTProbeCapture" })
                {
                    var shader = Shader.Find(name);
                    foreach (var message in ShaderUtil.GetShaderMessages(shader))
                        Assert.That(message.severity.ToString(), Is.Not.EqualTo("Error"), name + ": " + message.message);
                }
            }
            finally { Object.DestroyImmediate(collection); }
        }
    }
}
