using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Draw screen space overlay UI into the given color and depth target
    /// </summary>
    internal class DrawScreenSpaceUIPass : ScriptableRenderPass
    {
        /// <summary>
        /// Creates a new <c>DrawScreenSpaceUIPass</c> instance.
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <seealso cref="RenderPassEvent"/>
        public DrawScreenSpaceUIPass(RenderPassEvent evt)
        {
            profilingSampler = ProfilingSampler.Get(URPProfileId.DrawScreenSpaceUI);
            renderPassEvent = evt;
        }

        /// <summary>
        /// Get a descriptor for the required color texture for this pass.
        /// </summary>
        /// <param name="descriptor">Camera target descriptor.</param>
        /// <param name="cameraWidth">Unscaled pixel width of the camera.</param>
        /// <param name="cameraHeight">Unscaled pixel height of the camera.</param>
        /// <seealso cref="RenderTextureDescriptor"/>
        public static void ConfigureColorDescriptor(ref RenderTextureDescriptor descriptor, int cameraWidth, int cameraHeight)
        {
            descriptor.graphicsFormat = GraphicsFormat.R8G8B8A8_SRGB;
            descriptor.depthStencilFormat = GraphicsFormat.None;
            descriptor.width = cameraWidth;
            descriptor.height = cameraHeight;
        }

        /// <summary>
        /// Get a descriptor for the required depth texture for this pass.
        /// </summary>
        /// <param name="descriptor">Camera target descriptor.</param>
        /// <param name="depthStencilFormat">Depth stencil format required.</param>
        /// <param name="cameraWidth">Unscaled pixel width of the camera.</param>
        /// <param name="cameraHeight">Unscaled pixel height of the camera.</param>
        /// <seealso cref="RenderTextureDescriptor"/>
        public static void ConfigureDepthDescriptor(ref RenderTextureDescriptor descriptor, GraphicsFormat depthStencilFormat, int cameraWidth, int cameraHeight)
        {
            descriptor.graphicsFormat = GraphicsFormat.None;
            descriptor.depthStencilFormat = depthStencilFormat;
            descriptor.width = cameraWidth;
            descriptor.height = cameraHeight;
        }

#if UNITY_6000_0_OR_NEWER
        private static void ExecutePass(RasterCommandBuffer commandBuffer, PassData passData, RendererList rendererList)
        {
            commandBuffer.DrawRendererList(rendererList);
        }

        // Specific to RG cases which have to go through Unsafe commands
        private static void ExecutePass(UnsafeCommandBuffer commandBuffer, UnsafePassData passData, RendererList rendererList)
        {
            commandBuffer.DrawRendererList(rendererList);
        }
#endif

        // RenderGraph path
        private class PassData
        {
#if UNITY_6000_0_OR_NEWER
            internal RendererListHandle rendererList;
#else
            internal Camera camera;
            internal TextureHandle offscreenTexture;
            internal TextureHandle colorTarget;
            internal TextureHandle depthTarget;
#endif
        }

        // Specific to RG cases which have to go through Unsafe commands
        private class UnsafePassData
        {
#if UNITY_6000_0_OR_NEWER
            internal RendererListHandle rendererList;
            internal TextureHandle colorTarget;
#endif
        }

        internal void RenderOffscreen(RenderGraph renderGraph, ContextContainer frameData, GraphicsFormat depthStencilFormat, out TextureHandle output)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            RenderTextureDescriptor colorDescriptor = cameraData.cameraTargetDescriptor;
            ConfigureColorDescriptor(ref colorDescriptor, cameraData.pixelWidth, cameraData.pixelHeight);
            output = UniversalRenderer.CreateRenderGraphTexture(renderGraph, colorDescriptor, "_OverlayUITexture", true);
            RenderTextureDescriptor depthDescriptor = cameraData.cameraTargetDescriptor;
            ConfigureDepthDescriptor(ref depthDescriptor, depthStencilFormat, cameraData.pixelWidth, cameraData.pixelHeight);
            TextureHandle depthBuffer = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDescriptor, "_OverlayUITexture_Depth", false);

