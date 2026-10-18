using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEditorInternal;

namespace UnityEditor.Rendering.Universal
{
    class SerializedUniversalRenderPipelineGlobalSettings
    {
        public SerializedObject serializedObject { get; }
        public SerializedProperty shaderVariantLogLevel { get; }
        public SerializedProperty exportShaderVariants { get; }

        private List<UniversalRenderPipelineGlobalSettings> serializedSettings = new List<UniversalRenderPipelineGlobalSettings>();
        public SerializedProperty renderingLayerNames;
        public SerializedProperty stripDebugVariants;
        public SerializedProperty stripUnusedPostProcessingVariants;
        public SerializedProperty stripUnusedVariants;
        public SerializedProperty stripScreenCoordOverrideVariants;
        public SerializedProperty defaultVolumeProfileSettings;
        public SerializedProperty probeVolumeGlobalSettings;
        public SerializedProperty renderGraphGlobalSettings;
        public ReorderableList renderingLayerNameList;

        public SerializedUniversalRenderPipelineGlobalSettings(SerializedObject serializedObject)
        {
            this.serializedObject = serializedObject;

            // do the cast only once
            foreach (var currentSetting in serializedObject.targetObjects)
            {
                if (currentSetting is UniversalRenderPipelineGlobalSettings urpSettings)
                    serializedSettings.Add(urpSettings);
                else
                    throw new System.Exception($"Target object has an invalid object, objects must be of type {typeof(UniversalRenderPipelineGlobalSettings)}");
            }

#if UNITY_6000_0_OR_NEWER
            renderingLayerNames = serializedObject.FindProperty("m_RenderingLayerNames");
            stripDebugVariants = serializedObject.FindProperty("m_StripDebugVariants");
            stripUnusedPostProcessingVariants = serializedObject.FindProperty("m_StripUnusedPostProcessingVariants");
            stripUnusedVariants = serializedObject.FindProperty("m_StripUnusedVariants");
            shaderVariantLogLevel = serializedObject.FindProperty("m_ShaderVariantLogLevel");
            exportShaderVariants = serializedObject.FindProperty("m_ExportShaderVariants");
            stripScreenCoordOverrideVariants = serializedObject.FindProperty("m_StripScreenCoordOverrideVariants");
#else
            renderingLayerNames = serializedObject.FindProperty("m_RenderingLayerNamesCompatibility");

            // In the Unity 2022 backport the settings are stored as
            // SerializeReference entries inside the global asset.  Resolve
            // the concrete entries so the existing URP inspector edits the
            // same objects consumed by ShaderBuildPreprocessor rather than
            // silently changing only the legacy migration fields.
            var settingsContainer = serializedObject.FindProperty("m_Settings");
            var shaderStrippingSetting = FindGraphicsSettingProperty(settingsContainer, typeof(ShaderStrippingSetting));
            var urpShaderStrippingSetting = FindGraphicsSettingProperty(settingsContainer, typeof(URPShaderStrippingSetting));
            defaultVolumeProfileSettings = FindGraphicsSettingProperty(settingsContainer, typeof(URPDefaultVolumeProfileSettings));
            probeVolumeGlobalSettings = FindGraphicsSettingProperty(settingsContainer, typeof(ProbeVolumeGlobalSettings));
            renderGraphGlobalSettings = FindGraphicsSettingProperty(settingsContainer, typeof(RenderGraphGlobalSettings));

            if (shaderStrippingSetting != null)
            {
                shaderVariantLogLevel = shaderStrippingSetting.FindPropertyRelative("m_ShaderVariantLogLevel");
                exportShaderVariants = shaderStrippingSetting.FindPropertyRelative("m_ExportShaderVariants");
                stripDebugVariants = shaderStrippingSetting.FindPropertyRelative("m_StripRuntimeDebugShaders");
            }

            if (urpShaderStrippingSetting != null)
            {
                stripUnusedPostProcessingVariants = urpShaderStrippingSetting.FindPropertyRelative("m_StripUnusedPostProcessingVariants");
                stripUnusedVariants = urpShaderStrippingSetting.FindPropertyRelative("m_StripUnusedVariants");
                stripScreenCoordOverrideVariants = urpShaderStrippingSetting.FindPropertyRelative("m_StripScreenCoordOverrideVariants");
            }

#endif

            renderingLayerNameList = new ReorderableList(serializedObject, renderingLayerNames, false, false, true, true)
            {
                drawElementCallback = OnDrawElement,
                onCanRemoveCallback = (ReorderableList list) => list.IsSelected(list.count - 1) && !list.IsSelected(0),
                onCanAddCallback = (ReorderableList list) => list.count < 32,
                onAddCallback = OnAddElement,
            };
        }

#if !UNITY_6000_0_OR_NEWER
        static SerializedProperty FindGraphicsSettingProperty(SerializedProperty settingsContainer, Type settingType)
        {
            if (settingsContainer == null || settingType == null)
                return null;

            var collection = settingsContainer.FindPropertyRelative("m_SettingsList");
            var entries = collection?.FindPropertyRelative("m_Settings");
            if (entries == null || !entries.isArray)
                return null;

            for (int i = 0; i < entries.arraySize; ++i)
            {
                var entry = entries.GetArrayElementAtIndex(i);
                if (ManagedReferenceIs(entry, settingType))
                    return entry;
            }

            return null;
        }

        static bool ManagedReferenceIs(SerializedProperty property, Type settingType)
        {
            if (property == null)
                return false;

            return settingType.IsInstanceOfType(property.managedReferenceValue);
        }
#endif

        void OnAddElement(ReorderableList list)
        {
            int index = list.count;
            list.serializedProperty.arraySize = list.count + 1;
            list.serializedProperty.GetArrayElementAtIndex(index).stringValue = GetDefaultLayerName(index);
        }

        void OnDrawElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            // For some reason given rect is not centered
            rect.y += 2.5f;

            SerializedProperty element = renderingLayerNameList.serializedProperty.GetArrayElementAtIndex(index);

            EditorGUI.PropertyField(rect, element, EditorGUIUtility.TrTextContent($"Layer {index}"), true);

            if (element.stringValue == "")
            {
                element.stringValue = GetDefaultLayerName(index);
                serializedObject.ApplyModifiedProperties();
            }
        }

        string GetDefaultLayerName(int index)
        {
            return index == 0 ? "Default" : $"Layer {index}";
        }
    }
}
