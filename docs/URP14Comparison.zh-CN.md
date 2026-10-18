# 为什么选择 Fraxinus：与 URP 14 的改造对照

[English](URP14Comparison.md) | [简体中文](URP14Comparison.zh-CN.md) · [返回首页](../README.zh-CN.md)

对照对象为 Unity 2022.3 的 URP/Core 14.0.12。下文原版引用固定到 [Unity Graphics 公开源码快照](https://github.com/Unity-Technologies/Graphics/tree/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3)，其中包元信息标记为该版本。本文比较源码与工作流，不是性能跑分。

## 改造目的

在继续使用 Unity 2022.3 的前提下，获得较新的渲染工作流，以及自定义场景渲染器和探针光照所需的控制能力。改造包含上游功能回移、引擎版本适配和 Fraxinus 扩展，涉及渲染调度、Shader、烘焙工具与数据生命周期，因此 Core 和 URP 需要配套演进。

## RenderGraph

**URP 14 已有实验性 RenderGraph 代码。**其[管线开关][urp14-graph]仅在 `RENDER_GRAPH_ENABLED` 条件下读取 `asset.enableRenderGraph`，否则设为 `false`。Core 使用较早的 [`AddRenderPass` 模型][core14-graph]；URP 的[公开自定义 Pass 接口][urp14-pass]使用 `Execute`，图入口为 internal。URP 14 也已经支持 Native Render Pass。

Fraxinus 使用 Core 的 [Raster / Compute / Unsafe Builder](../Packages/Fraxinus-RPCore/Runtime/RenderGraph/RenderGraph.cs)，并提供 [Native Pass Compiler 适配](../Packages/Fraxinus-RPCore/Runtime/RenderGraph/Compiler/NativePassCompiler.2022.cs)，将较新的编译器桥接到 Unity 2022 的 `ScriptableRenderContext` Render Pass API。URP 将 [`RecordRenderGraph(RenderGraph, ContextContainer)`](../Packages/Fraxinus-URPExtensions/Runtime/Passes/ScriptableRenderPass.cs) 作为执行接口。

收益是管线与自定义 Pass 使用同一套资源/依赖模型，并在满足条件时合并原生渲染通道。已有的 `Execute` 扩展需要迁移；实际合并与性能取决于 Pass 和目标设备。

## Cached CSM

[标准主光源阴影 Pass][urp14-shadows]绘制有效级联。Fraxinus 增加[按相机保存的级联数据和刷新掩码](../Packages/Fraxinus-URPExtensions/Runtime/Extensions/CachedCSM/CachedCascadeShadowMap.cs)，并提供 [`SetCachedCSMDirty()`](../Packages/Fraxinus-URPExtensions/Runtime/UniversalAdditionalCameraData.cs)。

符合条件且未变化的级联可复用阴影贴图，起始缓存级联和刷新工作量均可配置。收益是控制阴影绘制开销；未被追踪的动态投影物或灯光变化需要主动使缓存失效，以免显示过期阴影。

## Upscaling

[URP 14 的升采样枚举][urp14-asset]包含 Automatic、Bilinear、Nearest 和 FSR 1。URP 14 也已有 TAA 与运动矢量，这些属于原有基础能力。

Fraxinus 增加 [STP、SGSR1、SGSR2 选项](../Packages/Fraxinus-URPExtensions/Runtime/Data/UniversalRenderPipelineAsset.cs)、[相机与时域调度](../Packages/Fraxinus-URPExtensions/Runtime/UniversalRenderPipeline.cs)和 [SGSR2 历史缓冲路径](../Packages/Fraxinus-URPExtensions/Runtime/Extensions/SRExtensions/TAAU_SGSR2.cs)。降低内部渲染分辨率时，可选择更多空间/时域重建方案；启用条件和重建开销仍需按相机配置与目标设备验证。

## HZB and external drawing

URP 14 原本就支持 Renderer Feature 和自定义 Pass。Fraxinus 在此基础上增加 [CPU/GPU HZB 快照](../Packages/Fraxinus-URPExtensions/Runtime/UniversalRenderPipelineBRG.cs)、[深度绘制挂钩](../Packages/Fraxinus-URPExtensions/Runtime/Passes/DepthOnlyPass.cs)与[阴影绘制挂钩](../Packages/Fraxinus-URPExtensions/Runtime/ShadowUtils.cs)。

外部实例化或地形后端可以读取深度数据，并参与相应绘制阶段。这些接口本身不构成完整的自动 GPU 剔除系统；接入方需要检查能力、快照有效性及帧/资源生命周期。

## APV

**Core 14 已包含 APV。**其[烘焙器][core14-baker]依赖 `Lightmapping` 和 `SetAdditionalBakedProbes`；但 [URP 14 的材质 GI 路径][urp14-gi]主要采样 Lightmap 或球谐探针，没有本库这套完整 APV 接入。

Fraxinus 将 [APV 材质采样](../Packages/Fraxinus-URPExtensions/ShaderLibrary/GlobalIllumination.hlsl)接到[独立烘焙会话](../Packages/Fraxinus-RPCore/Editor/Lighting/APV/ProbeGIBaking.Standalone.cs)和 [Compute 光照烘焙器](../Packages/Fraxinus-RPCore/Editor/Lighting/APV)。布点、Virtual Offset、天空遮挡、Rendering Layers、光照积分和数据发布形成完整编辑器流程，结果由 Baking Set、光照场景和 APV 流式加载管理。

收益是在这套 Unity 2022 URP 中完成 APV 烘焙与渲染，无需 DXR。独立烘焙器有自己的[输入支持范围](../Packages/Fraxinus-RPCore/Documentation~/StandaloneAPVBaking.md)，并未覆盖原生 Lightmapper 的全部功能和降噪能力。

## APRTV

标准 URP 14 不提供本库的预计算传输系统。Fraxinus 增加[捕获与烘焙](../Packages/Fraxinus-RPCore/Editor/Lighting/APRT)、[Compute 重光照](../Packages/Fraxinus-RPCore/Runtime/Lighting/APRT/Resources/APRTRelight.compute)和 [URP 渲染图调度](../Packages/Fraxinus-URPExtensions/Runtime/Passes/APRTRelightPass.cs)。

静态几何可根据方向光和 HDR 天空重新计算光照，支持独立 Baking Set、双场景混合与按相机控制。几何/材质变化需要重烘焙，不支持动态遮挡和点光/聚光灯传输。**APRT 按已加载场景整体驻留探针单元，不使用 APV 的按相机流式加载。**

## Light Transport

配套的[射线追踪上下文](../Packages/Fraxinus-Light-Transport/Runtime/UnifiedRayTracing/RayTracingContext.cs)、[加速结构接口](../Packages/Fraxinus-Light-Transport/Runtime/UnifiedRayTracing/IRayTracingAccelStruct.cs)与[射线查询 Shader](../Packages/Fraxinus-Light-Transport/Runtime/UnifiedRayTracing/TraceRay.hlsl)提供 Compute BVH 构建/遍历、最近命中和遮挡查询。该库独立于基线 Core 14 包，为独立烘焙器提供 Compute 后端。

它可用于无需硬件光追的自定义光照或几何查询；目前 C# 接口为 internal，直接集成需要程序集访问权限。[URP Global 的配置](../Packages/Fraxinus-URPGlobal/Runtime/ShaderConfig.cs)让 C# 与生成的 HLSL 使用一致的平台灯光数量限制，它负责配置一致性，不是独立的画质功能。

## 适用项目

需要这些控制能力和工作流、并愿意在 Unity 2022.3 上维护自定义渲染栈的项目，更能受益于 Fraxinus。原版行为与扩展入口已经满足需求时，标准 URP 14 的维护更简单。功能覆盖、渲染开销与最终画质应分别评估，本文不宣称所有场景都有更高帧率或更好画质。

[urp14-graph]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.universal/Runtime/UniversalRenderPipeline.cs#L304
[core14-graph]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/RenderGraph.cs#L613
[urp14-pass]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.universal/Runtime/Passes/ScriptableRenderPass.cs#L675
[urp14-shadows]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.universal/Runtime/Passes/MainLightShadowCasterPass.cs
[urp14-asset]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.universal/Runtime/Data/UniversalRenderPipelineAsset.cs#L353
[core14-baker]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.core/Editor/Lighting/ProbeVolume/ProbeGIBaking.cs#L1992
[urp14-gi]: https://github.com/Unity-Technologies/Graphics/blob/03ca85dffdde4b7bc1d6870074e6f5ff9f0352a3/Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl#L132
