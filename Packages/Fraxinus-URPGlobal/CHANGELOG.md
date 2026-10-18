# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [14.0.99] - 2026-10-18

First coordinated Fraxinus URP configuration release for Unity 2022.3.

### Bilingual documentation patch (same version)

- Add English and Simplified Chinese READMEs with language navigation.
- Simplify feature descriptions and use relative documentation links.
- Keep the package version, dependencies and code unchanged in this documentation patch.

新增双语 README 与语言切换，精简特点说明并使用相对链接；本次仅更新文档，版本、依赖与代码保持不变。

### Public source preparation (same-version patch)

- Include the Unity Companion License text and document coordinated four-package installation.
- Identify the package as part of the unofficial Fraxinus fork and provide public documentation links.

### Changed

- Align package identity and the Core dependency with Fraxinus Core and URP 14.0.99.
- Retain shared runtime and generated HLSL shader configuration for the matching URP package.

This configuration package must be installed with the matching Fraxinus Core
and URP packages; it is not a standalone rendering pipeline.
