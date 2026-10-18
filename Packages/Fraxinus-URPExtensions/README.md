# Fraxinus URP Extensions

[English](README.md) | [简体中文](README.zh-CN.md)

A custom URP fork for Unity 2022.3, providing RenderGraph rendering, cached shadows, external rendering backend interfaces, and upscaling extensions.
Package version **14.0.99**, package ID `com.unity.render-pipelines.universal`. Use with the matching Fraxinus Core and Config packages.

- **RenderGraph rendering**: organizes rendering with typed passes and explicit resource dependencies, using a Native Render Pass bridge for Unity 2022.
  Native pass merging depends on the renderer, camera, and device conditions.
- **Cached cascaded shadows**: caches main-light CSM per camera, with controls for eligible cascades, update policies, and manual invalidation
  to manage repeated rendering of static cascades.
- **HZB and external drawing interfaces**: provides CPU / GPU snapshots of historical depth pyramids and a current-frame HZB path,
  plus drawing hooks in camera, depth / normals, and shadow stages for external instancing or terrain backends.
- **STP / SGSR upscaling**: integrates STP, SGSR1 spatial upscaling, and SGSR2 temporal upscaling,
  including the associated camera jitter, motion vectors, and history buffer scheduling.
- **Multiple rendering paths**: provides Forward, Forward+, Deferred, and the 2D Renderer, retaining camera stacking, post-processing, and ShaderGraph workflows.
- **APV / APRT integration**: connects Core's probe backends to camera and light inputs, resource dependencies, material sampling, and debug views.
  APRT runs within the Universal Renderer, supporting directional-light / sky relighting and baked lighting scenarios.

This fork runs only RenderGraph. Custom passes must implement `RecordRenderGraph(RenderGraph, ContextContainer)`;
extensions that only implement the legacy `Execute` method need migration. Cached CSM does not automatically track all dynamic shadow casters or lighting changes;
call `UniversalAdditionalCameraData.SetCachedCSMDirty()` when a refresh is needed. HZB callers must check snapshot and capability query results.
DLSS / MetalFX options currently fall back to Linear. Unity's native GPU Resident Drawer backend is unavailable in Unity 2022.

Resources: [RenderGraph samples](Samples~/URPRenderGraphSamples), [APRT guide](Documentation~/APRT.md),
[cached CSM](Runtime/Extensions/CachedCSM/CachedCascadeShadowMap.cs), and [HZB interfaces](Runtime/UniversalRenderPipelineBRG.cs).

Companion packages: [Fraxinus-RPCore](../Fraxinus-RPCore/README.md) and [Fraxinus-URPGlobal](../Fraxinus-URPGlobal/README.md).

This is an independently maintained, unofficial fork. Unity-derived code is licensed under the [Unity Companion License](Unity%20Companion%20License.md).
Third-party components retain their own terms; see the [license summary](LICENSE.md) and [third-party notices](Third%20Party%20Notices.md).
