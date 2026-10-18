# Fraxinus RPCore

[English](README.md) | [简体中文](README.zh-CN.md)

A rendering foundation for Unity 2022.3, providing RenderGraph, resource management, probe lighting, and editor tools for the companion pipeline.

Version: `14.0.99` · Package ID: `com.unity.render-pipelines.core`

## Features

- RenderGraph: Raster, Compute, and Unsafe passes, resource dependencies, and Native Render Pass merging, adapted to Unity 2022 execution APIs.
- Rendering utilities: RTHandle, Blitter, Volume, ShaderLibrary, and Rendering Debugger for resource management, visual settings, and diagnostics.
- Temporal upscaling: STP compute resources and APIs, with motion vectors, frame history, and post-processing scheduled by the companion URP.
- Standalone APV baking: compute ray tracing with adaptive probe placement, sky occlusion, Rendering Layers, static materials, and diffuse bounces. DXR is not required.
- Probe data management: shared Cell / Brick layouts, Baking Set workflows, and lighting scenarios. APV supports streaming; APRT loads all cells belonging to loaded scenes together.
- APRT relighting: precomputed transport responds to directional lights and HDR skies, with multiple Baking Sets and blending between two lighting scenarios.

## Integration and limits

Use with the companion [URP](../Fraxinus-URPExtensions/README.md), [Config](../Fraxinus-URPGlobal/README.md),
and [Light Transport](../Fraxinus-Light-Transport/README.md) packages.
Custom rendering code must target this fork's RenderGraph APIs. The native GPU Resident Drawer is unavailable on Unity 2022.

APV baking targets static geometry. APRT requires rebaking after geometry or material changes and does not currently handle dynamic occlusion or runtime multi-bounce feedback.

## Documentation

- [RenderGraph implementation](Runtime/RenderGraph/RenderGraph.cs)
- [Standalone APV baking](Documentation~/StandaloneAPVBaking.md)
- [APRT workflow](Documentation~/APRT.md)
- [Validation records](Tests~/Unity2022APV/README.md)

## License

Unity-derived code is covered by the [Unity Companion License](LICENSE.md). Third-party components retain their own licenses.
