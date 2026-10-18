# FraxinusGraphics

[English](README.md) | [简体中文](README.zh-CN.md)

FraxinusGraphics brings RenderGraph rendering, cached cascaded shadows, spatial and temporal upscaling, external drawing interfaces, and GPU probe baking to Unity 2022.3. Four companion packages connect frame rendering, lighting tools and compute ray queries in one source tree.

[Usage guide](docs/Usage.md) · [Packages](#packages) · [Rendering preview](#rendering-preview)

## What it provides

### RenderGraph rendering

Raster, Compute and Unsafe passes declare their resources and dependencies, with resource lifetime management and a Native Render Pass bridge for Unity 2022. Supported passes can merge according to renderer and device conditions. Custom effects use the same graph workflow as the pipeline, with [RenderGraph samples](Packages/Fraxinus-URPExtensions/Samples~/URPRenderGraphSamples) as starting points.

### Cached cascaded shadows

Reuse eligible main-light shadow cascades per camera to reduce repeated rendering of unchanged cascades. Choose the first cached cascade and the refresh workload, and explicitly invalidate the cache when scene changes require it. [Cached CSM implementation](Packages/Fraxinus-URPExtensions/Runtime/Extensions/CachedCSM/CachedCascadeShadowMap.cs).

### STP and SGSR upscaling

Choose STP or SGSR2 for temporal reconstruction, or SGSR1 for spatial upscaling. The pipeline schedules the required camera jitter, motion vectors and history resources for the temporal paths. Render Scale and Upscaling Filter expose the resolution/quality tradeoff in the URP Asset; [the usage guide covers their requirements](docs/Usage.md#feature-controls).

### HZB and external drawing

Depth-pyramid snapshots are available to CPU and GPU consumers, alongside a current-frame HZB path. Camera, depth/normal and shadow drawing hooks let external instancing and terrain renderers participate in the pipeline. These are [integration interfaces](Packages/Fraxinus-URPExtensions/Runtime/UniversalRenderPipelineBRG.cs); consumers supply their own rendering or culling backend.

### Standalone APV baking

Bake adaptive probe lighting through compute ray tracing without DXR. The workflow includes adaptive placement, sky occlusion, Rendering Layers, static material emission and diffuse bounces, with Baking Sets, lighting scenarios and APV streaming for scene data. Baking runs through the editor with progress and cancellation. [Supported inputs and baking details](Packages/Fraxinus-RPCore/Documentation~/StandaloneAPVBaking.md).

### Relightable probe volumes

APRTV stores static geometry transport so directional lights and HDR skies can update probe lighting at runtime. It also supports independently loaded Baking Sets, blending between two baked lighting scenarios, per-camera Volume controls and probe debug views. [APRT workflow](Packages/Fraxinus-RPCore/Documentation~/APRT.md).

### Compute ray queries

Light Transport supplies mesh/instance acceleration structures, closest-hit and occlusion queries, GPU dispatch and sampling sequences for custom lighting calculations. Its Compute backend runs without hardware ray tracing; direct C# integration currently requires assembly access to its internal APIs. [Library interfaces](Packages/Fraxinus-Light-Transport/README.md).

### Rendering workflows and diagnostics

Use Forward, Forward+, Deferred or the separate 2D Renderer, with ShaderGraph, post-processing and camera stacking where supported. RTHandle, Blitter and Volume provide shared rendering utilities; Render Graph Viewer and Rendering Debugger help inspect passes, resources and lighting while developing extensions.

## Packages

| Package | Version | Responsibility |
| --- | --- | --- |
| [Fraxinus-RPCore](Packages/Fraxinus-RPCore/README.md) | `14.0.99` | Shared rendering infrastructure, probe data and baking workflows. |
| [Fraxinus-URPExtensions](Packages/Fraxinus-URPExtensions/README.md) | `14.0.99` | Frame rendering, shadows, upscaling and external renderer integration. |
| [Fraxinus-URPGlobal](Packages/Fraxinus-URPGlobal/README.md) | `14.0.99` | Platform light limits shared by C# and generated HLSL. |
| [Fraxinus-Light-Transport](Packages/Fraxinus-Light-Transport/README.md) | `1.0.1` | GPU acceleration structures, ray queries and sampling. |

## Start using it

1. Reference all four local packages from one checkout. The [usage guide](docs/Usage.md#use-in-a-project) includes the relative-path `manifest.json` entries.
2. Create or select a URP Asset and assign it in **Project Settings > Graphics**; check the active **Quality** override too.
3. Enable cached shadows in **Lighting**, select an upscaler in **Quality**, or add RenderGraph features to the active Renderer. Probe lighting is selected separately under **Light Probe System**.

The guide includes [feature controls](docs/Usage.md#feature-controls) and [APV/APRT baking steps](docs/Usage.md#probe-lighting). URP Global and Light Transport need no extra scene components for these built-in workflows.

## Rendering preview

![APRTV indirect lighting off and on](docs/images/aprtv-gi-comparison.png)

Same camera and lighting, with APRTV indirect lighting off and on. [GI response and probe visualization](docs/Screenshots.md).

The images were captured from a separate Sponza demo; this repository contains package source.

## Compatibility

Custom passes use `RecordRenderGraph`; `Execute`-only extensions require migration. Cached shadows need explicit refresh for changes the cache does not track. Temporal upscalers have camera/device requirements. APRT requires rebaking after geometry or material changes and does not handle dynamic occlusion. See the [usage guide](docs/Usage.md) and package docs for the relevant limits.

This is an independently maintained Unity fork. Unity-derived code follows the [Unity Companion License](Packages/Fraxinus-RPCore/LICENSE.md); individual packages retain their third-party notices.
