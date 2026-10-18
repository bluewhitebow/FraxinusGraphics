# Fraxinus URP Global

[English](README.md) | [简体中文](README.zh-CN.md)

版本 `14.0.99` · Unity `2022.3` · 包 ID `com.unity.render-pipelines.universal-config`

Fraxinus URP 的共享配置层，让 C# 渲染代码与生成的 HLSL 使用相同的平台常量。
与 [Fraxinus URP](../Fraxinus-URPExtensions/README.zh-CN.md) 和
[Fraxinus Core](../Fraxinus-RPCore/README.zh-CN.md) 配套使用。

当前配置集中在可见灯光数量上限：

| 配置 | 数值 |
| --- | --- |
| 低端移动平台 | 16 |
| 移动端及受限图形 API | 32 |
| 桌面平台 | 256 |

[ShaderOptions](Runtime/ShaderConfig.cs) 是常量来源，
[ShaderConfig.cs.hlsl](Runtime/ShaderConfig.cs.hlsl) 提供对应 Shader 宏。
配套 URP 根据设备选择这些上限，用于灯光处理和 Shader 缓冲区布局。

修改 C# 配置后，执行 **Edit → Rendering → Generate Shader Includes**，同步生成 HLSL。

许可：[Unity Companion License](LICENSE.md)。
