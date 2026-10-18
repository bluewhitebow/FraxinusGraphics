using System.Runtime.CompilerServices;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Implement this interface on post-process volume components compiled
    /// against the Unity 2022 URP namespace.
    /// </summary>
    public interface IPostProcessComponent : UnityEngine.Rendering.IPostProcessComponent
    {
        /// <summary>Returns whether the component is active.</summary>
        new bool IsActive();

    }

    /// <summary>
    /// Renders the post-processing effect stack.
    /// </summary>
    internal partial class PostProcessPass : ScriptableRenderPass
    {
        RenderTextureDescriptor m_Descriptor;
        TextureHandle[] _BloomMipUp;
        TextureHandle[] _BloomMipDown;
        RTHandle m_UserLut;

        MaterialLibrary m_Materials;
        PostProcessData m_Data;

        // Builtin effects settings
        DepthOfField m_DepthOfField;
        MotionBlur m_MotionBlur;
        ScreenSpaceLensFlare m_LensFlareScreenSpace;
        PaniniProjection m_PaniniProjection;
        Bloom m_Bloom;
        LensDistortion m_LensDistortion;
        ChromaticAberration m_ChromaticAberration;
        Vignette m_Vignette;
        ColorLookup m_ColorLookup;
        ColorAdjustments m_ColorAdjustments;
        Tonemapping m_Tonemapping;
        FilmGrain m_FilmGrain;

        // Depth Of Field shader passes
        const int k_GaussianDoFPassComputeCoc = 0;
        const int k_GaussianDoFPassDownscalePrefilter = 1;
        const int k_GaussianDoFPassBlurH = 2;
        const int k_GaussianDoFPassBlurV = 3;
        const int k_GaussianDoFPassComposite = 4;

        const int k_BokehDoFPassComputeCoc = 0;
        const int k_BokehDoFPassDownscalePrefilter = 1;
        const int k_BokehDoFPassBlur = 2;
        const int k_BokehDoFPassPostFilter = 3;
        const int k_BokehDoFPassComposite = 4;


        // Misc
        const int k_MaxPyramidSize = 16;
        readonly GraphicsFormat m_DefaultColorFormat;   // The default format for post-processing, follows back-buffer format in URP.
        readonly GraphicsFormat m_SMAAEdgeFormat;
        readonly GraphicsFormat m_GaussianCoCFormat;

        int m_DitheringTextureIndex;
        Vector4[] m_BokehKernel;
        int m_BokehHash;
        // Needed if the device changes its render target width/height (ex, Mobile platform allows change of orientation)
        float m_BokehMaxRadius;
        float m_BokehRCPAspect;

        // If there's a final post process pass after this pass.
        // If yes, Film Grain and Dithering are setup in the final pass, otherwise they are setup in this pass.
        bool m_HasFinalPass;

        // Some Android devices do not support sRGB backbuffer
        // We need to do the conversion manually on those
        // Also if HDR output is active
        bool m_EnableColorEncodingIfNeeded;

        // Use Fast conversions between SRGB and Linear
        bool m_UseFastSRGBLinearConversion;

        // Support Screen Space Lens Flare post process effect
        bool m_SupportScreenSpaceLensFlare;

        // Support Data Driven Lens Flare post process effect
        bool m_SupportDataDrivenLensFlare;

        Material m_BlitMaterial;

        ImageUpscalingFilter GetSupportedUpscalingFilter(ImageUpscalingFilter requested)
        {
            switch (requested)
            {
                case ImageUpscalingFilter.SGSR2:
                    // Keep the requested algorithm visible to the final pass.
                    // SGSR2 has a deterministic linear fallback when its
                    // optional material/history is unavailable; silently
                    // changing it to SGSR1 would lose the temporal path and
                    // make capability/diagnostic reporting incorrect.
                    return ImageUpscalingFilter.SGSR2;

                case ImageUpscalingFilter.FSR:
                    if (m_Materials.easu != null && m_Materials.scalingSetup != null)
                        return ImageUpscalingFilter.FSR;

                    return m_Materials.sgsr1 != null
                        ? ImageUpscalingFilter.SGSR1
                        : ImageUpscalingFilter.Linear;

                case ImageUpscalingFilter.SGSR1:
                    return m_Materials.sgsr1 != null
                        ? ImageUpscalingFilter.SGSR1
                        : ImageUpscalingFilter.Linear;

                case ImageUpscalingFilter.STP:
                case ImageUpscalingFilter.DLSS:
                case ImageUpscalingFilter.MetalFXSpatial:
                case ImageUpscalingFilter.MetalFXTemporal:
                    // STP is executed by the main RenderGraph post-process chain. The
                    // other temporal/vendor upscalers do not have a complete backend in
                    // this package, so the final pass must only perform a linear resolve.
                    return ImageUpscalingFilter.Linear;

                default:
                    return requested;
            }
        }

        // Cached bloom params from previous frame to avoid unnecessary material updates
        BloomMaterialParams m_BloomParamsPrev;

        /// <summary>
        /// Creates a new <c>PostProcessPass</c> instance.
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <param name="data">The <c>PostProcessData</c> resources to use.</param>
        /// <param name="postProcessParams">The <c>PostProcessParams</c> run-time params to use.</param>
        /// <seealso cref="RenderPassEvent"/>
        /// <seealso cref="PostProcessData"/>
        /// <seealso cref="PostProcessParams"/>
        public PostProcessPass(RenderPassEvent evt, PostProcessData data, ref PostProcessParams postProcessParams)
        {
            profilingSampler = new ProfilingSampler(nameof(PostProcessPass));
            renderPassEvent = evt;
            m_Data = data;
            m_Materials = new MaterialLibrary(data);

            // Bloom pyramid TextureHandles
            _BloomMipUp = new TextureHandle[k_MaxPyramidSize];
            _BloomMipDown = new TextureHandle[k_MaxPyramidSize];

            m_BlitMaterial = postProcessParams.blitMaterial;

            // NOTE: Request color format is the back-buffer color format. It can be HDR or SDR (when HDR disabled).
            // Request color might have alpha or might not have alpha.
            // The actual post-process target can be different. A RenderTexture with a custom format. Not necessarily a back-buffer.
            // A RenderTexture with a custom format can have an alpha channel, regardless of the back-buffer setting,
            // so the post-processing should just use the current target format/alpha to toggle alpha output.
            //
            // However, we want to filter out the alpha shader variants when not used (common case).
            // The rule is that URP post-processing format follows the back-buffer format setting.

            bool requestHDR = IsHDRFormat(postProcessParams.requestColorFormat);

            // Texture format pre-lookup
            // UUM-41070: We require `Linear | Render` but with the deprecated FormatUsage this was checking `Blend`
            // For now, we keep checking for `Blend` until the performance hit of doing the correct checks is evaluated
            if (requestHDR)
            {
                const GraphicsFormatUsage usage = GraphicsFormatUsage.Blend;
                if (GraphicsFormatCompatibility.IsFormatSupported(postProcessParams.requestColorFormat, usage))    // Typically, RGBA16Float.
                {
                    m_DefaultColorFormat = postProcessParams.requestColorFormat;
                }
                else if (GraphicsFormatCompatibility.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, usage)) // HDR fallback
                {
                    // NOTE: Technically request format can be with alpha, however if it's not supported and we fall back here
                    // , we assume no alpha. Post-process default format follows the back buffer format.
                    // If support failed, it must have failed for back buffer too.
                    m_DefaultColorFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                }
                else
                {
                    m_DefaultColorFormat = QualitySettings.activeColorSpace == ColorSpace.Linear
                        ? GraphicsFormat.R8G8B8A8_SRGB
                        : GraphicsFormat.R8G8B8A8_UNorm;
                }
            }
            else // SDR
            {
                m_DefaultColorFormat = QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? GraphicsFormat.R8G8B8A8_SRGB
                    : GraphicsFormat.R8G8B8A8_UNorm;
            }

            // Only two components are needed for edge render texture, but on some vendors four components may be faster.
            if (GraphicsFormatCompatibility.IsFormatSupported(GraphicsFormat.R8G8_UNorm, GraphicsFormatUsage.Render) && SystemInfo.graphicsDeviceVendor.ToLowerInvariant().Contains("arm"))
                m_SMAAEdgeFormat = GraphicsFormat.R8G8_UNorm;
            else
                m_SMAAEdgeFormat = GraphicsFormat.R8G8B8A8_UNorm;

            // UUM-41070: We require `Linear | Render` but with the deprecated FormatUsage this was checking `Blend`
            // For now, we keep checking for `Blend` until the performance hit of doing the correct checks is evaluated
            if (GraphicsFormatCompatibility.IsFormatSupported(GraphicsFormat.R16_UNorm, GraphicsFormatUsage.Blend))
                m_GaussianCoCFormat = GraphicsFormat.R16_UNorm;
            else if (GraphicsFormatCompatibility.IsFormatSupported(GraphicsFormat.R16_SFloat, GraphicsFormatUsage.Blend))
                m_GaussianCoCFormat = GraphicsFormat.R16_SFloat;
            else // Expect CoC banding
                m_GaussianCoCFormat = GraphicsFormat.R8_UNorm;
        }

        /// <summary>
        /// Cleans up the Material Library used in the passes.
        /// </summary>
        public void Cleanup()
        {
            m_Materials.Cleanup();
            Dispose();
        }

        /// <summary>
        /// Disposes used resources.
        /// </summary>
        public void Dispose()
        {
            m_UserLut?.Release();
            m_UserLut = null;
        }

        bool IsHDRFormat(GraphicsFormat format)
        {
            return format == GraphicsFormat.B10G11R11_UFloatPack32 ||
                   GraphicsFormatUtility.IsHalfFormat(format) ||
                   GraphicsFormatUtility.IsFloatFormat(format);
        }

        RenderTextureDescriptor GetCompatibleDescriptor(int width, int height, GraphicsFormat format, GraphicsFormat depthStencilFormat = GraphicsFormat.None)
            => GetCompatibleDescriptor(m_Descriptor, width, height, format, depthStencilFormat);

        internal static RenderTextureDescriptor GetCompatibleDescriptor(RenderTextureDescriptor desc, int width, int height, GraphicsFormat format, GraphicsFormat depthStencilFormat = GraphicsFormat.None)
        {
            desc.depthStencilFormat = depthStencilFormat;
            desc.msaaSamples = 1;
            desc.width = width;
            desc.height = height;
            desc.graphicsFormat = format;
            return desc;
        }

        bool RequireSRGBConversionBlitToBackBuffer(bool requireSrgbConversion)
        {
            return requireSrgbConversion && m_EnableColorEncodingIfNeeded;
        }

        bool RequireHDROutput(UniversalCameraData cameraData)
        {
            // If capturing, don't convert to HDR.
            // If not last in the stack, don't convert to HDR.
            return cameraData.isHDROutputActive && cameraData.captureActions == null;
        }

        #region Depth Of Field

        void PrepareBokehKernel(float maxRadius, float rcpAspect)
        {
            const int kRings = 4;
            const int kPointsPerRing = 7;

            // Check the existing array
            if (m_BokehKernel == null)
                m_BokehKernel = new Vector4[42];

            // Fill in sample points (concentric circles transformed to rotated N-Gon)
            int idx = 0;
            float bladeCount = m_DepthOfField.bladeCount.value;
            float curvature = 1f - m_DepthOfField.bladeCurvature.value;
            float rotation = m_DepthOfField.bladeRotation.value * Mathf.Deg2Rad;
            const float PI = Mathf.PI;
            const float TWO_PI = Mathf.PI * 2f;

            for (int ring = 1; ring < kRings; ring++)
            {
                float bias = 1f / kPointsPerRing;
                float radius = (ring + bias) / (kRings - 1f + bias);
                int points = ring * kPointsPerRing;

                for (int point = 0; point < points; point++)
                {
                    // Angle on ring
                    float phi = 2f * PI * point / points;

                    // Transform to rotated N-Gon
                    // Adapted from "CryEngine 3 Graphics Gems" [Sousa13]
                    float nt = Mathf.Cos(PI / bladeCount);
                    float dt = Mathf.Cos(phi - (TWO_PI / bladeCount) * Mathf.Floor((bladeCount * phi + Mathf.PI) / TWO_PI));
                    float r = radius * Mathf.Pow(nt / dt, curvature);
                    float u = r * Mathf.Cos(phi - rotation);
                    float v = r * Mathf.Sin(phi - rotation);

                    float uRadius = u * maxRadius;
                    float vRadius = v * maxRadius;
                    float uRadiusPowTwo = uRadius * uRadius;
                    float vRadiusPowTwo = vRadius * vRadius;
                    float kernelLength = Mathf.Sqrt((uRadiusPowTwo + vRadiusPowTwo));
                    float uRCP = uRadius * rcpAspect;

                    m_BokehKernel[idx] = new Vector4(uRadius, vRadius, kernelLength, uRCP);
                    idx++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float GetMaxBokehRadiusInPixels(float viewportHeight)
        {
            // Estimate the maximum radius of bokeh (empirically derived from the ring count)
            const float kRadiusInPixels = 14f;
            return Mathf.Min(0.05f, kRadiusInPixels / viewportHeight);
        }


        #endregion

        #region LensFlareDataDriven

        static float GetLensFlareLightAttenuation(Light light, Camera cam, Vector3 wo)
        {
            // Must always be true
            if (light != null)
            {
                switch (light.type)
                {
                    case LightType.Directional:
                        return LensFlareCommonSRP.ShapeAttenuationDirLight(light.transform.forward, cam.transform.forward);
                    case LightType.Point:
                        return LensFlareCommonSRP.ShapeAttenuationPointLight();
                    case LightType.Spot:
                        return LensFlareCommonSRP.ShapeAttenuationSpotConeLight(light.transform.forward, wo, light.spotAngle, light.innerSpotAngle / 180.0f);
                    default:
                        return 1.0f;
                }
            }

            return 1.0f;
        }


        #endregion

        #region Motion Blur

        internal static readonly int k_ShaderPropertyId_ViewProjM = Shader.PropertyToID("_ViewProjM");
        internal static readonly int k_ShaderPropertyId_PrevViewProjM = Shader.PropertyToID("_PrevViewProjM");
        internal static readonly int k_ShaderPropertyId_ViewProjMStereo = Shader.PropertyToID("_ViewProjMStereo");
        internal static readonly int k_ShaderPropertyId_PrevViewProjMStereo = Shader.PropertyToID("_PrevViewProjMStereo");

        internal static void UpdateMotionBlurMatrices(ref Material material, Camera camera, XRPass xr)
        {
            MotionVectorsPersistentData motionData = null;

            if(camera.TryGetComponent<UniversalAdditionalCameraData>(out var additionalCameraData))
                motionData = additionalCameraData.motionVectorsPersistentData;

            if (motionData == null)
                return;

#if ENABLE_VR && ENABLE_XR_MODULE
            if (xr.enabled && xr.singlePassEnabled)
            {
                material.SetMatrixArray(k_ShaderPropertyId_PrevViewProjMStereo, motionData.previousViewProjectionStereo);
                material.SetMatrixArray(k_ShaderPropertyId_ViewProjMStereo, motionData.viewProjectionStereo);
            }
            else
#endif
            {
                int viewProjMIdx = 0;
#if ENABLE_VR && ENABLE_XR_MODULE
                if (xr.enabled)
                    viewProjMIdx = xr.multipassId;
#endif

                // TODO: These should be part of URP main matrix set. For now, we set them here for motion vector rendering.
                material.SetMatrix(k_ShaderPropertyId_PrevViewProjM, motionData.previousViewProjectionStereo[viewProjMIdx]);
                material.SetMatrix(k_ShaderPropertyId_ViewProjM, motionData.viewProjectionStereo[viewProjMIdx]);
            }
        }



#endregion

#region Panini Projection

        // Back-ported & adapted from the work of the Stockholm demo team - thanks Lasse!

        Vector2 CalcViewExtents(Camera camera)
        {
            float fovY = camera.fieldOfView * Mathf.Deg2Rad;
            float aspect = m_Descriptor.width / (float)m_Descriptor.height;

            float viewExtY = Mathf.Tan(0.5f * fovY);
            float viewExtX = aspect * viewExtY;

            return new Vector2(viewExtX, viewExtY);
        }

        Vector2 CalcCropExtents(Camera camera, float d)
        {
            // given
            //    S----------- E--X-------
            //    |    `  ~.  /,´
            //    |-- ---    Q
            //    |        ,/    `
            //  1 |      ,´/       `
            //    |    ,´ /         ´
            //    |  ,´  /           ´
            //    |,`   /             ,
            //    O    /
            //    |   /               ,
            //  d |  /
            //    | /                ,
            //    |/                .
            //    P
            //    |              ´
            //    |         , ´
            //    +-    ´
            //
            // have X
            // want to find E

            float viewDist = 1f + d;

            var projPos = CalcViewExtents(camera);
            var projHyp = Mathf.Sqrt(projPos.x * projPos.x + 1f);

            float cylDistMinusD = 1f / projHyp;
            float cylDist = cylDistMinusD + d;
            var cylPos = projPos * cylDistMinusD;

            return cylPos * (viewDist / cylDist);
        }

#endregion

        #region Lens Distortion

        void SetupLensDistortion(Material material, bool isSceneView)
        {
            float amount = 1.6f * Mathf.Max(Mathf.Abs(m_LensDistortion.intensity.value * 100f), 1f);
            float theta = Mathf.Deg2Rad * Mathf.Min(160f, amount);
            float sigma = 2f * Mathf.Tan(theta * 0.5f);
            var center = m_LensDistortion.center.value * 2f - Vector2.one;
            var p1 = new Vector4(
                center.x,
                center.y,
                Mathf.Max(m_LensDistortion.xMultiplier.value, 1e-4f),
                Mathf.Max(m_LensDistortion.yMultiplier.value, 1e-4f)
            );
            var p2 = new Vector4(
                m_LensDistortion.intensity.value >= 0f ? theta : 1f / theta,
                sigma,
                1f / m_LensDistortion.scale.value,
                m_LensDistortion.intensity.value * 100f
            );

            material.SetVector(ShaderConstants._Distortion_Params1, p1);
            material.SetVector(ShaderConstants._Distortion_Params2, p2);

            if (m_LensDistortion.IsActive() && !isSceneView)
                material.EnableKeyword(ShaderKeywordStrings.Distortion);
        }

#endregion

#region Chromatic Aberration

        void SetupChromaticAberration(Material material)
        {
            material.SetFloat(ShaderConstants._Chroma_Params, m_ChromaticAberration.intensity.value * 0.05f);

            if (m_ChromaticAberration.IsActive())
                material.EnableKeyword(ShaderKeywordStrings.ChromaticAberration);
        }

#endregion

#region Vignette

        void SetupVignette(Material material, XRPass xrPass)
        {
            var color = m_Vignette.color.value;
            var center = m_Vignette.center.value;
            var aspectRatio = m_Descriptor.width / (float)m_Descriptor.height;


#if ENABLE_VR && ENABLE_XR_MODULE
            if (xrPass != null && xrPass.enabled)
            {
                if (xrPass.singlePassEnabled)
                    material.SetVector(ShaderConstants._Vignette_ParamsXR, xrPass.ApplyXRViewCenterOffset(center));
                else
                    // In multi-pass mode we need to modify the eye center with the values from .xy of the corrected
                    // center since the version of the shader that is not single-pass will use the value in _Vignette_Params2
                    center = xrPass.ApplyXRViewCenterOffset(center);
            }
#endif

            var v1 = new Vector4(
                color.r, color.g, color.b,
                m_Vignette.rounded.value ? aspectRatio : 1f
            );
            var v2 = new Vector4(
                center.x, center.y,
                m_Vignette.intensity.value * 3f,
                m_Vignette.smoothness.value * 5f
            );

            material.SetVector(ShaderConstants._Vignette_Params1, v1);
            material.SetVector(ShaderConstants._Vignette_Params2, v2);
        }

#endregion

        #region Film Grain

        void SetupGrain(UniversalCameraData cameraData, Material material)
        {
            if (!m_HasFinalPass && m_FilmGrain.IsActive())
            {
                material.EnableKeyword(ShaderKeywordStrings.FilmGrain);
                PostProcessUtils.ConfigureFilmGrain(
                    m_Data,
                    m_FilmGrain,
                    cameraData.pixelWidth, cameraData.pixelHeight,
                    material
                );
            }
        }

#endregion

#region 8-bit Dithering

        void SetupDithering(UniversalCameraData cameraData, Material material)
        {
            if (!m_HasFinalPass && cameraData.isDitheringEnabled)
            {
                material.EnableKeyword(ShaderKeywordStrings.Dithering);
                m_DitheringTextureIndex = PostProcessUtils.ConfigureDithering(
                    m_Data,
                    m_DitheringTextureIndex,
                    cameraData.pixelWidth, cameraData.pixelHeight,
                    material
                );
            }
        }

        #endregion

#region HDR Output
        void SetupHDROutput(HDROutputUtils.HDRDisplayInformation hdrDisplayInformation, ColorGamut hdrDisplayColorGamut, Material material, HDROutputUtils.Operation hdrOperations, bool rendersOverlayUI)
        {
            Vector4 hdrOutputLuminanceParams;
            UniversalRenderPipeline.GetHDROutputLuminanceParameters(hdrDisplayInformation, hdrDisplayColorGamut, m_Tonemapping, out hdrOutputLuminanceParams);
            material.SetVector(ShaderPropertyId.hdrOutputLuminanceParams, hdrOutputLuminanceParams);

            HDROutputUtils.ConfigureHDROutput(material, hdrDisplayColorGamut, hdrOperations);
            CoreUtils.SetKeyword(material, ShaderKeywordStrings.HDROverlay, rendersOverlayUI);
        }
#endregion

        #region Internal utilities

        class MaterialLibrary
        {
            public readonly Material stopNaN;
            public readonly Material subpixelMorphologicalAntialiasing;
            public readonly Material gaussianDepthOfField;
            public readonly Material gaussianDepthOfFieldCoC;
            public readonly Material bokehDepthOfField;
            public readonly Material bokehDepthOfFieldCoC;
            public readonly Material cameraMotionBlur;
            public readonly Material paniniProjection;
            public readonly Material bloom;
            public readonly Material[] bloomUpsample;
            public readonly Material temporalAntialiasing;
            public readonly Material scalingSetup;
            public readonly Material easu;
            public readonly Material uber;
            public readonly Material finalPass;
            public readonly Material lensFlareDataDriven;
            public readonly Material lensFlareScreenSpace;
            public readonly Material sgsr1;
            public readonly Material sgsr2;

            public MaterialLibrary(PostProcessData data)
            {
                // NOTE NOTE NOTE NOTE NOTE NOTE
                // If you create something here you must also destroy it in Cleanup()
                // or it will leak during enter/leave play mode cycles
                // NOTE NOTE NOTE NOTE NOTE NOTE
                stopNaN = Load(data.shaders.stopNanPS);
                subpixelMorphologicalAntialiasing = Load(data.shaders.subpixelMorphologicalAntialiasingPS);
                gaussianDepthOfField = Load(data.shaders.gaussianDepthOfFieldPS);
                gaussianDepthOfFieldCoC = Load(data.shaders.gaussianDepthOfFieldPS);
                bokehDepthOfField = Load(data.shaders.bokehDepthOfFieldPS);
                bokehDepthOfFieldCoC = Load(data.shaders.bokehDepthOfFieldPS);
                cameraMotionBlur = Load(data.shaders.cameraMotionBlurPS);
                paniniProjection = Load(data.shaders.paniniProjectionPS);
                bloom = Load(data.shaders.bloomPS);
                temporalAntialiasing = Load(data.shaders.temporalAntialiasingPS);
                scalingSetup = Load(data.shaders.scalingSetupPS);
                easu = Load(data.shaders.easuPS);
                uber = Load(data.shaders.uberPostPS);
                finalPass = Load(data.shaders.finalPostPassPS);
                lensFlareDataDriven = Load(data.shaders.LensFlareDataDrivenPS);
                lensFlareScreenSpace = Load(data.shaders.LensFlareScreenSpacePS);
                sgsr1 = Load(data.shaders.sgsr1PS);
                sgsr2 = Load(data.shaders.sgsr2PS);

                bloomUpsample = new Material[k_MaxPyramidSize];
                for (uint i = 0; i < k_MaxPyramidSize; ++i)
                    bloomUpsample[i] = Load(data.shaders.bloomPS);
            }

            Material Load(Shader shader)
            {
                if (shader == null)
                {
                    Debug.LogErrorFormat($"Missing shader. PostProcessing render passes will not execute. Check for missing reference in the renderer resources.");
                    return null;
                }
                else if (!shader.isSupported)
                {
                    return null;
                }

                return CoreUtils.CreateEngineMaterial(shader);
            }

            internal void Cleanup()
            {
                CoreUtils.Destroy(stopNaN);
                CoreUtils.Destroy(subpixelMorphologicalAntialiasing);
                CoreUtils.Destroy(gaussianDepthOfField);
                CoreUtils.Destroy(gaussianDepthOfFieldCoC);
                CoreUtils.Destroy(bokehDepthOfField);
                CoreUtils.Destroy(bokehDepthOfFieldCoC);
                CoreUtils.Destroy(cameraMotionBlur);
                CoreUtils.Destroy(paniniProjection);
                CoreUtils.Destroy(bloom);
                CoreUtils.Destroy(temporalAntialiasing);
                CoreUtils.Destroy(scalingSetup);
                CoreUtils.Destroy(easu);
                CoreUtils.Destroy(uber);
                CoreUtils.Destroy(finalPass);
                CoreUtils.Destroy(lensFlareDataDriven);
                CoreUtils.Destroy(lensFlareScreenSpace);
                CoreUtils.Destroy(sgsr1);
                CoreUtils.Destroy(sgsr2);

                for (uint i = 0; i < k_MaxPyramidSize; ++i)
                    CoreUtils.Destroy(bloomUpsample[i]);
            }
        }

        // Precomputed shader ids to same some CPU cycles (mostly affects mobile)
        static class ShaderConstants
        {
            public static readonly int _StencilRef = Shader.PropertyToID("_StencilRef");
            public static readonly int _StencilMask = Shader.PropertyToID("_StencilMask");

            public static readonly int _FullCoCTexture = Shader.PropertyToID("_FullCoCTexture");
            public static readonly int _HalfCoCTexture = Shader.PropertyToID("_HalfCoCTexture");
            public static readonly int _DofTexture = Shader.PropertyToID("_DofTexture");
            public static readonly int _CoCParams = Shader.PropertyToID("_CoCParams");
            public static readonly int _BokehKernel = Shader.PropertyToID("_BokehKernel");
            public static readonly int _BokehConstants = Shader.PropertyToID("_BokehConstants");

            public static readonly int _Metrics = Shader.PropertyToID("_Metrics");
            public static readonly int _AreaTexture = Shader.PropertyToID("_AreaTexture");
            public static readonly int _SearchTexture = Shader.PropertyToID("_SearchTexture");
            public static readonly int _BlendTexture = Shader.PropertyToID("_BlendTexture");

            public static readonly int _ColorTexture = Shader.PropertyToID("_ColorTexture");
            public static readonly int _Params = Shader.PropertyToID("_Params");
            public static readonly int _SourceTexLowMip = Shader.PropertyToID("_SourceTexLowMip");
            public static readonly int _Bloom_Params = Shader.PropertyToID("_Bloom_Params");
            public static readonly int _Bloom_Texture = Shader.PropertyToID("_Bloom_Texture");
            public static readonly int _LensDirt_Texture = Shader.PropertyToID("_LensDirt_Texture");
            public static readonly int _LensDirt_Params = Shader.PropertyToID("_LensDirt_Params");
            public static readonly int _LensDirt_Intensity = Shader.PropertyToID("_LensDirt_Intensity");
            public static readonly int _Distortion_Params1 = Shader.PropertyToID("_Distortion_Params1");
            public static readonly int _Distortion_Params2 = Shader.PropertyToID("_Distortion_Params2");
            public static readonly int _Chroma_Params = Shader.PropertyToID("_Chroma_Params");
            public static readonly int _Vignette_Params1 = Shader.PropertyToID("_Vignette_Params1");
            public static readonly int _Vignette_Params2 = Shader.PropertyToID("_Vignette_Params2");
            public static readonly int _Vignette_ParamsXR = Shader.PropertyToID("_Vignette_ParamsXR");
            public static readonly int _Lut_Params = Shader.PropertyToID("_Lut_Params");
            public static readonly int _UserLut_Params = Shader.PropertyToID("_UserLut_Params");
            public static readonly int _InternalLut = Shader.PropertyToID("_InternalLut");
            public static readonly int _UserLut = Shader.PropertyToID("_UserLut");
            public static readonly int _DownSampleScaleFactor = Shader.PropertyToID("_DownSampleScaleFactor");

            public static readonly int _FullscreenProjMat = Shader.PropertyToID("_FullscreenProjMat");

        }

#endregion
    }
}
