using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if !UNITY_6000_0_OR_NEWER
using System.Reflection;
using UnityEditor;
using UnityEngine.Rendering;
#endif
#if UNITY_6000_0_OR_NEWER
using RenderingLayerEditorUtility = UnityEditor.Rendering.RenderPipelineEditorUtility;
#else
using RenderingLayerEditorUtility = UnityEngine.Rendering.Universal.UniversalRenderPipelineGlobalSettings;
#endif

[TestFixture]
class RenderingLayersTests
{
    string[] m_DefinedLayers;
    int[] m_DefinedValues;
    int m_LayersSize;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        m_DefinedLayers = RenderingLayerMask.GetDefinedRenderingLayerNames();
        m_DefinedValues = RenderingLayerMask.GetDefinedRenderingLayerValues();
        m_LayersSize = RenderingLayerMask.GetRenderingLayerCount();
    }

    [OneTimeTearDown]
    public void OneTimeTeardown()
    {
        var diff = RenderingLayerMask.GetRenderingLayerCount() - m_LayersSize;
        if (diff > 0)
            for (int i = 0; i < diff; i++)
                RenderingLayerEditorUtility.TryRemoveLastRenderingLayerName();
        else
            for (int i = 0; i < -diff; i++)
                RenderingLayerEditorUtility.TryAddRenderingLayerName(string.Empty);

        for (int i = 1; i < RenderingLayerMask.GetRenderingLayerCount(); i++)
            RenderingLayerEditorUtility.TrySetRenderingLayerName(i, string.Empty);

        for (int i = 0; i < m_DefinedValues.Length; i++)
        {
            var value = m_DefinedValues[i];
            if (RenderingLayerMask.defaultRenderingLayerMask == value)
                continue;
            var name = m_DefinedLayers[i];
            var index = Mathf.FloorToInt(Mathf.Log(value, 2));
            RenderingLayerEditorUtility.TrySetRenderingLayerName(index, name);
        }
    }

    [SetUp]
    public void Setup()
    {
        var layerCount = RenderingLayerMask.GetRenderingLayerCount() - 1;
        for (int i = 0; i < layerCount; i++)
            RenderingLayerEditorUtility.TryRemoveLastRenderingLayerName();
    }

    static TestCaseData[] s_MaskSizeTestCases =
    {
        new TestCaseData(7)
            .SetName("Given a Rendering Layers size of 7, the mask size is 8 bits")
            .Returns((int)RenderingLayerUtils.MaskSize.Bits8),
        new TestCaseData(8)
            .SetName("Given a Rendering Layers size of 8, the mask size is 8 bits")
            .Returns((int)RenderingLayerUtils.MaskSize.Bits8),
        new TestCaseData(9)
            .SetName("Given a Rendering Layers size of 9, the mask size is 16 bits")
            .Returns((int)RenderingLayerUtils.MaskSize.Bits16),
        new TestCaseData(16)
            .SetName("Given a Rendering Layers size of 16, the mask size is 16 bits")
            .Returns((int)RenderingLayerUtils.MaskSize.Bits16),
        new TestCaseData(17)
            .SetName("Given a Rendering Layers size of 17, the mask size is 24 bits")
            .Returns((int)RenderingLayerUtils.MaskSize.Bits24),
        new TestCaseData(24)
            .SetName("Given a Rendering Layers size of 24, the mask size is 24 bits")
            .Returns((int)RenderingLayerUtils.MaskSize.Bits24),
        new TestCaseData(25)
            .SetName("Given a Rendering Layers size of 25, the mask size is 32 bits")
            .Returns((int)RenderingLayerUtils.MaskSize.Bits32),
        new TestCaseData(32)
            .SetName("Given a Rendering Layers size of 32, the mask size is 32 bits")
            .Returns((int)RenderingLayerUtils.MaskSize.Bits32)
    };

    [Test, TestCaseSource(nameof(s_MaskSizeTestCases))]
    public int MaskSizeTest(int tagManagerLayerCount)
    {
        var currentLayerCount = RenderingLayerMask.GetRenderingLayerCount();
        var requiredLayers = tagManagerLayerCount - currentLayerCount;
        if (requiredLayers > 0)
            for (int i = 0; i < requiredLayers; i++)
                RenderingLayerEditorUtility.TryAddRenderingLayerName($"Layer {currentLayerCount + i}");

        var urpRenderer = ScriptableObject.CreateInstance<UniversalRendererData>().InternalCreateRenderer() as UniversalRenderer;
        RenderingLayerUtils.RequireRenderingLayers(urpRenderer, new List<ScriptableRendererFeature>(), 0, out var evt, out var maskSize);
        urpRenderer.Dispose();
        return (int)maskSize;
    }
}

