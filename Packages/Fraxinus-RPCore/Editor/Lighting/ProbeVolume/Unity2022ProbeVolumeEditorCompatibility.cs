#if !UNITY_6000_0_OR_NEWER
using UnityEditor;
using UnityEngine;

namespace UnityEngine.Rendering
{
    // Unity 6 hosts probe volumes inside LightingWindow through this tab contract. Unity
    // 2022 has no extensible LightingWindowTab, so keep the same tab code and
    // host it in a small EditorWindow instead of forking the probe volume inspector.
    internal abstract class LightingWindowTab
    {
        public GUIContent titleContent { get; protected set; }
        public int priority { get; protected set; }

        public virtual void OnEnable() { }
        public virtual void OnDisable() { }
        public abstract void OnGUI();
        public virtual bool HasHelpGUI() => false;
        public virtual void OnHeaderSettingsGUI() { }
        public virtual void OnBakeButtonGUI() { }
        public virtual void OnSummaryGUI() { }

        public virtual void FocusTab()
        {
            var window = EditorWindow.GetWindow<ProbeVolumeLightingWindow2022>("Probe Volumes");
            window.Focus();
        }
    }

    internal sealed class ProbeVolumeLightingWindow2022 : EditorWindow
    {
        ProbeVolumeLightingTab m_Tab;

        [MenuItem("Window/Rendering/Probe Volumes")]
        internal static void Open()
        {
            GetWindow<ProbeVolumeLightingWindow2022>("Probe Volumes");
        }

        void OnEnable()
        {
            m_Tab = new ProbeVolumeLightingTab();
            m_Tab.OnEnable();
            titleContent = m_Tab.titleContent;
            EditorApplication.update += RepaintWhileBaking;
        }

        void OnDisable()
        {
            EditorApplication.update -= RepaintWhileBaking;
            m_Tab?.OnDisable();
            m_Tab = null;
        }

        void RepaintWhileBaking()
        {
            if (ProbeVolumeBakingProvider.isRunning)
                Repaint();
        }

        void OnGUI()
        {
            if (m_Tab == null)
                OnEnable();

            m_Tab.RefreshBackendTitle();
            if (titleContent.text != m_Tab.titleContent.text)
                titleContent = m_Tab.titleContent;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(m_Tab.titleContent, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                m_Tab.OnHeaderSettingsGUI();
            }

            m_Tab.OnGUI();

            using (new EditorGUILayout.HorizontalScope())
            {
                m_Tab.OnSummaryGUI();
                GUILayout.FlexibleSpace();
                m_Tab.OnBakeButtonGUI();
            }
        }
    }
}
#endif
