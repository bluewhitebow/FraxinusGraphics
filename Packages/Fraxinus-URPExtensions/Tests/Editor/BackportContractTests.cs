using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class BackportContractTests
    {
        const BindingFlags k_InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

#if !UNITY_6000_0_OR_NEWER
        [Test]
        public void Unity2022_CurrentPipelineHelpURLProvidesBaseHelpURL()
        {
            var attribute = new CurrentPipelineHelpURLAttribute("Volumes");
            var helpAttribute = (HelpURLAttribute)attribute;

            Assert.That(helpAttribute.URL, Is.Not.Null.And.Not.Empty);
            Assert.That(helpAttribute.URL, Does.StartWith("https://"));
        }

        [Test]
        public void Unity2022_CurrentPipelineHelpURLUsesActivePipeline()
        {
            var previousDefault = GraphicsSettings.defaultRenderPipeline;
            var previousQuality = QualitySettings.renderPipeline;
            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            try
            {
                QualitySettings.renderPipeline = null;
                GraphicsSettings.defaultRenderPipeline = pipeline;
                var attribute = new CurrentPipelineHelpURLAttribute("Volumes");

                Assert.That(((HelpURLAttribute)attribute).URL,
                    Does.Contain("com.unity.render-pipelines.universal"));
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                UnityEngine.Object.DestroyImmediate(pipeline);
            }
        }

        [Test]
        public void Unity2022_HelpResolvesVolumeProfileURL()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            try
            {
                Assert.That(Help.GetHelpURLForObject(profile),
                    Does.StartWith("https://"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [TestCase(typeof(VolumeProfile), "VolumeProfileEditor", false)]
        [TestCase(typeof(LensFlareComponentSRP), "LensFlareComponentSRPEditor", false)]
        [TestCase(typeof(LensFlareComponentSRP), "LensFlareComponentSRPEditor", true)]
        [TestCase(typeof(LensFlareDataSRP), "LensFlareDataSRPEditor", false)]
        [TestCase(typeof(Projector), "ProjectorEditor", false)]
        [TestCase(typeof(LensFlare), "LensFlareEditor", false)]
        public void Unity2022_SelectsCorePipelineInspectorOnlyForSRP(Type inspectedType, string editorName, bool multiEdit)
        {
            var editorNamespace = inspectedType == typeof(LensFlare) ? "UnityEditor." : "UnityEditor.Rendering.";
            var editorType = typeof(UniversalRenderPipelineVolumeComponentEditor).BaseType.Assembly
                .GetType(editorNamespace + editorName, true);
            Unity2022_SelectsPipelineInspectorOnlyForURP(inspectedType, editorType, multiEdit);
        }

        [Test]
        public void Unity2022_FindsURPLightingExplorer()
        {
            var previousDefault = GraphicsSettings.defaultRenderPipeline;
            var previousQuality = QualitySettings.renderPipeline;
            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            try
            {
                QualitySettings.renderPipeline = null;
                GraphicsSettings.defaultRenderPipeline = pipeline;
                Assert.That(RenderPipelineEditorUtility
                    .FetchFirstCompatibleTypeUsingScriptableRenderPipelineExtension<ILightingExplorerExtension>(),
                    Is.EqualTo(typeof(LightExplorer)));

                GraphicsSettings.defaultRenderPipeline = null;
                Assert.That(RenderPipelineEditorUtility
                    .FetchFirstCompatibleTypeUsingScriptableRenderPipelineExtension<ILightingExplorerExtension>(),
                    Is.Null);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                UnityEngine.Object.DestroyImmediate(pipeline);
            }
        }

        [TestCase(typeof(UniversalRenderPipelineAsset), typeof(UniversalRenderPipelineAssetEditor))]
        [TestCase(typeof(UniversalRendererData), typeof(UniversalRendererDataEditor))]
        [TestCase(typeof(Renderer2DData), typeof(Renderer2DDataEditor))]
        [TestCase(typeof(UniversalRenderPipelineGlobalSettings), typeof(UniversalGlobalSettingsEditor))]
        [TestCase(typeof(Bloom), typeof(BloomEditor))]
        public void Unity2022_RetainsTypeSpecificInspectors(Type inspectedType, Type expectedEditor)
        {
            var editorAttributes = typeof(Editor).Assembly.GetType("UnityEditor.CustomEditorAttributes", true);
            var findEditor = editorAttributes.GetMethod("FindCustomEditorTypeByType",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(findEditor.Invoke(null, new object[] { inspectedType, false }), Is.EqualTo(expectedEditor));
        }

        [Test]
        public void Unity2022_ResolvesDefaultVolumeProfilePropertyDrawer()
        {
            var utility = typeof(Editor).Assembly.GetType("UnityEditor.ScriptAttributeUtility", true);
            var method = utility.GetMethod("GetDrawerTypeForType", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var arguments = method.GetParameters().Length == 1
                ? new object[] { typeof(URPDefaultVolumeProfileSettings) }
                : new object[] { typeof(URPDefaultVolumeProfileSettings), false };
            Assert.That(method.Invoke(null, arguments), Is.EqualTo(typeof(URPDefaultVolumeProfileSettingsPropertyDrawer)));
        }

        [TestCase(typeof(Camera), typeof(UniversalRenderPipelineCameraEditor))]
        [TestCase(typeof(Light), typeof(UniversalRenderPipelineLightEditor))]
        public void Unity2022_CreatesCameraAndLightInspectors(Type componentType, Type expectedEditor)
        {
            var previousDefault = GraphicsSettings.defaultRenderPipeline;
            var previousQuality = QualitySettings.renderPipeline;
            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            var gameObject = new GameObject("Inspector initialization test");
            gameObject.SetActive(false);
            Editor editor = null;
            try
            {
                QualitySettings.renderPipeline = null;
                GraphicsSettings.defaultRenderPipeline = pipeline;
                var component = gameObject.AddComponent(componentType);
                editor = Editor.CreateEditor(component);
                Assert.That(editor.GetType(), Is.EqualTo(expectedEditor));
                if (component is Camera)
                {
                    Assert.That(gameObject.GetComponent<UniversalAdditionalCameraData>(), Is.Not.Null);
                    var serializedCamera = new UniversalRenderPipelineSerializedCamera(editor.serializedObject);
                    serializedCamera.Update();
                    foreach (var property in typeof(UniversalRenderPipelineSerializedCamera).GetProperties())
                    {
                        if (property.PropertyType == typeof(SerializedProperty))
                            Assert.That(property.GetValue(serializedCamera), Is.Not.Null, property.Name);
                    }
                    serializedCamera.Apply();
                }
                else
                {
                    Assert.That(gameObject.GetComponent<UniversalAdditionalLightData>(), Is.Not.Null);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(editor);
                UnityEngine.Object.DestroyImmediate(gameObject);
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                UnityEngine.Object.DestroyImmediate(pipeline);
            }
        }

        [TestCase(typeof(Camera), typeof(UniversalRenderPipelineCameraEditor), false)]
        [TestCase(typeof(Camera), typeof(UniversalRenderPipelineCameraEditor), true)]
        [TestCase(typeof(Light), typeof(UniversalRenderPipelineLightEditor), false)]
        [TestCase(typeof(Light), typeof(UniversalRenderPipelineLightEditor), true)]
        [TestCase(typeof(VolumeComponent), typeof(UniversalRenderPipelineVolumeComponentEditor), false)]
        [TestCase(typeof(ColorAdjustments), typeof(UniversalRenderPipelineVolumeComponentEditor), false)]
        public void Unity2022_SelectsPipelineInspectorOnlyForURP(Type inspectedType, Type expectedEditor, bool multiEdit)
        {
            var editorAttributes = typeof(Editor).Assembly.GetType("UnityEditor.CustomEditorAttributes", true);
            var findEditor = editorAttributes.GetMethod("FindCustomEditorTypeByType",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(findEditor, Is.Not.Null);

            var previousDefault = GraphicsSettings.defaultRenderPipeline;
            var previousQuality = QualitySettings.renderPipeline;
            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            try
            {
                QualitySettings.renderPipeline = null;
                GraphicsSettings.defaultRenderPipeline = pipeline;
                Assert.That(findEditor.Invoke(null, new object[] { inspectedType, multiEdit }),
                    Is.EqualTo(expectedEditor), "Unity must select the URP Inspector when URP is active.");

                GraphicsSettings.defaultRenderPipeline = null;
                Assert.That(findEditor.Invoke(null, new object[] { inspectedType, multiEdit }),
                    Is.Not.EqualTo(expectedEditor), "The URP Inspector must not replace the built-in pipeline Inspector.");
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                UnityEngine.Object.DestroyImmediate(pipeline);
            }
        }
#endif

        [Test]
        public void RenderGraphOnlyRenderer_HasNoLegacyFrameRoutingOrNativeRenderPassExecutor()
        {
            Type rendererType = typeof(ScriptableRenderer);

            Assert.That(rendererType.GetMethod(
                "ValidateRenderGraphPasses", k_InstanceMembers), Is.Null,
                "RenderGraph-only URP no longer keeps the compatibility pass validator.");

            Assert.That(rendererType.GetMethod(
                "SupportsRenderGraphForCurrentFrame",
                k_InstanceMembers,
                null,
                new[] { typeof(RenderingData).MakeByRefType() },
                null), Is.Null);

            foreach (string removedMethod in new[]
                     {
                         "ResetNativeRenderPassFrameData",
                         "SetupNativeRenderPassFrameData",
                         "ExecuteNativeRenderPass",
                         "SetNativeRenderPassMRTAttachmentList",
                         "SetNativeRenderPassAttachmentList",
                     })
            {
                Assert.That(rendererType.GetMethod(removedMethod, k_InstanceMembers), Is.Null,
                    $"Legacy NativeRenderPass entry point {removedMethod} must remain removed; " +
                    "RenderGraph owns native pass compilation and execution.");
            }

            MethodInfo renderGraphRecorder = rendererType.GetMethod(
                "RecordRenderGraph",
                k_InstanceMembers,
                null,
                new[] { typeof(RenderGraph), typeof(ScriptableRenderContext) },
                null);
            Assert.That(renderGraphRecorder, Is.Not.Null);
        }

        [Test]
        public void UniversalRenderPipelineAsset_PreservesUnity6TypeShape()
        {
            Type assetType = typeof(UniversalRenderPipelineAsset);
            Type baseType = assetType.BaseType;

            Assert.That(baseType, Is.Not.Null);
            Assert.That(baseType.IsGenericType, Is.True);
            Assert.That(baseType.GetGenericTypeDefinition(), Is.EqualTo(typeof(RenderPipelineAsset<>)));
            Assert.That(baseType.GetGenericArguments().Single(), Is.EqualTo(typeof(UniversalRenderPipeline)));

            Assert.That(typeof(IProbeVolumeEnabledRenderPipeline).IsAssignableFrom(assetType), Is.True);
            Assert.That(typeof(IGPUResidentRenderPipeline).IsAssignableFrom(assetType), Is.True);
            Assert.That(typeof(IRenderGraphEnabledRenderPipeline).IsAssignableFrom(assetType), Is.True);
            Assert.That(typeof(ISTPEnabledRenderPipeline).IsAssignableFrom(assetType), Is.True);
        }

        [Test]
        public void UniversalRenderPipelineAsset_PreservesFraxinusCapabilityApi()
        {
            Type assetType = typeof(UniversalRenderPipelineAsset);

            AssertPublicInstanceMethod(assetType, "IsGPUResidentDrawerSupportedBySRP",
                typeof(bool), typeof(bool));
            AssertPublicInstanceMethod(assetType, "GetGPUResidentDrawerCapabilityMessage", typeof(string));
        }

#if !UNITY_6000_0_OR_NEWER
        [Test]
        public void GlobalSettings_UsesActiveGraphicsSettingsContainer()
        {
            var settings = ScriptableObject.CreateInstance<UniversalRenderPipelineGlobalSettings>();
            try
            {
                settings.EnsureGraphicsSettings();
                Assert.That(RenderPipelineGraphicsSettingsCompatibility
                    .GetRenderPipelineSettings<ShaderStrippingSetting>(settings), Is.Not.Null);
                Assert.That(RenderPipelineGraphicsSettingsCompatibility
                    .GetRenderPipelineSettings<URPShaderStrippingSetting>(settings), Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

#endif

        [Test]
        public void CompatibilityEntryPoints_RemainAvailable()
        {
            Assert.That(typeof(ScriptableRenderer).GetMethod(
                "FinishRenderGraphRendering",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(CommandBuffer) },
                null), Is.Not.Null);

            MethodInfo shadowCull = typeof(ShadowCulling).GetMethod(
                "CullShadowCasters",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(ScriptableRenderContext).MakeByRefType(),
                    typeof(UniversalShadowData),
                    typeof(AdditionalLightsShadowAtlasLayout).MakeByRefType(),
                    typeof(CullingResults).MakeByRefType(),
                },
                null);
            Assert.That(shadowCull, Is.Not.Null);

            MethodInfo isSliceValid = typeof(URPLightShadowCullingInfos).GetMethod(
                "IsSliceValid", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(isSliceValid, Is.Not.Null);
            Assert.That(isSliceValid.GetParameters().Single().Name, Is.EqualTo("i"));

            MethodInfo defaultVolumeProfile = typeof(UniversalRenderPipelineGlobalSettings).GetMethod(
                "GetOrCreateDefaultVolumeProfile",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(defaultVolumeProfile, Is.Not.Null);
            Assert.That(defaultVolumeProfile.GetParameters().Single().Name,
                Is.EqualTo("defaultVolumeProfile"));

            PropertyInfo renderPath = typeof(UniversalRendererData)
                .GetProperty("renderPath", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(renderPath, Is.Not.Null);
            Assert.That(renderPath.PropertyType, Is.EqualTo(typeof(RenderPathCompatibility)));

            FieldInfo renderPathField = typeof(UniversalRendererData)
                .GetField("m_RenderPath", k_InstanceMembers);
            Assert.That(renderPathField, Is.Not.Null);
            Assert.That(renderPathField.IsDefined(typeof(SerializeField), false), Is.True);
        }

        [Test]
        public void RenderPassAndRenderer_RemoveCompatibilityAttachmentApis()
        {
            Type passType = typeof(ScriptableRenderPass);
            foreach (string removedRenderGraphCapability in new[]
                     {
                         "supportsRenderGraph",
                         "overridesContextContainerRecordRenderGraph",
                     })
            {
                Assert.That(passType.GetProperty(removedRenderGraphCapability, k_InstanceMembers), Is.Null,
                    removedRenderGraphCapability);
            }

            foreach (string obsoleteProperty in new[]
                     {
                         "colorAttachments", "colorAttachment", "legacyColorAttachment", "depthAttachment",
                         "colorAttachmentHandles", "colorAttachmentHandle", "depthAttachmentHandle",
                     })
            {
                Assert.That(passType.GetProperty(obsoleteProperty, k_InstanceMembers), Is.Null,
                    obsoleteProperty);
            }

            Assert.That(passType.GetMethod("ConfigureTarget", new[]
            {
                typeof(RenderTargetIdentifier), typeof(RenderTargetIdentifier)
            }), Is.Null);
            Assert.That(passType.GetMethod("ConfigureTarget", new[]
            {
                typeof(RenderTargetIdentifier[]), typeof(RenderTargetIdentifier)
            }), Is.Null);

            Type rendererType = typeof(ScriptableRenderer);
            Assert.That(rendererType.GetProperty("cameraColorTarget", k_InstanceMembers), Is.Null);
            Assert.That(rendererType.GetProperty("cameraDepthTarget", k_InstanceMembers), Is.Null);
            Assert.That(rendererType.GetProperty("cameraColorTargetHandle", k_InstanceMembers), Is.Null);
            Assert.That(rendererType.GetProperty("cameraDepthTargetHandle", k_InstanceMembers), Is.Null);
            Assert.That(rendererType.GetMethod("ConfigureCameraTarget", new[]
            {
                typeof(RenderTargetIdentifier), typeof(RenderTargetIdentifier)
            }), Is.Null);
        }

        [Test]
        public void Unity2022PublicSurface_UsesActiveUnity6ResourceFlow()
        {
            Type postProcessInterface =
                typeof(UnityEngine.Rendering.Universal.IPostProcessComponent);
            Assert.That(typeof(UnityEngine.Rendering.IPostProcessComponent)
                .IsAssignableFrom(postProcessInterface), Is.True);
            Assert.That(postProcessInterface.GetMethod("IsActive"), Is.Not.Null);
            Assert.That(postProcessInterface.GetMethod("IsTileCompatible"), Is.Null);
            Assert.That(postProcessInterface.IsAssignableFrom(typeof(Bloom)), Is.True);
            Assert.That(typeof(PostProcessingData).GetProperty(
                "allowPostProcessAlphaOutput", BindingFlags.Instance | BindingFlags.Public),
                Is.Not.Null);
            Assert.That(typeof(UniversalPostProcessingData).GetField(
                "allowPostProcessAlphaOutput", BindingFlags.Instance | BindingFlags.Public),
                Is.Not.Null);

            Assert.That(typeof(DrawSkyboxPass).GetField(
                "m_IsActiveTargetBackBuffer", BindingFlags.Instance | BindingFlags.Public),
                Is.Null,
                "DrawSkyboxPass must not retain compatibility-path backbuffer state.");

            Assert.That(ShaderKeywordStrings.UseRGBM, Is.EqualTo("_USE_RGBM"));
            Assert.That(ShaderKeywordStrings.FxaaAndGamma20, Is.EqualTo("_FXAA_AND_GAMMA_20"));
            Assert.That(ShaderKeywordStrings.UseDrawProcedural, Is.EqualTo("_USE_DRAW_PROCEDURAL"));

            foreach (string propertyName in new[]
                     {
                         "defaultUIMaterial",
                         "defaultUIOverdrawMaterial",
                         "defaultUIETC1SupportedMaterial",
                     })
            {
                Assert.That(typeof(UniversalRenderPipelineAsset).GetProperty(
                    propertyName, BindingFlags.Instance | BindingFlags.Public), Is.Not.Null,
                    propertyName);
            }

            Assert.That(typeof(ScriptableRenderer.RenderingFeatures).GetProperty(
                "cameraStacking", k_InstanceMembers), Is.Null);
        }

        [Test]
        public void RenderGraphRuntime_RemovesDeprecatedUrp14Contracts()
        {
            Assembly runtimeAssembly = typeof(UniversalRenderPipeline).Assembly;

            Assert.That(typeof(CameraData).GetField(
                "isStereoEnabled", BindingFlags.Instance | BindingFlags.Public), Is.Null);
            Assert.That(typeof(ShadowData).GetField(
                "requiresScreenSpaceShadowResolve", BindingFlags.Instance | BindingFlags.Public), Is.Null);
            Assert.That(typeof(UniversalRenderPipeline).GetMethod(
                "IsStereoEnabled", BindingFlags.Static | BindingFlags.Public), Is.Null);
            Assert.That(typeof(UniversalRenderPipeline).GetMethod(
                "RenderSingleCamera", BindingFlags.Static | BindingFlags.Public), Is.Null);
            Assert.That(typeof(RendererType).GetField(
                "ForwardRenderer", BindingFlags.Static | BindingFlags.Public), Is.Null);
            Assert.That(runtimeAssembly.GetType(
                "UnityEngine.Rendering.Universal.ForwardRenderer"), Is.Null);
            Assert.That(runtimeAssembly.GetType(
                "UnityEngine.Rendering.Universal.ForwardRendererData"), Is.Null);

            Type copyColorPass = runtimeAssembly.GetType(
                "UnityEngine.Rendering.Universal.Internal.CopyColorPass");
            Assert.That(copyColorPass?.GetConstructor(new[]
            {
                typeof(RenderPassEvent), typeof(Material), typeof(Material)
            }), Is.Not.Null);
            Assert.That(copyColorPass?.GetMethod("OnCameraCleanup", new[]
            {
                typeof(CommandBuffer)
            }), Is.Null);

            Type copyDepthPass = runtimeAssembly.GetType(
                "UnityEngine.Rendering.Universal.Internal.CopyDepthPass");
            Assert.That(copyDepthPass?.GetConstructor(new[]
            {
                typeof(RenderPassEvent), typeof(Material), typeof(bool), typeof(bool), typeof(bool)
            }), Is.Not.Null);

            Type drawObjectsPass = runtimeAssembly.GetType(
                "UnityEngine.Rendering.Universal.Internal.DrawObjectsPass");
            Assert.That(drawObjectsPass?.GetMethod(
                "OnExecute", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(CommandBuffer) }, null), Is.Null,
                "DrawObjectsPass must not retain its compatibility-mode execution helper.");

            Assert.That(runtimeAssembly.GetType("UnityEngine.Rendering.Universal.LibTessDotNet.Tess"), Is.Not.Null);
        }

        [Test]
        public void LegacyPixelPerfectNamespace_IsRemoved()
        {
            Assembly assembly = typeof(Renderer2DData).Assembly;
            Assert.That(assembly.GetType(
                "UnityEngine.Experimental.Rendering.Universal.PixelPerfectCamera"), Is.Null);
            Assert.That(typeof(PixelPerfectCamera).GetProperty(
                "cropFrame", BindingFlags.Instance | BindingFlags.Public), Is.Not.Null);
            Assert.That(typeof(PixelPerfectCamera).GetProperty(
                "gridSnapping", BindingFlags.Instance | BindingFlags.Public), Is.Not.Null);
        }

        [Test]
        public void Renderer2DData_PreservesFraxinusSerializedCompatibility()
        {
            Type rendererDataType = typeof(Renderer2DData);
            FieldInfo layerMaskField = rendererDataType.GetField("m_LayerMask", k_InstanceMembers);
            Assert.That(layerMaskField, Is.Not.Null);
            Assert.That(layerMaskField.FieldType, Is.EqualTo(typeof(LayerMask)));
            Assert.That(layerMaskField.IsDefined(typeof(SerializeField), false), Is.True);

            PropertyInfo layerMaskProperty = rendererDataType.GetProperty(
                "layerMask", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(layerMaskProperty, Is.Not.Null);
            Assert.That(layerMaskProperty.PropertyType, Is.EqualTo(typeof(LayerMask)));

            string[] legacyResourceFields =
            {
                "m_ShapeLightShader",
                "m_ShapeLightVolumeShader",
                "m_PointLightShader",
                "m_PointLightVolumeShader",
                "m_CoreBlitShader",
                "m_BlitHDROverlay",
                "m_CoreBlitColorAndDepthPS",
                "m_CopyDepthShader",
                "m_SamplingShader",
                "m_ProjectedShadowShader",
                "m_SpriteShadowShader",
                "m_SpriteUnshadowShader",
                "m_GeometryUnshadowShader",
                "m_FallbackErrorShader",
                "m_FallOffLookup",
            };

            foreach (string fieldName in legacyResourceFields)
            {
                FieldInfo field = rendererDataType.GetField(fieldName, k_InstanceMembers);
                Assert.That(field, Is.Not.Null, fieldName);
                Assert.That(field.IsDefined(typeof(SerializeField), false), Is.True, fieldName);
                Assert.That(field.IsDefined(typeof(HideInInspector), false), Is.True, fieldName);
            }
        }

        [Test]
        public void ShadowCaster2D_MigratesLegacyShadowMeshSettings()
        {
            Type casterType = typeof(ShadowCaster2D);
            string[] legacyFields =
            {
                "m_TrimEdge",
                "m_Mesh",
                "m_EdgeProcessing",
                "m_LocalBounds",
            };

            foreach (string fieldName in legacyFields)
            {
                FieldInfo field = casterType.GetField(fieldName, k_InstanceMembers);
                Assert.That(field, Is.Not.Null, fieldName);
                Assert.That(field.IsDefined(typeof(SerializeField), false), Is.True, fieldName);
                Assert.That(field.IsDefined(typeof(HideInInspector), false), Is.True, fieldName);
            }

            var gameObject = new GameObject("ShadowCaster2D legacy migration test");
            try
            {
                var caster = gameObject.AddComponent<ShadowCaster2D>();
                FieldInfo versionField = casterType.GetField("m_ComponentVersion", k_InstanceMembers);
                FieldInfo trimField = casterType.GetField("m_TrimEdge", k_InstanceMembers);
                FieldInfo edgeField = casterType.GetField("m_EdgeProcessing", k_InstanceMembers);
                FieldInfo shadowMeshField = casterType.GetField("m_ShadowMesh", k_InstanceMembers);

                Assert.That(versionField, Is.Not.Null);
                Assert.That(trimField, Is.Not.Null);
                Assert.That(edgeField, Is.Not.Null);
                Assert.That(shadowMeshField, Is.Not.Null);

                versionField.SetValue(caster, Enum.ToObject(versionField.FieldType, 2));
                trimField.SetValue(caster, 0.375f);
                edgeField.SetValue(caster, Enum.ToObject(edgeField.FieldType, 0));

                caster.OnAfterDeserialize();

                object shadowMesh = shadowMeshField.GetValue(caster);
                Assert.That(shadowMesh, Is.Not.Null);
                PropertyInfo trimProperty = shadowMesh.GetType().GetProperty(
                    "trimEdge", BindingFlags.Instance | BindingFlags.Public);
                PropertyInfo edgeProperty = shadowMesh.GetType().GetProperty(
                    "edgeProcessing", BindingFlags.Instance | BindingFlags.Public);
                Assert.That(trimProperty, Is.Not.Null);
                Assert.That(edgeProperty, Is.Not.Null);
                Assert.That((float)trimProperty.GetValue(shadowMesh), Is.EqualTo(0.375f));
                Assert.That(Convert.ToInt32(edgeProperty.GetValue(shadowMesh)), Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void RenderObjectsSettings_UseCurrentNamespaceAndDrawer()
        {
            Type drawerType = typeof(UniversalRenderPipelineAssetEditor).Assembly.GetType(
                "UnityEditor.Experimental.Rendering.Universal.RenderObjectsPassFeatureEditor");
            Assert.That(drawerType, Is.Not.Null);

            Type[] drawerTargets = CustomAttributeData.GetCustomAttributes(drawerType)
                .Where(data => data.AttributeType == typeof(UnityEditor.CustomPropertyDrawer))
                .Select(data => data.ConstructorArguments[0].Value as Type)
                .ToArray();

            Assert.That(drawerTargets, Does.Contain(
                typeof(UnityEngine.Rendering.Universal.RenderObjects.RenderObjectsSettings)));
            Assert.That(typeof(RenderObjects).Assembly.GetType(
                "UnityEngine.Experimental.Rendering.Universal.RenderObjects"), Is.Null);
        }

        [Test]
        public void SpeedTree9Upgrader_PreservesUnity6BaseContract()
        {
            Type upgraderType = typeof(UnityEditor.Rendering.Universal.UniversalRenderPipelineAssetEditor)
                .Assembly.GetType("UnityEditor.Rendering.Universal.UniversalSpeedTree9Upgrader");

            Assert.That(upgraderType, Is.Not.Null);
            Assert.That(upgraderType.BaseType, Is.Not.Null);
            Assert.That(upgraderType.BaseType.FullName,
                Is.EqualTo("UnityEditor.Rendering.SpeedTree9MaterialUpgrader"));
        }

        [Test]
        public void BuiltInRenderPasses_DoNotOverrideLegacyLifecycle()
        {
            string[] legacyLifecycleMethods =
            {
                "Execute",
                "Configure",
                "OnCameraSetup",
                "OnCameraCleanup",
                "OnFinishCameraStackRendering",
            };

            Type renderPassType = typeof(ScriptableRenderPass);
            Type[] concreteRenderPasses = renderPassType.Assembly.GetTypes()
                .Where(type => type != renderPassType &&
                               !type.IsAbstract &&
                               renderPassType.IsAssignableFrom(type))
                .ToArray();

            foreach (Type concreteRenderPass in concreteRenderPasses)
            {
                MethodInfo[] declaredOverrides = concreteRenderPass
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(method => method.IsVirtual &&
                                     legacyLifecycleMethods.Contains(method.Name) &&
                                     method.GetBaseDefinition().DeclaringType == renderPassType)
                    .ToArray();

                Assert.That(declaredOverrides, Is.Empty,
                    $"{concreteRenderPass.FullName} must record through RenderGraph only; " +
                    $"legacy override(s): {string.Join(", ", declaredOverrides.Select(method => method.Name))}.");
            }
        }

#if !UNITY_6000_0_OR_NEWER
        [TestCase(false)]
        [TestCase(true)]
        public void GlobalSettings_ActiveShaderStrippingSettingsStayScopedPerAsset(bool enabled)
        {
            var settings = ScriptableObject.CreateInstance<UniversalRenderPipelineGlobalSettings>();
            var otherSettings = ScriptableObject.CreateInstance<UniversalRenderPipelineGlobalSettings>();
            try
            {
                settings.EnsureGraphicsSettings();
                otherSettings.EnsureGraphicsSettings();
                var stripping = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ShaderStrippingSetting>(settings);
                var urpStripping = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<URPShaderStrippingSetting>(settings);
                var otherStripping = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ShaderStrippingSetting>(otherSettings);
                otherStripping.exportShaderVariants = !enabled;

                stripping.exportShaderVariants = enabled;
                stripping.stripRuntimeDebugShaders = enabled;
                stripping.shaderVariantLogLevel = UnityEngine.Rendering.ShaderVariantLogLevel.AllShaders;
                urpStripping.stripUnusedPostProcessingVariants = enabled;
                urpStripping.stripUnusedVariants = enabled;
                urpStripping.stripScreenCoordOverrideVariants = enabled;

                Assert.That(stripping.exportShaderVariants, Is.EqualTo(enabled));
                Assert.That(stripping.stripRuntimeDebugShaders, Is.EqualTo(enabled));
                Assert.That(stripping.shaderVariantLogLevel, Is.EqualTo(UnityEngine.Rendering.ShaderVariantLogLevel.AllShaders));
                Assert.That(urpStripping.stripUnusedPostProcessingVariants, Is.EqualTo(enabled));
                Assert.That(urpStripping.stripUnusedVariants, Is.EqualTo(enabled));
                Assert.That(urpStripping.stripScreenCoordOverrideVariants, Is.EqualTo(enabled));
                Assert.That(otherStripping.exportShaderVariants, Is.EqualTo(!enabled), "Editing one asset must not change another asset.");

                Assert.That(RenderPipelineGraphicsSettingsCompatibility
                    .GetRenderPipelineSettings<ShaderStrippingSetting>(settings), Is.SameAs(stripping));
                Assert.That(RenderPipelineGraphicsSettingsCompatibility
                    .GetRenderPipelineSettings<URPShaderStrippingSetting>(settings), Is.SameAs(urpStripping));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
                UnityEngine.Object.DestroyImmediate(otherSettings);
            }
        }

        [Test]
        public void MaterialDebugger_RenderingLayerFieldPreservesMaskAndColors()
        {
            var data = new DebugDisplaySettingsMaterial();
            using (var panel = ((IDebugDisplaySettingsData)data).CreatePanel())
            {
                DebugUI.RenderingLayerField FindField(System.Collections.Generic.IEnumerable<DebugUI.Widget> widgets)
                {
                    foreach (var widget in widgets)
                    {
                        if (widget is DebugUI.RenderingLayerField field)
                            return field;
                        if (widget is DebugUI.IContainer container)
                        {
                            var child = FindField(container.children);
                            if (child != null)
                                return child;
                        }
                    }
                    return null;
                }

                var layerField = FindField(panel.Widgets);
                Assert.That(layerField, Is.Not.Null);
                layerField.SetValue(0x80000001u);
                Assert.That(data.renderingLayerMask, Is.EqualTo(0x80000001u));
                data.renderingLayerMask = 0xFFFFFFFFu;
                Assert.That((uint)layerField.GetValue(), Is.EqualTo(0xFFFFFFFFu));
                var color = new Vector4(0.1f, 0.2f, 0.3f, 1f);
                layerField.setRenderingLayerColor(color, 31);
                Assert.That(data.debugRenderingLayersColors[31], Is.EqualTo(color));
                Assert.That(layerField.getRenderingLayerColor(31), Is.EqualTo(color));
                data.renderingLayersSelectedLight = true;
                Assert.That(layerField.isHiddenCallback(), Is.True);
            }
        }

        [Test]
        public void PipelineAsset_DefaultMaterialsWorkWithoutGlobalSettings()
        {
            var previousSettings = GraphicsSettings.GetSettingsForRenderPipeline<UniversalRenderPipeline>();
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            var asset = UniversalRenderPipelineAsset.Create(renderer);
            try
            {
                GraphicsSettings.UnregisterRenderPipelineSettings<UniversalRenderPipeline>();
                RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(null, typeof(UniversalRenderPipeline));
                string materialPath = UniversalRenderPipelineAsset.packagePath + "/Runtime/Materials/";
                Assert.That(asset.defaultMaterial, Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(materialPath + "Lit.mat")));
                Assert.That(asset.defaultParticleMaterial, Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(materialPath + "ParticlesUnlit.mat")));
                Assert.That(asset.defaultLineMaterial, Is.SameAs(asset.defaultParticleMaterial));
                Assert.That(asset.defaultTerrainMaterial, Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(materialPath + "TerrainLit.mat")));
                Assert.That(asset.decalMaterial, Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(materialPath + "Decal.mat")));
                Assert.That(asset.default2DMaterial, Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(materialPath + "Sprite-Unlit-Default.mat")));
            }
            finally
            {
                if (previousSettings != null)
                    GraphicsSettings.RegisterRenderPipelineSettings<UniversalRenderPipeline>(previousSettings);
                RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(previousSettings, typeof(UniversalRenderPipeline));
                UnityEngine.Object.DestroyImmediate(asset);
                UnityEngine.Object.DestroyImmediate(renderer);
            }
        }

#endif

        static void AssertPublicInstanceMethod(
            Type type,
            string name,
            Type returnType,
            params Type[] parameterTypes)
        {
            MethodInfo method = type.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public,
                null,
                parameterTypes,
                null);
            Assert.That(method, Is.Not.Null, $"Missing {type.FullName}.{name}.");
            Assert.That(method.ReturnType, Is.EqualTo(returnType));
        }

    }
}
