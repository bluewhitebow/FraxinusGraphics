# Unity 2022 APV auxiliary GPU smoke

## Native probe sampling texture import contract

Copy `StandaloneAPVSamplingImportSmoke.cs`, `.compute`, and `.json` into
`Assets/Editor` in the test project. Run
`-executeMethod StandaloneAPVSamplingImportSmoke.Run` with a graphics device,
or create `Temp/StandaloneAPVSamplingImportSmoke.request` in an open editor.
The smoke reads 145 actual GPU `Texture2D.Load` values from the Sobol sequence,
256-SPP ranking, and scrambling textures. Its fixed expected bytes come from
Unity 6000.0.31f1's native `GI/SamplingData/bluenoisedata`; the fixture records
that resource's SHA-256 and does not require a Unity 6 installation to run.

Unity imports these PNGs bottom-up, while the native tables are top-down.
The production baker must therefore bind `_NativeProbeSamplingFlipY = 1`.
All 145 bottom-up byte expectations must match exactly; failures identify an
importer, texture-content, texture-format, or platform row-order change.
The test does not modify a scene or start a bake and reports to
`Logs/standalone-apv-sampling-import.json`.

This smoke checks the import contract, not the baker's parameter binding or
complete transport. Also run a focused probe bake before and after sampling
changes with identical probe indices, 128 direct / 1024 indirect and environment
samples, and two bounces. Compare raw SH coefficients and validity at the same
probe positions, then verify the full scene; changing probe-array order changes
the Cranley-Patterson rotation and is not a valid isolated comparison. The
original bug bound zero despite the import contract, changing the first 256
continuation and next-event samples. Increasing the sample budget diluted that
error without repairing the sequence.

Copy `APVAuxiliarySmoke.cs` into an Editor assembly in an isolated Unity 2022
project that references this Core package, URP, and the Fraxinus light-transport
package. Run:

```text
Unity.exe -batchmode -force-d3d11 -projectPath <project> -executeMethod APVAuxiliarySmoke.Run -logFile apv-auxiliary.log
```

Do not use `-nographics` or `-quit`. The test creates a small APV scene and
steps only virtual offset, sky occlusion, and rendering-layer jobs. It never
starts Unity native lightmapping. The JSON report proves that all three jobs
use `ComputeRayTracingAccelStruct`, write finite GPU readback data, compile the
APV compute shaders, and release their resources. A reference run on Unity
2022.3.62f3 / GTX 1650 / D3D11 produced 374 probes and passed all three stages.

## Standalone bake and clear regression

Copy `StandaloneAPVSmoke.cs` into the isolated project's Editor folder and run
`APVStandaloneSmoke.Run` using the same Unity command above. Set `APV_CLEAR=1`
in the process environment to run the clear regression. `APV_SAMPLES=16`,
`APV_DIRECT_SAMPLES=1` and `APV_BOUNCES=1` keep the small test scene inexpensive.
Set `APV_DISK_STREAMING=1`
to repeat with disk streaming enabled. Set `APV_TEXTURES=1` to include textured
opaque geometry and an alpha-clipped panel in lighting, virtual offset and sky
occlusion, including the bake/clear/rebake sequence.

The test bakes real cell files, invokes the Lighting tab's clear action with a
different active scene, no cached baker, and APV disabled, then verifies file
and metadata deletion, zero size in the summary, detached scene references,
and preservation of another Baking Set and regular lightmaps. It also covers
repeated clear, rebaking, cancellation and progress cleanup when clearing
during a bake, and clearing with an uninitialized runtime. Frames are rendered
after clearing to detect delayed streaming or bake updates. Results are written
to `apv-standalone-result.json` in the test project.

Add `APV_PREPARE_REENTRY=1` to first exercise a virtual-offset override that
attempts to start another bake from its initializer, then fails deliberately.
The test requires the nested start to be rejected, the provider to retain the
preparing owner, and all prepared resources and progress state to be released.
It restores the override and continues the normal bake/clear regression to
verify recovery. Only the exact injected preparation exception is expected.

