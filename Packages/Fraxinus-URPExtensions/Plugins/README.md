# Native rendering plugin: source and build status

This directory contains retained Apple native-plugin artifacts and their source:

| Path | Contents |
| --- | --- |
| `source/RenderingPlugin.cpp`, `source/RenderAPI.cpp`, `source/RenderAPI_Metal.mm` | Unity plugin entry points and Metal implementation |
| `source/Unity/` | Unity native-plugin interface headers |
| `source/gl3w/` | OpenGL loader sources and notices |
| `iOS/libRenderingPluginIOS.a`, `iOS/RegisterPlugin.mm` | iOS static library and Unity application-controller registration |
| `MacOS/RenderingPlugin.bundle` | macOS plugin bundle, including its native executable |

The package includes source as well as binaries, but **a reproducible build has
not been established**. No complete Xcode project, CMake configuration, pinned
Apple toolchain or verified binary-to-source comparison is supplied. The notes
below describe the inputs to a future build, not a tested build recipe. Do not
treat the shipped binaries as proof of iOS or macOS compatibility.

## Current behavior and importer settings

The URP selector currently maps MetalFX Spatial/Temporal and DLSS to Linear.
Keeping this plugin in the package does not enable those upscalers. The tested
Windows D3D11 rendering path does not use these Apple artifacts.

The iOS library importer is enabled for iOS and declares `Metal;MetalFX` framework
dependencies. The macOS bundle importer is enabled for macOS Editor and Player.
Thus the artifacts can participate in Apple import/builds even though the
upscaling selector falls back. Neither Apple build nor runtime behavior was
validated by the Unity 2022.3.62f3 Windows Editor tests.

The Metal source uses APIs annotated for iOS 16 and macOS 13. Those annotations
do not establish the shipped binaries' architectures or deployment targets;
inspect the actual artifacts before selecting an Apple target.

## Inputs and steps for a future rebuild

1. Use macOS with Xcode and Apple SDKs that provide Metal and MetalFX. Record the
   exact Xcode/SDK versions, target architectures and deployment targets.
2. Create a native bundle target for macOS or static-library target for iOS.
   Include `RenderingPlugin.cpp`, `RenderAPI.cpp` and `RenderAPI_Metal.mm`, plus
   the selected backend's sources and headers. Set include paths for `source`
   and its Unity headers. Check `PlatformBase.h` and target defines explicitly;
   this directory is not a complete cross-platform build project.
3. Link the Apple frameworks used by the Objective-C++ code, including Foundation,
   Metal and MetalFX. Review API availability and memory-management settings
   against the selected SDK instead of assuming the existing binary's settings.
4. Export Unity's plugin entry points. For iOS, integrate `RegisterPlugin.mm`
   with the generated Unity Xcode project and its `UnityAppController` headers.
   It declares an application-controller subclass; reconcile it with any
   existing subclass rather than registering two application controllers.
5. Inspect architectures, linked frameworks, minimum OS versions and exported
   symbols using Apple tools such as `file`, `lipo`, `otool` and `nm`. Package the
   macOS executable in a valid bundle. Preserve or deliberately update the Unity
   `.meta` importer settings and record hashes for any replacement artifacts.
6. Verify import, native linking, plugin load/unload and actual rendering in both
   an Apple Editor/Player configuration and the intended iOS device build.
   Confirm the managed upscaler's selected path separately; successful plugin
   compilation alone does not activate MetalFX in this release.

Until those steps have been completed and recorded, Apple native-plugin builds
and runtime support remain unverified. See the [installation and support
guide](../Documentation~/Installation.md) for the scope of the existing tests.
