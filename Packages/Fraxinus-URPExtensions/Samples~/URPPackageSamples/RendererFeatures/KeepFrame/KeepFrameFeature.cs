using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using CoreRenderGraphUtils = UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;

// This renderer feature will replicate a "don't clear" behaviour by injecting two passes into the pipeline:
// One pass that copies color at the end of a frame
// Another pass that draws the content of the copied texture at the beginning of a new frame
// Both passes are recorded exclusively through RenderGraph.
public class KeepFrameFeature : ScriptableRendererFeature
{
    // This pass is responsible for copying color to a specified destination
    class CopyFramePass : ScriptableRenderPass
    {
        RTHandle m_Destination;

        public void Setup(RTHandle destination)
        {
            m_Destination = destination;
        }

        // RecordRenderGraph declares resources and records the copy pass.
        // Because RenderGraph has to calculate internally how resources are used we must be aware of 2
        // distinct timelines inside this method: one for recording resource usage and one for recording draw commands.
        // It is important to scope resources correctly as global state may change between the execution times of each.
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            if (cameraData.camera.cameraType != CameraType.Game)
                return;

            TextureHandle source = resourceData.activeColorTexture;

            // When using the RenderGraph API the lifetime and ownership of resources is managed by the render graph system itself.
            // This allows for optimal resource usage and other optimizations to be done automatically for the user.
            // In the cases where resources must persist across frames, between different cameras or when users want
            // to manage their lifetimes themselves, the resources must be imported when recording the render pass.
            TextureHandle destination = renderGraph.ImportTexture(m_Destination);

            if (!source.IsValid() || !destination.IsValid())
                return;

            CoreRenderGraphUtils.BlitMaterialParameters para = new(source, destination, Blitter.GetBlitMaterial(TextureDimension.Tex2D), 0);
            renderGraph.AddBlitPass(para, "Copy Frame Pass");
        }
    }

    // This pass is responsible for drawing the old color to a full screen quad
    class DrawOldFramePass : ScriptableRenderPass
    {
        class PassData
        {
            public TextureHandle source;
            public Material material;
            public string name;
        }

        Material m_DrawOldFrameMaterial;
        RTHandle m_Handle;
        string m_TextureName;

        public void Setup(Material drawOldFrameMaterial, RTHandle handle, string textureName)
        {
            m_DrawOldFrameMaterial = drawOldFrameMaterial;
            m_TextureName = textureName;
            m_Handle = handle;
        }

        // Keep the render callback separate from graph resource declaration.
        static void ExecutePass(RasterCommandBuffer cmd, RTHandle source, Material material)
        {
            if (material == null)
                return;

            Vector2 viewportScale = source.useScaling ? new Vector2(source.rtHandleProperties.rtHandleScale.x, source.rtHandleProperties.rtHandleScale.y) : Vector2.one;
            Blitter.BlitTexture(cmd, source, viewportScale, material, 0);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            TextureHandle oldFrameTextureHandle = renderGraph.ImportTexture(m_Handle);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Draw Old Frame Pass", out var passData))
            {
                TextureHandle destination = resourceData.activeColorTexture;

                if (!oldFrameTextureHandle.IsValid() || !destination.IsValid())
                    return;

                passData.material = m_DrawOldFrameMaterial;
                passData.source = oldFrameTextureHandle;
                passData.name = m_TextureName;

                builder.UseTexture(oldFrameTextureHandle, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

                // Normally global state modifications are not allowed when using RenderGraph and will result in errors.
                // In the exceptional cases where this is intentional we must let the RenderGraph API know by calling
                // AllowGlobalStateModification(true). Use this only where necessary as it will introduce a sync point
                // in the frame which may have a negative impact on performance.
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalTexture(data.name, data.source);
                    ExecutePass(context.cmd, data.source, data.material);
                });
            }
        }
    }

    [Serializable]
    public class Settings
    {
        [Tooltip("The material that is used when the old frame is redrawn at the start of the new frame (before opaques).")]
        public Material displayMaterial;
        [Tooltip("The name of the texture used for referencing the copied frame. (Defaults to _FrameCopyTex if empty)")]
        public string textureName;
    }

    CopyFramePass m_CopyFrame;
    DrawOldFramePass m_DrawOldFrame;

    RTHandle m_OldFrameHandle;

    public Settings settings = new Settings();

    // In this function the passes are created and their point of injection is set
    public override void Create()
    {
        m_CopyFrame = new CopyFramePass();
        m_CopyFrame.renderPassEvent = RenderPassEvent.AfterRenderingTransparents; // Frame color is copied late in the frame

        m_DrawOldFrame = new DrawOldFramePass();
        m_DrawOldFrame.renderPassEvent = RenderPassEvent.BeforeRenderingOpaques; // Old frame is drawn early in the frame
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var descriptor = renderingData.cameraData.cameraTargetDescriptor;
        descriptor.msaaSamples = 1;
        descriptor.depthBufferBits = 0;
        descriptor.graphicsFormat = GraphicsFormat.R8G8B8A8_SRGB;
        var textureName = String.IsNullOrEmpty(settings.textureName) ? "_FrameCopyTex" : settings.textureName;
        RenderingUtils.ReAllocateHandleIfNeeded(ref m_OldFrameHandle, descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: textureName);

        m_CopyFrame.Setup(m_OldFrameHandle);
        m_DrawOldFrame.Setup(settings.displayMaterial, m_OldFrameHandle, textureName);

        renderer.EnqueuePass(m_CopyFrame);
        renderer.EnqueuePass(m_DrawOldFrame);
    }

    protected override void Dispose(bool disposing)
    {
        m_OldFrameHandle?.Release();
    }
}
