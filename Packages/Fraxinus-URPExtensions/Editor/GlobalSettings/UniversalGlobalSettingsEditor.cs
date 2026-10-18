using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(UniversalRenderPipelineGlobalSettings))]
    sealed class UniversalGlobalSettingsEditor : Editor
    {
        SerializedUniversalRenderPipelineGlobalSettings m_SerializedGlobalSettings;
#if !UNITY_6000_0_OR_NEWER
        DefaultVolumeProfileEditor m_DefaultVolumeProfileEditor;
        VolumeProfile m_DefaultVolumeProfile;
        bool m_VolumeExpanded = true;
        bool m_DefaultProfileExpanded = true;
        bool m_AdaptiveProbeVolumesExpanded = true;
        bool m_ShaderStrippingExpanded = true;
        bool m_RenderGraphExpanded = true;
#endif

        void OnEnable()
        {
#if !UNITY_6000_0_OR_NEWER
            // Ensure the Unity 2022 compatibility container has been
            // populated before resolving managed-reference properties for the
            // inspector.  This is a no-op for an already initialized asset.
            if (target is UniversalRenderPipelineGlobalSettings globalSettings)
                globalSettings.EnsureGraphicsSettings();
            serializedObject.Update();
#endif
            m_SerializedGlobalSettings = new SerializedUniversalRenderPipelineGlobalSettings(serializedObject);
        }

        void OnDisable()
        {
#if !UNITY_6000_0_OR_NEWER
            DestroyDefaultVolumeProfileEditor();
#endif
        }

        public override void OnInspectorGUI()
        {
            var serialized = m_SerializedGlobalSettings;

            serialized.serializedObject.Update();

#if !UNITY_6000_0_OR_NEWER
            DrawUnity6StyleGraphicsSettings(serialized);
#else
            // In the quality window use more space for the labels.
            UniversalRenderPipelineGlobalSettingsUI.Inspector.Draw(serialized, this);
#endif

            serialized.serializedObject.ApplyModifiedProperties();
        }

#if !UNITY_6000_0_OR_NEWER
        void DrawUnity6StyleGraphicsSettings(SerializedUniversalRenderPipelineGlobalSettings serialized)
        {
            EditorGUILayout.HelpBox(
                "These are the settings that are currently active on the project.",
                MessageType.Info);

            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(220f, previousLabelWidth);

            try
            {
                DrawVolumeSettings(serialized);
                DrawLightingSettings(serialized);
                DrawShaderStrippingSettings(serialized);
                DrawRenderGraphSettings(serialized);
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }

            // Rendering Layers remains the URP-specific section from the
            // existing inspector, but follows the Unity 6 graphics-settings
            // categories so the page has the same information hierarchy.
            EditorGUILayout.Space(6f);
            UniversalRenderPipelineGlobalSettingsUI.Inspector.Draw(serialized, this);
        }

        void DrawVolumeSettings(SerializedUniversalRenderPipelineGlobalSettings serialized)
        {
            if (serialized.defaultVolumeProfileSettings == null)
                return;

            m_VolumeExpanded = CoreEditorUtils.DrawHeaderFoldout(
                EditorGUIUtility.TrTextContent("Volume"), m_VolumeExpanded);
            if (!m_VolumeExpanded)
                return;

            var profileProperty = serialized.defaultVolumeProfileSettings.FindPropertyRelative("m_VolumeProfile");
            if (profileProperty == null)
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                var profileRect = EditorGUILayout.GetControlRect();
                var foldoutRect = new Rect(profileRect.x, profileRect.y, 16f, profileRect.height);
                var fieldRect = profileRect;
                fieldRect.xMin += 16f;
                m_DefaultProfileExpanded = EditorGUI.Foldout(
                    foldoutRect, m_DefaultProfileExpanded, GUIContent.none, true);
                EditorGUI.PropertyField(
                    fieldRect,
                    profileProperty,
                    EditorGUIUtility.TrTextContent("Default Profile"));

                var profile = profileProperty.objectReferenceValue as VolumeProfile;
                if (profile == null)
                    return;

                if (m_DefaultProfileExpanded)
                {
                    EditorGUILayout.HelpBox(
                        "The values in the Default Volume can be overridden by a Volume Profile assigned to the URP asset and Volumes inside scenes.",
                        MessageType.Info);

                    if (VolumeManager.instance == null || !VolumeManager.instance.isInitialized)
                    {
                        EditorGUILayout.HelpBox(
                            "Volume components are available after the active render pipeline has initialized.",
                            MessageType.Info);
                        return;
                    }

                    if (profile == VolumeManager.instance.globalDefaultProfile)
                        VolumeProfileUtils.EnsureAllOverridesForDefaultProfile(profile);

                    if (m_DefaultVolumeProfileEditor == null || m_DefaultVolumeProfile != profile)
                    {
                        DestroyDefaultVolumeProfileEditor();
                        m_DefaultVolumeProfile = profile;
                        m_DefaultVolumeProfileEditor = new DefaultVolumeProfileEditor(
                            profile,
                            serialized.serializedObject);
                    }

                    m_DefaultVolumeProfileEditor.OnGUI();
                }
            }
        }

        void DrawLightingSettings(SerializedUniversalRenderPipelineGlobalSettings serialized)
        {
            if (serialized.probeVolumeGlobalSettings == null)
                return;

            EditorGUILayout.LabelField(EditorGUIUtility.TrTextContent("Lighting"), EditorStyles.boldLabel);
            m_AdaptiveProbeVolumesExpanded = CoreEditorUtils.DrawHeaderFoldout(
                EditorGUIUtility.TrTextContent("Adaptive Probe Volumes Options"),
                m_AdaptiveProbeVolumesExpanded);
            if (!m_AdaptiveProbeVolumesExpanded)
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                var disableStreaming = serialized.probeVolumeGlobalSettings
                    .FindPropertyRelative("m_ProbeVolumeDisableStreamingAssets");
                if (disableStreaming != null)
                {
                    EditorGUILayout.PropertyField(
                        disableStreaming,
                        EditorGUIUtility.TrTextContent("Probe Volume Disable Streaming Assets"));
                }
            }
        }

        void DrawShaderStrippingSettings(SerializedUniversalRenderPipelineGlobalSettings serialized)
        {
            m_ShaderStrippingExpanded = CoreEditorUtils.DrawHeaderFoldout(
                EditorGUIUtility.TrTextContent("Additional Shader Stripping Settings"),
                m_ShaderStrippingExpanded);
            if (!m_ShaderStrippingExpanded)
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(
                    serialized.exportShaderVariants,
                    UniversalRenderPipelineGlobalSettingsUI.Styles.exportShaderVariantsLabel);
                EditorGUILayout.PropertyField(
                    serialized.shaderVariantLogLevel,
                    UniversalRenderPipelineGlobalSettingsUI.Styles.shaderVariantLogLevelLabel);
                EditorGUILayout.PropertyField(
                    serialized.stripDebugVariants,
                    UniversalRenderPipelineGlobalSettingsUI.Styles.stripDebugVariantsLabel);
                EditorGUILayout.PropertyField(
                    serialized.stripUnusedPostProcessingVariants,
                    UniversalRenderPipelineGlobalSettingsUI.Styles.stripUnusedPostProcessingVariantsLabel);
                EditorGUILayout.PropertyField(
                    serialized.stripUnusedVariants,
                    UniversalRenderPipelineGlobalSettingsUI.Styles.stripUnusedVariantsLabel);
                EditorGUILayout.PropertyField(
                    serialized.stripScreenCoordOverrideVariants,
                    UniversalRenderPipelineGlobalSettingsUI.Styles.stripScreenCoordOverrideVariants);
            }
        }

        void DrawRenderGraphSettings(SerializedUniversalRenderPipelineGlobalSettings serialized)
        {
            if (serialized.renderGraphGlobalSettings == null)
                return;

            m_RenderGraphExpanded = CoreEditorUtils.DrawHeaderFoldout(
                EditorGUIUtility.TrTextContent("Render Graph"), m_RenderGraphExpanded);
            if (!m_RenderGraphExpanded)
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                var compilationCaching = serialized.renderGraphGlobalSettings
                    .FindPropertyRelative("m_EnableCompilationCaching");
                if (compilationCaching != null)
                {
                    EditorGUILayout.PropertyField(
                        compilationCaching,
                        EditorGUIUtility.TrTextContent("Enable Compilation Caching"));
                }

                var validityChecks = serialized.renderGraphGlobalSettings
                    .FindPropertyRelative("m_EnableValidityChecks");
                if (validityChecks != null)
                {
                    EditorGUILayout.PropertyField(
                        validityChecks,
                        EditorGUIUtility.TrTextContent("Enable Validity Checks"));
                }
            }
        }

        void DestroyDefaultVolumeProfileEditor()
        {
            if (m_DefaultVolumeProfileEditor != null)
                m_DefaultVolumeProfileEditor.Destroy();

            m_DefaultVolumeProfileEditor = null;
            m_DefaultVolumeProfile = null;
        }
#endif
    }
}
