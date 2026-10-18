# FraxinusGraphics usage guide

[English](Usage.md) | [简体中文](Usage.zh-CN.md)

[FraxinusGraphics](../README.md)

This guide targets **Unity 2022.3** and the four packages from the same FraxinusGraphics checkout. The Sponza scene shown in the screenshots is a separate demo and is not included.

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
| HZB / external drawing | Integrate through the [HZB and drawing interfaces](../Packages/Fraxinus-URPExtensions/Runtime/UniversalRenderPipelineBRG.cs). Check capability and snapshot availability before consuming depth data. |
| Shared shader configuration | Edit [ShaderConfig.cs](../Packages/Fraxinus-URPGlobal/Runtime/ShaderConfig.cs), then run **Edit > Rendering > Generate Shader Includes** to update HLSL. |

Custom passes must implement `RecordRenderGraph(RenderGraph, ContextContainer)`; an `Execute`-only pass needs migration. STP also requires compute and motion-vector support, with dynamic resolution and camera stacking disabled. DLSS / MetalFX currently fall back to Linear; Unity's native GPU Resident Drawer backend is unavailable on Unity 2022.

## Probe lighting

Select one backend in **URP Asset > Lighting > Light Probe System**. APRTV (Adaptive PRT Volumes) is the probe-lighting system; its code, bake controls and detailed documentation use the shorter name **APRT**.

| Backend | Scene volume | Bake action |
| --- | --- | --- |
| **Adaptive Probe Volumes** | **GameObject > Light > Adaptive Probe Volume** | **Bake Probe Volumes** |
| **Adaptive PRT Volumes (APRTV)** | **GameObject > Light > Adaptive PRT Volume** | **Bake APRT** |

1. Save the scene. Enable **Contribute GI** on geometry that should contribute to baking, then place the corresponding volume around the area to light. Use Baked/Mixed lights for APV; APRT uses directional lights and the sky. Start with a small region; APRT's **Adaptive** layout handles adaptive probe placement.
2. Open **Window > Rendering > Lighting**. Use the **Adaptive Probe Volumes** or **Adaptive Precompute Radiance Transfer** tab. Choose **Single Scene**, or create a **Baking Set** for multiple scenes and load/save every scene in that set.
3. Adjust probe spacing and bounds, run the matching bake action, then save the scene. APRT also has **Bake APRT** in its volume Inspector. **Generate Lighting** does not start the standalone APV bake.

### Live APRT relighting

Set the Baking Set's **Relighting > Lighting Source** to **Live Scene Lighting**. Add **Lighting > APRTV Options** to a Global Volume Profile and check the overrides you want to adjust. **GI Intensity** controls the overall contribution; **Sky Intensity** defaults to zero, so raise it to enable the scene skybox contribution. Directional lights and skybox changes can then relight the baked transport. Geometry or material changes require rebaking.

### Stored APRT lighting scenarios

Enable **Enable Lighting Scenarios** and **Enable Lighting Scenario Blending** on the URP Asset. Bake each Active Scenario, using **Probe Positions > Don't Recalculate** for subsequent states, then preview with **Blend Target / Blend Factor**. A successful scenario bake selects **Baked Lighting Scenarios**; switch back to **Live Scene Lighting** when you want GI to follow current scene lighting.

APRT does not handle dynamic occlusion or point/spot-light transport. It keeps all probe cells for loaded scenes resident; size Baking Sets accordingly. APV's standalone compute baker does not require DXR.

## Common setup issues

| Symptom | Check |
| --- | --- |
| Changes to a URP Asset have no effect | Check the active **Quality** level's **Render Pipeline Asset** override and the camera's selected Renderer. |
| Generate Lighting does not bake APV | Use **Bake Probe Volumes** in the **Adaptive Probe Volumes** lighting tab. |
| APRT does not follow edited lights | Select **Live Scene Lighting**; a successful scenario bake switches to **Baked Lighting Scenarios**. |
| APRT has no sky contribution | In **APRTV Options**, enable the **Sky Intensity** override and set it above zero. |
| A legacy Renderer Feature no longer renders | Its passes must implement `RecordRenderGraph(RenderGraph, ContextContainer)`; `Execute` alone is not supported. |

## Further reading

- [APRT setup, scenarios and debug views](../Packages/Fraxinus-RPCore/Documentation~/APRT.md)
- [APRT integration with URP](../Packages/Fraxinus-URPExtensions/Documentation~/APRT.md)
- [Standalone APV baking and supported inputs](../Packages/Fraxinus-RPCore/Documentation~/StandaloneAPVBaking.md)
- [Light Transport APIs and limits](../Packages/Fraxinus-Light-Transport/README.md)
- [Rendering screenshots](Screenshots.md)

Unity-derived code is covered by the [Unity Companion License](../Packages/Fraxinus-RPCore/LICENSE.md). See each package's license and third-party notices for its components.