Set `APV_PERSISTENCE_SMOKE=1` and leave `APV_CANCEL` unset to run the light-metadata
persistence regression through the same `APVStandaloneSmoke.Run` entry point.
After a complete bake, it saves, unloads and reopens the generated scene, checks
that Baked light output is restored without a native Lighting Data Asset, then
clears APV and verifies restoration of the original light output and deletion
of the payload files. This mode takes precedence over `APV_CLEAR` and writes
the same `apv-standalone-result.json` report. Run only in the disposable batch
project described above.

Set `APV_REBAKE_STREAMING=1` and leave `APV_CANCEL` unset to run consecutive
rebakes without Clear. This mode automatically enables disk streaming and takes
precedence over persistence/clear modes. `APV_REBAKE_ROUNDS` defaults to 3 (range
3–8); `APV_SAMPLES=16 APV_DIRECT_SAMPLES=1 APV_BOUNCES=1 APV_TEXTURES=1` keeps the fixture inexpensive.
Before and after each rebake it evicts GPU residency and renders until a fresh
disk-streamed cell is uploaded. During baking it renders and exercises the real
streamable assets' native file handles: odd rounds retain open handles, even
rounds call CloseFile immediately after OpenFile without waiting for the open
job or close fence. Manual close/dispose paths first drain runtime read requests;
the pending-open race is exercised without closing files underneath live reads.
The report records how often an actually pending open was
observed; a zero count does not claim pending-open coverage.

Every rebake changes the Baked sun intensity and checks that SH payload bytes
change, all file lengths match their cell descriptors, imported TextAssets match
disk bytes, GUIDs survive, imports settle, and disposed payloads permit exclusive
read/write access. AccessDenied, import-loop messages and excessive repeated
imports fail the test. Finally it streams again, exercises immediate open/close,
and calls Clear followed by Bake in the same editor tick, with no intervening
yield or manual disposal, to catch Windows delete-pending handles preventing
recreation of the payload files. Progress markers `APV_DIRECT_REBAKE_BEGIN/COMPLETE` identify
the round if Unity's importer blocks its editor loop. Keep an external process
timeout because a native import loop can prevent the in-editor timeout from
running. Results use `apv-standalone-result.json`, including per-round evidence.

Set `APV_PUBLICATION_FAILURE=1` and leave `APV_CANCEL` unset to test rollback
through the real standalone state machine. This mode enables disk streaming and
takes precedence over the other smoke branches. After the first successful bake
it snapshots every payload hash/.meta/GUID, baked metadata, cell descriptors,
scene cell lists and light output. It changes the requested probe grid and sun
intensity, holds a read-sharing lock on the last support payload, and bakes again.
Only I/O errors naming that exact payload from the publication method are
expected; rollback errors and unrelated errors still fail. The test verifies
that publication failure stops the driver, preserves the full previous bake,
and allows the old cells to stream before releasing the lock. It then completes
an unlocked bake and verifies publication of the new requested grid and SH.

Add `APV_DILATION=1` to enable dilation on the generated Baking Set with distance
1 and two iterations. The failure/recovery test checks that disk-streaming
override, memory budget and SH bands return to their previous values after both
failure and successful publication/dilation. Example environment:
`APV_PUBLICATION_FAILURE=1 APV_DILATION=1 APV_TEXTURES=1 APV_SAMPLES=16 APV_DIRECT_SAMPLES=1 APV_BOUNCES=1`.
The usual report includes `publicationRollbackVerified`,
`publicationRecoveryBakeVerified`, and a separate `expectedPublicationErrors`
array; unexpected errors remain in `errors` and fail the test.

## Standalone material GPU regression

