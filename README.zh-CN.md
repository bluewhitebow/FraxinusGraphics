# FraxinusGraphics

[English](README.md) | [简体中文](README.zh-CN.md)

面向 Unity 2022.3 的渲染包套件，提供 RenderGraph、缓存阴影、升采样和探针光照。本仓库包含四个配套包的源码，未包含下图中的 Sponza Demo 工程。

![APRTV 间接光关闭与开启对比](docs/images/aprtv-gi-comparison.png)

同一机位、同一灯光下的 APRTV 间接光开关对比。[间接光响应与探针可视化](docs/Screenshots.md)。

| 包 | 版本 | 主要特点 |
| --- | --- | --- |
| [Fraxinus-RPCore](Packages/Fraxinus-RPCore/README.zh-CN.md) | `14.0.99` | RenderGraph、资源管理、渲染工具、探针光照与独立 APV 烘焙。 |
| [Fraxinus-URPExtensions](Packages/Fraxinus-URPExtensions/README.zh-CN.md) | `14.0.99` | RenderGraph 渲染、缓存级联阴影、HZB 与外部绘制接口、STP / SGSR 升采样及探针光照集成。 |
| [Fraxinus-URPGlobal](Packages/Fraxinus-URPGlobal/README.zh-CN.md) | `14.0.99` | URP 共享配置层，统一 C# 与 HLSL 中各平台的可见灯光数量上限。 |
| [Fraxinus-Light-Transport](Packages/Fraxinus-Light-Transport/README.zh-CN.md) | `1.0.1` | 基于 Compute Shader 的 GPU 射线查询、加速结构与采样工具，用于光照计算和几何查询。 |

## 在项目中使用

将本仓库与 Unity 工程放在同一级目录：

```text
workspace/
├─ FraxinusGraphics/Packages/
└─ MyUnityProject/Packages/manifest.json
```

在工程的 `Packages/manifest.json` → `dependencies` 中添加或替换以下四项，保留工程的其他依赖：

```json
{
  "com.unity.render-pipelines.core": "file:../../FraxinusGraphics/Packages/Fraxinus-RPCore",
  "com.unity.render-pipelines.universal": "file:../../FraxinusGraphics/Packages/Fraxinus-URPExtensions",
  "com.unity.render-pipelines.universal-config": "file:../../FraxinusGraphics/Packages/Fraxinus-URPGlobal",
  "com.fraxinus.rendering.light-transport": "file:../../FraxinusGraphics/Packages/Fraxinus-Light-Transport"
}
```

路径相对于工程的 `Packages` 目录。四个包应引用同一份仓库源码。Core、URP 和 Config 沿用 Unity 包 ID，会替换对应包，请保留这些 ID；其余声明的依赖由 Unity 解析。

1. 用 **Unity 2022.3** 打开工程，等待包导入完成。
2. 通过 **Assets > Create > Rendering > URP Asset (with Universal Renderer)** 创建管线资产，也可以使用已有 URP Asset。
3. 在 **Edit > Project Settings > Graphics > Scriptable Render Pipeline Settings** 指定该资产。同时检查 **Quality** 当前质量级别的 **Render Pipeline Asset**，使用同一个资产或继承 Graphics 设置。
4. 从管线资产的 **Renderer List** 打开实际使用的 Renderer。通过 **Rendering Path** 选择 Forward、Forward+ 或 Deferred；自定义渲染功能也添加在这个 Renderer 上。

URP Global 提供共享配置，Light Transport 提供 Compute 光线追踪后端；使用内置流程时，这两个包不需要额外添加场景组件。

## 主要功能怎么用

