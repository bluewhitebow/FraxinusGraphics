# FraxinusGraphics

[English](README.md) | [简体中文](README.zh-CN.md)

FraxinusGraphics 是面向 **Unity 2022.3** 的自定义渲染套件。四个配套包共同提供 RenderGraph 渲染、缓存级联阴影、STP/SGSR 升采样、外部绘制接口和 GPU 探针烘焙。

[示例工程](https://github.com/bluewhitebow/FraxinusGraphicsDemo/blob/main/README.zh-CN.md#打开工程) · [为什么选择 Fraxinus](#为什么不直接使用标准-urp-14) · [使用指南](docs/Usage.zh-CN.md) · [源码对照](docs/URP14Comparison.zh-CN.md) · [渲染效果](#渲染效果)

## 为什么不直接使用标准 URP 14？

目标是在继续使用 Unity 2022.3 的前提下，引入较新的渲染基础设施和更细的控制能力：复用静态阴影级联、重建低分辨率画面、接入外部场景渲染器，以及直接在 URP 中烘焙探针光照。改造结合了上游功能回移、引擎适配和 Fraxinus 扩展，覆盖 Core、URP 与射线后端。

## 相较 URP 14，具体改了什么？

| 方向 | 标准 URP 14 | Fraxinus |
| --- | --- | --- |
| [RenderGraph](docs/URP14Comparison.zh-CN.md#rendergraph) | 实验性渲染图路径；公开自定义 Pass 使用 `Execute`。 | 全管线使用 Raster / Compute / Unsafe Pass，为 Unity 2022 桥接 Native Pass Compiler。 |
| [缓存阴影](docs/URP14Comparison.zh-CN.md#cached-csm) | 绘制有效的主光源阴影级联。 | 按相机缓存 CSM，支持选择性刷新与手动失效，复用符合条件的级联。 |
| [升采样](docs/URP14Comparison.zh-CN.md#upscaling) | 双线性/邻近过滤、FSR 1，另有 TAA。 | 增加 STP、SGSR1、SGSR2，为时域重建接入抖动、运动矢量和历史调度。 |
| [HZB 与外部绘制](docs/URP14Comparison.zh-CN.md#hzb-and-external-drawing) | Renderer Feature 和自定义 Pass。 | 提供深度金字塔快照，以及相机、深度/法线和阴影接口，便于接入实例化或地形后端。 |
| [自适应探针体积（APV）](docs/URP14Comparison.zh-CN.md#apv) | Core 已有 APV 基础设施，URP 未完整接通渲染流程。 | 独立 Compute 烘焙与 URP 材质采样，支持自适应布点、天空遮挡、光照场景和流式加载，无需 DXR。 |
| [自适应 PRT 体积（APRTV）](docs/URP14Comparison.zh-CN.md#aprtv) | 无对应的预计算传输工作流。 | 为烘焙的静态几何提供方向光/HDR 天空重光照、多 Baking Set、双场景混合与按相机控制。 |
| [Compute 射线查询](docs/URP14Comparison.zh-CN.md#light-transport) | Core 14 无配套的 Compute 射线查询库。 | 提供 BVH 构建、命中/遮挡查询和采样，服务烘焙与自定义集成。 |

点击各项可查看实现与上游源码对照。Forward、Forward+、Deferred、2D、ShaderGraph、相机堆叠，以及 RTHandle、Volume、Blitter 等 URP/Core 原有能力继续保留。

## 四个包的分工

| 包 | 版本 | 职责 |
| --- | --- | --- |
| [Fraxinus-RPCore](Packages/Fraxinus-RPCore/README.zh-CN.md) | `14.0.99` | 共享渲染基础设施、探针数据与烘焙流程。 |
| [Fraxinus-URPExtensions](Packages/Fraxinus-URPExtensions/README.zh-CN.md) | `14.0.99` | 帧渲染、阴影、升采样与外部渲染器集成。 |
| [Fraxinus-URPGlobal](Packages/Fraxinus-URPGlobal/README.zh-CN.md) | `14.0.99` | 统一 C# 与生成 HLSL 的平台灯光数量配置。 |
| [Fraxinus-Light-Transport](Packages/Fraxinus-Light-Transport/README.zh-CN.md) | `1.0.1` | GPU 加速结构、射线查询与采样。 |

Core 承担[渲染图 API](Packages/Fraxinus-RPCore/Runtime/RenderGraph/RenderGraph.cs)、[Unity 2022 编译器桥接](Packages/Fraxinus-RPCore/Runtime/RenderGraph/Compiler/NativePassCompiler.2022.cs)和 [APV 烘焙器](Packages/Fraxinus-RPCore/Editor/Lighting/APV/ProbeGIBaking.Standalone.cs)；URP 将它们接到相机、Pass 和[材质光照](Packages/Fraxinus-URPExtensions/ShaderLibrary/GlobalIllumination.hlsl)。四个包应从同一份仓库配套引用，保持渲染、烘焙和 Shader 配置一致。

## 开始使用

建议先打开 [FraxinusGraphicsDemo](https://github.com/bluewhitebow/FraxinusGraphicsDemo/blob/main/README.zh-CN.md#打开工程)：使用 **Unity 2022.3.62f3** 运行已配置好的 Sponza 场景，体验重光照、探针调试、缓存阴影和升采样。Unity 会自动下载四个渲染包。

接入自己的项目时：

1. 按[使用指南](docs/Usage.zh-CN.md#在项目中使用)中的相对路径引用四个包。
2. 创建或选择 URP Asset，在 **Project Settings > Graphics** 中指定，并检查当前 **Quality** 级别的覆盖设置。
3. 在 **Lighting** 开启缓存阴影，在 **Quality** 选择升采样，或在当前 Renderer 中添加 RenderGraph 功能。探针光照通过 **Light Probe System** 单独选择。

具体见[功能入口](docs/Usage.zh-CN.md#主要功能怎么用)、[探针烘焙](docs/Usage.zh-CN.md#探针光照)与[常见配置问题](docs/Usage.zh-CN.md#常见配置问题)。APRTV 在详细文档、代码和烘焙按钮中使用简称 **APRT**。

## 渲染效果

![APRTV 间接光关闭与开启对比](docs/images/aprtv-gi-comparison.png)

同一机位、同一灯光，仅将 APRTV GI 强度从 0 切换到 1。[更多探针光照截图与采集条件](docs/Screenshots.md)。

![Probe Volume 调试视图，展示探针位置与光照](docs/images/aprtv-balcony-probes.png)

Probe Volume 调试视图（APRTV 后端）。球体显示探针位置及光照响应；场景共加载了 4,563 个探针。

可在 [FraxinusGraphicsDemo](https://github.com/bluewhitebow/FraxinusGraphicsDemo/blob/main/README.zh-CN.md) 中运行 Sponza 场景并体验这些功能。

## 兼容与边界

- 自定义 Pass 需实现 `RecordRenderGraph`；仅实现 `Execute` 的旧扩展需要迁移。
- 缓存阴影未追踪到的场景变化需要主动刷新；启用升采样前需检查对应的相机与设备条件。
- APRT 的几何/材质变化需要重烘焙，不处理动态遮挡，已加载场景的探针单元整体驻留；APV 支持流式加载。
- Light Transport 的 C# 接口目前为 `internal`，直接集成需要程序集访问权限。

配置方法见[使用指南](docs/Usage.zh-CN.md)，实现范围见[源码对照](docs/URP14Comparison.zh-CN.md)。

这是独立维护的 Unity 分支。Unity 衍生代码遵循 [Unity Companion License](Packages/Fraxinus-RPCore/LICENSE.md)，各包保留所属组件的第三方声明。
