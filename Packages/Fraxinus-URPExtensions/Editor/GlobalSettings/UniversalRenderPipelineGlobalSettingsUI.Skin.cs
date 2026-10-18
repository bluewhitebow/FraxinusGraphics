using UnityEngine;

namespace UnityEditor.Rendering.Universal
{
    internal partial class UniversalRenderPipelineGlobalSettingsUI
    {
        internal class Styles
        {
            private const float k_shaderStrippingLabelWidth = 250f;
            internal static float shaderStrippingLabelWidth => k_shaderStrippingLabelWidth;
            public static readonly GUIContent shaderStrippingSettingsLabel = EditorGUIUtility.TrTextContent("Shader Stripping", "Shader Stripping settings");
            public static readonly GUIContent shaderVariantLogLevelLabel = EditorGUIUtility.TrTextContent("Shader Variant Log Level", "Controls the level of logging of shader variant information outputted during the build process. Information appears in the Unity Console when the build finishes.");
            public static readonly GUIContent exportShaderVariantsLabel = EditorGUIUtility.TrTextContent("Export Shader Variants", "Controls whether to output shader variant information to a file.");
            public static readonly GUIContent renderingLayersLabel = EditorGUIUtility.TrTextContent("Rendering Layers (3D)", "The list of rendering layer names. When using decals, increasing the layer count inscreases the required memory bandwidth and decreases the performance.");
            public static readonly GUIContent stripDebugVariantsLabel = EditorGUIUtility.TrTextContent("Strip Runtime Debug Shaders", "When enabled, all debug display shader variants are removed when you build for the Unity Player. This decreases build time, but prevents the use of Rendering Debugger in Player builds.");
            public static readonly GUIContent stripUnusedPostProcessingVariantsLabel = EditorGUIUtility.TrTextContent("Strip Unused Post Processing Variants", "Controls whether strips automatically post processing shader variants based on VolumeProfile components. It strips based on VolumeProfiles in project and not scenes that actually uses it.");
            public static readonly GUIContent stripUnusedVariantsLabel = EditorGUIUtility.TrTextContent("Strip Unused Variants", "Controls whether strip disabled keyword variants if the feature is enabled.");
            public static readonly GUIContent stripScreenCoordOverrideVariants = EditorGUIUtility.TrTextContent("Strip Screen Coord Override Variants", "Controls whether Screen Coordinates Override shader variants are stripped.");
        }
    }
}
