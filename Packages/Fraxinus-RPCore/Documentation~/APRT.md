# Adaptive PRT Volumes (APRTV)

APRTV runs six-direction capture, relighting and receiver response inside
Fraxinus's probe-volume framework. Baking Sets, scene membership, asynchronous
baking, oriented regions, Adaptive brick placement and URP Forward/Deferred
integration remain owned by APRTV. New bakes use one APRTV lighting response;
the inspector does not expose a lighting-model selector. Historical Diffuse
Transfer bakes retain their recorded response until a new bake upgrades them.

Update Core and URP together. APRT loads all cells belonging to loaded scenes
together; camera-driven cell streaming and its budgets have been removed.

## Setup and baking

1. Select **Adaptive PRT Volumes** in the render pipeline asset. Enable
   **Lighting Scenarios** to author baked lighting states, and **Lighting Scenario
   Blending** to transition between them. These options also control runtime playback.
2. Add **GameObject > Light > Adaptive PRT Volume**. **Adaptive** uses the shared
   geometry-adaptive brick placement. **Reference Grid** provides the regular
   lattice with explicit dimensions and spacing; keep one Reference Grid per Baking Set.
3. Enable **Contribute GI** on active LOD0 Mesh Renderers and Terrains to capture.
   Choose the Baking Set's contributor layer mask/minimum size, or volume overrides.
4. Load and save every scene in the Baking Set. In its inspector choose
   probe spacing/subdivision and capture settings. Defaults are 64 pixels per face,
   100 world units, Bake Saturation Priority 10 and Bake Center Weight 5.
5. Choose **Bake APRT**. Placement and capture run on editor updates. Cancellation
   and source changes keep the previous complete bake. Save scenes after a successful
   bake to persist the Baking Set association.

Local bounds preserve rotation and ignore Transform scale. The inspector's fit
buttons include GI contributors and terrain trees. Scene and Global Adaptive
regions derive their bounds from participating contributors. Multiple Adaptive
volumes are supported. Reference Grid uses exact origin, rotation, dimensions
and spacing; its first probe is Transform Position plus the rotated First Probe
Offset. Sampling Size and Sampling Origin Offset control the sampling box
independently of the captured lattice. Zero Sampling Size uses the captured
lattice extent `(dimensions - 1) * spacing`.

## Runtime Volume options

Add **Lighting > APRTV Options** to a Global or Local Volume Profile. GI and sky
intensity, directional response, temporal blend, leak reduction and
receiver biases are evaluated through the camera's Volume stack. Global, Quality
and Custom Default Profiles are also supported. Float parameters blend using
normal Volume weights and priorities; switches use discrete selection. Overlay
cameras inherit their Base camera's initialized lighting.

The Baking Set contains placement/capture settings. APRT loads the complete
probe data for loaded scenes without a resident-probe cap or streaming budget.
It no longer exposes a second set of relighting controls. For compatibility,
unchecked Volume parameters inherit configured
default profiles or the old serialized Baking Set values. Local Volume blending
uses those existing values as its baseline, preserving authored scenes.

**Export Existing Relighting to Volume Profile** creates a profile containing the
Baking Set's saved relighting values as overrides. Assign it to a Global Volume
to move the existing appearance into the Volume workflow. Export creates a new
asset using the normal Unity asset workflow and does not edit or save scenes. Parameter edits support
normal Undo/Redo and require no rebake.

RenderGraph records immutable settings and sky radiance snapshots per Base
camera. Each camera has a separate cache of the scene skybox, including changes
made by camera callbacks. A camera switch initializes the shared SH output
without blending another camera's history; repeated updates by the same camera
retain Temporal Blend. Reflection, Preview, depth and cubemap captures do not
participate in this lighting path.

## Lighting scenarios

APRT uses the same **Lighting Scenarios** list as APV: **Scenario**, **Active**,
and **Status** columns, **+ / -** controls, drag reordering and double-click rename.
The Active radio button selects the scenario that the normal **Bake APRT** action
will bake. Status reports whether that name has compatible baked APRT lighting;
old captured lighting presets alone do not count as a completed scenario bake.

