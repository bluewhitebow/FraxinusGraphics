#if !UNITY_6000_0_OR_NEWER
using System;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// Context passed to <see cref="GPUResidentDrawer.PostCullBeginCameraRendering"/>.
    /// Kept on Unity 2022 so Unity 6 render-pipeline code retains the same API shape.
    /// </summary>
    public struct RenderRequestBatcherContext
    {
        /// <summary>The command buffer used for resulting commands.</summary>
        public CommandBuffer commandBuffer;
    }

    /// <summary>The type of GPU-resident instance occlusion test.</summary>
    public enum OcclusionTest
    {
        /// <summary>No occlusion test.</summary>
        None,

        /// <summary>Test all instances against the latest occluders.</summary>
        TestAll,

        /// <summary>Test only instances culled by the previous pass.</summary>
        TestCulled,
    }

    /// <summary>Maps a culling split to an occluder subview.</summary>
    public struct SubviewOcclusionTest
    {
        /// <summary>The split index in the CPU culling output.</summary>
        public int cullingSplitIndex;

        /// <summary>The occluder subview index.</summary>
        public int occluderSubviewIndex;
    }

    /// <summary>Settings passed to a GPU-resident occlusion test.</summary>
    public struct OcclusionCullingSettings
    {
        /// <summary>The camera or view instance identifier.</summary>
        public int viewInstanceID;

        /// <summary>The requested occlusion test.</summary>
        public OcclusionTest occlusionTest;

        /// <summary>The instance multiplier for generated indirect draws.</summary>
        public int instanceMultiplier;

        /// <summary>Creates settings for the requested view and test.</summary>
        public OcclusionCullingSettings(int viewInstanceID, OcclusionTest occlusionTest)
        {
            this.viewInstanceID = viewInstanceID;
            this.occlusionTest = occlusionTest;
            instanceMultiplier = 1;
        }
    }

    /// <summary>Describes one occluder subview update.</summary>
    public struct OccluderSubviewUpdate
    {
        /// <summary>The subview index.</summary>
        public int subviewIndex;

        /// <summary>The depth texture slice.</summary>
        public int depthSliceIndex;

        /// <summary>The depth-region offset in pixels.</summary>
        public Vector2Int depthOffset;

        /// <summary>The world-to-view matrix.</summary>
        public Matrix4x4 viewMatrix;

        /// <summary>The view-to-world matrix.</summary>
        public Matrix4x4 invViewMatrix;

        /// <summary>The GPU projection matrix.</summary>
        public Matrix4x4 gpuProjMatrix;

        /// <summary>An additional world-space view offset.</summary>
        public Vector3 viewOffsetWorldSpace;

        /// <summary>Creates an update for a subview.</summary>
        public OccluderSubviewUpdate(int subviewIndex)
        {
            this.subviewIndex = subviewIndex;
            depthSliceIndex = 0;
            depthOffset = Vector2Int.zero;
            viewMatrix = Matrix4x4.identity;
            invViewMatrix = Matrix4x4.identity;
            gpuProjMatrix = Matrix4x4.identity;
            viewOffsetWorldSpace = Vector3.zero;
        }
    }

    /// <summary>Describes the depth data used to update instance occluders.</summary>
    public struct OccluderParameters
    {
        /// <summary>The camera or view instance identifier.</summary>
        public int viewInstanceID;

        /// <summary>The number of occluder subviews.</summary>
        public int subviewCount;

        /// <summary>The source depth texture.</summary>
        public TextureHandle depthTexture;

        /// <summary>The source depth size in pixels.</summary>
        public Vector2Int depthSize;

        /// <summary>Whether the source is a texture array.</summary>
        public bool depthIsArray;

        /// <summary>Creates parameters for a view.</summary>
        public OccluderParameters(int viewInstanceID)
        {
            this.viewInstanceID = viewInstanceID;
            subviewCount = 1;
            depthTexture = TextureHandle.nullHandle;
            depthSize = Vector2Int.zero;
            depthIsArray = false;
        }
    }

    /// <summary>
    /// Unity 2022 facade for Unity 6's native GPU Resident Drawer.
    /// The public render-pipeline surface is preserved, while every operation is
    /// inert because Unity 2022 has no corresponding native backend.
    /// </summary>
    public partial class GPUResidentDrawer
    {
        /// <summary>Unity 2022 never enables native instance occlusion culling.</summary>
        public static bool IsInstanceOcclusionCullingEnabled() => false;

        /// <summary>No-op on Unity 2022.</summary>
        public static void PostCullBeginCameraRendering(RenderRequestBatcherContext context)
        {
            // This is a per-camera hot-path hook. Unity 2022 has no native
            // GPU Resident Drawer instance to update, so keep it a true no-op.
            // Unsupported-mode diagnostics are emitted by the explicit
            // ReinitializeGPUResidentDrawer/asset-validation path instead.
        }

        /// <summary>No-op on Unity 2022.</summary>
        public static void OnSetupAmbientProbe()
        {
            GPUResidentDrawerCompatibility.ReportRequestedModeUnavailable();
        }

        /// <summary>No-op on Unity 2022.</summary>
        public static void InstanceOcclusionTest(
            RenderGraph renderGraph,
            in OcclusionCullingSettings settings,
            ReadOnlySpan<SubviewOcclusionTest> subviewOcclusionTests)
        {
            GPUResidentDrawerCompatibility.ReportRequestedModeUnavailable();
        }

        /// <summary>No-op on Unity 2022.</summary>
        public static void UpdateInstanceOccluders(
            RenderGraph renderGraph,
            in OccluderParameters occluderParameters,
            ReadOnlySpan<OccluderSubviewUpdate> occluderSubviewUpdates)
        {
            GPUResidentDrawerCompatibility.ReportRequestedModeUnavailable();
        }

        /// <summary>No-op on Unity 2022.</summary>
        public static void ReinitializeIfNeeded()
        {
            // Unity 6 polls this once per rendered frame because its native
            // drawer can be toggled outside the render-pipeline asset. There
            // is no such backend on Unity 2022, so no state can change here.
            // Keep diagnostics on the explicit reinitialization path rather
            // than querying GraphicsSettings from every frame.
        }

        /// <summary>No-op on Unity 2022.</summary>
        public static void RenderDebugOcclusionTestOverlay(
            RenderGraph renderGraph,
            DebugDisplayGPUResidentDrawer debugSettings,
            int viewInstanceID,
            TextureHandle colorBuffer)
        {
            GPUResidentDrawerCompatibility.ReportRequestedModeUnavailable();
        }

        /// <summary>No-op on Unity 2022.</summary>
        public static void RenderDebugOccluderOverlay(
            RenderGraph renderGraph,
            DebugDisplayGPUResidentDrawer debugSettings,
            Vector2 screenPos,
            float maxHeight,
            TextureHandle colorBuffer)
        {
            GPUResidentDrawerCompatibility.ReportRequestedModeUnavailable();
        }
    }
}
#endif
