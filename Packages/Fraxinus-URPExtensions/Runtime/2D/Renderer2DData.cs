using System;
using System.Collections.Generic;
using UnityEngine.Scripting.APIUpdating;
using UnityEngine.Serialization;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ProjectWindowCallback;
#endif

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Class <c>Renderer2DData</c> contains resources for a <c>Renderer2D</c>.
    /// </summary>
    [Serializable, ReloadGroup, ExcludeFromPreset]
    [MovedFrom(true, "UnityEngine.Experimental.Rendering.Universal", "Unity.RenderPipelines.Universal.Runtime")]
    [HelpURL("https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@latest/index.html?subfolder=/manual/2DRendererData-overview.html")]
    public partial class Renderer2DData : ScriptableRendererData
    {
        internal enum Renderer2DDefaultMaterialType
        {
            Lit,
            Unlit,
            Custom
        }

        // Kept as a renderer-specific filter for compatibility with existing
        // Renderer2DData assets. Unity 6 removed this field from upstream URP,
        // but Fraxinus assets authored before the source upgrade still rely on
        // it in both compatibility mode and RenderGraph mode.
        [SerializeField]
        LayerMask m_LayerMask = -1;

        [SerializeField]
        TransparencySortMode m_TransparencySortMode = TransparencySortMode.Default;

        [SerializeField]
        Vector3 m_TransparencySortAxis = Vector3.up;

        [SerializeField]
        float m_HDREmulationScale = 1;

        [SerializeField, Range(0.01f, 1.0f)]
        float m_LightRenderTextureScale = 0.5f;

        [SerializeField, FormerlySerializedAs("m_LightOperations")]
        Light2DBlendStyle[] m_LightBlendStyles = null;

        [SerializeField]
        bool m_UseDepthStencilBuffer = true;

        [SerializeField]
        bool m_UseCameraSortingLayersTexture = false;

        [SerializeField]
        int m_CameraSortingLayersTextureBound = 0;

        [SerializeField]
        Downsampling m_CameraSortingLayerDownsamplingMethod = Downsampling.None;

        [SerializeField]
        uint m_MaxLightRenderTextureCount = 16;

        [SerializeField]
        uint m_MaxShadowRenderTextureCount = 1;

        // Legacy Renderer2DData resource slots are retained as serialized
        // receivers so opening and saving an existing asset does not discard
        // its data. Runtime resource lookup intentionally remains on the
        // Unity 6 IRenderPipelineResources path; the four old light shaders
        // cannot be forwarded because Unity 6 replaced them with Light2D.shader.
        [SerializeField, HideInInspector]
        Shader m_ShapeLightShader;

        [SerializeField, HideInInspector]
        Shader m_ShapeLightVolumeShader;

        [SerializeField, HideInInspector]
        Shader m_PointLightShader;

        [SerializeField, HideInInspector]
        Shader m_PointLightVolumeShader;

        [SerializeField, HideInInspector]
        Shader m_CoreBlitShader;

        [SerializeField, HideInInspector]
        Shader m_BlitHDROverlay;

        [SerializeField, HideInInspector]
        Shader m_CoreBlitColorAndDepthPS;

        [SerializeField, HideInInspector]
        Shader m_CopyDepthShader;

        [SerializeField, HideInInspector]
        Shader m_SamplingShader;

        [SerializeField, HideInInspector]
        Shader m_ProjectedShadowShader;

        [SerializeField, HideInInspector]
        Shader m_SpriteShadowShader;

        [SerializeField, HideInInspector]
        Shader m_SpriteUnshadowShader;

        [SerializeField, HideInInspector]
        Shader m_GeometryUnshadowShader;

        [SerializeField, HideInInspector]
        Shader m_FallbackErrorShader;

        [SerializeField]
        PostProcessData m_PostProcessData = null;

        [SerializeField, HideInInspector]
        Texture2D m_FallOffLookup;

        /// <summary>
        /// HDR Emulation Scale allows platforms to use HDR lighting by compressing the number of expressible colors in exchange for extra intensity range.
        /// Scale describes this extra intensity range. Increasing this value too high may cause undesirable banding to occur.
        /// </summary>
        public float hdrEmulationScale => m_HDREmulationScale;
        internal float lightRenderTextureScale => m_LightRenderTextureScale;
        /// <summary>
        /// Returns a list Light2DBlendStyle
        /// </summary>
        public Light2DBlendStyle[] lightBlendStyles => m_LightBlendStyles;
        internal bool useDepthStencilBuffer => m_UseDepthStencilBuffer;
        internal PostProcessData postProcessData { get => m_PostProcessData; set { m_PostProcessData = value; } }
        internal TransparencySortMode transparencySortMode => m_TransparencySortMode;
        internal Vector3 transparencySortAxis => m_TransparencySortAxis;
        internal uint lightRenderTextureMemoryBudget => m_MaxLightRenderTextureCount;
        internal uint shadowRenderTextureMemoryBudget => m_MaxShadowRenderTextureCount;
        internal bool useCameraSortingLayerTexture => m_UseCameraSortingLayersTexture;
        internal int cameraSortingLayerTextureBound => m_CameraSortingLayersTextureBound;
        internal Downsampling cameraSortingLayerDownsamplingMethod => m_CameraSortingLayerDownsamplingMethod;
        internal LayerMask layerMask => m_LayerMask;

        /// <summary>
        /// Creates the instance of the Renderer2D.
        /// </summary>
        /// <returns>The instance of Renderer2D</returns>
        protected override ScriptableRenderer Create()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                ReloadAllNullProperties();
            }