1. Add a scenario with **+**, rename it, and set it as **Active**.
2. Adjust directional lights, the active scene's skybox, and bake response settings.
3. Choose **Bake APRT**. This captures geometry transport and stores the active
   scenario's final directional and sky SH for every probe. A successful bake
   selects **Baked Lighting Scenarios** as the lighting source.
4. Choose **Probe Positions > Don't Recalculate**, then add and activate the next
   scenario, adjust its lighting, and choose **Bake APRT** again. Probe positions
   and brick layout are reused so previously baked scenarios remain compatible.
5. Use **Blend Target** and **Blend Factor** beneath the list to preview two baked
   scenarios. The preview target and weight are temporary; Active and Lighting
   Source are saved. **Live Scene Lighting**, under **Relighting**, enables realtime
   relighting of the shared geometry transport.

Frozen placement requires a valid previous bake, compatible placement settings
and volume bounds, and every scene in the Baking Set to be loaded. The geometry
capture still runs for the active scenario, allowing small geometry/material
changes while keeping the probe layout. Recalculating positions can produce a
new layout; any previous scenario with a different layout becomes unavailable
until rebaked. Cancellation and failed bakes retain the last complete bake.

Several Unity scenes can share one Baking Set. Load and save all of them, and
make a scene from that set active before baking each scenario. Up to four enabled
directional lights from the set contribute; the active scene supplies the skybox.
A missing skybox bakes black sky. A scenario covers the set's complete probe layout,
including all participating loaded scenes.

Baked scenario blending interpolates the two stored lighting results instead of
relighting them from today's scene lights. Each scenario's stored SH includes the
lighting evaluation settings used for its bake. Receiver controls in APRTV Options, including GI Intensity,
visibility rejection, receiver biases and floor response, remain live per camera.
Lighting evaluation controls, including Sky Intensity, require rebaking to change
a stored scenario. Synchronize direct lights and the visible sky separately, since scenario
blending controls APRT GI. Temporal Blend applies once after the scenario blend;
set it to 1 when a scripted weight must be followed immediately. Camera and
endpoint changes reset temporal history. The active and target scenarios supply
lighting for every loaded cell; switching scenarios preserves the shared layout.

After a scene from the Baking Set has activated APRT, game scripts can use its
public API:

```csharp
// bakingSet contains compatible baked Day and Night scenarios.
bakingSet.aprtLightingScenario = "Day";
bakingSet.BlendAPRTLightingScenario("Night", 0.5f);
// During the transition:
bakingSet.aprtScenarioBlendingFactor = progress; // 0 = Day, 1 = Night
// Commit the target before beginning the next transition:
bakingSet.aprtLightingScenario = "Night";
// Return to current scene lights and sky:
bakingSet.aprtUseLightingScenarios = false;
```

Missing or incompatible baked scenarios and non-finite weights are rejected;
an invalid saved selection does not publish stale lighting. Scenario names belong
to the Baking Set and are shared with APV, so renaming or deleting a name also
affects APV data using that name. Baked outputs and active/blend selections remain
backend-specific. Each Baking Set blends its active scenario with one target;
the API does not provide a three-way or larger scenario blend within one set.

## Multiple Baking Sets and additive scenes

Additively loaded scenes can belong to different, independently baked APRT
Baking Sets. Each loaded set keeps its own transport, spatial layout, cell IDs,
scenario pair and blend weight. Different sets can reuse local
cell IDs and scenario names. Load a scene from each set before controlling that
set through its public API:

```csharp
roomASet.aprtUseLightingScenarios = true;
roomASet.aprtLightingScenario = "Warm";
roomASet.BlendAPRTLightingScenario("Cool", 0.25f);

roomBSet.aprtUseLightingScenarios = true;
roomBSet.aprtLightingScenario = "Warm";
roomBSet.BlendAPRTLightingScenario("Cool", 0.75f);

// These updates affect only their respective set.
roomASet.aprtScenarioBlendingFactor = aProgress;
roomBSet.aprtScenarioBlendingFactor = bProgress;
```

