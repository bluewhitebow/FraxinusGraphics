#if !UNITY_6000_0_OR_NEWER
// Local Unity 2022 compatibility facade.
namespace UnityEngine.Rendering
{
    /// <summary>
    /// Modes exposed by the Unity 6 GPU Resident Drawer API.
    /// Unity 2022 preserves both values so serialized Unity 6 assets can be
    /// round-tripped without silently changing their authored configuration.
    /// The native backend remains unavailable and is reported separately.
    /// </summary>
    public enum GPUResidentDrawerMode : byte
    {
        /// <summary>The GPU Resident Drawer is disabled.</summary>
        Disabled,

        /// <summary>
        /// Requests instanced drawing when a native Unity 6 backend is present.
        /// On Unity 2022 this is a serialized compatibility value only; it does
        /// not activate the native resident drawer.
        /// </summary>
        InstancedDrawing
    }

    /// <summary>
    /// Source-compatible settings surface for Unity 6 render-pipeline assets.
    /// The settings are preserved on Unity 2022, while the native drawer
    /// capability is reported independently.
    /// </summary>
    public struct GPUResidentDrawerSettings
    {
        /// <summary>The configured drawer mode.</summary>
        public GPUResidentDrawerMode mode;

        /// <summary>Whether dithered cross-fade would be supported.</summary>
        public bool supportDitheringCrossFade;

        /// <summary>Whether GPU occlusion culling would be enabled.</summary>
        public bool enableOcclusionCulling;

        /// <summary>Whether edit-mode drawing would be allowed.</summary>
        public bool allowInEditMode;

        /// <summary>The minimum screen percentage used for small-mesh culling.</summary>
        public float smallMeshScreenPercentage;

#if UNITY_EDITOR
        /// <summary>The optional picking shader.</summary>
        public Shader pickingShader;
#endif

        /// <summary>The fallback error shader.</summary>
        public Shader errorShader;

        /// <summary>The fallback loading shader.</summary>
        public Shader loadingShader;
    }

    /// <summary>
    /// Unity 2022 compatibility contract for assets authored against the Unity 6
    /// GPU Resident Drawer surface. Configuration remains serializable, but the
    /// Unity 6 native drawer runtime is not available in this player version.
    /// </summary>
    public interface IGPUResidentRenderPipeline
    {
        /// <summary>Gets the compatibility settings.</summary>
        GPUResidentDrawerSettings gpuResidentDrawerSettings { get; }

        /// <summary>Gets or sets the serialized compatibility mode.</summary>
        GPUResidentDrawerMode gpuResidentDrawerMode { get; set; }

        /// <summary>
        /// Unity 6 callback used to rebuild the resident drawer after an asset
        /// change. Unity 2022 has no engine resident-drawer backend, so this is
        /// intentionally a no-op; external BRG backends own their own lifecycle.
        /// </summary>
        public static void ReinitializeGPUResidentDrawer()
        {
            GPUResidentDrawerCompatibility.ReportRequestedModeUnavailable();
        }

        /// <summary>
        /// Reports whether the project can use the native resident drawer. The
        /// 2022 compatibility target always returns false because its native
        /// GPU-resident implementation is not available.
        /// </summary>
        public static bool IsGPUResidentDrawerSupportedByProjectConfiguration(bool logReason = false)
        {
            if (logReason)
                GPUResidentDrawerCompatibility.LogNativeBackendUnavailable();
            return false;
        }

        /// <summary>Returns false on Unity 2022.</summary>
        public static bool IsGPUResidentDrawerEnabled() => false;

        /// <summary>Returns whether the current SRP supports the drawer.</summary>
        bool IsGPUResidentDrawerSupportedBySRP(bool logReason = false)
        {
            bool supported = IsGPUResidentDrawerSupportedBySRP(out var message, out var severity);
            if (logReason && !supported && !string.IsNullOrEmpty(message))
            {
                switch (severity)
                {
                    case LogType.Error:
                    case LogType.Exception:
                    case LogType.Assert:
                        Debug.LogError(message);
                        break;
                    case LogType.Warning:
                        Debug.LogWarning(message);
                        break;
                    default:
                        Debug.Log(message);
                        break;
                }
            }

            return supported;
        }

        /// <summary>Returns false and describes why the drawer is unavailable.</summary>
        bool IsGPUResidentDrawerSupportedBySRP(out string message, out LogType severity)
        {
            message = GPUResidentDrawerCompatibility.NativeBackendUnavailableReason;
            severity = LogType.Warning;
            return false;
        }
    }

    /// <summary>
    /// Capability and diagnostics for the Unity 2022 GPU Resident Drawer facade.
    /// This class describes only the native Unity backend; independent packages
    /// such as FraxinusBRG must expose and report their own backend status.
    /// </summary>
    public static class GPUResidentDrawerCompatibility
    {
        static bool s_LoggedRequestedModeUnavailable;

        public const string NativeBackendUnavailableReason =
            "Unity GPU Resident Drawer native backend is unavailable on Unity 2022.3.";

        /// <summary>Unity 2022 has no native GPU Resident Drawer backend.</summary>
        public static bool IsNativeBackendAvailable => false;

        /// <summary>The native Unity backend can never be enabled on this target.</summary>
        public static bool IsNativeBackendEnabled => false;

        /// <summary>Returns the reason the native backend is unavailable.</summary>
        public static string GetNativeBackendUnavailableReason() =>
            NativeBackendUnavailableReason;

        /// <summary>
        /// Logs the native capability failure without claiming that an external
        /// BatchRendererGroup backend is unavailable.
        /// </summary>
        public static void LogNativeBackendUnavailable()
        {
            Debug.LogWarning(NativeBackendUnavailableReason +
                " An external BRG backend, when installed, is a separate capability.");
        }

        internal static void ReportRequestedModeUnavailable()
        {
            if (s_LoggedRequestedModeUnavailable)
                return;

            var pipeline = GraphicsSettings.currentRenderPipeline as IGPUResidentRenderPipeline
                ?? GraphicsSettings.renderPipelineAsset as IGPUResidentRenderPipeline;
            if (pipeline == null || pipeline.gpuResidentDrawerMode == GPUResidentDrawerMode.Disabled)
                return;

            s_LoggedRequestedModeUnavailable = true;
            LogNativeBackendUnavailable();
        }
    }
}
#endif