Copy `StandaloneAPVMaterialSmoke.cs` into the isolated project's Editor folder,
set `APV_MATERIAL_SMOKE=1`, and run `APVStandaloneMaterialSmoke.Run`. It compares
actual irradiance SH for non-readable base/emission textures, mesh UVs, tiling,
offset, wrapping, sRGB decoding, HDR emission, emission keywords, and alpha-clipped
primary/shadow rays against opaque controls. Separate albedo and emission cases
hold UV0 on red texels while adding and changing an independent lightmap UV2
channel; irradiance must remain unchanged. The fixture explicitly uses Baked
Indirect to prevent a project's Subtractive default from adding direct light,
and enables hard shadows for the cutout/opaque comparisons. A final control
disables light shadows and must restore the unblocked reflected sunlight.
Results are written to
`apv-standalone-material-result.json`.
Run in both Gamma and Linear color spaces to cover texture upload conversion.

The UV0 regression run completed 28 measurements and 10 check groups with
`passed=true`, no errors, `nativeBakeObserved=false`, and all resources disposed.
This validates the isolated material fixtures; it does not establish complete
scene parity with the native baker.

The standalone baker evaluates opaque materials through their Meta pass on a
unit quad. The resulting albedo/emission maps are in material UV0 space and
are sampled with the original mesh UV0; lightmap UVs are not used or normalized.
Meta applies authored texture transforms while producing these maps, so sampling
must not apply those transforms again. Materials without a usable Meta pass and
alpha-clipped materials use the source textures and UV0. URP Lit and Standard
emission use the base map transform on that source-texture path. Opaque alpha is ignored;
cutout uses the material cutoff, including in virtual offset and sky visibility.
Alpha-blended transmission and normal mapping are not implemented.

Texture copies use linear RGBAHalf, up to 256 pixels per side and a 128 MiB
budget per material resource set; all passes plan the resolution from the same
scene GI materials, decreasing it when needed to fit the budget. This is an
indirect-light texture approximation, so very fine
cutouts can lose detail. Source assets and import settings are never modified.
Visibility-only passes upload only cutout textures.

## Unified ray-tracing shader import regression

Copy `UnifiedRTImportSmoke.cs` into the isolated project's Editor folder, set
`APV_IMPORT_SMOKE=1`, and run `APVUnifiedRTImportSmoke.Run` with a graphics device.
The test imports a shader while its include files are missing, creates the files,
then changes a nested include. It uses ordinary AssetDatabase refreshes and GPU
readback to verify that the shader recovers and its compiled output changes,
without explicitly reimporting the shader. It also checks the kernels of the
APV lighting, virtual-offset, sky-occlusion and rendering-layer shaders.

## Standalone radiometry regression

Copy `StandaloneAPVRadiometrySmoke.cs`, `APVAnalyticGradientSky.shader`, and
`APVAnalyticLambert.shader` into the isolated project's Editor folder, set
`APV_RADIOMETRY_SMOKE=1`, and run `APVStandaloneRadiometrySmoke.Run` with D3D11.
The report `apv-standalone-radiometry-result.json` checks absolute constant-field
energy on CPU and GPU, directional SH rotation, Baked direct versus Mixed
indirect transport, zero and one reflection, indirect intensity, single-sided
backface validity, Realtime exclusion and batch-size invariance. A constant
emissive enclosure verifies that visible emission survives a zero-bounce bake
and is counted once when indirect integration is enabled. A constant environment
with distant geometry checks that the emission visibility rays do not duplicate
the direct sky contribution. A white floor, single-sided backface roof and four
black closing walls verify that subsequent backface hits terminate without
injecting sky at zero, one or two bounces. An open-floor control verifies that
true reflected misses still collect sky radiance.

Four gradient-sky measurements isolate diffuse continuation from primary sky
by subtracting zero-bounce results from one-bounce results. A known linear
reflectance of 0.5 and sky `L(w)=(0.4+0.3*w.y,0.4,0.4-0.3*w.y)` give the
geometric Lambert integral `rho*(A+2B/3)`. The square's exact solid angle gives
reflected L0 `(0.14865,0.09910,0.04955)`, independently of the baker's sampler.
Tilting only authored vertex normals by 60 degrees must preserve those values
and all SH coefficients for a fixed seed. This tests the geometric diffuse
transport contract used by the installed RadeonRays baker, rather than making
a claim about every possible shading-normal model.

