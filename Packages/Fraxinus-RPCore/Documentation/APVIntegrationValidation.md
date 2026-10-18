# APV 接入与流式验证

本次收尾遵循 [ProbeVolumeStreamingArchitecture.md](ProbeVolumeStreamingArchitecture.md)：复用 Cell 生命周期、索引、流式调度、内存预算和场景混合，以 Cell 数据及其算法实现作为扩展点。没有新增一套管理器或调度系统。

## 当前调用关系

- URP 使用现有 `ProbeReferenceVolumeProvider` 初始化、更新和释放后端。
- `TProbeReferenceVolume` 保留共享的 Cell 与流式流程。
- `APVProbeReferenceVolume`、`APVCellData`、`APVProbeBrickPool` 和 `APVProbeVolumeBakingDataFormat` 提供 APV 实现。
- APV 专用烘焙编辑器直接使用该实现。
- 已移除 `Runtime/Compatibility/Unity2022ProbeReferenceVolume.cs`；项目内调用点已迁移。依赖旧 `ProbeReferenceVolume` 公共类型的外部代码需要迁移。

## 已修正的问题

1. 将生成的 HLSL 放到相应 APV C# 文件旁，并更新 include，使采样 Shader 读取的常量缓冲区与 C# 上传的名称一致。
2. 对齐磁盘读取与上传缓冲区的布局：目标坐标、L0/L1、共享数据、可选 L2、Probe Occlusion。L1 不再预留未加载的 L2 数据。
3. 混合场景的第二份数据跳过共享数据读取时，保留共享区域的地址空间，防止后续通道偏移。
4. CPU 上传路径沿用完整 staging buffer 偏移；关闭天空遮蔽时清除上传 Shader 的相关关键字。
5. 释放异步读取句柄，以及全部已打开的烘焙数据文件；允许缺少 L2 或 Probe Occlusion 的合法数据集安全释放。
6. 明确引用并注册内置 APV 后端及资源适配方法，使其具有静态可达路径。保留现有扩展发现机制。
7. 初始化后端前发布 Provider 上下文，使初始化期间的场景注册和数据解析能看到选定后端；初始化失败或清理异常时清空上下文。

## 验证

环境：Unity 2022.3.62f3，Windows，Direct3D 11，NVIDIA GeForce GTX 1650。

- 完整项目解决方案编译通过。
- `Tests/Editor/APV/APVIntegrationTests.cs` 的 10 项 EditMode 测试全部通过，无跳过：采样常量的 GPU 读取、真实异步磁盘读取到 GPU 纹理的 L1/L2 通道校验、共享数据跳过、可选数据释放及 Provider 初始化时序。
- 独立场景通过 Progressive CPU Lightmapper 烘焙，生成 4 个 Cell，并成功加载 4 个运行时 Cell。
- 保存场景与 Baking Set 引用，退出并重新启动 Unity 后，4 个 Cell 通过磁盘流式加载完成索引更新，并成功执行相机渲染。

测试包需要在验证工程的 `Packages/manifest.json` 中将 `com.unity.render-pipelines.core` 加入 `testables`。GPU 测试需要图形设备，不能使用 `-nographics`。场景验证工程及执行脚本保存在工作区 `.tmp/apv-unity`，没有改动业务场景。

## 验证范围

上述结果覆盖本次 APV 接入、数据上传与基础烘焙链路。没有执行 IL2CPP Player 构建、其他图形 API 或所有 Unity 6 烘焙特性的等价性验证。Unity 2022 的光传输、虚拟偏移和天空遮蔽继续使用现有适配实现；未来算法及动态 Relight 不属于本次实现范围。