#if UNITY_6000_0_OR_NEWER
            // Render uGUI and UIToolkit overlays
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Draw Screen Space UIToolkit/uGUI - Offscreen", out var passData, profilingSampler))
            {
                // UIToolkit/uGUI pass accept custom shaders, we need to make sure we use all global textures
                builder.UseAllGlobalTextures(true);

                builder.SetRenderAttachment(output, 0);

                passData.rendererList = renderGraph.CreateUIOverlayRendererList(cameraData.camera, UISubset.UIToolkit_UGUI);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderAttachmentDepth(depthBuffer, AccessFlags.ReadWrite);

                if (output.IsValid())
                    builder.SetGlobalTextureAfterPass(output, ShaderPropertyId.overlayUITexture);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    ExecutePass(context.cmd, data, data.rendererList);
                });
            }
            // Render IMGUI overlay and software cursor in a UnsafePass
            // Doing so allow us to safely cover cases when graphics commands called through onGUI() in user scripts are not supported by RenderPass API
            // Besides, Vulkan backend doesn't support SetSRGWrite() in RenderPass API and we have some of them at IMGUI levels
            // Note, these specific UI calls doesn't need depth buffer unlike UIToolkit/uGUI
            using (var builder = renderGraph.AddUnsafePass<UnsafePassData>("Draw Screen Space IMGUI/SoftwareCursor - Offscreen", out var passData, profilingSampler))
            {
                passData.colorTarget = output;
                builder.UseTexture(output, AccessFlags.Write);

                passData.rendererList = renderGraph.CreateUIOverlayRendererList(cameraData.camera, UISubset.LowLevel);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((UnsafePassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.colorTarget);
                    ExecutePass(context.cmd, data, data.rendererList);
                });
            }
#else
            // Unity 2022 cannot create Unity 6's split UI renderer lists. Keep the full
            // overlay in one standalone unsafe node and use the typed command-buffer bridge
            // to issue ScriptableRenderContext.DrawUIOverlay after binding the RG targets.
            using (var builder = renderGraph.AddUnsafePass<PassData>(
                       "Draw Screen Space UI - Offscreen (Unity 2022)", out var passData,
                       profilingSampler))
            {
                passData.colorTarget = output;
                passData.depthTarget = depthBuffer;
                builder.UseTexture(output, AccessFlags.Write);
                builder.UseTexture(depthBuffer, AccessFlags.ReadWrite);
                builder.UseAllGlobalTextures(true);
                passData.camera = cameraData.camera;
                passData.offscreenTexture = output;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.colorTarget, data.depthTarget);
                    context.cmd.ClearRenderTarget(false, true, Color.clear);
                    context.cmd.DrawUIOverlay(data.camera);
                    context.cmd.SetGlobalTexture(ShaderPropertyId.overlayUITexture,
                        data.offscreenTexture);
                });
            }
#endif
        }

        internal void RenderOverlay(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle colorBuffer, in TextureHandle depthBuffer)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalRenderer renderer = cameraData.renderer as UniversalRenderer;

#if UNITY_6000_0_OR_NEWER
            // Render uGUI and UIToolkit overlays
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Draw UIToolkit/uGUI Overlay", out var passData, profilingSampler))
            {
                // UIToolkit/uGUI pass accept custom shaders, we need to make sure we use all global textures
                builder.UseAllGlobalTextures(true);

                builder.SetRenderAttachment(colorBuffer, 0);
                builder.SetRenderAttachmentDepth(depthBuffer, AccessFlags.ReadWrite);

                passData.rendererList = renderGraph.CreateUIOverlayRendererList(cameraData.camera, UISubset.UIToolkit_UGUI);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    ExecutePass(context.cmd, data, data.rendererList);
                });
            }
            // Render IMGUI overlay and software cursor in a UnsafePass
            // Doing so allow us to safely cover cases when graphics commands called through onGUI() in user scripts are not supported by RenderPass API
            // Besides, Vulkan backend doesn't support SetSRGWrite() in RenderPass API and we have some of them at IMGUI levels
            // Note, these specific UI calls doesn't need depth buffer unlike UIToolkit/uGUI
            using (var builder = renderGraph.AddUnsafePass<UnsafePassData>("Draw IMGUI/SoftwareCursor Overlay", out var passData, profilingSampler))
            {
                passData.colorTarget = colorBuffer;
                builder.UseTexture(colorBuffer, AccessFlags.Write);

                passData.rendererList = renderGraph.CreateUIOverlayRendererList(cameraData.camera, UISubset.LowLevel);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((UnsafePassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.colorTarget);
                    ExecutePass(context.cmd, data, data.rendererList);
                });
            }
#else
            using (var builder = renderGraph.AddUnsafePass<PassData>(
                       "Draw Screen Space UI Overlay (Unity 2022)", out var passData,
                       profilingSampler))
            {
                passData.colorTarget = colorBuffer;
                passData.depthTarget = depthBuffer;
                builder.UseTexture(colorBuffer, AccessFlags.Write);
                builder.UseTexture(depthBuffer, AccessFlags.ReadWrite);
                builder.UseAllGlobalTextures(true);
                passData.camera = cameraData.camera;
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.colorTarget, data.depthTarget);
                    context.cmd.DrawUIOverlay(data.camera);
                });
            }
#endif
        }
    }
}
