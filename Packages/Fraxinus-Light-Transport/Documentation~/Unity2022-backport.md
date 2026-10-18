# Unity 2022 compatibility

The Unity 6 package relies on editor and runtime APIs that were added after
Unity 2022:

- `UnityEditor.Embree.GpuBvh` is unavailable in the 2022 editor. The CPU BVH
  builder is therefore excluded and the RadeonRays compute builder is used.
- Unity 2022's hardware ray-tracing API has different acceleration-structure
  types and method signatures. The Unity 6 hardware implementation is excluded
  from 2022; the Compute backend is selected instead.
- Unity 2022 cannot create a ray-tracing shader asset with
  `ShaderUtil.CreateRayTracingShaderAsset`. `.urtshader` assets import their
  ComputeShader representation on 2022.

Resource paths use the package name
`com.fraxinus.rendering.light-transport` throughout the package.