Read `aprtLightingScenario`, `aprtOtherScenario` and
`aprtScenarioBlendingFactor` from each set to inspect its selection. Pass
`null` to `BlendAPRTLightingScenario` to stop that set's transition. Changing the
focused Baking Set preserves the other registered sets and their allocations.
Unloading a scene removes its cell membership; shared cells and a shared set
remain while another loaded scene owns them. Unloading the last scene belonging
to a set releases that set without resetting the others. Resources already
imported by a camera remain alive through the frame before they are released.

Scenario interpolation within each set and spatial sampling across sets are
separate operations. A receiver samples the resident sets whose valid domains
contain its world position. Overlapping valid results are averaged with equal
weight; a set's Scenario blend factor controls its own lighting endpoints, not
its share of that spatial average. Sampling does not bind a renderer exclusively
to the Baking Set associated with `renderer.scene`. Where no resident set
provides a valid result, normal lighting remains available. Missing or invalid
bake data uses that fallback or an available overlapping result.

The frame's prepared sampling topology contains all available resident sources,
including a source disabled by one camera's Volume settings. Later cameras use
the same source and atlas allocations with a separate active-set mask and
descriptor values. A camera enabling or disabling a set does not rebuild buffers
already imported into RenderGraph. New sources and changed endpoint allocations
can join when loaded data is updated after the frame finishes.

## Loading and legacy bake compatibility

APRT loads every cell belonging to the loaded scenes, together with the selected
lighting scenarios. Loading happens synchronously when scene membership, baked
data or scenario endpoints change. Camera movement does not rank, load or evict
cells. Repeated camera preparation reuses the loaded buffers.

APRT does not use GPU/Disk Streaming, Memory Budget, Blending Memory Budget,
Resident Probe Limit or per-frame cell-loading quotas. Those serialized values
in older assets do not limit APRT coverage or scenario blending. APV retains
its own streaming settings and behavior.

All eligible cameras share the loaded source buffers and the combined sampling
atlas. Multiple Baking Sets retain independent lighting and scene membership.
Buffers imported by RenderGraph remain valid through frame execution; scene
unloads, rebakes and scenario changes release or replace them after that frame.
GPU memory therefore grows with the data loaded by participating scenes and
their selected scenario endpoints. **Estimated GPU Memory cost** and
**Total GPU memory (MiB)** report those allocations, including the combined atlas.

New bakes store transport and scenario SH arrays directly in the Baking Set.
They no longer create `<BakingSetName>_APRTData/<generation>/` files. Compatible
scenarios from an earlier bake remain available when rebaking another scenario
with the same probe layout.

Existing external bakes remain supported. Their referenced `.aprtbin` files are
validated and read synchronously during loading. Include those existing files
when moving or sharing the Baking Set. The compatibility build processor still
copies referenced legacy files into
`StreamingAssets/APRTStreamingAssets/<BakingSetGuid>/`. This compatibility reader
requires a filesystem path; URL or packaged-jar StreamingAssets cannot provide
legacy external payloads. Rebinding the same data each frame does not reread it.
Rebaking a scenario stores its new data inline; older scenario references and
files remain intact for compatibility and Undo.

**Enable Lighting Scenarios** gates scenario authoring and playback. Disabling it
preserves saved results and uses live scene lighting. **Enable Lighting Scenario
Blending** gates blend previews and runtime transitions independently; selecting
a single baked scenario remains available. These two feature flags remain
independent of the removed streaming controls.

## APRTV response

