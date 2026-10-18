using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Renders a shadow map for the main Light.
    /// </summary>
    public class MainLightShadowCasterPass : ScriptableRenderPass
    {
        // Internal
        internal RTHandle m_MainLightShadowmapTexture;

        // Private
        private int renderTargetWidth;
        private int renderTargetHeight;
        private int m_ShadowCasterCascadesCount;
        private bool m_CreateEmptyShadowmap;
        private bool m_SetKeywordForEmptyShadowmap;
        private Vector4 m_EmptyShadowParams;
        private float m_CascadeBorder;
        private float m_MaxShadowDistanceSq;
        private Vector4[] m_CascadeSplitDistances;
        private Matrix4x4[] m_MainLightShadowMatrices;
        private ProfilingSampler m_ProfilingSetupSampler = new ("Setup Main Shadowmap");
        private ShadowSliceData[] m_CascadeSlices;
        private RenderTextureDescriptor m_MainLightShadowDescriptor;
        private bool m_CachedMode;
        private bool m_CachedShadowmapNeedsClear;
        private int m_CascadeUpdateMask = -1;
        private ShadowUtils.ShadowLightSnapshot m_MainLightSnapshot;
        private bool m_MainLightSnapshotValid;
        // Diagnostic state also protects against accidentally reintroducing the
        // hand-built indirect matrix path for mixed native/indirect scenes.
        private bool m_UsedIndirectMatrices;

        private sealed class CachedIndirectShadowState
        {
            internal bool atlasMayContainIndirectCasters;
            internal bool currentSetupHasIndirectCasters;
            internal bool atlasNeedsClear;
        }

        private readonly ConditionalWeakTable<CachedCSMPersistentData, CachedIndirectShadowState>
            m_CachedIndirectShadowStates = new();

        // Constants and Statics
        private const int k_MaxCascades = 4;
        private const int k_ShadowmapBufferBits = 16;
        private const string k_MainLightShadowMapTextureName = "_MainLightShadowmapTexture";
        private const int k_EmptyShadowMapDimensions = 1;
        private static readonly Vector4 s_EmptyShadowParams = new (0f, 0f, 1f, 0f);
        private static readonly Vector4 s_EmptyShadowmapSize = new (k_EmptyShadowMapDimensions, 1f / k_EmptyShadowMapDimensions, k_EmptyShadowMapDimensions, k_EmptyShadowMapDimensions);

        // Classes
        private static class MainLightShadowConstantBuffer
        {
            public static readonly int _WorldToShadow = Shader.PropertyToID("_MainLightWorldToShadow");
            public static readonly int _ShadowParams = Shader.PropertyToID("_MainLightShadowParams");
            public static readonly int _CascadeShadowSplitSpheres0 = Shader.PropertyToID("_CascadeShadowSplitSpheres0");
            public static readonly int _CascadeShadowSplitSpheres1 = Shader.PropertyToID("_CascadeShadowSplitSpheres1");
            public static readonly int _CascadeShadowSplitSpheres2 = Shader.PropertyToID("_CascadeShadowSplitSpheres2");
            public static readonly int _CascadeShadowSplitSpheres3 = Shader.PropertyToID("_CascadeShadowSplitSpheres3");
            public static readonly int _CascadeShadowSplitSphereRadii = Shader.PropertyToID("_CascadeShadowSplitSphereRadii");
            public static readonly int _ShadowOffset0 = Shader.PropertyToID("_MainLightShadowOffset0");
            public static readonly int _ShadowOffset1 = Shader.PropertyToID("_MainLightShadowOffset1");
            public static readonly int _ShadowmapSize = Shader.PropertyToID("_MainLightShadowmapSize");
            public static readonly int _MainLightShadowmapID = Shader.PropertyToID(k_MainLightShadowMapTextureName);
        }

        private class PassData
        {
            internal UniversalRenderingData renderingData;
            internal UniversalCameraData cameraData;
            internal UniversalLightData lightData;
            internal UniversalShadowData shadowData;

            internal MainLightShadowCasterPass pass;

            internal bool emptyShadowmap;
            internal bool setKeywordForEmptyShadowmap;
            internal Vector4 emptyShadowParams;

            internal readonly RendererListHandle[] shadowRendererListsHandle = new RendererListHandle[k_MaxCascades];
            internal readonly List<Action<RasterCommandBuffer>>[] shadowSliceRenderFuncs =
                new List<Action<RasterCommandBuffer>>[k_MaxCascades];
            internal readonly List<ShadowUtils.RenderGraphBufferUse>[] shadowSliceBufferUses =
                new List<ShadowUtils.RenderGraphBufferUse>[k_MaxCascades];
            internal readonly List<ShadowUtils.RenderGraphTextureUse>[] shadowSliceTextureUses =
                new List<ShadowUtils.RenderGraphTextureUse>[k_MaxCascades];

            internal void ClearRecordingLists()
            {
                for (int index = 0; index < shadowSliceRenderFuncs.Length; ++index)
                {
                    shadowSliceRenderFuncs[index]?.Clear();
                    shadowSliceBufferUses[index]?.Clear();
                    shadowSliceTextureUses[index]?.Clear();
                }
            }
        }

        private sealed class PreparedPassData : PassData, IDisposable
        {
            public void Dispose()
            {
                ClearRecordingLists();
                pass = null;
                renderingData = null;
                cameraData = null;
                lightData = null;
                shadowData = null;
                GenericPool<PreparedPassData>.Release(this);
            }
        }

        /// <summary>
        /// Creates a new <c>MainLightShadowCasterPass</c> instance.
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <seealso cref="RenderPassEvent"/>
        public MainLightShadowCasterPass(RenderPassEvent evt)
        {
            profilingSampler = new ProfilingSampler("Draw Main Light Shadowmap");
            renderPassEvent = evt;

            m_MainLightShadowMatrices = new Matrix4x4[k_MaxCascades + 1];
            m_CascadeSlices = new ShadowSliceData[k_MaxCascades];
            m_CascadeSplitDistances = new Vector4[k_MaxCascades];

        }

        /// <summary>
        /// Cleans up resources used by the pass.
        /// </summary>
        public void Dispose()
        {
            m_MainLightShadowmapTexture = null;
        }

        /// <summary>
        /// Sets up the pass.
        /// </summary>
        /// <param name="renderingData"></param>
        /// <returns>True if the pass should be enqueued, otherwise false.</returns>
        /// <seealso cref="RenderingData"/>
        public bool Setup(ref RenderingData renderingData)
        {
            ContextContainer frameData = renderingData.frameData;
            UniversalRenderingData universalRenderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();
            return Setup(default, universalRenderingData, cameraData, lightData, shadowData);
        }

        /// <summary>
        /// Sets up the pass.
        /// </summary>
        /// <param name="renderingData">Data containing rendering settings.</param>
        /// <param name="cameraData">Data containing camera settings.</param>
        /// <param name="lightData">Data containing light settings.</param>
        /// <param name="shadowData">Data containing shadow settings.</param>
        /// <returns>True if the pass should be enqueued, otherwise false.</returns>
        /// <seealso cref="RenderingData"/>
        public bool Setup(UniversalRenderingData renderingData, UniversalCameraData cameraData, UniversalLightData lightData, UniversalShadowData shadowData)
        {
            return Setup(default, renderingData, cameraData, lightData, shadowData);
        }

        internal bool Setup(ScriptableRenderContext renderContext,
            UniversalRenderingData renderingData, UniversalCameraData cameraData,
            UniversalLightData lightData, UniversalShadowData shadowData)
        {
            ResetPerCameraStateBeforeSetup();

            if (shadowData == null || cameraData == null || lightData == null ||
                renderingData == null)
                return false;

            bool shadowsEnabled = shadowData.mainLightShadowsEnabled;
            bool shadowsSupported = shadowData.supportsMainLightShadows;

            bool stripShadowsOffVariants = cameraData.renderer != null &&
                cameraData.renderer.stripShadowsOffVariants;

#if UNITY_EDITOR
            if (CoreUtils.IsSceneLightingDisabled(cameraData.camera))
                return false;
#endif

            using var profScope = new ProfilingScope(m_ProfilingSetupSampler);

            int shadowLightIndex = lightData.mainLightIndex;
            if (!lightData.visibleLights.IsCreated || shadowLightIndex < 0 ||
                shadowLightIndex >= lightData.visibleLights.Length)
                return shadowsEnabled && SetupForEmptyRendering(stripShadowsOffVariants,
                    shadowsEnabled, null, cameraData, shadowData);

            VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
            Light light = shadowLight.light;
            if (light == null)
                return shadowsEnabled && SetupForEmptyRendering(stripShadowsOffVariants,
                    shadowsEnabled, null, cameraData, shadowData);

            if (shadowsSupported && light.shadows == LightShadows.None)
                return SetupForEmptyRendering(stripShadowsOffVariants,
                    shadowsEnabled, light, cameraData, shadowData);

            if (!shadowsEnabled)
            {
                // Shadow masks still use the main-light strength when realtime shadows
                // are disabled. Publish their parameters without enabling realtime shadows.
                if (light.shadows != LightShadows.None && light.bakingOutput.isBaked &&
                    light.bakingOutput.mixedLightingMode != MixedLightingMode.IndirectOnly &&
                    light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed)
                    return SetupForEmptyRendering(stripShadowsOffVariants,
                        shadowsEnabled, light, cameraData, shadowData);

                return false;
            }

            if (!shadowsSupported)
                return SetupForEmptyRendering(stripShadowsOffVariants,
                    shadowsEnabled, null, cameraData, shadowData);

            // Validate both representations. Custom culling integrations can leave the
            // VisibleLight metadata stale while the Unity Light has already changed type; the
            // native directional-matrix API must never receive a punctual light through either
            // route.
            if (shadowLight.lightType != LightType.Directional ||
                light.type != LightType.Directional)
            {
                Debug.LogWarning("Only directional lights are supported as main light.");
                // Directional cascades are the only matrices exposed by the main-light native
                // API. A malformed/custom culling result must not route a spot or point light
                // through that API; use the normal empty-shadow fallback for this camera.
                return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);
            }

            bool hasRendererShadowCasters = renderingData.cullResults.GetShadowCasterBounds(
                shadowLightIndex, out Bounds rendererBounds);
            bool hasLightSnapshot = ShadowUtils.ShadowLightSnapshot.TryCapture(
                in shadowLight, out ShadowUtils.ShadowLightSnapshot lightSnapshot);
            m_MainLightSnapshot = lightSnapshot;
            m_MainLightSnapshotValid = hasLightSnapshot;
            bool hasIndirectShadowCasters = ShadowUtils.ShouldKeepMainLightShadowSlices(
                renderContext, cameraData.camera, light,
                hasLightSnapshot ? lightSnapshot : default, shadowLightIndex);
            if (!hasRendererShadowCasters && !hasIndirectShadowCasters)
                return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);

            m_ShadowCasterCascadesCount = shadowData.mainLightShadowCascadesCount;
            if (m_ShadowCasterCascadesCount <= 0 ||
                m_ShadowCasterCascadesCount > k_MaxCascades)
                return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);

            CachedCSMPersistentData cachedCsmPersistentData =
                cameraData.cachedCSMPersistentData;
            Bounds indirectBounds = default;
            // When a GPU driven renderer contributes casters, Unity's CullingResults do not
            // contain those instances. Use the indirect bounds path so its light-space depth
            // range includes those casters while the receiver sphere and texel snapping remain
            // stable across the frame.
            bool useIndirectMatrices = hasIndirectShadowCasters &&
                ShadowUtils.TryGetIndirectMainLightShadowBounds(cameraData.camera, light, out indirectBounds);
            m_UsedIndirectMatrices = useIndirectMatrices;
            if (useIndirectMatrices && hasRendererShadowCasters)
                indirectBounds.Encapsulate(rendererBounds);
            if (useIndirectMatrices && cachedCsmPersistentData != null)
                cachedCsmPersistentData.forceUpdate = true;
            if (!useIndirectMatrices && cachedCsmPersistentData != null && cachedCsmPersistentData.Init(shadowData))
            {
                CachedIndirectShadowState indirectShadowState =
                    m_CachedIndirectShadowStates.GetOrCreateValue(cachedCsmPersistentData);
                indirectShadowState.currentSetupHasIndirectCasters = hasIndirectShadowCasters;
                if (hasIndirectShadowCasters)
                    indirectShadowState.atlasMayContainIndirectCasters = true;
                if (indirectShadowState.atlasMayContainIndirectCasters ||
                    indirectShadowState.atlasNeedsClear)
                    cachedCsmPersistentData.forceUpdate = true;

                if (!cachedCsmPersistentData.CalculateCascadeSlices(
                        ref renderingData.cullResults, shadowData, shadowLightIndex,
                        light.shadowNearPlane, m_CascadeSplitDistances, m_CascadeSlices))
                    return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);

                // RenderGraph imports this per-camera atlas instead of allocating a transient
                // shadow map. Keep the pending clear beside the per-camera persistent state so
                // an aborted graph cannot leave a newly allocated atlas marked initialized.
                bool cachedShadowmapReallocated = cachedCsmPersistentData.AllocateTargets();
                if (cachedCsmPersistentData.cachedCascadeShadowMap == null)
                    return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);

                if (cachedShadowmapReallocated)
                    indirectShadowState.atlasNeedsClear = true;

                renderTargetWidth = cachedCsmPersistentData.renderTargetWidth;
                renderTargetHeight = cachedCsmPersistentData.renderTargetHeight;
                m_MainLightShadowmapTexture = cachedCsmPersistentData.cachedCascadeShadowMap;
                m_CachedMode = true;
                m_CachedShadowmapNeedsClear = indirectShadowState.atlasNeedsClear;
                m_CascadeUpdateMask = cachedShadowmapReallocated
                    ? (1 << m_ShadowCasterCascadesCount) - 1
                    : cachedCsmPersistentData.cascadeUpdateMask;
            }
            else
            {
                if (cachedCsmPersistentData != null)
                    m_CachedIndirectShadowStates.Remove(cachedCsmPersistentData);

                renderTargetWidth = shadowData.mainLightRenderTargetWidth;
                renderTargetHeight = shadowData.mainLightRenderTargetHeight;
                if (renderTargetWidth <= 0 || renderTargetHeight <= 0)
                    return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);

                URPLightShadowCullingInfos shadowCullingInfos = default;
                if (!useIndirectMatrices)
                {
                    if (!shadowData.visibleLightsShadowCullingInfos.IsCreated ||
                        shadowLightIndex >= shadowData.visibleLightsShadowCullingInfos.Length)
                        return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);
                    shadowCullingInfos = shadowData.visibleLightsShadowCullingInfos.UnsafeElementAt(shadowLightIndex);
                    if (!shadowCullingInfos.slices.IsCreated ||
                        shadowCullingInfos.slices.Length < m_ShadowCasterCascadesCount)
                        return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);
                }

                for (int cascadeIndex = 0;
                    cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
                {
                    if (useIndirectMatrices)
                    {
                        if (!ShadowUtils.ExtractIndirectDirectionalLightMatrix(cameraData, shadowData,
                                light, indirectBounds, cascadeIndex, renderTargetWidth, renderTargetHeight,
                                out m_CascadeSlices[cascadeIndex]))
                            return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);
                        m_CascadeSplitDistances[cascadeIndex] = m_CascadeSlices[cascadeIndex].splitData.cullingSphere;
                        continue;
                    }
                    if (!shadowCullingInfos.IsSliceValid(cascadeIndex))
                        return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);

                    ref readonly ShadowSliceData sliceData = ref
                        shadowCullingInfos.slices.UnsafeElementAt(cascadeIndex);
                    m_CascadeSplitDistances[cascadeIndex] =
                        sliceData.splitData.cullingSphere;
                    m_CascadeSlices[cascadeIndex] = sliceData;
                }

                m_MainLightShadowmapTexture = null;
            }

            UpdateTextureDescriptorIfNeeded();

            m_MaxShadowDistanceSq = cameraData.maxShadowDistance * cameraData.maxShadowDistance;
            m_CascadeBorder = shadowData.mainLightShadowCascadeBorder;
            m_CreateEmptyShadowmap = false;

            return true;
        }

        private void ResetPerCameraStateBeforeSetup()
        {
            m_CreateEmptyShadowmap = false;
            m_SetKeywordForEmptyShadowmap = false;
            m_EmptyShadowParams = s_EmptyShadowParams;
            m_CachedMode = false;
            m_CachedShadowmapNeedsClear = false;
            m_CascadeUpdateMask = -1;
            m_ShadowCasterCascadesCount = 0;
            m_MainLightSnapshot = default;
            m_MainLightSnapshotValid = false;
            renderTargetWidth = 0;
            renderTargetHeight = 0;
            m_MainLightShadowmapTexture = null;
            Clear();
        }

        private void UpdateTextureDescriptorIfNeeded()
        {
            if (   m_MainLightShadowDescriptor.width != renderTargetWidth
                || m_MainLightShadowDescriptor.height != renderTargetHeight
                || m_MainLightShadowDescriptor.depthBufferBits != k_ShadowmapBufferBits
                || m_MainLightShadowDescriptor.colorFormat != RenderTextureFormat.Shadowmap)
            {
                m_MainLightShadowDescriptor = new RenderTextureDescriptor(renderTargetWidth, renderTargetHeight, RenderTextureFormat.Shadowmap, k_ShadowmapBufferBits);
            }
        }

        bool SetupForEmptyRendering(bool stripShadowsOffVariants, bool shadowsEnabled,
            Light light, UniversalCameraData cameraData, UniversalShadowData shadowData)
        {
            if (!stripShadowsOffVariants)
                return false;

            m_CreateEmptyShadowmap = true;
            m_SetKeywordForEmptyShadowmap = shadowsEnabled;

            if (light == null)
            {
                m_EmptyShadowParams = s_EmptyShadowParams;
            }
            else
            {
                bool softShadows = light.shadows == LightShadows.Soft && shadowData.supportsSoftShadows;
                float softShadowsProp = ShadowUtils.SoftShadowQualityToShaderProperty(light, softShadows);
                ShadowUtils.GetScaleAndBiasForLinearDistanceFade(cameraData.maxShadowDistance,
                    shadowData.mainLightShadowCascadeBorder, out float shadowFadeScale, out float shadowFadeBias);
                m_EmptyShadowParams = new Vector4(light.shadowStrength, softShadowsProp,
                    shadowFadeScale, shadowFadeBias);
            }

            return true;
        }

        void Clear()
        {
            for (int i = 0; i < m_MainLightShadowMatrices.Length; ++i)
                m_MainLightShadowMatrices[i] = Matrix4x4.identity;

            for (int i = 0; i < m_CascadeSplitDistances.Length; ++i)
                m_CascadeSplitDistances[i] = new Vector4(0.0f, 0.0f, 0.0f, 0.0f);

            for (int i = 0; i < m_CascadeSlices.Length; ++i)
                m_CascadeSlices[i].Clear();
        }

        static void SetEmptyMainLightCascadeShadowmap(RasterCommandBuffer cmd, PassData data)
        {
            if (data.setKeywordForEmptyShadowmap)
                cmd.EnableKeyword(ShaderGlobalKeywords.MainLightShadows);
            cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowParams, data.emptyShadowParams);
            cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowmapSize, s_EmptyShadowmapSize);
        }

        internal static void SetEmptyMainLightShadowParams(RasterCommandBuffer cmd)
        {
            cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowParams, s_EmptyShadowParams);
            cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowmapSize, s_EmptyShadowmapSize);
        }

        private void RenderMainLightCascadeShadowmap(RasterCommandBuffer cmd, ref PassData data)
        {
            var lightData = data.lightData;

            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex < 0 || shadowLightIndex >= lightData.visibleLights.Length)
                return;

            VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
            Light shadowLightObject = shadowLight.light;
            if (shadowLightObject == null)
                return;

            using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.MainLightShadow)))
            {
                // Need to start by setting the Camera position and worldToCamera Matrix as that is not set for passes executed before normal rendering
                ShadowUtils.SetCameraPosition(cmd, data.cameraData.worldSpaceCameraPos);

                for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
                {
                    if ((m_CascadeUpdateMask & (1 << cascadeIndex)) == 0)
                        continue;

                    Vector4 shadowBias = ShadowUtils.GetShadowBias(ref shadowLight, shadowLightIndex, data.shadowData, m_CascadeSlices[cascadeIndex].projectionMatrix, m_CascadeSlices[cascadeIndex].resolution);
                    ShadowUtils.SetupShadowCasterConstantBuffer(cmd, ref shadowLight, shadowBias);
                    cmd.SetKeyword(ShaderGlobalKeywords.CastingPunctualLightShadow, false);
                    if (m_CachedMode && !m_CachedShadowmapNeedsClear)
                    {
                        ShadowSliceData slice = m_CascadeSlices[cascadeIndex];
                        cmd.SetViewport(new Rect(slice.offsetX, slice.offsetY,
                            slice.resolution, slice.resolution));
                        cmd.ClearRenderTarget(true, false, Color.black);
                    }
                    RendererList shadowRendererList = data.shadowRendererListsHandle[cascadeIndex];
                    ShadowUtils.RenderShadowSlice(cmd, ref m_CascadeSlices[cascadeIndex], ref shadowRendererList,
                        m_CascadeSlices[cascadeIndex].projectionMatrix,
                        m_CascadeSlices[cascadeIndex].viewMatrix,
                        data.shadowSliceRenderFuncs[cascadeIndex]);
                }

                data.shadowData.isKeywordSoftShadowsEnabled = shadowLight.light.shadows == LightShadows.Soft && data.shadowData.supportsSoftShadows;
                cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadows, data.shadowData.mainLightShadowCascadesCount == 1);
                cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadowCascades, data.shadowData.mainLightShadowCascadesCount > 1);
                ShadowUtils.SetSoftShadowQualityShaderKeywords(cmd, data.shadowData);

                SetupMainLightShadowReceiverConstants(cmd, ref shadowLight, data.shadowData);
            }

            CompleteCachedShadowRendering(data.cameraData);
        }

        private void CompleteCachedShadowRendering(UniversalCameraData cameraData)
        {
            if (!m_CachedMode || cameraData?.cachedCSMPersistentData == null ||
                !m_CachedIndirectShadowStates.TryGetValue(
                    cameraData.cachedCSMPersistentData,
                    out CachedIndirectShadowState cachedShadowState))
                return;

            cachedShadowState.atlasMayContainIndirectCasters =
                cachedShadowState.currentSetupHasIndirectCasters;
            cachedShadowState.atlasNeedsClear = false;
            m_CachedShadowmapNeedsClear = false;
        }

        void SetupMainLightShadowReceiverConstants(RasterCommandBuffer cmd, ref VisibleLight shadowLight, UniversalShadowData shadowData)
        {
            Light light = shadowLight.light;
            bool softShadows = shadowLight.light.shadows == LightShadows.Soft && shadowData.supportsSoftShadows;

            int cascadeCount = m_ShadowCasterCascadesCount;
            for (int i = 0; i < cascadeCount; ++i)
                m_MainLightShadowMatrices[i] = m_CascadeSlices[i].shadowTransform;

            // We setup and additional a no-op WorldToShadow matrix in the last index
            // because the ComputeCascadeIndex function in Shadows.hlsl can return an index
            // out of bounds. (position not inside any cascade) and we want to avoid branching
            Matrix4x4 noOpShadowMatrix = Matrix4x4.zero;
            noOpShadowMatrix.m22 = (SystemInfo.usesReversedZBuffer) ? 1.0f : 0.0f;
            for (int i = cascadeCount; i <= k_MaxCascades; ++i)
                m_MainLightShadowMatrices[i] = noOpShadowMatrix;

            // Setup rejects zero-sized atlases, but keep receiver constants finite if custom
            // pipeline data mutates the shared pass before a delayed render function executes.
            int safeRenderTargetWidth = Math.Max(1, renderTargetWidth);
            int safeRenderTargetHeight = Math.Max(1, renderTargetHeight);
            float invShadowAtlasWidth = 1.0f / safeRenderTargetWidth;
            float invShadowAtlasHeight = 1.0f / safeRenderTargetHeight;
            float invHalfShadowAtlasWidth = 0.5f * invShadowAtlasWidth;
            float invHalfShadowAtlasHeight = 0.5f * invShadowAtlasHeight;
            float softShadowsProp = ShadowUtils.SoftShadowQualityToShaderProperty(light, softShadows);

            ShadowUtils.GetScaleAndBiasForLinearDistanceFade(m_MaxShadowDistanceSq, m_CascadeBorder, out float shadowFadeScale, out float shadowFadeBias);

            cmd.SetGlobalMatrixArray(MainLightShadowConstantBuffer._WorldToShadow, m_MainLightShadowMatrices);
            cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowParams,
                new Vector4(light.shadowStrength, softShadowsProp, shadowFadeScale, shadowFadeBias));

            if (m_ShadowCasterCascadesCount > 1)
            {
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres0,
                    m_CascadeSplitDistances[0]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres1,
                    m_CascadeSplitDistances[1]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres2,
                    m_CascadeSplitDistances[2]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres3,
                    m_CascadeSplitDistances[3]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSphereRadii, new Vector4(
                    m_CascadeSplitDistances[0].w * m_CascadeSplitDistances[0].w,
                    m_CascadeSplitDistances[1].w * m_CascadeSplitDistances[1].w,
                    m_CascadeSplitDistances[2].w * m_CascadeSplitDistances[2].w,
                    m_CascadeSplitDistances[3].w * m_CascadeSplitDistances[3].w));
            }

            // Inside shader soft shadows are controlled through global keyword.
            // If any additional light has soft shadows it will force soft shadows on main light too.
            // As it is not trivial finding out which additional light has soft shadows, we will pass main light properties if soft shadows are supported.
            // This workaround will be removed once we will support soft shadows per light.
            if (shadowData.supportsSoftShadows)
            {
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowOffset0,
                    new Vector4(-invHalfShadowAtlasWidth, -invHalfShadowAtlasHeight,
                        invHalfShadowAtlasWidth, -invHalfShadowAtlasHeight));
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowOffset1,
                    new Vector4(-invHalfShadowAtlasWidth, invHalfShadowAtlasHeight,
                        invHalfShadowAtlasWidth, invHalfShadowAtlasHeight));

                cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowmapSize, new Vector4(invShadowAtlasWidth,
                    invShadowAtlasHeight,
                    safeRenderTargetWidth, safeRenderTargetHeight));
            }
        }

        private void InitPassData(
            ref PassData passData,
            UniversalRenderingData renderingData,
            UniversalCameraData cameraData,
            UniversalLightData lightData,
            UniversalShadowData shadowData)
        {
            passData.ClearRecordingLists();
            passData.pass = this;

            passData.emptyShadowmap = m_CreateEmptyShadowmap;
            passData.setKeywordForEmptyShadowmap = m_SetKeywordForEmptyShadowmap;
            passData.emptyShadowParams = m_EmptyShadowParams;
            passData.renderingData = renderingData;
            passData.cameraData = cameraData;
            passData.lightData = lightData;
            passData.shadowData = shadowData;
        }

        private void InitRendererLists(ref PassData passData, RenderGraph renderGraph)
        {
            Array.Clear(passData.shadowRendererListsHandle, 0, passData.shadowRendererListsHandle.Length);
            int shadowLightIndex = passData.lightData.mainLightIndex;
            if (!m_CreateEmptyShadowmap && shadowLightIndex != -1)
            {
                // Indirect-only casters keep the atlas alive through the extension filter,
                // but Unity cannot execute a native shadow list without native casters.
                // The slice callbacks still run with an invalid native renderer-list handle.
                if (!passData.renderingData.cullResults.GetShadowCasterBounds(shadowLightIndex, out _))
                    return;
                var settings = new ShadowDrawingSettings(passData.renderingData.cullResults,
                    shadowLightIndex, BatchCullingProjectionType.Orthographic);
                settings.useRenderingLayerMaskTest = UniversalRenderPipeline.asset.useRenderingLayers;
                for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
                {
                    if ((m_CascadeUpdateMask & (1 << cascadeIndex)) == 0)
                        continue;

                    settings.splitData = m_CascadeSlices[cascadeIndex].splitData;
                    passData.shadowRendererListsHandle[cascadeIndex] = renderGraph.CreateShadowRendererList(ref settings);
                }
            }
        }

        internal TextureHandle Render(RenderGraph graph, ContextContainer frameData)
        {
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();

            // Prepare callbacks run before the outer raster pass is added. This lets an
            // extension append a per-cascade compute pass and register the later raster draw.
            bool hasPrepareSubscribers =
                !m_CreateEmptyShadowmap &&
                ShadowUtils.mainLightShadowSliceRenderGraphPrepareSubscribers != null;
            using var preparation = hasPrepareSubscribers ? GenericPool<PreparedPassData>.Get() : null;
            PassData preparedPassData = preparation;
            if (hasPrepareSubscribers)
            {
                InitPassData(ref preparedPassData, renderingData, cameraData, lightData, shadowData);
                InitRendererLists(ref preparedPassData, graph);
            }

            TextureHandle shadowTexture;
            if (!m_CreateEmptyShadowmap)
            {
                if (m_CachedMode)
                {
                    var importParams = new ImportResourceParams
                    {
                        clearOnFirstUse = m_CachedShadowmapNeedsClear,
                        clearColor = Color.black,
                        discardOnLastUse = false,
                    };
                    shadowTexture = graph.ImportTexture(m_MainLightShadowmapTexture, importParams);
                }
                else
                {
                    shadowTexture = UniversalRenderer.CreateRenderGraphTexture(graph,
                        m_MainLightShadowDescriptor, k_MainLightShadowMapTextureName, true,
                        ShadowUtils.m_ForceShadowPointSampling ? FilterMode.Point : FilterMode.Bilinear);
                }

                for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
                {
                    if ((m_CascadeUpdateMask & (1 << cascadeIndex)) == 0)
                        continue;

                    if (hasPrepareSubscribers)
                    {
                        List<Action<RasterCommandBuffer>> renderGraphFuncs = preparedPassData.shadowSliceRenderFuncs[cascadeIndex] ??= new List<Action<RasterCommandBuffer>>();
                        List<ShadowUtils.RenderGraphBufferUse> bufferUses = preparedPassData.shadowSliceBufferUses[cascadeIndex] ??= new List<ShadowUtils.RenderGraphBufferUse>();
                        List<ShadowUtils.RenderGraphTextureUse> textureUses = preparedPassData.shadowSliceTextureUses[cascadeIndex] ??= new List<ShadowUtils.RenderGraphTextureUse>();
                        RecordMainLightShadowSliceRenderGraph(graph, null, frameData, shadowTexture, preparedPassData, cascadeIndex, renderGraphFuncs, bufferUses, textureUses,
                            ShadowUtils.mainLightShadowSliceRenderGraphPrepareSubscribers);
                    }
                }
            }
            else
            {
                shadowTexture = graph.defaultResources.defaultShadowTexture;
            }

            using (var builder = graph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
            {
                if (preparedPassData != null)
                    CopyPassData(preparedPassData, passData);
                else
                {
                    InitPassData(ref passData, renderingData, cameraData, lightData, shadowData);
                    InitRendererLists(ref passData, graph);
                }

                if (!m_CreateEmptyShadowmap)
                {
                    for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
                    {
                        if ((m_CascadeUpdateMask & (1 << cascadeIndex)) == 0)
                            continue;
                        if (passData.shadowRendererListsHandle[cascadeIndex].IsValid())
                            builder.UseRendererList(passData.shadowRendererListsHandle[cascadeIndex]);
                    }

                    builder.SetRenderAttachmentDepth(shadowTexture,
                        m_CachedMode ? AccessFlags.ReadWrite : AccessFlags.Write);

                    for (int cascadeIndex = 0;
                        cascadeIndex < m_ShadowCasterCascadesCount;
                        ++cascadeIndex)
                    {
                        if ((m_CascadeUpdateMask & (1 << cascadeIndex)) == 0)
                            continue;

                        List<Action<RasterCommandBuffer>> renderGraphFuncs = passData.shadowSliceRenderFuncs[cascadeIndex];
                        List<ShadowUtils.RenderGraphBufferUse> bufferUses = passData.shadowSliceBufferUses[cascadeIndex];
                        List<ShadowUtils.RenderGraphTextureUse> textureUses = passData.shadowSliceTextureUses[cascadeIndex];
                        ShadowUtils.ApplyRenderGraphTextureUses(builder, textureUses);
                        ShadowUtils.ApplyRenderGraphBufferUses(builder, bufferUses);

                        if (ShadowUtils.mainLightShadowSliceRenderGraphSubscribers != null)
                        {
                            if (renderGraphFuncs == null)
                            {
                                renderGraphFuncs = new List<Action<RasterCommandBuffer>>();
                                passData.shadowSliceRenderFuncs[cascadeIndex] = renderGraphFuncs;
                            }

                            RecordMainLightShadowSliceRenderGraph(
                                graph, builder, frameData, shadowTexture, passData,
                                cascadeIndex, renderGraphFuncs, bufferUses, textureUses,
                                ShadowUtils.mainLightShadowSliceRenderGraphSubscribers);
                        }
                    }
                }

                // Need this as shadowmap is only used as Global Texture and not a buffer, so would get culled by RG
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                if (shadowTexture.IsValid())
                    builder.SetGlobalTextureAfterPass(shadowTexture, MainLightShadowConstantBuffer._MainLightShadowmapID);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    if (!data.emptyShadowmap)
                        data.pass.RenderMainLightCascadeShadowmap(context.cmd, ref data);
                    else
                        SetEmptyMainLightCascadeShadowmap(context.cmd, data);
                });
            }

            return shadowTexture;
        }

        private static void CopyPassData(PassData source, PassData destination)
        {
            destination.pass = source.pass;
            destination.emptyShadowmap = source.emptyShadowmap;
            destination.setKeywordForEmptyShadowmap = source.setKeywordForEmptyShadowmap;
            destination.emptyShadowParams = source.emptyShadowParams;
            destination.renderingData = source.renderingData;
            destination.cameraData = source.cameraData;
            destination.lightData = source.lightData;
            destination.shadowData = source.shadowData;
            Array.Copy(source.shadowRendererListsHandle, destination.shadowRendererListsHandle, source.shadowRendererListsHandle.Length);
            // The final graph pass owns its lists until execution completes. Copy contents so
            // the preparation container can return to its pool without sharing mutable state.
            for (int index = 0; index < source.shadowSliceRenderFuncs.Length; ++index)
            {
                CopyRecordingList(source.shadowSliceRenderFuncs[index], ref destination.shadowSliceRenderFuncs[index]);
                CopyRecordingList(source.shadowSliceBufferUses[index], ref destination.shadowSliceBufferUses[index]);
                CopyRecordingList(source.shadowSliceTextureUses[index], ref destination.shadowSliceTextureUses[index]);
            }
        }

        private static void CopyRecordingList<T>(List<T> source, ref List<T> destination)
        {
            destination?.Clear();
            if (source == null || source.Count == 0)
                return;
            destination ??= new List<T>(source.Count);
            destination.AddRange(source);
        }

        private void RecordMainLightShadowSliceRenderGraph(
            RenderGraph graph,
            IRasterRenderGraphBuilder builder,
            ContextContainer frameData,
            TextureHandle shadowTexture,
            PassData passData,
            int cascadeIndex,
            List<Action<RasterCommandBuffer>> renderGraphFuncs,
            List<ShadowUtils.RenderGraphBufferUse> bufferUses,
            List<ShadowUtils.RenderGraphTextureUse> textureUses,
            Delegate[] subscribers)
        {
            if (subscribers == null || passData.lightData == null ||
                !passData.lightData.visibleLights.IsCreated)
                return;

            int shadowLightIndex = passData.lightData.mainLightIndex;
            if (shadowLightIndex < 0 || shadowLightIndex >= passData.lightData.visibleLights.Length)
                return;

            VisibleLight shadowLight = passData.lightData.visibleLights[shadowLightIndex];
            Light light = shadowLight.light;
            if (light == null || cascadeIndex < 0 || cascadeIndex >= m_ShadowCasterCascadesCount)
                return;

            ShadowSliceData slice = m_CascadeSlices[cascadeIndex];
            var settings = new ShadowDrawingSettings(
                passData.renderingData.cullResults,
                shadowLightIndex,
                BatchCullingProjectionType.Orthographic)
            {
                useRenderingLayerMaskTest = UniversalRenderPipeline.asset.useRenderingLayers,
                splitData = slice.splitData
            };
            Vector4 shadowBias = ShadowUtils.GetShadowBias(
                ref shadowLight, shadowLightIndex, passData.shadowData,
                slice.projectionMatrix, slice.resolution);
            var callbackData = new ShadowUtils.MainLightShadowSliceRenderGraphContext(
                graph, builder, frameData, shadowTexture,
                passData.cameraData.camera, light,
                m_MainLightSnapshotValid ? m_MainLightSnapshot : default,
                shadowLightIndex, cascadeIndex, passData.cameraData.maxShadowDistance,
                shadowBias, slice, settings, renderGraphFuncs, bufferUses, textureUses);

            for (int index = 0; index < subscribers.Length; ++index)
            {
                try
                {
                    ((Action<ShadowUtils.MainLightShadowSliceRenderGraphContext>)
                        subscribers[index])(callbackData);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

    };
}