#endif

            return new Renderer2D(this);
        }

        internal void Dispose()
        {
            for (var i = 0; i < m_LightBlendStyles.Length; ++i)
                m_LightBlendStyles[i].renderTargetHandle?.Release();

            foreach(var mat in lightMaterials)
                CoreUtils.Destroy(mat.Value);

            lightMaterials.Clear();

            CoreUtils.Destroy(spriteSelfShadowMaterial);
            CoreUtils.Destroy(spriteUnshadowMaterial);
            CoreUtils.Destroy(geometrySelfShadowMaterial);
            CoreUtils.Destroy(geometryUnshadowMaterial);
            CoreUtils.Destroy(projectedShadowMaterial);
            CoreUtils.Destroy(projectedUnshadowMaterial);
        }

        /// <summary>
        /// OnEnable implementation.
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();

            for (var i = 0; i < m_LightBlendStyles.Length; ++i)
            {
                m_LightBlendStyles[i].renderTargetHandleId = Shader.PropertyToID($"_ShapeLightTexture{i}");
                m_LightBlendStyles[i].renderTargetHandle = RTHandles.Alloc(m_LightBlendStyles[i].renderTargetHandleId, $"_ShapeLightTexture{i}");
            }

            geometrySelfShadowMaterial = null;
            geometryUnshadowMaterial = null;

            spriteSelfShadowMaterial = null;
            spriteUnshadowMaterial = null;
            projectedShadowMaterial = null;
            projectedUnshadowMaterial = null;
        }

        // transient data
        internal Dictionary<uint, Material> lightMaterials { get; } = new Dictionary<uint, Material>();
        internal Material spriteSelfShadowMaterial { get; set; }
        internal Material spriteUnshadowMaterial { get; set; }
        internal Material geometrySelfShadowMaterial { get; set; }
        internal Material geometryUnshadowMaterial { get; set; }
        internal Material projectedShadowMaterial { get; set; }
        internal Material projectedUnshadowMaterial { get; set; }

        internal RTHandle normalsRenderTarget;
        internal RTHandle cameraSortingLayerRenderTarget;

        // this shouldn've been in RenderingData along with other cull results
        internal ILight2DCullResult lightCullResult { get; set; }
    }
}
