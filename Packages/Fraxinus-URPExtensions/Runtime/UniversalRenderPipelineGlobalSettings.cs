using System;
using System.IO;
using System.ComponentModel;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Rendering;
#endif

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Universal Render Pipeline's Global Settings.
    /// Global settings are unique per Render Pipeline type. In URP, Global Settings contain:
    /// - light layer names
    /// </summary>
    [URPHelpURL("urp-global-settings")]
    [DisplayInfo(name = "URP Global Settings Asset", order = CoreUtils.Sections.section4 + 2)]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    [DisplayName("URP")]
    partial class UniversalRenderPipelineGlobalSettings : RenderPipelineGlobalSettings<UniversalRenderPipelineGlobalSettings, UniversalRenderPipeline>, ISerializationCallbackReceiver
#if !UNITY_6000_0_OR_NEWER
        , IRenderingLayerNamesProvider
#endif
    {
        [SerializeField] RenderPipelineGraphicsSettingsContainer m_Settings = new RenderPipelineGraphicsSettingsContainer();
        protected override List<IRenderPipelineGraphicsSettings> settingsList =>
            m_Settings != null ? m_Settings.settingsList : null;

#if !UNITY_6000_0_OR_NEWER
        internal bool EnsureGraphicsSettings()
        {
            if (m_Settings == null)
                m_Settings = new RenderPipelineGraphicsSettingsContainer();

            // Unity 2022 has no Tags & Layers registry owned by the editor.
            // Materialize the package-owned active rendering-layer array before
            // the inspector creates its SerializedObject, so the inspector
            // edits the same names consumed by lights, decals and renderers.
            var renderingLayerNamesBeforeEnsure = m_RenderingLayerNamesCompatibility;
            GetActiveRenderingLayerNames();
            var renderingLayerNamesChanged = !ReferenceEquals(
                renderingLayerNamesBeforeEnsure,
                m_RenderingLayerNamesCompatibility);

            var changed = RenderPipelineGraphicsSettingsCompatibility.PopulateRenderPipelineGraphicsSettings(
                this,
                typeof(UniversalRenderPipelineAsset));
            changed |= renderingLayerNamesChanged;
#if UNITY_EDITOR
            var defaultVolumeSettings = RenderPipelineGraphicsSettingsCompatibility
                .GetRenderPipelineSettings<URPDefaultVolumeProfileSettings>(this);
            if (defaultVolumeSettings != null &&
                (defaultVolumeSettings.volumeProfile == null || defaultVolumeSettings.volumeProfile.Equals(null)))
            {
                defaultVolumeSettings.volumeProfile = GetOrCreateDefaultVolumeProfile(null);
                changed = true;
            }
            if (changed)
                EditorUtility.SetDirty(this);
#endif
            return changed;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Builds and serializes the Unity-2022 player copy of the managed
        /// graphics-settings collection before BuildPipeline starts writing
        /// player data.
        /// </summary>
        internal void PrepareGraphicsSettingsForPlayerBuild()
        {
            EnsureGraphicsSettings();
            m_Settings?.OnBeforeSerialize();
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssetIfDirty(this);
        }
#endif
#endif

        public void OnBeforeSerialize()
        {
#if UNITY_EDITOR && !UNITY_6000_0_OR_NEWER
            InvalidateInstanceCache();
#endif
#if !UNITY_6000_0_OR_NEWER
            // Keep the runtime copy in sync when Unity serializes a player
            // build.  RenderPipelineGraphicsSettingsContainer performs the
            // editor-only stripping in its own callback.
            if (m_Settings != null)
                m_Settings.OnBeforeSerialize();
#endif
        }

        public void OnAfterDeserialize()
        {
#if UNITY_EDITOR && !UNITY_6000_0_OR_NEWER
            InvalidateInstanceCache();
#endif
#if !UNITY_6000_0_OR_NEWER
            // Populate after deserialization, once all package assemblies are
            // loaded.  Delaying in the editor avoids running reflection while
            // AssetDatabase is still importing the global settings asset.
#if UNITY_EDITOR
            EditorApplication.delayCall += () =>
            {
                if (this != null)
                    EnsureGraphicsSettings();
            };
#endif
#endif
        }
#if UNITY_EDITOR
        static T GetOrCreateGraphicsSettings<T>(UniversalRenderPipelineGlobalSettings data)
            where T : class, IRenderPipelineGraphicsSettings, new()
        {
            if (data.m_Settings == null)
                data.m_Settings = new RenderPipelineGraphicsSettingsContainer();

            if (data.TryGet(typeof(T), out var baseSettings) && baseSettings is T settings)
                return settings;

            settings = new T();
            data.Add(settings);
            return settings;
        }

#endif

        private static UniversalRenderPipelineGlobalSettings cachedInstance = null;
#if UNITY_EDITOR && !UNITY_6000_0_OR_NEWER
        // Unity 2022 has no EditorGraphicsSettings singleton accessor.  The
        // native GraphicsSettings lookup is considerably more expensive than
        // a managed field read and this property is also reached from the
        // renderer's per-light rendering-layer path.  Keep an explicit cache
        // and invalidate it from editor lifecycle notifications instead of
        // polling the native registry every camera/light.
        static bool cachedInstanceValid;

        [InitializeOnLoadMethod]
        static void RegisterInstanceCacheInvalidationCallbacks()
        {
            // Domain reloads clear the static event invocation lists, while
            // a package recompile can execute this method more than once in
            // the same editor domain.  Remove first to keep subscriptions
            // idempotent.
            EditorApplication.projectChanged -= InvalidateInstanceCache;
            EditorApplication.projectChanged += InvalidateInstanceCache;
            Undo.undoRedoPerformed -= InvalidateInstanceCache;
            Undo.undoRedoPerformed += InvalidateInstanceCache;
            EditorApplication.playModeStateChanged -= InvalidateInstanceCacheOnPlayModeChange;
            EditorApplication.playModeStateChanged += InvalidateInstanceCacheOnPlayModeChange;
            AssemblyReloadEvents.afterAssemblyReload -= InvalidateInstanceCache;
            AssemblyReloadEvents.afterAssemblyReload += InvalidateInstanceCache;

            RenderPipelineGraphicsSettingsCompatibility.globalSettingsChanged -= OnCompatibilityGlobalSettingsChanged;
            RenderPipelineGraphicsSettingsCompatibility.globalSettingsChanged += OnCompatibilityGlobalSettingsChanged;

            cachedInstance = null;
            cachedInstanceValid = false;
        }

        static void InvalidateInstanceCache()
        {
            cachedInstanceValid = false;
        }

        static void InvalidateInstanceCacheOnPlayModeChange(PlayModeStateChange state)
        {
            cachedInstanceValid = false;
        }

        static void OnCompatibilityGlobalSettingsChanged(
            Type renderPipelineType,
            RenderPipelineGlobalSettings settings)
        {
            // A null type is used by the bridge for a global unregister.  It
            // invalidates this typed cache; UpdateGraphicsSettings then sets
            // the deliberate null result explicitly.  Registration by a
            // different SRP also invalidates so the next access resolves the
            // correct native association.
            if (renderPipelineType == typeof(UniversalRenderPipeline))
            {
                cachedInstance = settings as UniversalRenderPipelineGlobalSettings;
                cachedInstanceValid = true;
            }
            else
            {
                cachedInstanceValid = false;
            }
        }

#endif

        static void SetCachedInstance(UniversalRenderPipelineGlobalSettings settings)
        {
            cachedInstance = settings;
#if UNITY_EDITOR && !UNITY_6000_0_OR_NEWER
            cachedInstanceValid = true;
#endif
        }
        /// <summary>
        /// Active URP Global Settings asset. If the value is null then no UniversalRenderPipelineGlobalSettings has been registered to the Graphics Settings with the UniversalRenderPipeline.
        /// </summary>
        public new static UniversalRenderPipelineGlobalSettings instance
        {
            get
            {
                // Keep this accessor equivalent to Unity 6's inherited
                // GlobalSettings.instance contract: resolving the registered
                // asset must be cheap and side-effect free.  In particular,
                // never run the Unity-2022 graphics-settings population here.
                // This property is read from the renderer hot path (including
                // once per rendering-layer query).  Population is
                // performed by Ensure(), deserialization, and explicit asset
                // updates below.
#if UNITY_EDITOR
#if UNITY_6000_0_OR_NEWER
                cachedInstance = EditorGraphicsSettings.GetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>() as UniversalRenderPipelineGlobalSettings;
#else
                if (!cachedInstanceValid)
                {
                    var resolved = GraphicsSettings.GetSettingsForRenderPipeline<UniversalRenderPipeline>() as UniversalRenderPipelineGlobalSettings;
                    // Re-register only when the cache is refreshed.  This
                    // repairs the package-side bridge for constructor-time
                    // lookups while keeping the steady-state getter a single
                    // managed branch.
                    RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(
                        resolved,
                        typeof(UniversalRenderPipeline));
                    SetCachedInstance(resolved);
                }
#endif
#else
                // The URP Global Settings could have been changed by script,
                // undo/redo, or file update.  In a Player only one asset is
                // supported, so retain the lazy cache used by Unity 6.
                if (cachedInstance == null)
                {
                    cachedInstance = GraphicsSettings.GetSettingsForRenderPipeline<UniversalRenderPipeline>() as UniversalRenderPipelineGlobalSettings;
#if !UNITY_6000_0_OR_NEWER
                    RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(
                        cachedInstance,
                        typeof(UniversalRenderPipeline));
#endif
                }
#endif
                return cachedInstance;
            }
        }

        static internal void UpdateGraphicsSettings(UniversalRenderPipelineGlobalSettings newSettings)
        {
            if (newSettings == cachedInstance)
            {
#if !UNITY_6000_0_OR_NEWER
                // Updating the registration is an explicit editor lifecycle
                // operation, so it remains a safe place to repair an older
                // asset whose managed graphics-settings list is incomplete.
                // Keep this work out of the frequently-read instance getter.
                if (newSettings != null)
                    newSettings.EnsureGraphicsSettings();
#endif
                SetCachedInstance(newSettings);
                return;
            }
            if (newSettings != null)
            {
#if !UNITY_6000_0_OR_NEWER
                // Materialize the package-owned equivalent of Unity 6's Tags
                // & Layers registry before this asset becomes globally visible.
                newSettings.GetActiveRenderingLayerNames();
#endif
                GraphicsSettings.RegisterRenderPipelineSettings<UniversalRenderPipeline>(newSettings as RenderPipelineGlobalSettings);
#if !UNITY_6000_0_OR_NEWER
                RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(
                    newSettings,
                    typeof(UniversalRenderPipeline));
                newSettings.EnsureGraphicsSettings();
#endif
            }
            else
            {
                GraphicsSettings.UnregisterRenderPipelineSettings<UniversalRenderPipeline>();
#if !UNITY_6000_0_OR_NEWER
                RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(null);
#endif
            }
            SetCachedInstance(newSettings);
        }

        /// <summary>Default name when creating an URP Global Settings asset.</summary>
        public const string defaultAssetName = "UniversalRenderPipelineGlobalSettings";

#if UNITY_EDITOR
        internal static string defaultPath => $"Assets/{defaultAssetName}.asset";

        //Making sure there is at least one UniversalRenderPipelineGlobalSettings instance in the project
        internal static UniversalRenderPipelineGlobalSettings Ensure(bool canCreateNewAsset = true)
        {
#if UNITY_EDITOR && !UNITY_6000_0_OR_NEWER
            // Ensure is an explicit editor lifecycle operation and may be
            // called after an asset was replaced outside this class.  Force
            // the next instance read to observe the registration performed
            // below instead of retaining a stale typed cache.
            InvalidateInstanceCache();
#endif
            UniversalRenderPipelineGlobalSettings currentInstance = GraphicsSettings
                .GetSettingsForRenderPipeline<UniversalRenderPipeline>() as UniversalRenderPipelineGlobalSettings;

            if (RenderPipelineGlobalSettingsUtils.TryEnsure<UniversalRenderPipelineGlobalSettings, UniversalRenderPipeline>(
                    ref currentInstance, defaultPath, canCreateNewAsset))
            {
#if !UNITY_6000_0_OR_NEWER
                bool settingsChanged = currentInstance.EnsureGraphicsSettings();
                RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(
                    currentInstance,
                    typeof(UniversalRenderPipeline));

                if (settingsChanged)
                    AssetDatabase.SaveAssetIfDirty(currentInstance);
#endif
                SetCachedInstance(currentInstance);
                return currentInstance;
            }

            return null;
        }

        internal static UniversalRenderPipelineGlobalSettings Create(string path, UniversalRenderPipelineGlobalSettings src = null)
        {
            UniversalRenderPipelineGlobalSettings assetCreated = null;

            // make sure the asset does not already exists
            assetCreated = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineGlobalSettings>(path);
            if (assetCreated == null)
            {
                assetCreated = ScriptableObject.CreateInstance<UniversalRenderPipelineGlobalSettings>();
                if (assetCreated != null)
                {
                    assetCreated.name = Path.GetFileName(path);
                }
                AssetDatabase.CreateAsset(assetCreated, path);
                Debug.Assert(assetCreated);
            }

            if (assetCreated)
            {
                if (src != null)
                    EditorUtility.CopySerializedManagedFieldsOnly(src, assetCreated);

#if !UNITY_6000_0_OR_NEWER
                assetCreated.EnsureGraphicsSettings();
#endif
                assetCreated.Initialize(src);

                EditorUtility.SetDirty(assetCreated);
                AssetDatabase.SaveAssetIfDirty(assetCreated);
                AssetDatabase.Refresh();
            }

            return assetCreated;
        }

        public override void Initialize(RenderPipelineGlobalSettings source = null)
        {
            if (source is UniversalRenderPipelineGlobalSettings globalSettingsSource)
            {
#if !UNITY_6000_0_OR_NEWER
                m_RenderingLayerNamesCompatibility =
                    (string[])globalSettingsSource.GetActiveRenderingLayerNames().Clone();
#endif
            }

            var defaultVolumeProfileSettings = GetOrCreateGraphicsSettings<URPDefaultVolumeProfileSettings>(this);
            defaultVolumeProfileSettings.volumeProfile = GetOrCreateDefaultVolumeProfile(defaultVolumeProfileSettings.volumeProfile);
        }

#endif

        /// <summary>
        /// Returns the project default volume profile, creating the same
        /// conventional asset used by Unity 6 when a project has none.
        /// </summary>
        internal static VolumeProfile GetOrCreateDefaultVolumeProfile(VolumeProfile defaultVolumeProfile)
        {
#if UNITY_EDITOR
            if (defaultVolumeProfile == null || defaultVolumeProfile.Equals(null))
            {
                const string k_DefaultVolumeProfileName = "DefaultVolumeProfile";
                const string k_DefaultVolumeProfilePath = "Assets/" + k_DefaultVolumeProfileName + ".asset";

                defaultVolumeProfile = CreateInstance<VolumeProfile>();
                Debug.Assert(defaultVolumeProfile);

                defaultVolumeProfile.name = k_DefaultVolumeProfileName;
                AssetDatabase.CreateAsset(defaultVolumeProfile, k_DefaultVolumeProfilePath);

                AssetDatabase.SaveAssetIfDirty(defaultVolumeProfile);
                AssetDatabase.Refresh();

                if (VolumeManager.instance.isInitialized && RenderPipelineManager.currentPipeline is UniversalRenderPipeline)
                    VolumeManager.instance.SetGlobalDefaultProfile(defaultVolumeProfile);
            }
#endif

            return defaultVolumeProfile;
        }

        /// <inheritdoc/>
        public override void Reset()
        {
#if UNITY_EDITOR && !UNITY_6000_0_OR_NEWER
            InvalidateInstanceCache();
#endif
#if !UNITY_6000_0_OR_NEWER && UNITY_EDITOR
            EnsureGraphicsSettings();
            Initialize();
#endif
            base.Reset();
            UpdateRenderingLayerNames();
        }

#if !UNITY_6000_0_OR_NEWER
        [SerializeField]
        string[] m_RenderingLayerNamesCompatibility;
#endif

        string[] renderingLayerNames
        {
            get
            {
#if UNITY_6000_0_OR_NEWER
                return RenderingLayerMask.GetDefinedRenderingLayerNames();
#else
                return GetActiveRenderingLayerNames();
#endif
            }
        }
        [System.NonSerialized]
        string[] m_PrefixedRenderingLayerNames;
        string[] prefixedRenderingLayerNames
        {
            get
            {
                if (m_PrefixedRenderingLayerNames == null)
                    UpdateRenderingLayerNames();
                return m_PrefixedRenderingLayerNames;
            }
        }
        /// <summary>Names used for display of rendering layer masks.</summary>
        public string[] renderingLayerMaskNames => renderingLayerNames;
        /// <summary>Names used for display of rendering layer masks with a prefix.</summary>
        public string[] prefixedRenderingLayerMaskNames => prefixedRenderingLayerNames;

        [SerializeField]
        uint m_ValidRenderingLayers;
        /// <summary>Valid rendering layers that can be used by graphics. </summary>
        public uint validRenderingLayers
        {
            get
            {
                if (m_PrefixedRenderingLayerNames == null)
                    UpdateRenderingLayerNames();

                return m_ValidRenderingLayers;
            }
        }
        /// <summary>Regenerate Rendering Layer names and their prefixed versions.</summary>
        internal void UpdateRenderingLayerNames()
        {
            var activeRenderingLayerNames = renderingLayerNames;

            // Update prefixed
            if (m_PrefixedRenderingLayerNames == null)
                m_PrefixedRenderingLayerNames = new string[32];
            for (int i = 0; i < m_PrefixedRenderingLayerNames.Length; ++i)
            {
                uint renderingLayer = (uint)(1 << i);

                m_ValidRenderingLayers = i < activeRenderingLayerNames.Length ? (m_ValidRenderingLayers | renderingLayer) : (m_ValidRenderingLayers & ~renderingLayer);
                m_PrefixedRenderingLayerNames[i] = i < activeRenderingLayerNames.Length ? activeRenderingLayerNames[i] : $"Unused Layer {i}";
            }

            // Update decals
            DecalProjector.UpdateAllDecalProperties();
        }

#if !UNITY_6000_0_OR_NEWER
        string[] GetActiveRenderingLayerNames()
        {
            if (m_RenderingLayerNamesCompatibility == null || m_RenderingLayerNamesCompatibility.Length == 0)
            {
                m_RenderingLayerNamesCompatibility = new[] { "Default" };
            }

            if (m_RenderingLayerNamesCompatibility.Length > 32)
                Array.Resize(ref m_RenderingLayerNamesCompatibility, 32);

            m_RenderingLayerNamesCompatibility[0] = "Default";
            return m_RenderingLayerNamesCompatibility;
        }

        bool TryAddRenderingLayerNameCompatibility(string name)
        {
            var names = GetActiveRenderingLayerNames();
            if (names.Length >= 32)
                return false;

            Array.Resize(ref m_RenderingLayerNamesCompatibility, names.Length + 1);
            m_RenderingLayerNamesCompatibility[names.Length] = name ?? string.Empty;
            RenderingLayerNamesChanged();
            return true;
        }

        bool TrySetRenderingLayerNameCompatibility(int index, string name)
        {
            var names = GetActiveRenderingLayerNames();
            if (index <= 0 || index >= names.Length)
                return false;

            m_RenderingLayerNamesCompatibility[index] = name ?? string.Empty;
            RenderingLayerNamesChanged();
            return true;
        }

        bool TryRemoveLastRenderingLayerNameCompatibility()
        {
            var names = GetActiveRenderingLayerNames();
            if (names.Length <= 1)
                return false;

            Array.Resize(ref m_RenderingLayerNamesCompatibility, names.Length - 1);
            RenderingLayerNamesChanged();
            return true;
        }

        void RenderingLayerNamesChanged()
        {
            m_PrefixedRenderingLayerNames = null;
            UpdateRenderingLayerNames();
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }
#endif

#if UNITY_EDITOR
        // Explicit compatibility facade for Unity 6's engine editor API.
        // Static methods cannot be supplied as C# extensions, and defining a
        // second RenderPipelineEditorUtility would clash with UnityEditor.dll.
        internal static bool TryAddRenderingLayerName(string name)
        {
#if UNITY_6000_0_OR_NEWER
            return RenderPipelineEditorUtility.TryAddRenderingLayerName(name);
#else
            var settings = instance;
            return settings != null && settings.TryAddRenderingLayerNameCompatibility(name);
#endif
        }

        internal static bool TrySetRenderingLayerName(int index, string name)
        {
#if UNITY_6000_0_OR_NEWER
            return RenderPipelineEditorUtility.TrySetRenderingLayerName(index, name);
#else
            var settings = instance;
            return settings != null && settings.TrySetRenderingLayerNameCompatibility(index, name);
#endif
        }

        internal static bool TryRemoveLastRenderingLayerName()
        {
#if UNITY_6000_0_OR_NEWER
            return RenderPipelineEditorUtility.TryRemoveLastRenderingLayerName();
#else
            var settings = instance;
            return settings != null && settings.TryRemoveLastRenderingLayerNameCompatibility();
#endif
        }
#endif

        internal void ResetRenderingLayerNames()
        {
#if !UNITY_6000_0_OR_NEWER
            m_RenderingLayerNamesCompatibility = new string[] { "Default" };
#endif
            m_PrefixedRenderingLayerNames = null;
        }

    }
}
