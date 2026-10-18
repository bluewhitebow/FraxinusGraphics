using UnityEngine;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// CPU-side SGSR2 integration for the RenderGraph post-processing path.
    ///
    /// The Qualcomm shader is intentionally split into two fullscreen passes:
    /// a render-resolution depth/motion conversion and an output-resolution
    /// temporal reconstruction. Both stages share these history and reset parameters.
    /// </summary>
    public static class TAAU_SGSR2
    {
        internal const int ConvertPass = 0;
        internal const int UpscalePass = 1;
        // Keep the copy pass index in one place.  A stripped/incorrectly
        // imported SGSR2 shader must never be dispatched with an out-of-range
        // pass index (Unity reports this as a material error and leaves the
        // history target undefined).
        internal const int CopyHistoryPass = 2;
        internal const float DefaultMinLerpContribution = 0.3f;

        internal static class ShaderConstants
        {
            internal static readonly int ClipToPrevClip = Shader.PropertyToID("_ClipToPrevClip");
            internal static readonly int RenderSize = Shader.PropertyToID("_RenderSize");
            internal static readonly int OutputSize = Shader.PropertyToID("_OutputSize");
            internal static readonly int RenderSizeRcp = Shader.PropertyToID("_RenderSizeRcp");
            internal static readonly int OutputSizeRcp = Shader.PropertyToID("_OutputSizeRcp");
            internal static readonly int JitterOffset = Shader.PropertyToID("_JitterOffset");
            internal static readonly int ScaleRatio = Shader.PropertyToID("_ScaleRatio");
            internal static readonly int CameraFovAngleHor = Shader.PropertyToID("_CameraFovAngleHor");
            internal static readonly int MinLerpContribution = Shader.PropertyToID("_MinLerpContribution");
            internal static readonly int Reset = Shader.PropertyToID("_Reset");
            internal static readonly int SameCameraFrame = Shader.PropertyToID("_SameCameraFrmNum");
            internal static readonly int UseMotionVectors = Shader.PropertyToID("_Sgsr2UseMotionVectors");

            internal static readonly int MotionVectorTexture = Shader.PropertyToID("_Sgsr2MotionVectorTexture");
            internal static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            internal static readonly int HistoryTexture = Shader.PropertyToID("_Sgsr2HistoryTexture");
            internal static readonly int DepthClipTexture = Shader.PropertyToID("_DepthClipTexture");
        }

        /// <summary>
        /// Runtime settings captured before recording the RenderGraph passes.
        /// The struct is blittable and can be copied into RenderGraph pass
        /// data without retaining camera references.
        /// </summary>
        internal struct Parameters
        {
            internal Matrix4x4 clipToPrevClip;
            internal Vector2 renderSize;
            internal Vector2 outputSize;
            internal Vector2 jitterOffset;
            internal Vector2 scaleRatio;
            internal float cameraFovAngleHor;
            internal float minLerpContribution;
            internal float reset;
            internal bool useMotionVectors;
            internal bool sameCamera;
        }

        internal static bool IsSupported(Material material, Sgsr2History history)
        {
            // SGSR2 is a three-pass contract: conversion, reconstruction and
            // history copy. Check the complete contract up front to select a
            // deterministic linear fallback when a stale or stripped shader is assigned.
            return material != null && material.passCount > CopyHistoryPass && history != null;
        }

        internal static int GetEyeIndex(UniversalCameraData cameraData)
        {
            int eyeIndex = 0;
#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData != null && cameraData.xr != null && cameraData.xr.enabled && !cameraData.xr.singlePassEnabled)
                eyeIndex = cameraData.xr.multipassId;
#endif
            return eyeIndex;
        }

        internal static Matrix4x4 GetNonJitteredViewProjection(UniversalCameraData cameraData, int eyeIndex)
        {
            if (cameraData == null)
                return Matrix4x4.identity;

            return cameraData.GetGPUProjectionMatrixNoJitter(eyeIndex) * cameraData.GetViewMatrix(eyeIndex);
        }

        internal static Parameters BuildParameters(UniversalCameraData cameraData, Sgsr2History history, int eyeIndex, bool hasMotionVectors)
        {
            Parameters parameters = default;

            int renderWidth = Mathf.Max(1, cameraData.cameraTargetDescriptor.width);
            int renderHeight = Mathf.Max(1, cameraData.cameraTargetDescriptor.height);
            int outputWidth = Mathf.Max(1, cameraData.pixelWidth);
            int outputHeight = Mathf.Max(1, cameraData.pixelHeight);

            parameters.renderSize = new Vector2(renderWidth, renderHeight);
            parameters.outputSize = new Vector2(outputWidth, outputHeight);
            parameters.jitterOffset = GetJitterOffset(ref cameraData.taaSettings);

            float ratioX = (float)outputWidth / renderWidth;
            float areaRatio = (float)((outputWidth * (double)outputHeight) /
                                      (renderWidth * (double)renderHeight));
            parameters.scaleRatio = new Vector2(ratioX, Mathf.Min(20.0f, Mathf.Pow(Mathf.Max(1.0f, (float)areaRatio), 3.0f)));

            parameters.cameraFovAngleHor = cameraData.camera != null && !cameraData.camera.orthographic
                ? Mathf.Tan(cameraData.camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * renderWidth / renderHeight
                : 1.0f;
            parameters.minLerpContribution = DefaultMinLerpContribution;
            parameters.useMotionVectors = hasMotionVectors;

            Matrix4x4 currentViewProjection = GetNonJitteredViewProjection(cameraData, eyeIndex);
            bool hasPrevious = history != null && history.HasPreviousViewProjection(eyeIndex);
            Matrix4x4 previousViewProjection = hasPrevious
                ? history.GetPreviousViewProjection(eyeIndex)
                : currentViewProjection;

            // The conversion shader reconstructs previous clip coordinates
            // from the current clip position when a motion-vector texture is
            // unavailable.
            parameters.clipToPrevClip = previousViewProjection * currentViewProjection.inverse;
            parameters.sameCamera = hasPrevious;

            bool historyValid = history != null &&
                                history.GetAccumulationVersion(eyeIndex) >= 0 &&
                                hasPrevious;
            // A reset request (including a descriptor reallocation) makes
            // the first output authoritative and prevents stale history from
            // contributing to the reconstruction.
            parameters.reset = (cameraData.resetHistory || !historyValid) ? 1.0f : 0.0f;

            return parameters;
        }

        private static Vector2 GetJitterOffset(ref TemporalAA.Settings settings)
        {
            int frameIndex = TemporalAA.CalculateTaaFrameIndex(ref settings);
            TemporalAA.CalculateJitter(frameIndex, out var jitter, out var allowScaling);
            if (allowScaling)
                jitter *= settings.jitterScale;
            return jitter;
        }

        internal static void SetParameters(Material material, Parameters parameters)
        {
            if (material == null)
                return;

            material.SetMatrix(ShaderConstants.ClipToPrevClip, parameters.clipToPrevClip);
            material.SetVector(ShaderConstants.RenderSize, new Vector4(parameters.renderSize.x, parameters.renderSize.y, 0.0f, 0.0f));
            material.SetVector(ShaderConstants.OutputSize, new Vector4(parameters.outputSize.x, parameters.outputSize.y, 0.0f, 0.0f));
            material.SetVector(ShaderConstants.RenderSizeRcp, new Vector4(1.0f / parameters.renderSize.x, 1.0f / parameters.renderSize.y, 0.0f, 0.0f));
            material.SetVector(ShaderConstants.OutputSizeRcp, new Vector4(1.0f / parameters.outputSize.x, 1.0f / parameters.outputSize.y, 0.0f, 0.0f));
            material.SetVector(ShaderConstants.JitterOffset, new Vector4(parameters.jitterOffset.x, parameters.jitterOffset.y, 0.0f, 0.0f));
            material.SetVector(ShaderConstants.ScaleRatio, new Vector4(parameters.scaleRatio.x, parameters.scaleRatio.y, 0.0f, 0.0f));
            material.SetFloat(ShaderConstants.CameraFovAngleHor, parameters.cameraFovAngleHor);
            material.SetFloat(ShaderConstants.MinLerpContribution, parameters.minLerpContribution);
            material.SetFloat(ShaderConstants.Reset, parameters.reset);
            material.SetInt(ShaderConstants.SameCameraFrame, parameters.sameCamera ? 1 : 0);
            material.SetInt(ShaderConstants.UseMotionVectors, parameters.useMotionVectors ? 1 : 0);
        }
    }
}
