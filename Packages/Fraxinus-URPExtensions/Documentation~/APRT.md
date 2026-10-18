# Adaptive PRT Volumes

Update Fraxinus-URPExtensions and Fraxinus-RPCore together. Select **Light Probe
System > Adaptive PRT Volumes (APRTV)** on the URP Asset; no Renderer Feature is
required. Add an Adaptive PRT Volume, save its scene, and bake from its Inspector
or the Lighting window's Probe Volumes tab.

## Rendering

Core stores versioned six-direction albedo, static visibility, distance moments,
probe validity, layout data and a directional sky irradiance transfer matrix.
New bakes use one **APRTV** lighting response for directional-light capture,
compute relighting and receiving. URP supplies the first four enabled scene
directional lights, preserving the reference ordering and light intensity.
Add **Lighting > APRTV Options** to a Volume Profile for camera-specific relighting
and receiving controls, including **Sky Intensity**. Sky lighting captures raw HDR
radiance from the scene's `RenderSettings.skybox`. The old Baking Set cubemap field
is retained for serialization compatibility and does not supply lighting. In
**Live Scene Lighting**, changing the scene skybox or its material settings
relights existing sky transfer without rebaking. Core integrates that radiance
with baked directional visibility and the cosine kernel to produce irradiance divided by
pi in a separate buffer. It does not read `RenderSettings.ambientProbe` or depend
on enabled directional lights. Old bakes require rebaking for sky lighting.
Point and spot lights continue to use URP direct lighting and do not relight APRT
probes.

`APRTRelightPass` imports the persistent probe buffers into RenderGraph and
declares their writes before material lighting. Forward, GBuffer and Render
Objects passes declare reads. Eligible Base cameras update probes; Overlay
cameras consume the Base camera's initialized result and settings. The graph
records settings and sky radiance snapshots, and each Base camera uses a separate
scene-skybox cache, including skybox changes made by camera callbacks.
Reflection, Preview, depth-only and cubemap cameras do not update or consume APRT
lighting.

`SampleAPRTEmission` adds the sampled response multiplied by
surface albedo to emission, matching the reference Lit ShaderGraph. Standard URP
direct, ambient and specular lighting continue to contribute. The extra response
uses the mesh normal independently of the material normal map, as the reference
graph does. Normal maps continue to affect ordinary URP lighting. The response
preserves the reference's saturation, shadow visibility, double application of
Indirect Intensity and upward-facing floor compensation for directional lights.
Physical sky irradiance bypasses those artistic gains. Lit and Simple Lit
Forward/Deferred, Baked Lit and Lit ShaderGraphs use this integration. Deferred
stores the result in the usual GBuffer lighting target; there is no additional
fullscreen composition pass.

Historical **Diffuse Transfer** bakes instead supply probe irradiance divided
by pi through `ApplyAPRTGI`, with normal material BRDF/occlusion behavior. It uses
visible realtime/mixed directional lights and excludes baked-only lights. Valid
samples replace ordinary diffuse ambient/probe input unless **Add Ambient
Lighting** is enabled; lightmapped surfaces retain their lightmap GI.

Disabled APRT, zero GI intensity and missing/invalid transport use existing URP
lighting. A single Reference Grid retains the legacy boundary clamp. With
multiple Baking Sets, each grid has a bounded sampling region; adaptive mode
requires coverage in its brick domains. Historical bakes keep their
recorded response until rebaked; the inspector has no lighting-model selector.
Unchecked APRTV Options parameters inherit Default Profiles or existing Baking Set
values. Export Existing Relighting to Volume Profile preserves the current look
in a profile without modifying scenes.

## Lighting scenarios

APRT uses APV's shared **Scenario / Active / Status** list, with add/remove,
reordering and double-click rename. Select an Active Scenario, adjust the lights,
skybox and bake response, then choose **Bake APRT** to store its final directional
and sky SH. A successful bake selects **Baked Lighting Scenarios**. Choose
**Probe Positions > Don't Recalculate** before baking the next Scenario to reuse
the probe layout and retain compatible previous results. Frozen placement
requires a valid previous bake, compatible placement settings and all Baking Set
scenes loaded. Input-only lighting captures do not count as completed Scenario
bakes. Failed or cancelled bakes retain the previous complete data.

Use **Blend Target** and **Blend Factor** beneath the list to preview two baked
Scenarios. The public Baking Set API exposes `aprtLightingScenario`,
`BlendAPRTLightingScenario(target, weight)` and `aprtScenarioBlendingFactor`.
Commit the target with `aprtLightingScenario` before starting another transition.
Set `aprtUseLightingScenarios = false` to return to **Live Scene Lighting**.

RenderGraph records the loaded endpoints and weight per Base camera. Runtime
interpolates their stored directional and sky SH and applies Temporal Blend once.
Set Temporal Blend to 1 when a scripted weight must be followed immediately.
Receiver controls in APRTV Options, including GI Intensity, floor response and
leak/bias controls, remain live. Lighting evaluation settings such as Sky Intensity
are stored in the Scenario result and require rebaking to change it. Saved mode
skips live sky capture. Direct lighting and the visible sky must be driven
separately by the scene controller.

Additively loaded scenes can share one Baking Set or load independent Baking
Sets simultaneously. Each set retains its own cell IDs, layout, Active Scenario,
target and weight, including when two sets reuse the same Scenario names. Many
Scenarios may be saved; each set loads its Active and optional target
for every cell belonging to its loaded scenes. Three-way Scenario interpolation within one set is
not implemented. Scenario names are shared with APV; baked outputs and
Active/blend selection remain backend-specific.

