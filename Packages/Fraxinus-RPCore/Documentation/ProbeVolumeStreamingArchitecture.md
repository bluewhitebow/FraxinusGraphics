# 可流式光照探针体积系统 - 架构重构设计文档

## 当前实现边界（2026-09-22）

本系统统一采用 `Volume → Cell → Brick → Probe → 算法数据`。Probe/Brick 是共享空间约定，APV 和未来的体积 PRT 都应复用这套布局。下文保留原始设计背景；当前代码的扩展边界以本节为准，仅抽象 CellData 不足以隔离完整的烘焙和加载流程。

- `Runtime/Lighting/ProbeVolumeLayout.cs` 定义只读空间配置、三叉细分、Probe 间距与世界偏移。Editor 的细分、布点和 APV 烘焙共用它，旧 `ProbeVolumeProfileInfo` 已删除。
- `Editor/Lighting/ProbeVolume` 保留共享布局、场景、Baking Set、烘焙生命周期和编辑器宿主；`Editor/Lighting/APV` 实现 APV 光照、有效性、Virtual Offset、Rendering Layers 等算法行为。
- `ICellData<T>` 只要求场景数据、Brick 和 Probe 位置。Validity、Rendering Layer、Touchup、Offset 数据由 `APVCellData` 自己持有，其支持数据读取和探针调试绘制移入 APV Runtime。
- `IProbeVolumeBakingDataFormat` 接收 Baking Set、运行时体积、Cell 索引和加载模式。后端负责选择、读取和解码自己的资源；公共 Baking Set 不再固定读取 APV SH、可选 L2、Occlusion 或 APV 场景资源。
- Baking Set 只初始化当前后端的数据，保留其他后端的序列化数据。共享层继续提供 Cell 索引、临时缓冲及流式文件读取设施。

扩展一个体积 PRT 时，应提供自己的 Cell 数据、烘焙数据格式、Editor 烘焙实现及 Runtime GPU 资源/Shader 采样，直接复用共享布局。本次没有保留不可运行的 PRT 注册空壳，也没有实现 PRT 光传输烘焙。

这仍不是任意算法可直接运行的完整平台：Runtime 的 GPU 流式请求、部分初始化/着色参数及 URP 的光照接入仍有 APV 语义；当前 Provider 选择一个活动后端，不支持 APV 与 PRT 同时叠加。新增算法必须继续实现这些运行时边界，不能只新增 Editor 类型便宣称完成。

验证包含 Core Runtime/Editor/Editor.Tests 编译，以及 Unity 2022.3 EditMode 的 12 项布局回归和 4 项非 APV 格式边界测试。测试不等同于完整场景烘焙、GPU 流式和最终画面的端到端验收。

## 摘要

本文档设计了一套**基于 Unity APV 的极简可扩展流式光照探针体积系统**。核心思想是：**只需抽象 `CellData` 层**，即可支持 APV 及未来新算法。

- ✅ **极简设计** - 只抽象 `CellData` 层，其余逻辑完全复用
- ✅ **算法可扩展** - 通过 `ICellData` 接口支持不同算法
- ✅ **流式统一** - 一套流式系统服务多种算法
- ✅ **易于维护** - 最小化改动，保留现有代码

---

## 目录

