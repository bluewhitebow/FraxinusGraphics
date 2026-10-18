using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Struct container for shadow slice data.
    /// </summary>
    public struct ShadowSliceData
    {
        /// <summary>
        /// The view matrix.
        /// </summary>
        public Matrix4x4 viewMatrix;

        /// <summary>
        /// The projection matrix.
        /// </summary>
        public Matrix4x4 projectionMatrix;

        /// <summary>
        /// The shadow transform matrix.
        /// </summary>
        public Matrix4x4 shadowTransform;

        /// <summary>
        /// The X offset to the shadow map.
        /// </summary>
        public int offsetX;

        /// <summary>
        /// The Y offset to the shadow map.
        /// </summary>
        public int offsetY;

        /// <summary>
        /// The maximum tile resolution in an Atlas.
        /// </summary>
        public int resolution;

        /// <summary>
        /// The shadow split data containing culling information.
        /// </summary>
        public ShadowSplitData splitData;

        /// <summary>
        /// Clears and resets the data.
        /// </summary>
        public void Clear()
        {
            viewMatrix = Matrix4x4.identity;
            projectionMatrix = Matrix4x4.identity;
            shadowTransform = Matrix4x4.identity;
            offsetX = offsetY = 0;
            resolution = 1024;
        }
    }

    /// <summary>
    /// Various utility functions used for shadows.
    /// </summary>
    public static class ShadowUtils
    {
        /// <summary>
        /// GPU-only renderers can supply caster bounds when Unity cannot extract directional
        /// cascades from its native renderer bounds. URP owns the camera/cascade matrices.
        /// </summary>
        public static event Func<Camera, Light, Bounds?> indirectMainLightShadowBounds
        {
            add
            {
                s_IndirectMainLightShadowBounds += value;
                s_IndirectMainLightShadowBoundsSubscribers = s_IndirectMainLightShadowBounds?.GetInvocationList();
            }
            remove
            {
                s_IndirectMainLightShadowBounds -= value;
                s_IndirectMainLightShadowBoundsSubscribers = s_IndirectMainLightShadowBounds?.GetInvocationList();
            }
        }
        private static Func<Camera, Light, Bounds?> s_IndirectMainLightShadowBounds;
        private static Delegate[] s_IndirectMainLightShadowBoundsSubscribers;

        // No callbacks occur while this scratch is in use. Keep it per thread so cascade
        // extraction reuses its eight corners and six planes without sharing mutable arrays
        // with another thread.
        [ThreadStatic] private static Vector3[] s_IndirectFrustumCorners;
        [ThreadStatic] private static Plane[] s_IndirectFrustumPlanes;

        internal static bool TryGetIndirectMainLightShadowBounds(Camera camera, Light light, out Bounds bounds)
        {
            bounds = default;
            bool found = false;
            var subscribers = s_IndirectMainLightShadowBoundsSubscribers;
            if (subscribers == null) return false;
            foreach (Delegate subscriber in subscribers)
            {
                try
                {
                    Bounds? candidate = ((Func<Camera, Light, Bounds?>)subscriber)(camera, light);
                    if (!candidate.HasValue) continue;
                    if (found) bounds.Encapsulate(candidate.Value);
                    else { bounds = candidate.Value; found = true; }
                }
                catch (Exception exception) { Debug.LogException(exception); }
            }
            return found;
        }

        internal static bool ExtractIndirectDirectionalLightMatrix(UniversalCameraData cameraData,
            UniversalShadowData shadowData, Light light, Bounds casterBounds, int cascade,
            int atlasWidth, int atlasHeight, out ShadowSliceData slice)
        {
            slice = default;
            Camera camera = cameraData.camera;
            if (camera == null || light == null) return false;
            int count = shadowData.mainLightShadowCascadesCount;
            Vector3 splits = shadowData.mainLightShadowCascadesSplit;
            float splitNear = cascade == 0 ? 0 : splits[cascade - 1];
            float splitFar = cascade == count - 1 ? 1 : splits[cascade];
            float far = Mathf.Min(camera.farClipPlane, cameraData.maxShadowDistance);
            float near = camera.nearClipPlane;
            if (far <= near) return false;
            // Use the actual culling projection, including orthographic and asymmetric views.
            Matrix4x4 inverse = camera.cullingMatrix.inverse;
            Matrix4x4 cameraView = cameraData.GetViewMatrix();
            Vector3[] corners = s_IndirectFrustumCorners;
            if (corners == null)
                s_IndirectFrustumCorners = corners = new Vector3[8];
            int corner = 0;
            for (int y = -1; y <= 1; y += 2)
                for (int x = -1; x <= 1; x += 2)
                {
                    Vector3 a = inverse.MultiplyPoint(new Vector3(x, y, -1));
                    Vector3 b = inverse.MultiplyPoint(new Vector3(x, y, 1));
                    float depthA = -cameraView.MultiplyPoint(a).z;
                    float depthB = -cameraView.MultiplyPoint(b).z;
                    float rayDepth = depthB - depthA;
                    if (Mathf.Abs(rayDepth) < 0.0001f) return false;
                    float fractionNear = (Mathf.Lerp(near, far, splitNear) - depthA) / rayDepth;
                    float fractionFar = (Mathf.Lerp(near, far, splitFar) - depthA) / rayDepth;
                    corners[corner++] = Vector3.LerpUnclamped(a, b, fractionNear);
                    corners[corner++] = Vector3.LerpUnclamped(a, b, fractionFar);
                }
            Vector3 center = Vector3.zero;
            foreach (Vector3 point in corners) center += point;
            center /= 8;
            float radius = 0;
            foreach (Vector3 point in corners) radius = Mathf.Max(radius, Vector3.Distance(point, center));
            radius = Mathf.Max(0.1f, radius);
            if (float.IsNaN(radius) || float.IsInfinity(radius)) return false;
            int resolution = Mathf.Max(1, shadowData.mainLightShadowResolution);
            // A stable sphere bounds receivers; terrain bounds extend light-space depth only,
            // preserving off-camera casters without diluting cascade XY resolution.
            Quaternion rotation = light.transform.rotation;
            Matrix4x4 rotationView = Matrix4x4.TRS(Vector3.zero, rotation, Vector3.one).inverse;
            float minDepth = float.PositiveInfinity;
            float maxDepth = float.NegativeInfinity;
            Vector3 minimum = casterBounds.min;
            Vector3 maximum = casterBounds.max;
            for (int i = 0; i < 8; ++i)
            {
                Vector3 point = new Vector3((i & 1) == 0 ? minimum.x : maximum.x,
                    (i & 2) == 0 ? minimum.y : maximum.y, (i & 4) == 0 ? minimum.z : maximum.z);
                float depth = rotationView.MultiplyPoint(point).z;
                minDepth = Mathf.Min(minDepth, depth);
                maxDepth = Mathf.Max(maxDepth, depth);
            }
            Vector3 centerLS = rotationView.MultiplyPoint(center);
            minDepth = Mathf.Min(minDepth, centerLS.z - radius);
            maxDepth = Mathf.Max(maxDepth, centerLS.z + radius);
            float texel = 2 * radius / resolution;
            centerLS.x = Mathf.Floor(centerLS.x / texel) * texel;
            centerLS.y = Mathf.Floor(centerLS.y / texel) * texel;
            float padding = Mathf.Max(1, light.shadowNearPlane);
            centerLS.z = minDepth - padding;
            Vector3 eye = rotation * centerLS;
            slice.viewMatrix = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(eye, rotation, Vector3.one).inverse;
            slice.projectionMatrix = Matrix4x4.Ortho(-radius - texel, radius + texel, -radius - texel, radius + texel,
                0, maxDepth - minDepth + 2 * padding);
            slice.resolution = resolution;
            slice.offsetX = cascade % 2 * resolution;
            slice.offsetY = cascade / 2 * resolution;
            slice.splitData.cullingSphere = new Vector4(center.x, center.y, center.z, radius);
            slice.splitData.shadowCascadeBlendCullingFactor = 1;
            Plane[] planes = s_IndirectFrustumPlanes;
            if (planes == null)
                s_IndirectFrustumPlanes = planes = new Plane[6];
            GeometryUtility.CalculateFrustumPlanes(slice.projectionMatrix * slice.viewMatrix, planes);
            slice.splitData.cullingPlaneCount = planes.Length;
            for (int i = 0; i < planes.Length; ++i) slice.splitData.SetCullingPlane(i, planes[i]);
            slice.shadowTransform = GetShadowTransform(slice.projectionMatrix, slice.viewMatrix);
            if (count > 1) ApplySliceTransform(ref slice, atlasWidth, atlasHeight);
            return true;
        }
        /// <summary>
        /// A buffer access recorded while a RenderGraph shadow slice is in its prepare phase.
        /// Prepare callbacks run before URP creates the owning raster pass (so they can append a
        /// per-slice compute pass); the accesses are replayed on that raster pass once its
        /// builder exists.  This type is internal because it is only the hand-off between the
        /// embedded URP pass and the RenderGraph extension point.
        /// </summary>
        internal readonly struct RenderGraphBufferUse
        {
            internal readonly BufferHandle buffer;
            internal readonly AccessFlags access;

            internal RenderGraphBufferUse(BufferHandle buffer, AccessFlags access)
            {
                this.buffer = buffer;
                this.access = access;
            }
        }

        /// <summary>
        /// A texture access recorded while a RenderGraph shadow slice is in its prepare phase.
        /// Prepare callbacks run before URP creates the owning raster pass, so texture accesses
        /// are replayed on that pass once its builder exists.
        /// </summary>
        internal readonly struct RenderGraphTextureUse
        {
            internal readonly TextureHandle texture;
            internal readonly AccessFlags access;

            internal RenderGraphTextureUse(TextureHandle texture, AccessFlags access)
            {
                this.texture = texture;
                this.access = access;
            }
        }

        /// <summary>
        /// Applies texture accesses collected during a shadow-slice prepare callback to the
        /// raster pass that will execute that slice.
        /// </summary>
        internal static void ApplyRenderGraphTextureUses(
            IRasterRenderGraphBuilder builder,
            List<RenderGraphTextureUse> textureUses)
        {
            if (builder == null || textureUses == null)
                return;

            for (int index = 0; index < textureUses.Count; ++index)
            {
                RenderGraphTextureUse use = textureUses[index];
                builder.UseTexture(use.texture, use.access);
            }
        }

        /// <summary>
        /// Applies buffer accesses collected during a shadow-slice prepare callback to the
        /// raster pass that will execute that slice.
        /// </summary>
        internal static void ApplyRenderGraphBufferUses(
            IRasterRenderGraphBuilder builder,
            List<RenderGraphBufferUse> bufferUses)
        {
            if (builder == null || bufferUses == null)
                return;

            for (int index = 0; index < bufferUses.Count; ++index)
            {
                RenderGraphBufferUse use = bufferUses[index];
                builder.UseBuffer(use.buffer, use.access);
            }
        }

        // Compatibility metadata consumed by the FraxinusBRG package.  URP's current
        // shadow callbacks no longer need to expose this snapshot directly, but the
        // embedded BRG renderer still uses it for camera/light admission decisions.
        public readonly struct ShadowLightSnapshot
        {
            public readonly LightType lightType;
            public readonly int cullingMask;
            public readonly uint renderingLayerMask;
            public readonly Vector3 position;
            public readonly Vector3 direction;
            public readonly float range;
            public readonly bool isValid;

            internal ShadowLightSnapshot(
                LightType lightType,
                int cullingMask,
                uint renderingLayerMask,
                Vector3 position,
                Vector3 direction,
                float range,
                bool isValid)
            {
                this.lightType = lightType;
                this.cullingMask = cullingMask;
                this.renderingLayerMask = renderingLayerMask;
                this.position = position;
                this.direction = direction;
                this.range = range;
                this.isValid = isValid;
            }

            internal static bool TryCapture(in VisibleLight visibleLight,
                out ShadowLightSnapshot snapshot)
            {
                snapshot = default;
                Light light = visibleLight.light;
                if (light == null)
                    return false;
                try
                {
                    Matrix4x4 matrix = visibleLight.localToWorldMatrix;
                    Vector4 position4 = matrix.GetColumn(3);
                    Vector4 direction4 = matrix.GetColumn(2);
                    Vector3 position = new Vector3(position4.x, position4.y, position4.z);
                    Vector3 direction = new Vector3(direction4.x, direction4.y, direction4.z);
                    float directionLength = direction.magnitude;
                    float range = visibleLight.range;
                    if (!IsFinite(position) || !IsFinite(direction) ||
                        !IsFinite(directionLength) || !IsFinite(range))
                        return false;

                    if (directionLength > 1e-6f)
                        direction /= directionLength;
                    else
                        direction = Vector3.zero;

                    snapshot = new ShadowLightSnapshot(
                        visibleLight.lightType,
                        light.cullingMask,
                        unchecked((uint)light.renderingLayerMask),
                        position,
                        direction,
                        range,
                        true);
                    return true;
                }
                catch
                {
                    snapshot = default;
                    return false;
                }
            }

            private static bool IsFinite(Vector3 value) =>
                IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

            private static bool IsFinite(float value) =>
                !float.IsNaN(value) && !float.IsInfinity(value);
        }
        /// <summary>
        /// RenderGraph recording context for an additional main-light shadow slice.
        ///
        /// A recorder is called while the shadow pass is being built, so it can declare
        /// every graph resource it will read or write.  The actual command recording is
        /// deferred until the raster pass executes; this keeps indirect draws on the same
        /// graph-owned command buffer as the built-in renderer list.
        /// </summary>
        public readonly struct MainLightShadowSliceRenderGraphContext
        {
            private readonly List<Action<RasterCommandBuffer>> m_RenderGraphFuncs;
            private readonly List<RenderGraphBufferUse> m_RenderGraphBufferUses;
            private readonly List<RenderGraphTextureUse> m_RenderGraphTextureUses;

            public readonly RenderGraph renderGraph;
            public readonly IRasterRenderGraphBuilder builder;
            public readonly ContextContainer frameData;
            public readonly TextureHandle shadowmapTexture;
            public readonly Camera camera;
            public readonly Light light;
            public readonly ShadowLightSnapshot lightSnapshot;
            public readonly int lightIndex;
            public readonly int cascadeIndex;
            public readonly float maxShadowDistance;
            public readonly Vector4 shadowBias;
            public readonly ShadowSliceData slice;
            public readonly ShadowDrawingSettings drawingSettings;

            internal MainLightShadowSliceRenderGraphContext(
                RenderGraph renderGraph,
                IRasterRenderGraphBuilder builder,
                ContextContainer frameData,
                TextureHandle shadowmapTexture,
                Camera camera,
                Light light,
                ShadowLightSnapshot lightSnapshot,
                int lightIndex,
                int cascadeIndex,
                float maxShadowDistance,
                Vector4 shadowBias,
                ShadowSliceData slice,
                ShadowDrawingSettings drawingSettings,
                List<Action<RasterCommandBuffer>> renderGraphFuncs,
                List<RenderGraphBufferUse> renderGraphBufferUses = null,
                List<RenderGraphTextureUse> renderGraphTextureUses = null)
            {
                this.renderGraph = renderGraph;
                this.builder = builder;
                this.frameData = frameData;
                this.shadowmapTexture = shadowmapTexture;
                this.camera = camera;
                this.light = light;
                this.lightSnapshot = lightSnapshot;
                this.lightIndex = lightIndex;
                this.cascadeIndex = cascadeIndex;
                this.maxShadowDistance = maxShadowDistance;
                this.shadowBias = shadowBias;
                this.slice = slice;
                this.drawingSettings = drawingSettings;
                m_RenderGraphFuncs = renderGraphFuncs;
                m_RenderGraphBufferUses = renderGraphBufferUses;
                m_RenderGraphTextureUses = renderGraphTextureUses;
            }

            /// <summary>Declare a texture dependency for this recorder.</summary>
            public void UseTexture(TextureHandle texture, AccessFlags access = AccessFlags.Read)
            {
                if (builder != null)
                    builder.UseTexture(texture, access);
                else
                    m_RenderGraphTextureUses?.Add(new RenderGraphTextureUse(texture, access));
            }

            /// <summary>Declare a graphics-buffer dependency for this recorder.</summary>
            public void UseBuffer(BufferHandle buffer, AccessFlags access = AccessFlags.Read)
            {
                if (builder != null)
                    builder.UseBuffer(buffer, access);
                else
                    m_RenderGraphBufferUses?.Add(new RenderGraphBufferUse(buffer, access));
            }

            /// <summary>
            /// Register commands to be recorded after the slice viewport/matrices are set and
            /// immediately before URP's renderer list is drawn.
            /// </summary>
            public void RegisterRenderFunc(Action<RasterCommandBuffer> renderFunc)
            {
                if (renderFunc == null)
                    throw new ArgumentNullException(nameof(renderFunc));
                m_RenderGraphFuncs?.Add(renderFunc);
            }
        }

        /// <summary>RenderGraph recording context for a punctual-light shadow slice.</summary>
        public readonly struct AdditionalLightShadowSliceRenderGraphContext
        {
            private readonly List<Action<RasterCommandBuffer>> m_RenderGraphFuncs;
            private readonly List<RenderGraphBufferUse> m_RenderGraphBufferUses;
            private readonly List<RenderGraphTextureUse> m_RenderGraphTextureUses;

            public readonly RenderGraph renderGraph;
            public readonly IRasterRenderGraphBuilder builder;
            public readonly ContextContainer frameData;
            public readonly TextureHandle shadowmapTexture;
            public readonly Camera camera;
            public readonly Light light;
            public readonly ShadowLightSnapshot lightSnapshot;
            public readonly int visibleLightIndex;
            public readonly int additionalLightIndex;
            public readonly int shadowSliceIndex;
            public readonly int perLightShadowSliceIndex;
            public readonly LightType lightType;
            public readonly float maxShadowDistance;
            public readonly Vector4 shadowBias;
            public readonly ShadowSliceData slice;
            public readonly ShadowDrawingSettings drawingSettings;

            internal AdditionalLightShadowSliceRenderGraphContext(
                RenderGraph renderGraph,
                IRasterRenderGraphBuilder builder,
                ContextContainer frameData,
                TextureHandle shadowmapTexture,
                Camera camera,
                Light light,
                ShadowLightSnapshot lightSnapshot,
                int visibleLightIndex,
                int additionalLightIndex,
                int shadowSliceIndex,
                int perLightShadowSliceIndex,
                LightType lightType,
                float maxShadowDistance,
                Vector4 shadowBias,
                ShadowSliceData slice,
                ShadowDrawingSettings drawingSettings,
                List<Action<RasterCommandBuffer>> renderGraphFuncs,
                List<RenderGraphBufferUse> renderGraphBufferUses = null,
                List<RenderGraphTextureUse> renderGraphTextureUses = null)
            {
                this.renderGraph = renderGraph;
                this.builder = builder;
                this.frameData = frameData;
                this.shadowmapTexture = shadowmapTexture;
                this.camera = camera;
                this.light = light;
                this.lightSnapshot = lightSnapshot;
                this.visibleLightIndex = visibleLightIndex;
                this.additionalLightIndex = additionalLightIndex;
                this.shadowSliceIndex = shadowSliceIndex;
                this.perLightShadowSliceIndex = perLightShadowSliceIndex;
                this.lightType = lightType;
                this.maxShadowDistance = maxShadowDistance;
                this.shadowBias = shadowBias;
                this.slice = slice;
                this.drawingSettings = drawingSettings;
                m_RenderGraphFuncs = renderGraphFuncs;
                m_RenderGraphBufferUses = renderGraphBufferUses;
                m_RenderGraphTextureUses = renderGraphTextureUses;
            }

            /// <summary>Declare a texture dependency for this recorder.</summary>
            public void UseTexture(TextureHandle texture, AccessFlags access = AccessFlags.Read)
            {
                if (builder != null)
                    builder.UseTexture(texture, access);
                else
                    m_RenderGraphTextureUses?.Add(new RenderGraphTextureUse(texture, access));
            }

            /// <summary>Declare a graphics-buffer dependency for this recorder.</summary>
            public void UseBuffer(BufferHandle buffer, AccessFlags access = AccessFlags.Read)
            {
                if (builder != null)
                    builder.UseBuffer(buffer, access);
                else
                    m_RenderGraphBufferUses?.Add(new RenderGraphBufferUse(buffer, access));
            }

            /// <summary>
            /// Register commands to be recorded after the slice viewport/matrices are set and
            /// immediately before URP's renderer list is drawn.
            /// </summary>
            public void RegisterRenderFunc(Action<RasterCommandBuffer> renderFunc)
            {
                if (renderFunc == null)
                    throw new ArgumentNullException(nameof(renderFunc));
                m_RenderGraphFuncs?.Add(renderFunc);
            }
        }

        /// <summary>RenderGraph recording context for the depth-only prepass.</summary>
        public readonly struct DepthOnlyRenderGraphContext
        {
            private readonly List<Action<RasterCommandBuffer>> m_RenderGraphFuncs;
            private readonly List<RenderGraphBufferUse> m_RenderGraphBufferUses;
            private readonly List<RenderGraphTextureUse> m_RenderGraphTextureUses;

            public readonly RenderGraph renderGraph;
            public readonly IRasterRenderGraphBuilder builder;
            public readonly ContextContainer frameData;
            public readonly TextureHandle cameraDepthTexture;
            public readonly uint batchLayerMask;

            internal DepthOnlyRenderGraphContext(
                RenderGraph renderGraph,
                IRasterRenderGraphBuilder builder,
                ContextContainer frameData,
                TextureHandle cameraDepthTexture,
                uint batchLayerMask,
                List<Action<RasterCommandBuffer>> renderGraphFuncs,
                List<RenderGraphBufferUse> bufferUses = null,
                List<RenderGraphTextureUse> textureUses = null)
            {
                this.renderGraph = renderGraph;
                this.builder = builder;
                this.frameData = frameData;
                this.cameraDepthTexture = cameraDepthTexture;
                this.batchLayerMask = batchLayerMask;
                m_RenderGraphFuncs = renderGraphFuncs;
                m_RenderGraphBufferUses = bufferUses;
                m_RenderGraphTextureUses = textureUses;
            }

            public void UseTexture(TextureHandle texture, AccessFlags access = AccessFlags.Read)
            {
                if (builder != null) builder.UseTexture(texture, access);
                else m_RenderGraphTextureUses?.Add(new RenderGraphTextureUse(texture, access));
            }

            public void UseBuffer(BufferHandle buffer, AccessFlags access = AccessFlags.Read)
            {
                if (builder != null) builder.UseBuffer(buffer, access);
                else m_RenderGraphBufferUses?.Add(new RenderGraphBufferUse(buffer, access));
            }

            public void RegisterRenderFunc(Action<RasterCommandBuffer> renderFunc)
            {
                if (renderFunc == null)
                    throw new ArgumentNullException(nameof(renderFunc));
                m_RenderGraphFuncs?.Add(renderFunc);
            }
        }

        /// <summary>RenderGraph recording context for the depth-normal prepass.</summary>
        public readonly struct DepthNormalOnlyRenderGraphContext
        {
            private readonly List<Action<RasterCommandBuffer>> m_RenderGraphFuncs;
            private readonly List<RenderGraphBufferUse> m_RenderGraphBufferUses;
            private readonly List<RenderGraphTextureUse> m_RenderGraphTextureUses;

            public readonly RenderGraph renderGraph;
            public readonly IRasterRenderGraphBuilder builder;
            public readonly ContextContainer frameData;
            public readonly TextureHandle cameraDepthTexture;
            public readonly TextureHandle cameraNormalsTexture;
            public readonly TextureHandle renderingLayersTexture;
            public readonly bool writeRenderingLayers;
            public readonly uint batchLayerMask;

            internal DepthNormalOnlyRenderGraphContext(
                RenderGraph renderGraph,
                IRasterRenderGraphBuilder builder,
                ContextContainer frameData,
                TextureHandle cameraDepthTexture,
                TextureHandle cameraNormalsTexture,
                TextureHandle renderingLayersTexture,
                bool writeRenderingLayers,
                uint batchLayerMask,
                List<Action<RasterCommandBuffer>> renderGraphFuncs,
                List<RenderGraphBufferUse> bufferUses = null,
                List<RenderGraphTextureUse> textureUses = null)
            {
                this.renderGraph = renderGraph;
                this.builder = builder;
                this.frameData = frameData;
                this.cameraDepthTexture = cameraDepthTexture;
                this.cameraNormalsTexture = cameraNormalsTexture;
                this.renderingLayersTexture = renderingLayersTexture;
                this.writeRenderingLayers = writeRenderingLayers;
                this.batchLayerMask = batchLayerMask;
                m_RenderGraphFuncs = renderGraphFuncs;
                m_RenderGraphBufferUses = bufferUses;
                m_RenderGraphTextureUses = textureUses;
            }

            public void UseTexture(TextureHandle texture, AccessFlags access = AccessFlags.Read)
            {
                if (builder != null) builder.UseTexture(texture, access);
                else m_RenderGraphTextureUses?.Add(new RenderGraphTextureUse(texture, access));
            }

            public void UseBuffer(BufferHandle buffer, AccessFlags access = AccessFlags.Read)
            {
                if (builder != null) builder.UseBuffer(buffer, access);
                else m_RenderGraphBufferUses?.Add(new RenderGraphBufferUse(buffer, access));
            }

            public void RegisterRenderFunc(Action<RasterCommandBuffer> renderFunc)
            {
                if (renderFunc == null)
                    throw new ArgumentNullException(nameof(renderFunc));
                m_RenderGraphFuncs?.Add(renderFunc);
            }
        }

        /// <summary>
        /// Describes an additional light while URP decides whether its shadow atlas slices need
        /// to be kept.  The predicate is evaluated once per valid punctual light, before any
        /// spot matrix or point-light face is extracted.  A subscriber should return <c>true</c>
        /// only when it may contribute a caster that is not visible to URP's renderer-list
        /// bounds query.
        /// </summary>
        public readonly struct AdditionalLightShadowSliceFilterContext
        {
            /// <summary>
            /// The native render context for the camera whose shadow atlas is being prepared.
            /// A renderer that owns context-scoped state can use this identity to reject a
            /// recursively entered, different context before it admits any atlas slices.
            /// </summary>
            public readonly ScriptableRenderContext renderContext;
            public readonly Camera camera;
            public readonly Light light;
            public readonly ShadowLightSnapshot lightSnapshot;
            public readonly int visibleLightIndex;
            public readonly int additionalLightIndex;
            public readonly LightType lightType;

            internal AdditionalLightShadowSliceFilterContext(
                ScriptableRenderContext renderContext,
                Camera camera,
                Light light,
                ShadowLightSnapshot lightSnapshot,
                int visibleLightIndex,
                int additionalLightIndex,
                LightType lightType)
            {
                this.renderContext = renderContext;
                this.camera = camera;
                this.light = light;
                this.lightSnapshot = lightSnapshot;
                this.visibleLightIndex = visibleLightIndex;
                this.additionalLightIndex = additionalLightIndex;
                this.lightType = lightType;
            }
        }

        /// <summary>
        /// Describes the main light while URP decides whether its cascades need to be kept.
        /// The query runs once per camera/light setup, before cascade matrices are extracted.
        /// </summary>
        public readonly struct MainLightShadowSliceFilterContext
        {
            /// <summary>
            /// The native render context for the camera whose shadow atlas is being prepared.
            /// </summary>
            public readonly ScriptableRenderContext renderContext;
            public readonly Camera camera;
            public readonly Light light;
            public readonly ShadowLightSnapshot lightSnapshot;
            public readonly int lightIndex;

            internal MainLightShadowSliceFilterContext(
                ScriptableRenderContext renderContext,
                Camera camera,
                Light light,
                ShadowLightSnapshot lightSnapshot,
                int lightIndex)
            {
                this.renderContext = renderContext;
                this.camera = camera;
                this.light = light;
                this.lightSnapshot = lightSnapshot;
                this.lightIndex = lightIndex;
            }
        }

        /// <summary>
        /// Main-light shadow recorder. Subscribers
        /// are invoked while the pass is recorded and must use
        /// <see cref="MainLightShadowSliceRenderGraphContext.RegisterRenderFunc"/> for GPU
        /// commands.
        /// </summary>
        public static event Action<MainLightShadowSliceRenderGraphContext>
            mainLightShadowSliceRenderGraph
        {
            add
            {
                s_MainLightShadowSliceRenderGraph += value;
                s_MainLightShadowSliceRenderGraphSubscribers =
                    s_MainLightShadowSliceRenderGraph?.GetInvocationList();
            }
            remove
            {
                s_MainLightShadowSliceRenderGraph -= value;
                s_MainLightShadowSliceRenderGraphSubscribers =
                    s_MainLightShadowSliceRenderGraph?.GetInvocationList();
            }
        }
        static Action<MainLightShadowSliceRenderGraphContext>
            s_MainLightShadowSliceRenderGraph;
        static Delegate[] s_MainLightShadowSliceRenderGraphSubscribers;

        /// <summary>
        /// RenderGraph prepare hook for a main-light shadow slice.  This is invoked before URP
        /// creates its outer raster pass, allowing a subscriber to add a per-slice compute/unsafe
        /// pass (for example GPU culling) and register the eventual raster draw callback.  The
        /// context's <see cref="MainLightShadowSliceRenderGraphContext.builder"/> is null during
        /// this callback; buffer uses are deferred and attached to the raster pass afterwards.
        /// </summary>
        public static event Action<MainLightShadowSliceRenderGraphContext>
            mainLightShadowSliceRenderGraphPrepare
        {
            add
            {
                s_MainLightShadowSliceRenderGraphPrepare += value;
                s_MainLightShadowSliceRenderGraphPrepareSubscribers =
                    s_MainLightShadowSliceRenderGraphPrepare?.GetInvocationList();
            }
            remove
            {
                s_MainLightShadowSliceRenderGraphPrepare -= value;
                s_MainLightShadowSliceRenderGraphPrepareSubscribers =
                    s_MainLightShadowSliceRenderGraphPrepare?.GetInvocationList();
            }
        }
        static Action<MainLightShadowSliceRenderGraphContext>
            s_MainLightShadowSliceRenderGraphPrepare;
        static Delegate[] s_MainLightShadowSliceRenderGraphPrepareSubscribers;

        /// <summary>Additional-light shadow recorder.</summary>
        public static event Action<AdditionalLightShadowSliceRenderGraphContext>
            additionalLightShadowSliceRenderGraph
        {
            add
            {
                s_AdditionalLightShadowSliceRenderGraph += value;
                s_AdditionalLightShadowSliceRenderGraphSubscribers =
                    s_AdditionalLightShadowSliceRenderGraph?.GetInvocationList();
            }
            remove
            {
                s_AdditionalLightShadowSliceRenderGraph -= value;
                s_AdditionalLightShadowSliceRenderGraphSubscribers =
                    s_AdditionalLightShadowSliceRenderGraph?.GetInvocationList();
            }
        }
        static Action<AdditionalLightShadowSliceRenderGraphContext>
            s_AdditionalLightShadowSliceRenderGraph;
        static Delegate[] s_AdditionalLightShadowSliceRenderGraphSubscribers;

        /// <summary>
        /// RenderGraph prepare hook for an additional-light shadow slice.  The callback runs
        /// before URP's outer raster pass is created; see
        /// <see cref="mainLightShadowSliceRenderGraphPrepare"/> for the two-stage contract.
        /// </summary>
        public static event Action<AdditionalLightShadowSliceRenderGraphContext>
            additionalLightShadowSliceRenderGraphPrepare
        {
            add
            {
                s_AdditionalLightShadowSliceRenderGraphPrepare += value;
                s_AdditionalLightShadowSliceRenderGraphPrepareSubscribers =
                    s_AdditionalLightShadowSliceRenderGraphPrepare?.GetInvocationList();
            }
            remove
            {
                s_AdditionalLightShadowSliceRenderGraphPrepare -= value;
                s_AdditionalLightShadowSliceRenderGraphPrepareSubscribers =
                    s_AdditionalLightShadowSliceRenderGraphPrepare?.GetInvocationList();
            }
        }
        static Action<AdditionalLightShadowSliceRenderGraphContext>
            s_AdditionalLightShadowSliceRenderGraphPrepare;
        static Delegate[] s_AdditionalLightShadowSliceRenderGraphPrepareSubscribers;

        /// <summary>
        /// Lets a GPU-driven renderer admit an additional-light shadow atlas slice when its
        /// casters are not represented by URP's CPU renderer-list bounds.  The event is queried
        /// per light rather than globally, so an indirect renderer does not force every valid
        /// spot/point light (and every point-light face) through shadow setup.
        /// </summary>
        public static event Func<AdditionalLightShadowSliceFilterContext, bool>
            additionalLightShadowSliceFilter
        {
            add
            {
                s_AdditionalLightShadowSliceFilter += value;
                s_AdditionalLightShadowSliceFilterSubscribers =
                    s_AdditionalLightShadowSliceFilter?.GetInvocationList();
            }
            remove
            {
                s_AdditionalLightShadowSliceFilter -= value;
                s_AdditionalLightShadowSliceFilterSubscribers =
                    s_AdditionalLightShadowSliceFilter?.GetInvocationList();
            }
        }
        static Func<AdditionalLightShadowSliceFilterContext, bool>
            s_AdditionalLightShadowSliceFilter;
        static Delegate[] s_AdditionalLightShadowSliceFilterSubscribers;

        /// <summary>
        /// Lets a GPU-driven renderer admit the main-light cascades when its casters are not
        /// represented by URP's CPU renderer-list bounds. The predicate is evaluated once per
        /// camera/light setup; cascade-specific visibility is still evaluated by the slice
        /// callback using URP's split data.
        /// </summary>
        public static event Func<MainLightShadowSliceFilterContext, bool>
            mainLightShadowSliceFilter
        {
            add
            {
                s_MainLightShadowSliceFilter += value;
                s_MainLightShadowSliceFilterSubscribers =
                    s_MainLightShadowSliceFilter?.GetInvocationList();
            }
            remove
            {
                s_MainLightShadowSliceFilter -= value;
                s_MainLightShadowSliceFilterSubscribers =
                    s_MainLightShadowSliceFilter?.GetInvocationList();
            }
        }
        static Func<MainLightShadowSliceFilterContext, bool>
            s_MainLightShadowSliceFilter;
        static Delegate[] s_MainLightShadowSliceFilterSubscribers;

        // Events cannot be read outside the declaring type in C#, even by another type in the
        // same assembly.  Expose read-only internal accessors for the pass-side RG recorder
        // setup without allowing external code to replace the event delegate.
        internal static Delegate[] mainLightShadowSliceRenderGraphSubscribers =>
            s_MainLightShadowSliceRenderGraphSubscribers;
        internal static Delegate[] additionalLightShadowSliceRenderGraphSubscribers =>
            s_AdditionalLightShadowSliceRenderGraphSubscribers;
        internal static Delegate[] mainLightShadowSliceRenderGraphPrepareSubscribers =>
            s_MainLightShadowSliceRenderGraphPrepareSubscribers;
        internal static Delegate[] additionalLightShadowSliceRenderGraphPrepareSubscribers =>
            s_AdditionalLightShadowSliceRenderGraphPrepareSubscribers;

        /// <summary>
        /// Returns whether any main-light filter subscriber needs the cascades for this camera.
        /// Subscribers are isolated so a faulty extension cannot suppress another extension's
        /// admission decision. Exceptions are treated as <c>false</c>.
        /// </summary>
        internal static bool ShouldKeepMainLightShadowSlices(
            ScriptableRenderContext renderContext,
            Camera camera,
            Light light,
            int lightIndex)
        {
            return ShouldKeepMainLightShadowSlices(renderContext, camera, light, default, lightIndex);
        }

        internal static bool ShouldKeepMainLightShadowSlices(
            ScriptableRenderContext renderContext,
            Camera camera,
            Light light,
            ShadowLightSnapshot lightSnapshot,
            int lightIndex)
        {
            Delegate[] subscribers = s_MainLightShadowSliceFilterSubscribers;
            if (subscribers == null)
                return false;

            MainLightShadowSliceFilterContext filterContext =
                new MainLightShadowSliceFilterContext(
                    renderContext, camera, light, lightSnapshot, lightIndex);
            for (int index = 0; index < subscribers.Length; ++index)
            {
                try
                {
                    if (((Func<MainLightShadowSliceFilterContext, bool>)subscribers[index])(
                            filterContext))
                        return true;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
            return false;
        }

        /// <summary>
        /// Returns whether any filter subscriber needs shadow slices for this punctual light.
        /// Subscribers are isolated: one faulty extension cannot suppress another extension's
        /// admission decision.  Exceptions are treated as <c>false</c> for that subscriber,
        /// preserving URP's normal bounds-based path and avoiding an accidental atlas explosion.
        /// </summary>
        internal static bool ShouldKeepAdditionalLightShadowSlices(
            ScriptableRenderContext renderContext,
            Camera camera,
            Light light,
            int visibleLightIndex,
            int additionalLightIndex,
            LightType lightType)
        {
            return ShouldKeepAdditionalLightShadowSlices(renderContext, camera, light, default,
                visibleLightIndex, additionalLightIndex, lightType);
        }

        internal static bool ShouldKeepAdditionalLightShadowSlices(
            ScriptableRenderContext renderContext,
            Camera camera,
            Light light,
            ShadowLightSnapshot lightSnapshot,
            int visibleLightIndex,
            int additionalLightIndex,
            LightType lightType)
        {
            Delegate[] subscribers = s_AdditionalLightShadowSliceFilterSubscribers;
            if (subscribers == null)
                return false;

            AdditionalLightShadowSliceFilterContext filterContext =
                new AdditionalLightShadowSliceFilterContext(
                    renderContext, camera, light, lightSnapshot, visibleLightIndex, additionalLightIndex, lightType);
            for (int index = 0; index < subscribers.Length; ++index)
            {
                try
                {
                    if (((Func<AdditionalLightShadowSliceFilterContext, bool>)subscribers[index])(
                            filterContext))
                        return true;
                }
                catch (Exception exception)
                {
                    // A filter is an admission hint, not a reason to break URP's shadow pass.
                    // Log and continue so another renderer can still request its own slices.
                    Debug.LogException(exception);
                }
            }
            return false;
        }

        internal static readonly bool m_ForceShadowPointSampling;

        static ShadowUtils()
        {
            m_ForceShadowPointSampling = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal &&
                GraphicsSettings.HasShaderDefine(Graphics.activeTier, BuiltinShaderDefine.UNITY_METAL_SHADOWS_USE_POINT_FILTERING);
        }

        /// <summary>
        /// Extracts the directional light matrix.
        /// </summary>
        /// <param name="cullResults">The results of a culling operation.</param>
        /// <param name="shadowData">Data containing shadow settings.</param>
        /// <param name="shadowLightIndex">The visible light index.</param>
        /// <param name="cascadeIndex">The cascade index.</param>
        /// <param name="shadowmapWidth">The shadow map width.</param>
        /// <param name="shadowmapHeight">The shadow map height.</param>
        /// <param name="shadowResolution">The shadow map resolution.</param>
        /// <param name="shadowNearPlane">Near plane value to use for shadow frustums.</param>
        /// <param name="cascadeSplitDistance">The culling sphere for the cascade.</param>
        /// <param name="shadowSliceData">The struct container for shadow slice data.</param>
        /// <param name="viewMatrix">The view matrix to be set.</param>
        /// <param name="projMatrix">The projection matrix to be set.</param>
        /// <returns>True if the matrix was successfully extracted.</returns>
        public static bool ExtractDirectionalLightMatrix(ref CullingResults cullResults, ref ShadowData shadowData, int shadowLightIndex, int cascadeIndex, int shadowmapWidth, int shadowmapHeight, int shadowResolution, float shadowNearPlane, out Vector4 cascadeSplitDistance, out ShadowSliceData shadowSliceData, out Matrix4x4 viewMatrix, out Matrix4x4 projMatrix)
        {
            bool result = ExtractDirectionalLightMatrix(ref cullResults, ref shadowData, shadowLightIndex, cascadeIndex, shadowmapWidth, shadowmapHeight, shadowResolution, shadowNearPlane, out cascadeSplitDistance, out shadowSliceData);
            viewMatrix = shadowSliceData.viewMatrix;
            projMatrix = shadowSliceData.projectionMatrix;
            return result;
        }

        /// <summary>
        /// Extracts the directional light matrix.
        /// </summary>
        /// <param name="cullResults">The results of a culling operation.</param>
        /// <param name="shadowData">Data containing shadow settings.</param>
        /// <param name="shadowLightIndex">The visible light index.</param>
        /// <param name="cascadeIndex">The cascade index.</param>
        /// <param name="shadowmapWidth">The shadow map width.</param>
        /// <param name="shadowmapHeight">The shadow map height.</param>
        /// <param name="shadowResolution">The shadow map resolution.</param>
        /// <param name="shadowNearPlane">Near plane value to use for shadow frustums.</param>
        /// <param name="cascadeSplitDistance">The culling sphere for the cascade.</param>
        /// <param name="shadowSliceData">The struct container for shadow slice data.</param>
        /// <returns>True if the matrix was successfully extracted.</returns>
        public static bool ExtractDirectionalLightMatrix(ref CullingResults cullResults, ref ShadowData shadowData, int shadowLightIndex, int cascadeIndex, int shadowmapWidth, int shadowmapHeight, int shadowResolution, float shadowNearPlane, out Vector4 cascadeSplitDistance, out ShadowSliceData shadowSliceData)
        {
            return ExtractDirectionalLightMatrix(ref cullResults, shadowData.universalShadowData,
                shadowLightIndex, cascadeIndex, shadowmapWidth, shadowmapHeight, shadowResolution,
                shadowNearPlane, out cascadeSplitDistance, out shadowSliceData);
        }

        /// <summary>
        /// Extracts the directional light matrix.
        /// </summary>
        /// <param name="cullResults">The results of a culling operation.</param>
        /// <param name="shadowData">Data containing shadow settings.</param>
        /// <param name="shadowLightIndex">The visible light index.</param>
        /// <param name="cascadeIndex">The cascade index.</param>
        /// <param name="shadowmapWidth">The shadow map width.</param>
        /// <param name="shadowmapHeight">The shadow map height.</param>
        /// <param name="shadowResolution">The shadow map resolution.</param>
        /// <param name="shadowNearPlane">Near plane value to use for shadow frustums.</param>
        /// <param name="cascadeSplitDistance">The culling sphere for the cascade.</param>
        /// <param name="shadowSliceData">The struct container for shadow slice data.</param>
        /// <returns>True if the matrix was successfully extracted.</returns>
        public static bool ExtractDirectionalLightMatrix(ref CullingResults cullResults, UniversalShadowData shadowData, int shadowLightIndex, int cascadeIndex, int shadowmapWidth, int shadowmapHeight, int shadowResolution, float shadowNearPlane, out Vector4 cascadeSplitDistance, out ShadowSliceData shadowSliceData)
        {
            try
            {
                bool success = cullResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(
                    shadowLightIndex,
                    cascadeIndex,
                    shadowData.mainLightShadowCascadesCount,
                    shadowData.mainLightShadowCascadesSplit,
                    shadowResolution,
                    shadowNearPlane,
                    out shadowSliceData.viewMatrix,
                    out shadowSliceData.projectionMatrix,
                    out shadowSliceData.splitData);
                if (!success)
                {
                    cascadeSplitDistance = Vector4.zero;
                    shadowSliceData = default;
                    return false;
                }

                cascadeSplitDistance = shadowSliceData.splitData.cullingSphere;
                shadowSliceData.offsetX = (cascadeIndex % 2) * shadowResolution;
                shadowSliceData.offsetY = (cascadeIndex / 2) * shadowResolution;
                shadowSliceData.resolution = shadowResolution;
                shadowSliceData.shadowTransform = GetShadowTransform(
                    shadowSliceData.projectionMatrix, shadowSliceData.viewMatrix);

                // It is the culling sphere radius multiplier for shadow cascade blending. If
                // this is less than 1.0, it will begin to cull casters across cascades.
                shadowSliceData.splitData.shadowCascadeBlendCullingFactor = 1.0f;

                // If cascades are baked into the atlas, bake the cascade transform into each
                // matrix to save shader ALU and loads.
                if (shadowData.mainLightShadowCascadesCount > 1)
                    ApplySliceTransform(ref shadowSliceData, shadowmapWidth, shadowmapHeight);

                return true;
            }
            catch (Exception exception)
            {
                // Extraction is a per-camera best-effort operation. Preserve diagnostics while
                // allowing the pass to use its existing empty-shadow fallback.
                Debug.LogException(exception);
                cascadeSplitDistance = Vector4.zero;
                shadowSliceData = default;
                return false;
            }
        }

        /// <summary>
        /// Extracts the spot light matrix.
        /// </summary>
        /// <param name="cullResults">The results of a culling operation.</param>
        /// <param name="shadowData">Data containing shadow settings.</param>
        /// <param name="shadowLightIndex">The visible light index.</param>
        /// <param name="shadowMatrix">The shadow matrix to be set.</param>
        /// <param name="viewMatrix">The view matrix to be set.</param>
        /// <param name="projMatrix">The projection matrix to be set.</param>
        /// <param name="splitData">The shadow split data containing culling information.</param>
        /// <returns>True if the matrix was successfully extracted.</returns>
        public static bool ExtractSpotLightMatrix(ref CullingResults cullResults, ref ShadowData shadowData, int shadowLightIndex, out Matrix4x4 shadowMatrix, out Matrix4x4 viewMatrix, out Matrix4x4 projMatrix, out ShadowSplitData splitData)
        {
            return ExtractSpotLightMatrix(ref cullResults, shadowData.universalShadowData, shadowLightIndex, out shadowMatrix, out viewMatrix, out projMatrix, out splitData);
        }

        /// <summary>
        /// Extracts the spot light matrix.
        /// </summary>
        /// <param name="cullResults">The results of a culling operation.</param>
        /// <param name="shadowData">Data containing shadow settings.</param>
        /// <param name="shadowLightIndex">The visible light index.</param>
        /// <param name="shadowMatrix">The shadow matrix to be set.</param>
        /// <param name="viewMatrix">The view matrix to be set.</param>
        /// <param name="projMatrix">The projection matrix to be set.</param>
        /// <param name="splitData">The shadow split data containing culling information.</param>
        /// <returns>True if the matrix was successfully extracted.</returns>
        public static bool ExtractSpotLightMatrix(ref CullingResults cullResults, UniversalShadowData shadowData, int shadowLightIndex, out Matrix4x4 shadowMatrix, out Matrix4x4 viewMatrix, out Matrix4x4 projMatrix, out ShadowSplitData splitData)
        {
            bool success = cullResults.ComputeSpotShadowMatricesAndCullingPrimitives(shadowLightIndex, out viewMatrix, out projMatrix, out splitData); // returns false if input parameters are incorrect (rare)
            shadowMatrix = GetShadowTransform(projMatrix, viewMatrix);
            return success;
        }

        /// <summary>
        /// Extracts the point light matrix.
        /// </summary>
        /// <param name="cullResults">The results of a culling operation.</param>
        /// <param name="shadowData">Data containing shadow settings.</param>
        /// <param name="shadowLightIndex">The visible light index.</param>
        /// <param name="cubemapFace">The face of the cubemap to use.</param>
        /// <param name="fovBias">The guard angle that must be added to a point light shadow face frustum angle in order to avoid shadows missing at the boundaries between cube faces.</param>
        /// <param name="shadowMatrix">The shadow matrix to be set.</param>
        /// <param name="viewMatrix">The view matrix to be set.</param>
        /// <param name="projMatrix">The projection matrix to be set.</param>
        /// <param name="splitData">The shadow split data containing culling information.</param>
        /// <returns>True if the matrix was successfully extracted.</returns>
        public static bool ExtractPointLightMatrix(ref CullingResults cullResults, ref ShadowData shadowData, int shadowLightIndex, CubemapFace cubemapFace, float fovBias, out Matrix4x4 shadowMatrix, out Matrix4x4 viewMatrix, out Matrix4x4 projMatrix, out ShadowSplitData splitData)
        {
            return ExtractPointLightMatrix(ref cullResults, shadowData.universalShadowData, shadowLightIndex, cubemapFace, fovBias, out shadowMatrix, out viewMatrix, out projMatrix, out splitData);
        }

        /// <summary>
        /// Extracts the point light matrix.
        /// </summary>
        /// <param name="cullResults">The results of a culling operation.</param>
        /// <param name="shadowData">Data containing shadow settings.</param>
        /// <param name="shadowLightIndex">The visible light index.</param>
        /// <param name="cubemapFace">The face of the cubemap to use.</param>
        /// <param name="fovBias">The guard angle that must be added to a point light shadow face frustum angle in order to avoid shadows missing at the boundaries between cube faces.</param>
        /// <param name="shadowMatrix">The shadow matrix to be set.</param>
        /// <param name="viewMatrix">The view matrix to be set.</param>
        /// <param name="projMatrix">The projection matrix to be set.</param>
        /// <param name="splitData">The shadow split data containing culling information.</param>
        /// <returns>True if the matrix was successfully extracted.</returns>
        public static bool ExtractPointLightMatrix(ref CullingResults cullResults, UniversalShadowData shadowData, int shadowLightIndex, CubemapFace cubemapFace, float fovBias, out Matrix4x4 shadowMatrix, out Matrix4x4 viewMatrix, out Matrix4x4 projMatrix, out ShadowSplitData splitData)
        {
            // In native API CullingResults.ComputeSpotShadowMatricesAndCullingPrimitives there is code that inverts the 3rd component of shadow-casting spot light's "world-to-local" matrix (it was so since its original addition to the code base):
            // https://github.cds.internal.unity3d.com/unity/unity/commit/34813e063526c4be0ef0448dfaae3a911dd8be58#diff-cf0b417fc6bd8ee2356770797e628cd4R331
            // (the same transformation has also always been used in the Built-In Render Pipeline)
            //
            // However native API CullingResults.ComputePointShadowMatricesAndCullingPrimitives does not contain this transformation.
            // As a result, the view matrices returned for a point light shadow face, and for a spot light with same direction as that face, have opposite 3rd component.
            //
            // This causes normalBias to be incorrectly applied to shadow caster vertices during the point light shadow pass.
            // To counter this effect, we invert the point light shadow view matrix component here:
            try
            {
                bool success = cullResults.ComputePointShadowMatricesAndCullingPrimitives(
                    shadowLightIndex, cubemapFace, fovBias,
                    out viewMatrix, out projMatrix, out splitData);
                // Returns false if input parameters are incorrect (rare). Keep the whole
                // cubemap transaction fail-closed; callers can discard all faces.
                if (!success)
                {
                    shadowMatrix = default;
                    viewMatrix = default;
                    projMatrix = default;
                    splitData = default;
                    return false;
                }

                // The native spot path inverts the third component of the light-space basis,
                // while the point path does not. Mirror URP's long-standing correction so
                // normal bias is applied consistently for point-light faces.
                viewMatrix.m10 = -viewMatrix.m10;
                viewMatrix.m11 = -viewMatrix.m11;
                viewMatrix.m12 = -viewMatrix.m12;
                viewMatrix.m13 = -viewMatrix.m13;

                shadowMatrix = GetShadowTransform(projMatrix, viewMatrix);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                shadowMatrix = default;
                viewMatrix = default;
                projMatrix = default;
                splitData = default;
                return false;
            }
        }

        /// <summary>
        /// Renders shadows to a shadow slice.
        /// </summary>
        /// <param name="cmd"></param>
        /// <param name="context"></param>
        /// <param name="shadowSliceData"></param>
        /// <param name="settings"></param>
        /// <param name="proj"></param>
        /// <param name="view"></param>
        public static void RenderShadowSlice(CommandBuffer cmd,
            ref ScriptableRenderContext context,
            ref ShadowSliceData shadowSliceData,
            ref ShadowDrawingSettings settings,
            Matrix4x4 proj, Matrix4x4 view)
        {
            cmd.SetGlobalDepthBias(1.0f, 2.5f);
            cmd.SetViewport(new Rect(shadowSliceData.offsetX, shadowSliceData.offsetY,
                shadowSliceData.resolution, shadowSliceData.resolution));
            cmd.SetViewProjectionMatrices(view, proj);
            var shadowRendererList = context.CreateShadowRendererList(ref settings);
            cmd.DrawRendererList(shadowRendererList);
            cmd.DisableScissorRect();
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            cmd.SetGlobalDepthBias(0.0f, 0.0f);
        }

        internal static void RenderShadowSlice(RasterCommandBuffer cmd,
            ref ShadowSliceData shadowSliceData, ref RendererList shadowRendererList,
            Matrix4x4 proj, Matrix4x4 view)
        {
            RenderShadowSlice(cmd, ref shadowSliceData, ref shadowRendererList,
                proj, view, null);
        }

        /// <summary>
        /// Records a shadow slice and gives RG-only extension recorders a chance to issue
        /// additional draws after the slice viewport/matrices are installed.
        /// </summary>
        internal static void RenderShadowSlice(RasterCommandBuffer cmd,
            ref ShadowSliceData shadowSliceData, ref RendererList shadowRendererList,
            Matrix4x4 proj, Matrix4x4 view,
            List<Action<RasterCommandBuffer>> renderGraphFuncs)
        {
            cmd.SetGlobalDepthBias(1.0f, 2.5f); // these values match HDRP defaults (see https://github.com/Unity-Technologies/Graphics/blob/9544b8ed2f98c62803d285096c91b44e9d8cbc47/com.unity.render-pipelines.high-definition/Runtime/Lighting/Shadow/HDShadowAtlas.cs#L197 )

            cmd.SetViewport(new Rect(shadowSliceData.offsetX, shadowSliceData.offsetY, shadowSliceData.resolution, shadowSliceData.resolution));
            cmd.SetViewProjectionMatrices(view, proj);

            if (renderGraphFuncs != null)
            {
                for (int index = 0; index < renderGraphFuncs.Count; ++index)
                {
                    try
                    {
                        renderGraphFuncs[index]?.Invoke(cmd);
                    }
                    catch (Exception exception)
                    {
                        // An optional indirect stream must not suppress URP's built-in
                        // shadow renderer list or another recorder.
                        Debug.LogException(exception);
                    }
                }
            }

            if(shadowRendererList.isValid)
                cmd.DrawRendererList(shadowRendererList);

            cmd.DisableScissorRect();
            cmd.SetGlobalDepthBias(0.0f, 0.0f); // Restore previous depth bias values
        }

        /// <summary>
        /// Renders shadows to a shadow slice.
        /// </summary>
        /// <param name="cmd"></param>
        /// <param name="context"></param>
        /// <param name="shadowSliceData"></param>
        /// <param name="settings"></param>
        public static void RenderShadowSlice(CommandBuffer cmd, ref ScriptableRenderContext context,
            ref ShadowSliceData shadowSliceData, ref ShadowDrawingSettings settings)
        {
            RenderShadowSlice(cmd, ref context, ref shadowSliceData, ref settings,
                shadowSliceData.projectionMatrix, shadowSliceData.viewMatrix);
        }

        /// <summary>
        /// Calculates the maximum tile resolution in an Atlas.
        /// </summary>
        /// <param name="atlasWidth"></param>
        /// <param name="atlasHeight"></param>
        /// <param name="tileCount"></param>
        /// <returns>The maximum tile resolution in an Atlas.</returns>
        public static int GetMaxTileResolutionInAtlas(int atlasWidth, int atlasHeight, int tileCount)
        {
            if (atlasWidth <= 0 || atlasHeight <= 0)
                return 0;

            int resolution = Mathf.Min(atlasWidth, atlasHeight);
            if (tileCount <= 1)
                return resolution;

            while (resolution > 1)
            {
                long currentTileCount = (long)(atlasWidth / resolution) * (atlasHeight / resolution);
                if (currentTileCount >= tileCount)
                    break;

                resolution >>= 1;
            }

            return resolution;
        }

        /// <summary>
        /// Used for baking bake cascade transforms in each shadow matrix.
        /// </summary>
        /// <param name="shadowSliceData"></param>
        /// <param name="atlasWidth"></param>
        /// <param name="atlasHeight"></param>
        public static void ApplySliceTransform(ref ShadowSliceData shadowSliceData, int atlasWidth, int atlasHeight)
        {
            Matrix4x4 sliceTransform = Matrix4x4.identity;
            float oneOverAtlasWidth = 1.0f / atlasWidth;
            float oneOverAtlasHeight = 1.0f / atlasHeight;
            sliceTransform.m00 = shadowSliceData.resolution * oneOverAtlasWidth;
            sliceTransform.m11 = shadowSliceData.resolution * oneOverAtlasHeight;
            sliceTransform.m03 = shadowSliceData.offsetX * oneOverAtlasWidth;
            sliceTransform.m13 = shadowSliceData.offsetY * oneOverAtlasHeight;

            // Apply shadow slice scale and offset
            shadowSliceData.shadowTransform = sliceTransform * shadowSliceData.shadowTransform;
        }

        /// <summary>
        /// Calculates the depth and normal bias from a light.
        /// </summary>
        /// <param name="shadowLight"></param>
        /// <param name="shadowLightIndex"></param>
        /// <param name="shadowData"></param>
        /// <param name="lightProjectionMatrix"></param>
        /// <param name="shadowResolution"></param>
        /// <returns>The depth and normal bias from a visible light.</returns>
        public static Vector4 GetShadowBias(ref VisibleLight shadowLight, int shadowLightIndex, ref ShadowData shadowData, Matrix4x4 lightProjectionMatrix, float shadowResolution)
        {
            return GetShadowBias(ref shadowLight, shadowLightIndex, shadowData.bias, shadowData.supportsSoftShadows, lightProjectionMatrix, shadowResolution);
        }

        /// <summary>
        /// Calculates the depth and normal bias from a light.
        /// </summary>
        /// <param name="shadowLight"></param>
        /// <param name="shadowLightIndex"></param>
        /// <param name="shadowData"></param>
        /// <param name="lightProjectionMatrix"></param>
        /// <param name="shadowResolution"></param>
        /// <returns>The depth and normal bias from a visible light.</returns>
        public static Vector4 GetShadowBias(ref VisibleLight shadowLight, int shadowLightIndex, UniversalShadowData shadowData, Matrix4x4 lightProjectionMatrix, float shadowResolution)
        {
            return GetShadowBias(ref shadowLight, shadowLightIndex, shadowData.bias, shadowData.supportsSoftShadows, lightProjectionMatrix, shadowResolution);
        }

        static Vector4 GetShadowBias(ref VisibleLight shadowLight, int shadowLightIndex, List<Vector4> bias, bool supportsSoftShadows, Matrix4x4 lightProjectionMatrix, float shadowResolution)
        {
            if (shadowLightIndex < 0 || shadowLightIndex >= bias.Count)
            {
                Debug.LogWarning(string.Format("{0} is not a valid light index.", shadowLightIndex));
                return Vector4.zero;
            }

            float frustumSize;
            if (shadowLight.lightType == LightType.Directional)
            {
                // Frustum size is guaranteed to be a cube as we wrap shadow frustum around a sphere
                frustumSize = 2.0f / lightProjectionMatrix.m00;
            }
            else if (shadowLight.lightType == LightType.Spot)
            {
                // For perspective projections, shadow texel size varies with depth
                // It will only work well if done in receiver side in the pixel shader. Currently UniversalRP
                // do bias on caster side in vertex shader. When we add shader quality tiers we can properly
                // handle this. For now, as a poor approximation we do a constant bias and compute the size of
                // the frustum as if it was orthogonal considering the size at mid point between near and far planes.
                // Depending on how big the light range is, it will be good enough with some tweaks in bias
                frustumSize = Mathf.Tan(shadowLight.spotAngle * 0.5f * Mathf.Deg2Rad) * shadowLight.range; // half-width (in world-space units) of shadow frustum's "far plane"
            }
            else if (shadowLight.lightType == LightType.Point)
            {
                // [Copied from above case:]
                // "For perspective projections, shadow texel size varies with depth
                //  It will only work well if done in receiver side in the pixel shader. Currently UniversalRP
                //  do bias on caster side in vertex shader. When we add shader quality tiers we can properly
                //  handle this. For now, as a poor approximation we do a constant bias and compute the size of
                //  the frustum as if it was orthogonal considering the size at mid point between near and far planes.
                //  Depending on how big the light range is, it will be good enough with some tweaks in bias"
                // Note: HDRP uses normalBias both in HDShadowUtils.CalcGuardAnglePerspective and HDShadowAlgorithms/EvalShadow_NormalBias (receiver bias)
                float fovBias = Internal.AdditionalLightsShadowCasterPass.GetPointLightShadowFrustumFovBiasInDegrees((int)shadowResolution, (shadowLight.light.shadows == LightShadows.Soft));
                // Note: the same fovBias was also used to compute ShadowUtils.ExtractPointLightMatrix
                float cubeFaceAngle = 90 + fovBias;
                frustumSize = Mathf.Tan(cubeFaceAngle * 0.5f * Mathf.Deg2Rad) * shadowLight.range; // half-width (in world-space units) of shadow frustum's "far plane"
            }
            else
            {
                Debug.LogWarning("Only point, spot and directional shadow casters are supported in universal pipeline");
                frustumSize = 0.0f;
            }

            // depth and normal bias scale is in shadowmap texel size in world space
            float texelSize = frustumSize / shadowResolution;
            float depthBias = -bias[shadowLightIndex].x * texelSize;
            float normalBias = -bias[shadowLightIndex].y * texelSize;

            // The current implementation of NormalBias in Universal RP is the same as in Unity Built-In RP (i.e moving shadow caster vertices along normals when projecting them to the shadow map).
            // This does not work well with Point Lights, which is why NormalBias value is hard-coded to 0.0 in Built-In RP (see value of unity_LightShadowBias.z in FrameDebugger, and native code that sets it: https://github.cds.internal.unity3d.com/unity/unity/blob/a9c916ba27984da43724ba18e70f51469e0c34f5/Runtime/Camera/Shadows.cpp#L1686 )
            // We follow the same convention in Universal RP:
            if (shadowLight.lightType == LightType.Point)
                normalBias = 0.0f;

            if (supportsSoftShadows && shadowLight.light.shadows == LightShadows.Soft)
            {
                SoftShadowQuality softShadowQuality = SoftShadowQuality.Medium;
                if (shadowLight.light.TryGetComponent(out UniversalAdditionalLightData additionalLightData))
                    softShadowQuality = additionalLightData.softShadowQuality;

                // TODO: depth and normal bias assume sample is no more than 1 texel away from shadowmap
                // This is not true with PCF. Ideally we need to do either
                // cone base bias (based on distance to center sample)
                // or receiver place bias based on derivatives.
                // For now we scale it by the PCF kernel size of non-mobile platforms (5x5)
                float kernelRadius = 2.5f;

                switch (softShadowQuality)
                {
                    case SoftShadowQuality.High: kernelRadius = 3.5f; break; // 7x7
                    case SoftShadowQuality.Medium: kernelRadius = 2.5f; break; // 5x5
                    case SoftShadowQuality.Low: kernelRadius = 1.5f; break; // 3x3
                    default: break;
                }

                depthBias *= kernelRadius;
                normalBias *= kernelRadius;
            }

            return new Vector4(depthBias, normalBias, (float)shadowLight.lightType, 0.0f);
        }


        /// <summary>
        /// Extract scale and bias from a fade distance to achieve a linear fading of the fade distance.
        /// </summary>
        /// <param name="fadeDistance">Distance at which object should be totally fade</param>
        /// <param name="border">Normalized distance of fade</param>
        /// <param name="scale">[OUT] Slope of the fading on the fading part</param>
        /// <param name="bias">[OUT] Ordinate of the fading part at abscissa 0</param>
        internal static void GetScaleAndBiasForLinearDistanceFade(float fadeDistance, float border, out float scale, out float bias)
        {
            // To avoid division from zero
            // This values ensure that fade within cascade will be 0 and outside 1
            if (border < 0.0001f)
            {
                float multiplier = 1000f; // To avoid blending if difference is in fractions
                scale = multiplier;
                bias = -fadeDistance * multiplier;
                return;
            }

            border = 1 - border;
            border *= border;

            // Fade with distance calculation is just a linear fade from 90% of fade distance to fade distance. 90% arbitrarily chosen but should work well enough.
            float distanceFadeNear = border * fadeDistance;
            scale = 1.0f / (fadeDistance - distanceFadeNear);
            bias = -distanceFadeNear / (fadeDistance - distanceFadeNear);
        }

        /// <summary>
        /// Sets up the shadow bias, light direction and position for rendering.
        /// </summary>
        /// <param name="cmd"></param>
        /// <param name="shadowLight"></param>
        /// <param name="shadowBias"></param>
        public static void SetupShadowCasterConstantBuffer(CommandBuffer cmd, ref VisibleLight shadowLight, Vector4 shadowBias)
        {
            SetupShadowCasterConstantBuffer(CommandBufferHelpers.GetRasterCommandBuffer(cmd), ref shadowLight, shadowBias);
        }

        internal static void SetupShadowCasterConstantBuffer(RasterCommandBuffer cmd, ref VisibleLight shadowLight, Vector4 shadowBias)
        {
            SetShadowBias(cmd, shadowBias);

            // Light direction is currently used in shadow caster pass to apply shadow normal offset (normal bias).
            Vector3 lightDirection = -shadowLight.localToWorldMatrix.GetColumn(2);
            SetLightDirection(cmd, lightDirection);

            // For punctual lights, computing light direction at each vertex position provides more consistent results (shadow shape does not change when "rotating the point light" for example)
            Vector3 lightPosition = shadowLight.localToWorldMatrix.GetColumn(3);
            SetLightPosition(cmd, lightPosition);
        }

        internal static void SetShadowBias(RasterCommandBuffer cmd, Vector4 shadowBias)
        {
            cmd.SetGlobalVector(ShaderPropertyId.shadowBias, shadowBias);
        }

        internal static void SetLightDirection(RasterCommandBuffer cmd, Vector3 lightDirection)
        {
            cmd.SetGlobalVector(ShaderPropertyId.lightDirection, new Vector4(lightDirection.x, lightDirection.y, lightDirection.z, 0.0f));
        }

        internal static void SetLightPosition(RasterCommandBuffer cmd, Vector3 lightPosition)
        {
            cmd.SetGlobalVector(ShaderPropertyId.lightPosition, new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, 1.0f));
        }

        internal static void SetCameraPosition(RasterCommandBuffer cmd, Vector3 worldSpaceCameraPos)
        {
            cmd.SetGlobalVector(ShaderPropertyId.worldSpaceCameraPos, worldSpaceCameraPos);
        }

        internal static void SetWorldToCameraAndCameraToWorldMatrices(RasterCommandBuffer cmd, Matrix4x4 viewMatrix)
        {
            // There's an inconsistency in handedness between unity_matrixV and unity_WorldToCamera
            // Unity changes the handedness of unity_WorldToCamera (see Camera::CalculateMatrixShaderProps)
            // we will also change it here to avoid breaking existing shaders. (case 1257518)
            Matrix4x4 worldToCameraMatrix = Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)) * viewMatrix;
            Matrix4x4 cameraToWorldMatrix = worldToCameraMatrix.inverse;
            cmd.SetGlobalMatrix(ShaderPropertyId.worldToCameraMatrix, worldToCameraMatrix);
            cmd.SetGlobalMatrix(ShaderPropertyId.cameraToWorldMatrix, cameraToWorldMatrix);
        }

        private static RenderTextureDescriptor GetTemporaryShadowTextureDescriptor(int width, int height, int bits)
        {
            var format = Experimental.Rendering.GraphicsFormatUtility.GetDepthStencilFormat(bits, 0);
            RenderTextureDescriptor rtd = new RenderTextureDescriptor(width, height, Experimental.Rendering.GraphicsFormat.None, format);
            rtd.shadowSamplingMode = RenderingUtils.SupportsRenderTextureFormat(RenderTextureFormat.Shadowmap) ? ShadowSamplingMode.CompareDepths : ShadowSamplingMode.None;
            return rtd;
        }

        /// <summary>
        /// Return true if handle does not match the requirements
        /// </summary>
        /// <param name="handle">RTHandle to check (can be null).</param>
        /// <param name="width">Width of the RTHandle to match.</param>
        /// <param name="height">Height of the RTHandle to match.</param>
        /// <param name="bits">Depth bits of the RTHandle to match.</param>
        /// <param name="anisoLevel">Anisotropic filtering level of the RTHandle to match.</param>
        /// <param name="mipMapBias">Bias applied to mipmaps during filtering of the RTHandle to match.</param>
        /// <param name="name">Name of the RTHandle of the RTHandle to match.</param>
        /// <returns>If the RTHandle needs to be re-allocated</returns>
        public static bool ShadowRTNeedsReAlloc(RTHandle handle, int width, int height, int bits, int anisoLevel, float mipMapBias, string name)
        {
            if (handle == null || handle.rt == null)
                return true;
            var descriptor = GetTemporaryShadowTextureDescriptor(width, height, bits);
            if (m_ForceShadowPointSampling)
            {
                if (handle.rt.filterMode != FilterMode.Point)
                    return true;
            }
            else
            {
                if (handle.rt.filterMode != FilterMode.Bilinear)
                    return true;
            }

            TextureDesc shadowDesc = RTHandleResourcePool.CreateTextureDesc(descriptor, TextureSizeMode.Explicit, anisoLevel, mipMapBias, m_ForceShadowPointSampling ? FilterMode.Point : FilterMode.Bilinear, TextureWrapMode.Clamp, name);
            return RenderingUtils.RTHandleNeedsReAlloc(handle, shadowDesc, false);
        }

        /// <summary>
        /// Allocate a Shadow Map
        /// </summary>
        /// <param name="width">Width of the Shadow Map.</param>
        /// <param name="height">Height of the Shadow Map.</param>
        /// <param name="bits">Minimum depth bits of the Shadow Map.</param>
        /// <param name="anisoLevel">Anisotropic filtering level of the Shadow Map.</param>
        /// <param name="mipMapBias">Bias applied to mipmaps during filtering of the Shadow Map.</param>
        /// <param name="name">Name of the Shadow Map.</param>
        /// <returns>If an RTHandle for the Shadow Map</returns>
        public static RTHandle AllocShadowRT(int width, int height, int bits, int anisoLevel, float mipMapBias, string name)
        {
            var rtd = GetTemporaryShadowTextureDescriptor(width, height, bits);
            return RTHandles.Alloc(rtd, m_ForceShadowPointSampling ? FilterMode.Point : FilterMode.Bilinear, TextureWrapMode.Clamp, isShadowMap: true, name: name);
        }

        /// <summary>
        /// Allocate a Shadow Map or re-allocate if it doesn't match requirements.
        /// For use only if the map requirements changes at runtime.
        /// </summary>
        /// <param name="handle">RTHandle to check (can be null).</param>
        /// <param name="width">Width of the Shadow Map.</param>
        /// <param name="height">Height of the Shadow Map.</param>
        /// <param name="bits">Minimum depth bits of the Shadow Map.</param>
        /// <param name="anisoLevel">Anisotropic filtering level of the Shadow Map.</param>
        /// <param name="mipMapBias">Bias applied to mipmaps during filtering of the Shadow Map.</param>
        /// <param name="name">Name of the Shadow Map.</param>
        /// <returns>If the RTHandle was re-allocated</returns>
        public static bool ShadowRTReAllocateIfNeeded(ref RTHandle handle, int width, int height, int bits, int anisoLevel = 1, float mipMapBias = 0, string name = "")
        {
            if (ShadowRTNeedsReAlloc(handle, width, height, bits, anisoLevel, mipMapBias, name))
            {
                handle?.Release();
                handle = AllocShadowRT(width, height, bits, anisoLevel, mipMapBias, name);
                return true;
            }
            return false;
        }

        static Matrix4x4 GetShadowTransform(Matrix4x4 proj, Matrix4x4 view)
        {
            // Currently CullResults ComputeDirectionalShadowMatricesAndCullingPrimitives doesn't
            // apply z reversal to projection matrix. We need to do it manually here.
            if (SystemInfo.usesReversedZBuffer)
            {
                proj.m20 = -proj.m20;
                proj.m21 = -proj.m21;
                proj.m22 = -proj.m22;
                proj.m23 = -proj.m23;
            }

            Matrix4x4 worldToShadow = proj * view;

            var textureScaleAndBias = Matrix4x4.identity;
            textureScaleAndBias.m00 = 0.5f;
            textureScaleAndBias.m11 = 0.5f;
            textureScaleAndBias.m22 = 0.5f;
            textureScaleAndBias.m03 = 0.5f;
            textureScaleAndBias.m23 = 0.5f;
            textureScaleAndBias.m13 = 0.5f;
            // textureScaleAndBias maps texture space coordinates from [-1,1] to [0,1]

            // Apply texture scale and offset to save a MAD in shader.
            return textureScaleAndBias * worldToShadow;
        }

        internal static float SoftShadowQualityToShaderProperty(Light light, bool softShadowsEnabled)
        {
            float softShadows = softShadowsEnabled ? 1.0f : 0.0f;
            if (light.TryGetComponent(out UniversalAdditionalLightData additionalLightData))
            {
                var softShadowQuality = (additionalLightData.softShadowQuality == SoftShadowQuality.UsePipelineSettings)
                    ? UniversalRenderPipeline.asset?.softShadowQuality
                    : additionalLightData.softShadowQuality;
                softShadows *= Math.Max((int)softShadowQuality, (int)SoftShadowQuality.Low);
            }

            return softShadows;
        }

        internal static bool SupportsPerLightSoftShadowQuality()
        {
            bool supportsPerLightSoftShadowQuality = true;
            #if ENABLE_VR && ENABLE_VR_MODULE
            #if PLATFORM_WINRT || PLATFORM_ANDROID
                // We are using static branches on Quest2 + HL for performance reasons
                supportsPerLightSoftShadowQuality = !PlatformAutoDetect.isXRMobile;
            #endif
            #endif
            return supportsPerLightSoftShadowQuality;
        }

        internal static void SetPerLightSoftShadowKeyword(RasterCommandBuffer cmd, bool hasSoftShadows)
        {
            if (SupportsPerLightSoftShadowQuality())
                cmd.SetKeyword(ShaderGlobalKeywords.SoftShadows, hasSoftShadows);
        }

        internal static void SetSoftShadowQualityShaderKeywords(RasterCommandBuffer cmd, UniversalShadowData shadowData)
        {
            cmd.SetKeyword(ShaderGlobalKeywords.SoftShadows, shadowData.isKeywordSoftShadowsEnabled);
            if (SupportsPerLightSoftShadowQuality())
            {
                cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsLow, false);
                cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsMedium, false);
                cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsHigh, false);
            }
            else
            {
                if (shadowData.isKeywordSoftShadowsEnabled && UniversalRenderPipeline.asset?.softShadowQuality == SoftShadowQuality.Low)
                {
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsLow, true);
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsMedium, false);
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsHigh, false);
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadows, false);
                }
                else if (shadowData.isKeywordSoftShadowsEnabled && UniversalRenderPipeline.asset?.softShadowQuality == SoftShadowQuality.Medium)
                {
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsLow, false);
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsMedium, true);
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsHigh, false);
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadows, false);
                }
                else if (shadowData.isKeywordSoftShadowsEnabled && UniversalRenderPipeline.asset?.softShadowQuality == SoftShadowQuality.High)
                {
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsLow, false);
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsMedium, false);
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsHigh, true);
                    cmd.SetKeyword(ShaderGlobalKeywords.SoftShadows, false);
                }
            }
        }

        internal static bool IsValidShadowCastingLight(UniversalLightData lightData, int i)
        {
            ref VisibleLight shadowLight = ref lightData.visibleLights.UnsafeElementAt(i);
            Light light = shadowLight.light;

            if (light == null)
                return false;

            return IsValidShadowCastingLight(lightData, i, shadowLight.lightType, light.shadows, light.shadowStrength);
        }

        internal static bool IsValidShadowCastingLight(UniversalLightData lightData, int i, LightType lightType, LightShadows lightShadows, float shadowStrength)
        {
            if (i == lightData.mainLightIndex)
                return false;

            // Directional and light shadows are not supported in the shadow map atlas
            if (lightType == LightType.Directional)
                return false;

            return lightShadows != LightShadows.None && shadowStrength > 0.0f;
        }

        internal static int GetPunctualLightShadowSlicesCount(in LightType lightType)
        {
            switch (lightType)
            {
                case LightType.Spot:
                    return 1;
                case LightType.Point:
                    return 6;
                default:
                    return 0;
            }
        }

        internal static bool FastApproximately(float a, float b)
        {
            return Mathf.Abs(a - b) < 0.000001f;
        }

        internal static bool FastApproximately(Vector4 a, Vector4 b)
        {
            return FastApproximately(a.x, b.x)
                && FastApproximately(a.y, b.y)
                && FastApproximately(a.z, b.z)
                && FastApproximately(a.w, b.w);
        }

        internal const int kMinimumPunctualLightHardShadowResolution = 8;
        internal const int kMinimumPunctualLightSoftShadowResolution = 16;
        // Minimal shadow map resolution required to have meaningful shadows visible during lighting
        internal static int MinimalPunctualLightShadowResolution(bool softShadow)
        {
            return softShadow ? kMinimumPunctualLightSoftShadowResolution : kMinimumPunctualLightHardShadowResolution;
        }
    }
}
