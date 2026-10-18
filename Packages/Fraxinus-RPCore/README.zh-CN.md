# Fraxinus RPCore

[English](README.md) | [简体中文](README.zh-CN.md)

[仓库首页](../../README.zh-CN.md) · [使用指南](../../docs/Usage.zh-CN.md)

面向 Unity 2022.3 的渲染基础库，为配套管线提供 RenderGraph、资源管理、探针光照和编辑器工具。

版本：`14.0.99` · 包 ID：`com.unity.render-pipelines.core`

## 主要特点

- RenderGraph：支持 Raster / Compute / Unsafe Pass、资源依赖管理和 Native Render Pass 合并，并适配 Unity 2022 执行接口。
- 通用渲染工具：提供 RTHandle、Blitter、Volume、ShaderLibrary 和 Rendering Debugger，统一资源、画面参数与调试流程。
- 时空升采样：提供 STP 计算资源和接口，由配套 URP 管理运动矢量、历史帧和后处理调度。
- 独立 APV 烘焙：使用 Compute 光线追踪，支持自适应布点、天空遮蔽、Rendering Layers、静态材质与漫反射反弹，无需 DXR。
- 探针数据管理：共享 Cell / Brick 布局和 Baking Set 工作流，支持光照场景；APV 保留流式加载，APRT 按已加载场景整批加载。
- APRT 重光照：预计算传输响应方向光与 HDR 天空变化，支持多 Baking Set 和双光照场景混合。

## 配套与边界

与 [URP](../Fraxinus-URPExtensions/README.zh-CN.md)、[Config](../Fraxinus-URPGlobal/README.zh-CN.md)
及 [Light Transport](../Fraxinus-Light-Transport/README.zh-CN.md) 配套使用。
自定义渲染代码需适配本分支的 RenderGraph API；Unity 2022 下原生 GPU Resident Drawer 不可用。

APV 烘焙面向静态几何；APRT 几何与材质变化需重烘，当前不处理动态遮挡或运行时多反弹反馈。

## 文档

- [RenderGraph 实现](Runtime/RenderGraph/RenderGraph.cs)
- [独立 APV 烘焙](Documentation~/StandaloneAPVBaking.md)
- [APRT 工作流](Documentation~/APRT.md)
- [验证记录](Tests~/Unity2022APV/README.md)

## 许可

Unity 衍生代码遵循 [Unity Companion License](LICENSE.md)，第三方组件保留各自许可。