Control independently loaded sets through their public APIs:

```csharp
roomA.aprtLightingScenario = "Warm";
roomA.BlendAPRTLightingScenario("Cool", 0.25f);
roomB.aprtLightingScenario = "Warm";
roomB.BlendAPRTLightingScenario("Cool", 0.75f);
```

RenderGraph relights each loaded set and copies its sampling buffers into a
combined GPU atlas. The atlas topology stays fixed for all recorded cameras in
the frame, while each camera can select a different active subset. Sampling
uses the receiving world position and valid probe domains. Overlapping valid
domains contribute equal weights; a renderer's Unity scene does not select its
lighting set. Unloading a scene removes its own membership, and removing a set's
last scene retires its resources after recorded cameras finish.

## Loading and storage

APRT loads all cells belonging to the loaded scenes together and keeps them on
the GPU until those scenes or their baked data change. An explicitly activated
Baking Set without scene registration loads the entire set. Camera movement
does not select cells or trigger loading. APRT has no GPU/disk streaming,
per-frame loading quotas, resident probe limits or streaming memory budgets.
APV retains its own streaming controls and budgets.

Live APRTV directional lighting uses a cached scene-wide light list. Native
object change tracking discovers additions and removals; camera visibility,
culling masks and baked-light filtering do not change APRTV's light inputs.
Light properties are read from the live objects. The steady render path reuses
its storage, while loading and changes to scene membership may allocate.

The URP Asset exposes **Enable Lighting Scenarios** and
**Enable Lighting Scenario Blending** for APRT. Disabling Lighting Scenarios
preserves saved results and uses live scene lighting; disabling blending still
permits a single baked Scenario. Blending is controlled by these switches and
does not depend on the APV Blending Memory Budget.

New bakes store transport and Scenario SH inside the Baking Set asset. Existing
external bakes remain supported: the complete required cell and Active/target
payloads are read synchronously when loaded. Keep their referenced
`<BakingSetName>_APRTData/<generation>/` files when moving or sharing old assets;
the build processor stages them under
`StreamingAssets/APRTStreamingAssets/<BakingSetGuid>/<generation>/`. Rebaking
stores new data inline. External payload compatibility requires directly
accessible filesystem paths; URL/jar StreamingAssets paths are unsupported.

Loading occurs on first use and after scene membership, baked data or Scenario
endpoints change. Changing the weight of the current Scenario pair does not
rebuild GPU allocations. Imported buffers remain stable until recorded cameras
finish; changes that would replace them wait for the next unpinned update.
**Estimated GPU Memory cost** reports the current allocation, including the
combined sampling atlas. Memory usage now follows the complete loaded data, so
large sets should be measured for loading time and GPU memory consumption.

## Probe debug display

In **Rendering Debugger > APRTV > Probe display**, enable **Show Probes**
to draw spheres at loaded probe positions. Selection and Scene Gizmos are not
required. **Probe Size** controls world-space radius and **Draw Distance** limits
the display around the camera. **Color Mode** offers Positions (cyan), Relit
Lighting, Validity, Baked Transfer RGB and Sky Visibility; **Exposure** appears
only for Relit Lighting and Baked Transfer RGB and adjusts those visualizations.
Hover **Color Mode** or **Display view** for their mode descriptions.
Relit Lighting shows probe directional and sky SH without receiver albedo,
GI Intensity or floor compensation.

This sphere display is independent of **Display view**, which replaces receiver
surface shading with interpolated diagnostics. It runs in Forward and Deferred
for eligible Base Game and Scene cameras, excluding Overlay, Reflection, Preview,
depth-only and cubemap captures. GI Intensity zero still permits probe display
and retains the ordinary lighting fallback. The sphere pass declares its probe
buffer reads after relighting in RenderGraph. With Show Probes off it records no
pass, and debug resources are created only when needed. Pixel picking and
contributing-probe selection are not implemented.

The panel overview shows Status, Baking Set, loaded probe count and GPU memory.
Collapsed foldouts provide baked probe count, cells and Reference
Grid spacing; sky status/source; and the acceptance threshold/loaded probe count
below it. The validity count remains diagnostic when Visibility Leak Reduction
is off. Sky status/source only inspect cached state and do not start captures or
readbacks. Edit relighting parameters through **APRTV Options** in a Volume Profile.

## Approximation and validation

The six-direction representation approximates transport from captured surface
colors and static visibility. APRTV capture preserves the reference center
weighting and saturation preference; Visibility Leak Reduction is optional and
disabled by default to match the reference. Adaptive placement uses the same lighting
response with a different probe topology, so its pixels can differ from a regular
reference grid. Neither response evaluates lighting at individual hit surfaces,
reads dynamic shadow maps or updates static visibility when geometry moves. Sky
uses L2 radiance and L1 receiver irradiance, so sharp environment features and
directional occlusion are approximated.

The runtime initializes all loaded probes before enabling sampling. It uses
compute shaders and structured buffers (shader model 4.5); unsupported hardware
uses the ordinary GI fallback. Allocations contain the complete loaded-scene
selection and are reused while that selection and its Scenario endpoints stay
unchanged.

See the Core [APRT documentation](../../Fraxinus-RPCore/Documentation~/APRT.md)
for baking and layout details. Both packages must be updated together.
Validate the target configuration in Forward and Deferred, including pure sky,
changing directional lights, disabled/missing-data fallback, rotated reference
grids, adaptive layouts and APV backend isolation.
