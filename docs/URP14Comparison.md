# Why Fraxinus: changes from URP 14

[English](URP14Comparison.md) | [简体中文](URP14Comparison.zh-CN.md) · [Home](../README.md)

The reference is Unity 2022.3's URP/Core 14.0.12. Upstream links below use a fixed [Unity Graphics source snapshot](https://github.com/Unity-Technologies/Graphics/tree/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3), whose package metadata identifies that version. This is a comparison of source and workflows, not a performance benchmark.

## Design goal

Keep Unity 2022.3 while providing a newer rendering workflow and the controls needed by custom scene renderers and probe lighting. The work combines upstream backports, engine-version adaptation and Fraxinus extensions. It crosses the render scheduler, shaders, baking tools and data lifetime management, so Core and URP must evolve together.

## RenderGraph

**URP 14 already has experimental RenderGraph code.** Its [pipeline switch][urp14-graph] reads `asset.enableRenderGraph` under `RENDER_GRAPH_ENABLED` and selects `false` otherwise. Core exposes the older [`AddRenderPass` model][core14-graph]; URP's [public custom-pass API][urp14-pass] uses `Execute`, while its graph entry is internal. URP 14 also already has Native Render Pass support.

Fraxinus uses Core's typed [Raster / Compute / Unsafe builders](../Packages/Fraxinus-RPCore/Runtime/RenderGraph/RenderGraph.cs) and [Native Pass Compiler adaptation](../Packages/Fraxinus-RPCore/Runtime/RenderGraph/Compiler/NativePassCompiler.2022.cs). The latter bridges the newer compiler to Unity 2022's `ScriptableRenderContext` render-pass APIs. URP exposes [`RecordRenderGraph(RenderGraph, ContextContainer)`](../Packages/Fraxinus-URPExtensions/Runtime/Passes/ScriptableRenderPass.cs) as the execution contract.

This gives pipeline and custom passes one resource/dependency model and conditional native-pass merging. Existing `Execute`-only effects require migration; merging and performance depend on the passes and target device.

## Cached CSM

The [standard main-light shadow pass][urp14-shadows] draws the active cascades. Fraxinus adds [per-camera persistent cascade data and refresh masks](../Packages/Fraxinus-URPExtensions/Runtime/Extensions/CachedCSM/CachedCascadeShadowMap.cs), plus [`SetCachedCSMDirty()`](../Packages/Fraxinus-URPExtensions/Runtime/UniversalAdditionalCameraData.cs).

Eligible unchanged cascades can reuse their shadow maps. The first cacheable cascade and refresh workload are configurable. This is control over shadow work; dynamic caster or light changes that are not tracked need explicit invalidation to avoid stale shadows.

## Upscaling

The [URP 14 filter enum][urp14-asset] offers Automatic, Bilinear, Nearest and FSR 1. URP 14 also has TAA and motion vectors; those are existing foundations.

Fraxinus adds [STP, SGSR1 and SGSR2 selections](../Packages/Fraxinus-URPExtensions/Runtime/Data/UniversalRenderPipelineAsset.cs), [camera/temporal scheduling](../Packages/Fraxinus-URPExtensions/Runtime/UniversalRenderPipeline.cs) and the [SGSR2 history path](../Packages/Fraxinus-URPExtensions/Runtime/Extensions/SRExtensions/TAAU_SGSR2.cs). These provide more spatial/temporal reconstruction options when reducing internal resolution. They have camera and hardware requirements, and their cost must be measured on the target device.

## HZB and external drawing

URP 14 already supports Renderer Features and custom passes. Fraxinus extends that surface with [CPU/GPU HZB snapshots](../Packages/Fraxinus-URPExtensions/Runtime/UniversalRenderPipelineBRG.cs), [depth drawing hooks](../Packages/Fraxinus-URPExtensions/Runtime/Passes/DepthOnlyPass.cs) and [shadow drawing hooks](../Packages/Fraxinus-URPExtensions/Runtime/ShadowUtils.cs).

