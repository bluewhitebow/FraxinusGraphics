using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal
{
    internal static class NewPostProcessTemplateDropdownItems
    {
        const string k_FeatureTemplatePath =
            "Packages/com.unity.render-pipelines.universal/Editor/RendererFeatures/NewPostProcessRendererFeature.cs.txt";

        const string k_VolumeTemplatePath =
            "Packages/com.unity.render-pipelines.universal/Editor/RendererFeatures/NewPostProcessVolumeComponent.cs.txt";

        static string PreprocessScriptTemplate(string content, string featureType = null, string volumeType = null, string displayName = null)
        {
            if(featureType != null)
                content = content.Replace("#FEATURE_TYPE#", featureType);

            if(volumeType != null)
                content = content.Replace("#VOLUME_TYPE#", volumeType);

            if(displayName != null)
                content = content.Replace("#DISPLAY_NAME#", displayName);

            return content;
        }

        static Object CreateScriptAssetFromTemplate(string templatePath, string targetPath, string featureType = null, string volumeType = null, string displayName = null)
        {
            string content = File.ReadAllText(templatePath);
            content = PreprocessScriptTemplate(content, featureType, volumeType, displayName);
#if UNITY_6000_0_OR_NEWER
            return ProjectWindowUtil.CreateScriptAssetWithContent(targetPath, content);
#else
            // Unity 2022 has no CreateScriptAssetWithContent helper. Write and import
            // the already-expanded template while retaining the same two-asset action.
            string assetPath = targetPath.Replace('\\', '/');
            File.WriteAllText(assetPath, content);
            AssetDatabase.ImportAsset(assetPath);
            return AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);
#endif
        }

        internal class CreateCombinedScriptTemplateAssetsAction : ProjectWindowCallback.EndNameEditAction
        {
            public override void Action(int instanceId, string userPath, string resourceFile)
            {
                string directoryPath = Path.GetDirectoryName(userPath);
                string enteredName = Path.GetFileNameWithoutExtension(userPath);
                string cleanedEnteredNamed = enteredName.Replace(" ", "");

                string featureTypeName = cleanedEnteredNamed + "RendererFeature";
                string volumeTypeName = cleanedEnteredNamed + nameof(VolumeComponent);

                Object createdFeature = null;
                string featurePath = Path.Combine(directoryPath, featureTypeName + ".cs")
                    .Replace('\\', '/');
                try
                {
                    AssetDatabase.StartAssetEditing();

                    createdFeature = CreateScriptAssetFromTemplate(k_FeatureTemplatePath,
                        featurePath, featureTypeName, volumeTypeName, null);
                    CreateScriptAssetFromTemplate(k_VolumeTemplatePath,
                        Path.Combine(directoryPath, volumeTypeName + ".cs"), featureTypeName, volumeTypeName,
                        enteredName);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                if (createdFeature == null)
                    createdFeature = AssetDatabase.LoadAssetAtPath<MonoScript>(featurePath);
                ProjectWindowUtil.ShowCreatedAsset(createdFeature);
            }
        }

        [MenuItem("Assets/Create/Rendering/URP Post-processing Effect (Renderer Feature with Volume)", priority = CoreUtils.Sections.section4 + CoreUtils.Priorities.assetsCreateRenderingMenuPriority)]
        static void MenuCreateCustomPostProcessVolumeRendererFeature()
        {
            Texture2D icon = EditorGUIUtility.IconContent("cs Script Icon").image as Texture2D;

            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(0,
                ScriptableObject.CreateInstance<CreateCombinedScriptTemplateAssetsAction>(), "NewPostProcessEffect.cs", icon, k_FeatureTemplatePath);
        }
    }
}