The APRTV response uses center weighting and saturation preference during baking.
Captured RGB uses front-facing hit colors divided by all capture
weights; saturated surfaces can produce transfer values above one. Sky visibility
counts missing rays and front-facing hits beyond axial depth 90 above the world
horizon. Runtime relighting applies color saturation, direct transfer, bounce,
a directional-light visibility gate, first-order directional reconstruction and
floor compensation. Defaults are Indirect Intensity 0.56, Contrast 15, Sky Intensity 0,
Saturation Boost 1.49, Shadow GI Visibility 0.009, Sky Visibility Power 0.32,
Floor Boost 2.5 and Floor Power 2.

URP receives the APRTV contribution through albedo-times-emission composition.
Standard URP direct/ambient lighting continues to contribute. This response is
artistic: Indirect Intensity appears twice in the bounce calculation, direct
transfer is included, and upward-facing surfaces receive floor compensation.
APRTV GI samples the mesh normal; normal maps continue to affect standard URP
lighting.

In **Live Scene Lighting**, APRT sky lighting runs independently of directional lights. **Sky Intensity**
scales irradiance integrated from the scene's `RenderSettings.skybox`. APRTV uses
this single sky source; the old Baking Set cubemap field is retained only for
serialization compatibility and does not supply lighting. Changing the scene
skybox or its material settings relights the existing transfer without rebaking.
Skybox material exposure and tint belong to the source; scene Ambient Mode,
Ambient Intensity and the cached `RenderSettings.ambientProbe` do not supply or
scale APRTV sky lighting. APRTV Options Sky Intensity defaults to zero.
Historical bakes retain their serialized intensity.
Sky light works when directional lights are disabled. Indirect Intensity, the
directional visibility gate and Floor Boost affect the APRTV directional response
only; GI Intensity scales the combined receiver contribution.

## Sky irradiance transfer

The environment capture projects linear HDR **radiance** into nine L2 polynomial
coefficients. Geometry capture independently records a four-by-nine transfer
matrix per probe, accumulating the actual unoccluded rays with cubemap solid-angle
weights. It retains the relationship between environment direction and visibility;
an average visibility factor cannot distinguish a blocked bright region from an
open dark region. The legacy horizon and 90-unit depth classification do not
participate in this matrix.

At runtime the matrix maps environment radiance to the probe's first-order
**irradiance divided by pi**. This represents
`integral(skyRadiance(direction) * visibility(direction) * max(dot(normal, direction), 0) dOmega) / pi`
with L2 input and L1 output. Visibility is applied before the receiver's cosine
convolution; the input is not the already-convolved ambient probe. Receiver albedo
is multiplied once during material composition. A fully enclosed probe receives
zero sky contribution. The first-order output approximates detailed angular
response and can lose narrow openings or sharp lighting features.

This matrix adds 36 floats / 144 bytes per probe, about 0.63 MiB for 4,563 probes.
Earlier bakes remain usable for directional lighting but must be baked again to
add sky irradiance transfer. Changing the scene skybox, its material, or sky
intensity in **Live Scene Lighting** then relights the existing transfer without rebaking; source changes
refresh the environment cache on the next relighting camera.

By default this transfer models the directly visible sky. **Bake Sky One Bounce**
also traces static reflecting mesh surfaces and their visibility to the sky during
baking. It combines one diffuse wall reflection with direct sky in a colored
L2-radiance to L1-irradiance matrix. Runtime still performs one relight dispatch
and samples the existing sky SH buffer; it does not trace surfaces or add receiver
samples. Directional-light bounce remains part of its separate directional response.

Start with a small volume. **Surface Rays per Probe** controls the directions
traced to reflecting surfaces; **Sky Rays per Surface** controls the cosine-weighted
sky visibility integral at each hit. Defaults are 128 and 64. Higher counts reduce
noise and increase offline bake time. The one-bounce option is disabled by default
and takes effect after a complete bake. Geometry or reflecting material changes
require rebaking; in **Live Scene Lighting**, changing the scene skybox or Sky
Intensity relights the same matrix. **Baked Lighting Scenarios** keeps each
scenario's baked sky result until that scenario is baked again.

