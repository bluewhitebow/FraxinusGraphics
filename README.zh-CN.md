# FraxinusGraphics

[English](README.md) | [简体中文](README.zh-CN.md)

FraxinusGraphics 为 Unity 2022.3 提供 RenderGraph 渲染、缓存级联阴影、空间与时域升采样、外部绘制接口和 GPU 探针烘焙。四个配套包将帧渲染、光照工具与 Compute 射线查询整合在同一个源码仓库中。

[为什么选择 Fraxinus](#为什么不直接使用标准-urp-14) · [使用指南](docs/Usage.zh-CN.md) · [源码对照](docs/URP14Comparison.zh-CN.md) · [渲染效果](#渲染效果)

## 为什么不直接使用标准 URP 14？

Fraxinus 面向继续使用 **Unity 2022.3**，同时需要较新 RenderGraph 工作流、可控阴影更新、时域升采样、外部渲染器接入和更完整探针光照流程的项目。我们将较新的渲染基础设施适配到这一引擎版本，并在四个包之间补齐烘焙与运行时集成。

升级价值在于更细的渲染控制和更多可用流程：符合条件的阴影级联可以复用，降低渲染分辨率后可以选择 STP/SGSR 重建，APV 可以在这套 URP 中完成烘焙与采样，外部渲染器可以共享深度数据并接入绘制阶段。项目是否受益，取决于实际使用的能力。

## 相较 URP 14，具体改了什么？

| 方向 | 标准 URP 14 | Fraxinus 的改造与目的 |
| --- | --- | --- |
| RenderGraph | 已有实验性渲染图代码，与传统 `Execute` 路径并存，图执行受开关限制。 | 将较新的 Raster / Compute / Unsafe Pass 模型作为管线执行路径，为 Unity 2022 桥接 Native Pass Compiler，让自定义 Pass 使用统一的资源与依赖声明流程。[源码依据](docs/URP14Comparison.zh-CN.md#rendergraph)。 |
| 主光源阴影 | 绘制有效阴影级联，没有本库这套按相机复用 CSM 的策略。 | 增加持久化级联缓存、选择性刷新和手动失效控制，复用符合条件且未变化的阴影级联。[源码依据](docs/URP14Comparison.zh-CN.md#cached-csm)。 |
| 升采样 | 已有双线性/邻近过滤、FSR 1，也已有 TAA。 | 增加 STP、SGSR1、SGSR2，接入时域路径的抖动、运动矢量与历史调度，为降低内部渲染分辨率提供更多重建选择。[源码依据](docs/URP14Comparison.zh-CN.md#upscaling)。 |
| HZB 与自定义渲染器 | 提供 Renderer Feature 和自定义 Pass 扩展。 | 增加 CPU/GPU 深度金字塔快照，以及相机、深度/法线、阴影绘制接口，便于接入外部实例化或地形后端。[源码依据](docs/URP14Comparison.zh-CN.md#hzb-and-external-drawing)。 |
| APV 烘焙与渲染 | Core 已有 APV 基础设施，但 URP 14 没有完整接通 APV 渲染流程。 | 将 APV 接入 URP 材质，提供独立 Compute 烘焙、自适应布点、天空遮挡、光照场景和 APV 流式加载；探针烘焙无需 DXR。[源码依据](docs/URP14Comparison.zh-CN.md#apv)。 |
| 可重光照体积 | 未提供本库的 APRTV 传输与重光照系统。 | 为烘焙的静态几何提供方向光/HDR 天空重光照、多 Baking Set、双场景混合和按相机生效的 Volume 控制。[源码依据](docs/URP14Comparison.zh-CN.md#aprtv)。 |
| GPU 射线查询 | Core 14 不包含本库配套的 Compute 射线查询库。 | Light Transport 提供 BVH、命中/遮挡查询与采样，服务烘焙器及自定义集成；直接调用 C# internal 接口仍需程序集访问权限。[源码依据](docs/URP14Comparison.zh-CN.md#light-transport)。 |

Forward、Forward+、Deferred、2D、ShaderGraph、相机堆叠，以及 RTHandle、Volume、Blitter 都是 URP/Core 14 这一代已有的基础能力。我们保留这些工作流，并与较新的渲染图和调试工具配套使用，不将它们当作新增原创功能。

## 四个包的分工

| 包 | 版本 | 职责 |
| --- | --- | --- |
| [Fraxinus-RPCore](Packages/Fraxinus-RPCore/README.zh-CN.md) | `14.0.99` | 共享渲染基础设施、探针数据与烘焙流程。 |
| [Fraxinus-URPExtensions](Packages/Fraxinus-URPExtensions/README.zh-CN.md) | `14.0.99` | 帧渲染、阴影、升采样与外部渲染器集成。 |
| [Fraxinus-URPGlobal](Packages/Fraxinus-URPGlobal/README.zh-CN.md) | `14.0.99` | 统一 C# 与生成 HLSL 的平台灯光数量配置。 |
| [Fraxinus-Light-Transport](Packages/Fraxinus-Light-Transport/README.zh-CN.md) | `1.0.1` | GPU 加速结构、射线查询与采样。 |

Core 承担[渲染图 API](Packages/Fraxinus-RPCore/Runtime/RenderGraph/RenderGraph.cs)、[Unity 2022 编译器桥接](Packages/Fraxinus-RPCore/Runtime/RenderGraph/Compiler/NativePassCompiler.2022.cs)和[独立 APV 烘焙器](Packages/Fraxinus-RPCore/Editor/Lighting/APV/ProbeGIBaking.Standalone.cs)；URP 将它们接到相机、渲染 Pass 和[材质光照](Packages/Fraxinus-URPExtensions/ShaderLibrary/GlobalIllumination.hlsl)，Light Transport 提供射线后端，Global 保持 C# 与 HLSL 配置一致。这些改造跨越多个包，因此需要配套维护和使用。

## 开始使用

1. 从同一份仓库引用四个本地包，[使用指南](docs/Usage.zh-CN.md#在项目中使用) 提供了相对路径的 `manifest.json` 配置。
2. 创建或选择 URP Asset，在 **Project Settings > Graphics** 中指定，并检查当前 **Quality** 级别的覆盖设置。
3. 在 **Lighting** 开启缓存阴影，在 **Quality** 选择升采样，或在当前 Renderer 中添加 RenderGraph 功能。探针光照通过 **Light Probe System** 单独选择。

指南包含[各功能的操作入口](docs/Usage.zh-CN.md#主要功能怎么用)和 [APV/APRT 烘焙步骤](docs/Usage.zh-CN.md#探针光照)。使用这些内置流程时，URP Global 和 Light Transport 不需要额外添加场景组件。

## 渲染效果

![APRTV 间接光关闭与开启对比](docs/images/aprtv-gi-comparison.png)

同一机位、同一灯光下的 APRTV 间接光开关对比。[间接光响应与探针可视化](docs/Screenshots.md)。

![Probe Volume 调试视图，展示探针位置与光照](docs/images/aprtv-balcony-probes.png)

Probe Volume 调试视图（APRTV 后端）。球体显示探针位置及光照响应；场景共加载了 4,563 个探针。

图片来自独立的 Sponza Demo；本仓库提供渲染包源码。

## 兼容与边界

自定义 Pass 使用 `RecordRenderGraph`，仅实现 `Execute` 的旧扩展需要迁移。缓存阴影未追踪到的场景变化需要主动刷新；时域升采样有相机和设备条件。APRT 的几何或材质变化需要重烘焙，当前不处理动态遮挡。这些能力不代表每个场景都自动获得更高帧率或更好画质；标准 URP 14 已满足需求的项目，可以避免自定义管线的维护成本。具体边界见[源码对照](docs/URP14Comparison.zh-CN.md)与[使用指南](docs/Usage.zh-CN.md)。

这是独立维护的 Unity 分支。Unity 衍生代码遵循 [Unity Companion License](Packages/Fraxinus-RPCore/LICENSE.md)，各包保留所属组件的第三方声明。
