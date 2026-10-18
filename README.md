# FraxinusGraphics

[English](README.md) | [简体中文](README.zh-CN.md)

A rendering package suite for Unity 2022.3, combining RenderGraph, cached shadows, upscaling and probe lighting. This repository contains package source; the Sponza demo shown below is not included.

![APRTV indirect lighting off and on](docs/images/aprtv-gi-comparison.png)

Same camera and lighting, with APRTV indirect lighting off and on. [GI response and probe visualization](docs/Screenshots.md).

| Package | Version | Highlights |
| --- | --- | --- |
| [Fraxinus-RPCore](Packages/Fraxinus-RPCore/README.md) | `14.0.99` | RenderGraph, resource management, rendering utilities, probe lighting and standalone APV baking. |
| [Fraxinus-URPExtensions](Packages/Fraxinus-URPExtensions/README.md) | `14.0.99` | RenderGraph rendering, cached cascaded shadows, HZB and external drawing interfaces, STP / SGSR upscaling, and probe lighting integration. |
| [Fraxinus-URPGlobal](Packages/Fraxinus-URPGlobal/README.md) | `14.0.99` | Shared URP configuration that keeps platform light limits consistent between C# and HLSL. |
| [Fraxinus-Light-Transport](Packages/Fraxinus-Light-Transport/README.md) | `1.0.1` | Compute-based GPU ray queries, acceleration structures and sampling tools for lighting and geometry calculations. |

## Use in a project

Keep this repository beside your Unity project:

```text
workspace/
├─ FraxinusGraphics/Packages/
└─ MyUnityProject/Packages/manifest.json
```

Add or replace these entries inside the project's `Packages/manifest.json` → `dependencies`, keeping its other dependencies:

```json
{
  "com.unity.render-pipelines.core": "file:../../FraxinusGraphics/Packages/Fraxinus-RPCore",
  "com.unity.render-pipelines.universal": "file:../../FraxinusGraphics/Packages/Fraxinus-URPExtensions",
  "com.unity.render-pipelines.universal-config": "file:../../FraxinusGraphics/Packages/Fraxinus-URPGlobal",
  "com.fraxinus.rendering.light-transport": "file:../../FraxinusGraphics/Packages/Fraxinus-Light-Transport"
}
```

Paths are relative to the project's `Packages` directory. Reference all four packages from the same checkout. Core, URP and Config retain their Unity package IDs and replace the corresponding packages; keep those IDs unchanged. Unity resolves the other declared dependencies.

1. Open the project in **Unity 2022.3** and let package import finish.
2. Create **Assets > Create > Rendering > URP Asset (with Universal Renderer)**, or use an existing URP Asset.
3. Assign it in **Edit > Project Settings > Graphics > Scriptable Render Pipeline Settings**. In **Quality**, check that the active level's **Render Pipeline Asset** uses the same asset or inherits Graphics.
4. Open the renderer referenced by the asset's **Renderer List**. Use **Rendering Path** to choose Forward, Forward+ or Deferred; add custom rendering features on this renderer.

URP Global supplies shared configuration and Light Transport supplies the compute tracing backend. Neither needs a scene component for the built-in workflows.

## Feature controls