An external instancing or terrain backend can consume depth data and participate in the relevant rendering stages. These interfaces do not constitute a complete automatic GPU culling system; consumers check capability, snapshot validity and frame/resource lifetime.

## APV

**Core 14 already contains APV.** Its [baker][core14-baker] integrates with `Lightmapping` and `SetAdditionalBakedProbes`. However, the [URP 14 material GI path][urp14-gi] samples lightmaps or spherical-harmonic probes without the complete APV integration supplied here.

Fraxinus connects [APV material sampling](../Packages/Fraxinus-URPExtensions/ShaderLibrary/GlobalIllumination.hlsl) to an [independent baking session](../Packages/Fraxinus-RPCore/Editor/Lighting/APV/ProbeGIBaking.Standalone.cs) and [Compute lighting baker](../Packages/Fraxinus-RPCore/Editor/Lighting/APV). Placement, Virtual Offset, sky occlusion, Rendering Layers, lighting integration and data publication form one editor workflow. Baking Sets, lighting scenarios and APV streaming organize the result.

The gain is a usable APV baking-and-rendering workflow in this Unity 2022 URP without DXR. The baker has its own [supported-input limits](../Packages/Fraxinus-RPCore/Documentation~/StandaloneAPVBaking.md); it does not implement every native Lightmapper feature or its full denoising.

## APRTV

Standard URP 14 does not supply this precomputed transport system. Fraxinus adds [capture and baking](../Packages/Fraxinus-RPCore/Editor/Lighting/APRT), [compute relighting](../Packages/Fraxinus-RPCore/Runtime/Lighting/APRT/Resources/APRTRelight.compute), and [URP graph scheduling](../Packages/Fraxinus-URPExtensions/Runtime/Passes/APRTRelightPass.cs).

Static geometry can be relit from directional lights and HDR skies, with independent Baking Sets, two-scenario blending and per-camera controls. Geometry/material edits need rebaking; dynamic occlusion and point/spot-light transport are not supported. **APRT keeps cells for loaded scenes resident; APV's camera-driven streaming does not apply to APRT.**

## Light Transport

The companion [ray-tracing context](../Packages/Fraxinus-Light-Transport/Runtime/UnifiedRayTracing/RayTracingContext.cs), [acceleration-structure API](../Packages/Fraxinus-Light-Transport/Runtime/UnifiedRayTracing/IRayTracingAccelStruct.cs) and [ray-query shaders](../Packages/Fraxinus-Light-Transport/Runtime/UnifiedRayTracing/TraceRay.hlsl) supply Compute BVH building/traversal, closest-hit and occlusion queries. This library is separate from the baseline Core 14 package and supplies the compute backend used by the standalone baker.

It can support custom lighting or geometry queries without hardware ray tracing. Its C# interfaces are currently internal, so direct integration requires assembly access. [URP Global's configuration](../Packages/Fraxinus-URPGlobal/Runtime/ShaderConfig.cs) keeps platform light limits consistent between C# and generated HLSL; it is a supporting consistency layer, not a separate image-quality feature.

## Choosing the pipeline

Fraxinus is useful when these controls and workflows justify maintaining a custom rendering stack on Unity 2022.3. Standard URP 14 remains a simpler choice when its built-in behavior and extension points meet the project requirements. Feature availability, rendering cost and final image quality should be evaluated separately; this comparison claims no universal FPS or quality advantage.

[urp14-graph]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.universal/Runtime/UniversalRenderPipeline.cs#L304
[core14-graph]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/RenderGraph.cs#L613
[urp14-pass]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.universal/Runtime/Passes/ScriptableRenderPass.cs#L675
[urp14-shadows]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.universal/Runtime/Passes/MainLightShadowCasterPass.cs
[urp14-asset]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.universal/Runtime/Data/UniversalRenderPipelineAsset.cs#L353
[core14-baker]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/ProbeGIBaking.cs#L1992
[urp14-gi]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl#L132
