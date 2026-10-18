# Fraxinus Light Transport

[English](README.md) | [简体中文](README.zh-CN.md)

Version `1.0.1` · Unity `2022.3` · Package ID `com.fraxinus.rendering.light-transport`

GPU ray queries, acceleration structures and sampling tools for lighting calculations and geometry queries.
On Unity 2022, compute shaders build and traverse BVHs without requiring DXR or hardware ray tracing.

- **Acceleration structures**: mesh BLAS and instance TLAS, with instance addition/removal, transform updates, masks and face culling.
- **Ray queries**: closest-hit queries return distance, instance, triangle and barycentric coordinates; any-hit queries test occlusion.
- **GPU dispatch**: direct and indirect dispatch with consistent bindings for acceleration structures, parameters and input/output buffers.
- **Sampling tools**: Sobol, blue noise and quasi-random sequences for custom integration and sampling tasks.

The library can be used independently of Core / URP. Its C# interfaces are currently `internal`, so integration requires assembly access.
Scene extraction, lighting integration and data publication for probe baking are implemented in the matching
[Fraxinus Core](../Fraxinus-RPCore/README.md) package.

Source: [ray tracing context](Runtime/UnifiedRayTracing/RayTracingContext.cs),
[instances and acceleration structures](Runtime/UnifiedRayTracing/IRayTracingAccelStruct.cs),
[ray queries](Runtime/UnifiedRayTracing/TraceRay.hlsl),
[dispatch interface](Runtime/UnifiedRayTracing/IRayTracingShader.cs) and [sampling library](Runtime/Sampling).

Validation: [Compute GPU Smoke](Tests~/GpuSmoke/README.md).

License: [Unity Companion License](LICENSE.md); see [third-party notices](THIRD%20PARTY%20NOTICES.md) for other components.