| Feature | Where to start |
| --- | --- |
| Cached cascaded shadows | URP Asset → **Lighting**: set **Main Light** to **Per Pixel**, enable **Cast Shadows** and **Cached Main Light**, then set **Min Static Cascade** and **Cached Quality**. When a changed shadow caster needs a refresh, call `UniversalAdditionalCameraData.SetCachedCSMDirty()` on the affected camera. |
| STP / SGSR | URP Asset → **Quality**: set **Render Scale**, for example `0.75`, and choose **Upscaling Filter**. STP is listed as **Spatial-Temporal Post-Processing**; SGSR uses **Snapdragon Game Super Resolution 1.0 / 2.0**. For temporal modes, start with a single Base camera, post-processing enabled and MSAA disabled. |
| RenderGraph extensions | **Window > Package Manager > Fraxinus URP Extensions > Samples > URP RenderGraph Samples > Import**. Add an imported feature to the renderer's **Add Renderer Feature** list. These are code samples; some require a material or show their work only in the Frame Debugger. |
| HZB / external drawing | Integrate through the [HZB and drawing interfaces](Packages/Fraxinus-URPExtensions/Runtime/UniversalRenderPipelineBRG.cs). Check capability and snapshot availability before consuming depth data. |
| Shared shader configuration | Edit [ShaderConfig.cs](Packages/Fraxinus-URPGlobal/Runtime/ShaderConfig.cs), then run **Edit > Rendering > Generate Shader Includes** to update HLSL. |

Custom passes must implement `RecordRenderGraph(RenderGraph, ContextContainer)`; an `Execute`-only pass needs migration. STP also requires compute and motion-vector support, with dynamic resolution and camera stacking disabled. DLSS / MetalFX currently fall back to Linear; Unity's native GPU Resident Drawer backend is unavailable on Unity 2022.

## Probe lighting

Select one backend in **URP Asset > Lighting > Light Probe System**:

| Backend | Scene volume | Bake action |
| --- | --- | --- |
| **Adaptive Probe Volumes** | **GameObject > Light > Adaptive Probe Volume** | **Bake Probe Volumes** |
| **Adaptive PRT Volumes (APRTV)** | **GameObject > Light > Adaptive PRT Volume** | **Bake APRT** |

1. Save the scene. Enable **Contribute GI** on geometry that should contribute to baking, then place the corresponding volume around the area to light. Use Baked/Mixed lights for APV; APRT uses directional lights and the sky. Start with a small region; APRT's **Adaptive** layout handles adaptive probe placement.
2. Open **Window > Rendering > Lighting**. Use the **Adaptive Probe Volumes** or **Adaptive Precompute Radiance Transfer** tab. Choose **Single Scene**, or create a **Baking Set** for multiple scenes and load/save every scene in that set.
3. Adjust probe spacing and bounds, run the matching bake action, then save the scene. APRT also has **Bake APRT** in its volume Inspector. **Generate Lighting** does not start the standalone APV bake.

For live APRT lighting, set the Baking Set's **Relighting > Lighting Source** to **Live Scene Lighting**. Add **Lighting > APRTV Options** to a Global Volume Profile and check the overrides you want to adjust. **GI Intensity** controls the overall contribution; **Sky Intensity** defaults to zero, so raise it to enable the scene skybox contribution. Directional lights and skybox changes can then relight the baked transport. Geometry or material changes require rebaking.

For stored APRT lighting states, enable **Enable Lighting Scenarios** and **Enable Lighting Scenario Blending** on the URP Asset. Bake each Active Scenario, using **Probe Positions > Don't Recalculate** for subsequent states, then preview with **Blend Target / Blend Factor**. A successful scenario bake selects **Baked Lighting Scenarios**; switch back to **Live Scene Lighting** when you want GI to follow current scene lighting.

APRT does not handle dynamic occlusion or point/spot-light transport. It keeps all probe cells for loaded scenes resident; size Baking Sets accordingly. APV's standalone compute baker does not require DXR.

## Further reading

- [APRT setup, scenarios and debug views](Packages/Fraxinus-RPCore/Documentation~/APRT.md)
- [APRT integration with URP](Packages/Fraxinus-URPExtensions/Documentation~/APRT.md)
- [Standalone APV baking and supported inputs](Packages/Fraxinus-RPCore/Documentation~/StandaloneAPVBaking.md)
- [Light Transport APIs and limits](Packages/Fraxinus-Light-Transport/README.md)
- [Rendering screenshots](docs/Screenshots.md)

Unity-derived code is covered by the [Unity Companion License](Packages/Fraxinus-RPCore/LICENSE.md). See each package's license and third-party notices for its components.