The directional reference is an analytic clamped-cosine polynomial with the
direct SH band window, independent of the baker's projection code. Soft-light
checks cover mean-energy agreement between CPU and GPU, blocked visibility,
and preservation of the original directional cosine during diffuse reflection.
An unoccluded grazing receiver must give identical hard/soft results when the
entire shadow disk is above its horizon. A sun just below the receiver horizon
must remain dark even when some jittered shadow rays point upward. Mixed-light fixtures
explicitly use `MixedLightingMode.IndirectOnly` and disable their temporary
lights and geometry before subsequent cases. The material regression uses a
Mixed light and one bounce to isolate material transport from probe direct
illumination.

A Unity 2022.3.62f3 / GTX 1650 / D3D11 run passed all 86 measurements in 23
check groups with 256-sample GPU dispatches, no errors and all resources disposed.
The four gradient measurements failed before the continuation-normal fix.
The following directional-light cases also detect a sky capture changing the
global linear-intensity setting before light extraction. These transport regressions
do not by themselves establish whole-scene visual parity with a Unity 6 bake.

`dotnet run --project Deringing/Verify.csproj -c Release` runs the independent
post-processing regression with the .NET 9 SDK. It links the production C#
helper and checks preservation of DC, separate L1/L2 band windows shared across
RGB, and reference coefficient cases. The helper and regression run without
Unity's native deringing or baking APIs. Run from a temporary directory to keep
its `result.json` separate from source files.

## Payload publication transaction regression

Copy `APVBakingPublicationSmoke.cs` into the disposable project's Editor folder,
set `APV_PUBLICATION_SMOKE=1`, and run `APVBakingPublicationSmoke.Run` in batch
mode. The `apv-publication-result.json` report contains eight checks for staging,
a real lock on a later payload, partial-publication rollback, repeated/dilation
publication, removal of newly published assets, and stable .meta bytes/GUIDs.
This test isolates file transactions; the `APV_PUBLICATION_FAILURE` mode above
covers Baking Set metadata rollback and the full GPU bake state machine.

## Unity 2022 APV window shutdown regression

Copy `APVWindowShutdownSmoke.cs` into the disposable project's Editor folder,
set `APV_WINDOW_SHUTDOWN_SMOKE=1`, and run `APVWindowShutdownSmoke.Run` with
`-batchmode -force-d3d11`. Do not pass `-quit`: the test opens the actual Probe
Volumes window, closes it normally, reopens it, and requests editor exit while
the window remains open. The report `apv-window-shutdown-result.json` must show
all lifecycle flags true and no errors. Also require process exit code 0 and
check the complete Unity log for native fatal errors, including
`GetManagerFromContext` and a missing `LightmapSettings` manager. The JSON alone
cannot establish success because Unity tears down its native managers afterward.

## Sponza GI image capture

Temporarily copy `APVParityImageCapture.cs` into `Assets/Editor` in the Sponza
project. It uses public URP APIs shared by Unity 2022 and Unity 6. In an idle
interactive editor with Sponza open, write a unique label to
`Temp/APVParityImageCapture.request`. In batch mode, run
`-executeMethod APVParityImageCapture.Run` with a graphics device and without
`-quit`; this opens `Assets/Scenes/Sponza.unity`. Optional environment variables
`APV_CAPTURE_OUTPUT` and `APV_CAPTURE_LABEL` select the output folder and label.

The tool captures the Global Illumination debug view from a fixed camera pose,
with post processing disabled, after APV data finishes streaming. It writes a
linear float EXR, a clipped sRGB PNG preview, and a text report to
`.tmp/apv-images`. A pass requires loaded APV data, the correct debug shader
state, finite RGB values and a nonblack image. It does not establish visual
parity with a reference bake; compare matching captures separately.

Capture owns a disabled temporary camera and restores the active render target
after every render. It unbinds and destroys that camera before releasing its
texture, including on failure, assembly reload and editor exit. It restores
debug settings and render scale, and never saves scene or lighting assets.
Remove the copied script and its `.meta` after validation; the source in this
`Tests~` folder is deliberately excluded from normal Unity compilation.
