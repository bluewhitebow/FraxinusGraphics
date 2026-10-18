# Standalone APV deringing regression

Run `dotnet run --project Verify.csproj` from this directory. The test compiles
the production helper directly and requires no Unity installation, native
library, GPU, or bake. It writes a short `result.json` report.

`NativeUnity6000Fixtures.json` contains 16 independently captured input/output
pairs from Unity **6000.0.31f1**, using
`WintermuteContext.DeringSphericalHarmonicsL2Internal` with one probe per call.
The assembly MVID and exact native signature are recorded in the fixture.
Expected outputs must not be regenerated from the standalone implementation.

Each array is 27 floats in channel-major order: `R[0..8], G[0..8], B[0..8]`.
Within a channel, coefficients are `DC, Y, Z, X, XY, YZ, (3ZZ-1), XZ, (XX-YY)`.
The diagnostic populated and read `SphericalHarmonicsL2[channel, coefficient]`
directly, without basis conversion. These are the Unity polynomial coefficients
present after irradiance convolution and Unity-format conversion, before APV
storage encoding. They are not the reordered/quantized runtime texture data.

The fixture covers unfiltered and filtered L1, window interpolation, directional
and diagonal inputs, pure L2 bands, and colored indoor/outdoor probes from the
Sponza comparison. `full-3` is the floor probe at `(0,1,0)` whose old independent
RGB filtering produced purple, dark GI. Its up-facing L1 RGB is checked against
the native output as a separate visible-regression assertion.

Additional checks cover the `DC <= 0.001` participation threshold, shared RGB
band weights, channel permutation, finite inputs, and coefficient count.
The previous tests assumed a common L1/L2 scale, strict reconstruction
positivity, a 10% storage reserve, and idempotence. Those were properties of the
old filter, not the reference bake pipeline, so they are intentionally removed.
