using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering
{
    class ProbeVolumeLightingTab : LightingWindowTab
    {
        const int k_AddSceneID = 654423;
        const string k_APRTBackendId = "APRT";
        internal static bool aprtSelected => ProbeReferenceVolumeProvider.activeBackend?.id == k_APRTBackendId;

        static string documentationURL = "probevolumes";

        static class Styles
        {
            public static readonly GUIContent helpIcon = EditorGUIUtility.IconContent("_Help");
            public static readonly GUIContent settingsIcon = EditorGUIUtility.IconContent("_Popup");
            public static readonly GUIContent bakingTitle = new GUIContent("Baking");

            public static readonly GUIContent bakingMode = new GUIContent("Baking Mode", "In Single Scene mode, only the Active Scene will be baked. Probe Volumes in other Scenes will be ignored.");
            public static readonly GUIContent currentBakingSet = new GUIContent("Current Baking Set");
            public static readonly GUIContent scenesInSet = new GUIContent("Scenes in Baking Set");
            public static readonly GUIContent addLoadedScenes = new GUIContent("Add Loaded Scenes");
            public static readonly GUIContent toggleBakeAll = new GUIContent("Toggle All");
            public static readonly GUIContent toggleBakeNone = new GUIContent("Toggle None");
            public static readonly GUIContent status = new GUIContent("Status", "Unloaded scenes will not be considered when generating lighting data.");
            public static readonly GUIContent bake = new GUIContent("Bake", "Scenes loaded but not selected for Baking will contribute to lighting but baked data will not be regenerated for these scenes.");
            public static readonly GUIContent bakeBox = new GUIContent("", "Controls whether this scene is included when baking probe lighting.");
            public static readonly GUIContent warnings = new GUIContent("Warnings");

            public static readonly string[] bakingModeOptions = new string[] { "Single Scene", "Baking Set" };

            public static readonly GUIContent iconEnableAll = new GUIContent("", CoreEditorStyles.GetMessageTypeIcon(MessageType.Info), "The Scene is loaded but is currently not enabled for Baking. It will therefore not be considered when generating lighting data.");
            public static readonly GUIContent iconLoadForBake = new GUIContent("", CoreEditorStyles.GetMessageTypeIcon(MessageType.Warning), "The Scene is currently enabled for baking but is unloaded in the Hierarchy. This may result in incomplete lighting data being generated.\nLoad the Scene in the Hierarchy, or use the shortcuts below to fix the issue.");

            public static readonly string msgEnableAll = "Some loaded Scenes are disabled by this Baking Set. These Scenes will not contribute to the generation of probe data.";
            public static readonly string msgUnloadOther = "Scene(s) not belonging to this Baking Set are currently loaded in the Hierarchy. This might result in incorrect lighting.";
            public static readonly string msgLoadForBake = "Some scene(s) in this Baking Set are not currently loaded in the Hierarchy. This might result in missing or incomplete lighting.";

            public const float statusLabelWidth = 80;

            // Summary
            public static readonly GUIContent scenarioCostStat = new GUIContent("Scenario Size", "Size of the current Scenario's lighting data.");
            public static readonly GUIContent totalCostStat = new GUIContent("Baking Set Size", "Size of the lighting data for all Scenarios in this Baking Set.");

            // Bake Button
            public static readonly GUIContent generateProbes = new GUIContent("Bake Probe Volumes", "Calculate probe positions and generate lighting data for Probe Volumes.");
            public static readonly GUIContent generateAPRT = new GUIContent("Bake APRT", "Capture geometry transport and bake the active Lighting Scenario for all scenes in the selected Baking Set.");
            public static readonly GUIContent cancelBake = new GUIContent("Cancel", "Cancel current Probe Volumes baking task.");
            public static readonly string[] bakeOptionsText = { "Bake Probe Volumes", "Clear APV Data" };

            public static readonly GUIStyle buttonStyle = "LargeButton";
            public const float lightingButtonWidth = 170;

            // Font styles
            public static readonly GUIStyle inspectorTitle = "IN Title";
            public static readonly GUIStyle labelFont = new GUIStyle(EditorStyles.label);
        }

        internal enum Expandable
        {
            Baking = 1 << 0,
            BakingWarnings = 1 << 1,
            Scenarios = 1 << 2,
            Placement = 1 << 3,
            PlacementFilters = 1 << 4,
        };

        static readonly Expandable k_ExpandableDefault = Expandable.Baking | Expandable.BakingWarnings | Expandable.Scenarios | Expandable.Placement;
        static ExpandedState<Expandable, ProbeVolumeLightingTab> k_Foldouts;

        // This set is used to draw a read only inspector in case no other set has been created
        // It's content must not be changed
        static ProbeVolumeBakingSet s_DefaultSet;
        static ProbeVolumeBakingSet defaultSet
        {
            get
            {
                if (s_DefaultSet == null)
                {
                    s_DefaultSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                    s_DefaultSet.hideFlags = HideFlags.NotEditable | HideFlags.HideAndDontSave;
                    s_DefaultSet.SetDefaults();
                }
                return s_DefaultSet;
            }
        }

        public static ProbeVolumeLightingTab instance;

        public static bool singleSceneMode => instance?.m_SingleSceneMode ?? true;
        internal static ProbeVolumeBakingSet displayedBakingSet => instance?.activeSet;
        internal static Scene displayedScene => instance?.uiScene ?? SceneManager.GetActiveScene();

        // Inspector baking can persist the temporary set displayed here.
        // Once it is an asset the tab must no longer own/destroy it as a temp.
        internal static void NotifyBakingSetSaved(ProbeVolumeBakingSet bakingSet)
        {
            if (instance != null && instance.activeSet == bakingSet)
                instance.m_TempBakingSet = false;
        }


        Vector2 m_ScrollPosition = Vector2.zero;
        bool m_SingleSceneMode = true;
        Scene m_InspectorScene;
        Scene uiScene => m_InspectorScene.IsValid() && m_InspectorScene.isLoaded ? m_InspectorScene : SceneManager.GetActiveScene();
        bool m_TempBakingSet = false;
        bool m_Initialized = false;

        ProbeVolumeBakingSetWeakReference m_ActiveSet = new();
        ProbeVolumeBakingSet activeSet
        {
            get => m_ActiveSet.Get();
            set
            {
                if (ReferenceEquals(m_ActiveSet.Get(), value)) return;
                m_InspectorScene = default;
                if (m_TempBakingSet) Object.DestroyImmediate(m_ActiveSet.Get());
                m_ActiveSet.Set(value);
                m_TempBakingSet = false;
                if (m_ActiveSet.Get() == null) return;
                m_SingleSceneMode = m_ActiveSet.Get().singleSceneMode;
                InitializeSceneList();
            }
        }

        Editor m_ActiveSetEditor;
        ProbeVolumeBakingSetEditor activeSetEditor
        {
            get
            {
                var set = activeSet != null ? activeSet : defaultSet;
                Editor.CreateCachedEditor(set, typeof(ProbeVolumeBakingSetEditor), ref m_ActiveSetEditor);
                return (ProbeVolumeBakingSetEditor)m_ActiveSetEditor;
            }
        }

        public override void OnEnable()
        {
            instance = this;
            RefreshBackendTitle();
            priority = 1;

            RefreshSceneAssets();
        }

        internal void RefreshBackendTitle()
        {
            var title = ProbeVolumeBakingProvider.backend?.displayName ?? "Probe Volumes";
            if (titleContent == null || titleContent.text != title)
                titleContent = new GUIContent(title);
        }

        bool FindActiveSet()
        {
            if (m_TempBakingSet)
            {
                var registeredSet = ProbeVolumeBakingSet.GetBakingSetForScene(uiScene);
                if (registeredSet != null && EditorUtility.IsPersistent(registeredSet))
                    activeSet = registeredSet;
            }

            if (m_ActiveSet.Get() == null)
            {
                activeSet = ProbeVolumeBakingSet.GetBakingSetForScene(SceneManager.GetActiveScene());
                for (int i = 0; activeSet == null && i < SceneManager.sceneCount; i++)
                    activeSet = ProbeVolumeBakingSet.GetBakingSetForScene(SceneManager.GetSceneAt(i));
            }

            return m_ActiveSet.Get() != null;
        }

        void Initialize()
        {
            if (!ProbeReferenceVolumeProvider.instance.isInitialized || !ProbeReferenceVolumeProvider.instance.enabledBySRP)
            {
                ProbeVolumeBakingProvider.backend?.DrawDisabledHelpBox();
                EditorGUILayout.Space();
                return;
            }

            ProbeVolumeBakingProvider.backend?.DrawVolumeWarnings();

            FindActiveSet();

            if (m_Initialized)
                return;

            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;

            m_Initialized = true;
        }

        public override void OnDisable()
        {
            if (instance == this)
                instance = null;

            if (m_ActiveSetEditor != null)
                Object.DestroyImmediate(m_ActiveSetEditor);

            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;

            // We keep allocated acceleration structures while the Lighting window is open in order to make subsequent bakes faster, but when the window closes we dispose of them
            // Unless a bake is running, in which case we leave disposing to CleanBakeData()
            // The standalone provider owns these resources. Querying native
            // Lightmapping here can access a destroyed LightmapSettings manager
            // when Unity disables the window during editor shutdown.
            if (!ProbeVolumeBakingProvider.isRunning)
                ProbeVolumeBakingProvider.DisposeActive();
        }

        #region On GUI
        public override void OnGUI()
        {
            RefreshBackendTitle();
            EditorGUIUtility.hierarchyMode = true;

            Initialize();

            var prv = ProbeReferenceVolumeProvider.instance;

            // In single scene mode, user can't control active set, so we automatically create a new one
            // in case the active scene doesn't have a baking set so that we can display baking settings
            // Clone the current activeSet if possible so that it's seamless when eg. duplicating a scene
            if (activeSet != null && m_SingleSceneMode)
            {
                var activeScene = uiScene;
                var set = ProbeVolumeBakingSet.GetBakingSetForScene(activeScene);
                if (set == null)
                    UseTemporaryBakingSet(activeScene.GetGUID(), activeSet ? activeSet.Clone() : null);
            }

            // Not sure how we can get to that state but we can
            if (m_TempBakingSet && activeSet == null)
                FindActiveSet();

            using (new EditorGUI.DisabledScope(!prv.isInitialized || !prv.enabledBySRP))
            {
                m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);

                if (Foldout(Styles.bakingTitle, Expandable.Baking, true))
                {
                    EditorGUI.indentLevel++;
                    BakingGUI();
                    EditorGUI.indentLevel--;
                    EditorGUILayout.Space();
                }

                using (new EditorGUI.DisabledScope(activeSet == null && !m_TempBakingSet))
                    activeSetEditor.OnInspectorGUI();

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space();
        }

        public override bool HasHelpGUI()
        {
            return true;
        }

        public override void OnHeaderSettingsGUI()
        {
            if (aprtSelected)
                return;

            var iconSize = EditorStyles.iconButton.CalcSize(Styles.helpIcon);
            if (GUI.Button(GUILayoutUtility.GetRect(iconSize.x, iconSize.y), Styles.helpIcon, EditorStyles.iconButton))
                Help.BrowseURL(DocumentationInfo.GetPageLink("com.unity.render-pipelines.high-definition", documentationURL));

            iconSize = EditorStyles.iconButton.CalcSize(Styles.settingsIcon);
            var rect = GUILayoutUtility.GetRect(iconSize.x, iconSize.y);
            if (EditorGUI.DropdownButton(rect, Styles.settingsIcon, FocusType.Passive, EditorStyles.iconButton))
                EditorUtility.DisplayCustomMenu(rect, new[] { EditorGUIUtility.TrTextContent("Open Rendering Debugger") }, -1, OpenProbeDebugPanel, null);
        }
        internal static void OpenProbeDebugPanel(object userData, string[] options, int selected) => ProbeVolumeBakingProvider.backend?.OpenDebugPanel();

        bool ClearBakedData()
        {
            if (activeSet != null && ProbeVolumeBakingProvider.ClearBakedData(activeSet))
                return true;

            Debug.LogWarning($"Could not clear {ProbeVolumeBakingProvider.backend?.displayName ?? "probe volume"} data for the selected Baking Set.");
            return false;
        }

        public override void OnBakeButtonGUI()
        {
            void StartStandaloneBake()
            {
                if (aprtSelected)
                {
                    BakeAPRT(activeSet, uiScene);
                    return;
                }
                AdaptiveProbeVolumes.SyncStandaloneBakeConfigurationWithLightingSettings();
                ProbeVolumeBakingProvider.BakeStandaloneAsync();
            }

            void BakeButtonCallback(object data)
            {
                // Order of options defined by Styles.bakeOptionsText
                int option = (int)data;
                switch (option)
                {
                    // Bake and clear only the backend selected in the pipeline asset.
                    case 0: StartStandaloneBake(); break;
                    case 1: ClearBakedData(); break;
                    default: Debug.Log("invalid option in BakeButtonCallback"); break;
                }
            }

            if (ProbeVolumeBakingProvider.isRunning)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawBakeProgress();
                    if (GUILayout.Button(Styles.cancelBake, Styles.buttonStyle, GUILayout.Width(Styles.lightingButtonWidth)))
                        ProbeVolumeBakingProvider.Cancel();
                }
                return;
            }

            s_BakeStartedAt = -1.0;

            using var disableBake = new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode ||
                !ProbeReferenceVolumeProvider.instance.enabledBySRP);
            var buttonLabel = aprtSelected ? Styles.generateAPRT : Styles.generateProbes;
            var bakeOptions = aprtSelected ? new[] { "Bake APRT", "Clear APRT Data" } : Styles.bakeOptionsText;
