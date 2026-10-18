# APV on Unity 2022

This embedded Core package uses the standalone APV baker on Unity 2022. The
Unity 6 `LightTransport` delegate remains guarded by
`UNITY_6000_0_OR_NEWER`; the standalone path uses the Fraxinus compute ray
tracer and the shared APV post-processing code instead of engine-owned probe
submission APIs.

APV owns its probe lighting, placement, virtual offset, sky occlusion,
rendering-layer masks, cell finalization, and serialization. It never submits
additional probe positions to Unity's Progressive Lightmapper and does not
subscribe to Unity's bake-started, bake-completed, or additional-probe events.
Cancellation disposes the standalone jobs directly and preserves the previous
cell data.

The supported workflow is:

1. Add both local packages from the project manifest.
2. Configure the standalone APV samples, bounce count, and environment
   intensity through `AdaptiveProbeVolumes.standaloneBakeConfiguration` when
   needed.
3. Enable APV virtual offset, sky occlusion, or rendering layers only when the
   corresponding Compute resources are available.
4. Start **Bake Probe Volumes** from the APV window. **Generate Lighting**
   remains the separate Unity scene-lightmap action and does not bake APV.

The standalone Compute ray-tracing layer has been validated on Unity
2022.3.62f3 with a GTX 1650 over Direct3D 11. A 374-probe APV bake completed,
published all cell payloads, and reloaded successfully. Cancellation returned
without entering Unity's lightmapper and released the standalone jobs.

The auxiliary GPU test also passed: 374 probes completed through the standalone
state machine with no native lightmapping bake observed and no shader errors.
Its source and run instructions are in `Tests~/Unity2022APV/`.
