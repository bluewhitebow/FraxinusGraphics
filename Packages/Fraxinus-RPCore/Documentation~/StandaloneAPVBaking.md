# Standalone APV baking (Unity 2022 backport)

The editor API `AdaptiveProbeVolumes.BakeStandaloneAsync()` starts the only APV
bake path. It drives placement, virtual offset, sky occlusion, rendering
layers, GPU lighting, cell post-processing and serialization from the editor
update loop. `AdaptiveProbeVolumes.Cancel()` cancels that session
and keeps the previous cell data.

The baker traces static `MeshRenderer` geometry through the Fraxinus compute ray
tracer and writes Unity L2 spherical harmonics plus validity in bounded probe
batches. It supports textured opaque and alpha-clipped materials, emission,
directional/point/spot baked lights, direct lighting, and indirect bounces. Mixed lights are
accepted in `IndirectOnly` mode; mixed-light occlusion output is currently
zero. Unsupported alpha-blended contributing materials are rejected
during the preflight check.

SH coefficients use Unity's polynomial irradiance-over-PI convention, including
the basis normalization factors. Baked lights contribute direct incident light
at each probe; Baked and Mixed lights both contribute at the first reflected
surface. A bounce budget of zero includes the constant environment and Baked
direct lights and visible emissive surfaces, but no diffuse reflections. The
direct pass traces visible emission with its own sample budget and applies the
direct SH window; indirect paths exclude this first emission segment so it is
counted once. Light indirect
intensity scales reflected lighting once; it never scales probe direct light.
Punctual light energy includes URP's PI compensation. Single-sided backfaces
terminate paths without collecting environment light. Only primary backface
hits contribute to probe invalidity; double-sided GI materials reflect on both
sides. Soft directional shadows sample a disk for visibility, while diffuse
reflection uses the original light direction for its cosine. Shadow jitter
does not illuminate a surface that faces away from that direction.
Primary rays use Unity 6's audited Sobol plus Cranley-Patterson sequence, with
the same deterministic MT19937 shifts and the bundled 256-sample blue-noise
tables for diffuse continuation. The request-local probe index is kept stable
across GPU batches, so changing batch sizes does not change the sampling
pattern. The direct-light sampler still uses the standalone hash stream; this
keeps the transport change explicit instead of claiming complete finite-sample
native parity.
Diffuse continuations sample the triangle's geometric hemisphere; interpolated
vertex normals evaluate the direct-light cosine. This prevents smoothed normals
from sending reflected paths through their own surface and losing energy.
GPU integration advances in slices of at most 256 samples and 256 probes per
dispatch. Each slice retains the global probe and sample indices and contributes
its fraction of the full sample budget. Higher quality therefore schedules more
short steps instead of lengthening one kernel. Readbacks carry a dispatch marker;
incomplete or stale GPU output fails the bake instead of publishing black data.
The standalone configuration keeps the primary/indirect sample count (`samples`)
separate from the direct-light budget (`directSamples`); the defaults are 1024 and
128 respectively, matching the Unity 2022/Unity 6 LightingSettings budgets for
Sponza-style bakes (256 environment/indirect and 32 direct samples, each with a
light-probe multiplier of 4).

Before publication, standalone post-processing preserves the mean (DC) energy
and reduces SH ringing with two stages of band windowing. Each probe uses one
L1 factor and one L2 factor shared across its RGB channels. The filter aligns
the SH coefficients with the L1 axis, filters the zonal part formed by DC, L1
and zonal L2, then filters a bound that includes the remaining azimuthal bands.
Sharing each band factor across RGB keeps
the filter from changing the relative color of that band. This is an independent
C# implementation; it does not call Unity's native deringing or baking APIs.
Subdivision seam repair then interpolates the processed coefficients using the
original placement grid. Both stages run during baking and add no runtime
shader work.

After successful publication, per-scene APV data records each participating
light's baking output and restores it on scene load. This prevents Baked lights
from also contributing real-time direct illumination. Clear restores the
previous light output; failed and cancelled bakes do not publish new light
states. Save the scene normally to persist these references. No Lighting Data
Asset or native lightmapping callback is required.

This is still a diffuse baker without the full Unity lightmapper's denoising.
When a skybox is assigned, the baker renders the active skybox into explicit
linear HDR faces and binds a temporary cubemap for every environment miss. The
same capture is projected to Unity's L2 spherical-harmonic basis as a fallback,
so a failed cube allocation does not abort a bake. It applies
`RenderSettings.ambientIntensity` and does not consume a cached
`RenderSettings.ambientProbe`, which may belong to a stale Lighting Data Asset.
`AmbientMode.Trilight` uses deterministic spherical quadrature, while Flat mode
projects the linear ambient color into the DC coefficient. Primary environment
samples are integrated with the direct component and receive the same direct
SH window as baked direct lights. True environment misses after a surface bounce,
and emission reached after that first bounce, are integrated as indirect
transport. The direct environment pass uses a luminance CDF for skybox importance
sampling. Light cookies, area lights and normal maps are not implemented.

APV does not register Unity lightmapping callbacks and does not use Unity's
additional-probe submission API. The Probe Volumes tab's primary **Bake Probe
Volumes** button and its matching menu action call `BakeStandaloneAsync`.
**Generate Lighting** in Unity's Lighting window runs the regular lightmap bake
for scene lightmaps; that action does not start APV. APV progress and cancellation
only control the standalone bake.

The native baking backend, `LightProbes` fallback, `LightingBaker` override, and
`BakeAdditionalRequest` / `BakeAdditionalRequests` APIs have been removed.
Call `BakeStandaloneAsync` to rebake the Baking Set; the Adjustment Volume
inspector's **Rebake Baking Set** button does the same. Reflection normalization
requests are still calculated during a full standalone bake. This fork is no
longer source-compatible with the partial additional-baking API used by HDRP 17's
`HDBakedReflectionSystem`.

Adjustment Volume overrides for direct/indirect samples, sample multiplier and
lighting bounces are no longer serialized; old scene values are ignored.
Standalone lighting samples and bounces come from `StandaloneAPVBakeConfiguration`
for the whole bake. Per-volume sky occlusion samples and bounces remain supported.

Use **Clear APV Data** in the Probe Volumes tab's bake menu to delete the selected
Baking Set's cell files and scenario data. This works after an editor reload and
while APV is disabled, without starting a bake. It cancels any running standalone
bake before clearing, unloads the set's runtime cells, and leaves scene lightmaps
and other Baking Sets intact. The Baking Set is saved immediately; loaded scenes
whose APV references changed are marked dirty for the next scene save. Baking
settings and the list of scenes in the set are retained for the next bake.
