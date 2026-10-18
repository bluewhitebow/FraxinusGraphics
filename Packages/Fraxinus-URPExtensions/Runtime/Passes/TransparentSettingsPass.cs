namespace UnityEngine.Rendering.Universal.Internal
{
    // Command helper used from DrawObjectsPass' RenderGraph callback. This is
    // intentionally not a ScriptableRenderPass: it records no independent pass
    // and has no compatibility lifecycle.
    internal static class TransparentShadowSettings
    {
        internal static void SetEmptyShadowParams(RasterCommandBuffer cmd)
        {
            MainLightShadowCasterPass.SetEmptyMainLightShadowParams(cmd);
            AdditionalLightsShadowCasterPass.SetEmptyAdditionalLightShadowParams(
                cmd, AdditionalLightsShadowCasterPass.s_EmptyAdditionalLightIndexToShadowParams);
        }
    }
}
