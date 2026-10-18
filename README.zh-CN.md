# FraxinusGraphics

[English](README.md) | [简体中文](README.zh-CN.md)

FraxinusGraphics 为 Unity 2022.3 提供 RenderGraph 渲染、缓存级联阴影、空间与时域升采样、外部绘制接口和 GPU 探针烘焙。四个配套包将帧渲染、光照工具与 Compute 射线查询整合在同一个源码仓库中。

[使用指南](docs/Usage.zh-CN.md) · [四个包的分工](#四个包的分工) · [渲染效果](#渲染效果)

## 主要特点

### RenderGraph 渲染架构

Raster、Compute 和 Unsafe Pass 显式声明资源与依赖，由渲染图管理资源生命周期，并通过 Native Render Pass 桥接适配 Unity 2022。满足 Renderer 和设备条件时可合并渲染通道。自定义效果与管线使用同一套渲染图流程，可从 [RenderGraph 示例](Packages/Fraxinus-URPExtensions/Samples~/URPRenderGraphSamples) 开始扩展。

### 缓存级联阴影

按相机复用符合条件的主光源阴影级联，减少未变化级联的重复绘制。可控制从哪一级开始缓存、每帧刷新工作量，并在场景变化时主动使缓存失效。[Cached CSM 实现](Packages/Fraxinus-URPExtensions/Runtime/Extensions/CachedCSM/CachedCascadeShadowMap.cs)。

### STP 与 SGSR 升采样

提供 STP、SGSR2 时域重建和 SGSR1 空间升采样，管线负责时域路径所需的相机抖动、运动矢量与历史缓冲调度。通过 URP Asset 的 Render Scale 和 Upscaling Filter 调整内部渲染分辨率与重建方式，[使用指南列出了启用条件](docs/Usage.zh-CN.md#主要功能怎么用)。

### HZB 与外部绘制接入

提供供 CPU、GPU 使用的历史深度金字塔快照，以及当前帧 HZB 路径。相机、深度/法线和阴影阶段的绘制挂钩，让外部实例化或地形渲染器接入管线。这些是[供渲染系统使用的接口](Packages/Fraxinus-URPExtensions/Runtime/UniversalRenderPipelineBRG.cs)，具体绘制或剔除后端由接入方提供。

### 独立 APV 烘焙

通过 Compute 光线追踪烘焙自适应探针光照，无需 DXR。流程包含自适应布点、天空遮挡、Rendering Layers、静态材质自发光与漫反射反弹，并提供 Baking Set、光照场景和 APV 流式加载来管理场景数据。烘焙在编辑器中分步执行，可查看进度和取消。[支持范围与烘焙说明](Packages/Fraxinus-RPCore/Documentation~/StandaloneAPVBaking.md)。

### 可重光照的探针体积

APRTV 预计算静态几何传输，让方向光与 HDR 天空在运行时更新探针光照。支持独立加载多个 Baking Set、两套烘焙光照状态之间的混合、按相机生效的 Volume 参数和探针调试视图。[APRT 使用说明](Packages/Fraxinus-RPCore/Documentation~/APRT.md)。

### Compute 射线查询

Light Transport 提供网格/实例加速结构、最近命中与遮挡查询、GPU 调度和采样序列，可用于自定义光照计算。Compute 后端无需硬件光追；直接调用其 C# 接口目前需要程序集访问权限，因为这些接口仍是 internal。[底层库接口](Packages/Fraxinus-Light-Transport/README.zh-CN.md)。

### 渲染工作流与调试工具

支持 Forward、Forward+、Deferred 和独立的 2D Renderer，并保留适用路径下的 ShaderGraph、后处理和相机堆叠。RTHandle、Blitter 与 Volume 提供共享渲染工具；Render Graph Viewer 和 Rendering Debugger 用于检查 Pass、资源与光照状态，方便开发和排查扩展效果。

## 四个包的分工

| 包 | 版本 | 职责 |
| --- | --- | --- |
| [Fraxinus-RPCore](Packages/Fraxinus-RPCore/README.zh-CN.md) | `14.0.99` | 共享渲染基础设施、探针数据与烘焙流程。 |
| [Fraxinus-URPExtensions](Packages/Fraxinus-URPExtensions/README.zh-CN.md) | `14.0.99` | 帧渲染、阴影、升采样与外部渲染器集成。 |
| [Fraxinus-URPGlobal](Packages/Fraxinus-URPGlobal/README.zh-CN.md) | `14.0.99` | 统一 C# 与生成 HLSL 的平台灯光数量配置。 |
| [Fraxinus-Light-Transport](Packages/Fraxinus-Light-Transport/README.zh-CN.md) | `1.0.1` | GPU 加速结构、射线查询与采样。 |

## 开始使用

1. 从同一份仓库引用四个本地包，[使用指南](docs/Usage.zh-CN.md#在项目中使用) 提供了相对路径的 `manifest.json` 配置。
2. 创建或选择 URP Asset，在 **Project Settings > Graphics** 中指定，并检查当前 **Quality** 级别的覆盖设置。
3. 在 **Lighting** 开启缓存阴影，在 **Quality** 选择升采样，或在当前 Renderer 中添加 RenderGraph 功能。探针光照通过 **Light Probe System** 单独选择。

指南包含[各功能的操作入口](docs/Usage.zh-CN.md#主要功能怎么用)和 [APV/APRT 烘焙步骤](docs/Usage.zh-CN.md#探针光照)。使用这些内置流程时，URP Global 和 Light Transport 不需要额外添加场景组件。

## 渲染效果

![APRTV 间接光关闭与开启对比](docs/images/aprtv-gi-comparison.png)

同一机位、同一灯光下的 APRTV 间接光开关对比。[间接光响应与探针可视化](docs/Screenshots.md)。

图片来自独立的 Sponza Demo；本仓库提供渲染包源码。

## 兼容与边界

自定义 Pass 使用 `RecordRenderGraph`，仅实现 `Execute` 的旧扩展需要迁移。缓存阴影未追踪到的场景变化需要主动刷新；时域升采样有相机和设备条件。APRT 的几何或材质变化需要重烘焙，当前不处理动态遮挡。具体限制见[使用指南](docs/Usage.zh-CN.md)和各包文档。

这是独立维护的 Unity 分支。Unity 衍生代码遵循 [Unity Companion License](Packages/Fraxinus-RPCore/LICENSE.md)，各包保留所属组件的第三方声明。
