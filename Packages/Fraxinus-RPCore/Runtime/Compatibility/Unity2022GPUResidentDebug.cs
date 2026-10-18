#if !UNITY_6000_0_OR_NEWER
using UnityEngine;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// Unity 2022 counterpart of the Unity 6 GPU resident drawer debug data.
    /// The 2022 engine has no native resident-drawer backend, but URP keeps
    /// this data object in its debugger tree so assets and tooling retain the
    /// Unity 6 shape.  All switches are inert and the panel is intentionally
    /// empty.
    /// </summary>
    public sealed class DebugDisplayGPUResidentDrawer : IDebugDisplaySettingsData
    {
        internal bool occlusionTestOverlayEnable { get; set; }
        internal bool occluderDebugViewEnable { get; set; }
        internal int occluderDebugViewIndex { get; set; }
        internal Vector2 occluderDebugViewRange { get; set; } = new Vector2(0f, 1f);

        public bool AreAnySettingsActive => false;
        public bool IsPostProcessingAllowed => true;
        public bool IsLightingActive => true;

        public bool TryGetScreenClearColor(ref Color color) => false;

        internal bool GetOccluderViewInstanceID(out int viewInstanceID)
        {
            viewInstanceID = 0;
            return false;
        }

        IDebugDisplaySettingsPanelDisposable IDebugDisplaySettingsData.CreatePanel()
        {
            return new EmptyPanel();
        }

        // DebugDisplaySettingsPanel discovers its ordering metadata through
        // reflection during construction.  Unity 6's real panel carries this
        // attribute; the inert 2022 facade must carry it as well, otherwise
        // every frame that initializes the Rendering Debugger emits
        // "Type ...EmptyPanel should specify DisplayInfoAttribute".
        [DisplayInfo(name = "GPU Resident Drawer", order = 5)]
        sealed class EmptyPanel : DebugDisplaySettingsPanel
        {
            public override string PanelName => "GPU Resident Drawer";
        }
    }
}
#endif