One-bounce reflection uses diffuse base color and the metallic workflow, including
base-map UV transforms, alpha clipping and double-sided materials. Surface color
is integrated once into the reflected radiance; receiver albedo is applied once
later. The bake uses mesh normals and does not include normal maps, emission,
specular reflections, additional wall bounces or moving geometry. Transparent
non-cutout surfaces are omitted. The initial implementation rejects terrain,
specular-workflow materials, MaterialPropertyBlock overrides and material maps
other than Texture2D; the previous successful bake remains available on failure.
The capture distance must enclose relevant geometry for both visibility rays.

RGB transfer contains 36 Vector3 values / 432 bytes per probe, about 1.88 MiB for
4,563 probes. It replaces the scalar matrix on the GPU, adding about 1.25 MiB.
The serialized bake also retains the direct scalar matrix for diagnostics. These
figures describe transport storage, not the complete resident volume allocation.

Visibility Leak Reduction is optional and disabled by default for APRTV. Enabling
it applies APRTV's validity/visibility moments and can alter the lighting response.
Reference Grid clamps sampling at its boundary; Adaptive uses bounded
brick lookup and its sampling domains. Adaptive layouts can therefore differ
from regular lattices even with the same transfer response.

## Capture and limitations

Capture submits private materials with a CommandBuffer and never changes the
active SRP asset or authored scene materials. APRTV capture copies BaseMap/MainTex
and BaseColor, uses default texture scale/offset and treats captured meshes as
opaque. Backface hit colors are
excluded. Custom shader, transparent/cutout and texture transform behavior is
therefore approximated by the capture material instead of evaluating the authored shader.
The Diffuse Transfer mode instead preserves texture scale/offset, alpha clipping,
Color fallback and double-sided behavior, excludes transparent non-cutout surfaces,
and captures solid-angle weighted conditional mean albedo with radial visibility.

Terrain capture supports holes, trees and eight diffuse layers. Heightfields
larger than 257 vertices per axis are resampled and extra layers are omitted with
warnings. Terrain-specific tint, height blending, normal mapping and custom
surface deformations are approximations. A bake is limited to 250,000 probes.
The configured capture far plane must include relevant geometry.

Geometry/material changes require rebaking. Neither model evaluates dynamic
shadow visibility at captured surfaces or handles point/spot light transport.
The first runtime dispatch initializes every loaded probe before sampling;
subsequent dispatches update those probes. Regions outside loaded probe data
use standard lighting.
GI Intensity zero, disabled APRT
and absent/invalid transport use the same fallback.

## Debugging APRTV

In **Rendering Debugger > APRT Volumes**, **Display view** selects normal Lighting,
Baked Transfer RGB, Sky Visibility, Probe Validity, Probe Coverage or GI Only.
Diagnostic views replace lighting on APRT-enabled receivers rather than add light
to the material. Background and other materials retain their own rendering; camera
postprocessing and separate Deferred fog/SSAO passes can still process the result.

To see the probes themselves, open **Probe display** and enable **Show Probes**.
The spheres use actual resident probe positions and do not require selecting a
volume or enabling Scene Gizmos. **Probe Size** is the sphere radius in world
units, and **Draw Distance** limits drawing around the camera. **Color Mode**
selects Positions (cyan), Relit Lighting, Validity, Baked Transfer RGB or Sky
Visibility. **Exposure** appears only for Relit Lighting and Baked Transfer RGB
and adjusts those visualizations for inspection. Hover **Color Mode** or
**Display view** for their mode descriptions.
Relit Lighting shows directional and sky SH at each probe without receiver albedo,
GI Intensity or floor compensation; it is not the final receiver appearance.
Validity and baked transfer modes inspect the probe's data directly, separately
from the interpolated receiver views selected by Display view.

Probe spheres appear in eligible Base Game and Scene cameras in Forward and
Deferred rendering. Overlay, Reflection, Preview, depth-only and cubemap cameras
do not draw them. Show Probes works with GI Intensity at zero while ordinary
surface lighting retains its normal fallback. The display defaults off; when off,
no probe-display pass is recorded and its resources are not created until needed.
Probe display does not provide pixel picking or contributing-probe selection.

