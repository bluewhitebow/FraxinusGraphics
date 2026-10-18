# Compute GPU smoke test

`LightTransportSmoke.cs` is an isolated, low-level validation for the Unity
2022 Compute RadeonRays backend. It is deliberately not an APV bake test: it
does not create probe volumes, extract APV geometry, integrate lighting, or
write lighting data. The test only validates the portable Unified Ray Tracing
layer used by an APV delegate.

The files live under `Tests~`, so Unity does not import them as part of the
normal package. To run the test, copy this directory's files into an isolated
Unity project's `Assets/Editor/FraxinusLightTransportSmoke/` directory. The
target project must already reference this package as
`com.fraxinus.rendering.light-transport` and must have the package's Runtime
assembly available. Keep the copied assembly definition named
`Unity.LightTransport.Editor.Tests`; the package Runtime assembly grants this
friend assembly access to its internal Unified Ray Tracing API.

Run the editor method:

```text
Unity.exe -projectPath "<isolated-project>" -executeMethod Fraxinus.LightTransportSmoke.Run -logFile gpu-smoke.log
```

Use a real graphics device and a graphics API with compute shader support. Do
not pass `-nographics`. In batch mode the method performs synchronous GPU
readbacks and calls `EditorApplication.Exit(0)` on success or
`EditorApplication.Exit(1)` on failure; do not add `-quit`, since the method
owns the exit after writing its report. In an interactive editor, the method
returns after logging the result and throws if it failed.

The result is written to `fraxinus-light-transport-smoke.json` in the project
working directory and a log line has the form:

```text
FRAXINUS_GPU_SMOKE PASS device=<device> rays=<count> report=<path>
```

The test first imports the `.urtshader` and all Compute backend resources and
rejects shader compiler errors. It then builds a triangle and transformed cube
on the GPU, dispatches rays through `Graphics.ExecuteCommandBuffer`, and reads
the result buffers back from the GPU. It checks triangle/cube hit and miss,
user instance IDs, primitive index and barycentrics, hit distance, instance
masks, back-face behavior with culling disabled, maximum ray distance, instance
transform rebuild, and instance removal rebuild. A poisoned output buffer is
used so a skipped dispatch cannot appear as a valid miss.

The test does not prove APV integration, probe-volume scheduling, bake
progress/cancellation, or lighting quality. It also does not validate the
Unity 6 hardware ray-tracing backend, editor Embree path, or native Unity
LightTransport APV delegate. A successful run only proves that this package's
Unity 2022 Compute ray-tracing primitives execute and read back correctly on
the selected GPU.

Reference run (Unity 2022.3.62f3, Direct3D 11, NVIDIA GeForce GTX 1650):

```json
{"passed":true,"computeResources":9,"verifiedRays":12}
```
