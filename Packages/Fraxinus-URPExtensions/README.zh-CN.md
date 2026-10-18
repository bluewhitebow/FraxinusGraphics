# Fraxinus URP Extensions

[English](README.md) | [简体中文](README.zh-CN.md)

[仓库首页](../../README.zh-CN.md) · [使用指南](../../docs/Usage.zh-CN.md)

面向 Unity 2022.3 的 URP 定制分支，提供 RenderGraph 渲染流程、缓存阴影、外部绘制后端接口及升采样扩展。
包版本 **14.0.99**，包 ID 为 `com.unity.render-pipelines.universal`，与配套 Fraxinus Core 和 Config 共同使用。

- **RenderGraph 渲染流程**：通过类型化 Pass 和显式资源依赖组织渲染，接入 Unity 2022 的 Native Render Pass 桥接；
  原生 Pass 合并是否启用取决于 Renderer、相机及设备条件。
- **缓存级联阴影**：按相机缓存主光 CSM，提供可缓存级联范围、更新策略和手动失效接口，
  用于控制静态级联的重复渲染。
- **HZB 与外部绘制接口**：提供历史深度金字塔的 CPU / GPU 快照及当前帧 HZB 路径，
  并开放相机、深度 / 法线和阴影阶段的绘制接入，供外部实例或地形后端使用。
- **STP / SGSR 升采样**：接入 STP、SGSR1 空间升采样和 SGSR2 时域升采样，
  包含相机抖动、运动矢量及历史缓冲的相关调度。
- **多种渲染路径**：提供 Forward、Forward+、Deferred 与 2D Renderer，保留相机堆叠、后处理和 ShaderGraph 工作流。
- **APV / APRT 接入**：连接 Core 的探针后端，负责相机与灯光输入、资源依赖、材质采样和调试显示；
  APRT 在 Universal Renderer 中内置调度，支持方向光 / 天空重光照及烘焙光照场景。

本分支仅运行 RenderGraph。自定义 Pass 必须实现 `RecordRenderGraph(RenderGraph, ContextContainer)`；
仅实现旧 `Execute` 的扩展需要迁移。缓存 CSM 不会自动跟踪所有动态投影物或灯光变化，
需要时调用 `UniversalAdditionalCameraData.SetCachedCSMDirty()` 刷新。HZB 调用方需检查快照和能力查询结果。
DLSS / MetalFX 选项当前回退为 Linear；Unity 2022 下 Unity 原生 GPU Resident Drawer 后端不可用。

相关入口：[RenderGraph 示例](Samples~/URPRenderGraphSamples)、[APRT 使用说明](Documentation~/APRT.md)、
[缓存 CSM](Runtime/Extensions/CachedCSM/CachedCascadeShadowMap.cs)、[HZB 接口](Runtime/UniversalRenderPipelineBRG.cs)。

配套包：[Fraxinus-RPCore](../Fraxinus-RPCore/README.zh-CN.md)、[Fraxinus-URPGlobal](../Fraxinus-URPGlobal/README.zh-CN.md)。

本包为独立维护的非官方分支。Unity 衍生代码遵循 [Unity Companion License](Unity%20Companion%20License.md)，
第三方组件遵循各自条款；详见 [许可说明](LICENSE.md) 和 [第三方声明](Third%20Party%20Notices.md)。
