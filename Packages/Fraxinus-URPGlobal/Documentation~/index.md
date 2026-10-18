# Fraxinus URP Configuration

This independently maintained fork supplies settings for the matching Fraxinus
URP and Core packages. It replaces Unity's package with the same ID; it is not
an official Unity release or a standalone rendering pipeline.

See the [README](../README.md) for the four-package installation and tested
support scope. To edit configuration, embed the package in your project and
keep its generated HLSL in sync; do not modify `Library/PackageCache` directly.

The [upstream URP documentation](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@14.0/manual/index.html)
provides background, but does not describe this fork's RenderGraph-only API or
its complete feature and platform support.
