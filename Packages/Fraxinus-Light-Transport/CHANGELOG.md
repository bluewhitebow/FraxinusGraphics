# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.0.1] - 2026-10-18

First Fraxinus Unity 2022.3 release of the Light Transport backport.

### Bilingual documentation patch (same version)

- Add English and Simplified Chinese READMEs with language navigation.
- Simplify feature descriptions and use relative documentation links.
- Keep the package version, dependencies and code unchanged in this documentation patch.

新增双语 README 与语言切换，精简特点说明并使用相对链接；本次仅更新文档，版本、依赖与代码保持不变。

### Public source preparation (same-version patch)

- Include the Unity Companion License text and retain the separate third-party licenses.
- Document the coordinated four-package installation, tested environment and public support scope.
- Identify the package as an unofficial Unity backport and provide public documentation links.

### Added

- RadeonRays Compute ray tracing, acceleration-structure updates and sampling on Unity 2022.3 without a DXR requirement.
- GPU smoke coverage for hits, masks, culling, transforms, rebuilds and instance removal.

### Fixed

- Compute ray tracing shader imports, shader include dependency tracking and failed-import recovery.
- Initialization of ray tracing hit payloads.

Unity 6 hardware ray tracing and native light integration remain version-gated.
This package does not supply Unity 6's engine-owned APV light integrator on Unity 2022.

## Upstream 1.0.1 - 2024-01-08

### Fixed

- Fixed UUM-59879: MissingComponentException when no MeshFilter is attached to a Terrain tree game object.

## [1.0.0] - 2023-04-25

### Added

- Initial package release