#if !UNITY_6000_0_OR_NEWER
// Exercise the legacy API that Unity 2022's built-in Renderer inspector consumes.
[TestFixture]
class RenderingLayerInspectorCompatibilityTests
{
    private UniversalRenderPipelineGlobalSettings _previousSettings;
    private UniversalRenderPipelineGlobalSettings _settings;
    private UniversalRenderPipelineAsset _asset;

    [SetUp]
    public void SetUp()
    {
        _previousSettings = GraphicsSettings.GetSettingsForRenderPipeline<UniversalRenderPipeline>()
            as UniversalRenderPipelineGlobalSettings;
        _settings = ScriptableObject.CreateInstance<UniversalRenderPipelineGlobalSettings>();
        _asset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
        RegisterSettings(_settings);
    }

    [TearDown]
    public void TearDown()
    {
        RegisterSettings(_previousSettings);
        Object.DestroyImmediate(_asset);
        Object.DestroyImmediate(_settings);
    }

    [Test]
    public void PipelineAsset_UsesCompleteCachedNames()
    {
        Assert.That(_asset.renderingLayerMaskNames, Is.SameAs(_settings.renderingLayerMaskNames));
        var names = _asset.prefixedRenderingLayerMaskNames;
        Assert.That(names, Is.SameAs(_settings.prefixedRenderingLayerMaskNames));
        Assert.That(names.Length, Is.EqualTo(32));
        Assert.That(names[0], Is.EqualTo("Default"));
        Assert.That(names[31], Is.EqualTo("Unused Layer 31"));
    }

    [Test]
    public void PipelineAsset_PreservesBitPositionsAndObservesLayerEdits()
    {
        // Populate the cache before changing the layer registry.
        Assert.That(_asset.prefixedRenderingLayerMaskNames[2], Is.EqualTo("Unused Layer 2"));
        Assert.That(UniversalRenderPipelineGlobalSettings.TryAddRenderingLayerName(string.Empty), Is.True);
        Assert.That(UniversalRenderPipelineGlobalSettings.TryAddRenderingLayerName("Occluders"), Is.True);
        Assert.That(_asset.renderingLayerMaskNames, Is.EqualTo(new[] { "Default", "", "Occluders" }));
        Assert.That(_asset.prefixedRenderingLayerMaskNames[2], Is.EqualTo("Occluders"));

        Assert.That(UniversalRenderPipelineGlobalSettings.TrySetRenderingLayerName(2, "Characters"), Is.True);
        Assert.That(_asset.prefixedRenderingLayerMaskNames[2], Is.EqualTo("Characters"));
        Assert.That(UniversalRenderPipelineGlobalSettings.TryRemoveLastRenderingLayerName(), Is.True);
        Assert.That(_asset.prefixedRenderingLayerMaskNames[2], Is.EqualTo("Unused Layer 2"));
    }

    [Test]
    public void PipelineAsset_MissingGlobalSettingsAllowsEditorFallback()
    {
        RegisterSettings(null);
        Assert.That(_asset.renderingLayerMaskNames, Is.Null);
        Assert.That(_asset.prefixedRenderingLayerMaskNames, Is.Null);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(1 << 7)]
    [TestCase(int.MinValue)]
    [TestCase(-1)]
    public void PipelineAsset_RendererMaskMenuAcceptsAllMaskBits(int mask)
    {
        var names = _asset.prefixedRenderingLayerMaskNames;
        var values = new int[names.Length];
        for (int i = 0; i < values.Length; ++i)
            values[i] = 1 << i;

        // Invoke the same menu builder as EditorGUI.MaskField without needing an OnGUI event.
        var maskFieldType = typeof(EditorGUI).Assembly.GetType("UnityEditor.MaskFieldGUI");
        Assert.That(maskFieldType, Is.Not.Null);
        var getMenuOptions = maskFieldType.GetMethod("GetMenuOptions", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(getMenuOptions, Is.Not.Null);
        var arguments = new object[] { mask, names, values, null, null, null, null, null, null };
        Assert.DoesNotThrow(() => getMenuOptions.Invoke(null, arguments));
    }

    private static void RegisterSettings(UniversalRenderPipelineGlobalSettings settings)
    {
        // Avoid UpdateGraphicsSettings: these transient fixtures must not create project assets.
        if (settings != null)
            GraphicsSettings.RegisterRenderPipelineSettings<UniversalRenderPipeline>(settings);
        else
            GraphicsSettings.UnregisterRenderPipelineSettings<UniversalRenderPipeline>();
        RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(settings, typeof(UniversalRenderPipeline));
    }
}
#endif