#if UNITY_6000_0_OR_NEWER
            if (EditorGUI.LargeSplitButtonWithDropdownList(buttonLabel, bakeOptions, BakeButtonCallback))
                StartStandaloneBake();
#else
            // Unity 2022 does not expose LargeSplitButtonWithDropdownList.
            // Use supported IMGUI controls for the selected backend and its menu.
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(buttonLabel, Styles.buttonStyle,
                        GUILayout.Width(Styles.lightingButtonWidth - 24.0f)))
                    StartStandaloneBake();

                if (GUILayout.Button(new GUIContent("\u25be", "Baking options"), Styles.buttonStyle,
                        GUILayout.Width(24.0f)))
                {
                    var menu = new GenericMenu();
                    for (int i = 0; i < bakeOptions.Length; ++i)
                    {
                        int option = i;
                        menu.AddItem(new GUIContent(bakeOptions[i]), false,
                            () => BakeButtonCallback(option));
                    }
                    menu.ShowAsContext();
                }
            }
#endif
        }
        #endregion

        #region Baking
        ReorderableList m_ScenesInSet;

        List<Scene> scenesToUnload = new();
        List<SceneData> scenesForBake = new();
        List<SceneData> scenesToEnable = new();

        void BakingGUI()
        {
            EditorGUI.BeginChangeCheck();
            m_SingleSceneMode = EditorGUILayout.Popup(Styles.bakingMode, m_SingleSceneMode ? 0 : 1, Styles.bakingModeOptions) == 0;
            if (EditorGUI.EndChangeCheck() && !m_SingleSceneMode)
            {
                if (activeSet != null) { EditorUtility.SetDirty(activeSet); activeSet.singleSceneMode = false; }
                SaveTempBakingSetIfNeeded();
            }

            if (m_SingleSceneMode)
            {
                SingleSceneUI();
                return;
            }

            EditorGUI.BeginChangeCheck();
            var newSet = ObjectFieldWithNew(Styles.currentBakingSet, activeSet, CreateBakingSet);
            if (EditorGUI.EndChangeCheck())
            {
                if (newSet != null) { EditorUtility.SetDirty(newSet); newSet.singleSceneMode = false; }
                activeSet = newSet;
            }

            if (activeSet != null)
            {
                if (HasSelectedSceneToAdd())
                {
                    var newScene = EditorGUIUtility.GetObjectPickerObject() as SceneAsset;
                    if (newScene != null)
                    {
                        Event.current.Use();
                        TryAddSceneToSet(newScene);
                    }
                }

                activeSet.SanitizeScenes();
                DrawListWithIndent(m_ScenesInSet);

                using (new EditorGUI.IndentLevelScope())
                    ShowWarnings();
            }
        }

        void SingleSceneUI()
        {
            if (m_InspectorScene.IsValid() && m_InspectorScene.isLoaded)
                EditorGUILayout.LabelField("Scene", uiScene.name);
            if (m_Initialized)
            {
                var activeScene = uiScene;
                var activeSceneGUID = activeScene.GetGUID();
                var activeSceneSet = ProbeVolumeBakingSet.GetBakingSetForScene(activeSceneGUID);
                if (activeSceneSet && activeSceneSet.sceneGUIDs.Count == 1)
                {
                    if (!activeSceneSet.singleSceneMode)
                    {
                        EditorUtility.SetDirty(activeSceneSet);
                        activeSceneSet.singleSceneMode = true;
                        activeSet = activeSceneSet;
                    }
                }
                else if (activeSceneSet && (activeSceneSet.sceneGUIDs.Any(s => s != activeSceneGUID) && activeSceneSet.sceneGUIDs.Count > 1))
                {
                    if (EditorUtility.DisplayDialog("Move Scene to new baking set", $"The scene '{activeScene.name}' is part of the Baking Set '{activeSceneSet.name}' with other scenes." +
                        " This is not compatible with the Single Scene mode.\nDo you want to move the scene to a new set?", "Yes", "Cancel"))
                    {
                        var tmpSet = activeSceneSet.Clone();
                        activeSceneSet.RemoveScene(activeSceneGUID);
                        UseTemporaryBakingSet(activeSceneGUID, tmpSet);
                    }
                    else
                        m_SingleSceneMode = false;
                }
                else if (activeSet == null || (activeSet.sceneGUIDs.Any(s => s != activeSceneGUID) && activeSet.sceneGUIDs.Count > 1))
                    UseTemporaryBakingSet(activeSceneGUID);
            }

            Component firstInvalid = GetFirstProbeVolumeInNonActiveScene();
            if (firstInvalid != null)
            {
                EditorGUILayout.HelpBox($"In Single Scene mode, only scene '{uiScene.name}' will be baked. Scene '{firstInvalid.gameObject.scene.name}' contains Probe Volumes but will not be baked.\nUse Baking Sets to bake multiple scenes together.", MessageType.Warning);
                if (RightAlignedButton("Create a Baking Set"))
                    ConvertTempBakingSet();
            }
        }

        void ShowWarnings()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!aprtSelected && scene.isLoaded && ProbeVolumeBakingSet.GetBakingSetForScene(scene) != activeSet)
                    scenesToUnload.Add(scene);
            }

            bool hasWarnings = scenesForBake.Count + scenesToUnload.Count + scenesToEnable.Count > 0;
            if (hasWarnings && Foldout(Styles.warnings, Expandable.BakingWarnings, false))
            {
                if (scenesForBake.Count > 0)
                {
                    EditorGUILayout.HelpBox(Styles.msgLoadForBake, MessageType.Warning);
                    if (RightAlignedButton("Load Baking Set"))
                    {
                        foreach (var scene in scenesForBake)
                            EditorSceneManager.OpenScene(scene.GetPath(), OpenSceneMode.Additive);
                        if (scenesToUnload.All(s => !s.isDirty) || EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                        {
                            foreach (var scene in scenesToUnload)
                                EditorSceneManager.CloseScene(scene, true);
                        }
                    }
                }
                if (scenesToUnload.Count > 0)
                {
                    EditorGUILayout.HelpBox(Styles.msgUnloadOther, MessageType.Warning);
                    switch (RightAlignedButton("Add to Baking Set", "Unload These Scenes", disable2: scenesToUnload.Count == SceneManager.loadedSceneCount))
                    {
                        case 1:
                            for (int i = 0; i < SceneManager.sceneCount; i++)
                            {
                                var scene = SceneManager.GetSceneAt(i);
                                if (scene.isLoaded)
                                    TryAddSceneToSet(scene);
                            }
                            break;
                        case 2:
                            if (scenesToUnload.All(s => !s.isDirty) || EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                            {
                                foreach (var scene in scenesToUnload)
                                    EditorSceneManager.CloseScene(scene, false);
                            }
                            break;
                    }
                }
                if (scenesToEnable.Count > 0)
                {
                    EditorGUILayout.HelpBox(Styles.msgEnableAll, MessageType.Info);
                    if (RightAlignedButton("Enable All Scenes"))
                    {
                        foreach (var scene in scenesToEnable)
                            activeSet.SetSceneBaking(scene.guid, true);
                    }
                }
            }

            scenesForBake.Clear();
            scenesToUnload.Clear();
            scenesToEnable.Clear();
        }

        void UseTemporaryBakingSet(string sceneGUID, ProbeVolumeBakingSet set = null)
        {
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                set.SetDefaults();

                ProbeReferenceVolumeProvider.instance.AddPendingSceneRemoval(sceneGUID);
            }

            EditorUtility.SetDirty(set);
            var sceneData = FindSceneData(sceneGUID);
            set.name = (sceneData.asset == null ? "Untitled" : sceneData.asset.name) + " Baking Set";
            set.singleSceneMode = true;
            set.AddScene(sceneGUID);
            activeSet = set;
            m_TempBakingSet = true;
        }

        void SaveTempBakingSetIfNeeded()
        {
            var scene = uiScene;
            if (!m_TempBakingSet || scene == null) return;
            string path = string.IsNullOrEmpty(scene.path) ?
                ProbeVolumeBakingSet.GetDirectory("Assets/", "Untitled") :
                ProbeVolumeBakingSet.GetDirectory(scene.path, scene.name);
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            path = Path.Combine(path, activeSet.name + ".asset");
            path = AssetDatabase.GenerateUniqueAssetPath(path);

            AssetDatabase.CreateAsset(activeSet, path.Replace('\\', '/'));
            AssetDatabase.SaveAssetIfDirty(activeSet);
            // The scene registration already points to this same object;
            // rescanning would discard other unsaved Baking Set registrations.
            m_TempBakingSet = false;
        }

        void ConvertTempBakingSet()
        {
            m_SingleSceneMode = activeSet.singleSceneMode = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                TryAddSceneToSet(SceneManager.GetSceneAt(i));
            SaveTempBakingSetIfNeeded();
        }

        ProbeVolumeBakingSet CreateBakingSet()
        {
            var scene = uiScene;
            string path = string.IsNullOrEmpty(scene.path) ?
                ProbeVolumeBakingSet.GetDirectory("Assets/", "Untitled") :
                ProbeVolumeBakingSet.GetDirectory(scene.path, scene.name);

            var newSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            newSet.name = "New Baking Set";
            newSet.singleSceneMode = false;
            newSet.SetDefaults();
            ProjectWindowUtil.CreateAsset(newSet, System.IO.Path.Combine(path, "New Baking Set.asset").Replace('\\', '/'));
            return newSet;
        }

        void CreateProbeVolume()
        {
            var probeVolume = CoreEditorUtils.CreateGameObject(null, "Probe Volume");
            var pv = probeVolume.AddComponent<ProbeVolume>();
            pv.mode = ProbeVolume.Mode.Scene;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        bool HasSelectedSceneToAdd()
        {
            var evt = Event.current;
            return evt.type == EventType.ExecuteCommand && evt.commandName == "ObjectSelectorSelectionDone" && GUIUtility.keyboardControl == k_AddSceneID;
        }

        bool TryMoveSceneToActiveSet(string sceneName, string sceneGUID, ProbeVolumeBakingSet oldSet, int index = -1)
        {
            if (!EditorUtility.DisplayDialog("Move Scene to baking set", $"The scene '{sceneName}' was already added in the baking set '{oldSet.name}'. Do you want to move it to the current set?", "Yes", "Cancel"))
                return false;

            Undo.RegisterCompleteObjectUndo(new Object[] { activeSet, oldSet }, "Moved scene to baking set");
            activeSet.MoveSceneToBakingSet(sceneGUID, index);

            return true;
        }

        void TrySetSceneInSet(SceneData scene, int index)
        {
            var sceneSet = ProbeVolumeBakingSet.GetBakingSetForScene(scene.guid);
            if (scene.guid == null || sceneSet == activeSet)
                return;
            if (sceneSet != null)
            {
                if (!TryMoveSceneToActiveSet(scene.asset.name, scene.guid, sceneSet, index))
                    return;
            }
            else
            {
                Undo.RegisterCompleteObjectUndo(new Object[] { activeSet }, "Updated scene in baking set");
                activeSet.SetScene(scene.guid, index);
            }
        }

        void TryAddSceneToSet(SceneAsset scene) { TryAddSceneToSet(scene.name, FindSceneData(scene).guid); }
        void TryAddSceneToSet(Scene scene) { TryAddSceneToSet(scene.name, scene.GetGUID()); }

        void TryAddSceneToSet(string sceneName, string sceneGUID)
        {
            // Don't allow the same scene in two different sets
            var sceneSet = ProbeVolumeBakingSet.GetBakingSetForScene(sceneGUID);
            if (sceneSet == activeSet)
                return;
            if (sceneSet != null)
            {
                if (!TryMoveSceneToActiveSet(sceneName, sceneGUID, sceneSet))
                    return;
            }
            else
            {
                Undo.RegisterCompleteObjectUndo(new Object[] { activeSet }, "Added scene in baking set");
                activeSet.AddScene(sceneGUID);
            }
        }

        void InitializeSceneList()
        {
            m_ScenesInSet = new ReorderableList((System.Collections.IList)activeSet.sceneGUIDs, typeof(string), true, true, true, true)
            {
                multiSelect = true,
                elementHeight = EditorGUIUtility.singleLineHeight + 2,

                onAddCallback = (list) =>
                {
                    GUIUtility.keyboardControl = k_AddSceneID;
                    EditorGUIUtility.ShowObjectPicker<SceneAsset>(null, false, null, k_AddSceneID);
                },

                onRemoveCallback = (list) =>
                {
                    var guid = (string)list.list[list.index];
                    activeSet.RemoveScene(guid);
                    Undo.RegisterCompleteObjectUndo(new Object[] { activeSet }, "Deleted scene in baking set");
                    EditorUtility.SetDirty(activeSet);
                },

                drawHeaderCallback = (rect) =>
                {
                    SplitRectInThree(rect, out var sceneRect, out var statusRect, out var bakeRect);
                    EditorGUI.LabelField(sceneRect, Styles.scenesInSet);
                    EditorGUI.LabelField(statusRect, Styles.status);
                    EditorGUI.LabelField(bakeRect, Styles.bake);

                    var contextMenuRect = new Rect(bakeRect.xMax - 13f, bakeRect.y + 1f, 16, 16);
                    if (GUI.Button(contextMenuRect, CoreEditorStyles.contextMenuIcon, CoreEditorStyles.contextMenuStyle))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(Styles.addLoadedScenes, false, () => {
                            for (int i = 0; i < SceneManager.sceneCount; i++)
                            {
                                if (SceneManager.GetSceneAt(i).isLoaded)
                                    TryAddSceneToSet(SceneManager.GetSceneAt(i));
                            }
                        });
                        if (!aprtSelected)
                        {
                            menu.AddSeparator(string.Empty);
                            menu.AddItem(Styles.toggleBakeAll, false, () => activeSet.SetAllSceneBaking(true));
                            menu.AddItem(Styles.toggleBakeNone, false, () => activeSet.SetAllSceneBaking(false));
                        }

                        menu.DropDown(contextMenuRect);
                    }
                },

                drawElementCallback = (rect, index, active, focused) =>
                {
                    rect.yMin++;
                    rect.yMax--;
                    SplitRectInThree(rect, out var sceneRect, out var statusRect, out var bakeRect);

                    var scene = index < activeSet.sceneGUIDs.Count ? FindSceneData(activeSet.sceneGUIDs[index]) : default;
                    Scene scene2 = SceneManager.GetSceneByPath(scene.GetPath());
                    bool isLoaded = scene2.isLoaded;
                    bool isActive = SceneManager.GetActiveScene() == scene2;

                    using (new EditorGUI.DisabledScope(!isLoaded))
                    {
                        EditorGUI.BeginChangeCheck();
                        var newScene = EditorGUI.ObjectField(sceneRect, scene.asset, typeof(SceneAsset), false) as SceneAsset;
                        if (EditorGUI.EndChangeCheck() && newScene != null)
                            TrySetSceneInSet(FindSceneData(newScene), index);

                        string label = isLoaded ? (isActive ? "Active" : "Loaded") : "Unloaded";
                        EditorGUI.LabelField(statusRect, label, isActive ? EditorStyles.boldLabel : EditorStyles.label);

                        bakeRect.xMin += 5f;
                        bakeRect.width = 21;

                        bool bake = true;
                        if (scene.guid != null)
                        {
                            var bakeData = activeSet.GetSceneBakeData(scene.guid);
                            if (aprtSelected)
                            {
                                using (new EditorGUI.DisabledScope(true))
                                    EditorGUI.Toggle(bakeRect, isLoaded);
                            }
                            else if (bakeData.hasProbeVolume)
                            {
                                EditorGUI.BeginChangeCheck();
                                EditorGUI.LabelField(bakeRect, Styles.bakeBox); // Show a tooltip on the checkbox
                                bake = EditorGUI.Toggle(bakeRect, bakeData.bakeScene && isLoaded);
                                if (EditorGUI.EndChangeCheck())
                                {
                                    Undo.RegisterCompleteObjectUndo(activeSet, "Set scene bake status");
                                    EditorUtility.SetDirty(activeSet);
                                    bakeData.bakeScene = bake;
                                }
                            }
                        }

                        GUIContent content = null;
                        if (isLoaded && !bake)
                        {
                            content = Styles.iconEnableAll;
                            scenesToEnable.Add(scene);
                        }
                        if (!isLoaded && scene.guid != null)
                        {
                            content = Styles.iconLoadForBake;
                            scenesForBake.Add(scene);
                        }

                        if (content != null)
                        {
                            bakeRect.x += 20f;
                            bakeRect.width = bakeRect.height;
                            GUI.Label(bakeRect, content);
                        }
                    }
                }
            };
        }
        #endregion

        #region Summary
        public override void OnSummaryGUI()
        {
            Func<long, string> FormatToMB = (bytes) => (bytes / (float)(1000 * 1000)).ToString("F1") + " MB";

            if (!m_Initialized || activeSet == null)
            {
                GUILayout.BeginHorizontal();

                GUILayout.BeginVertical();
                GUILayout.Label(Styles.totalCostStat, EditorStyles.wordWrappedMiniLabel);
                GUILayout.EndVertical();

                GUILayout.BeginVertical();
                GUILayout.Label(FormatToMB(0), EditorStyles.wordWrappedMiniLabel);
                GUILayout.EndVertical();

                GUILayout.EndHorizontal();
                return;
            }

            var backendId = ProbeReferenceVolumeProvider.activeBackend?.id;
            var data = backendId != null ? activeSet.GetBakedData(backendId) : null;
            long scenarioCost = data?.GetDiskSizeOfScenarioData(activeSet.GetLightingScenario(backendId)) ?? 0;

            long sharedCost = data?.GetDiskSizeOfSharedData() ?? 0;
            foreach (var scenario in activeSet.m_LightingScenarios)
                sharedCost += data?.GetDiskSizeOfScenarioData(scenario) ?? 0;

            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical();
            GUILayout.Label(Styles.scenarioCostStat, EditorStyles.wordWrappedMiniLabel);
            GUILayout.Label(Styles.totalCostStat, EditorStyles.wordWrappedMiniLabel);
            GUILayout.EndVertical();

            GUILayout.BeginVertical();
            GUILayout.Label(FormatToMB(scenarioCost), EditorStyles.wordWrappedMiniLabel);
            GUILayout.Label(FormatToMB(sharedCost), EditorStyles.wordWrappedMiniLabel);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }
        #endregion

        #region Scene Management
        struct SceneData
        {
            public SceneAsset asset;
            public string guid;

            public string GetPath()
            {
                return AssetDatabase.GUIDToAssetPath(guid);
            }
        }

        List<SceneData> m_ScenesInProject = new List<SceneData>();

        internal void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (scene == SceneManager.GetActiveScene())
            {
                m_InspectorScene = default;
                var prv = ProbeReferenceVolumeProvider.instance;
                // Find the set in which the new active scene belongs
                var set = ProbeVolumeBakingSet.GetBakingSetForScene(scene);

                activeSet = set;

                if (set == null)
                {
                    m_SingleSceneMode = true;
                }
                else
                {
                    // Validate this set's selection for the selected backend.
                    if (set.m_LightingScenarios.Count > 0 && !set.m_LightingScenarios.Contains(set.GetLightingScenario(prv.backendId)))
                        set.SetActiveScenario(set.m_LightingScenarios[0], false, prv);
                }
            }
        }

        bool NoSceneHasProbeVolume() => ProbeVolume.instances.Count == 0;
        bool ActiveSceneHasProbeVolume() => ProbeVolume.instances.Any(d => d.gameObject.scene == SceneManager.GetActiveScene());
        ProbeVolume GetFirstProbeVolumeInNonActiveScene() => ProbeVolume.instances.FirstOrDefault(d => d.gameObject.scene != SceneManager.GetActiveScene());

        void RefreshSceneAssets()
        {
            var sceneAssets = AssetDatabase.FindAssets("t:Scene");

            m_ScenesInProject = sceneAssets.Select(s =>
            {
                var path = AssetDatabase.GUIDToAssetPath(s);
                var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
                return new SceneData
                {
                    asset = asset,
                    guid = s,
                };
            }).ToList();
        }

        SceneData FindSceneData(SceneAsset asset)
        {
            var data = m_ScenesInProject.FirstOrDefault(s => s.asset == asset);

            if (data.asset == null)
            {
                RefreshSceneAssets();
                data = m_ScenesInProject.FirstOrDefault(s => s.asset == asset);
            }

            return data;
        }

        SceneData FindSceneData(string guid)
        {
            var data = m_ScenesInProject.FirstOrDefault(s => s.guid == guid);

            if (data.asset == null)
            {
                RefreshSceneAssets();
                data = m_ScenesInProject.FirstOrDefault(s => s.guid == guid);
            }

            return data;
        }

        internal static ProbeVolumeBakingSet GetSceneBakingSetForUI(Scene scene)
        {
            // If the set is available, return it
            var bakingSet = ProbeVolumeBakingSet.GetBakingSetForScene(scene);
            if (bakingSet != null)
                return bakingSet;

            // Otherwise, a baking set might be created in the UI but not registered yet in the system
            if (instance == null || instance.activeSet == null)
                return null;
            if (!singleSceneMode || !instance.activeSet.singleSceneMode)
                return null;
            if (!instance.activeSet.sceneGUIDs.Contains(scene.GetGUID()))
                return null;

            return instance.activeSet;
        }
        #endregion

        #region Async Bake
        internal static bool BakeAPRT(ProbeVolumeBakingSet bakingSet, Scene scene)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || ProbeVolumeBakingProvider.isRunning ||
                !ProbeVolumeBakingProvider.IsBackendAvailable(k_APRTBackendId))
                return false;
            if (!PrepareAPRTBakingSet(ref bakingSet, scene))
                return false;
            if (instance != null)
                instance.SetInspectorContext(bakingSet, scene);
            ProbeVolumeBakingProvider.partialBakeSceneList = null;
            return ProbeVolumeBakingProvider.BakeStandaloneAsync(k_APRTBackendId, bakingSet);
        }

        static bool PrepareAPRTBakingSet(ref ProbeVolumeBakingSet bakingSet, Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
            {
                Debug.LogWarning("Save the scene before baking APRT.");
                return false;
            }
            bakingSet ??= GetSceneBakingSetForUI(scene);
            if (bakingSet == null)
            {
                bakingSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                bakingSet.SetDefaults();
                bakingSet.name = scene.name + " Baking Set";
                bakingSet.singleSceneMode = true;
                bakingSet.AddScene(scene.GetGUID());
            }
            if (bakingSet.sceneGUIDs.Count == 0)
                bakingSet.AddScene(scene.GetGUID());
            foreach (var guid in bakingSet.sceneGUIDs)
            {
                var source = SceneManager.GetSceneByPath(AssetDatabase.GUIDToAssetPath(guid));
                if (!source.IsValid() || !source.isLoaded || string.IsNullOrEmpty(source.path))
                {
                    Debug.LogWarning("Load and save every scene in the APRT Baking Set before baking.");
                    return false;
                }
            }
            if (!EditorUtility.IsPersistent(bakingSet))
            {
                string directory = ProbeVolumeBakingSet.GetDirectory(scene.path, scene.name);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                string path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(directory, bakingSet.name + ".asset").Replace('\\', '/'));
                AssetDatabase.CreateAsset(bakingSet, path);
                AssetDatabase.SaveAssetIfDirty(bakingSet);
                NotifyBakingSetSaved(bakingSet);
            }
            return true;
        }

        internal static void BakeProbeVolumeButton()
        {
            if (ProbeVolumeBakingProvider.isRunning)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawBakeProgress();
                    if (GUILayout.Button(Styles.cancelBake))
                        ProbeVolumeBakingProvider.Cancel();
                }
            }
            else
            {
                s_BakeStartedAt = -1.0;
                using var disableBake = new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode ||
                    !ProbeReferenceVolumeProvider.instance.enabledBySRP);
                if (GUILayout.Button(aprtSelected ? Styles.generateAPRT : Styles.generateProbes))
                {
                    EditorApplication.delayCall += () =>
                    {
                        if (aprtSelected)
                        {
                            BakeAPRT(displayedBakingSet, displayedScene);
                            return;
                        }
                        AdaptiveProbeVolumes.SyncStandaloneBakeConfigurationWithLightingSettings();
                        ProbeVolumeBakingProvider.BakeStandaloneAsync();
                    };
                }
            }
        }

        static double s_BakeStartedAt = -1.0;

        // Unity 2022 does not expose the newer progress window, so show a
        // compact status row for the selected standalone baker.
        static void DrawBakeProgress()
        {
            if (!ProbeVolumeBakingProvider.isRunning)
            {
                s_BakeStartedAt = -1.0;
                return;
            }

            if (s_BakeStartedAt < 0.0)
                s_BakeStartedAt = EditorApplication.timeSinceStartup;

            Rect rect = GUILayoutUtility.GetRect(120.0f, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));

            // The standalone baker does not publish a per-probe percentage;
            // use a moving indeterminate segment while it is active.
            EditorGUI.DrawRect(rect, new Color(0.18f, 0.18f, 0.18f));
            float segmentWidth = Mathf.Max(24.0f, rect.width * 0.25f);
            float travel = rect.width + segmentWidth;
            float offset = Mathf.Repeat((float)(EditorApplication.timeSinceStartup * 90.0), travel) - segmentWidth;
            float segmentStart = Mathf.Max(0.0f, offset);
            float segmentEnd = Mathf.Min(rect.width, offset + segmentWidth);
            EditorGUI.DrawRect(new Rect(rect.x + segmentStart, rect.y, Mathf.Max(0.0f, segmentEnd - segmentStart), rect.height), new Color(0.24f, 0.58f, 0.95f));
            var elapsed = TimeSpan.FromSeconds(EditorApplication.timeSinceStartup - s_BakeStartedAt);
            GUI.Label(rect, $"{ProbeVolumeBakingProvider.backend?.displayName ?? "Probe Volume"} baking  {elapsed:mm\\:ss}", EditorStyles.centeredGreyMiniLabel);
        }
        #endregion

        #region UI Helpers
        internal void SetInspectorContext(ProbeVolumeBakingSet bakingSet, Scene scene)
        {
            activeSet = bakingSet;
            m_InspectorScene = scene.IsValid() && scene.isLoaded && bakingSet != null && bakingSet.sceneGUIDs.Contains(scene.GetGUID())
                ? scene : default;
        }

        internal static void OpenBakingSet(ProbeVolumeBakingSet bakingSet, Scene? scene = null)
        {
#if UNITY_6000_0_OR_NEWER
            var lightingWindow = Type.GetType("UnityEditor.LightingWindow,UnityEditor");
            EditorWindow.GetWindow(lightingWindow, utility: false, title: null, focus: true);
#else
            ProbeVolumeLightingWindow2022.Open();
#endif
            if (instance == null)
                return;

            instance.FocusTab();
            instance.SetInspectorContext(bakingSet, scene ?? default);
        }

        static MethodInfo k_FoldoutTitlebar;
        internal static bool Foldout(GUIContent label, Expandable expandable, bool title, Action<Rect> contextMenuCallback = null)
        {
            k_Foldouts ??= new(k_ExpandableDefault, "ProbeVolume");
            bool expanded = Foldout(label, k_Foldouts[expandable], title, contextMenuCallback);
            k_Foldouts.SetExpandedAreas(expandable, expanded);
            return expanded;
        }

        internal static bool Foldout(GUIContent label, bool expanded, bool title, Action<Rect> contextMenuCallback = null)
        {
            if (k_FoldoutTitlebar == null)
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Static;
                Type[] args = new Type[] { typeof(Rect), typeof(GUIContent), typeof(bool), typeof(bool) };
                k_FoldoutTitlebar = typeof(EditorGUI).GetMethod("FoldoutTitlebar", flags, null, args, null);
            }

            var labelRect = GUILayoutUtility.GetRect(GUIContent.none, Styles.inspectorTitle, GUILayout.ExpandWidth(true));
            var contextMenuRect = new Rect(labelRect.xMax - 17f, labelRect.y + 3f, 16, 16);

            if (contextMenuCallback != null)
            {
                if (GUI.Button(contextMenuRect, CoreEditorStyles.contextMenuIcon, CoreEditorStyles.contextMenuStyle))
                    contextMenuCallback(contextMenuRect);
            }

            bool foldout = title ?
                (bool)k_FoldoutTitlebar.Invoke(null, new object[] { labelRect, label, expanded, true }) :
                EditorGUI.Foldout(labelRect, expanded, label, true);

            // Shameful hack warning: we have to display the button before the foldout to take precedence on click
            // Therefore we have to draw it again afterwards so that we can see it on top.
            if (contextMenuCallback != null)
                GUI.Button(contextMenuRect, CoreEditorStyles.contextMenuIcon, CoreEditorStyles.contextMenuStyle);

            return foldout;
        }

        static MethodInfo k_GetLightingSettingsOrDefaultsFallback = typeof(Lightmapping).GetMethod("GetLightingSettingsOrDefaultsFallback", BindingFlags.Static | BindingFlags.NonPublic);
        internal static LightingSettings GetLightingSettings()
        {
            return k_GetLightingSettingsOrDefaultsFallback.Invoke(null, null) as LightingSettings;
        }

        internal static bool PrepareBake()
        {
            if (instance != null)
                return instance.PrepareActiveBake();

            var tab = new ProbeVolumeLightingTab();
            try
            {
                return tab.PrepareActiveBake();
            }
            finally
            {
                if (tab.m_TempBakingSet)
                    Object.DestroyImmediate(tab.activeSet);
            }
        }

        void OnActiveSceneChanged(Scene previous, Scene current) => OnSceneOpened(current, OpenSceneMode.Additive);

        bool PrepareActiveBake()
        {
            var prv = ProbeReferenceVolumeProvider.instance;
            if (!prv.isInitialized || !prv.enabledBySRP)
                return false;

            if (aprtSelected)
            {
                var set = ProbeVolumeBakingProvider.requestedBakingSet ?? activeSet ??
                    ProbeVolumeBakingSet.GetBakingSetForScene(uiScene);
                if (!PrepareAPRTBakingSet(ref set, uiScene))
                    return false;
                activeSet = set;
                var data = set.GetBakedData(k_APRTBackendId) as APRTBakedData;
                if (!set.freezePlacement && data?.scenarioCount > 1 && !EditorUtility.DisplayDialog(
                    "Recalculate Probe Positions?",
                    "Recalculating probe positions may invalidate other baked Lighting Scenarios. Use Don't Recalculate to keep a compatible layout.",
                    "Recalculate", "Cancel"))
                    return false;
                ProbeVolumeBakingProvider.partialBakeSceneList = null;
                return true;
            }

            // In case UI was never opened we have to setup some stuff
            FindActiveSet();

            if (activeSet == null)
            {
                // Probe volumes were never setup by the user, try to do it for him by creating a default baking set
                var activeScene = SceneManager.GetActiveScene();
                var activeSceneGUID = activeScene.GetGUID();
                UseTemporaryBakingSet(activeSceneGUID);
            }

            bool createPV = m_SingleSceneMode ? !ActiveSceneHasProbeVolume() : NoSceneHasProbeVolume();
            if (createPV)
            {
                if(!activeSet.DialogNoProbeVolumeInSetShown())
                {
                    if(EditorUtility.DisplayDialog("No Probe Volume in Scene", "Probe Volumes are enabled for this Project, but none exist in the Scene.\n\n" +
                        "Do you wish to add an Probe Volume to the Active Scene?", "Yes", "No"))
                        CreateProbeVolume();
                    activeSet.SetDialogNoProbeVolumeInSetShown(true);
                }
            }
            if (m_SingleSceneMode)
            {
                if (GetFirstProbeVolumeInNonActiveScene() != null)
                {
                    int res = EditorUtility.DisplayDialogComplex("Create Baking Set?", "You are using the Single Scene Baking Mode and have more than one Scene loaded. It is not possible to generate lighting.\n\n" +
                        "Do you want to create a Baking Set instead?", "Yes", "Cancel", "Bake anyway");
                    if (res == 0)
                        ConvertTempBakingSet();
                    if (res == 1)
                        return false;
                }
            }

            SaveTempBakingSetIfNeeded();

            if (!FindActiveSet())
                return false;

            // Exclude scenes unchecked from the UI and scenes from other baking sets
            ProbeVolumeBakingProvider.partialBakeSceneList = new();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                var guid = scene.GetGUID();
                var sceneBakingSet = ProbeVolumeBakingSet.GetBakingSetForScene(guid);
                if (!scene.isLoaded || sceneBakingSet != activeSet) continue;
                var sceneBakeData = sceneBakingSet.GetSceneBakeData(guid);
                if (!sceneBakeData.bakeScene && sceneBakeData.hasProbeVolume) continue;

                ProbeVolumeBakingProvider.partialBakeSceneList.Add(guid);
            }

            if (ProbeVolumeBakingProvider.partialBakeSceneList.Count == activeSet.sceneGUIDs.Count)
                ProbeVolumeBakingProvider.partialBakeSceneList = null;

            if (ProbeReferenceVolumeProvider.instance.supportLightingScenarios && !activeSet.m_LightingScenarios.Contains(activeSet.lightingScenario) && activeSet.m_LightingScenarios.Count > 0)
                activeSet.SetActiveScenario(activeSet.m_LightingScenarios[0], false);

            // Layout has changed and is incompatible.
            if (activeSet.HasValidSharedData() && !activeSet.freezePlacement && !activeSet.CheckCompatibleCellLayout())
            {
                if (ProbeVolumeBakingProvider.partialBakeSceneList != null)
                {
                    if (EditorUtility.DisplayDialog("Incompatible Layout", "You are partially baking the set with an incompatible cell layout. Proceeding will invalidate all previously bake data.\n\n" + "Do you wish to continue?", "Yes", "No"))
                        return ClearBakedData();
                    else
                        return false;
                }
                else if (ProbeReferenceVolumeProvider.instance.supportLightingScenarios && activeSet.bakedData.scenarioCount != (activeSet.bakedData.HasBakedData(activeSet.lightingScenario, true) ? 1 : 0))
                {
                    if (EditorUtility.DisplayDialog("Incompatible Layout", "You are baking scenarios with incompatible cell layouts. Proceeding will invalidate all previously bake data.\n\n" + "Do you wish to continue?", "Yes", "No"))
                        return ClearBakedData();
                    else
                        return false;
                }
            }

            return true;
        }

        static T ObjectFieldWithNew<T>(GUIContent label, T obj, Func<T> onClick) where T : Object
        {
            const int k_NewFieldWidth = 70;

            var rect = EditorGUILayout.GetControlRect();
            rect.xMax -= k_NewFieldWidth + 2;

            var newFieldRect = rect;
            newFieldRect.x = rect.xMax + 2;
            newFieldRect.width = k_NewFieldWidth;
            if (GUI.Button(newFieldRect, "New"))
                return onClick();

            return EditorGUI.ObjectField(rect, label, obj, typeof(T), false) as T;
        }

        internal static void SplitRectInThree(Rect rect, out Rect left, out Rect middle, out Rect right, float middleWith = Styles.statusLabelWidth, float rightWidth = 50)
        {
            right = rect;
            right.xMin = rect.xMax - rightWidth;
            middle = rect;
            middle.xMin = right.xMin - middleWith;
            middle.xMax = right.xMin - 2;
            left = rect;
            left.xMax = middle.xMin - 10;
        }

        static bool RightAlignedButton(string text, float width = Styles.lightingButtonWidth)
        {
            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var rect = EditorGUILayout.GetControlRect(GUILayout.Width(width));
            rect.y -= 8;
            bool click = GUI.Button(rect, text);
            GUILayout.EndHorizontal();
            return click;
        }

        static int RightAlignedButton(string text1, string text2, bool disable1 = false, bool disable2 = false, float width = Styles.lightingButtonWidth)
        {
            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var rect1 = EditorGUILayout.GetControlRect(GUILayout.Width(width));
            var rect2 = EditorGUILayout.GetControlRect(GUILayout.Width(width));
            rect1.y -= 8;
            rect2.y -= 8;
            bool click1, click2;
            using (new EditorGUI.DisabledScope(disable1))
                click1 = GUI.Button(rect1, text1);
            using (new EditorGUI.DisabledScope(disable2))
                click2 = GUI.Button(rect2, text2);
            GUILayout.EndHorizontal();
            return click1 ? 1 : click2 ? 2 : 0;
        }

        internal static void DrawListWithIndent(ReorderableList list)
        {
            EditorGUILayout.Space();

            int level = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            GUILayout.BeginHorizontal();
            EditorGUILayout.Space(15 * level, false);
            {
                list.DoLayoutList();
            }
            EditorGUILayout.Space(3, false);
            GUILayout.EndHorizontal();
            EditorGUI.indentLevel = level;
        }

        private static Color k_DarkThemeColor = new Color32(153, 153, 153, 255);
        private static Color k_LiteThemeColor = new Color32(97, 97, 97, 255);
        static Color GetMarkerColor() => EditorGUIUtility.isProSkin ? k_DarkThemeColor : k_LiteThemeColor;

        internal static void DrawSimplificationLevelsMarkers(Rect rect, float minDistanceBetweenProbes, int lowestSimplification, int highestSimplification, int hideStart, int hideEnd)
        {
            var markerRect = new Rect(rect) { width = 2, height = 2 };
            markerRect.y += (EditorGUIUtility.singleLineHeight / 2f) - 1;
            float indent = 15 * EditorGUI.indentLevel;
            GUILayout.Space(18);

            for (int i = lowestSimplification; i <= highestSimplification; i++)
            {
                float position = (float)(i - lowestSimplification) / (highestSimplification - lowestSimplification);

                float knobSize = (i == lowestSimplification || i == highestSimplification) ? 0 : 10;
                float start = rect.x + knobSize / 2f;
                float range = rect.width - knobSize;
                markerRect.x = start + range * position - 0.5f * markerRect.width;

                float min = rect.x;
                float max = (rect.x + rect.width) - markerRect.width;
                markerRect.x = Mathf.Clamp(markerRect.x, min, max);

                float maxTextWidth = 200;
                string text = (minDistanceBetweenProbes * ProbeVolumeLayout.CellSize(i)) + "m";
                float textX = markerRect.x + 1 - (maxTextWidth + indent) * 0.5f;
                Styles.labelFont.alignment = TextAnchor.UpperCenter;
                if (i == highestSimplification)
                {
                    textX = rect.xMax - maxTextWidth;
                    Styles.labelFont.alignment = TextAnchor.UpperRight;
                }
                else if (i == lowestSimplification)
                {
                    textX = markerRect.x - indent;
                    Styles.labelFont.alignment = TextAnchor.UpperLeft;
                }

                var label = new Rect(rect) { x = textX, width = maxTextWidth, y = rect.y + 18 };
                EditorGUI.LabelField(label, text, Styles.labelFont);
                if (i < hideStart || i > hideEnd)
                    EditorGUI.DrawRect(markerRect, GetMarkerColor());
            }
        }

        #endregion


    }
}
