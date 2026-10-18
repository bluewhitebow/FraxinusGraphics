# Fraxinus Light Transport

[English](README.md) | [简体中文](README.zh-CN.md)

版本 `1.0.1` · Unity `2022.3` · 包 ID `com.fraxinus.rendering.light-transport`

提供 GPU 射线查询、加速结构和采样工具，作为光照计算与几何查询的基础库。
Unity 2022 使用 Compute Shader 构建和遍历 BVH，不要求 DXR 或硬件光追支持。

- **加速结构**：构建网格 BLAS 与实例 TLAS，支持实例增删、变换更新、掩码和面剔除。
- **射线查询**：最近命中返回距离、实例、三角形及重心坐标；任意命中用于遮挡查询。
- **GPU 调度**：支持直接与间接 Dispatch，统一绑定加速结构、参数和输入输出缓冲区。
- **采样工具**：包含 Sobol、蓝噪声与准随机序列，供自定义积分和采样任务复用。

可独立于 Core / URP 使用；C# 接口目前为 `internal`，集成时需要处理程序集访问。
探针烘焙的场景提取、光照积分与数据发布由配套
[Fraxinus Core](../Fraxinus-RPCore/README.zh-CN.md) 实现。

源码入口：[射线上下文](Runtime/UnifiedRayTracing/RayTracingContext.cs)、
[实例与加速结构](Runtime/UnifiedRayTracing/IRayTracingAccelStruct.cs)、
[射线查询](Runtime/UnifiedRayTracing/TraceRay.hlsl)、
[Dispatch 接口](Runtime/UnifiedRayTracing/IRayTracingShader.cs)、[采样库](Runtime/Sampling)。

验证记录：[Compute GPU Smoke](Tests~/GpuSmoke/README.md)。

许可：[Unity Companion License](LICENSE.md)；第三方组件见[许可声明](THIRD%20PARTY%20NOTICES.md)。
