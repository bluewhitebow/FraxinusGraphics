# Fraxinus URP Global

[English](README.md) | [简体中文](README.zh-CN.md)

Version `14.0.99` · Unity `2022.3` · Package ID `com.unity.render-pipelines.universal-config`

Shared configuration for Fraxinus URP, keeping platform constants consistent between C# rendering code and generated HLSL.
Used with the matching [Fraxinus URP](../Fraxinus-URPExtensions/README.md) and
[Fraxinus Core](../Fraxinus-RPCore/README.md) packages.

The current configuration defines visible light count limits:

| Configuration | Limit |
| --- | --- |
| Low-end mobile platforms | 16 |
| Mobile and constrained graphics APIs | 32 |
| Desktop platforms | 256 |

[ShaderOptions](Runtime/ShaderConfig.cs) defines the constants;
[ShaderConfig.cs.hlsl](Runtime/ShaderConfig.cs.hlsl) provides the corresponding shader macros.
The matching URP selects these limits by device for light processing and shader buffer layouts.

After changing the C# configuration, run **Edit → Rendering → Generate Shader Includes** to update HLSL.

License: [Unity Companion License](LICENSE.md).