Transfer RGB and Sky Visibility display unweighted trilinear samples of six-axis
baked means. APRTV transfer RGB is artistic transport and can exceed one.
Sky Visibility retains the legacy horizon/depth classification;
it does not display the new directional sky irradiance transfer matrix.
Validity interpolates the raw baked validity: red is below the displayed acceptance
threshold, green is accepted, and brightness follows validity from zero to one.
These views bypass receiver albedo, direct light, normal/visibility rejection,
GI gain and floor uplift. They remain available when GI Intensity is zero.
Coverage shows green inside the probe domain and magenta outside; raw diagnostic
views check geometric bounds before APRTV's normal lighting boundary clamp.
Magenta also identifies missing resident data. GI Only displays the normal APRT
receiver response before multiplying by receiver albedo. Zero GI gain is black.

Debugging never writes diagnostic values into the relit SH buffer or its temporal
history. Switching back to Lighting restores the ordinary composition immediately.
The panel's overview shows **Status**, **Baking Set**, **Loaded Baking Sets**,
**Resident probes**, **Loaded cells**, **Loaded scenarios** and
**Total GPU memory (MiB)**. **Baking Set** identifies the
focused set. Status, resident probes, the **Baked data**, **Sky lighting** and
**Probe validity** foldouts describe that focused set. **Loaded Baking Sets**
counts the registered sets; **Total GPU memory (MiB)** sums allocations across
all sets and the combined atlas. A focused set waiting for data does not imply
that other loaded sets are unavailable. Status identifies missing bake,
registration, loading or GI gain conditions. The collapsed **Baked data** foldout
shows the baked probe count, cell count and **Probe spacing** for Reference Grid;
probe spacing is hidden for Adaptive bakes. Relighting parameters are edited
through **APRTV Options** in a Volume Profile.

**Display view** and **Probe display** settings apply to the loaded sets together;
probe spheres draw each participating set's resident probes with those settings.

The collapsed **Sky lighting** foldout shows sky status and source. Status
identifies disabled intensity, a bake missing sky transfer, a missing source,
an uninitialized cache, and a ready sky. Source identifies the live scene skybox
or baked lighting scenario used by the last relighting camera. These queries
only inspect cached state and do not start sky captures or GPU readbacks.

The collapsed **Probe validity** foldout shows the acceptance threshold and
raw loaded probe count below it. Statistics are cached when loaded data or the
threshold changes. This query does not load data. The count remains a
raw validity diagnostic when Visibility Leak Reduction is off;
APRTV then ignores validity in normal GI weighting. Use Probe display for
individual probe spheres; the receiver views do not identify the eight
contributing probes for a picked pixel.

## Data and backend ownership

Inline transport format version 1 records the lighting model used during baking.
The optional `hasSkyIrradianceTransfer` extension identifies cells containing
36 sky transfer coefficients per probe; an older payload without this flag keeps
its directional-light response until rebaked. Sky matrix coefficients can be
negative, since the directional basis has both signs, but must be finite.
The optional `hasSkyBounceTransfer` extension additionally requires a valid direct
sky matrix and 36 finite RGB coefficients per probe in `skyIrradianceTransferRGB`.
It stays within format 1 and does not change old directional or scalar sky data.
Early format-1 bakes without a recorded model deserialize as Diffuse Transfer;
bake again to upgrade their semantics. Historical transport formats retain references
but require rebaking. Cell/brick indices, scene membership and data remain isolated
from APV. Clear APRT drops its payload/layout references. External bake generations
remain on disk so previous results can be restored by Undo.
Partial Baking Set bakes are unsupported. Frozen placement reuses compatible
probe positions and brick layout. Lighting scenarios store per-probe directional
and sky SH plus a layout compatibility signature; older captured presets remain
available for migration and legacy runtime playback; Bake APRT produces the
per-probe results required by the new scenario authoring workflow.
