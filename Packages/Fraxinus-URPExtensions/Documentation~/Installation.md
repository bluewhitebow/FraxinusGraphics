# Compatibility and validation scope

Fraxinus is an independently maintained fork of Unity's rendering packages, not
an official Unity release. The tested configuration is **Unity 2022.3.62f3,
Windows, D3D11, NVIDIA GeForce GTX 1650**. Package manifests target Unity 2022.3;
that declaration does not mean every patch version or platform has been tested.

## Package compatibility

Core, URP and Config use version **14.0.99**, with Light Transport **1.0.1**.
ShaderGraph remains at **14.0.12**. This same-version patch updates documentation;
package versions, dependencies, and runtime/editor implementation are unchanged.

## Migration and platform limits

Fraxinus URP runs **RenderGraph only**. Custom passes must implement
`RecordRenderGraph(RenderGraph, ContextContainer)` and declare their resource
use. Old `Execute` / `OnCameraSetup` implementations do not become compatible by
disabling RenderGraph. The shared Core package also contains newer APIs; this
release is not a general drop-in replacement for arbitrary URP 14 or HDRP
projects. See the [URP README](../README.md) for feature-specific limitations.

APRT needs compute shaders and structured buffers. It loads all cells belonging
to loaded scenes synchronously; the first load, increased resident memory,
scene changes and resource rebuilding still have costs. Sky changes can require
synchronous GPU readback. Geometry and material changes require rebaking.
The Unity 2022 Light Transport backend uses compute ray tracing, not DXR or
Unity 6's engine-owned native APV integrator. Native Unity 6 GPU Resident Drawer
is unavailable on Unity 2022.

Apple platforms, Android, WebGL, XR, other graphics APIs, Player and IL2CPP are
not covered by the Windows Editor validation below. Some features explicitly
exclude these configurations. URP includes Apple native-plugin binaries and
source, but rebuilding those binaries and matching them to the included source
has not been verified. The iOS importer is enabled and links Metal/MetalFX; the
macOS bundle importer is enabled for macOS Editor and Player. These importer
settings are not support guarantees. MetalFX and DLSS selections currently fall
back to Linear. See [native plugin build notes](../Plugins/README.md).

## What was validated

Validation for this same-version **14.0.99 / 1.0.1** documentation patch covers
documentation formatting, unchanged package dependencies, bilingual README links,
archive contents and SHA-256 checksums.
The rendering tests below are historical results and were not rerun for this patch.

The original **14.0.99 / 1.0.1** release packages were imported and compiled together in the
tested editor. **251 selected Editor tests passed with no skips**. That count
includes six tests from the optional, separately maintained GPU Batcher; it is
not the total coverage of every feature, platform or package. The Config test
is a placeholder, so package compilation and integration provide its evidence.
Fixtures under `Tests~` require explicit setup and do not run on normal import.

A separate GPU render regression exercised two independently lit rooms across
343 frames and 11 stages, including scenario weights and unload/reload. A
Profiler capture with positive allocation controls measured **0 B GC on the
camera's main thread for 32 warmed frames** in the tested scene. That capture
also used the independent GPU Batcher fix `fc43eec`, which is not part of these
four releases. It does not establish zero GC for the entire editor, all threads,
dynamic scene changes, arbitrary projects or Player builds. The accompanying
lighting-collection CPU measurements are local microbenchmarks, not a general
frame-rate comparison with official URP.

When reporting a problem, include the Unity patch version, four package
versions or commits, graphics API and GPU, render path, platform, relevant
Console output and a minimal reproduction. For performance reports, identify
the measured scope and distinguish initialization from repeated frames.
