using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    class UniversalGlobalSettingsPanelProvider : SettingsProvider
    {
        Editor m_Editor;

        static UniversalRenderPipelineGlobalSettings currentSettings =>
            GraphicsSettings.GetSettingsForRenderPipeline<UniversalRenderPipeline>() as UniversalRenderPipelineGlobalSettings;

        public UniversalGlobalSettingsPanelProvider()
            : base("Project/Graphics/URP Global Settings", SettingsScope.Project)
        {
            keywords = GetSearchKeywordsFromGUIContentProperties<UniversalRenderPipelineGlobalSettingsUI.Styles>().ToArray();
        }

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider() => new UniversalGlobalSettingsPanelProvider();

        public override void OnTitleBarGUI()
        {
            if (GUILayout.Button(CoreEditorStyles.iconHelp, CoreEditorStyles.iconHelpStyle))
                Help.BrowseURL(Help.GetHelpURLForObject(currentSettings));
        }

        public override void OnGUI(string searchContext)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.inspectorDefaultMargins))
            {
                var settings = currentSettings;
                if (settings == null)
                {
                    CoreEditorUtils.DrawFixMeBox("Select a valid URP Global Settings asset.",
                        () => UniversalRenderPipelineGlobalSettings.Ensure());
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    var selected = (UniversalRenderPipelineGlobalSettings)EditorGUILayout.ObjectField(
                        settings, typeof(UniversalRenderPipelineGlobalSettings), false);
                    if (selected != null && selected != settings)
                    {
#if UNITY_6000_0_OR_NEWER
                        EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>(selected);
#else
                        GraphicsSettings.RegisterRenderPipelineSettings<UniversalRenderPipeline>(selected);
                        selected.EnsureGraphicsSettings();
                        RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(
                            selected, typeof(UniversalRenderPipeline));
#endif
                    }

                    if (GUILayout.Button("New", GUILayout.Width(45)))
                        UniversalGlobalSettingsCreator.Create(useProjectSettingsFolder: true, activateAsset: true);
                    if (GUILayout.Button("Clone", GUILayout.Width(45)))
                        UniversalGlobalSettingsCreator.Clone(settings, activateAsset: true);
                }

                if (RenderPipelineManager.currentPipeline != null &&
                    !(RenderPipelineManager.currentPipeline is UniversalRenderPipeline))
                {
                    EditorGUILayout.HelpBox("URP is not the active Render Pipeline. Check Graphics and Quality settings.",
                        MessageType.Warning);
                }

                Editor.CreateCachedEditor(currentSettings, null, ref m_Editor);
                if (m_Editor != null)
                    m_Editor.OnInspectorGUI();
            }
        }

        public override void OnDeactivate()
        {
            CoreUtils.Destroy(m_Editor);
            m_Editor = null;
            base.OnDeactivate();
        }
    }
}
