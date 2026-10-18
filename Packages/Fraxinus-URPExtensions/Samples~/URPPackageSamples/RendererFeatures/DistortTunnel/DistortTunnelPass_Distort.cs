using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using CoreRenderGraphUtils = UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;

// This pass takes the outputs from the CopyColor pass and the Tunnel pass as inputs and blits them to the screen with a Material that creates the distortion effect.
public class DistortTunnelPass_Distort : ScriptableRenderPass
{
    private Material m_Material;

    public DistortTunnelPass_Distort(Material mat, RenderPassEvent evt)
    {
        renderPassEvent = evt;
        m_Material = mat;
    }

    // Unity calls the RecordRenderGraph method to add and configure one or more render passes in the render graph system.
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        DistortTunnelRendererFeature.TexRefData texRefData = frameData.Get<DistortTunnelRendererFeature.TexRefData>();

        if (cameraData.camera.cameraType != CameraType.Game)
            return;

        if (m_Material == null)
            return;

        // Set the input and output textures for the AddBlitPass method.
        TextureHandle destination = resourceData.activeColorTexture;
        TextureHandle source = texRefData.distortTunnelTexHandle;

        if (!source.IsValid() || !destination.IsValid())
            return;

        CoreRenderGraphUtils.BlitMaterialParameters para = new(source, destination, m_Material, 0);
        renderGraph.AddBlitPass(para, "DistortTunnelPass_Distort");

    }
}