1. [文件结构](#1-文件结构)
2. [设计原则](#2-设计原则)
3. [整体架构](#3-整体架构)
4. [核心抽象：ICellData](#4-核心抽象-icelldata)

---

## 1. 文件结构

### 1.1 现有文件位置

```
Packages/com.unity.render-pipelines.core/Runtime/Lighting/ProbeVolume/
├── ProbeReferenceVolume.cs                 # 核心管理器（复用）
├── ProbeReferenceVolume.Streaming.cs       # 流式系统（复用）
├── ProbeBrickPool.cs                       # APV 专用 SH 纹理池（复用）
├── ProbeBrickIndex.cs                      # APV 专用索引管理（复用）
├── ProbeVolume.cs                          # 配置组件（复用）
├── ProbeVolumeAsset.cs                     # 数据结构（复用）
└── ... (其他现有文件)
```

### 1.2 新增文件位置

**核心抽象层**（新增文件）:
```
Packages/com.unity.render-pipelines.core/Runtime/Lighting/ProbeVolume/
├── ICellData.cs                  # 新增：Cell 数据接口
└── IPerScenarioData.cs           # 新增：每场景数据接口（标记接口）
```

### 1.3 文件结构说明

| 文件 | 位置 | 说明 |
|------|------|------|
| `ICellData.cs` | `Runtime/Lighting/ProbeVolume/` | `ICellData` 和 `IPerScenarioData` 接口定义 |
| `APVCellData.cs` | `Runtime/Lighting/ProbeVolume/` (可选) | APV 算法的 `CellData` 实现（如需要可单独文件） |

### 1.4 迁移步骤

1. **创建接口文件** - 在 `Runtime/Lighting/ProbeVolume/` 目录下创建 `ICellData.cs`
2. **修改现有 Cell 类** - 将 `ProbeReferenceVolume.cs` 中的 `CellData` 改为 `ICellData` 接口
3. **实现 APVCellData** - 将现有 `CellData` 重命名为 `APVCellData`，实现 `ICellData` 接口
4. **修改 Cell 类** - 将 `Cell.data` 字段类型从 `CellData` 改为 `ICellData`

### 1.5 命名空间

```csharp
namespace UnityEngine.Rendering
{
    // ICellData 和 IPerScenarioData 放在 UnityEngine.Rendering 命名空间
    // 与现有的 ProbeReferenceVolume、ProbeBrickPool 等保持一致
}
```

---

## 2. 设计原则

### 1.1 核心思想

```
┌─────────────────────────────────────────────────┐
│          保留 Unity APV 的完整流式系统            │
│          只抽象 CellData 层支持不同算法           │
│          Cell 结构完全复用                       │
└─────────────────────────────────────────────────┘
```

**关键洞察：** 从 `ProbeReferenceVolume` 的实现来看，`Cell` 的结构已经很清晰：

```
Cell
├── desc (CellDesc)        - 元数据（位置、索引、Brick 数量等）→ 算法无关
├── data (CellData)        - 实际数据 → 需要抽象的部分 ←
├── poolInfo               - 运行时 GPU 资源分配 → 可复用
├── indexInfo              - 运行时索引管理 → 可复用
├── blendingInfo           - 运行时场景混合 → 可复用
└── streamingInfo          - 运行时流式状态 → 可复用
```

### 1.2 复用策略

| 现有组件 | 复用方式 | 修改范围 |
|---------|---------|---------|
| `ProbeReferenceVolume` | **完全复用** | 作为流式系统基类 |
| `Cell` | **完全复用** | 仅 `data` 字段改为接口类型 |
| `CellDesc` | **完全复用** | 无需修改 |
| `CellPoolInfo/CellIndexInfo/CellBlendingInfo/CellStreamingInfo` | **完全复用** | 无需修改 |
| `ProbeBrickPool` / `ProbeBrickIndex` | **APV 专用** | 作为 APV 的 GPU 资源管理实现 |
| 流式逻辑 | **完全复用** | 无需修改 |

### 1.3 扩展性设计

```
新增算法只需实现:
  1. ICellData 接口 - 定义数据结构和方法
  2. 具体的 CellData 类 - 如 APVCellData, FutureAlgorithmCellData
  3. 算法逻辑 - 烘焙和运行时更新
  
无需修改:
  - Cell 结构和生命周期管理
  - 流式管理系统
  - 内存预算系统
  - 场景混合系统
```

---

## 2. 整体架构

### 2.1 简化架构图

```
┌─────────────────────────────────────────────────────────────┐
│                    应用层                                    │
│  ┌─────────────────┐  ┌─────────────────┐                  │
│  │  ProbeVolume    │  │  编辑器工具      │                  │
│  │  (配置)         │  │  (烘焙/调试)     │                  │
│  └─────────────────┘  └─────────────────┘                  │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                   算法管理层                                  │
│  ┌─────────────────────────────────────────────────────┐   │
│  │           ProbeVolumeAlgorithmManager               │   │
│  │  - 管理当前算法类型                                   │   │
│  │  - 每帧 Update (动态算法需要 Relight)                │   │
│  │  - 收集 Shader 参数                                    │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                    流式层                                    │
│  ┌─────────────────────────────────────────────────────┐   │
│  │        ProbeVolumeStreamingManager                  │   │
│  │  ← 完全复用 ProbeReferenceVolume 的流式逻辑           │   │
│  │  - 管理所有 Cell (Dictionary<int, Cell>)             │   │
│  │  - 计算流式优先级                                     │   │
│  │  - 加载/卸载 Cell                                     │   │
│  │  - 内存预算管理                                       │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                    Cell 层                                   │
│  ┌─────────────────────────────────────────────────────┐   │
│  │              Cell (结构完全复用)                     │   │
│  │  - desc: CellDesc (元数据，算法无关)                 │   │
│  │  - data: ICellData (抽象的数据层) ← 核心抽象         │   │
│  │  - poolInfo/indexInfo/blendingInfo/streamingInfo    │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                    数据层 (核心抽象点)                        │
│  ┌───────────────┐  ┌───────────────┐                      │
│  │  APVCellData  │  │  未来算法      │                      │
│  │  (SH 系数)     │  │  (自定义)     │                      │
│  └───────────────┘  └───────────────┘                      │
└─────────────────────────────────────────────────────────────┘
```

### 2.2 运行时数据流

```
场景启动
    ↓
ProbeVolume.Initialize()
    ↓
AlgorithmManager.Initialize(algorithmType, asset)
    ├─ APV: 初始化 ProbeBrickPool + ProbeBrickIndex
    └─ 未来算法：初始化对应的 GPU 资源
    ↓
StreamingManager.Initialize()
    ↓
每帧 Update()
    ├─ StreamingManager.UpdateCellStreaming(camera)
    │   ├─ 计算 Cell 流式优先级
    │   ├─ 加载高优先级的 Cell → Cell.data.Load()
    │   └─ 卸载低优先级的 Cell → Cell.data.Unload()
    │
    ├─ AlgorithmManager.Update(camera)
    │   ├─ APV: 无操作
    │   └─ 未来算法：Relight() (如果需要动态更新)
    │
    └─ AlgorithmManager.BindShaderResources()
        └─ 遍历所有已加载 Cell，收集 Shader 参数
```

---

## 3. 核心抽象：ICellData

### 3.1 极简设计思想

**核心洞察：** 从 `ProbeReferenceVolume` 的实现来看，`Cell` 的结构已经很完整：

```csharp
// 现有 Cell 结构 (完全复用)
internal class Cell
{
    public CellDesc desc;         // 元数据 (算法无关)
    public CellData data;         // 数据 ← 只需抽象这一层
    public CellPoolInfo poolInfo; // 运行时 GPU 资源
    public CellIndexInfo indexInfo;
    public CellBlendingInfo blendingInfo;
    public CellStreamingInfo streamingInfo;
}
```

**设计决策：**
- `CellDesc`、`CellPoolInfo`、`CellIndexInfo`、`CellBlendingInfo`、`CellStreamingInfo` **完全复用**
- 只需将 `CellData` 改为接口 `ICellData`
- 不同算法实现各自的 `CellData` 类

### 3.2 ICellData 接口

```
┌────────────────────────────────────────────────────────┐
│                    ICellData                           │
│  ──────────────────────────────────────────────────    │
│  + CleanupPerScenarioData(data)                        │
│  + Cleanup(cleanScenarioList)                          │
└────────────────────────────────────────────────────────┘
                ↑
                │
        ┌───────┴───────┐
        │               │
┌──────────────┐  ┌──────────────┐
│ APVCellData  │  │  未来算法     │
│ ──────────── │  │  CellData    │
│ (复用现有)   │  │  (自定义)    │
└──────────────┘  └──────────────┘
```

**伪代码:**
```csharp
/// <summary>
/// 探针体积 Cell 数据接口
/// 不同算法（APV、未来算法等）实现各自的 CellData 类
/// 当前阶段仅定义 Cleanup 方法，其余方法后续设计
/// </summary>
interface ICellData
{
    /// <summary>清理每场景数据</summary>
    void CleanupPerScenarioData(IPerScenarioData data);
    
    /// <summary>清理所有数据</summary>
    void Cleanup(bool cleanScenarioList);
}

/// <summary>
/// 每场景数据接口（标记接口，具体字段由算法实现定义）
/// </summary>
interface IPerScenarioData
{
    // 空接口，具体字段由各算法的 PerScenarioData 实现定义
}
```

### 3.3 APVCellData 数据结构示例

```csharp
/// <summary>
/// APV 算法的 Cell 数据实现
/// 完全复用现有的 CellData 结构
/// </summary>
class APVCellData : ICellData
{
    // === 完全复用现有 CellData 结构 ===
    
    // 共享数据（所有场景通用）
    public NativeArray<byte> validityNeighMaskData;
    public NativeArray<ushort> skyOcclusionDataL0L1;
    public NativeArray<byte> skyShadingDirectionIndices;
    
    // 每场景数据
    public Dictionary<string, PerScenarioData> scenarios;
    public struct PerScenarioData : IPerScenarioData
    {
        public NativeArray<ushort> shL0L1RxData;
        public NativeArray<byte> shL1GL1RyData;
        public NativeArray<byte> shL1BL1RzData;
        public NativeArray<byte> shL2Data_0; // 可选
        public NativeArray<byte> shL2Data_1;
        public NativeArray<byte> shL2Data_2;
        public NativeArray<byte> shL2Data_3;
        public NativeArray<byte> probeOcclusion; // 可选
    }
    
    // 辅助数据
    public NativeArray<Vector3> probePositions;
    public NativeArray<Vector3> offsetVectors;
    public NativeArray<float> validity;
    public NativeArray<byte> layer;
    
    // Brick 数据
    public NativeArray<Brick> bricks;
    
    // === ICellData 实现 ===
    
    public void CleanupPerScenarioData(IPerScenarioData data)
    {
        // 与现有 CellData.CleanupPerScenarioData 对齐
    }
    
    public void Cleanup(bool cleanScenarioList)
    {
        // 与现有 CellData.Cleanup 对齐
    }
}
```

---

## 4. 文件结构

### 4.1 现有文件位置

```
Packages/com.unity.render-pipelines.core/Runtime/Lighting/ProbeVolume/
├── ProbeReferenceVolume.cs                 # 核心管理器（复用）
├── ProbeReferenceVolume.Streaming.cs       # 流式系统（复用）
├── ProbeBrickPool.cs                       # APV 专用 SH 纹理池（复用）
├── ProbeBrickIndex.cs                      # APV 专用索引管理（复用）
├── ProbeVolume.cs                          # 配置组件（复用）
├── ProbeVolumeAsset.cs                     # 数据结构（复用）
└── ... (其他现有文件)
```

### 4.2 新增文件位置

**核心抽象层**（新增文件）:
```
Packages/com.unity.render-pipelines.core/Runtime/Lighting/ProbeVolume/
├── ICellData.cs                  # 新增：Cell 数据接口
└── IPerScenarioData.cs           # 新增：每场景数据接口（标记接口）
```

### 4.3 文件结构说明

| 文件 | 位置 | 说明 |
|------|------|------|
| `ICellData.cs` | `Runtime/Lighting/ProbeVolume/` | `ICellData` 和 `IPerScenarioData` 接口定义 |
| `APVCellData.cs` | `Runtime/Lighting/ProbeVolume/` (可选) | APV 算法的 `CellData` 实现（如需要可单独文件） |

### 4.4 迁移步骤

1. **创建接口文件** - 在 `Runtime/Lighting/ProbeVolume/` 目录下创建 `ICellData.cs`
2. **修改现有 Cell 类** - 将 `ProbeReferenceVolume.cs` 中的 `CellData` 改为 `ICellData` 接口
3. **实现 APVCellData** - 将现有 `CellData` 重命名为 `APVCellData`，实现 `ICellData` 接口
4. **修改 Cell 类** - 将 `Cell.data` 字段类型从 `CellData` 改为 `ICellData`

### 4.5 命名空间

```csharp
namespace UnityEngine.Rendering
{
    // ICellData 和 IPerScenarioData 放在 UnityEngine.Rendering 命名空间
    // 与现有的 ProbeReferenceVolume、ProbeBrickPool 等保持一致
}
```

---

*文档版本：7.0 (极简版 - 仅接口定义)*  
*创建时间：2026-01-07*  
*最后更新：2026-01-07*  
*作者：Fraxinus RP Core Team*
