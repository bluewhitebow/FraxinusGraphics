// Fallback material used when a renderer draws an object without a native
// MotionVectors/XRMotionVectors pass.  The GUID/path are kept at the 2022
// location for serialized renderer-data compatibility; the implementation is
// shared with the Unity 6 shader through ObjectMotionVectors.hlsl.
Shader "Hidden/Universal Render Pipeline/ObjectMotionVectors"
{
    SubShader
    {
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }
            ColorMask RG

            HLSLPROGRAM
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ObjectMotionVectors.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "XRMotionVectors"
            Tags { "LightMode" = "XRMotionVectors" }
            ColorMask RGB

            // Mark pixels covered by an object so the XR depth/motion pass can
            // distinguish object motion from camera motion.
            Stencil
            {
                WriteMask 1
                Ref 1
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #define APLICATION_SPACE_WARP_MOTION 1
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ObjectMotionVectors.hlsl"
            ENDHLSL
        }
    }
}