| 功能 | 使用入口 |
| --- | --- |
| 缓存级联阴影 | URP Asset → **Lighting**：将 **Main Light** 设为 **Per Pixel**，开启 **Cast Shadows** 和 **Cached Main Light**，设置 **Min Static Cascade** 与 **Cached Quality**。投影物体变化、需要刷新缓存时，对相应相机调用 `UniversalAdditionalCameraData.SetCachedCSMDirty()`。 |
| STP / SGSR 升采样 | URP Asset → **Quality**：设置 **Render Scale**（例如 `0.75`），再选择 **Upscaling Filter**。STP 的显示名称为 **Spatial-Temporal Post-Processing**，SGSR 为 **Snapdragon Game Super Resolution 1.0 / 2.0**。时域模式建议先使用单个 Base 相机，开启后处理并关闭 MSAA。 |
| RenderGraph 扩展 | **Window > Package Manager > Fraxinus URP Extensions > Samples > URP RenderGraph Samples > Import**。导入后，在 Renderer 的 **Add Renderer Feature** 中添加示例功能。这些是代码示例，部分需要指定材质，部分需在 Frame Debugger 中观察执行结果。 |
| HZB / 外部绘制 | 通过 [HZB 与绘制接口](Packages/Fraxinus-URPExtensions/Runtime/UniversalRenderPipelineBRG.cs) 接入；使用深度数据前检查能力查询结果与快照是否可用。 |
| 共享 Shader 配置 | 修改 [ShaderConfig.cs](Packages/Fraxinus-URPGlobal/Runtime/ShaderConfig.cs) 后，运行 **Edit > Rendering > Generate Shader Includes** 更新 HLSL。 |

自定义 Pass 需要实现 `RecordRenderGraph(RenderGraph, ContextContainer)`，仅实现 `Execute` 的旧扩展需要迁移。STP 还需要 Compute 和运动矢量支持，并关闭动态分辨率与相机堆叠；DLSS / MetalFX 当前回退到 Linear，Unity 2022 不提供原生 GPU Resident Drawer 后端。

## 探针光照

在 **URP Asset > Lighting > Light Probe System** 中选择一种后端：

| 后端 | 场景组件 | 烘焙按钮 |
| --- | --- | --- |
| **Adaptive Probe Volumes** | **GameObject > Light > Adaptive Probe Volume** | **Bake Probe Volumes** |
| **Adaptive PRT Volumes (APRTV)** | **GameObject > Light > Adaptive PRT Volume** | **Bake APRT** |

1. 保存场景，给参与烘焙的几何体开启 **Contribute GI**，添加对应 Volume 并覆盖需要光照的区域。APV 使用 Baked/Mixed 灯光，APRT 使用方向光与天空。建议先用小区域验证；APRT 的 **Adaptive** 布局支持自适应探针布置。
2. 打开 **Window > Rendering > Lighting**，选择 **Adaptive Probe Volumes** 或 **Adaptive Precompute Radiance Transfer** 页。单场景选 **Single Scene**；多场景创建 **Baking Set**，加载并保存集合中的全部场景。
3. 调整探针间距和范围，点击对应烘焙按钮，完成后保存场景。APRT Volume 的 Inspector 中也有 **Bake APRT**。**Generate Lighting** 不会启动独立 APV 烘焙。

要让 APRT 跟随当前灯光变化，将 Baking Set 的 **Relighting > Lighting Source** 设为 **Live Scene Lighting**。在 Global Volume Profile 中添加 **Lighting > APRTV Options**，勾选需要修改的参数覆盖。**GI Intensity** 控制整体贡献；**Sky Intensity** 默认是零，需要调高才能启用场景 Skybox 的贡献。之后修改方向光或天空可重新计算已有传输数据，几何或材质变化则需要重烘焙。

要保存多套 APRT 光照状态，在 URP Asset 开启 **Enable Lighting Scenarios** 和 **Enable Lighting Scenario Blending**。逐个烘焙 Active Scenario，后续状态使用 **Probe Positions > Don't Recalculate** 复用探针布局，通过 **Blend Target / Blend Factor** 预览混合。场景状态烘焙成功后会选中 **Baked Lighting Scenarios**；需要跟随当前灯光时，切回 **Live Scene Lighting**。

APRT 不处理动态遮挡，也不计算点光和聚光灯的传输。已加载场景的探针单元会整体驻留，应按场景规模规划 Baking Set。APV 的独立 Compute 烘焙不要求 DXR。

## 详细文档

- [APRT 配置、场景混合与调试视图](Packages/Fraxinus-RPCore/Documentation~/APRT.md)
- [APRT 与 URP 的集成](Packages/Fraxinus-URPExtensions/Documentation~/APRT.md)
- [独立 APV 烘焙与支持范围](Packages/Fraxinus-RPCore/Documentation~/StandaloneAPVBaking.md)
- [Light Transport 接口与限制](Packages/Fraxinus-Light-Transport/README.zh-CN.md)
- [渲染效果截图](docs/Screenshots.md)

Unity 衍生代码遵循 [Unity Companion License](Packages/Fraxinus-RPCore/LICENSE.md)。各组件的授权信息见所属包的许可证与第三方声明。
